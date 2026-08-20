namespace WhatsDemo.Tests;

public class SlashCommandsTests
{
    [Fact]
    public void Catalog_is_exactly_the_six_session_commands()
    {
        Assert.Equal(
            ["logout", "disconnect", "connect", "subscribe", "unsubscribe", "directory"],
            SlashCommands.Names);
    }

    [Fact]
    public void Slash_alone_completes_to_the_full_catalog()
    {
        Assert.Equal(SlashCommands.Names, SlashCommands.Complete("/"));
    }

    [Theory]
    [InlineData("/con", "connect")]
    [InlineData("/lo", "logout")]
    [InlineData("/un", "unsubscribe")]
    [InlineData("/subscribe", "subscribe")]
    [InlineData("/CONNECT", "connect")]
    public void Prefix_filter_returns_the_matching_command(string input, string expected)
    {
        Assert.Equal([expected], SlashCommands.Complete(input));
    }

    [Fact]
    public void Prefix_d_matches_disconnect_then_directory()
    {
        Assert.Equal(["disconnect", "directory"], SlashCommands.Complete("/d"));
    }

    [Fact]
    public void Space_after_command_hides_the_popup()
    {
        Assert.Empty(SlashCommands.Complete("/subscribe "));
        Assert.Empty(SlashCommands.Complete("/directory ada"));
    }

    [Fact]
    public void Non_slash_input_has_no_completions()
    {
        Assert.Empty(SlashCommands.Complete(""));
        Assert.Empty(SlashCommands.Complete("hello"));
        Assert.Empty(SlashCommands.Complete("logout"));
    }

    [Fact]
    public void LineEditor_defaults_to_SlashCommands_Complete()
    {
        var editor = new LineEditor(new ConsoleLock());
        Assert.Equal(SlashCommands.Names, editor.Complete("/").Select(i => i.Label));
        Assert.Equal(["connect"], editor.Complete("/con").Select(i => i.Label));
        Assert.Equal(["disconnect", "directory"], editor.Complete("/d").Select(i => i.Label));
    }

    [Fact]
    public void LineEditor_and_ConsoleLock_share_one_sync_lock()
    {
        var output = new ConsoleLock();
        var editor = new LineEditor(output);
        Assert.True(output.Sync == editor.Sync);
    }

    [Theory]
    [InlineData("subscribe")]
    [InlineData("unsubscribe")]
    [InlineData("directory")]
    public void Argument_commands_complete_with_a_trailing_space(string name)
    {
        Assert.True(SlashCommands.TakesArgument(name));
        Assert.Equal("/" + name + " ", SlashCommands.CompletedInput(name));
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("disconnect")]
    [InlineData("connect")]
    public void Session_commands_complete_without_a_trailing_space(string name)
    {
        Assert.False(SlashCommands.TakesArgument(name));
        Assert.Equal("/" + name, SlashCommands.CompletedInput(name));
    }

    [Theory]
    [InlineData("/subscribe")]
    [InlineData("/subscribe ")]
    [InlineData("/un")]
    [InlineData("/unsubscribe")]
    [InlineData("/directory")]
    [InlineData("/directory  ")]
    [InlineData("/dir")]
    public void Argument_command_without_a_value_stays_open(string input)
        => Assert.True(SlashCommands.IsPendingArgument(input));

    [Theory]
    [InlineData("/subscribe +15551234567")]
    [InlineData("/unsubscribe 999@lid")]
    [InlineData("/directory ada")]
    [InlineData("/logout")]
    [InlineData("/connect")]
    [InlineData("hello")]
    [InlineData("")]
    public void Line_with_an_argument_or_no_slash_command_commits(string input)
        => Assert.False(SlashCommands.IsPendingArgument(input));
}

public class UnsubscribeCompletionsTests
{
    [Fact]
    public void Unsubscribe_without_space_still_completes_the_command()
    {
        var items = Completions.Complete("/unsubscribe", ["111@lid"], new DemoSession(), new RecentChats());
        Assert.Equal(["unsubscribe"], items.Select(i => i.Label));
        Assert.Equal(["/unsubscribe "], items.Select(i => i.Insert));
    }

    [Fact]
    public void Unsubscribe_lists_subscribed_chats_except_self()
    {
        var book = new DirectoryBook();
        book.Remember("me@lid", "@danielkzu", "Kzu");
        book.Remember("111@lid", "@ada", "Ada");
        book.Remember(new WhatsBox.DirectoryRow
        {
            Topic = "12036342@g.us",
            Kind = "group",
            Name = "Nosotros",
        });
        var session = new DemoSession(book);
        session.NoteIdentity("me@lid");

        var items = Completions.Complete(
            "/unsubscribe ",
            ["me@lid", "111@lid", "12036342@g.us"],
            session,
            new RecentChats());

        Assert.Equal(["@ada", "Nosotros"], items.Select(i => i.Label));
        Assert.Equal(
            ["/unsubscribe 111@lid", "/unsubscribe 12036342@g.us"],
            items.Select(i => i.Insert));
    }

    [Fact]
    public void Unsubscribe_filters_by_argument_prefix()
    {
        var book = new DirectoryBook();
        book.Remember("111@lid", "@ada", "Ada");
        book.Remember(new WhatsBox.DirectoryRow
        {
            Topic = "12036342@g.us",
            Kind = "group",
            Name = "Nosotros",
        });
        var session = new DemoSession(book);

        var items = Completions.Complete(
            "/unsubscribe nos",
            ["111@lid", "12036342@g.us"],
            session,
            new RecentChats());

        Assert.Equal(["Nosotros"], items.Select(i => i.Label));
        Assert.Equal(["/unsubscribe 12036342@g.us"], items.Select(i => i.Insert));
    }
}
