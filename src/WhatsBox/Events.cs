using System.Text.Json.Serialization;

namespace WhatsBox;

/// <summary>Base type for every PRODUCT.md §6 <c>event</c> notification.</summary>
public abstract record WhatsEvent
{
    /// <summary>Canonical topic. <c>$session</c>, <c>$directory</c>, or a chat JID.</summary>
    public string Topic { get; init; } = "";

    /// <summary>
    /// PRODUCT.md <c>kind</c>. On <see cref="ChatMessage"/> this is the JSON type discriminator
    /// and must not be serialized as a regular property.
    /// </summary>
    [JsonIgnore]
    public string Kind { get; protected init; } = "";

    /// <summary>Creates a system event with a fixed topic and kind.</summary>
    protected WhatsEvent(string topic, string kind)
    {
        Topic = topic;
        Kind = kind;
    }

    /// <summary>Creates an event whose <see cref="Topic"/> is set from JSON or an initializer.</summary>
    protected WhatsEvent() { }
}

/// <summary><c>$session</c> / <c>qr</c>.</summary>
public sealed record SessionQr(string Code) : WhatsEvent("$session", "qr");

/// <summary><c>$session</c> / <c>paired</c>.</summary>
public sealed record SessionPaired(string Me) : WhatsEvent("$session", "paired");

/// <summary><c>$session</c> / <c>pair_error</c>.</summary>
public sealed record SessionPairError(string Message) : WhatsEvent("$session", "pair_error");

/// <summary><c>$session</c> / <c>online</c>.</summary>
public sealed record SessionOnline(string? Me) : WhatsEvent("$session", "online");

/// <summary><c>$session</c> / <c>offline</c>.</summary>
public sealed record SessionOffline(string? Reason) : WhatsEvent("$session", "offline");

/// <summary><c>$session</c> / <c>logged_out</c>.</summary>
public sealed record SessionLoggedOut(string? Reason) : WhatsEvent("$session", "logged_out");

/// <summary><c>$session</c> / <c>remap</c>.</summary>
public sealed record SessionRemap(string From, string To) : WhatsEvent("$session", "remap");

/// <summary>
/// <c>$session</c> / <c>overflow</c>.
/// <see cref="OverflowTopic"/> is the dropped queue (wire field <c>queue</c> or <c>topic</c>).
/// </summary>
public sealed record SessionOverflow(string OverflowTopic, int Dropped) : WhatsEvent("$session", "overflow");

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
    int ParticipantCount) : WhatsEvent("$directory", "upsert");

/// <summary><c>$directory</c> / <c>remove</c>.</summary>
public sealed record DirectoryRemove(string Jid) : WhatsEvent("$directory", "remove");

/// <summary><c>$directory</c> / <c>ready</c>.</summary>
public sealed record DirectoryReady(int Generated) : WhatsEvent("$directory", "ready");

/// <summary>
/// Chat-topic event. JSON-polymorphic on PRODUCT.md <c>kind</c>
/// (<c>text</c>, <c>image</c>, <c>ack</c>, …). Not used for <c>$session</c> / <c>$directory</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ChatText), "text")]
[JsonDerivedType(typeof(ChatImage), "image")]
[JsonDerivedType(typeof(ChatVideo), "video")]
[JsonDerivedType(typeof(ChatAudio), "audio")]
[JsonDerivedType(typeof(ChatDocument), "document")]
[JsonDerivedType(typeof(ChatSticker), "sticker")]
[JsonDerivedType(typeof(ChatLocation), "location")]
[JsonDerivedType(typeof(ChatReaction), "reaction")]
[JsonDerivedType(typeof(ChatAck), "ack")]
[JsonDerivedType(typeof(ChatMeta), "meta")]
[JsonDerivedType(typeof(ChatUnknown), "unknown")]
public abstract record ChatMessage : WhatsEvent
{
    /// <summary>Fixes <see cref="WhatsEvent.Kind"/> for a concrete chat kind.</summary>
    protected ChatMessage(string kind) => Kind = kind;

    /// <summary>Used by the JSON deserializer; derived types set <see cref="WhatsEvent.Kind"/> in their constructor.</summary>
    protected ChatMessage() { }

