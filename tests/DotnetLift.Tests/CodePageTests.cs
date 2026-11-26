using DotnetLift.Cli;
using DotnetLift.Core;
using DotnetLift.Core.Analyzers;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Rewrites;
using Microsoft.CodeAnalysis.CSharp;

namespace DotnetLift.Tests;

public class CodePageTests
{
    private const string Ns = "Acme.Lib.Infrastructure";

    [Fact]
    public void Encoding_Default_and_legacy_code_pages_go_through_the_helper()
    {
        const string source = """
            using System.Text;

            namespace Acme.Lib
            {
                class C
                {
                    // Encoding.Default in a comment stays
                    byte[] A(string s) => Encoding.Default.GetBytes(s);
                    Encoding B() => System.Text.Encoding.GetEncoding(0);
                    Encoding C1() => Encoding.GetEncoding(1254);
                    Encoding D() => Encoding.GetEncoding("windows-1254");
                    Encoding E() => Encoding.GetEncoding(65001);
                    Encoding F() => Encoding.UTF8;
                    Encoding G(int page) => Encoding.GetEncoding(page);
                }
            }
            """;

        var result = CodePageRewrite.Rewrite(source, Ns)!;

        Assert.Equal(4, result.Replaced);
        Assert.Contains("byte[] A(string s) => LegacyEncoding.Default.GetBytes(s);", result.NewText);
        Assert.Contains("Encoding B() => LegacyEncoding.Default;", result.NewText);
        Assert.Contains("Encoding C1() => LegacyEncoding.Get(1254);", result.NewText);
        Assert.Contains("Encoding D() => LegacyEncoding.Get(\"windows-1254\");", result.NewText);
        Assert.Contains("Encoding E() => Encoding.GetEncoding(65001);", result.NewText);
        Assert.Contains("Encoding G(int page) => Encoding.GetEncoding(page);", result.NewText);
        Assert.Contains("// Encoding.Default in a comment stays", result.NewText);
        Assert.Contains($"using System.Text;\nusing {Ns};", result.NewText.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Files_without_code_pages_are_left_alone()
    {
        Assert.Null(CodePageRewrite.Rewrite("using System.Text; class C { Encoding E() => Encoding.UTF8; }", Ns));
    }

    [Fact]
    public void Helper_is_plain_CSharp_7_3_and_uses_the_profile_code_page()
    {
        var code = CodePageRewrite.HelperSource(Ns, 1254);
        var tree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.CSharp7_3));

        Assert.Empty(tree.GetDiagnostics());
        Assert.Contains("public const int DefaultCodePage = 1254;", code);
        Assert.Contains("CodePagesEncodingProvider.Instance", code);
    }

    [Fact]
    public void The_step_is_planned_only_with_a_code_page_in_the_profile()
    {
        var analysis = new SolutionAnalyzer().Analyze(Samples.LegacyShopSln);
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, MigrationProfile.FileName), """{ "codePage": 1254 }""");

        var without = UpgradePlanner.Create(analysis);
        var with = UpgradePlanner.Create(analysis, MigrationProfile.Load(Path.Combine(dir, MigrationProfile.FileName)));

        Assert.DoesNotContain(without.Steps, s => s.Id == "explicit-code-page");
        var step = Assert.Single(with.Steps, s => s.Id == "explicit-code-page");
        Assert.Equal("LegacyShop.Core", step.Project);
        Assert.Equal("Use code page 1254 instead of Encoding.Default", step.Title);
        Assert.Equal("1254", step.Argument);
    }

    [Fact]
    public async Task Upgrade_with_a_code_page_rewrites_the_sample_and_the_finding_goes_away()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        File.WriteAllText(Path.Combine(dir, MigrationProfile.FileName), """{ "target": "net10.0", "codePage": 1254 }""");
        Git.InitWithCommit(dir);

        await new App(new StringReader(""), new StringWriter(), interactive: false, new FakeBuilder()).RunAsync(["upgrade", dir, "--yes"]);

        var hasher = File.ReadAllText(Path.Combine(dir, "LegacyShop.Core", "Services", "PasswordHasher.cs"));
        Assert.Contains("LegacyEncoding.Default.GetBytes(", hasher);
        Assert.Contains("using LegacyShop.Core.Infrastructure;", hasher);
        Assert.True(File.Exists(Path.Combine(dir, "LegacyShop.Core", "Infrastructure", "LegacyEncoding.cs")));
        Assert.Contains("dotnet-lift: Use code page 1254 instead of Encoding.Default (LegacyShop.Core)", Git.LogSubjects(dir));

        var findings = new SolutionAnalyzer().Analyze(dir).Findings;
        Assert.DoesNotContain(findings, f => f.Category == DataCompatibilityAnalyzer.Category && f.Message.StartsWith("Encoding.Default"));
        Assert.Equal("", Git.Status(dir));
    }
}
