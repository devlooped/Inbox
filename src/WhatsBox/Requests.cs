namespace WhatsBox;

/// <summary>PRODUCT.md §5.1 / §13 <c>initialize</c> parameters.</summary>
public sealed record InitializeOptions
{
    /// <summary>Protocol version. v1 value is <c>0.1</c>.</summary>
    public string Version { get; init; } = "0.1";

    /// <summary>Absolute store path. Required when the process was not started with <c>--store</c>.</summary>
    public string? Store { get; init; }

    /// <summary>Absolute blob directory. Missing means text-only.</summary>
    public string? Files { get; init; }

    /// <summary>Initial topics; applied before any event is eligible for dispatch.</summary>
    public IReadOnlyList<string>? Subscribe { get; init; }

    /// <summary>stderr level: <c>error</c>, <c>warn</c>, <c>info</c>, or <c>debug</c>.</summary>
    public string? Verbosity { get; init; }

    /// <summary>If <c>true</c>, implicit <c>session.connect</c> after subscriptions.</summary>
    public bool? Connect { get; init; }

    /// <summary>
    /// Linked-device name shown in WhatsApp → Linked devices after pairing.
    /// Defaults to <c>whatsbox on {machine}</c>.
    /// </summary>
    public string DeviceName { get; init; } = DefaultDeviceName;

    /// <summary>Default <see cref="DeviceName"/>: <c>whatsbox on {<see cref="Environment.MachineName"/>}</c>.</summary>
    public static string DefaultDeviceName { get; } = $"whatsbox on {Environment.MachineName}";
}

/// <summary>PRODUCT.md §5.8 / §13 <c>directory.list</c> parameters.</summary>
public sealed record DirectoryListOptions
{
    /// <summary>Optional match against name, <c>pn</c>, <c>handle</c>, and JID string.</summary>
    public string? Query { get; init; }

    /// <summary>Optional. <c>user</c> or <c>group</c>.</summary>
    public string? Kind { get; init; }

    /// <summary>Page size. Implementation default applies when omitted.</summary>
    public int? Limit { get; init; }

    /// <summary>Opaque cursor; omit or empty for the first page.</summary>
    public string? Cursor { get; init; }
}

/// <summary>Quote target for <see cref="WhatsBoxClient.SendAsync"/>.</summary>
public sealed record MessageReply(string Id, string By, string? Text = null);

/// <summary>Reaction target for <see cref="WhatsBoxClient.SendAsync"/>.</summary>
public sealed record MessageReact(string Id, string By, string Emoji);

/// <summary>PRODUCT.md §5.7 <c>subscribe</c> / <c>unsubscribe</c> params.</summary>
public sealed record TopicsParams
{
    /// <summary>Canonical topics (LID, group JID, PN JID, or <c>$directory</c>).</summary>
    public required IReadOnlyList<string> Topics { get; init; }
}

/// <summary>PRODUCT.md §5.9 <c>directory.get</c> params.</summary>
public sealed record DirectoryGetParams
{
    /// <summary>LID, PN JID, or phone number.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// When omitted, defaults to whether <c>initialize.files</c> was set.
    /// <c>true</c> without files is <c>files_required</c>.
    /// </summary>
    public bool? Icon { get; init; }
}

/// <summary>PRODUCT.md §5.10 <c>messages.send</c> params.</summary>
public sealed record MessagesSendParams
{
    /// <summary>Chat (LID / PN / phone / group JID).</summary>
    public required string To { get; init; }

    /// <summary>Body. Optional if <see cref="Path"/> or <see cref="React"/> is set.</summary>
    public string? Text { get; init; }

    /// <summary>Relative path under <c>files</c>.</summary>
    public string? Path { get; init; }

    /// <summary>Quote target.</summary>
    public MessageReply? Reply { get; init; }

    /// <summary>Reaction target.</summary>
    public MessageReact? React { get; init; }
}

/// <summary>PRODUCT.md §5.11 <c>messages.read</c> params.</summary>
public sealed record MessagesReadParams
{
    /// <summary>Chat (LID / PN / phone / group JID).</summary>
    public required string To { get; init; }

    /// <summary>Message ids. Required, non-empty.</summary>
    public required IReadOnlyList<string> Ids { get; init; }

    /// <summary>Author. Required for groups; omit in 1:1.</summary>
    public string? By { get; init; }
}
