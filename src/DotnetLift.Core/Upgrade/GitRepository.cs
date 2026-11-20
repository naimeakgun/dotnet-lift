using System.Diagnostics;
using System.Text;

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

    /// <summary>
    /// Stages exactly the given paths (including deletions and both ends of a move) and commits them.
    /// Paths git cannot stage are left out: ignored files, and files that never were in git and are gone now
    /// (for example an ignored file a step moved). Returns false when nothing was left to commit.
    /// </summary>
    public bool Commit(IEnumerable<string> paths, string message)
    {
        var relative = paths.Select(p => ToGitPath(Path.GetRelativePath(Root, p))).Distinct().ToArray();

        // Tracked files (changed or deleted) and new files that are not ignored; -z keeps names unquoted.
        var listed = Run(Root, ["--literal-pathspecs", "ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", .. relative]);
        Check(listed, "git ls-files");
        var stageable = listed.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        if (stageable.Length == 0)
        {
            return false;
        }

        Check(Run(Root, ["--literal-pathspecs", "add", "-A", "--", .. stageable]), "git add");
        Check(Run(Root, "commit", "-q", "-m", message), "git commit");
        return true;
    }

    private static string ToGitPath(string relative) =>
        Path.DirectorySeparatorChar == '\\' ? relative.Replace('\\', '/') : relative;

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
            // git writes paths as UTF-8; the console code page on Windows would garble non-ASCII names.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
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
