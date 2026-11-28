using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetLift.Core.Rewrites;

/// <summary>Which generated helpers a rewritten file needs.</summary>
[Flags]
public enum LegacyHelpers
{
    None = 0,
    LogicalCallContext = 1,
    PasswordHash = 2,
}

/// <summary>
/// Rewrites with an exact equivalent on both frameworks, through small helpers generated into the library (C# 7.3):
///   CallContext.LogicalSetData / LogicalGetData / FreeNamedDataSlot  → LogicalCallContext (AsyncLocal, flows the same way)
///   FormsAuthentication.HashPasswordForStoringInConfigFile(p, "SHA1" | "MD5") → LegacyPasswordHash (same uppercase hex of UTF-8 bytes)
/// CallContext.SetData / GetData (data that does not flow) and other hash formats are left for the report.
/// </summary>
public static class LegacyApiRewrite
{
    public const string CallContextHelper = "LogicalCallContext";
    public const string PasswordHashHelper = "LegacyPasswordHash";

    public sealed record Result(string NewText, int Replaced, LegacyHelpers Helpers);

    private static readonly Dictionary<string, string> CallContextMethods = new(StringComparer.Ordinal)
    {
        ["LogicalSetData"] = "SetData",
        ["LogicalGetData"] = "GetData",
        ["FreeNamedDataSlot"] = "FreeNamedDataSlot",
    };

    // System.Web.Security names that keep its using directive necessary.
    private static readonly HashSet<string> WebSecurityNames =
        ["FormsAuthentication", "FormsAuthenticationTicket", "Membership", "MembershipUser", "MembershipProvider", "Roles", "RoleProvider", "MachineKey", "FormsIdentity"];

