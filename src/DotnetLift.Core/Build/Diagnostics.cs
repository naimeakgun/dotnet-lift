using System.Text.RegularExpressions;

namespace DotnetLift.Core.Build;

/// <summary>One error reported by MSBuild, the compiler or NuGet.</summary>
public sealed record Diagnostic(string Code, string Message, string? File, int? Line, string? Project)
{
    /// <summary>Names quoted in the message, e.g. 'ConfigurationManager'.</summary>
    public IReadOnlyList<string> QuotedNames() =>
        DiagnosticParser.Quoted().Matches(Message).Select(m => m.Groups[1].Value).ToList();

    /// <summary>
    /// The identifiers this error is about. For CS0234 ("'X' does not exist in the namespace 'Y'") that is both X and Y.X.
    /// </summary>
    public IReadOnlyList<string> Symbols()
    {
        var names = QuotedNames();
        if (Code == "CS0234" && names.Count >= 2)
        {
            return [names[0], $"{names[1]}.{names[0]}"];
        }
        return names.Count > 0 ? [names[0]] : [];
    }
}

public sealed record BuildResult(bool Succeeded, IReadOnlyList<Diagnostic> Errors, string Output);

public static partial class DiagnosticParser
{
    // path(line,col): error CODE: message [project]   or   path : error CODE: message [project]
    [GeneratedRegex(@"^\s*(?<file>.*?)(?:\((?<line>\d+),\d+(?:,\d+,\d+)?\))?\s*:\s*error\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[(?<proj>[^\]]+)\])?\s*$")]
    private static partial Regex ErrorLine();

    [GeneratedRegex("'([^']+)'")]
    internal static partial Regex Quoted();

    public static IReadOnlyList<Diagnostic> ParseErrors(string output)
    {
        var seen = new HashSet<(string, string, string?, int?)>();
        var result = new List<Diagnostic>();

        foreach (var raw in output.Split('\n'))
        {
            var m = ErrorLine().Match(raw.TrimEnd('\r'));
            if (!m.Success)
            {
                continue;
            }

            var file = m.Groups["file"].Value.Trim();
            int? line = m.Groups["line"].Success ? int.Parse(m.Groups["line"].Value) : null;
            var project = m.Groups["proj"].Success ? m.Groups["proj"].Value.Split("::")[0].Trim() : null;
            var d = new Diagnostic(m.Groups["code"].Value, m.Groups["msg"].Value.Trim(),
                file.Length == 0 ? null : file, line, project);

            // MSBuild prints errors more than once (per target framework, and in the summary).
            if (seen.Add((d.Code, d.Message, d.File, d.Line)))
            {
                result.Add(d);
            }
        }

        return result;
    }
}
