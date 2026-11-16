using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetLift.Core.Rewrites;

/// <summary>Result of rewriting one source file.</summary>
public sealed record SourceRewrite(string NewText, int Replaced, int LeftAlone);

/// <summary>
/// HttpRuntime.Cache / HttpContext.Current.Cache → System.Runtime.Caching.MemoryCache.Default.
/// MemoryCache exists on .NET Framework and (as a package) on modern .NET, so the rewritten code builds for both targets.
/// Only call shapes with an exact equivalent are rewritten; anything else (dependencies, callbacks, priorities) is left as it is.
/// </summary>
public static class HttpRuntimeCacheRewrite
{
    private const string QualifiedCache = "System.Runtime.Caching.MemoryCache.Default";
    private const string QualifiedObjectCache = "System.Runtime.Caching.ObjectCache";
    private const string QualifiedPolicy = "System.Runtime.Caching.CacheItemPolicy";

    // System.Web.Caching names that keep `using System.Web.Caching;` necessary if they are still used.
    private static readonly HashSet<string> WebCachingNames =
        ["Cache", "CacheDependency", "SqlCacheDependency", "AggregateCacheDependency", "CacheItemPriority",
         "CacheItemRemovedCallback", "CacheItemUpdateCallback", "CacheItemRemovedReason"];

    public static SourceRewrite? Rewrite(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        // Without `using System.Web;` an unqualified HttpRuntime is someone else's type: only System.Web.HttpRuntime counts then.
        var importsSystemWeb = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Any(u => u.Name?.ToString() == "System.Web" && u.Alias is null);

        if (!root.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Any(m => IsWebCache(m, importsSystemWeb)))
        {
            return null;
        }

        var rewriter = new Rewriter(importsSystemWeb);
        var rewritten = (CompilationUnitSyntax)rewriter.Visit(root);
        if (rewriter.Replaced == 0)
        {
            return new SourceRewrite(source, 0, rewriter.LeftAlone);
        }

        var text = Usings(rewritten).ToFullString();
        return new SourceRewrite(text, rewriter.Replaced, rewriter.LeftAlone);
    }

    /// <summary>
    /// Swap `using System.Web.Caching;` for `using System.Runtime.Caching;` when nothing else from System.Web.Caching is used,
    /// and then write the short type names. Otherwise keep fully qualified names to avoid ambiguities (CacheItemPriority exists in both).
    /// </summary>
    private static CompilationUnitSyntax Usings(CompilationUnitSyntax root)
    {
        var webCaching = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .FirstOrDefault(u => u.Name?.ToString() == "System.Web.Caching" && u.Alias is null && u.StaticKeyword.IsKind(SyntaxKind.None));
        var hasRuntimeCaching = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Any(u => u.Name?.ToString() == "System.Runtime.Caching" && u.Alias is null);

        var stillUsesWebCaching = root.DescendantTokens()
            .Any(t => t.IsKind(SyntaxKind.IdentifierToken) && WebCachingNames.Contains(t.Text));

        CompilationUnitSyntax result;
        if (webCaching is not null && !stillUsesWebCaching)
        {
            result = hasRuntimeCaching
                ? root.RemoveNode(webCaching, SyntaxRemoveOptions.KeepNoTrivia)!
                : root.ReplaceNode(webCaching, RuntimeCachingUsing().WithTriviaFrom(webCaching));
        }
        else if (hasRuntimeCaching)
        {
            result = root;
        }
        else
        {
            return root; // keep fully qualified names
        }

        var shortened = result.ToFullString()
            .Replace(QualifiedCache, "MemoryCache.Default")
            .Replace(QualifiedObjectCache, "ObjectCache")
            .Replace(QualifiedPolicy, "CacheItemPolicy");
        return CSharpSyntaxTree.ParseText(shortened).GetCompilationUnitRoot();
    }

    private static UsingDirectiveSyntax RuntimeCachingUsing() =>
        SyntaxFactory.ParseCompilationUnit("using System.Runtime.Caching;").Usings[0];

    /// <summary>HttpRuntime.Cache, System.Web.HttpRuntime.Cache, HttpContext.Current.Cache.</summary>
    internal static bool IsWebCache(ExpressionSyntax expression, bool allowUnqualified) =>
        expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Cache" } access
        && (IsWebType(access.Expression, "HttpRuntime", allowUnqualified)
            || access.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Current" } current
               && IsWebType(current.Expression, "HttpContext", allowUnqualified));

    private static bool IsWebType(ExpressionSyntax e, string name, bool allowUnqualified) =>
        allowUnqualified && e is IdentifierNameSyntax id && id.Identifier.Text == name
        || e.ToString().Replace(" ", "") is var text && (text == $"System.Web.{name}" || text == $"global::System.Web.{name}");

    private sealed class Rewriter(bool allowUnqualified) : CSharpSyntaxRewriter
    {
        private bool IsWebCache(ExpressionSyntax e) => HttpRuntimeCacheRewrite.IsWebCache(e, allowUnqualified);

        public int Replaced { get; private set; }
        public int LeftAlone { get; private set; }

        // cache[key]  (read or write) → MemoryCache.Default[key]
        public override SyntaxNode? VisitElementAccessExpression(ElementAccessExpressionSyntax node)
        {
            var visited = (ElementAccessExpressionSyntax)base.VisitElementAccessExpression(node)!;
            if (!IsWebCache(visited.Expression))
            {
                return visited;
            }

            Replaced++;
            return visited.WithExpression(SyntaxFactory.ParseExpression(QualifiedCache).WithTriviaFrom(visited.Expression));
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            if (visited.Expression is not MemberAccessExpressionSyntax call || !IsWebCache(call.Expression))
            {
                return visited;
            }

            var args = visited.ArgumentList.Arguments.Select(a => a.ToString()).ToList();
            var replacement = (call.Name.Identifier.Text, args.Count) switch
            {
                ("Get", 1) or ("Remove", 1) => $"{QualifiedCache}.{call.Name.Identifier.Text}({args[0]})",
                ("Insert", 2) => $"{QualifiedCache}.Set({args[0]}, {args[1]}, {QualifiedObjectCache}.InfiniteAbsoluteExpiration)",
                ("Insert", 5) when IsNull(args[2]) && IsNoSliding(args[4]) && !IsNoAbsolute(args[3])
                    => $"{QualifiedCache}.Set({args[0]}, {args[1]}, {args[3]})",
                ("Insert", 5) when IsNull(args[2]) && IsNoAbsolute(args[3]) && !IsNoSliding(args[4])
                    => $"{QualifiedCache}.Set({args[0]}, {args[1]}, new {QualifiedPolicy} {{ SlidingExpiration = {args[4]} }})",
                _ => null,
            };

            if (replacement is null)
            {
                LeftAlone++;
                return visited;
            }

            Replaced++;
            return SyntaxFactory.ParseExpression(replacement).WithTriviaFrom(visited);
        }

        // Members of the cache that are not called or indexed (e.g. HttpRuntime.Cache.Count) are left alone and counted.
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
