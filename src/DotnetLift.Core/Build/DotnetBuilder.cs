using System.Diagnostics;

namespace DotnetLift.Core.Build;

public interface IBuilder
{
    BuildResult Build(string projectPath, string targetFramework);
}

/// <summary>Runs the tests of an already built test project for one target framework.</summary>
public interface ITestRunner
{
    TestRunResult Test(string projectPath, string targetFramework);
}

/// <summary>Runs <c>dotnet build</c> / <c>dotnet test</c> for one target framework and parses the results.</summary>
public sealed class DotnetBuilder : IBuilder, ITestRunner
{
    public BuildResult Build(string projectPath, string targetFramework)
    {
        var (exitCode, output) = Run(projectPath,
            "build", projectPath, "-f", targetFramework, "-nologo", "-tl:off", "-v", "q", "-clp:NoSummary");
        return new BuildResult(exitCode == 0, DiagnosticParser.ParseErrors(output), output);
    }

    public TestRunResult Test(string projectPath, string targetFramework)
    {
        // --no-build: the build-fix loop has just built this target.
        var (exitCode, output) = Run(projectPath,
            "test", projectPath, "-f", targetFramework, "--no-build", "-nologo", "-tl:off", "-v", "q");
        return TestOutputParser.Parse(output, exitCode);
    }

    private static (int ExitCode, string Output) Run(string projectPath, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("The dotnet SDK was not found on PATH; it is needed to build and test the project.");
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return (process.ExitCode, stdout.Result + stderr.Result);
        }
    }
}
