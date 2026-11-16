using DotnetLift.Core.Loading;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Core.Build;

public enum FixDecision
{
    Apply,
    Skip,
    Quit,
}

public sealed record BuildLoopResult(
    bool Succeeded,
    int FixesApplied,
    IReadOnlyList<Diagnostic> Remaining,
    bool BlockedByDependency = false,
    bool Quit = false,
    bool IterationLimitReached = false);

/// <summary>
/// build → read errors → apply known fixes → build again, until the project builds,
/// no known fix is left, the user stops, or the iteration limit is reached.
/// </summary>
public sealed class BuildFixLoop(IBuilder builder, int maxIterations = 5)
{
    public BuildLoopResult Run(
        string projectPath,
        string targetFramework,
        Func<BuildFix, FixDecision> decide,
        Action<BuildFix> onApplied,
        Action<string> log)
    {
        var applied = 0;
        IReadOnlyList<Diagnostic> last = [];

        for (var attempt = 1; attempt <= maxIterations; attempt++)
        {
            log(attempt == 1 ? $"Building for {targetFramework}..." : $"Building again (attempt {attempt})...");
            var result = builder.Build(projectPath, targetFramework);
            if (result.Succeeded)
            {
                return new BuildLoopResult(true, applied, []);
            }

            if (result.Errors.Count == 0)
            {
                // The build failed but printed nothing we recognise; surface the tail of the output instead of guessing.
                var tail = string.Join(" ", result.Output.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(3).Select(l => l.Trim()));
                return new BuildLoopResult(false, applied, [new Diagnostic("BUILD", tail.Length > 0 ? tail : "Build failed.", null, null, projectPath)]);
            }

            var own = result.Errors.Where(e => BelongsTo(e, projectPath)).ToList();
            var others = result.Errors.Except(own).ToList();
            if (others.Count > 0)
            {
                return new BuildLoopResult(false, applied, others, BlockedByDependency: true);
            }

            last = own;
            var project = ProjectLoader.Load(projectPath);
            var fixes = BuildFixCatalog.Fixes(own, project, targetFramework);
            if (fixes.Count == 0)
            {
                return new BuildLoopResult(false, applied, own);
            }

            var appliedThisRound = false;
            foreach (var fix in fixes)
            {
                switch (decide(fix))
                {
                    case FixDecision.Quit:
                        return new BuildLoopResult(false, applied, own, Quit: true);
                    case FixDecision.Apply:
                        FileChange.ApplyAll(fix.Changes);
                        onApplied(fix);
                        applied++;
                        appliedThisRound = true;
                        break;
                }
            }

            if (!appliedThisRound)
            {
                return new BuildLoopResult(false, applied, own);
            }
        }

        return new BuildLoopResult(false, applied, last, IterationLimitReached: true);
    }

    private static bool BelongsTo(Diagnostic d, string projectPath)
    {
        if (d.Project is not null)
        {
            return SamePath(d.Project, projectPath);
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath))! + Path.DirectorySeparatorChar;
        return d.File is null || Path.GetFullPath(d.File).StartsWith(dir, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
