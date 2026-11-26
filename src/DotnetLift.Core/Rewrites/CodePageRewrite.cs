using DotnetLift.Core.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetLift.Core.Rewrites;

/// <summary>
/// Makes code-page text handling behave the same on .NET Framework and modern .NET:
///   Encoding.Default / Encoding.GetEncoding(0)  → LegacyEncoding.Default   (the code page the old servers used, e.g. 1254)
///   Encoding.GetEncoding(1254 / "windows-1254") → LegacyEncoding.Get(...)  (registers CodePagesEncodingProvider first)
/// LegacyEncoding is a small helper generated into the library. Built-in encodings (UTF-8, ASCII, Latin-1, UTF-16/32) are not touched.
/// </summary>
public static class CodePageRewrite
{
    public const string HelperName = "LegacyEncoding";

    public static SourceRewrite? Rewrite(string source, string helperNamespace)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var rewriter = new Rewriter();
        var rewritten = (CompilationUnitSyntax)rewriter.Visit(root);
        if (rewriter.Replaced == 0)
        {
            return null;
        }

        return new SourceRewrite(AddUsing(rewritten, helperNamespace).ToFullString(), rewriter.Replaced, 0);
    }

    /// <summary>The helper class, compiling as C# 7.3 for .NET Framework and on modern .NET.</summary>
    public static string HelperSource(string helperNamespace, int codePage)
    {
        var nl = Environment.NewLine;
        string[] lines =
        [
            "using System.Text;",
            "",
            $"namespace {helperNamespace}",
            "{",
            "    /// <summary>",
            "    /// Code-page encodings that give the same bytes on .NET Framework and modern .NET. Added by dotnet-lift.",
            $"    /// On .NET Framework, Encoding.Default was the server's ANSI code page ({codePage}); on modern .NET it is always UTF-8.",
            "    /// Legacy code pages also need CodePagesEncodingProvider on modern .NET; this class registers it before using one.",
            "    /// </summary>",
            $"    internal static class {HelperName}",
            "    {",
            "        /// <summary>The code page Encoding.Default stood for on the old servers.</summary>",
            $"        public const int DefaultCodePage = {codePage};",
            "",
            "        private static readonly Encoding DefaultEncoding = Get(DefaultCodePage);",
            "",
            "        /// <summary>Use instead of Encoding.Default: the same bytes as on the old servers.</summary>",
            "        public static Encoding Default",
            "        {",
            "            get { return DefaultEncoding; }",
            "        }",
            "",
            "        public static Encoding Get(int codePage)",
            "        {",
            "            EnsureProvider();",
            "            return Encoding.GetEncoding(codePage);",
            "        }",
            "",
            "        public static Encoding Get(string name)",
            "        {",
            "            EnsureProvider();",
            "            return Encoding.GetEncoding(name);",
            "        }",
            "",
            "        private static void EnsureProvider()",
            "        {",
            "#if NETCOREAPP",
            "            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);",
            "#endif",
            "        }",
            "    }",
            "}",
        ];
        return string.Join(nl, lines) + nl;
    }

    private static CompilationUnitSyntax AddUsing(CompilationUnitSyntax root, string ns)
    {
        var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Where(u => u.Alias is null).ToList();
        if (usings.Any(u => u.Name?.ToString() == ns)
            || root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Any(n => n.Name.ToString() == ns))
        {
            return root;
        }

        var newUsing = SyntaxFactory.ParseCompilationUnit($"using {ns};").Usings[0];
        var anchor = usings.LastOrDefault();
        return anchor is null
            ? root.WithUsings(root.Usings.Add(newUsing.WithTrailingTrivia(SyntaxFactory.ParseTrailingTrivia(Environment.NewLine))))
            : root.InsertNodesAfter(anchor, [newUsing.WithTrailingTrivia(anchor.GetTrailingTrivia())]);
    }

    private sealed class Rewriter : CSharpSyntaxRewriter
    {
        public int Replaced { get; private set; }

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            var visited = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;
            if (visited.Name.Identifier.Text == "Default" && DataCompatibilityAnalyzer.IsEncodingType(visited.Expression))
            {
                Replaced++;
                return SyntaxFactory.ParseExpression($"{HelperName}.Default").WithTriviaFrom(visited);
            }
            return visited;
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            if (visited.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "GetEncoding" } member
                || !DataCompatibilityAnalyzer.IsEncodingType(member.Expression)
                || visited.ArgumentList.Arguments.Count != 1)
            {
                return visited;
            }

            var arg = visited.ArgumentList.Arguments[0].Expression;
            string? replacement = arg switch
            {
                LiteralExpressionSyntax { Token.Value: 0 } => $"{HelperName}.Default",
                LiteralExpressionSyntax { Token.Value: int page } when !DataCompatibilityAnalyzer.BuiltInCodePages.Contains(page)
                    => $"{HelperName}.Get({page})",
                LiteralExpressionSyntax { Token.Value: string name } when !DataCompatibilityAnalyzer.BuiltInEncodingNames.Contains(name)
                    => $"{HelperName}.Get({arg})",
                _ => null, // built-in encodings and variables stay as they are
            };

            if (replacement is null)
            {
                return visited;
            }

            Replaced++;
            return SyntaxFactory.ParseExpression(replacement).WithTriviaFrom(visited);
        }
    }
}
