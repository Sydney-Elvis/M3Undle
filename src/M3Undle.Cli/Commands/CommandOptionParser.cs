namespace M3Undle.Cli.Commands;

public static class CommandOptionParser
{
    public const string FlagPresentValue = "__flag_present__";

    public static CommandOptionSet Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        while (index < args.Count)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new CommandOptionException($"Unexpected token '{token}'. Use --option value syntax.");
            }

            var trimmed = token[2..];
            string key;
            string? value = null;

            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex >= 0)
            {
                key = trimmed[..eqIndex];
                value = trimmed[(eqIndex + 1)..];
            }
            else
            {
                key = trimmed;
                var nextIndex = index + 1;
                if (nextIndex < args.Count && !args[nextIndex].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[nextIndex];
                    index++; // consume value
                }
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                throw new CommandOptionException("Encountered malformed option with empty name.");
            }

            if (!values.TryGetValue(key, out var list))
            {
                list = new List<string>();
                values[key] = list;
            }

            list.Add(value ?? FlagPresentValue);
            index++;
        }

        return new CommandOptionSet(values);
    }
}

