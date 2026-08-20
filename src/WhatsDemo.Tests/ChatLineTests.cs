namespace WhatsDemo.Tests;

public class ChatLineTests
{
    static readonly DateTimeOffset Time = new(2026, 8, 19, 14, 30, 5, TimeSpan.FromHours(-3));

    [Fact]
    public void Sent_line_is_chat_time_up_arrow_and_body()
    {
        var session = new DemoSession();
        var line = session.FormatOutbound("hello from demo", Time);

        Assert.Contains("[me:14:30:05]", line);
        Assert.Contains(ChatLine.UpArrow, line);
        Assert.Contains("hello from demo", line);
        Assert.DoesNotContain(ChatLine.Checkmark, line);
        Assert.Equal($"[me:14:30:05] {ChatLine.UpArrow} hello from demo", line);
    }

    [Fact]
    public void Outbound_to_another_chat_uses_directory_label()
    {
        var book = new DirectoryBook();
        book.Remember("999@lid", "@ada", "Ada");
        var session = new DemoSession(book);
        var line = session.FormatOutbound("hi ada", Time, "999@lid");
        Assert.Equal($"[@ada:14:30:05] {ChatLine.UpArrow} hi ada", line);
    }

    [Fact]
    public void Received_line_is_chat_time_checkmark_and_body()
    {
        var session = new DemoSession();
        session.NoteIdentity("111@lid");
        var line = session.FormatInboundText("111@lid", "3EB1", "from phone", Time);

        Assert.NotNull(line);
        Assert.Contains("[me:14:30:05]", line);
        Assert.Contains(ChatLine.Checkmark, line);
        Assert.Contains("from phone", line);
        Assert.DoesNotContain(ChatLine.UpArrow, line);
        Assert.Equal($"[me:14:30:05] {ChatLine.Checkmark} from phone", line);
    }

    [Fact]
    public void Received_prefers_handle_then_by_name_then_by()
    {
        var session = new DemoSession();
        var handle = session.FormatInboundText("999@lid", "1", "hi", Time, by: "999@lid", handle: "@ada", byName: "Ada");
        var name = session.FormatInboundText("999@lid", "2", "hi", Time, by: "999@lid", byName: "Ada");
        var raw = session.FormatInboundText("999@lid", "3", "hi", Time, by: "999@lid");

        Assert.Equal($"[@ada:14:30:05] {ChatLine.Checkmark} hi", handle);
        Assert.Equal($"[Ada:14:30:05] {ChatLine.Checkmark} hi", name);
        Assert.Equal($"[999@lid:14:30:05] {ChatLine.Checkmark} hi", raw);
    }

    [Fact]
    public void Received_uses_directory_cache_when_event_has_no_handle()
    {
        var book = new DirectoryBook();
        book.Remember("999@lid", "@ada", "Ada");
        var session = new DemoSession(book);

        var line = session.FormatInboundText("999@lid", "1", "hi", Time, by: "999@lid");
        Assert.Equal($"[@ada:14:30:05] {ChatLine.Checkmark} hi", line);
    }

    [Fact]
    public void Self_chat_is_always_labeled_me_even_when_cache_has_a_handle()
    {
        var book = new DirectoryBook();
        book.Remember("111@lid", "@danielkzu", "Kzu");
        var session = new DemoSession(book);
        session.NoteIdentity("111@lid");

        var inbound = session.FormatInboundText("111@lid", "1", "from phone", Time, by: "111@lid", handle: "@danielkzu");
        Assert.Equal($"[me:14:30:05] {ChatLine.Checkmark} from phone", inbound);
        Assert.Equal($"[me:14:30:05] {ChatLine.UpArrow} hi", session.FormatOutbound("hi", Time, "111@lid"));
    }
}
