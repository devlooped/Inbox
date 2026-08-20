namespace WhatsDemo;

/// <summary>Dispatches the REPL completion popup for <c>/</c> commands and <c>@</c> mentions.</summary>
public static class Completions
{
    public static IReadOnlyList<CompletionItem> Slash(string input)
        => [.. SlashCommands.Complete(input).Select(name => new CompletionItem(SlashCommands.CompletedInput(name), name))];

    public static IReadOnlyList<CompletionItem> Complete(
        string input,
        IReadOnlyList<string> topics,
        DemoSession session,
        RecentChats recents)
    {
        if (input.Length == 0)
            return [];
        if (input[0] == '/')
        {
            var commands = Slash(input);
            return commands.Count > 0 ? commands : Unsubscribe(input, topics, session);
        }

        if (input[0] == '@')
            return AtMentions.Complete(input, topics, session, recents);
        return [];
    }

    /// <summary>
    /// After <c>/unsubscribe</c>, list subscribed chats (not self) as the next popup.
    /// </summary>
    public static IReadOnlyList<CompletionItem> Unsubscribe(
        string input,
        IReadOnlyList<string> topics,
        DemoSession session)
    {
        if (!SlashCommands.TryParse(input, out var name, out var argument) || name is not "unsubscribe")
            return [];

        var items = new List<CompletionItem>();
        foreach (var topic in topics)
        {
            if (IsSelfChat(topic, session))
                continue;
            var label = AtMentions.MentionLabel(topic, session.AuthorLabel(topic));
            items.Add(new CompletionItem($"/unsubscribe {topic}", label));
        }

        return Filter(items, argument);
    }

    static bool IsSelfChat(string topic, DemoSession session)
        => topic.Equals(session.Me, StringComparison.Ordinal)
           || session.AuthorLabel(topic) is "me";

    /// <summary>Keep items whose label or insert contains <paramref name="prefix"/> (case-insensitive).</summary>
    public static IReadOnlyList<CompletionItem> Filter(IReadOnlyList<CompletionItem> items, string? prefix)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0 || string.IsNullOrEmpty(prefix))
            return items;

        return [.. items.Where(item =>
            item.Label.Contains(prefix, StringComparison.OrdinalIgnoreCase)
            || item.Insert.Contains(prefix, StringComparison.OrdinalIgnoreCase))];
    }
}
