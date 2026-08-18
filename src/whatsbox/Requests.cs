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
}

/// <summary>PRODUCT.md §5.8 / §13 <c>directory.list</c> parameters.</summary>
public sealed record DirectoryListOptions
{
    /// <summary>Optional match against name, <c>pn</c>, and JID string.</summary>
    public string? Query { get; init; }

    /// <summary>Optional. <c>user</c> or <c>group</c>.</summary>
    public string? Kind { get; init; }

    /// <summary>Page size. Implementation default applies when omitted.</summary>
    public int? Limit { get; init; }

    /// <summary>Opaque cursor; omit or empty for the first page.</summary>
    public string? Cursor { get; init; }
}

/// <summary>Quote target for <see cref="WhatsBoxClient.SendAsync"/>.</summary>
public sealed record MessageReply(string Id, string By);

/// <summary>Reaction target for <see cref="WhatsBoxClient.SendAsync"/>.</summary>
public sealed record MessageReact(string Id, string By, string Emoji);
