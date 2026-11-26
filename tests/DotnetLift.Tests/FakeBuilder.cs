using DotnetLift.Core.Build;

namespace DotnetLift.Tests;

/// <summary>Stands in for `dotnet build`: returns scripted results in order (the last one repeats). Default: success.</summary>
internal sealed class FakeBuilder(params BuildResult[] results) : IBuilder, ITestRunner
{
    /// <summary>What Test() returns; passing by default.</summary>
    public TestRunResult TestResult { get; set; } = new(true, 3, 0, 0, [], "Passed!");

    public List<(string Project, string Tfm)> TestCalls { get; } = [];

    public TestRunResult Test(string projectPath, string targetFramework)
    {
        TestCalls.Add((projectPath, targetFramework));
        return TestResult;
    }

    public static readonly BuildResult Success = new(true, [], "Build succeeded.");

    public List<(string Project, string Tfm)> Calls { get; } = [];

    public BuildResult Build(string projectPath, string targetFramework)
    {
        Calls.Add((projectPath, targetFramework));
        if (results.Length == 0)
        {
            return Success;
        }
        return results[Math.Min(Calls.Count - 1, results.Length - 1)];
    }

    public static BuildResult Failure(params Diagnostic[] errors) => new(false, errors, "Build FAILED.");
}
