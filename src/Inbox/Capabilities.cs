using System.Text.Json.Serialization;

namespace Inbox;

/// <summary>INBOX.md <c>capabilities.auth</c> member.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AuthKind>))]
public enum AuthKind
{
    /// <summary>WhatsApp-style QR.</summary>
    [JsonStringEnumMemberName("qr")]
    Qr,

    /// <summary>OAuth authorization URL on <c>$session</c>.</summary>
    [JsonStringEnumMemberName("oauth")]
    Oauth,

    /// <summary>OAuth device grant (<c>user_code</c> + <c>verification_uri</c>).</summary>
    [JsonStringEnumMemberName("device_code")]
    DeviceCode,

    /// <summary>Token file in the store (<c>token_required</c>).</summary>
    [JsonStringEnumMemberName("token")]
    Token,
}

/// <summary>INBOX.md <c>capabilities.me</c>: who binds session <c>me</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MeBinding>))]
public enum MeBinding
{
    /// <summary>The product supplies <c>me</c> after auth.</summary>
    [JsonStringEnumMemberName("issued")]
    Issued,

    /// <summary>The client supplies <c>me</c> on <c>initialize</c> / <c>session.pair</c>.</summary>
    [JsonStringEnumMemberName("claimed")]
    Claimed,
}

/// <summary>INBOX.md <c>capabilities.membership</c>. Total order: <see cref="Create"/> ⊃ <see cref="Join"/> ⊃ roster.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MembershipCapability>))]
public enum MembershipCapability
{
    /// <summary>Find/join/leave/create are <c>unsupported</c>.</summary>
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary><c>directory.find</c>, <c>join</c>, <c>leave</c>.</summary>
    [JsonStringEnumMemberName("join")]
    Join,

    /// <summary><see cref="Join"/> plus <c>directory.create</c>.</summary>
    [JsonStringEnumMemberName("create")]
    Create,
}

/// <summary>INBOX.md <c>capabilities.reply</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReplyCapability>))]
public enum ReplyCapability
{
    /// <summary>In-chat quote. Outbound <c>context</c> ignored.</summary>
    [JsonStringEnumMemberName("quote")]
    Quote,

    /// <summary>Grouping key (Slack threads), not a quote bubble.</summary>
    [JsonStringEnumMemberName("context")]
    Context,

    /// <summary><c>messages.send</c> with <c>reply</c> is <c>unsupported</c>.</summary>
    [JsonStringEnumMemberName("none")]
    None,
}

/// <summary>INBOX.md <c>capabilities.read</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReadCapability>))]
public enum ReadCapability
{
    /// <summary>Per-id receipts (WhatsApp blue ticks).</summary>
    [JsonStringEnumMemberName("message")]
    Message,

    /// <summary>Conversation read cursor (Slack <c>conversations.mark</c>).</summary>
    [JsonStringEnumMemberName("cursor")]
    Cursor,

    /// <summary>Whole-chat mark (Teams).</summary>
    [JsonStringEnumMemberName("conversation")]
    Conversation,

    /// <summary><c>messages.read</c> is <c>unsupported</c>.</summary>
    [JsonStringEnumMemberName("none")]
    None,
}

/// <summary>INBOX.md <c>capabilities.attachments</c>: blob-part cardinality on one send.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AttachmentsCapability>))]
public enum AttachmentsCapability
{
    /// <summary>Any blob part is <c>unsupported</c>.</summary>
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary>At most one blob part; optional <c>text</c> is the caption.</summary>
    [JsonStringEnumMemberName("single")]
    Single,

    /// <summary>N blob parts, still one Box <c>{id, topic}</c>.</summary>
    [JsonStringEnumMemberName("many")]
    Many,
}

/// <summary>INBOX.md <c>capabilities</c> object on <c>initialize</c> / <c>session.status</c>.</summary>
public sealed record Capabilities
{
    /// <summary>How pair authenticates.</summary>
    public IReadOnlyList<AuthKind> Auth { get; init; } = [];

    /// <summary>How session <c>me</c> is bound.</summary>
    public MeBinding? Me { get; init; }

    /// <summary>Product membership verbs.</summary>
    public MembershipCapability? Membership { get; init; }

    /// <summary>How <c>messages.send</c> <c>reply</c> behaves.</summary>
    public ReplyCapability? Reply { get; init; }

    /// <summary>Whether a <c>reaction</c> content part is supported.</summary>
    public bool React { get; init; }

    /// <summary>How <c>messages.read</c> behaves.</summary>
    public ReadCapability? Read { get; init; }

    /// <summary>Whether <c>kind: ack</c> events are emitted.</summary>
    public bool Ack { get; init; }

    /// <summary>Whether the product can move blobs through <c>initialize.files</c>.</summary>
    public bool Files { get; init; }

    /// <summary>How many blob parts one <c>kind: message</c> may carry.</summary>
    public AttachmentsCapability? Attachments { get; init; }
}
