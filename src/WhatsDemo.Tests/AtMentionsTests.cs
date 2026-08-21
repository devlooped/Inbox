using Inbox;

namespace WhatsDemo.Tests;

public class AtMentionsTests
{
    [Fact]
    public void Chat_token_is_bracketed_topic_with_trailing_space()
        => Assert.Equal("@[111@lid] ", AtMentions.ChatInsert("111@lid"));

    [Fact]
    public void Reply_token_is_topic_and_message_id()
        => Assert.Equal("@[111@lid]:[3EB0] ", AtMentions.ReplyInsert("111@lid", "3EB0"));

    [Fact]
    public void Parses_chat_send()
    {
        Assert.True(AtMentions.TryParse("@[111@lid] hello there", out var send));
        Assert.Equal("111@lid", send.To);
        Assert.Equal("hello there", send.Text);
        Assert.Null(send.ReplyId);
        Assert.False(send.IsReply);
    }

    [Fact]
    public void Parses_reply_send()
    {
        Assert.True(AtMentions.TryParse("@[111@lid]:[3EB0ABCDEF] thanks", out var send));
        Assert.Equal("111@lid", send.To);
        Assert.Equal("3EB0ABCDEF", send.ReplyId);
        Assert.Equal("thanks", send.Text);
        Assert.True(send.IsReply);
    }

    [Fact]
    public void Empty_text_after_token_is_pending()
    {
        Assert.True(AtMentions.IsPending("@[111@lid]"));
        Assert.True(AtMentions.IsPending("@[111@lid] "));
        Assert.True(AtMentions.IsPending("@[111@lid]:[3EB0] "));
        Assert.True(AtMentions.IsPending("@"));
        Assert.True(AtMentions.IsPending("@ada"));
        Assert.False(AtMentions.IsPending("@[111@lid] hello"));
        Assert.False(AtMentions.IsPending("hello"));
    }

    [Fact]
    public void Complete_lists_chats_and_latest_messages()
    {
        var book = new DirectoryBook();
        book.Remember("111@lid", "@ada", "Ada");
        book.Remember("222@lid", "@bob", "Bob");
        var session = new DemoSession(book);
        var recents = new RecentChats();
        recents.Note("111@lid", "3EB0", "111@lid", "let's meet tomorrow");

        var items = AtMentions.Complete("@", ["111@lid", "222@lid"], session, recents);
        Assert.Equal(
            [
                "@ada",
                "@ada: let's meet tomorrow",
                "@bob",
            ],
            items.Select(i => i.Label));
        Assert.Equal("@[111@lid] ", items[0].Insert);
        Assert.Equal("@[111@lid]:[3EB0] ", items[1].Insert);
        Assert.Equal("@[222@lid] ", items[2].Insert);
    }

    [Fact]
    public void Complete_filters_by_handle_prefix()
    {
        var book = new DirectoryBook();
        book.Remember("111@lid", "@ada", "Ada");
        book.Remember("222@lid", "@bob", "Bob");
        var session = new DemoSession(book);
        var recents = new RecentChats();

        var items = AtMentions.Complete("@ad", ["111@lid", "222@lid"], session, recents);
        Assert.Equal(["@ada"], items.Select(i => i.Label));
    }

    [Fact]
    public void Space_hides_the_mention_popup()
        => Assert.Empty(AtMentions.Complete("@ada ", ["111@lid"], new DemoSession(), new RecentChats()));

    [Fact]
    public void Complete_lists_subscribed_groups_by_subject_without_at()
    {
        var book = new DirectoryBook();
        book.Remember("111@lid", "@ada", "Ada");
        book.Remember(new DirectoryRow
        {
            Topic = "5491159278282-1472673286@g.us",
            Kind = "group",
            Name = "Nosotros",
        });
        var session = new DemoSession(book);
        session.NoteIdentity("me@lid");

        var items = AtMentions.Complete(
            "@",
            ["111@lid", "5491159278282-1472673286@g.us"],
            session,
            new RecentChats());
        Assert.Equal(["me", "@ada", "Nosotros"], items.Select(i => i.Label));
        Assert.Equal("@[5491159278282-1472673286@g.us] ", items[2].Insert);
    }

    [Fact]
    public void Complete_always_lists_self_first_even_when_not_in_subscriptions()
    {
        var book = new DirectoryBook();
        book.Remember("me@lid", "@danielkzu", "@danielkzu");
        book.Remember("111@lid", null, "Analía Carvallo");
        var session = new DemoSession(book);
        session.NoteIdentity("me@lid");

        var items = AtMentions.Complete("@", ["111@lid"], session, new RecentChats());
        Assert.Equal(["me", "@Analía Carvallo"], items.Select(i => i.Label));
        Assert.Equal("@[me@lid] ", items[0].Insert);
        Assert.Equal("@[111@lid] ", items[1].Insert);
    }
}

public class RecentChatsTests
{
    [Fact]
    public void Last_per_topic_and_lookup_by_id()
    {
        var recents = new RecentChats();
        recents.Note("111@lid", "1", "111@lid", "first");
        recents.Note("111@lid", "2", "me", "second");
        recents.Note("222@lid", "3", "222@lid", "other");

        Assert.True(recents.TryLast("111@lid", out var last));
        Assert.Equal("2", last.Id);
        Assert.Equal("second", last.Text);
        Assert.True(recents.TryGetById("1", out var first));
        Assert.Equal("first", first.Text);
    }

    [Fact]
    public void ReplyTo_uses_cached_author()
    {
        var recents = new RecentChats();
        recents.Note("111@lid", "3EB0", "me", "anda?");
        var reply = recents.ReplyTo("3EB0");
        Assert.NotNull(reply);
        Assert.Equal("3EB0", reply.Id);
        Assert.Equal("me", reply.By);
        Assert.Equal("anda?", reply.Text);
        Assert.Null(recents.ReplyTo(null));
    }

    [Fact]
    public void Forget_drops_the_chat()
    {
        var recents = new RecentChats();
        recents.Note("111@lid", "1", "111@lid", "bye");
        recents.ForgetTopic("111@lid");
        Assert.False(recents.TryLast("111@lid", out _));
        Assert.False(recents.TryGetById("1", out _));
    }
}
