using DotnetLift.Core.Loading;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Cli;

/// <summary>`dotnet-lift cleanup`: remove references nothing uses and the backup folders Visual Studio left behind.</summary>
public sealed class CleanupCommand(Prompt prompt, TextWriter output)
{
    public int Run(Options options)
    {
        if (options.RemoveProject is { } name)
        {
            return RemoveProject(options, name);
        }

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

    /// <summary>`cleanup --remove-project NAME`: list what depends on the project, and take it out of the .sln only when nothing does.</summary>
    private int RemoveProject(Options options, string name)
    {
        output.WriteLine($"dotnet-lift · cleanup (remove project {name} from the solution)");
        output.WriteLine();

        var solution = SolutionLoader.Load(options.Path);
        var plan = ProjectRemoval.Plan(solution, name);
        var root = Path.GetDirectoryName(solution.Path)!;

        if (plan.Dependents.Count == 0)
        {
            output.WriteLine($"Nothing in the solution depends on {plan.Project.Name}.");
        }
        else
        {
            output.WriteLine($"Depends on {plan.Project.Name}:");
            foreach (var dependent in plan.Dependents)
            {
                output.WriteLine($"  {(dependent.Direct ? "✗" : "•")} {dependent.Project} ({dependent.How})");
            }
        }
        output.WriteLine();

        if (!plan.CanRemove)
        {
            output.WriteLine($"{plan.Project.Name} stays: remove the references marked ✗ first (or remove those projects too), then run this again.");
            return 1;
        }

        foreach (var project in plan.BuildOrderEntries)
        {
            output.WriteLine($"ℹ {project}: its build-order entry for {plan.Project.Name} in the solution goes too.");
        }
        output.WriteLine($"ℹ The folder {Rel(root, plan.Project.Directory)}/ stays on disk; delete it yourself once you are sure. Repositories outside this solution may still use it as a package.");
        output.WriteLine();

        var change = plan.SolutionChange!;
        var stats = LineDiff.Count(change.ReadOriginal(), change.NewContent);
        output.WriteLine($"    ~ {Rel(root, change.Path)} (+{stats.Added} −{stats.Removed})");
        output.WriteLine();

        if (options.DryRun)
        {
            output.Write(LineDiff.Unified(change.ReadOriginal(), change.NewContent, Rel(root, change.Path)));
            output.WriteLine();
            output.WriteLine("Dry run: nothing was written.");
            return 0;
        }

        var git = GitRepository.Find(root);
        if (git is not null && !options.AllowDirty && !git.IsClean(out _))
        {
            output.WriteLine("The working tree has uncommitted changes. Commit or stash them first, or pass --allow-dirty.");
            return 1;
        }

        if (!options.Yes && !prompt.Confirm($"Remove {plan.Project.Name} from the solution?", defaultYes: true))
        {
            output.WriteLine("Nothing was changed.");
            return 0;
        }

        change.Apply();
        Commit(git, [change], $"dotnet-lift: Remove {plan.Project.Name} from the solution");
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
