using System.Text.Json;

namespace WhatsBox;

/// <summary>NDJSON JSON-RPC 2.0 framing. One compact JSON object per line.</summary>
static class JsonRpc
{
    public const string Version = "2.0";

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

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

    public static string Request(string id, string method, object? @params)
    {
        var payload = new Dictionary<string, object?>
        {
            ["jsonrpc"] = Version,
            ["id"] = id,
            ["method"] = method,
        };
        if (@params is not null)
            payload["params"] = @params;
        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

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
