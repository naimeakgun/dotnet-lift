using DotnetLift.Core.Build;
using DotnetLift.Core.Loading;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Cli;

/// <summary>Walks the plan step by step: shows what will change, asks, applies, commits.</summary>
public sealed class UpgradeRunner(Prompt prompt, TextWriter output, IBuilder builder)
{
    public sealed record Settings(bool Yes, bool DryRun, bool AllowDirty, bool AcceptRisky = false, bool SingleCommit = false);

    // With SingleCommit, applied changes are collected here and committed once at the end.
    private readonly List<string> _pendingPaths = [];
    private int _pendingSteps;
    private bool _singleCommit;

    public sealed record Summary(int Applied, int Skipped, int NotAutomated, int NothingToDo, int Waiting, bool Quit,
        List<string> BuildsOk, List<string> BuildsWithErrors, List<string> TestsOk, List<string> TestsFailing);

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

        var automatable = plan.Steps.Count(s => IsBuildLoop(s, ProjectOf(analysis, s))
            || StepExecutors.For(s, ProjectOf(analysis, s)) is { } e && e.NotReadyReason(ProjectOf(analysis, s)) is null);
        output.WriteLine($"{plan.Steps.Count} step(s), {automatable} can be done automatically right now.");
        if (settings.DryRun)
        {
            output.WriteLine("Dry run: nothing will be written.");
        }
        output.WriteLine();

        _singleCommit = settings.SingleCommit;
        var summary = Walk(analysis, plan, settings, root, git);
        if (_singleCommit && git is not null && _pendingSteps > 0)
        {
            var message = $"dotnet-lift: upgrade to {plan.TargetFramework} ({_pendingSteps} step{(_pendingSteps == 1 ? "" : "s")})";
            output.WriteLine();
            output.WriteLine(git.Commit(_pendingPaths.Distinct(StringComparer.OrdinalIgnoreCase), message)
                ? $"✓ Committed all changes as one commit: \"{message}\"."
                : "Nothing to commit: git ignores every file the steps touched.");
        }

