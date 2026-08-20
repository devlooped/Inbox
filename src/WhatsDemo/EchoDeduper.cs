namespace WhatsDemo;

/// <summary>
/// Suppresses echoes of this client's own sends. Native <c>messages.send</c> emits
/// the <c>by:me</c> chat event before the RPC result, so a send must be marked
/// pending by body before the await — id-only tracking is too late.
/// </summary>
public sealed class EchoDeduper
{
    readonly HashSet<string> sentIds = new(StringComparer.Ordinal);
    readonly List<string> pendingBodies = [];
    readonly Lock gate = new();

    public void BeginSend(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        lock (gate)
            pendingBodies.Add(text);
    }

    public void Remember(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (gate)
            sentIds.Add(id);
    }

    public void Complete(string id, string text)
    {
        Remember(id);
        CancelPending(text);
    }

    public void CancelPending(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        lock (gate)
        {
            var i = pendingBodies.IndexOf(text);
            if (i >= 0)
                pendingBodies.RemoveAt(i);
        }
    }

    public bool IsOwnSend(string? id, string? text = null)
    {
        lock (gate)
        {
            if (!string.IsNullOrEmpty(id) && sentIds.Remove(id))
                return true;

            if (text is null)
                return false;

            var i = pendingBodies.IndexOf(text);
            if (i < 0)
                return false;

            pendingBodies.RemoveAt(i);
            if (!string.IsNullOrEmpty(id))
                sentIds.Add(id);
            return true;
        }
    }
}
