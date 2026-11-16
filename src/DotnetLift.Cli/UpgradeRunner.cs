using DotnetLift.Core.Build;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Cli;

/// <summary>Walks the plan step by step: shows what will change, asks, applies, commits.</summary>
public sealed class UpgradeRunner(Prompt prompt, TextWriter output, IBuilder builder)
{
    public sealed record Settings(bool Yes, bool DryRun, bool AllowDirty);

    public sealed record Summary(int Applied, int Skipped, int NotAutomated, int NothingToDo, bool Quit,
        List<string> BuildsOk, List<string> BuildsWithErrors);

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

        var automatable = plan.Steps.Count(s => IsBuildLoop(s, ProjectOf(analysis, s)) || StepExecutors.For(s, ProjectOf(analysis, s)) is not null);
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
        if (summary.BuildsOk.Count + summary.BuildsWithErrors.Count > 0)
        {
            output.WriteLine($"Builds for {plan.TargetFramework}: " +
                             (summary.BuildsOk.Count > 0 ? $"ok: {string.Join(", ", summary.BuildsOk)}" : "none ok yet") +
                             (summary.BuildsWithErrors.Count > 0 ? $"; errors left: {string.Join(", ", summary.BuildsWithErrors)}" : ""));
        }
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
        var buildsOk = new List<string>();
        var buildsWithErrors = new List<string>();
        Summary Done(bool quit) => new(applied, skipped, notAutomated, nothing, quit, buildsOk, buildsWithErrors);

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

            if (IsBuildLoop(step, project))
            {
                if (settings.DryRun)
                {
                    output.WriteLine("  → Skipped in a dry run: it has to build the project.");
                    continue;
                }

                var tfm = step.Argument ?? plan.TargetFramework;
                var fresh = ProjectLoader.Load(project.FullPath);
                var targets = fresh.TargetFramework?.Split(';', StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
                if (!fresh.IsSdkStyle || !targets.Contains(tfm, StringComparer.OrdinalIgnoreCase))
                {
                    output.WriteLine($"  → Skipped: the project does not target {tfm} yet (the retarget step was not applied).");
                    nothing++;
                    continue;
                }

                var result = new BuildFixLoop(builder).Run(
                    project.FullPath,
                    tfm,
                    fix =>
                    {
                        PrintFix(fix, root);
                        if (applyAll)
                        {
                            return FixDecision.Apply;
                        }
                        switch (AskApply("  Apply this fix?", fix.Changes, root))
                        {
                            case 'q':
                                return FixDecision.Quit;
                            case 'n':
                                skipped++;
                                return FixDecision.Skip;
                            case 'a':
                                applyAll = true;
                                return FixDecision.Apply;
                            default:
                                return FixDecision.Apply;
                        }
                    },
                    fix =>
                    {
                        Commit(git, fix.Changes, $"dotnet-lift: {fix.Title} ({step.Project})");
                        applied++;
                    },
                    message => output.WriteLine($"  {message}"));

                PrintBuildResult(result, project, tfm, root);
                (result.Succeeded ? buildsOk : buildsWithErrors).Add(project.Name);
                if (result.Quit)
                {
                    return Done(quit: true);
                }
                continue;
            }

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
                var decision = AskApply("  Apply this step?", changes, root);
                if (decision == 'q')
                {
                    return Done(quit: true);
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
            }

            FileChange.ApplyAll(changes);
            Commit(git, changes, $"dotnet-lift: {step.Title} ({step.Project})");
            applied++;
        }

        return Done(quit: false);
    }

    private static bool IsBuildLoop(UpgradeStep step, ProjectInfo project) => step.Id == "build" && !project.IsWeb;

    private char AskApply(string question, IReadOnlyList<FileChange> changes, string root)
    {
        while (true)
        {
            var c = prompt.Ask(question,
                [('y', "es"), ('n', "o, skip"), ('d', "iff"), ('a', "ll remaining"), ('q', "uit")], 'y');
            if (c != 'd')
            {
                return c;
            }
            PrintDiffs(changes, root);
        }
    }

    private void Commit(GitRepository? git, IReadOnlyList<FileChange> changes, string message)
    {
        if (git is not null)
        {
            git.Commit(changes.Select(c => c.Path), message);
            output.WriteLine("  ✓ Applied and committed.");
        }
        else
        {
            output.WriteLine("  ✓ Applied.");
        }
    }

    private void PrintFix(BuildFix fix, string root)
    {
        var codes = string.Join(", ", fix.Resolves.GroupBy(d => d.Code).Select(g => $"{g.Key} ×{g.Count()}"));
        output.WriteLine($"  Fix: {fix.Title}  (resolves {codes})");
        output.WriteLine($"  {fix.Explanation}");
        PrintChanges(fix.Changes, root);
    }

    private void PrintBuildResult(BuildLoopResult result, ProjectInfo project, string tfm, string root)
    {
        if (result.Succeeded)
        {
            output.WriteLine($"  ✓ {project.Name} builds for {tfm}.");
            return;
        }

        if (result.BlockedByDependency)
        {
            var blockers = result.Remaining.Select(d => d.Project).Where(p => p is not null)
                .Select(p => Path.GetFileNameWithoutExtension(p!)).Distinct().ToList();
            output.WriteLine($"  ✗ Cannot build for {tfm} yet: {string.Join(", ", blockers)} has errors for {tfm}. Fix those first.");
            return;
        }

        if (result.IterationLimitReached)
        {
            output.WriteLine("  ✗ Stopped after the maximum number of build attempts.");
        }

        output.WriteLine($"  ✗ {result.Remaining.Count} error(s) left that need a code change:");
        foreach (var group in result.Remaining.GroupBy(d => BuildFixCatalog.Explain(d)?.Summary ?? $"{d.Code}"))
        {
            var guidance = BuildFixCatalog.Explain(group.First());
            output.WriteLine($"    • {(guidance?.Summary ?? $"{group.Key}: {Shorten(group.First().Message)}")}");
            if (guidance is not null)
            {
                output.WriteLine($"      → {guidance.Suggestion}");
            }
            foreach (var d in group.Take(5))
            {
                var where = d.File is null ? "" : Path.GetRelativePath(root, d.File).Replace('\\', '/') + (d.Line is null ? "" : $":{d.Line}");
                output.WriteLine($"      {where}  {d.Code}  {Shorten(d.Message)}");
            }
            if (group.Count() > 5)
            {
                output.WriteLine($"      ... and {group.Count() - 5} more");
            }
        }
    }

    private static string Shorten(string message) => message.Length <= 110 ? message : message[..107] + "...";

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
