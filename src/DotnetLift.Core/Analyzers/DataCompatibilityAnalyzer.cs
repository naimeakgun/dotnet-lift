using System.Text.RegularExpressions;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetLift.Core.Analyzers;

/// <summary>
/// Code that compiles on both frameworks but produces different bytes or strings on modern .NET.
/// Nothing here fails the build, which is exactly why it has to be found up front: password hashes,
/// serialized values and cache keys change silently. Every finding says what to verify (ideally with
/// golden vectors produced by the .NET Framework build and compared on the new one).
/// Uses Roslyn syntax trees, so comments and string literals never produce findings.
/// </summary>
public sealed partial class DataCompatibilityAnalyzer : IAnalyzer
{
    public const string Category = "Data compatibility";

    // Code pages modern .NET has without CodePagesEncodingProvider.
    internal static readonly HashSet<int> BuiltInCodePages = [1200, 1201, 12000, 12001, 20127, 28591, 65000, 65001];
    internal static readonly HashSet<string> BuiltInEncodingNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "utf-8", "utf8", "utf-16", "utf-16le", "utf-16be", "unicode", "utf-32", "utf-32le", "utf-32be",
        "us-ascii", "ascii", "iso-8859-1", "latin1", "utf-7",
    };

    [GeneratedRegex(@"(?i)hash|md5|sha\d*|digest|computehash|hmac|cipher|encrypt|signature|salt")]
    private static partial Regex BinaryLooking();

    public string Name => "data-compatibility";

    public IEnumerable<Finding> Analyze(ProjectInfo project, AnalysisContext context)
    {
        var findings = new List<Finding>();
        var sources = project.SourceFiles.Select(f => (Path: f, Text: File.ReadAllText(f))).ToList();
        var registersCodePages = sources.Any(s => s.Text.Contains("CodePagesEncodingProvider", StringComparison.Ordinal));

        foreach (var (path, text) in sources)
        {
            var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
            var file = PathUtil.Relative(project.Directory, path);
            Finding At(SyntaxNode node, Severity severity, string message, string suggestion) =>
                new(project.Name, severity, Category, message, suggestion, file,
                    node.GetLocation().GetLineSpan().StartLinePosition.Line + 1);

            foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (access.Name.Identifier.Text == "Default" && IsEncodingType(access.Expression))
                {
                    findings.Add(At(access, Severity.Warning,
                        "Encoding.Default is the machine's ANSI code page on .NET Framework (e.g. windows-1254 on a Turkish server) but always UTF-8 on modern .NET: bytes differ for every non-ASCII character.",
                        "Name the encoding the stored data was written with, e.g. Encoding.GetEncoding(1254) (register CodePagesEncodingProvider on modern .NET). Verify with golden vectors that contain non-ASCII characters (ç ğ ı İ ö ş ü)."));
                }
            }

            foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (call.Expression is not MemberAccessExpressionSyntax member)
                {
                    continue;
                }

                var method = member.Name.Identifier.Text;
                var args = call.ArgumentList.Arguments;

                if (method == "GetEncoding" && IsEncodingType(member.Expression) && args.Count >= 1)
                {
                    var arg = args[0].Expression;
                    if (arg is LiteralExpressionSyntax { Token.Value: int zero } && zero == 0)
                    {
                        findings.Add(At(call, Severity.Warning,
                            "Encoding.GetEncoding(0) is the machine's ANSI code page on .NET Framework but UTF-8 on modern .NET (unless a code-page provider is registered).",
                            "Use the explicit code page the data was written with, and verify with golden vectors that contain non-ASCII characters."));
                    }
                    else if (!registersCodePages && IsLegacyCodePage(arg))
                    {
                        findings.Add(At(call, Severity.Warning,
                            $"Encoding.GetEncoding({arg}) throws NotSupportedException on modern .NET: legacy code pages are not available until CodePagesEncodingProvider is registered.",
                            "Call Encoding.RegisterProvider(CodePagesEncodingProvider.Instance) once at startup (System.Text.Encoding.CodePages is part of modern .NET)."));
                    }
                }
                else if (method == "GetString" && IsUtf8(member.Expression) && args.Count >= 1
                         && BinaryLooking().IsMatch(args[0].Expression.ToString()))
                {
                    findings.Add(At(call, Severity.Warning,
                        $"UTF-8 decoding of binary data ({args[0].Expression}): bytes that are not valid UTF-8 become U+FFFD, and since .NET Core 3.0 an invalid sequence can turn into a different number of U+FFFD characters than on .NET Framework, so the resulting string (and any hash or comparison built on it) can differ.",
                        "Keep stored values readable: compare golden vectors from the .NET Framework build. For new code, encode binary data as hex or Base64 (Convert.ToHexString / Convert.ToBase64String) instead of decoding it as text."));
                }
                else if (method == "GetHashCode" && args.Count == 0 && member.Expression is not BaseExpressionSyntax
                         && !InsideMember(call, "GetHashCode"))
                {
                    findings.Add(At(call, Severity.Info,
                        $"{member.Expression}.GetHashCode(): for strings this is randomized per process on modern .NET (and may differ between frameworks for other types).",
                        "Fine for dictionaries; never store, send or compare its value across processes. Use a stable hash (e.g. SHA-256, or XxHash from System.IO.Hashing) for anything persisted."));
                }
            }

            foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var type = creation.Type.ToString();
                if ((type.EndsWith("GZipStream", StringComparison.Ordinal) || type.EndsWith("DeflateStream", StringComparison.Ordinal))
                    && creation.ArgumentList?.Arguments.Any(a => !a.ToString().Contains("Decompress", StringComparison.Ordinal)
                                                                 && (a.ToString().Contains("CompressionMode.Compress", StringComparison.Ordinal)
                                                                     || a.ToString().Contains("CompressionLevel", StringComparison.Ordinal))) == true)
                {
                    findings.Add(At(creation, Severity.Info,
                        $"{type} compression: the compressed bytes are not guaranteed to be identical between .NET Framework and modern .NET (different zlib implementations), although each can read the other's output.",
                        "Test compatibility by decompressing output produced by the .NET Framework build (and round-tripping new output), not by comparing compressed bytes."));
                }
            }

            var casing = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(c => c.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "ToLower" or "ToUpper" }
                            && c.ArgumentList.Arguments.Count == 0)
                .ToList();
            if (casing.Count > 0)
            {
                findings.Add(At(casing[0], Severity.Info,
                    $"{casing.Count} culture-sensitive ToLower()/ToUpper() call(s) in this file. Results depend on the current culture (Turkish İ/ı), and modern .NET uses ICU where .NET Framework used Windows NLS, so some results can change.",
                    "Use ToLowerInvariant()/ToUpperInvariant() for identifiers and keys, or pass an explicit CultureInfo. Where the old behaviour must be kept exactly (e.g. text that is hashed), compare golden vectors; the System.Globalization.UseNls runtime option restores NLS on Windows."));
            }
        }

        return findings;
    }

    internal static bool IsEncodingType(ExpressionSyntax e) =>
        e.ToString().Replace(" ", "") is "Encoding" or "System.Text.Encoding" or "global::System.Text.Encoding";

    private static bool IsUtf8(ExpressionSyntax e)
    {
        var text = e.ToString().Replace(" ", "");
        return text.EndsWith("Encoding.UTF8", StringComparison.Ordinal) || text == "UTF8"
               || text.StartsWith("newUTF8Encoding(", StringComparison.Ordinal);
    }

    private static bool IsLegacyCodePage(ExpressionSyntax arg) => arg switch
    {
        LiteralExpressionSyntax { Token.Value: int page } => !BuiltInCodePages.Contains(page),
        LiteralExpressionSyntax { Token.Value: string name } => !BuiltInEncodingNames.Contains(name),
        _ => false, // a variable: cannot tell
    };

    private static bool InsideMember(SyntaxNode node, string name) =>
        node.Ancestors().OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == name);
}
