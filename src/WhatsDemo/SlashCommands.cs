namespace WhatsDemo;

/// <summary>Slash-command catalog and prefix filter used by the completion popup.</summary>
public static class SlashCommands
{
    public static readonly IReadOnlyList<string> Names =
    [
        "logout",
        "disconnect",
        "connect",
        "subscribe",
        "unsubscribe",
        "directory",
    ];

    /// <summary>
    /// Commands whose name starts with the token after <c>/</c>.
    /// Empty once a space is typed (command token is complete).
    /// </summary>
    public static IReadOnlyList<string> Complete(string input)
    {
        if (input.Length == 0 || input[0] != '/')
            return [];

        var rest = input.AsSpan(1);
        if (rest.Contains(' '))
            return [];

        var prefix = rest.ToString();
        return [.. Names.Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))];
    }

    public static bool TryParse(string line, out string name, out string argument)
    {
        name = "";
        argument = "";
        var trimmed = line.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '/')
            return false;

        var rest = trimmed.AsSpan(1).TrimStart();
        if (rest.IsEmpty)
            return false;

        var space = rest.IndexOf(' ');
        var token = (space < 0 ? rest : rest[..space]).ToString();
        argument = space < 0 ? "" : rest[(space + 1)..].Trim().ToString();

        var matches = Complete("/" + token);
        if (matches.Count == 1)
        {
            name = matches[0];
            return true;
        }

        foreach (var candidate in Names)
        {
            if (candidate.Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                name = candidate;
                return true;
            }
        }

        return false;
    }

    public static bool TakesArgument(string name)
        => name is "subscribe" or "unsubscribe" or "directory";

    /// <summary>Buffer after picking <paramref name="name"/> from the popup. Argument commands keep a trailing space.</summary>
    public static string CompletedInput(string name)
        => TakesArgument(name) ? $"/{name} " : $"/{name}";

    /// <summary>
    /// True when the line is a known argument-taking command with no argument yet.
    /// The editor should keep reading instead of committing.
    /// </summary>
    public static bool IsPendingArgument(string input)
        => TryParse(input, out var name, out var argument)
           && TakesArgument(name)
           && string.IsNullOrWhiteSpace(argument);
}
