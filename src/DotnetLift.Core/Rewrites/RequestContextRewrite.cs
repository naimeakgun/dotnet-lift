using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetLift.Core.Rewrites;

/// <summary>
/// HttpContext.Current.&lt;Member&gt; → the team's own abstraction, as configured in the profile's "requestContext":
///   HttpContext.Current.User           → UserContext.Current.Principal
///   HttpContext.Current.Items["key"]   → RequestContext.Current.Items["key"]
///   HttpContext.Current != null        → RequestContext.Current != null   (only with "context")
/// A file is rewritten completely or not at all: one use of a member without a mapping (or HttpContext.Current passed
/// around as a value) leaves the whole file alone and is reported, so no half-migrated file hides the coupling.
/// </summary>
public static class RequestContextRewrite
{
    public static SourceRewrite? Rewrite(string source, RequestContextMap map)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var importsSystemWeb = CurrentUserRewrite.Imports(root, "System.Web");
        var currents = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(m => CurrentUserRewrite.IsHttpContextCurrent(m, importsSystemWeb))
            .ToList();
        if (currents.Count == 0)
        {
            return null;
        }

        var replacements = new Dictionary<SyntaxNode, string>();
        foreach (var current in currents)
        {
            if (current.Parent is MemberAccessExpressionSyntax member && member.Expression == current
                && map.Members.TryGetValue(member.Name.Identifier.Text, out var expression))
            {
                replacements[member] = expression;
            }
            else if (map.Context is { } context && current.Parent is BinaryExpressionSyntax binary
                     && (binary.IsKind(SyntaxKind.NotEqualsExpression) || binary.IsKind(SyntaxKind.EqualsExpression))
                     && (CurrentUserRewrite.IsNullLiteral(binary.Left) || CurrentUserRewrite.IsNullLiteral(binary.Right)))
            {
                replacements[current] = context;
            }
            else
            {
                return new SourceRewrite(source, 0, currents.Count);
            }
        }

        var rewritten = root.ReplaceNodes(replacements.Keys,
            (original, _) => Parenthesize(SyntaxFactory.ParseExpression(replacements[original]), original).WithTriviaFrom(original));
        rewritten = Usings(rewritten, map.Usings);
        return new SourceRewrite(rewritten.ToFullString(), replacements.Count, 0);
    }

    /// <summary>An expression that is not a simple name or member chain gets parentheses, so `.Url` or `["key"]` after it still binds to all of it.</summary>
    private static ExpressionSyntax Parenthesize(ExpressionSyntax expression, SyntaxNode original) =>
        expression is SimpleNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax or ElementAccessExpressionSyntax
                      or ParenthesizedExpressionSyntax or ThisExpressionSyntax
            ? expression
            : SyntaxFactory.ParenthesizedExpression(expression);

    /// <summary>Adds the configured namespaces and removes `using System.Web;` once no System.Web type is left.</summary>
    private static CompilationUnitSyntax Usings(CompilationUnitSyntax root, IReadOnlyList<string> namespaces)
    {
        var existing = root.Usings.Where(u => u.Alias is null).Select(u => u.Name?.ToString()).ToHashSet(StringComparer.Ordinal);
        var declared = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).ToHashSet(StringComparer.Ordinal);
        var stillUsesSystemWeb = root.DescendantTokens()
            .Any(t => t.IsKind(SyntaxKind.IdentifierToken) && CurrentUserRewrite.SystemWebTypes.Contains(t.Text));

        var systemWeb = root.Usings.FirstOrDefault(u => u.Alias is null && u.Name?.ToString() == "System.Web");
        var toAdd = namespaces.Where(n => !existing.Contains(n) && !declared.Contains(n)).Distinct(StringComparer.Ordinal).ToList();
        var eol = root.Usings.LastOrDefault()?.GetTrailingTrivia().ToString() is { Length: > 0 } t ? t : Environment.NewLine;
        var added = toAdd.Select(n => SyntaxFactory.ParseCompilationUnit($"using {n};").Usings[0].WithTrailingTrivia(SyntaxFactory.ParseTrailingTrivia(eol))).ToList();

        var usings = root.Usings;
        if (systemWeb is not null && !stillUsesSystemWeb)
        {
            var index = usings.IndexOf(systemWeb);
            usings = usings.RemoveAt(index);
            if (added.Count > 0)
            {
                usings = usings.InsertRange(index, [added[0].WithLeadingTrivia(systemWeb.GetLeadingTrivia()), .. added.Skip(1)]);
                added.Clear();
            }
        }
        return root.WithUsings(usings.AddRange(added));
    }
}
