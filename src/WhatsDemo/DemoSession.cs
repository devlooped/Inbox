namespace WhatsDemo;

/// <summary>Identity, sent-id de-dupe, and message-line formatting for the demo REPL.</summary>
public sealed class DemoSession
{
    readonly EchoDeduper echoes = new();
    readonly DirectoryBook book;
    int pairedAnnounced;

    public DemoSession(DirectoryBook? book = null)
        => this.book = book ?? new DirectoryBook();

    public DirectoryBook Book => book;

    public string? Me { get; private set; }

    public string SelfChatLabel => "me";

    public void NoteIdentity(string? me)
    {
        if (!string.IsNullOrEmpty(me))
        {
            Me = me;
            book.MarkMe(me);
        }
    }

    public void ClearIdentity()
    {
        Me = null;
        Interlocked.Exchange(ref pairedAnnounced, 0);
    }

    public void BeginSend(string text) => echoes.BeginSend(text);

    public void RememberSent(string id) => echoes.Remember(id);

    public void RememberSent(string id, string text) => echoes.Complete(id, text);

    public void CancelPendingSend(string text) => echoes.CancelPending(text);

    public bool TryAnnouncePaired() => Interlocked.Exchange(ref pairedAnnounced, 1) == 0;

    public string ChatLabel(string topic)
        => AuthorLabel(topic);

    /// <summary><c>me</c> for the paired account; otherwise <c>handle ?? byName ?? by</c>.</summary>
    public string AuthorLabel(string topic, string? by = null, string? handle = null, string? byName = null)
    {
        var id = by ?? topic;
        if (IsSelfId(id) || book.IsMe(id))
            return SelfChatLabel;
        return book.Display(id, handle, byName);
    }

    public string FormatOutbound(string message, DateTimeOffset timestamp, string? topic = null)
    {
        var label = topic is null || IsSelfId(topic) ? SelfChatLabel : book.Display(topic);
        return ChatLine.Sent(label, timestamp, message);
    }

    /// <summary>
    /// Formats an inbound text event. Returns <c>null</c> when this is the echo of a
    /// message this client just sent (known id, or in-flight body before the RPC returns).
    /// </summary>
    public string? FormatInboundText(
        string topic,
        string? id,
        string? text,
        DateTimeOffset timestamp,
        string? by = null,
        string? handle = null,
        string? byName = null)
    {
        if (IsOwnAuthor(by) && echoes.IsOwnSend(id, text))
            return null;
        return ChatLine.Received(AuthorLabel(topic, by, handle, byName), timestamp, text ?? "");
    }

    bool IsOwnAuthor(string? by)
        => by is null or "me" || (Me is { } me && string.Equals(by, me, StringComparison.Ordinal));

    bool IsSelfId(string? id)
        => id is null or "me" || (Me is { } me && string.Equals(id, me, StringComparison.Ordinal));
}
