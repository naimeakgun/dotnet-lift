using System.Reflection;
using DotnetLift.Core;
using DotnetLift.Core.Build;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Reporting;

namespace DotnetLift.Cli;

/// <summary>The CLI, with reader/writer injected so the interactive flow is testable.</summary>
public sealed class App(TextReader input, TextWriter output, bool interactive, IBuilder? builder = null)
{
    private readonly IBuilder _builder = builder ?? new DotnetBuilder();
    private readonly TextWriter _out = output;
    private readonly Prompt _prompt = new(input, output, interactive);

    public async Task<int> RunAsync(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            _out.WriteLine($"error: {ex.Message}");
            _out.WriteLine();
            PrintHelp();
            return 2;
        }

        try
        {
            return options.Command switch
            {
                "analyze" => await AnalyzeAsync(options),
                "upgrade" => Upgrade(options),
                "cpm" => new CpmCommand(_prompt, _out).Run(options),
                "init" => Init(options),
                "refs" => new RefsCommand(_prompt, _out).Run(options),
                "cleanup" => new CleanupCommand(_prompt, _out).Run(options),
                "add-tests" => new AddTestsCommand(_prompt, _out).Run(options, LoadProfile(options)),
                "version" => PrintVersion(),
                "help" => PrintHelp(),
                _ => Unknown(options.Command),
            };
        }
        catch (Exception ex) when (ex is FileNotFoundException or NotSupportedException or InvalidOperationException or InvalidDataException)
        {
            _out.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private async Task<int> AnalyzeAsync(Options options)
    {
        _out.WriteLine("dotnet-lift · analyze");
        _out.WriteLine();

        var profile = LoadProfile(options);
        var target = AskTarget(options, profile);
        var analysis = new SolutionAnalyzer().Analyze(options.Path, target);
        if (analysis.Solution.Projects.Count == 0)
        {
            _out.WriteLine("No C# projects found.");
            return 1;
        }

        var plan = UpgradePlanner.Create(analysis, profile);
        PrintSummary(analysis, plan);

        if (options.NoReport)
        {
            return 0;
        }

        var reportPath = options.Output ?? Path.Combine(RootOf(analysis), "lift-report.md");
        if (options.Yes || options.Output is not null
            || _prompt.Confirm($"Write the full report to {Path.GetFileName(reportPath)}?", defaultYes: true))
        {
            await File.WriteAllTextAsync(reportPath, MarkdownReport.Render(analysis, plan));
            _out.WriteLine($"Report written to {reportPath}");
        }

        _out.WriteLine();
        _out.WriteLine("Next: `dotnet-lift upgrade` walks through these steps one by one.");
        return 0;
    }

    private int Upgrade(Options options)
    {
        _out.WriteLine("dotnet-lift · upgrade");
        _out.WriteLine();

        var profile = LoadProfile(options);
        var target = AskTarget(options, profile);
        var analysis = new SolutionAnalyzer().Analyze(options.Path, target);
        if (analysis.Solution.Projects.Count == 0)
        {
            _out.WriteLine("No C# projects found.");
            return 1;
        }

        _out.WriteLine($"Target: {target}");
        var plan = UpgradePlanner.Create(analysis, profile);
        var settings = new UpgradeRunner.Settings(options.Yes, options.DryRun, options.AllowDirty, options.AcceptRisky,
            SingleCommit: profile.Commits == CommitMode.Single);
        return new UpgradeRunner(_prompt, _out, _builder).Run(analysis, plan, settings);
    }

    /// <summary>--profile, else the nearest dotnet-lift.json at or above the input, else the defaults.</summary>
    private MigrationProfile LoadProfile(Options options)
    {
        var profile = options.Profile is not null ? MigrationProfile.Load(options.Profile) : MigrationProfile.Find(options.Path);
        if (!profile.IsDefault)
        {
            _out.WriteLine($"Profile: {profile.Path} ({profile.Describe()})");
            _out.WriteLine();
        }
        return profile;
    }

    private int Init(Options options)
    {
        var full = Path.GetFullPath(options.Path);
        var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;
        var path = Path.Combine(dir, MigrationProfile.FileName);
        if (File.Exists(path))
        {
            _out.WriteLine($"{path} already exists.");
            return 1;
        }
        File.WriteAllText(path, MigrationProfile.Template());
        _out.WriteLine($"Wrote {path}. Edit it with the team's decisions and commit it; analyze and upgrade read it from then on.");
        return 0;
    }

    private string AskTarget(Options options, MigrationProfile profile) =>
        options.Target ?? profile.Target ?? (options.Yes
            ? SolutionAnalyzer.DefaultTarget
            : _prompt.Choose("Which .NET version do you want to move to?",
                [
                    ("net10.0", ".NET 10 (LTS, supported until Nov 2028) - recommended"),
                    ("net11.0", ".NET 11 (STS, supported until Nov 2028)"),
                ]));

    private void PrintSummary(AnalysisResult analysis, UpgradePlan plan)
    {
        _out.WriteLine();
        _out.WriteLine($"Target: {analysis.TargetFramework}");
        _out.WriteLine();
        _out.WriteLine("Projects (upgrade order):");
        var i = 1;
        foreach (var p in plan.Order)
        {
            var b = analysis.Count(p, Severity.Blocker);
            var w = analysis.Count(p, Severity.Warning);
            _out.WriteLine($"  {i++}. {p.Name,-28} {MarkdownReport.Describe(p),-24} {p.TargetFramework,-8} " +
                             $"{b} blocker(s), {w} warning(s)");
        }

        _out.WriteLine();
        _out.WriteLine("Plan:");
        foreach (var s in plan.Steps)
        {
            var mode = s.Mode switch
            {
                StepMode.Automatic => "auto ",
                StepMode.AgentAssisted => "agent",
                _ => "human",
            };
            _out.WriteLine($"  {s.Number,2}. [{mode}] [{s.Risk,-6}] {s.Project}: {s.Title}");
        }

        var order = plan.Order.Select(p => p.Name).ToList();
        var blockers = analysis.Findings
            .Where(f => f.Severity == Severity.Blocker)
            .OrderBy(f => order.IndexOf(f.Project))
            .ToList();
        if (blockers.Count > 0)
        {
            _out.WriteLine();
            _out.WriteLine("Blockers:");
            foreach (var f in blockers)
            {
                var where = f.File is null ? "" : f.Line is null ? $" ({f.File})" : $" ({f.File}:{f.Line})";
                _out.WriteLine($"  - {f.Project}{where}: {f.Message}");
            }
        }
        _out.WriteLine();
    }

    private static string RootOf(AnalysisResult a) =>
        File.Exists(a.Solution.Path) ? Path.GetDirectoryName(a.Solution.Path)! : a.Solution.Path;

    private int Unknown(string command)
    {
        _out.WriteLine($"error: unknown command '{command}'.");
        _out.WriteLine();
        PrintHelp();
        return 2;
    }

    private int PrintVersion()
    {
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        _out.WriteLine($"dotnet-lift {version.Split('+')[0]}");
        return 0;
    }

    private int PrintHelp()
    {
        _out.WriteLine("""
            dotnet-lift - upgrade .NET Framework solutions to modern .NET, step by step

            Usage:
              dotnet-lift analyze [path] [options]   Report what has to change and build the plan
              dotnet-lift upgrade [path] [options]   Walk through the plan and apply the steps
              dotnet-lift init [path]                Write a dotnet-lift.json migration profile to fill in
              dotnet-lift cpm [path] [options]       Move package versions into Directory.Packages.props
              dotnet-lift refs [path] [options]      Turn DLL file references (HintPath) into PackageReferences
              dotnet-lift cleanup [path] [options]   Remove unused legacy references and Visual Studio migration backups
              dotnet-lift add-tests [path] -p <name> Add an MSTest project for a library (test-first migration)

            Arguments:
              path                 A .sln, a .csproj or a folder (default: current folder)

            Options:
              -t, --target <tfm>   net10.0 (default) or net11.0. Taken from the profile, else asked interactively.
                  --profile <file> Migration profile (default: the nearest dotnet-lift.json at or above the path)
              -o, --output <file>  Where to write the Markdown report (default: lift-report.md next to the solution)
                  --no-report      Print the summary only
              -y, --yes            Do not ask questions; use defaults (upgrade: apply every automatic step)
                  --dry-run        upgrade, cpm, refs, cleanup, add-tests: show what would change, write nothing
                  --allow-dirty    upgrade, cpm, refs, cleanup, add-tests: run even if the git working tree has uncommitted changes
                  --map <file>     refs: assembly → package map (default: dotnet-lift.packages.json next to the solution)
                  --init-map       refs: write a starting map from the DLLs found, then stop
                  --by-name        refs: assume package id = assembly name for DLLs the map does not mention
                  --remove-project <name>
                                   cleanup: remove that project from the .sln instead, after listing what depends
                                   on it (refused while another project still references it)
              -p, --project <name> add-tests: the project to write tests for; the test project is <name>.Tests
                  --accept-risky   upgrade: also apply steps that change behaviour (e.g. a serialization format)
                                   without asking; without it they always ask, and --yes skips them
              -h, --help           Show this help
                  --version        Show the version
            """);
        return 0;
    }
}
