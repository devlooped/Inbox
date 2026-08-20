namespace WhatsDemo;

/// <summary>CLI line for a sent or received self-chat message.</summary>
public static class ChatLine
{
    public const string Checkmark = "✓";
    public const string UpArrow = "↑";

    public static string Format(string chat, DateTimeOffset timestamp, string marker, string message)
        => $"[{chat}:{timestamp:HH:mm:ss}] {marker} {message}";

    public static string Sent(string chat, DateTimeOffset timestamp, string message)
        => Format(chat, timestamp, UpArrow, message);

    public static string Received(string chat, DateTimeOffset timestamp, string message)
        => Format(chat, timestamp, Checkmark, message);
}
