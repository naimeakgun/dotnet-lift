using DotnetLift.Core;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Cli;

/// <summary>`dotnet-lift add-tests -p NAME`: an MSTest project for a library, added to the .sln, to write tests in before the library moves.</summary>
public sealed class AddTestsCommand(Prompt prompt, TextWriter output)
{
    public int Run(Options options, MigrationProfile profile)
    {
        if (options.Project is not { } name)
        {
            output.WriteLine("error: add-tests needs the project to write tests for: dotnet-lift add-tests [path] --project <name>");
            return 2;
        }

        output.WriteLine($"dotnet-lift · add-tests ({name})");
        output.WriteLine();

        var solution = SolutionLoader.Load(options.Path);
        var target = options.Target ?? profile.Target ?? SolutionAnalyzer.DefaultTarget;
        var plan = TestProjectScaffold.Plan(solution, name, target);
        var root = Path.GetDirectoryName(Path.GetFullPath(solution.Path))!;

        output.WriteLine($"{plan.Name}: MSTest, {plan.TargetFrameworks}, references {plan.Project.Name}");
        foreach (var note in plan.Notes)
        {
            output.WriteLine($"ℹ {note}");
        }
        output.WriteLine();

        foreach (var change in plan.Changes)
        {
            if (change.IsCreate)
            {
                output.WriteLine($"    + {Rel(root, change.Path)} (new)");
            }
            else
            {
                var stats = LineDiff.Count(change.ReadOriginal(), change.NewContent);
                output.WriteLine($"    ~ {Rel(root, change.Path)} (+{stats.Added} −{stats.Removed})");
            }
        }
        output.WriteLine();

        if (options.DryRun)
        {
            foreach (var change in plan.Changes)
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

        if (!options.Yes && !prompt.Confirm($"Add {plan.Name} to the solution?", defaultYes: true))
        {
            output.WriteLine("Nothing was changed.");
            return 0;
        }

        FileChange.ApplyAll(plan.Changes);
        var message = $"dotnet-lift: Add test project {plan.Name}";
        if (git is null)
        {
            output.WriteLine($"✓ Added {plan.Name}.");
        }
        else if (git.Commit(plan.Changes.SelectMany(c => c.Paths), message))
        {
            output.WriteLine($"✓ Added {plan.Name} (committed).");
        }
        else
        {
            output.WriteLine($"✓ Added {plan.Name} (nothing to commit: git did not track these files).");
        }
        output.WriteLine($"Next: write tests in {Rel(root, Path.GetDirectoryName(plan.ProjectPath)!)}/ and run `dotnet test {Rel(root, plan.ProjectPath)}`.");
        return 0;
    }

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
