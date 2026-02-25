namespace M3Undle.Cli.Commands;

public sealed class CommandOptionSet
{
    private readonly Dictionary<string, List<string>> _values;

    public CommandOptionSet(Dictionary<string, List<string>> values)
    {
        _values = values;
    }

    public IEnumerable<string> Keys => _values.Keys;

    public bool IsFlagSet(string name)
        => _values.TryGetValue(name, out var list) && list.Count > 0 && list.Last() == CommandOptionParser.FlagPresentValue;

    public string? GetSingleValue(string name)
    {
        if (!_values.TryGetValue(name, out var list) || list.Count == 0)
        {
            return null;
        }

        var value = list.Last();
        return value == CommandOptionParser.FlagPresentValue ? "true" : value;
    }

    public IEnumerable<string> GetValues(string name)
    {
        if (_values.TryGetValue(name, out var list))
        {
            return list.Select(v => v == CommandOptionParser.FlagPresentValue ? "true" : v);
        }
        return Array.Empty<string>();
    }
}

