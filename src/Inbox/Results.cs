using System.Text.Json.Serialization;

namespace Inbox;

/// <summary>INBOX.md session <c>status</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionStatus>))]
public enum SessionStatus
{
    /// <summary>No session in the store.</summary>
    [JsonStringEnumMemberName("new")]
    New,

    /// <summary>Keys/tokens exist, connection down.</summary>
    [JsonStringEnumMemberName("offline")]
    Offline,

    /// <summary>Send and receive are up.</summary>
    [JsonStringEnumMemberName("online")]
    Online,
}

/// <summary>INBOX.md <c>identity</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Identity>))]
public enum Identity
{
    [JsonStringEnumMemberName("user")]
    User,

    [JsonStringEnumMemberName("bot")]
    Bot,
}

/// <summary>INBOX.md directory <c>kind</c>: <c>user</c> or <c>group</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DirectoryKind>))]
public enum DirectoryKind
{
    [JsonStringEnumMemberName("user")]
    User,

    [JsonStringEnumMemberName("group")]
    Group,
}

/// <summary>Result of <c>initialize</c> and the <c>session.*</c> methods.</summary>
public sealed record SessionSnapshot
{
    /// <summary><see cref="SessionStatus.New"/>, <see cref="SessionStatus.Offline"/>, or <see cref="SessionStatus.Online"/>.</summary>
    public required SessionStatus Status { get; init; }

    /// <summary>Current subscription set. Always includes <c>$session</c>.</summary>
    public IReadOnlyList<string> Topics { get; init; } = [];

    /// <summary>Paired product identity. Omitted when <see cref="Status"/> is <see cref="SessionStatus.New"/>.</summary>
    public string? Me { get; init; }

    /// <summary>Protocol version, present on <c>initialize</c> (and connect-as-init). v1 is <c>0.1</c>.</summary>
    public string? Version { get; init; }

    /// <summary>INBOX.md product id (e.g. <c>whatsapp</c>, <c>webpubsub</c>).</summary>
    public string? Product { get; init; }

    /// <summary>INBOX.md identity.</summary>
    public Identity? Identity { get; init; }

    /// <summary>Optional stack when it differs from <see cref="Identity"/> (e.g. <c>hub</c>).</summary>
    public string? Profile { get; init; }

    /// <summary>Advertised capabilities. Required on a conformant daemon.</summary>
    public Capabilities? Capabilities { get; init; }
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

    /// <summary><see cref="DirectoryKind.User"/> or <see cref="DirectoryKind.Group"/>.</summary>
    public required DirectoryKind Kind { get; init; }

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
    /// <summary>Canonical chat topic.</summary>
    public required string Topic { get; init; }
}

/// <summary>Result of <c>directory.join</c>, <c>directory.leave</c>, and <c>directory.create</c>.</summary>
public sealed record TopicResult
{
    /// <summary>Canonical chat topic.</summary>
    public required string Topic { get; init; }
}

