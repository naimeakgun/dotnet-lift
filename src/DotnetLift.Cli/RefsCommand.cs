using DotnetLift.Core.Loading;
using DotnetLift.Core.Upgrade;

namespace DotnetLift.Cli;

/// <summary>`dotnet-lift refs`: DLL references by file path (HintPath outside packages/) → PackageReference.</summary>
public sealed class RefsCommand(Prompt prompt, TextWriter output)
{
    public int Run(Options options)
    {
        output.WriteLine("dotnet-lift · refs (DLL references → packages)");
        output.WriteLine();

        var solution = SolutionLoader.Load(options.Path);
        var root = File.Exists(solution.Path) ? Path.GetDirectoryName(solution.Path)! : solution.Path;
        var mapPath = options.Map is null ? Path.Combine(root, PackageMap.FileName) : Path.GetFullPath(options.Map);
        var references = solution.Projects.SelectMany(p => p.FileReferences).ToList();

        if (references.Count == 0)
        {
            output.WriteLine("No DLL references outside packages/ found.");
            return 0;
        }

        if (options.InitMap)
        {
            if (File.Exists(mapPath))
            {
                output.WriteLine($"{Rel(root, mapPath)} already exists; edit it instead.");
                return 1;
            }
            File.WriteAllText(mapPath, PackageMap.Template(references));
            output.WriteLine($"Wrote {Rel(root, mapPath)} with {references.Select(r => r.AssemblyName).Distinct(StringComparer.OrdinalIgnoreCase).Count()} DLL(s).");
            output.WriteLine("Check each package id and version against your feed (use \"keep\" for DLLs that have no package), then run `dotnet-lift refs`.");
            return 0;
        }

        if (options.Map is not null && !File.Exists(mapPath))
        {
            output.WriteLine($"error: {mapPath} does not exist.");
            return 1;
        }

        var map = File.Exists(mapPath) ? PackageMap.Load(mapPath) : PackageMap.Empty;
        var plan = FileReferenceConversion.Plan(solution, map, options.ByName);

        if (plan.Converted.Count > 0)
        {
            output.WriteLine("Becomes a package reference:");
            foreach (var line in plan.Converted)
            {
                output.WriteLine($"  ✓ {line}");
            }
        }

        if (plan.Unmapped.Count > 0)
        {
            output.WriteLine("Left as file references (no package known):");
            foreach (var line in plan.Unmapped)
            {
                output.WriteLine($"  • {line}");
            }
            output.WriteLine($"  → Map them in {PackageMap.FileName} (`dotnet-lift refs --init-map` writes a starting point), or use --by-name.");
        }

        foreach (var note in plan.Notes)
        {
            output.WriteLine($"ℹ {note}");
        }
        output.WriteLine();

        if (plan.Changes.Count == 0)
        {
            output.WriteLine("Nothing to change.");
            return 0;
        }

        foreach (var change in plan.Changes)
        {
            var stats = LineDiff.Count(change.ReadOriginal(), change.NewContent);
            output.WriteLine(change.IsCreate ? $"    + {Rel(root, change.Path)} (new)" : $"    ~ {Rel(root, change.Path)} (+{stats.Added} −{stats.Removed})");
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

        if (!options.Yes && !prompt.Confirm("Apply these changes?", defaultYes: true))
        {
            output.WriteLine("Nothing was changed.");
            return 0;
        }

        FileChange.ApplyAll(plan.Changes);
        if (git is not null)
        {
            git.Commit(plan.Changes.SelectMany(c => c.Paths), "dotnet-lift: Reference DLLs as NuGet packages");
            output.WriteLine("✓ Applied and committed.");
        }
        else
        {
            output.WriteLine("✓ Applied.");
        }
        output.WriteLine("Run `dotnet restore` to check: NU1101 means a package id is not on your feeds, NU1102 that the version is not; fix the map and run again.");
        return 0;
    }

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
