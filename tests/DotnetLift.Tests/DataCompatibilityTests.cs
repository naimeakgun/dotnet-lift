using DotnetLift.Core;
using DotnetLift.Core.Analyzers;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;

namespace DotnetLift.Tests;

public class DataCompatibilityTests
{
    /// <summary>Analyzes one C# file in a throwaway SDK-style library.</summary>
    private static List<Finding> Analyze(string code, params (string Name, string Code)[] extraFiles)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Lib.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(dir, "Code.cs"), code);
        foreach (var (name, extra) in extraFiles)
        {
            File.WriteAllText(Path.Combine(dir, name), extra);
        }

        var solution = SolutionLoader.Load(dir);
        var project = solution.Projects.Single();
        return new DataCompatibilityAnalyzer().Analyze(project, new AnalysisContext(solution, "net10.0")).ToList();
    }

    private static string InMethod(string body) =>
        "using System; using System.IO; using System.IO.Compression; using System.Text;\n" +
        "class C\n{\n    object M(string s, byte[] hash, Stream stream)\n    {\n" + body + "\n        return null;\n    }\n}\n";

    [Fact]
    public void Finds_the_legacy_hashing_patterns_in_the_sample()
    {
        var analysis = new SolutionAnalyzer().Analyze(Samples.LegacyShopSln);
        var core = analysis.Findings
            .Where(f => f.Project == "LegacyShop.Core" && f.Category == DataCompatibilityAnalyzer.Category)
            .ToList();

        Assert.All(core, f => Assert.Equal("Services/PasswordHasher.cs", f.File));
        Assert.Contains(core, f => f.Message.StartsWith("Encoding.Default") && f.Line == 14 && f.Severity == Severity.Warning);
        Assert.Contains(core, f => f.Message.StartsWith("UTF-8 decoding of binary data (hash)") && f.Line == 16);
        Assert.Contains(core, f => f.Message.StartsWith("1 culture-sensitive ToLower()/ToUpper()") && f.Line == 14);
        Assert.Contains(core, f => f.Message.StartsWith("userName.GetHashCode()") && f.Line == 22 && f.Severity == Severity.Info);
        Assert.Equal(4, core.Count);
    }

    [Fact]
    public void Comments_and_strings_do_not_count()
    {
        var findings = Analyze(InMethod("""
                    // Encoding.Default was used here before
                    var text = "Encoding.UTF8.GetString(hash) and s.ToLower()";
            """));

        Assert.Empty(findings);
    }

    [Fact]
    public void GetEncoding_zero_is_reported_like_Encoding_Default()
    {
        var finding = Assert.Single(Analyze(InMethod("        var e = Encoding.GetEncoding(0);")));

        Assert.StartsWith("Encoding.GetEncoding(0)", finding.Message);
        Assert.Equal(Severity.Warning, finding.Severity);
    }

    [Theory]
    [InlineData("1254")]
    [InlineData("\"windows-1254\"")]
    [InlineData("\"iso-8859-9\"")]
    public void Legacy_code_pages_need_the_provider(string argument)
    {
        var finding = Assert.Single(Analyze(InMethod($"        var e = Encoding.GetEncoding({argument});")));

        Assert.Contains("NotSupportedException", finding.Message);
        Assert.Contains("CodePagesEncodingProvider", finding.Suggestion);
    }

    [Fact]
    public void Registered_provider_anywhere_in_the_project_silences_the_code_page_finding()
    {
        var findings = Analyze(InMethod("        var e = Encoding.GetEncoding(1254);"),
            ("Startup.cs", "using System.Text; static class S { static void Init() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } }"));

        Assert.Empty(findings);
    }

    [Theory]
    [InlineData("65001")]
    [InlineData("\"utf-8\"")]
    [InlineData("\"us-ascii\"")]
    [InlineData("28591")]
    public void Built_in_encodings_are_fine(string argument)
    {
        Assert.Empty(Analyze(InMethod($"        var e = Encoding.GetEncoding({argument});")));
    }

    [Fact]
    public void Only_binary_looking_UTF8_decoding_is_reported()
    {
        var findings = Analyze(InMethod("""
                    var a = Encoding.UTF8.GetString(hash);
                    var b = System.Text.Encoding.UTF8.GetString(md5.ComputeHash(stream));
                    var c = Encoding.UTF8.GetString(stream.ToString() == "" ? new byte[0] : new byte[1]);
            """));

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.StartsWith("UTF-8 decoding of binary data", f.Message));
    }

    [Fact]
    public void Casing_calls_are_reported_once_per_file()
    {
        var finding = Assert.Single(Analyze(InMethod("""
                    var a = s.ToLower();
                    var b = s.ToUpper();
                    var c = s.ToLowerInvariant();
                    var d = s.ToLower(System.Globalization.CultureInfo.InvariantCulture);
            """)));

        Assert.StartsWith("2 culture-sensitive ToLower()/ToUpper() call(s)", finding.Message);
        Assert.Equal(Severity.Info, finding.Severity);
    }

    [Fact]
    public void GetHashCode_inside_a_GetHashCode_override_is_fine()
    {
        const string code = """
            class Key
            {
                public string Name;
                public override int GetHashCode() { return Name.GetHashCode(); }
                public override bool Equals(object o) { return base.GetHashCode() == o.GetHashCode(); }
            }
            """;

        var finding = Assert.Single(Analyze(code)); // only o.GetHashCode() in Equals
        Assert.StartsWith("o.GetHashCode()", finding.Message);
    }

    [Fact]
    public void Compression_is_reported_but_decompression_is_not()
    {
        var findings = Analyze(InMethod("""
                    var z = new GZipStream(stream, CompressionMode.Compress);
                    var d = new DeflateStream(stream, CompressionLevel.Optimal);
                    var r = new GZipStream(stream, CompressionMode.Decompress);
            """));

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Contains("decompressing output produced by the .NET Framework build", f.Suggestion));
    }
}
