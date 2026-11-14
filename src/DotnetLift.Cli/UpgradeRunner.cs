using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Cli;

/// <summary>Walks the plan step by step: shows what will change, asks, applies, commits.</summary>
public sealed class UpgradeRunner(Prompt prompt, TextWriter output)
{
    public sealed record Settings(bool Yes, bool DryRun, bool AllowDirty);

    public sealed record Summary(int Applied, int Skipped, int NotAutomated, int NothingToDo, bool Quit);

    public int Run(AnalysisResult analysis, UpgradePlan plan, Settings settings)
    {
        var root = File.Exists(analysis.Solution.Path)
            ? Path.GetDirectoryName(analysis.Solution.Path)!
            : analysis.Solution.Path;

        GitRepository? git = null;
        if (!settings.DryRun && !CheckGit(root, settings, out git))
        {
            return 1;
        }

        var automatable = plan.Steps.Count(s => StepExecutors.For(s, ProjectOf(analysis, s)) is not null);
        output.WriteLine($"{plan.Steps.Count} step(s), {automatable} can be done automatically right now.");
        if (settings.DryRun)
        {
            output.WriteLine("Dry run: nothing will be written.");
        }
        output.WriteLine();

        var summary = Walk(analysis, plan, settings, root, git);

        output.WriteLine();
        output.WriteLine(summary.Quit ? "Stopped." : "Done.");
        output.WriteLine($"Applied {summary.Applied}, skipped {summary.Skipped}, nothing to change {summary.NothingToDo}, " +
                         $"not automated yet {summary.NotAutomated}.");
        if (summary.Applied > 0 && git is not null)
        {
            output.WriteLine("Each applied step is a separate commit: `git log --oneline` shows them, `git revert <sha>` undoes one.");
        }
        output.WriteLine("Run `dotnet-lift analyze` again to see what is left.");
        return 0;
    }

    private Summary Walk(AnalysisResult analysis, UpgradePlan plan, Settings settings, string root, GitRepository? git)
    {
        int applied = 0, skipped = 0, notAutomated = 0, nothing = 0;
        var applyAll = settings.Yes;

        foreach (var step in plan.Steps)
        {
            var project = ProjectOf(analysis, step);
            var mode = step.Mode switch
            {
                StepMode.Automatic => "automatic",
                StepMode.AgentAssisted => "agent-assisted",
                _ => "manual",
            };
            output.WriteLine($"[{step.Number}/{plan.Steps.Count}] {step.Project} · {step.Title}  ({mode}, {step.Risk.ToString().ToLowerInvariant()} risk)");

            var executor = StepExecutors.For(step, project);
            if (executor is null)
            {
                output.WriteLine("  → Not automated yet; it stays in the report as a to-do.");
                notAutomated++;
                continue;
            }

            var changes = executor.Prepare(project, step, plan.TargetFramework);
            if (changes.Count == 0)
            {
                output.WriteLine("  → Nothing to change.");
                nothing++;
                continue;
            }

            output.WriteLine($"  {step.Description}");
            PrintChanges(changes, root);

            if (settings.DryRun)
            {
                PrintDiffs(changes, root);
                continue;
            }

            if (!applyAll)
            {
                var decision = Ask();
                if (decision == 'q')
                {
                    return new Summary(applied, skipped, notAutomated, nothing, Quit: true);
                }
                if (decision == 'n')
                {
                    output.WriteLine("  → Skipped.");
                    skipped++;
                    continue;
                }
                if (decision == 'a')
                {
                    applyAll = true;
                }

                char Ask()
                {
                    while (true)
                    {
                        var c = prompt.Ask("  Apply this step?",
                            [('y', "es"), ('n', "o, skip"), ('d', "iff"), ('a', "ll remaining"), ('q', "uit")], 'y');
                        if (c != 'd')
                        {
                            return c;
                        }
                        PrintDiffs(changes, root);
                    }
                }
            }

            FileChange.ApplyAll(changes);
            if (git is not null)
            {
                git.Commit(changes.Select(c => c.Path), $"dotnet-lift: {step.Title} ({step.Project})");
                output.WriteLine("  ✓ Applied and committed.");
            }
            else
            {
                output.WriteLine("  ✓ Applied.");
            }
            applied++;
        }

        return new Summary(applied, skipped, notAutomated, nothing, Quit: false);
    }

    private bool CheckGit(string root, Settings settings, out GitRepository? git)
    {
        git = GitRepository.Find(root);
        if (git is null)
        {
            output.WriteLine("No git repository found: changes cannot be committed step by step, and undoing them is up to you.");
            output.WriteLine("Tip: `git init && git add -A && git commit -m \"before upgrade\"` first.");
            if (!settings.Yes && !prompt.Confirm("Continue without git checkpoints?", defaultYes: false))
            {
                return false;
            }
            output.WriteLine();
            return true;
        }

        if (!git.IsClean(out var status))
        {
            output.WriteLine("The working tree has uncommitted changes:");
            foreach (var line in status.Split('\n').Take(10))
            {
                output.WriteLine($"  {line.TrimEnd()}");
            }

            if (!settings.AllowDirty)
            {
                if (settings.Yes)
                {
                    output.WriteLine("Commit or stash them first, or pass --allow-dirty.");
                    return false;
                }
                if (!prompt.Confirm("Continue anyway? Your own changes stay out of the step commits.", defaultYes: false))
                {
                    return false;
                }
            }
        }

        output.WriteLine($"Git repository: {git.Root}");
        output.WriteLine();
        return true;
    }

    private void PrintChanges(IReadOnlyList<FileChange> changes, string root)
    {
        foreach (var change in changes)
        {
            var name = Path.GetRelativePath(root, change.Path).Replace('\\', '/');
            if (change.IsDelete)
            {
                output.WriteLine($"    - {name} (delete)");
            }
            else if (change.IsCreate)
            {
                output.WriteLine($"    + {name} (new)");
            }
            else
            {
                var stats = LineDiff.Count(change.ReadOriginal(), change.NewContent);
                output.WriteLine($"    ~ {name} (+{stats.Added} −{stats.Removed})");
            }
        }
    }

    private void PrintDiffs(IReadOnlyList<FileChange> changes, string root)
    {
        foreach (var change in changes)
        {
            var name = Path.GetRelativePath(root, change.Path).Replace('\\', '/');
            output.WriteLine();
            output.Write(LineDiff.Unified(change.ReadOriginal(), change.NewContent, name));
        }
        output.WriteLine();
    }

    private static ProjectInfo ProjectOf(AnalysisResult analysis, UpgradeStep step) =>
        analysis.Solution.Projects.Single(p => p.Name == step.Project);
}
