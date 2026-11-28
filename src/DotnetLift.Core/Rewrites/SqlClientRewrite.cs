using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetLift.Core.Rewrites;

/// <summary>
/// System.Data.SqlClient → Microsoft.Data.SqlClient (and System.Data.Sql → Microsoft.Data.Sql): using directives, aliases,
/// fully qualified type names and expressions. The type names are the same in both drivers, so only the namespace changes.
/// The text is edited span by span, so formatting and comments stay exactly as they were. System.Data.SqlTypes is shared
/// by both drivers and is not touched; string literals (provider names) are not code and are left to the analyzer.
/// </summary>
public static class SqlClientRewrite
{
    private static readonly Dictionary<string, string> Namespaces = new(StringComparer.Ordinal)
    {
        ["System.Data.SqlClient"] = "Microsoft.Data.SqlClient",
        ["System.Data.Sql"] = "Microsoft.Data.Sql",
    };

    /// <summary>The nodes that name a legacy namespace: in `using System.Data.SqlClient;` and in `System.Data.SqlClient.SqlConnection`.</summary>
    public static IReadOnlyList<SyntaxNode> Find(SyntaxNode root) =>
        root.DescendantNodes()
            .Where(n => n is QualifiedNameSyntax or MemberAccessExpressionSyntax && Replacement(n) is not null)
            .ToList();

    public static SourceRewrite? Rewrite(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var nodes = Find(root);
        if (nodes.Count == 0)
        {
            return null;
        }

        var text = source;
        foreach (var node in nodes.OrderByDescending(n => n.SpanStart))
        {
            text = text[..node.SpanStart] + Replacement(node) + text[node.Span.End..];
        }
        return new SourceRewrite(RemoveDuplicateUsings(text), nodes.Count, 0);
    }

    /// <summary>The new namespace text for a node that names a legacy one (keeping a global:: prefix), else null.</summary>
    private static string? Replacement(SyntaxNode node)
    {
        var name = string.Concat(node.ToString().Where(c => !char.IsWhiteSpace(c)));
        var prefix = "";
        if (name.StartsWith("global::", StringComparison.Ordinal))
        {
            prefix = "global::";
            name = name["global::".Length..];
        }
        return Namespaces.TryGetValue(name, out var replacement) ? prefix + replacement : null;
    }

    /// <summary>
    /// A file that already imported Microsoft.Data.SqlClient next to System.Data.SqlClient now has it twice (warning CS0105):
    /// the later plain using directive goes, with its line.
    /// </summary>
    private static string RemoveDuplicateUsings(string text)
    {
        var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new List<UsingDirectiveSyntax>();
        foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (directive.Alias is not null || !directive.StaticKeyword.IsKind(SyntaxKind.None) || directive.Name is null)
            {
                continue;
            }
            var scope = directive.Parent is BaseNamespaceDeclarationSyntax ns ? ns.Name.ToString() : "";
            if (!seen.Add(scope + "|" + directive.Name))
            {
                duplicates.Add(directive);
            }
        }

        foreach (var directive in duplicates.OrderByDescending(d => d.FullSpan.Start))
        {
            text = text[..directive.FullSpan.Start] + text[directive.FullSpan.End..];
        }
        return text;
    }
}
