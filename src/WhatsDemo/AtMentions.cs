namespace WhatsDemo;

/// <summary>Parsed <c>@[to]</c> send or <c>@[to]:[msgid]</c> reply, plus the user's message.</summary>
public readonly record struct MentionSend(string To, string Text, string? ReplyId)
{
    public bool IsReply => ReplyId is { Length: > 0 };

    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// <c>@</c> mention tokens for <c>messages.send</c>.
/// Chat: <c>@[topic] text</c>. Reply: <c>@[topic]:[msgid] text</c>.
/// </summary>
public static class AtMentions
{
    public static string ChatInsert(string topic) => $"@[{topic}] ";

    public static string ReplyInsert(string topic, string messageId) => $"@[{topic}]:[{messageId}] ";

    public static bool IsPending(string input)
    {
        if (input.Length == 0 || input[0] != '@')
            return false;
        if (TryParse(input, out var send))
            return !send.HasText;
        return !input.Contains(' ');
    }

    public static bool TryParse(string line, out MentionSend send)
    {
        send = default;
        if (!line.StartsWith("@[", StringComparison.Ordinal))
            return false;

        var close = line.IndexOf(']', 2);
        if (close < 3)
            return false;
        var to = line[2..close];
        if (to.Length == 0)
            return false;

        var i = close + 1;
        string? replyId = null;
        if (i + 1 < line.Length && line[i] == ':' && line[i + 1] == '[')
        {
            var close2 = line.IndexOf(']', i + 2);
            if (close2 < i + 3)
                return false;
            replyId = line[(i + 2)..close2];
            if (replyId.Length == 0)
                return false;
            i = close2 + 1;
        }

        var text = i < line.Length ? line[i..].Trim() : "";
        send = new MentionSend(to, text, replyId);
        return true;
    }

    public static IReadOnlyList<CompletionItem> Complete(
        string input,
        IReadOnlyList<string> topics,
        DemoSession session,
        RecentChats recents)
    {
        if (input.Length == 0 || input[0] != '@' || input.Contains(' ') || input.StartsWith("@[", StringComparison.Ordinal))
            return [];

        var prefix = input[1..];
        var items = new List<CompletionItem>();
        foreach (var topic in WithSelfFirst(topics, session.Me))
        {
            var raw = session.AuthorLabel(topic);
            var label = MentionLabel(topic, raw);
            if (Matches(prefix, label, raw, topic))
                items.Add(new CompletionItem(ChatInsert(topic), label));

            if (!recents.TryLast(topic, out var last))
                continue;
            var preview = Preview(last.Text);
            var replyLabel = $"{label}: {preview}";
            if (Matches(prefix, replyLabel, label, raw, topic, last.Text))
                items.Add(new CompletionItem(ReplyInsert(topic, last.Id), replyLabel));
        }

        return items;
    }

    /// <summary>Self-chat is always first so <c>@</c> can target it even if a later subscribe result omitted it.</summary>
    public static IReadOnlyList<string> WithSelfFirst(IReadOnlyList<string> topics, string? me)
    {
        if (me is null || me.Length == 0)
            return topics;
        var rest = topics.Where(t => !string.Equals(t, me, StringComparison.Ordinal));
        return [me, .. rest];
    }

    /// <summary>Groups keep their subject; users get a leading <c>@</c> when they have no handle.</summary>
    public static string MentionLabel(string topic, string raw)
    {
        if (raw is "me" || raw.StartsWith('@') || DirectoryBook.IsGroupId(topic))
            return raw;
        return "@" + raw;
    }

    static bool Matches(string prefix, params string?[] parts)
    {
        if (prefix.Length == 0)
            return true;
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part))
                continue;
            if (part.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
            if (part[0] == '@' && part.AsSpan(1).Contains(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    static string Preview(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "(message)";
        var one = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return one.Length <= 48 ? one : one[..45] + "...";
    }
}
