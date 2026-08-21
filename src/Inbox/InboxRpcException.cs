using System.Text.Json;

namespace Inbox;

/// <summary>JSON-RPC application error from an Inbox Protocol-implementation CLI.</summary>
public sealed class InboxRpcException : Exception
{
    /// <summary>Creates an exception from a JSON-RPC error object.</summary>
    public InboxRpcException(int code, string token, JsonElement? data = null)
        : base(Format(code, token, data))
    {
        Code = code;
        Token = token;
        ErrorData = data;
    }

    /// <summary>JSON-RPC error code.</summary>
    public int Code { get; }

    /// <summary>Stable INBOX.md §10 token (the <c>error.message</c> field).</summary>
    public string Token { get; }

    /// <summary>Optional <c>error.data</c>.</summary>
    public JsonElement? ErrorData { get; }

    static string Format(int code, string token, JsonElement? data)
        => data is { } d ? $"{token} ({code}): {d}" : $"{token} ({code})";
}