        output.WriteLine();
        output.WriteLine(summary.Quit ? "Stopped." : "Done.");
        output.WriteLine($"Applied {summary.Applied}, skipped {summary.Skipped}, nothing to change {summary.NothingToDo}, " +
                         $"not automated yet {summary.NotAutomated}" +
                         (summary.Waiting > 0 ? $", waiting for an earlier step {summary.Waiting}." : "."));
        if (summary.BuildsOk.Count + summary.BuildsWithErrors.Count > 0)
        {
            output.WriteLine($"Builds for {plan.TargetFramework}: " +
                             (summary.BuildsOk.Count > 0 ? $"ok: {string.Join(", ", summary.BuildsOk)}" : "none ok yet") +
                             (summary.BuildsWithErrors.Count > 0 ? $"; errors left: {string.Join(", ", summary.BuildsWithErrors)}" : ""));
        }
        if (summary.TestsOk.Count + summary.TestsFailing.Count > 0)
        {
            output.WriteLine($"Tests on {plan.TargetFramework}: " +
                             (summary.TestsOk.Count > 0 ? $"passing: {string.Join(", ", summary.TestsOk)}" : "none passing yet") +
                             (summary.TestsFailing.Count > 0 ? $"; failing: {string.Join(", ", summary.TestsFailing)}" : ""));
        }
        if (summary.Applied > 0 && git is not null && !_singleCommit)
        {
            output.WriteLine("Each applied step is a separate commit: `git log --oneline` shows them, `git revert <sha>` undoes one.");
        }
        output.WriteLine("Run `dotnet-lift analyze` again to see what is left.");
        return 0;
    }

    private Summary Walk(AnalysisResult analysis, UpgradePlan plan, Settings settings, string root, GitRepository? git)
    {
        int applied = 0, skipped = 0, notAutomated = 0, nothing = 0, waiting = 0;
        var applyAll = settings.Yes;
        var buildsOk = new List<string>();
        var buildsWithErrors = new List<string>();
        var testsOk = new List<string>();
        var testsFailing = new List<string>();
        Summary Done(bool quit) => new(applied, skipped, notAutomated, nothing, waiting, quit, buildsOk, buildsWithErrors, testsOk, testsFailing);

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

                // A test project that builds proves nothing yet: run its tests on the new target.
                if (result.Succeeded && fresh.IsTest && builder is ITestRunner tester)
                {
                    output.WriteLine($"  Running tests for {tfm}...");
                    var tests = tester.Test(project.FullPath, tfm);
                    PrintTestResult(tests, project, tfm);
                    (tests.Succeeded ? testsOk : testsFailing).Add(project.Name);
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

            if (executor.NotReadyReason(project) is { } reason)
            {
                output.WriteLine($"  → Not yet: {reason}");
                waiting++;
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
            if (executor.RequiresExplicitApproval)
            {
                output.WriteLine($"  ⚠ {executor.ApprovalNote}");
            }

            if (settings.DryRun)
            {
                PrintDiffs(changes, root);
                continue;
            }

            // Behaviour-changing steps: never applied by --yes alone, and the question defaults to "no".
            var mustConfirm = executor.RequiresExplicitApproval && !settings.AcceptRisky;
            if (settings.Yes && mustConfirm)
            {
                output.WriteLine("  → Not applied: this step needs your explicit approval. Run without --yes to decide, or add --accept-risky.");
                skipped++;
                continue;
            }

            if (!applyAll || mustConfirm)
            {
                var decision = AskApply("  Apply this step?", changes, root, mustConfirm ? 'n' : 'y');
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
                if (decision == 'a' && !mustConfirm)
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

    private char AskApply(string question, IReadOnlyList<FileChange> changes, string root, char defaultKey = 'y')
    {
        while (true)
        {
            var c = prompt.Ask(question,
                [('y', "es"), ('n', "o, skip"), ('d', "iff"), ('a', "ll remaining"), ('q', "uit")], defaultKey);
            if (c != 'd')
            {
                return c;
            }
            PrintDiffs(changes, root);
        }
    }

    private void Commit(GitRepository? git, IReadOnlyList<FileChange> changes, string message)
    {
        if (git is not null && _singleCommit)
        {
            _pendingPaths.AddRange(changes.SelectMany(c => c.Paths));
            _pendingSteps++;
            output.WriteLine("  ✓ Applied (committed with the others at the end).");
            return;
        }

        if (git is not null)
        {
            output.WriteLine(git.Commit(changes.SelectMany(c => c.Paths), message)
                ? "  ✓ Applied and committed."
                : "  ✓ Applied (not committed: git ignores every file this step touched).");
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

    private void PrintTestResult(TestRunResult tests, ProjectInfo project, string tfm)
    {
        var counts = $"{tests.Passed} passed, {tests.Failed} failed, {tests.Skipped} skipped";
        if (tests.Succeeded)
        {
            output.WriteLine($"  ✓ {project.Name}: tests pass on {tfm} ({counts}).");
            return;
        }

        output.WriteLine($"  ✗ {project.Name}: tests fail on {tfm} ({counts}).");
        foreach (var name in tests.FailedTests.Take(10))
        {
            output.WriteLine($"      {name}");
        }
        if (tests.FailedTests.Count > 10)
        {
            output.WriteLine($"      ... and {tests.FailedTests.Count - 10} more");
        }
        output.WriteLine($"      → Run `dotnet test {Path.GetFileName(project.FullPath)} -f {tfm}` for the details; behaviour that differs between frameworks shows up here.");
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
        const int maxMovesListed = 10;
        var moves = changes.Count(c => c.IsMove);
        var movesListed = 0;
        foreach (var change in changes)
        {
            var name = Path.GetRelativePath(root, change.Path).Replace('\\', '/');
            if (change.MovedFrom is { } source)
            {
                if (++movesListed <= maxMovesListed)
                {
                    output.WriteLine($"    > {Path.GetRelativePath(root, source).Replace('\\', '/')} → {name} (move)");
                }
                else if (movesListed == maxMovesListed + 1)
                {
                    output.WriteLine($"    > ... and {moves - maxMovesListed} more file(s) moved (d shows all)");
                }
            }
            else if (change.IsDelete)
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
            if (change.MovedFrom is { } source)
            {
                // Content is unchanged (and may be binary), so a move shows as a rename, like git does.
                output.WriteLine($"rename {Path.GetRelativePath(root, source).Replace('\\', '/')} → {name}");
                continue;
            }
            output.WriteLine();
            output.Write(LineDiff.Unified(change.ReadOriginal(), change.NewContent, name));
        }
        output.WriteLine();
    }

    private static ProjectInfo ProjectOf(AnalysisResult analysis, UpgradeStep step) =>
        analysis.Solution.Projects.Single(p => p.Name == step.Project);
}
