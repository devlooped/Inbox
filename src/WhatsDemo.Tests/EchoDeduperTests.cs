namespace WhatsDemo.Tests;

public class EchoDeduperTests
{
    static readonly DateTimeOffset Time = new(2026, 8, 19, 9, 8, 7, TimeSpan.Zero);

    [Fact]
    public void Inbound_whose_id_was_just_sent_is_not_formatted_as_received()
    {
        var session = new DemoSession();
        session.NoteIdentity("111@lid");
        session.RememberSent("3EB0");

        Assert.Null(session.FormatInboundText("111@lid", "3EB0", "hello", Time));
    }

    [Fact]
    public void Inbound_with_a_different_id_is_formatted_as_received()
    {
        var session = new DemoSession();
        session.NoteIdentity("111@lid");
        session.RememberSent("3EB0");

        var line = session.FormatInboundText("111@lid", "3EB1", "other", Time);
        Assert.NotNull(line);
        Assert.Contains("[me:09:08:07]", line);
        Assert.Contains(ChatLine.Checkmark, line);
        Assert.Contains("other", line);
        Assert.DoesNotContain(ChatLine.UpArrow, line);
    }

    [Fact]
    public void Echo_is_suppressed_only_once()
    {
        var session = new DemoSession();
        session.RememberSent("3EB0");
        Assert.Null(session.FormatInboundText("x@lid", "3EB0", "once", Time));
        var line = session.FormatInboundText("x@lid", "3EB0", "again", Time);
        Assert.NotNull(line);
        Assert.Contains("again", line);
    }

    [Fact]
    public void Inbound_during_in_flight_send_is_not_formatted_as_received()
    {
        var session = new DemoSession();
        session.NoteIdentity("111@lid");
        session.BeginSend("hello");

        Assert.Null(session.FormatInboundText("111@lid", "3EB0", "hello", Time, by: "me"));
    }

    [Fact]
    public void Inbound_other_body_during_in_flight_send_is_still_received()
    {
        var session = new DemoSession();
        session.NoteIdentity("111@lid");
        session.BeginSend("hello");

        var line = session.FormatInboundText("111@lid", "3EB1", "from phone", Time, by: "me");
        Assert.NotNull(line);
        Assert.Contains("[me:09:08:07]", line);
        Assert.Contains(ChatLine.Checkmark, line);
        Assert.Contains("from phone", line);
        Assert.DoesNotContain(ChatLine.UpArrow, line);
    }

    [Fact]
    public void Logout_allows_paired_to_be_announced_again()
    {
        var session = new DemoSession();
        Assert.True(session.TryAnnouncePaired());
        Assert.False(session.TryAnnouncePaired());
        session.ClearIdentity();
        Assert.True(session.TryAnnouncePaired());
    }
}
