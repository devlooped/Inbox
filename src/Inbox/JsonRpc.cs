using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Inbox;

/// <summary>NDJSON JSON-RPC 2.0 request envelope (no <c>params</c>).</summary>
sealed class JsonRpcRequest
{
    public string Jsonrpc { get; init; } = JsonRpc.Version;
    public required string Id { get; init; }
    public required string Method { get; init; }
}

/// <summary>NDJSON JSON-RPC 2.0 request envelope with typed <c>params</c>.</summary>
sealed class JsonRpcRequest<TParams>
{
    public string Jsonrpc { get; init; } = JsonRpc.Version;
    public required string Id { get; init; }
    public required string Method { get; init; }
    public TParams? Params { get; init; }
}

/// <summary>NDJSON JSON-RPC 2.0 framing. One compact JSON object per line.</summary>
static class JsonRpc
{
    public const string Version = "2.0";

    /// <summary>AOT-safe options backed by <see cref="InboxJsonContext"/>.</summary>
    public static JsonSerializerOptions SerializerOptions => InboxJsonContext.Default.Options;

    public static bool TryParse(string line, out JsonRpcMessage message)
    {
        message = default;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var jsonrpc = root.TryGetProperty("jsonrpc", out var v) ? v.GetString() : null;
            if (jsonrpc != Version)
                return false;

            var method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;
            var id = ReadId(root);
            JsonElement? result = root.TryGetProperty("result", out var r) ? r.Clone() : null;
            JsonRpcError? error = null;
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                var code = e.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : 0;
                var token = e.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
                JsonElement? data = e.TryGetProperty("data", out var d) ? d.Clone() : null;
                error = new JsonRpcError(code, token, data);
            }

            JsonElement? eventParams = null;
            if (method == "event" && root.TryGetProperty("params", out var p))
                eventParams = p.Clone();

            message = new JsonRpcMessage(id, method, result, error, eventParams);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string Request(string id, string method)
        => JsonSerializer.Serialize(
            new JsonRpcRequest { Id = id, Method = method },
            JsonRpcContext.Default.JsonRpcRequest);

    public static string Request<TParams>(string id, string method, TParams @params, JsonTypeInfo<JsonRpcRequest<TParams>> typeInfo)
        => JsonSerializer.Serialize(
            new JsonRpcRequest<TParams> { Id = id, Method = method, Params = @params },
            typeInfo);

    static string? ReadId(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var id) || id.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => id.GetRawText(),
        };
    }
}

readonly record struct JsonRpcMessage(
    string? Id,
    string? Method,
    JsonElement? Result,
    JsonRpcError? Error,
    JsonElement? EventParams)
{
    public bool IsEvent => Method == "event" && EventParams is not null;
    public bool IsResponse => Id is not null && (Result is not null || Error is not null);
}

readonly record struct JsonRpcError(int Code, string Message, JsonElement? Data);

