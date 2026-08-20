using WhatsBox;

namespace WhatsDemo;

/// <summary>Last message on a subscribed chat, used for <c>@</c> reply completions.</summary>
public readonly record struct RecentChatMessage(string Topic, string Id, string By, string Text);

/// <summary>In-memory last-message index, keyed by chat topic and by message id.</summary>
public sealed class RecentChats
{
    readonly Dictionary<string, RecentChatMessage> lastByTopic = new(StringComparer.Ordinal);
    readonly Dictionary<string, RecentChatMessage> byId = new(StringComparer.Ordinal);
    readonly Lock gate = new();

    public void Note(string topic, string? id, string? by, string? text)
    {
        if (string.IsNullOrEmpty(topic) || string.IsNullOrEmpty(id))
            return;
        var msg = new RecentChatMessage(topic, id, string.IsNullOrEmpty(by) ? "me" : by, text ?? "");
        lock (gate)
        {
            lastByTopic[topic] = msg;
            byId[id] = msg;
        }
    }

    public bool TryLast(string topic, out RecentChatMessage message)
    {
        lock (gate)
            return lastByTopic.TryGetValue(topic, out message);
    }

    public bool TryGetById(string id, out RecentChatMessage message)
    {
        lock (gate)
            return byId.TryGetValue(id, out message);
    }

    public MessageReply? ReplyTo(string? messageId)
    {
        if (string.IsNullOrEmpty(messageId))
            return null;
        if (TryGetById(messageId, out var msg))
            return new MessageReply(msg.Id, msg.By, msg.Text);
        return new MessageReply(messageId, "me");
    }

    public void ForgetTopic(string topic)
    {
        lock (gate)
        {
            if (lastByTopic.Remove(topic, out var msg))
                byId.Remove(msg.Id);
        }
    }
}
