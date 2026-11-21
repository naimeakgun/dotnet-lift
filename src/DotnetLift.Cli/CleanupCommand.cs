using DotnetLift.Core.Loading;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Cli;

/// <summary>`dotnet-lift cleanup`: remove references nothing uses and the backup folders Visual Studio left behind.</summary>
public sealed class CleanupCommand(Prompt prompt, TextWriter output)
{
    public int Run(Options options)
    {
        output.WriteLine("dotnet-lift · cleanup (unused references, migration backups)");
        output.WriteLine();

        var solution = SolutionLoader.Load(options.Path);
        var root = File.Exists(solution.Path) ? Path.GetDirectoryName(solution.Path)! : solution.Path;
        var plan = Cleanup.Plan(solution);

        if (plan.Removed.Count > 0)
        {
            output.WriteLine("Unused, removed:");
            foreach (var item in plan.Removed)
            {
                output.WriteLine($"  ✓ {item.Project}: {item.Library} ({string.Join(", ", item.References)})");
            }
        }

        if (plan.Kept.Count > 0)
        {
            output.WriteLine("Still used, kept:");
            foreach (var item in plan.Kept)
            {
                output.WriteLine($"  • {item.Project}: {item.Library}: {item.KeptBecause}");
            }
        }

        if (plan.BackupFolders.Count > 0)
        {
            output.WriteLine("Backup folders left by Visual Studio, deleted:");
            foreach (var folder in plan.BackupFolders)
            {
                var files = plan.BackupChanges.Count(c => c.Path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal));
                output.WriteLine($"  ✓ {Rel(root, folder)}/ ({files} file(s))");
            }
        }

        foreach (var note in plan.Notes)
        {
            output.WriteLine($"ℹ {note}");
        }
        output.WriteLine();

        if (plan.ReferenceChanges.Count == 0 && plan.BackupFolders.Count == 0)
        {
            output.WriteLine("Nothing to clean up.");
            return 0;
        }

        foreach (var change in plan.ReferenceChanges)
        {
            var stats = LineDiff.Count(change.ReadOriginal(), change.NewContent);
            output.WriteLine($"    ~ {Rel(root, change.Path)} (+{stats.Added} −{stats.Removed})");
        }
        output.WriteLine();

        if (options.DryRun)
        {
            foreach (var change in plan.ReferenceChanges)
            {
                output.Write(LineDiff.Unified(change.ReadOriginal(), change.NewContent, Rel(root, change.Path)));
                output.WriteLine();
            }
            output.WriteLine("Dry run: nothing was written.");
            return 0;
        }

        var git = GitRepository.Find(root);
        if (git is not null && !options.AllowDirty && !git.IsClean(out _))
        {
            output.WriteLine("The working tree has uncommitted changes. Commit or stash them first, or pass --allow-dirty.");
            return 1;
        }

        if (!options.Yes && !prompt.Confirm("Apply these changes?", defaultYes: true))
        {
            output.WriteLine("Nothing was changed.");
            return 0;
        }

        if (plan.ReferenceChanges.Count > 0)
        {
            FileChange.ApplyAll(plan.ReferenceChanges);
            Commit(git, plan.ReferenceChanges, "dotnet-lift: Remove unused references");
        }

        if (plan.BackupFolders.Count > 0)
        {
            FileChange.ApplyAll(plan.BackupChanges);
            Cleanup.RemoveEmptyFolders(plan.BackupFolders);
            Commit(git, plan.BackupChanges, "dotnet-lift: Remove Visual Studio migration backups");
        }

        if (plan.ReferenceChanges.Count > 0)
        {
            output.WriteLine("Build the solution to confirm nothing needed them; every removal is its own commit and can be reverted.");
        }
        return 0;
    }

    private void Commit(GitRepository? git, IReadOnlyList<FileChange> changes, string message)
    {
        var subject = message["dotnet-lift: ".Length..];
        if (git is null)
        {
            output.WriteLine($"✓ {subject}.");
        }
        else if (git.Commit(changes.SelectMany(c => c.Paths), message))
        {
            output.WriteLine($"✓ {subject} (committed).");
        }
        else
        {
            output.WriteLine($"✓ {subject} (nothing to commit: git did not track these files).");
        }
    }

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
