using System.Text.RegularExpressions;

namespace DotnetLift.Core.Build;

public sealed record TestRunResult(bool Succeeded, int Passed, int Failed, int Skipped, IReadOnlyList<string> FailedTests, string Output)
{
    public int Total => Passed + Failed + Skipped;
}

/// <summary>Reads the summary that `dotnet test` prints (VSTest and Microsoft.Testing.Platform formats).</summary>
public static partial class TestOutputParser
{
    // VSTest: "Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 40 ms - Lib.Tests.dll (net10.0)"
    [GeneratedRegex(@"(?:Passed|Failed)!\s*-\s*Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+),\s*Skipped:\s*(?<skipped>\d+)")]
    private static partial Regex VsTestSummary();

    // Microsoft.Testing.Platform: "Test run summary: Passed! ... total: 12 failed: 0 succeeded: 12 skipped: 0"
    [GeneratedRegex(@"total:\s*(?<total>\d+)\s+failed:\s*(?<failed>\d+)\s+succeeded:\s*(?<passed>\d+)\s+skipped:\s*(?<skipped>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PlatformSummary();

    // "  Failed Namespace.Class.Method [12 ms]"  /  "failed Namespace.Class.Method (12ms)"
    [GeneratedRegex(@"^\s*failed\s+(?<name>[^\s\[\(]+)\s*[\[\(]", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex FailedTest();

    public static TestRunResult Parse(string output, int exitCode)
    {
        int passed = 0, failed = 0, skipped = 0;
        var found = false;

        // A multi-target or multi-assembly run prints one summary per assembly: add them up.
        foreach (Match m in VsTestSummary().Matches(output))
        {
            found = true;
            failed += int.Parse(m.Groups["failed"].Value);
            passed += int.Parse(m.Groups["passed"].Value);
            skipped += int.Parse(m.Groups["skipped"].Value);
        }

        if (!found)
        {
            foreach (Match m in PlatformSummary().Matches(output))
            {
                found = true;
                failed += int.Parse(m.Groups["failed"].Value);
                passed += int.Parse(m.Groups["passed"].Value);
                skipped += int.Parse(m.Groups["skipped"].Value);
            }
        }

        var failedTests = FailedTest().Matches(output).Select(m => m.Groups["name"].Value).Distinct().ToList();
        var succeeded = exitCode == 0 && failed == 0;
        if (!found && !succeeded && failedTests.Count == 0)
        {
            // No summary at all (e.g. the test host did not start): report it as one failure rather than "0 tests".
            failed = 1;
        }

        return new TestRunResult(succeeded, passed, failed, skipped, failedTests, output);
    }
}
