using DotnetLift.Cli;
using DotnetLift.Core.Build;

namespace DotnetLift.Tests;

public class TestRunTests
{
    /// <summary>An SDK-style MSTest project that already targets net48 and net10.0, in a git repository.</summary>
    private static string TestProject()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-lift-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Lib.Tests"));
        File.WriteAllText(Path.Combine(dir, "Lib.Tests", "Lib.Tests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net48;net10.0</TargetFrameworks>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
                <PackageReference Include="MSTest.TestAdapter" Version="3.8.3" />
                <PackageReference Include="MSTest.TestFramework" Version="3.8.3" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Lib.Tests", "HashTests.cs"),
            "using Microsoft.VisualStudio.TestTools.UnitTesting;\n[TestClass] public class HashTests { [TestMethod] public void Same() { } }\n");
        Git.InitWithCommit(dir);
        return dir;
    }

    private static async Task<string> Upgrade(string dir, FakeBuilder builder)
    {
        var output = new StringWriter();
        await new App(new StringReader(""), output, interactive: false, builder).RunAsync(["upgrade", dir, "--yes", "--target", "net10.0"]);
        return output.ToString();
    }

    [Fact]
    public async Task Tests_run_after_the_test_project_builds()
    {
        var dir = TestProject();
        var builder = new FakeBuilder();

        var output = await Upgrade(dir, builder);

        var call = Assert.Single(builder.TestCalls);
        Assert.Equal("net10.0", call.Tfm);
        Assert.Contains("✓ Lib.Tests: tests pass on net10.0 (3 passed, 0 failed, 0 skipped).", output);
        Assert.Contains("Tests on net10.0: passing: Lib.Tests", output);
    }

    [Fact]
    public async Task Failing_tests_are_listed_by_name()
    {
        var dir = TestProject();
        var builder = new FakeBuilder
        {
            TestResult = new TestRunResult(false, 10, 2, 1, ["Lib.Tests.HashTests.TurkishPassword", "Lib.Tests.HashTests.Utf8OfMd5"], "Failed!"),
        };

        var output = await Upgrade(dir, builder);

        Assert.Contains("✗ Lib.Tests: tests fail on net10.0 (10 passed, 2 failed, 1 skipped).", output);
        Assert.Contains("Lib.Tests.HashTests.TurkishPassword", output);
        Assert.Contains("Tests on net10.0: none passing yet; failing: Lib.Tests", output);
    }

    [Fact]
    public async Task Tests_do_not_run_when_the_build_fails()
    {
        var dir = TestProject();
        var builder = new FakeBuilder(FakeBuilder.Failure(new Diagnostic("CS1002", "; expected",
            Path.Combine(dir, "Lib.Tests", "HashTests.cs"), 1, Path.Combine(dir, "Lib.Tests", "Lib.Tests.csproj"))));

        await Upgrade(dir, builder);

        Assert.Empty(builder.TestCalls);
    }

    [Fact]
    public async Task Libraries_that_are_not_test_projects_are_only_built()
    {
        var dir = Samples.CopyToTemp(Samples.LegacyShopDir);
        Git.InitWithCommit(dir);
        var builder = new FakeBuilder();

        await Upgrade(dir, builder);

        Assert.Empty(builder.TestCalls);
    }

    [Fact]
    public void Parses_the_VSTest_summary_and_failed_test_names()
    {
        const string output = """
              Failed Lib.Tests.HashTests.TurkishPassword [12 ms]
              Error Message:
               Assert.AreEqual failed.
            Failed!  - Failed:     1, Passed:    11, Skipped:     2, Total:    14, Duration: 40 ms - Lib.Tests.dll (net10.0)
            """;

        var result = TestOutputParser.Parse(output, exitCode: 1);

        Assert.False(result.Succeeded);
        Assert.Equal((11, 1, 2, 14), (result.Passed, result.Failed, result.Skipped, result.Total));
        Assert.Equal("Lib.Tests.HashTests.TurkishPassword", Assert.Single(result.FailedTests));
    }

    [Fact]
    public void Adds_up_one_summary_per_assembly_and_reads_the_testing_platform_format()
    {
        const string vstest = """
            Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 5 ms - A.Tests.dll (net10.0)
            Passed!  - Failed:     0, Passed:     4, Skipped:     1, Total:     5, Duration: 5 ms - B.Tests.dll (net10.0)
            """;
        const string platform = "Test run summary: Passed!\n  total: 9\n  failed: 0\n  succeeded: 8\n  skipped: 1\n  duration: 1s";

        var a = TestOutputParser.Parse(vstest, 0);
        var b = TestOutputParser.Parse(platform.Replace("\n ", " ").Replace("\n", " "), 0);

        Assert.Equal((7, 0, 1), (a.Passed, a.Failed, a.Skipped));
        Assert.True(b.Succeeded);
        Assert.Equal((8, 0, 1), (b.Passed, b.Failed, b.Skipped));
    }

    [Fact]
    public void A_failed_run_without_a_summary_is_not_reported_as_zero_tests()
    {
        var result = TestOutputParser.Parse("Testhost process exited with error", exitCode: 1);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.Failed);
    }
}
