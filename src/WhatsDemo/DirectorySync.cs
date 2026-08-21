using System.Collections.Concurrent;
using Inbox;

namespace WhatsDemo;

/// <summary>
/// Keeps <see cref="DirectoryBook"/> warm via <c>directory.get</c> and persists
/// subscriptions plus aliases to <c>wb.toml</c>.
/// </summary>
public sealed class DirectorySync
{
    readonly DirectoryBook book;
    readonly string path;
    readonly Func<string, CancellationToken, Task<DirectoryRow>> get;
    readonly ConcurrentDictionary<string, Task> inflight = new(StringComparer.Ordinal);
    readonly Lock gate = new();
    List<string> subscribe;

    public DirectorySync(
        DirectoryBook book,
        string path,
        IReadOnlyList<string>? subscribe = null,
        Func<string, CancellationToken, Task<DirectoryRow>>? get = null)
    {
        this.book = book ?? throw new ArgumentNullException(nameof(book));
        this.path = path ?? throw new ArgumentNullException(nameof(path));
        this.subscribe = [.. DirectoryBook.ChatTopics(subscribe ?? [])];
        this.get = get ?? ((_, _) => throw new InvalidOperationException("directory.get is not configured"));
    }

    public DirectoryBook Book => book;

    public IReadOnlyList<string> Subscribe
    {
        get
        {
            lock (gate)
                return [.. subscribe];
        }
    }

    /// <summary>Chat topics to pass as <c>initialize.subscribe</c>, or <c>null</c> when none are stored.</summary>
    public IReadOnlyList<string>? InitialSubscribe
    {
        get
        {
            var topics = Subscribe;
            return topics.Count == 0 ? null : topics;
        }
    }

    public static DirectorySync Load(
        DirectoryBook book,
        string directory,
        Func<string, CancellationToken, Task<DirectoryRow>> get)
    {
        var path = WhatsBoxToml.PathIn(directory);
        var doc = WhatsBoxToml.Load(path);
        book.Import(doc.Directory);
        return new DirectorySync(book, path, doc.Subscribe, get);
    }

    public bool Contains(string? topic)
    {
        if (topic is null || !DirectoryBook.IsLookupId(topic))
            return false;
        lock (gate)
            return subscribe.Contains(topic, StringComparer.Ordinal);
    }

    public void ReplaceSubscriptions(IEnumerable<string> topics)
    {
        ArgumentNullException.ThrowIfNull(topics);
        lock (gate)
            subscribe = [.. DirectoryBook.ChatTopics(topics)];
        Save();
    }

    /// <summary>
    /// Adds chat topics from a <c>subscribe</c> result. The RPC returns only the
    /// newly applied topics, not the full set — do not replace.
    /// </summary>
    public void MergeSubscriptions(IEnumerable<string> topics)
    {
        ArgumentNullException.ThrowIfNull(topics);
        var added = DirectoryBook.ChatTopics(topics);
        lock (gate)
        {
            foreach (var topic in added)
            {
                if (!subscribe.Contains(topic, StringComparer.Ordinal))
                    subscribe.Add(topic);
            }
        }

        Save();
    }

    public async Task<DirectoryRow?> OnSubscribeAsync(string requested, IReadOnlyList<string> topics, CancellationToken cancellation = default)
    {
        MergeSubscriptions(topics);
        var ids = new List<string>();
        foreach (var topic in DirectoryBook.ChatTopics(topics))
            ids.Add(topic);
        if (DirectoryBook.IsLookupId(requested) && !ids.Contains(requested, StringComparer.Ordinal))
            ids.Add(requested);

        DirectoryRow? row = null;
        foreach (var id in ids)
        {
            row = await TryGetAsync(id, cancellation).ConfigureAwait(false);
            if (row is not null)
            {
                Remember(row);
                break;
            }
        }

        return row;
    }

    public async Task OnUnsubscribeAsync(string requested, IReadOnlyList<string> topics, CancellationToken cancellation = default)
    {
        ReplaceSubscriptions(topics);
        await ResolveAsync(requested, cancellation, force: true).ConfigureAwait(false);
    }

    public void Remember(DirectoryRow row)
    {
        if (book.Remember(row))
            Save();
    }

    public void RememberAuthor(string? id, string? handle, string? name)
    {
        if (book.Remember(id, handle, name))
            Save();
    }

    public void NoteSelf(string? me)
    {
        if (book.MarkMe(me))
            Save();
    }

    public async Task WarmAsync(CancellationToken cancellation = default)
    {
        foreach (var topic in Subscribe)
            await ResolveAsync(topic, cancellation, force: true).ConfigureAwait(false);
    }

    public async Task ResolveAsync(string? id, CancellationToken cancellation = default, bool force = false)
    {
        if (id is null || !DirectoryBook.IsLookupId(id))
            return;
        if (!force && book.HasLabel(id))
            return;

        var task = inflight.GetOrAdd(id, key => FetchAsync(key, cancellation));
        try
        {
            await task.WaitAsync(cancellation).ConfigureAwait(false);
        }
        catch (InboxRpcException)
        {
            // Keep any persisted alias; the prompt falls back to the raw id.
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) when (task.IsFaulted)
        {
            // Auto-lookup is best-effort.
        }
        finally
        {
            inflight.TryRemove(id, out _);
        }
    }

    async Task FetchAsync(string id, CancellationToken cancellation)
    {
        var row = await get(id, cancellation).ConfigureAwait(false);
        Remember(row);
    }

    async Task<DirectoryRow?> TryGetAsync(string id, CancellationToken cancellation)
    {
        try
        {
            return await get(id, cancellation).ConfigureAwait(false);
        }
        catch (InboxRpcException)
        {
            return null;
        }
    }

    void Save()
    {
        lock (gate)
        {
            WhatsBoxToml.Save(path, new WhatsBoxDocument(subscribe, book.Snapshot()));
        }
    }
}
