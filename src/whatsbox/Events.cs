namespace WhatsBox;

/// <summary>Base type for every PRODUCT.md §6 <c>event</c> notification.</summary>
public abstract record WhatsEvent(string Topic, string Kind);

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

/// <summary>Chat topic / <c>text</c>.</summary>
public sealed record ChatText(string Topic, string? Id, string? By, string? Pn, string? Text) : WhatsEvent(Topic, "text");

/// <summary>Chat topic / <c>image</c>.</summary>
public sealed record ChatImage(string Topic, string? Id, string? By, string? Pn, string? Text, string? Path, string? Error) : WhatsEvent(Topic, "image");

/// <summary>Chat topic / <c>video</c>.</summary>
public sealed record ChatVideo(string Topic, string? Id, string? By, string? Pn, string? Text, string? Path, string? Error) : WhatsEvent(Topic, "video");

/// <summary>Chat topic / <c>audio</c>.</summary>
public sealed record ChatAudio(string Topic, string? Id, string? By, string? Pn, string? Text, string? Path, string? Error) : WhatsEvent(Topic, "audio");

/// <summary>Chat topic / <c>document</c>.</summary>
public sealed record ChatDocument(string Topic, string? Id, string? By, string? Pn, string? Text, string? Path, string? Error) : WhatsEvent(Topic, "document");

/// <summary>Chat topic / <c>sticker</c>.</summary>
public sealed record ChatSticker(string Topic, string? Id, string? By, string? Pn, string? Text, string? Path, string? Error) : WhatsEvent(Topic, "sticker");

/// <summary>Chat topic / <c>location</c>.</summary>
public sealed record ChatLocation(string Topic, string? Id, string? By, string? Pn, double? Lat, double? Lng, string? Name, string? Address) : WhatsEvent(Topic, "location");

/// <summary>Chat topic / <c>reaction</c>.</summary>
public sealed record ChatReaction(string Topic, string? Id, string? By, string? Pn, string? Emoji, string? Target) : WhatsEvent(Topic, "reaction");

/// <summary>Chat topic / <c>ack</c>.</summary>
public sealed record ChatAck(string Topic, IReadOnlyList<string> Ids, string? Ack) : WhatsEvent(Topic, "ack");

/// <summary>Chat topic / <c>meta</c>.</summary>
public sealed record ChatMeta(string Topic, string? Action, string? Text) : WhatsEvent(Topic, "meta");

/// <summary>Chat topic / <c>unknown</c>.</summary>
public sealed record ChatUnknown(string Topic, string? Id, string? By, string? Pn, string? Label) : WhatsEvent(Topic, "unknown");
