using System.Text.Json;

namespace Inbox;

/// <summary>Maps INBOX.md <c>event</c> params to typed <see cref="InboxEvent"/> instances.</summary>
static class EventMapper
{
    public static InboxEvent? TryMap(JsonElement p)
    {
        var topic = GetString(p, "topic") ?? "";
        var kind = GetString(p, "kind") ?? "";

        return topic switch
        {
            "$session" => MapSession(kind, p),
            "$directory" => MapDirectory(kind, p),
            _ => MapChat(p, topic, kind),
        };
    }

    static InboxEvent? MapSession(string kind, JsonElement p) => kind switch
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

    static InboxEvent? MapDirectory(string kind, JsonElement p) => kind switch
    {
        "upsert" => new DirectoryUpsert(
            Jid: GetString(p, "jid") ?? "",
            EntityKind: GetString(p, "entityKind") ?? "",
            Name: GetString(p, "name"),
            Handle: GetString(p, "handle"),
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

    static ChatEvent MapChat(JsonElement p, string topic, string kind)
    {
        var contents = MapParts(p);
        if (kind is not ("message" or "reaction" or "ack" or "meta"))
        {
            if (contents.Count == 0)
                contents = [new UnknownPart { Label = kind }];
            kind = "message";
        }

        return kind switch
        {
            "reaction" => new ChatReaction
            {
                Topic = topic,
                Id = GetString(p, "id"),
                By = GetString(p, "by"),
                Handle = GetString(p, "handle"),
                TopicName = GetString(p, "topicName"),
                ByName = GetString(p, "byName"),
                Context = GetString(p, "context"),
                Contents = contents,
            },
            "ack" => new ChatAck
            {
                Topic = topic,
                Id = GetString(p, "id"),
                By = GetString(p, "by"),
                Handle = GetString(p, "handle"),
                TopicName = GetString(p, "topicName"),
                ByName = GetString(p, "byName"),
                Context = GetString(p, "context"),
                Contents = contents,
            },
            "meta" => new ChatMeta
            {
                Topic = topic,
                Id = GetString(p, "id"),
                By = GetString(p, "by"),
                Handle = GetString(p, "handle"),
                TopicName = GetString(p, "topicName"),
                ByName = GetString(p, "byName"),
                Context = GetString(p, "context"),
                Contents = contents,
            },
            _ => new ChatMessage
            {
                Topic = topic,
                Id = GetString(p, "id"),
                By = GetString(p, "by"),
                Handle = GetString(p, "handle"),
                TopicName = GetString(p, "topicName"),
                ByName = GetString(p, "byName"),
                Context = GetString(p, "context"),
                Contents = contents,
            },
        };
    }

    static IReadOnlyList<ContentPart> MapParts(JsonElement p)
    {
        if (!p.TryGetProperty("contents", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<ContentPart>(arr.GetArrayLength());
        foreach (var el in arr.EnumerateArray())
        {
            var type = GetString(el, "type") ?? "unknown";
            list.Add(type switch
            {
                "text" => new TextPart { Text = GetString(el, "text") },
                "image" => new ImagePart { Path = GetString(el, "path"), Error = GetString(el, "error") },
                "video" => new VideoPart { Path = GetString(el, "path"), Error = GetString(el, "error") },
                "audio" => new AudioPart { Path = GetString(el, "path"), Error = GetString(el, "error") },
                "document" => new DocumentPart { Path = GetString(el, "path"), Error = GetString(el, "error") },
                "sticker" => new StickerPart { Path = GetString(el, "path"), Error = GetString(el, "error") },
                "location" => new LocationPart
                {
                    Lat = GetDouble(el, "lat"),
                    Lng = GetDouble(el, "lng"),
                    Name = GetString(el, "name"),
                    Address = GetString(el, "address"),
                },
                "reaction" => new ReactionPart
                {
                    Target = GetString(el, "target"),
                    By = GetString(el, "by"),
                    Emoji = GetString(el, "emoji"),
                },
                "ack" => new AckPart { Ids = GetStringList(el, "ids"), Ack = GetString(el, "ack") },
                "meta" => new MetaPart
                {
                    Action = GetString(el, "action"),
                    Name = GetString(el, "name"),
                    Text = GetString(el, "text"),
                },
                "unknown" => new UnknownPart { Label = GetString(el, "label") },
                _ => new UnknownPart { Label = GetString(el, "label") ?? type },
            });
        }
        return list;
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

    static IReadOnlyList<string> GetStringList(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var item in p.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                list.Add(s);
        }
        return list;
    }
}

