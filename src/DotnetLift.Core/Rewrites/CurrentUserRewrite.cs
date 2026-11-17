using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetLift.Core.Rewrites;

/// <summary>
/// Removes a library's dependency on the web request for the current user:
///   HttpContext.Current.User          → CurrentUser.Principal
///   HttpContext.Current != null       → CurrentUser.Principal != null
/// CurrentUser is a small provider generated into the library; the host application tells it where the user comes from.
/// A file that uses HttpContext.Current for anything else (Items, Request, Session, …) is left completely alone.
/// </summary>
public static class CurrentUserRewrite
{
    // System.Web types that keep `using System.Web;` necessary if they are still used.
    private static readonly HashSet<string> SystemWebTypes =
        ["HttpContext", "HttpRuntime", "HttpRequest", "HttpResponse", "HttpCookie", "HttpServerUtility", "HttpUtility",
         "HttpContextBase", "HttpRequestBase", "HttpApplication", "VirtualPathUtility", "HttpException", "IHttpModule", "IHttpHandler"];

    public static SourceRewrite? Rewrite(string source, string providerNamespace)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var importsSystemWeb = Imports(root, "System.Web");

        var currents = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(m => IsHttpContextCurrent(m, importsSystemWeb))
            .ToList();
        if (currents.Count == 0)
        {
            return null;
        }

        var replacements = new List<SyntaxNode>();
        foreach (var current in currents)
        {
            if (current.Parent is MemberAccessExpressionSyntax { Name.Identifier.Text: "User" } user && user.Expression == current)
            {
                replacements.Add(user);
            }
            else if (current.Parent is BinaryExpressionSyntax binary
                     && (binary.IsKind(SyntaxKind.NotEqualsExpression) || binary.IsKind(SyntaxKind.EqualsExpression))
                     && (IsNullLiteral(binary.Left) || IsNullLiteral(binary.Right)))
            {
                replacements.Add(current);
            }
            else
            {
                // Used for something other than the user: rewriting part of the file would hide the real coupling.
                return new SourceRewrite(source, 0, currents.Count);
            }
        }

        var principal = SyntaxFactory.ParseExpression("CurrentUser.Principal");
        var rewritten = root.ReplaceNodes(replacements, (original, _) => principal.WithTriviaFrom(original));
        rewritten = (CompilationUnitSyntax)new DuplicateConditionRemover().Visit(rewritten);
        rewritten = Usings(rewritten, providerNamespace);

