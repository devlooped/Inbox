using WhatsBox;

namespace WhatsDemo;

public enum TopicResolveStatus
{
    Found,
    NotFound,
    Cancelled,
}

public readonly record struct TopicResolve(TopicResolveStatus Status, string? Topic)
{
    public static TopicResolve Found(string topic) => new(TopicResolveStatus.Found, topic);

    public static TopicResolve NotFound() => new(TopicResolveStatus.NotFound, null);

    public static TopicResolve Cancelled() => new(TopicResolveStatus.Cancelled, null);
}

/// <summary>
/// Maps a subscribe/unsubscribe argument to a canonical LID/group JID.
/// JIDs pass through; everything else is <c>directory.list</c>.
/// </summary>
public static class TopicResolver
{
    public static bool IsCanonical(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var s = id.Trim();
        if (s[0] == '$')
            return true;
        return s.EndsWith("@lid", StringComparison.OrdinalIgnoreCase)
            || s.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)
            || s.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsGroup(string? id)
        => DirectoryBook.IsGroupId(id);

    public static IReadOnlyList<CompletionItem> Completions(IReadOnlyList<DirectoryRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return [.. rows.Select(RowItem)];
    }

    public static async Task<TopicResolve> ResolveAsync(
        string query,
        Func<string, CancellationToken, Task<IReadOnlyList<DirectoryRow>>> list,
        Func<IReadOnlyList<CompletionItem>, CancellationToken, Task<string?>> pick,
        CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(pick);

        var q = query.Trim();
        if (IsCanonical(q))
            return TopicResolve.Found(q);

        var items = await list(q, cancellation).ConfigureAwait(false);
        return items.Count switch
        {
            0 => TopicResolve.NotFound(),
            1 => TopicResolve.Found(items[0].Topic),
            _ => await PickAsync(items, pick, cancellation).ConfigureAwait(false),
        };
    }

    static async Task<TopicResolve> PickAsync(
        IReadOnlyList<DirectoryRow> items,
        Func<IReadOnlyList<CompletionItem>, CancellationToken, Task<string?>> pick,
        CancellationToken cancellation)
    {
        var picked = await pick(Completions(items), cancellation).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(picked))
            return TopicResolve.Cancelled();

        var topic = picked.Trim();
        return IsCanonical(topic) ? TopicResolve.Found(topic) : TopicResolve.NotFound();
    }

    static CompletionItem RowItem(DirectoryRow row)
    {
        var title = DirectoryAlias.NonEmpty(row.Handle)
            ?? DirectoryAlias.NonEmpty(row.Name)
            ?? DirectoryAlias.NonEmpty(row.Pn)
            ?? row.Topic;
        var label = title == row.Topic ? title : $"{title}  {row.Topic}";
        return new CompletionItem(row.Topic, label);
    }
}
