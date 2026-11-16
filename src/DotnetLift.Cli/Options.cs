namespace DotnetLift.Cli;

public sealed class Options
{
    public string Command { get; private set; } = "help";
    public string Path { get; private set; } = ".";
    public string? Target { get; private set; }
    public string? Output { get; private set; }
    public bool Yes { get; private set; }
    public bool NoReport { get; private set; }
    public bool DryRun { get; private set; }
    public bool AllowDirty { get; private set; }
    public bool AcceptRisky { get; private set; }

    public static readonly string[] SupportedTargets = ["net10.0", "net11.0"];

    /// <summary>Tiny hand-written parser: the CLI surface is small and this keeps the tool dependency-free.</summary>
    public static Options Parse(string[] args)
    {
        var o = new Options();
        var positional = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-h" or "--help":
                    o.Command = "help";
                    return o;
                case "--version":
                    o.Command = "version";
                    return o;
                case "-t" or "--target":
                    o.Target = Next(args, ref i, a);
                    break;
                case "-o" or "--output":
                    o.Output = Next(args, ref i, a);
                    break;
                case "-y" or "--yes":
                    o.Yes = true;
                    break;
                case "--no-report":
                    o.NoReport = true;
                    break;
                case "--dry-run":
                    o.DryRun = true;
                    break;
                case "--allow-dirty":
                    o.AllowDirty = true;
                    break;
                case "--accept-risky":
                    o.AcceptRisky = true;
                    break;
                default:
                    if (a.StartsWith('-'))
                    {
                        throw new ArgumentException($"Unknown option '{a}'.");
                    }
                    positional.Add(a);
                    break;
            }
        }

        if (positional.Count > 0)
        {
            o.Command = positional[0];
        }
        if (positional.Count > 1)
        {
            o.Path = positional[1];
        }

        if (o.Target is not null && !SupportedTargets.Contains(o.Target))
        {
            throw new ArgumentException($"Unsupported target '{o.Target}'. Use one of: {string.Join(", ", SupportedTargets)}.");
        }

        return o;
    }

    private static string Next(string[] args, ref int i, string option)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"Option '{option}' needs a value.");
        }
        return args[++i];
    }
}
