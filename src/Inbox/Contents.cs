using System.Text.Json.Serialization;

namespace Inbox;

/// <summary>A INBOX.md content part. Discriminated by JSON <c>type</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextPart), "text")]
[JsonDerivedType(typeof(ImagePart), "image")]
[JsonDerivedType(typeof(VideoPart), "video")]
[JsonDerivedType(typeof(AudioPart), "audio")]
[JsonDerivedType(typeof(DocumentPart), "document")]
[JsonDerivedType(typeof(StickerPart), "sticker")]
[JsonDerivedType(typeof(LocationPart), "location")]
[JsonDerivedType(typeof(UnknownPart), "unknown")]
[JsonDerivedType(typeof(ReactionPart), "reaction")]
[JsonDerivedType(typeof(AckPart), "ack")]
[JsonDerivedType(typeof(MetaPart), "meta")]
public abstract record ContentPart
{
    /// <summary>Fixes <see cref="Type"/> for a concrete part.</summary>
    protected ContentPart(string type) => Type = type;

    /// <summary>Used by the JSON deserializer.</summary>
    protected ContentPart() { }

    /// <summary>INBOX.md part <c>type</c>. Not serialized as a regular property (discriminator).</summary>
    [JsonIgnore]
    public string Type { get; protected init; } = "";
}

/// <summary>Body or caption text.</summary>
public sealed record TextPart() : ContentPart("text")
{
    /// <summary>Text body.</summary>
    public string? Text { get; init; }
}

/// <summary>Blob part (<c>image</c>, <c>video</c>, <c>audio</c>, <c>document</c>, <c>sticker</c>).</summary>
public abstract record MediaPart : ContentPart
{
    /// <summary>Fixes <see cref="ContentPart.Type"/> for a concrete media type.</summary>
    protected MediaPart(string type) : base(type) { }

    /// <summary>Used by the JSON deserializer.</summary>
    protected MediaPart() { }

    /// <summary>Relative path under <c>files</c> when the download succeeded.</summary>
    public string? Path { get; init; }

    /// <summary>Download / files error token when the blob was not written.</summary>
    public string? Error { get; init; }
}

/// <summary><c>image</c> part.</summary>
public sealed record ImagePart() : MediaPart("image");

/// <summary><c>video</c> part.</summary>
public sealed record VideoPart() : MediaPart("video");

/// <summary><c>audio</c> part.</summary>
public sealed record AudioPart() : MediaPart("audio");

/// <summary><c>document</c> part.</summary>
public sealed record DocumentPart() : MediaPart("document");

/// <summary><c>sticker</c> part.</summary>
public sealed record StickerPart() : MediaPart("sticker");

/// <summary><c>location</c> part. Place <see cref="Name"/> is not a chat topic name.</summary>
public sealed record LocationPart() : ContentPart("location")
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

/// <summary>Polls, view-once, ciphertext, and other non-blob payloads. No file.</summary>
public sealed record UnknownPart() : ContentPart("unknown")
{
    /// <summary>Short label when the daemon provided one (<c>view_once</c>, <c>poll</c>, <c>encrypted</c>, …).</summary>
    public string? Label { get; init; }
}

/// <summary>Reaction part. Envelope <c>kind</c> is <c>reaction</c>; <c>by</c> on the event is who reacted.</summary>
public sealed record ReactionPart() : ContentPart("reaction")
{
    /// <summary>Id of the reacted-to message.</summary>
    public string? Target { get; init; }

    /// <summary>Author of the reacted-to message. Required on send.</summary>
    public string? By { get; init; }

    /// <summary>Emoji. Empty string means the reaction was removed.</summary>
    public string? Emoji { get; init; }
}

/// <summary>Receipt part. Envelope <c>id</c> is omitted; ids live here.</summary>
public sealed record AckPart() : ContentPart("ack")
{
    /// <summary>Acknowledged message ids.</summary>
    public IReadOnlyList<string> Ids { get; init; } = [];

    /// <summary><c>delivered</c>, <c>read</c>, or <c>played</c>.</summary>
    public string? Ack { get; init; }
}

/// <summary>Room-notice part.</summary>
public sealed record MetaPart() : ContentPart("meta")
{
    /// <summary>Room notice action (<c>join</c>, <c>leave</c>, <c>rename</c>, …).</summary>
    public string? Action { get; init; }

    /// <summary>Optional notice name (rename subject, …).</summary>
    public string? Name { get; init; }

    /// <summary>Optional notice text.</summary>
    public string? Text { get; init; }
}

