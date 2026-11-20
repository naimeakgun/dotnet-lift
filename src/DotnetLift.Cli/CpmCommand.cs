using DotnetLift.Core.Loading;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Cli;

/// <summary>`dotnet-lift cpm`: move every package version into Directory.Packages.props.</summary>
public sealed class CpmCommand(Prompt prompt, TextWriter output)
{
    public int Run(Options options)
    {
        output.WriteLine("dotnet-lift · cpm (Central Package Management)");
        output.WriteLine();

        var solution = SolutionLoader.Load(options.Path);
        var root = File.Exists(solution.Path) ? Path.GetDirectoryName(solution.Path)! : solution.Path;
        var plan = CentralPackageManagement.Plan(solution);

        foreach (var note in plan.Notes)
        {
            output.WriteLine($"ℹ {note}");
        }

        if (plan.Blocker is not null)
        {
            output.WriteLine($"✗ {plan.Blocker}");
            return 1;
        }

        if (plan.Changes.Count == 0)
        {
            output.WriteLine("Nothing to do: no project file pins a package version.");
            return 0;
        }

        output.WriteLine("Changes:");
        foreach (var change in plan.Changes)
        {
            var name = Path.GetRelativePath(root, change.Path).Replace('\\', '/');
            if (change.IsCreate)
            {
                output.WriteLine($"    + {name} (new)");
            }
            else
            {
                var stats = LineDiff.Count(change.ReadOriginal(), change.NewContent);
                output.WriteLine($"    ~ {name} (+{stats.Added} −{stats.Removed})");
            }
        }

        if (plan.Conflicts.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Packages with more than one version (nothing changes for them; aligning is up to you):");
            foreach (var conflict in plan.Conflicts)
            {
                output.WriteLine($"  • {conflict}");
            }
        }
        output.WriteLine();

        if (options.DryRun)
        {
            foreach (var change in plan.Changes)
            {
                output.Write(LineDiff.Unified(change.ReadOriginal(), change.NewContent,
                    Path.GetRelativePath(root, change.Path).Replace('\\', '/')));
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

        FileChange.ApplyAll(plan.Changes);
        if (git is not null)
        {
            git.Commit(plan.Changes.SelectMany(c => c.Paths), "dotnet-lift: Central Package Management (Directory.Packages.props)");
            output.WriteLine("✓ Applied and committed.");
        }
        else
        {
            output.WriteLine("✓ Applied.");
        }
        output.WriteLine("Run `dotnet restore` to check: NU1008 means a project still sets a version, NU1010 that a version is missing.");
        return 0;
    }
}
