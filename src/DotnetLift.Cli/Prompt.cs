namespace DotnetLift.Cli;

/// <summary>Question/answer helpers. When not interactive every question takes its default answer.</summary>
public sealed class Prompt(TextReader input, TextWriter output, bool interactive)
{
    public string Choose(string question, IReadOnlyList<(string Value, string Label)> options, int defaultIndex = 0)
    {
        if (!interactive)
        {
            return options[defaultIndex].Value;
        }

        output.WriteLine(question);
        for (var i = 0; i < options.Count; i++)
        {
            output.WriteLine($"  {i + 1}) {options[i].Label}");
        }

        while (true)
        {
            output.Write($"Choose [1-{options.Count}] (default {defaultIndex + 1}): ");
            var answer = input.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(answer))
            {
                return options[defaultIndex].Value;
            }
            if (int.TryParse(answer, out var n) && n >= 1 && n <= options.Count)
            {
                return options[n - 1].Value;
            }
            var byValue = options.FirstOrDefault(o => o.Value.Equals(answer, StringComparison.OrdinalIgnoreCase));
            if (byValue.Value is not null)
            {
                return byValue.Value;
            }
            output.WriteLine("Please enter one of the numbers above.");
        }
    }

    /// <summary>
    /// One-letter menu, e.g. "[Y]es / [n]o / [q]uit". Each choice is its key plus the rest of the word.
    /// </summary>
    public char Ask(string question, IReadOnlyList<(char Key, string Rest)> choices, char defaultKey)
    {
        if (!interactive)
        {
            return defaultKey;
        }

        var menu = string.Join(" / ", choices.Select(c =>
            $"[{(c.Key == defaultKey ? char.ToUpperInvariant(c.Key) : c.Key)}]{c.Rest}"));

        while (true)
        {
            output.Write($"{question} {menu}: ");
            var answer = input.ReadLine();
            if (answer is null)
            {
                return defaultKey; // input closed: behave like pressing Enter
            }

            answer = answer.Trim().ToLowerInvariant();
            if (answer.Length == 0)
            {
                return defaultKey;
            }

            var key = answer[0];
            if (choices.Any(c => c.Key == key))
            {
                return key;
            }
            output.WriteLine($"Please answer one of: {string.Join(", ", choices.Select(c => c.Key))}.");
        }
    }

    public bool Confirm(string question, bool defaultYes)
    {
        if (!interactive)
        {
            return defaultYes;
        }

        while (true)
        {
            output.Write($"{question} [{(defaultYes ? "Y/n" : "y/N")}]: ");
            var answer = input.ReadLine()?.Trim().ToLowerInvariant();
            switch (answer)
            {
                case null or "":
                    return defaultYes;
                case "y" or "yes" or "e" or "evet":
                    return true;
                case "n" or "no" or "h" or "hayir" or "hayır":
                    return false;
                default:
                    output.WriteLine("Please answer y or n.");
                    break;
            }
        }
    }
}
