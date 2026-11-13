using System.Reflection;
using DotnetLift.Core;
using DotnetLift.Core.Model;
using DotnetLift.Core.Planning;
using DotnetLift.Core.Reporting;

namespace DotnetLift.Cli;

/// <summary>The CLI, with reader/writer injected so the interactive flow is testable.</summary>
public sealed class App(TextReader input, TextWriter output, bool interactive)
{
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

        var target = options.Target ?? (options.Yes
            ? SolutionAnalyzer.DefaultTarget
            : _prompt.Choose("Which .NET version do you want to move to?",
                [
                    ("net10.0", ".NET 10 (LTS, supported until Nov 2028) - recommended"),
                    ("net11.0", ".NET 11 (STS, supported until Nov 2028)"),
                ]));

        var analysis = new SolutionAnalyzer().Analyze(options.Path, target);
        if (analysis.Solution.Projects.Count == 0)
        {
            _out.WriteLine("No C# projects found.");
            return 1;
        }

        var plan = UpgradePlanner.Create(analysis);
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
        _out.WriteLine("Next: `dotnet-lift upgrade` will walk through these steps one by one (coming in v0.2).");
        return 0;
    }

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
            _out.WriteLine($"  {i++}. {p.Name,-28} {MarkdownReport.Describe(p.Kind),-24} {p.TargetFramework,-8} " +
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

        var blockers = analysis.Findings.Where(f => f.Severity == Severity.Blocker).ToList();
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
              dotnet-lift analyze [path] [options]

            Arguments:
              path                 A .sln, a .csproj or a folder (default: current folder)

            Options:
              -t, --target <tfm>   net10.0 (default) or net11.0. Asked interactively if omitted.
              -o, --output <file>  Where to write the Markdown report (default: lift-report.md next to the solution)
                  --no-report      Print the summary only
              -y, --yes            Do not ask questions; use defaults
              -h, --help           Show this help
                  --version        Show the version
            """);
        return 0;
    }
}
