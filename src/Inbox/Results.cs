namespace Inbox;

/// <summary>Result of <c>initialize</c> and the <c>session.*</c> methods.</summary>
public sealed record SessionSnapshot
{
    /// <summary><c>new</c>, <c>offline</c>, or <c>online</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Current subscription set. Always includes <c>$session</c>.</summary>
    public IReadOnlyList<string> Topics { get; init; } = [];

    /// <summary>Paired account LID. Omitted when <see cref="Status"/> is <c>new</c>.</summary>
    public string? Me { get; init; }

    /// <summary>Protocol version, present on <c>initialize</c> (and connect-as-init).</summary>
    public string? Version { get; init; }

    /// <summary>INBOX.md product id, e.g. <c>whatsapp</c>.</summary>
    public string? Product { get; init; }

    /// <summary>INBOX.md identity, e.g. <c>user</c> or <c>bot</c>.</summary>
    public string? Identity { get; init; }

    /// <summary>Optional stack when it differs from <see cref="Identity"/>.</summary>
    public string? Profile { get; init; }

    /// <summary>Advertised capabilities. Required on a conformant daemon.</summary>
    public Capabilities? Capabilities { get; init; }
}

/// <summary>INBOX.md <c>capabilities</c> object on <c>initialize</c> / <c>session.status</c>.</summary>
public sealed record Capabilities
{
    /// <summary>How pair authenticates: <c>qr</c>, <c>oauth</c>, <c>device_code</c>, <c>token</c>.</summary>
    public IReadOnlyList<string> Auth { get; init; } = [];

    /// <summary><c>quote</c>, <c>context</c>, or <c>none</c>.</summary>
    public string? Reply { get; init; }

    /// <summary>Whether a <c>reaction</c> content part is supported.</summary>
    public bool React { get; init; }

    /// <summary><c>message</c>, <c>cursor</c>, <c>conversation</c>, or <c>none</c>.</summary>
    public string? Read { get; init; }

    /// <summary>Whether <c>kind: ack</c> events are emitted.</summary>
    public bool Ack { get; init; }

    /// <summary>Whether the product can move blobs through <c>initialize.files</c>.</summary>
    public bool Files { get; init; }

    /// <summary><c>none</c>, <c>single</c>, or <c>many</c>.</summary>
    public string? Attachments { get; init; }
}

/// <summary>Result of <c>subscribe</c> / <c>unsubscribe</c>.</summary>
public sealed record TopicsResult
{
    /// <summary>Canonical topics from the RPC result.</summary>
    public IReadOnlyList<string> Topics { get; init; } = [];
}

/// <summary>Result of <c>directory.list</c>.</summary>
public sealed record DirectoryListResult
{
    /// <summary>Page of directory rows.</summary>
    public IReadOnlyList<DirectoryRow> Items { get; init; } = [];

    /// <summary>Opaque cursor; omitted or empty means last page.</summary>
    public string? Cursor { get; init; }
}

/// <summary>INBOX.md §7.1 <c>DirectoryRow</c>.</summary>
public sealed record DirectoryRow
{
    /// <summary>Canonical JID.</summary>
    public required string Topic { get; init; }

    /// <summary><c>user</c> or <c>group</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Best display name.</summary>
    public string? Name { get; init; }

    /// <summary>WhatsApp username with a leading <c>@</c>, when known. Users only.</summary>
    public string? Handle { get; init; }

    /// <summary>Phone-number JID label.</summary>
    public string? Pn { get; init; }

    /// <summary>Relative icon path under <c>files</c>.</summary>
    public string? Icon { get; init; }

    /// <summary>Muted flag from app-state.</summary>
    public bool Muted { get; init; }

    /// <summary>Pinned flag from app-state.</summary>
    public bool Pinned { get; init; }

    /// <summary>Archived flag from app-state.</summary>
    public bool Archived { get; init; }

    /// <summary>Optional group size; not the full roster.</summary>
    public int ParticipantCount { get; init; }

    /// <summary>Group members; present on <c>directory.get</c> only.</summary>
    public IReadOnlyList<DirectoryParticipant>? Participants { get; init; }
}

/// <summary>A group member on <c>directory.get</c>.</summary>
public sealed record DirectoryParticipant
{
    /// <summary>Canonical member JID.</summary>
    public required string Topic { get; init; }

    /// <summary>Display name if known.</summary>
    public string? Name { get; init; }

    /// <summary>WhatsApp username with a leading <c>@</c>, when known.</summary>
    public string? Handle { get; init; }

    /// <summary>Phone-number JID label if known.</summary>
    public string? Pn { get; init; }
}

/// <summary>Result of <c>messages.send</c>.</summary>
public sealed record SendResult
{
    /// <summary>WhatsApp message id.</summary>
    public required string Id { get; init; }

    /// <summary>Canonical chat JID after normalization.</summary>
    public required string Topic { get; init; }
}

/// <summary>Result of <c>messages.read</c>.</summary>
public sealed record ReadResult
{
    /// <summary>Canonical chat JID.</summary>
    public required string Topic { get; init; }
}

