using System.Text.Json;

namespace WhatsBox;

/// <summary>Maps PRODUCT.md §6 <c>event</c> params to typed <see cref="WhatsEvent"/> instances.</summary>
static class EventMapper
{
    public static WhatsEvent? TryMap(JsonElement p)
    {
        var topic = GetString(p, "topic") ?? "";
        var kind = GetString(p, "kind") ?? "";

        return (topic, kind) switch
        {
            ("$session", "qr") => new SessionQr(GetString(p, "code") ?? ""),
            ("$session", "paired") => new SessionPaired(GetString(p, "me") ?? ""),
            ("$session", "pair_error") => new SessionPairError(GetString(p, "message") ?? ""),
            ("$session", "online") => new SessionOnline(GetString(p, "me")),
            ("$session", "offline") => new SessionOffline(GetString(p, "reason")),
            ("$session", "logged_out") => new SessionLoggedOut(GetString(p, "reason")),
            ("$session", "remap") => new SessionRemap(GetString(p, "from") ?? "", GetString(p, "to") ?? ""),
            ("$session", "overflow") => new SessionOverflow(
                GetString(p, "queue") ?? GetString(p, "overflowTopic") ?? "",
                GetInt(p, "dropped") ?? 0),
            ("$directory", "upsert") => new DirectoryUpsert(
                Jid: GetString(p, "jid") ?? "",
                EntityKind: GetString(p, "entityKind") ?? "",
                Name: GetString(p, "name"),
                Pn: GetString(p, "pn"),
                Icon: GetString(p, "icon"),
                Muted: GetBool(p, "muted") ?? false,
                Pinned: GetBool(p, "pinned") ?? false,
                Archived: GetBool(p, "archived") ?? false,
                ParticipantCount: GetInt(p, "participantCount") ?? 0),
            ("$directory", "remove") => new DirectoryRemove(GetString(p, "jid") ?? GetString(p, "id") ?? ""),
            ("$directory", "ready") => new DirectoryReady(GetInt(p, "generated") ?? 0),
            (_, "text") => new ChatText(topic, GetString(p, "id"), GetString(p, "by"), GetString(p, "pn"), GetString(p, "text")),
            (_, "image") => Media<ChatImage>(topic, p, static (t, id, by, pn, text, path, err) => new(t, id, by, pn, text, path, err)),
            (_, "video") => Media<ChatVideo>(topic, p, static (t, id, by, pn, text, path, err) => new(t, id, by, pn, text, path, err)),
            (_, "audio") => Media<ChatAudio>(topic, p, static (t, id, by, pn, text, path, err) => new(t, id, by, pn, text, path, err)),
            (_, "document") => Media<ChatDocument>(topic, p, static (t, id, by, pn, text, path, err) => new(t, id, by, pn, text, path, err)),
            (_, "sticker") => Media<ChatSticker>(topic, p, static (t, id, by, pn, text, path, err) => new(t, id, by, pn, text, path, err)),
            (_, "location") => new ChatLocation(
                topic, GetString(p, "id"), GetString(p, "by"), GetString(p, "pn"),
                GetDouble(p, "lat"), GetDouble(p, "lng"), GetString(p, "name"), GetString(p, "address")),
            (_, "reaction") => new ChatReaction(
                topic, GetString(p, "id"), GetString(p, "by"), GetString(p, "pn"),
                GetString(p, "emoji"), GetString(p, "target")),
            (_, "ack") => new ChatAck(topic, GetStrings(p, "ids"), GetString(p, "ack")),
            (_, "meta") => new ChatMeta(topic, GetString(p, "action"), GetString(p, "text")),
            (_, "unknown") => new ChatUnknown(topic, GetString(p, "id"), GetString(p, "by"), GetString(p, "pn"), GetString(p, "label")),
            _ => null,
        };
    }

    static T Media<T>(
        string topic,
        JsonElement p,
        Func<string, string?, string?, string?, string?, string?, string?, T> ctor)
        => ctor(topic, GetString(p, "id"), GetString(p, "by"), GetString(p, "pn"), GetString(p, "text"), GetString(p, "path"), GetString(p, "error"));

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

    static double? GetDouble(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p))
            return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var n))
            return n;
        if (p.ValueKind == JsonValueKind.String && double.TryParse(p.GetString(), out n))
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

    static IReadOnlyList<string> GetStrings(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>(p.GetArrayLength());
        foreach (var item in p.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                list.Add(s);
        }
        return list;
    }
}
