using System.Text.Json;

namespace WhatsBox;

/// <summary>Maps PRODUCT.md §6 <c>event</c> params to typed <see cref="WhatsEvent"/> instances.</summary>
static class EventMapper
{
    public static WhatsEvent? TryMap(JsonElement p)
    {
        var topic = GetString(p, "topic") ?? "";
        var kind = GetString(p, "kind") ?? "";

        return topic switch
        {
            "$session" => MapSession(kind, p),
            "$directory" => MapDirectory(kind, p),
            _ => MapChat(p, topic),
        };
    }

    static WhatsEvent? MapSession(string kind, JsonElement p) => kind switch
    {
        "qr" => new SessionQr(GetString(p, "code") ?? ""),
        "paired" => new SessionPaired(GetString(p, "me") ?? ""),
        "pair_error" => new SessionPairError(GetString(p, "message") ?? ""),
        "online" => new SessionOnline(GetString(p, "me")),
        "offline" => new SessionOffline(GetString(p, "reason")),
        "logged_out" => new SessionLoggedOut(GetString(p, "reason")),
        "remap" => new SessionRemap(GetString(p, "from") ?? "", GetString(p, "to") ?? ""),
        "overflow" => new SessionOverflow(
            GetString(p, "queue") ?? GetString(p, "overflowTopic") ?? "",
            GetInt(p, "dropped") ?? 0),
        _ => null,
    };

    static WhatsEvent? MapDirectory(string kind, JsonElement p) => kind switch
    {
        "upsert" => new DirectoryUpsert(
            Jid: GetString(p, "jid") ?? "",
            EntityKind: GetString(p, "entityKind") ?? "",
            Name: GetString(p, "name"),
            Pn: GetString(p, "pn"),
            Icon: GetString(p, "icon"),
            Muted: GetBool(p, "muted") ?? false,
            Pinned: GetBool(p, "pinned") ?? false,
            Archived: GetBool(p, "archived") ?? false,
            ParticipantCount: GetInt(p, "participantCount") ?? 0),
        "remove" => new DirectoryRemove(GetString(p, "jid") ?? GetString(p, "id") ?? ""),
        "ready" => new DirectoryReady(GetInt(p, "generated") ?? 0),
        _ => null,
    };

    static ChatMessage MapChat(JsonElement p, string topic)
    {
        try
        {
            if (p.Deserialize(WhatsJsonContext.Default.ChatMessage) is { } chat)
                return chat;
        }
        catch (NotSupportedException)
        {
            // Unrecognized kind — fall through to ChatUnknown.
        }
        catch (JsonException)
        {
            // Malformed discriminator / payload — still surface a chat event.
        }

        return new ChatUnknown
        {
            Topic = topic,
            Id = GetString(p, "id"),
            By = GetString(p, "by"),
            Pn = GetString(p, "pn"),
            Label = GetString(p, "label"),
        };
    }

    internal static string? GetString(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p))
            return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    internal static int? GetInt(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p))
            return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n))
            return n;
        if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out n))
            return n;
        return null;
    }

    static bool? GetBool(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p))
            return null;
        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
