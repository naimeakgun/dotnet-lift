using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetLift.Core.Rewrites;

/// <summary>
/// HttpRuntime.Cache / HttpContext.Current.Cache → the team's cache API, from the templates in the profile's "cache":
///   cache[key]                 → get        cache[key] = value (statement) → set
///   cache.Get(key)             → get        cache.Remove(key)             → remove
///   cache.Insert(key, value)   → set
///   cache.Insert(key, value, null, absolute, Cache.NoSlidingExpiration)  → setAbsolute
///   cache.Insert(key, value, null, Cache.NoAbsoluteExpiration, sliding)  → setSliding
/// A call whose shape has no template (or has dependencies, priorities, callbacks) is left as it is and counted.
/// </summary>
public static class CustomCacheRewrite
{
    public static SourceRewrite? Rewrite(string source, CacheMap map)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var importsSystemWeb = CurrentUserRewrite.Imports(root, "System.Web");
        if (!root.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Any(m => HttpRuntimeCacheRewrite.IsWebCache(m, importsSystemWeb)))
        {
            return null;
        }

        var rewriter = new Rewriter(map, importsSystemWeb);
        var rewritten = (CompilationUnitSyntax)rewriter.Visit(root);
        if (rewriter.Replaced == 0)
        {
            return new SourceRewrite(source, 0, rewriter.LeftAlone);
        }
        return new SourceRewrite(Usings(rewritten, map.Usings).ToFullString(), rewriter.Replaced, rewriter.LeftAlone);
    }

    private static CompilationUnitSyntax Usings(CompilationUnitSyntax root, IReadOnlyList<string> namespaces)
    {
        var existing = root.Usings.Where(u => u.Alias is null).Select(u => u.Name?.ToString()).ToHashSet(StringComparer.Ordinal);
        var eol = root.Usings.LastOrDefault()?.GetTrailingTrivia().ToString() is { Length: > 0 } t ? t : Environment.NewLine;
        var usings = root.Usings;

        // using System.Web.Caching; goes when none of its names is left (Cache.NoSlidingExpiration in a skipped call keeps it).
        var webCaching = usings.FirstOrDefault(u => u.Alias is null && u.Name?.ToString() == "System.Web.Caching");
        var stillUsed = root.DescendantTokens().Any(tk => tk.IsKind(SyntaxKind.IdentifierToken)
            && tk.Text is "Cache" or "CacheDependency" or "CacheItemPriority" or "CacheItemRemovedCallback" or "CacheItemRemovedReason" or "SqlCacheDependency");
        var index = -1;
        if (webCaching is not null && !stillUsed)
        {
            index = usings.IndexOf(webCaching);
            usings = usings.RemoveAt(index);
        }

        var added = namespaces.Where(n => !existing.Contains(n)).Distinct(StringComparer.Ordinal)
            .Select(n => SyntaxFactory.ParseCompilationUnit($"using {n};").Usings[0].WithTrailingTrivia(SyntaxFactory.ParseTrailingTrivia(eol)))
            .ToList();
        usings = index >= 0 ? usings.InsertRange(index, added) : usings.AddRange(added);
        return root.WithUsings(usings);
    }

    private sealed class Rewriter(CacheMap map, bool allowUnqualified) : CSharpSyntaxRewriter
    {
        public int Replaced { get; private set; }
        public int LeftAlone { get; private set; }

        private bool IsWebCache(ExpressionSyntax e) => HttpRuntimeCacheRewrite.IsWebCache(e, allowUnqualified);

        private SyntaxNode Replace(SyntaxNode original, string? replacement)
        {
            if (replacement is null)
            {
                LeftAlone++;
                return original;
            }
            Replaced++;
            return SyntaxFactory.ParseExpression(replacement).WithTriviaFrom(original);
        }

        // cache[key] = value, as a statement → set
        public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
        {
            if (node.IsKind(SyntaxKind.SimpleAssignmentExpression)
                && node.Left is ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } target && IsWebCache(target.Expression))
            {
                var value = (ExpressionSyntax)Visit(node.Right)!;
                var filled = node.Parent is ExpressionStatementSyntax
                    ? map.Fill("set", new Dictionary<string, string> { ["key"] = target.ArgumentList.Arguments[0].ToString(), ["value"] = value.ToString() })
                    : null; // the assignment's value is used: a method call has none
                if (filled is null)
                {
                    LeftAlone++;
                    return node.WithRight(value);
                }
                return Replace(node, filled);
            }
            return base.VisitAssignmentExpression(node);
        }

        // cache[key] (read) → get
        public override SyntaxNode? VisitElementAccessExpression(ElementAccessExpressionSyntax node)
        {
            var visited = (ElementAccessExpressionSyntax)base.VisitElementAccessExpression(node)!;
            if (!IsWebCache(visited.Expression) || visited.ArgumentList.Arguments.Count != 1)
            {
                return visited;
            }
            return Replace(visited, map.Fill("get", new Dictionary<string, string> { ["key"] = visited.ArgumentList.Arguments[0].ToString() }));
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            if (visited.Expression is not MemberAccessExpressionSyntax call || !IsWebCache(call.Expression))
            {
                return visited;
            }

            var args = visited.ArgumentList.Arguments.Select(a => a.ToString()).ToList();
            string? Fill(string shape, params (string Name, int Index)[] values) =>
                map.Fill(shape, values.ToDictionary(v => v.Name, v => args[v.Index]));

            var replacement = (call.Name.Identifier.Text, args.Count) switch
            {
                ("Get", 1) => Fill("get", ("key", 0)),
                ("Remove", 1) => Fill("remove", ("key", 0)),
                ("Insert", 2) => Fill("set", ("key", 0), ("value", 1)),
                ("Insert", 5) when IsNull(args[2]) && IsNoSliding(args[4]) && !IsNoAbsolute(args[3])
                    => Fill("setAbsolute", ("key", 0), ("value", 1), ("absolute", 3)),
                ("Insert", 5) when IsNull(args[2]) && IsNoAbsolute(args[3]) && !IsNoSliding(args[4])
                    => Fill("setSliding", ("key", 0), ("value", 1), ("sliding", 4)),
                _ => null,
            };
            return Replace(visited, replacement);
        }

        // Other members (HttpRuntime.Cache.Count, passing the cache around) are left alone and counted.
        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            var visited = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;
            if (IsWebCache(visited.Expression) && node.Parent is not InvocationExpressionSyntax)
            {
                LeftAlone++;
            }
            return visited;
        }

        private static bool IsNull(string arg) => arg.Trim() == "null";

        private static bool IsNoSliding(string arg) => arg.Replace(" ", "").EndsWith("Cache.NoSlidingExpiration", StringComparison.Ordinal);

        private static bool IsNoAbsolute(string arg) => arg.Replace(" ", "").EndsWith("Cache.NoAbsoluteExpiration", StringComparison.Ordinal);
    }
}
