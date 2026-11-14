using System.Diagnostics;

namespace DotnetLift.Tests;

/// <summary>Test helper: create throwaway git repositories and inspect them.</summary>
internal static class Git
{
    public static void InitWithCommit(string dir)
    {
        Run(dir, "init", "-q");
        Run(dir, "config", "user.name", "dotnet-lift tests");
        Run(dir, "config", "user.email", "tests@example.com");
        Run(dir, "config", "commit.gpgsign", "false");
        Run(dir, "add", "-A");
        Run(dir, "commit", "-q", "-m", "initial");
    }

    public static string[] LogSubjects(string dir) =>
        Run(dir, "log", "--format=%s").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string Status(string dir) => Run(dir, "status", "--porcelain").Trim();

    public static string Run(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.Result}");
        }
        return stdout.Result;
    }
}
