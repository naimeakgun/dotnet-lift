using System.Diagnostics;

namespace DotnetLift.Core.Upgrade;

/// <summary>Thin wrapper over the git CLI, used to make every applied step its own commit.</summary>
public sealed class GitRepository
{
    private GitRepository(string root)
    {
        Root = root;
    }

    public string Root { get; }

    /// <summary>Returns the repository containing <paramref name="directory"/>, or null if there is none (or git is missing).</summary>
    public static GitRepository? Find(string directory)
    {
        try
        {
            var (code, output, _) = Run(directory, "rev-parse", "--show-toplevel");
            return code == 0 && output.Trim().Length > 0 ? new GitRepository(Path.GetFullPath(output.Trim())) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // git is not installed
        }
    }

    public bool IsClean(out string status)
    {
        var (_, output, _) = Run(Root, "status", "--porcelain");
        status = output.Trim();
        return status.Length == 0;
    }

    /// <summary>Stages exactly the given paths (including deletions) and commits them.</summary>
    public void Commit(IEnumerable<string> paths, string message)
    {
        var relative = paths.Select(p => Path.GetRelativePath(Root, p)).ToArray();
        Check(Run(Root, ["add", "-A", "--", .. relative]), "git add");
        Check(Run(Root, "commit", "-q", "-m", message), "git commit");
    }

    private static void Check((int Code, string Output, string Error) result, string what)
    {
        if (result.Code != 0)
        {
            throw new InvalidOperationException($"{what} failed: {(result.Error.Length > 0 ? result.Error : result.Output).Trim()}");
        }
    }

    private static (int Code, string Output, string Error) Run(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}