        return new SourceRewrite(rewritten.ToFullString(), replacements.Count, 0);
    }

    /// <summary>
    /// Inserts `<paramref name="providerNamespace"/>.CurrentUser.Use(() => HttpContext.Current?.User);` at the end of
    /// Application_Start, so the library sees the same user as before. Returns null if there is nothing to do.
    /// </summary>
    public static string? WireIntoGlobalAsax(string source, string providerNamespace)
    {
        if (source.Contains("CurrentUser.Use(", StringComparison.Ordinal))
        {
            return null;
        }

        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var start = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == "Application_Start" && m.Body is not null);
        if (start?.Body is not { } body)
        {
            return null;
        }

        var last = body.Statements.LastOrDefault();
        var indent = last?.GetLeadingTrivia().LastOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).ToString()
                     ?? body.OpenBraceToken.LeadingTrivia.ToString() + "    ";
        var eol = last?.GetTrailingTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia)).ToString() is { Length: > 0 } e
            ? e
            : Environment.NewLine;

        var statement = SyntaxFactory.ParseStatement($"{providerNamespace}.CurrentUser.Use(() => HttpContext.Current?.User);")
            .WithLeadingTrivia(SyntaxFactory.ParseLeadingTrivia(
                $"{indent}// dotnet-lift: libraries read the signed-in user through CurrentUser instead of HttpContext.{eol}{indent}"))
            .WithTrailingTrivia(SyntaxFactory.ParseTrailingTrivia(eol));

        return root.ReplaceNode(body, body.AddStatements(statement)).ToFullString();
    }

    /// <summary>The provider class added to the library.</summary>
    public static string ProviderSource(string providerNamespace)
    {
        var nl = Environment.NewLine;
        string[] lines =
        [
            "using System;",
            "using System.Security.Principal;",
            "",
            $"namespace {providerNamespace}",
            "{",
            "    /// <summary>",
            "    /// The signed-in user, supplied by the host application. Added by dotnet-lift to replace HttpContext.Current.User,",
            "    /// so this library no longer depends on System.Web.",
            "    /// ASP.NET (System.Web), in Application_Start:  CurrentUser.Use(() => HttpContext.Current?.User);",
            "    /// ASP.NET Core, in Program.cs:  CurrentUser.Use(() => app.Services.GetRequiredService&lt;IHttpContextAccessor&gt;().HttpContext?.User);",
            "    /// </summary>",
            "    public static class CurrentUser",
            "    {",
            "        private static Func<IPrincipal> _provider = () => null;",
            "",
            "        /// <summary>Sets where the current user comes from. Call once at startup.</summary>",
            "        public static void Use(Func<IPrincipal> provider)",
            "        {",
            "            _provider = provider ?? (() => null);",
            "        }",
            "",
            "        /// <summary>The current user, or null when there is none (no request, not signed in).</summary>",
            "        public static IPrincipal Principal",
            "        {",
            "            get { return _provider(); }",
            "        }",
            "    }",
            "}",
        ];
        return string.Join(nl, lines) + nl;
    }

    /// <summary>True if the C# source still uses System.Web (a using directive, a qualified name or a System.Web type).</summary>
    public static bool UsesSystemWeb(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        if (root.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(u => u.Name?.ToString() is { } n && (n == "System.Web" || n.StartsWith("System.Web."))))
        {
            return true;
        }
        return root.DescendantNodes().OfType<QualifiedNameSyntax>().Any(q => q.ToString().StartsWith("System.Web"))
               || root.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Any(m => m.ToString().StartsWith("System.Web."));
    }

    private static CompilationUnitSyntax Usings(CompilationUnitSyntax root, string providerNamespace)
    {
        var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Where(u => u.Alias is null).ToList();
        if (usings.Any(u => u.Name?.ToString() == providerNamespace) || InsideNamespace(root, providerNamespace))
        {
            return RemoveSystemWebIfUnused(root);
        }

        var stillUsesSystemWeb = root.DescendantTokens()
            .Any(t => t.IsKind(SyntaxKind.IdentifierToken) && SystemWebTypes.Contains(t.Text));
        var systemWeb = usings.FirstOrDefault(u => u.Name?.ToString() == "System.Web");
        var newUsing = SyntaxFactory.ParseCompilationUnit($"using {providerNamespace};").Usings[0];

        if (systemWeb is not null && !stillUsesSystemWeb)
        {
            return root.ReplaceNode(systemWeb, newUsing.WithTriviaFrom(systemWeb));
        }

        var anchor = usings.LastOrDefault();
        if (anchor is null)
        {
            return root.WithUsings(root.Usings.Add(newUsing.WithTrailingTrivia(SyntaxFactory.ParseTrailingTrivia(Environment.NewLine))));
        }
        return root.InsertNodesAfter(anchor, [newUsing.WithTrailingTrivia(anchor.GetTrailingTrivia())]);
    }

    private static CompilationUnitSyntax RemoveSystemWebIfUnused(CompilationUnitSyntax root)
    {
        var systemWeb = root.DescendantNodes().OfType<UsingDirectiveSyntax>().FirstOrDefault(u => u.Name?.ToString() == "System.Web" && u.Alias is null);
        var stillUsed = root.DescendantTokens().Any(t => t.IsKind(SyntaxKind.IdentifierToken) && SystemWebTypes.Contains(t.Text));
        return systemWeb is not null && !stillUsed ? root.RemoveNode(systemWeb, SyntaxRemoveOptions.KeepNoTrivia)! : root;
    }

    private static bool InsideNamespace(CompilationUnitSyntax root, string ns) =>
        root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Any(n => n.Name.ToString() == ns);

    private static bool Imports(CompilationUnitSyntax root, string ns) =>
        root.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(u => u.Name?.ToString() == ns && u.Alias is null);

    private static bool IsHttpContextCurrent(MemberAccessExpressionSyntax m, bool allowUnqualified)
    {
        if (m.Name.Identifier.Text != "Current")
        {
            return false;
        }
        var target = m.Expression.ToString().Replace(" ", "");
        return allowUnqualified && target == "HttpContext" || target is "System.Web.HttpContext" or "global::System.Web.HttpContext";
    }

    private static bool IsNullLiteral(ExpressionSyntax e) => e.IsKind(SyntaxKind.NullLiteralExpression);

    /// <summary>`X != null &amp;&amp; X != null` (left behind when two different checks became the same one) → `X != null`.</summary>
    private sealed class DuplicateConditionRemover : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            var visited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
            if ((visited.IsKind(SyntaxKind.LogicalAndExpression) || visited.IsKind(SyntaxKind.LogicalOrExpression))
                && visited.Left.ToString() == visited.Right.ToString())
            {
                return visited.Left.WithLeadingTrivia(visited.GetLeadingTrivia()).WithTrailingTrivia(visited.GetTrailingTrivia());
            }
            return visited;
        }
    }
}
