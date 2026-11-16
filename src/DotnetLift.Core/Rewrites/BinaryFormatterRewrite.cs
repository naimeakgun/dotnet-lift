using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetLift.Core.Rewrites;

/// <summary>
/// new BinaryFormatter().Serialize(stream, value)    → JsonSerializer.Serialize(stream, value)
/// (T)new BinaryFormatter().Deserialize(stream)      → JsonSerializer.Deserialize&lt;T&gt;(stream)
///
/// System.Text.Json is in the box on modern .NET and a package on .NET Framework, so the code builds for both.
/// This CHANGES THE SERIALIZED FORMAT: data written by the old code cannot be read by the new code.
/// That is why the step that uses this rewrite needs explicit approval.
/// Other shapes (a formatter stored in a variable, Deserialize without a cast, surrogates, binders) are left alone.
/// </summary>
public static class BinaryFormatterRewrite
{
    private const string QualifiedSerializer = "System.Text.Json.JsonSerializer";
    private const string FormatterNamespace = "System.Runtime.Serialization.Formatters.Binary";

    public static SourceRewrite? Rewrite(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        if (!root.DescendantTokens().Any(IsBinaryFormatterToken))
        {
            return null;
        }

        var rewriter = new Rewriter();
        var rewritten = (CompilationUnitSyntax)rewriter.Visit(root);
        var leftAlone = rewritten.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Count(IsBinaryFormatterCreation);

        if (rewriter.Replaced == 0)
        {
            return new SourceRewrite(source, 0, leftAlone);
        }

        var formatterStillUsed = rewritten.DescendantTokens().Any(IsBinaryFormatterToken);
        return new SourceRewrite(Usings(rewritten, formatterStillUsed).ToFullString(), rewriter.Replaced, leftAlone);
    }

    private static CompilationUnitSyntax Usings(CompilationUnitSyntax root, bool formatterStillUsed)
    {
        var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Where(u => u.Alias is null).ToList();
        var formatterUsing = usings.FirstOrDefault(u => u.Name?.ToString() == FormatterNamespace);
        var hasJsonUsing = usings.Any(u => u.Name?.ToString() == "System.Text.Json");

        // Newtonsoft.Json also has a JsonSerializer: with both namespaces imported the short name would be ambiguous.
        var importsNewtonsoft = usings.Any(u => u.Name?.ToString() == "Newtonsoft.Json");
        if (importsNewtonsoft)
        {
            return root;
        }

        CompilationUnitSyntax result;
        if (formatterUsing is not null && !formatterStillUsed)
        {
            result = hasJsonUsing
                ? root.RemoveNode(formatterUsing, SyntaxRemoveOptions.KeepNoTrivia)!
                : root.ReplaceNode(formatterUsing,
                    SyntaxFactory.ParseCompilationUnit("using System.Text.Json;").Usings[0].WithTriviaFrom(formatterUsing));
        }
        else if (hasJsonUsing)
        {
            result = root;
        }
        else
        {
            return root; // keep the fully qualified name
        }

        return CSharpSyntaxTree.ParseText(result.ToFullString().Replace(QualifiedSerializer + ".", "JsonSerializer."))
            .GetCompilationUnitRoot();
    }

    private static bool IsBinaryFormatterToken(SyntaxToken t) =>
        t.IsKind(SyntaxKind.IdentifierToken) && t.Text == "BinaryFormatter";

    /// <summary>new BinaryFormatter() with no arguments and no initializer.</summary>
    internal static bool IsBinaryFormatterCreation(ObjectCreationExpressionSyntax creation)
    {
        var type = creation.Type.ToString().Replace(" ", "");
        return (type == "BinaryFormatter" || type.EndsWith(".BinaryFormatter", StringComparison.Ordinal))
               && (creation.ArgumentList is null || creation.ArgumentList.Arguments.Count == 0)
               && creation.Initializer is null;
    }

    private static bool IsFormatterCall(ExpressionSyntax e, string method, int argumentCount, out InvocationExpressionSyntax call)
    {
        call = null!;
        if (e is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Expression: ObjectCreationExpressionSyntax creation } access,
            } invocation
            && access.Name.Identifier.Text == method
            && invocation.ArgumentList.Arguments.Count == argumentCount
            && IsBinaryFormatterCreation(creation))
        {
            call = invocation;
            return true;
        }
        return false;
    }

    private sealed class Rewriter : CSharpSyntaxRewriter
    {
        public int Replaced { get; private set; }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            if (!IsFormatterCall(visited, "Serialize", 2, out var call))
            {
                return visited;
            }

            var args = call.ArgumentList.Arguments;
            Replaced++;
            return SyntaxFactory.ParseExpression($"{QualifiedSerializer}.Serialize({args[0]}, {args[1]})").WithTriviaFrom(visited);
        }

        public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node)
        {
            var visited = (CastExpressionSyntax)base.VisitCastExpression(node)!;
            if (!IsFormatterCall(visited.Expression, "Deserialize", 1, out var call))
            {
                return visited;
            }

            Replaced++;
            return SyntaxFactory.ParseExpression(
                    $"{QualifiedSerializer}.Deserialize<{visited.Type}>({call.ArgumentList.Arguments[0]})")
                .WithTriviaFrom(visited);
        }
    }
}