    /// <summary>Message id, when the kind is message-like.</summary>
    public string? Id { get; init; }

    /// <summary>Author: <c>me</c> or a LID.</summary>
    public string? By { get; init; }

    /// <summary>Author WhatsApp username with a leading <c>@</c>, when known.</summary>
    public string? Handle { get; init; }

    /// <summary>Chat display name (group subject or 1:1 peer name), when known.</summary>
    public string? TopicName { get; init; }

    /// <summary>Author display name, when known. For <c>by: me</c>, the paired account’s name.</summary>
    public string? ByName { get; init; }
}

/// <summary>Chat topic / <c>text</c>.</summary>
public sealed record ChatText() : ChatMessage("text")
{
    /// <summary>Body.</summary>
    public string? Text { get; init; }
}

/// <summary>
/// Chat media (<c>image</c>, <c>video</c>, <c>audio</c>, <c>document</c>, <c>sticker</c>).
/// <see cref="Path"/> is relative to <c>files</c> when the download succeeded.
/// </summary>
public abstract record ChatMedia : ChatMessage
{
    /// <summary>Fixes <see cref="WhatsEvent.Kind"/> for a concrete media kind.</summary>
    protected ChatMedia(string kind) : base(kind) { }

    /// <summary>Used by the JSON deserializer.</summary>
    protected ChatMedia() { }

    /// <summary>Caption, when present.</summary>
    public string? Text { get; init; }

    /// <summary>Relative path under <c>files</c>.</summary>
    public string? Path { get; init; }

    /// <summary>Download / files error token, when the blob was not written.</summary>
    public string? Error { get; init; }
}

/// <summary>Chat topic / <c>image</c>.</summary>
public sealed record ChatImage() : ChatMedia("image");

/// <summary>Chat topic / <c>video</c>.</summary>
public sealed record ChatVideo() : ChatMedia("video");

/// <summary>Chat topic / <c>audio</c>.</summary>
public sealed record ChatAudio() : ChatMedia("audio");

/// <summary>Chat topic / <c>document</c>.</summary>
public sealed record ChatDocument() : ChatMedia("document");

/// <summary>Chat topic / <c>sticker</c>.</summary>
public sealed record ChatSticker() : ChatMedia("sticker");

/// <summary>Chat topic / <c>location</c>.</summary>
public sealed record ChatLocation() : ChatMessage("location")
{
    /// <summary>Latitude.</summary>
    public double? Lat { get; init; }

    /// <summary>Longitude.</summary>
    public double? Lng { get; init; }

    /// <summary>Optional place name.</summary>
    public string? Name { get; init; }

    /// <summary>Optional address.</summary>
    public string? Address { get; init; }
}

/// <summary>Chat topic / <c>reaction</c>.</summary>
public sealed record ChatReaction() : ChatMessage("reaction")
{
    /// <summary>Emoji. Empty string means the reaction was removed.</summary>
    public string? Emoji { get; init; }

    /// <summary>Id of the reacted-to message.</summary>
    public string? Target { get; init; }
}

/// <summary>Chat topic / <c>ack</c>.</summary>
public sealed record ChatAck() : ChatMessage("ack")
{
    /// <summary>Acknowledged message ids.</summary>
    public IReadOnlyList<string> Ids { get; init; } = [];

    /// <summary><c>delivered</c>, <c>read</c>, or <c>played</c>.</summary>
    public string? Ack { get; init; }
}

/// <summary>Chat topic / <c>meta</c>.</summary>
public sealed record ChatMeta() : ChatMessage("meta")
{
    /// <summary>Room notice action (<c>join</c>, <c>leave</c>, <c>rename</c>, …).</summary>
    public string? Action { get; init; }

    /// <summary>Optional notice text.</summary>
    public string? Text { get; init; }
}

/// <summary>Chat topic / <c>unknown</c>, or any kind not in the v1 set.</summary>
public sealed record ChatUnknown() : ChatMessage("unknown")
{
    /// <summary>Short label when the daemon provided one.</summary>
    public string? Label { get; init; }
}
