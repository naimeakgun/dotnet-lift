using System.Diagnostics;

namespace DotnetLift.Core.Build;

public interface IBuilder
{
    BuildResult Build(string projectPath, string targetFramework);
}

/// <summary>Runs <c>dotnet build</c> for one target framework and parses the errors.</summary>
public sealed class DotnetBuilder : IBuilder
{
    public BuildResult Build(string projectPath, string targetFramework)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "build", projectPath, "-f", targetFramework, "-nologo", "-tl:off", "-v", "q", "-clp:NoSummary" })
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
            throw new InvalidOperationException("The dotnet SDK was not found on PATH; it is needed to build the project.");
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            var output = stdout.Result + stderr.Result;
            return new BuildResult(process.ExitCode == 0, DiagnosticParser.ParseErrors(output), output);
        }
    }
}
