using Inbox;

namespace WhatsDemo.Tests;

public class TopicResolverTests
{
    [Theory]
    [InlineData("999@lid")]
    [InlineData(" 12036342@g.us ")]
    [InlineData("15551234567@s.whatsapp.net")]
    [InlineData("$directory")]
    [InlineData("$session")]
    public void Canonical_jids_and_system_topics_pass_through(string id)
        => Assert.True(TopicResolver.IsCanonical(id));

    [Theory]
    [InlineData("Nosotros")]
    [InlineData("@ada")]
    [InlineData("+15551234567")]
    [InlineData("15551234567")]
    [InlineData("")]
    [InlineData("me")]
    public void Names_handles_and_phones_are_not_canonical(string id)
        => Assert.False(TopicResolver.IsCanonical(id));

    [Theory]
    [InlineData("12036342@g.us", true)]
    [InlineData("999@lid", false)]
    [InlineData("$directory", false)]
    [InlineData(null, false)]
    public void IsGroup_is_g_us_only(string? id, bool expected)
        => Assert.Equal(expected, TopicResolver.IsGroup(id));

    [Fact]
    public async Task Resolve_returns_canonical_input_without_listing()
    {
        var listed = false;
        var result = await TopicResolver.ResolveAsync(
            "12036342@g.us",
            (_, _) =>
            {
                listed = true;
                return Task.FromResult<IReadOnlyList<DirectoryRow>>([]);
            },
            (_, _) => Task.FromResult<string?>(null));

        Assert.Equal(TopicResolveStatus.Found, result.Status);
        Assert.Equal("12036342@g.us", result.Topic);
        Assert.False(listed);
    }

    [Fact]
    public async Task Resolve_auto_subscribes_a_unique_directory_hit()
    {
        var result = await TopicResolver.ResolveAsync(
            "Nosotros",
            (q, _) =>
            {
                Assert.Equal("Nosotros", q);
                return Task.FromResult<IReadOnlyList<DirectoryRow>>(
                [
                    new() { Topic = "12036399@g.us", Kind = "group", Name = "Nosotros" },
                ]);
            },
            (_, _) => throw new InvalidOperationException("unique hit must not pick"));

        Assert.Equal(TopicResolveStatus.Found, result.Status);
        Assert.Equal("12036399@g.us", result.Topic);
    }

    [Fact]
    public async Task Resolve_is_not_found_when_the_directory_is_empty()
    {
        var result = await TopicResolver.ResolveAsync(
            "ghost",
            (_, _) => Task.FromResult<IReadOnlyList<DirectoryRow>>([]),
            (_, _) => throw new InvalidOperationException("empty list must not pick"));

        Assert.Equal(TopicResolveStatus.NotFound, result.Status);
        Assert.Null(result.Topic);
    }

    [Fact]
    public async Task Resolve_picks_when_directory_list_is_ambiguous()
    {
        DirectoryRow[] rows =
        [
            new() { Topic = "111@lid", Kind = "user", Name = "Ana", Handle = "@ana" },
            new() { Topic = "222@lid", Kind = "user", Name = "Analía" },
        ];

        var result = await TopicResolver.ResolveAsync(
            "Ana",
            (_, _) => Task.FromResult<IReadOnlyList<DirectoryRow>>(rows),
            (items, _) =>
            {
                Assert.Equal(["111@lid", "222@lid"], items.Select(i => i.Insert));
                Assert.Contains("@ana  111@lid", items.Select(i => i.Label));
                Assert.Contains("Analía  222@lid", items.Select(i => i.Label));
                return Task.FromResult<string?>("222@lid");
            });

        Assert.Equal(TopicResolveStatus.Found, result.Status);
        Assert.Equal("222@lid", result.Topic);
    }

    [Fact]
    public async Task Resolve_cancelled_when_the_picker_returns_empty()
    {
        var result = await TopicResolver.ResolveAsync(
            "Ana",
            (_, _) => Task.FromResult<IReadOnlyList<DirectoryRow>>(
            [
                new() { Topic = "111@lid", Kind = "user", Name = "Ana" },
                new() { Topic = "222@lid", Kind = "user", Name = "Analía" },
            ]),
            (_, _) => Task.FromResult<string?>(null));

        Assert.Equal(TopicResolveStatus.Cancelled, result.Status);
        Assert.Null(result.Topic);
    }

    [Fact]
    public void Completions_insert_the_canonical_topic()
    {
        var items = TopicResolver.Completions(
        [
            new() { Topic = "12036399@g.us", Kind = "group", Name = "Nosotros" },
            new() { Topic = "999@lid", Kind = "user", Handle = "@ada", Name = "Ada" },
        ]);

        Assert.Equal("12036399@g.us", items[0].Insert);
        Assert.Equal("Nosotros  12036399@g.us", items[0].Label);
        Assert.Equal("999@lid", items[1].Insert);
        Assert.Equal("@ada  999@lid", items[1].Label);
    }
}

public class CompletionsFilterTests
{
    [Fact]
    public void Empty_prefix_keeps_every_item()
    {
        CompletionItem[] items = [new("111@lid", "Ada"), new("222@lid", "Bob")];
        Assert.Equal(items, Completions.Filter(items, ""));
        Assert.Equal(items, Completions.Filter(items, null));
    }

    [Fact]
    public void Prefix_matches_label_or_insert()
    {
        CompletionItem[] items =
        [
            new("12036399@g.us", "Nosotros  12036399@g.us"),
            new("111@lid", "@ada  111@lid"),
        ];

        Assert.Equal(["12036399@g.us"], Completions.Filter(items, "noso").Select(i => i.Insert));
        Assert.Equal(["111@lid"], Completions.Filter(items, "111@").Select(i => i.Insert));
    }
}
