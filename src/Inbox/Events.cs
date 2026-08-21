using System.Text;
using System.Text.Json.Serialization;

namespace Inbox;

/// <summary>Base type for every INBOX.md <c>event</c> notification.</summary>
public abstract record InboxEvent
{
    /// <summary>Canonical topic. <c>$session</c>, <c>$directory</c>, or a chat id.</summary>
    public string Topic { get; init; } = "";

    /// <summary>
    /// INBOX.md <c>kind</c>. On <see cref="ChatEvent"/> this is the JSON type discriminator
    /// and must not be serialized as a regular property.
    /// </summary>
    [JsonIgnore]
    public string Kind { get; protected init; } = "";

    /// <summary>Creates a system event with a fixed topic and kind.</summary>
    protected InboxEvent(string topic, string kind)
    {
        Topic = topic;
        Kind = kind;
    }

    /// <summary>Creates an event whose <see cref="Topic"/> is set from JSON or an initializer.</summary>
    protected InboxEvent() { }
}

/// <summary><c>$session</c> / <c>qr</c>.</summary>
public sealed record SessionQr(string Code) : InboxEvent("$session", "qr");

/// <summary><c>$session</c> / <c>paired</c>.</summary>
public sealed record SessionPaired(string Me) : InboxEvent("$session", "paired");

/// <summary><c>$session</c> / <c>pair_error</c>.</summary>
public sealed record SessionPairError(string Message) : InboxEvent("$session", "pair_error");

/// <summary><c>$session</c> / <c>online</c>.</summary>
public sealed record SessionOnline(string? Me) : InboxEvent("$session", "online");

/// <summary><c>$session</c> / <c>offline</c>.</summary>
public sealed record SessionOffline(string? Reason) : InboxEvent("$session", "offline");

/// <summary><c>$session</c> / <c>logged_out</c>.</summary>
public sealed record SessionLoggedOut(string? Reason) : InboxEvent("$session", "logged_out");

/// <summary><c>$session</c> / <c>remap</c>.</summary>
public sealed record SessionRemap(string From, string To) : InboxEvent("$session", "remap");

/// <summary>
/// <c>$session</c> / <c>overflow</c>.
/// <see cref="OverflowTopic"/> is the dropped queue (wire field <c>queue</c> or <c>topic</c>).
/// </summary>
public sealed record SessionOverflow(string OverflowTopic, int Dropped) : InboxEvent("$session", "overflow");

/// <summary><c>$directory</c> / <c>upsert</c>. Native wire uses <c>jid</c> + <c>entityKind</c>.</summary>
public sealed record DirectoryUpsert(
    string Jid,
    string EntityKind,
    string? Name,
    string? Handle,
    string? Pn,
    string? Icon,
    bool Muted,
    bool Pinned,
    bool Archived,
    int ParticipantCount) : InboxEvent("$directory", "upsert");

/// <summary><c>$directory</c> / <c>remove</c>.</summary>
public sealed record DirectoryRemove(string Jid) : InboxEvent("$directory", "remove");

/// <summary><c>$directory</c> / <c>ready</c>.</summary>
public sealed record DirectoryReady(int Generated) : InboxEvent("$directory", "ready");

/// <summary>
/// Chat-topic event. JSON-polymorphic on INBOX.md <c>kind</c>
/// (<c>message</c>, <c>reaction</c>, <c>ack</c>, <c>meta</c>). Not used for <c>$session</c> / <c>$directory</c>.
/// Always has <see cref="Contents"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ChatMessage), "message")]
[JsonDerivedType(typeof(ChatReaction), "reaction")]
[JsonDerivedType(typeof(ChatAck), "ack")]
[JsonDerivedType(typeof(ChatMeta), "meta")]
public abstract record ChatEvent : InboxEvent
{
    /// <summary>Fixes <see cref="InboxEvent.Kind"/> for a concrete chat kind.</summary>
    protected ChatEvent(string kind) => Kind = kind;

    /// <summary>Used by the JSON deserializer; derived types set <see cref="InboxEvent.Kind"/> in their constructor.</summary>
    protected ChatEvent() { }

    /// <summary>Message id, when the kind is message-like. Omitted on <see cref="ChatAck"/>.</summary>
    public string? Id { get; init; }

    /// <summary>Author: <c>me</c> or an opaque user id.</summary>
    public string? By { get; init; }

    /// <summary>Author username with a leading <c>@</c>, when known.</summary>
    public string? Handle { get; init; }

    /// <summary>Chat display name, when known.</summary>
    public string? TopicName { get; init; }

    /// <summary>Author display name, when known. For <c>by: me</c>, the paired account’s name.</summary>
    public string? ByName { get; init; }

    /// <summary>Optional grouping key. Omit on the main stream.</summary>
    public string? Context { get; init; }

    /// <summary>INBOX.md content parts. Never null; empty only if the daemon omitted the array.</summary>
    public IReadOnlyList<ContentPart> Contents { get; init; } = [];

    /// <summary>
    /// Concatenation of every <see cref="TextPart.Text"/> in <see cref="Contents"/>, in order.
    /// Null when there is no text. Not serialized.
    /// </summary>
    [JsonIgnore]
    public string? Text
    {
        get
        {
            StringBuilder? sb = null;
            foreach (var part in Contents)
            {
                if (part is not TextPart { Text: { Length: > 0 } t })
                    continue;
                sb ??= new StringBuilder();
                sb.Append(t);
            }
            return sb?.ToString();
        }
    }
}

/// <summary>Chat topic / <c>message</c>. Heterogeneous parts (text, media, location, unknown).</summary>
public sealed record ChatMessage() : ChatEvent("message");

/// <summary>Chat topic / <c>reaction</c>. Exactly one <see cref="ReactionPart"/>.</summary>
public sealed record ChatReaction() : ChatEvent("reaction");

/// <summary>Chat topic / <c>ack</c>. Exactly one <see cref="AckPart"/>; envelope <c>id</c> omitted.</summary>
public sealed record ChatAck() : ChatEvent("ack");

/// <summary>Chat topic / <c>meta</c>. Exactly one <see cref="MetaPart"/>.</summary>
public sealed record ChatMeta() : ChatEvent("meta");