    public static Result? Rewrite(string source, string helperNamespace)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var replacements = new Dictionary<SyntaxNode, string>();
        var helpers = LegacyHelpers.None;

        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax member)
            {
                continue;
            }

            var type = member.Expression.ToString().Replace(" ", "").Replace("global::", "");
            var name = member.Name.Identifier.Text;
            if (type is "CallContext" or "System.Runtime.Remoting.Messaging.CallContext" && CallContextMethods.TryGetValue(name, out var method))
            {
                replacements[member] = $"{CallContextHelper}.{method}";
                helpers |= LegacyHelpers.LogicalCallContext;
            }
            else if (type is "FormsAuthentication" or "System.Web.Security.FormsAuthentication"
                     && name == "HashPasswordForStoringInConfigFile"
                     && call.ArgumentList.Arguments.Count == 2
                     && call.ArgumentList.Arguments[1].Expression is LiteralExpressionSyntax literal
                     && literal.IsKind(SyntaxKind.StringLiteralExpression)
                     && literal.Token.ValueText.ToUpperInvariant() is "SHA1" or "MD5")
            {
                replacements[member] = $"{PasswordHashHelper}.HashPasswordForStoringInConfigFile";
                helpers |= LegacyHelpers.PasswordHash;
            }
        }

        if (replacements.Count == 0)
        {
            return null;
        }

        var rewritten = root.ReplaceNodes(replacements.Keys,
            (original, _) => SyntaxFactory.ParseExpression(replacements[original]).WithTriviaFrom(original));
        rewritten = Usings(rewritten, helperNamespace);
        return new Result(rewritten.ToFullString(), replacements.Count, helpers);
    }

    private static CompilationUnitSyntax Usings(CompilationUnitSyntax root, string helperNamespace)
    {
        var identifiers = root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.Text).ToHashSet(StringComparer.Ordinal);
        var usings = root.Usings;

        bool Unused(UsingDirectiveSyntax u) =>
            u.Alias is null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name?.ToString() switch
            {
                "System.Runtime.Remoting.Messaging" => !identifiers.Contains("CallContext") && !identifiers.Contains("IMessage"),
                "System.Web.Security" => !identifiers.Overlaps(WebSecurityNames),
                _ => false,
            };

        var removed = usings.Where(Unused).ToList();
        var index = removed.Count > 0 ? usings.IndexOf(removed[0]) : -1;
        foreach (var u in removed)
        {
            usings = usings.Remove(u);
        }

        var inNamespace = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Any(n => n.Name.ToString() == helperNamespace);
        if (!inNamespace && !usings.Any(u => u.Alias is null && u.Name?.ToString() == helperNamespace))
        {
            var eol = root.Usings.LastOrDefault()?.GetTrailingTrivia().ToString() is { Length: > 0 } t ? t : Environment.NewLine;
            var added = SyntaxFactory.ParseCompilationUnit($"using {helperNamespace};").Usings[0].WithTrailingTrivia(SyntaxFactory.ParseTrailingTrivia(eol));
            usings = index >= 0 ? usings.Insert(index, added.WithLeadingTrivia(removed[0].GetLeadingTrivia())) : usings.Add(added);
        }
        return root.WithUsings(usings);
    }

    public static string CallContextSource(string ns) => Lines(
        "using System.Collections.Concurrent;",
        "using System.Threading;",
        "",
        $"namespace {ns}",
        "{",
        "    /// <summary>",
        "    /// CallContext.LogicalSetData / LogicalGetData on AsyncLocal (added by dotnet-lift): values flow into tasks, threads",
        "    /// and awaits started afterwards, and changes made there do not flow back, as with the logical call context",
        "    /// since .NET Framework 4.5. Works on .NET Framework 4.6+ and modern .NET.",
        "    /// </summary>",
        "    public static class LogicalCallContext",
        "    {",
        "        private static readonly ConcurrentDictionary<string, AsyncLocal<object>> Slots =",
        "            new ConcurrentDictionary<string, AsyncLocal<object>>();",
        "",
        "        public static void SetData(string name, object data)",
        "        {",
        "            Slots.GetOrAdd(name, _ => new AsyncLocal<object>()).Value = data;",
        "        }",
        "",
        "        public static object GetData(string name)",
        "        {",
        "            AsyncLocal<object> slot;",
        "            return Slots.TryGetValue(name, out slot) ? slot.Value : null;",
        "        }",
        "",
        "        public static void FreeNamedDataSlot(string name)",
        "        {",
        "            AsyncLocal<object> slot;",
        "            if (Slots.TryGetValue(name, out slot))",
        "            {",
        "                slot.Value = null;",
        "            }",
        "        }",
        "    }",
        "}");

    public static string PasswordHashSource(string ns) => Lines(
        "using System;",
        "using System.Security.Cryptography;",
        "using System.Text;",
        "",
        $"namespace {ns}",
        "{",
        "    /// <summary>",
        "    /// FormsAuthentication.HashPasswordForStoringInConfigFile for \"SHA1\" and \"MD5\" (added by dotnet-lift):",
        "    /// the same uppercase hex of the hash of the password's UTF-8 bytes, so stored hashes keep matching.",
        "    /// Both algorithms are weak for passwords: plan a move to a salted, slow hash (PBKDF2) with re-hashing on sign-in.",
        "    /// </summary>",
        "    public static class LegacyPasswordHash",
        "    {",
        "        public static string HashPasswordForStoringInConfigFile(string password, string passwordFormat)",
        "        {",
        "            if (password == null) throw new ArgumentNullException(\"password\");",
        "            if (passwordFormat == null) throw new ArgumentNullException(\"passwordFormat\");",
        "",
        "            HashAlgorithm algorithm;",
        "            if (string.Equals(passwordFormat, \"sha1\", StringComparison.OrdinalIgnoreCase)) algorithm = SHA1.Create();",
        "            else if (string.Equals(passwordFormat, \"md5\", StringComparison.OrdinalIgnoreCase)) algorithm = MD5.Create();",
        "            else throw new ArgumentException(\"Unsupported password format: \" + passwordFormat, \"passwordFormat\");",
        "",
        "            using (algorithm)",
        "            {",
        "                var hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(password));",
        "                var hex = new StringBuilder(hash.Length * 2);",
        "                foreach (var b in hash)",
        "                {",
        "                    hex.Append(b.ToString(\"X2\"));",
        "                }",
        "                return hex.ToString();",
        "            }",
        "        }",
        "    }",
        "}");

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;
}
