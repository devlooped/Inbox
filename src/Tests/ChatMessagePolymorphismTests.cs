using System.Text.Json;
using WhatsBox;

namespace Tests;

public class ChatMessagePolymorphismTests
{
    [Fact]
    public void Discriminator_selects_text_when_kind_is_not_first()
    {
        const string json = """{"topic":"999@lid","kind":"text","id":"3EB0","by":"999@lid","pn":"1555@s.whatsapp.net","text":"hi"}""";
        var ev = JsonSerializer.Deserialize(json, WhatsJsonContext.Default.ChatMessage);

        var text = Assert.IsType<ChatText>(ev);
        Assert.Equal("text", text.Kind);
        Assert.Equal("999@lid", text.Topic);
        Assert.Equal("3EB0", text.Id);
        Assert.Equal("999@lid", text.By);
        Assert.Equal("1555@s.whatsapp.net", text.Pn);
        Assert.Equal("hi", text.Text);
    }

    [Theory]
    [InlineData("image", typeof(ChatImage))]
    [InlineData("video", typeof(ChatVideo))]
    [InlineData("audio", typeof(ChatAudio))]
    [InlineData("document", typeof(ChatDocument))]
    [InlineData("sticker", typeof(ChatSticker))]
    public void Discriminator_selects_media_subtype(string kind, Type expected)
    {
        var json = $$"""{"topic":"g@g.us","kind":"{{kind}}","id":"1","by":"me","text":"cap","path":"in/a.jpg"}""";
        var ev = JsonSerializer.Deserialize(json, WhatsJsonContext.Default.ChatMessage);

        Assert.IsType(expected, ev);
        var media = Assert.IsAssignableFrom<ChatMedia>(ev);
        Assert.Equal(kind, media.Kind);
        Assert.Equal("g@g.us", media.Topic);
        Assert.Equal("cap", media.Text);
        Assert.Equal("in/a.jpg", media.Path);
        Assert.Equal("me", media.By);
    }

    [Fact]
    public void Serialize_as_ChatMessage_writes_kind_discriminator()
    {
        ChatMessage ev = new ChatImage
        {
            Topic = "999@lid",
            Id = "1",
            By = "me",
            Path = "in/p.jpg",
        };

        var json = JsonSerializer.Serialize(ev, WhatsJsonContext.Default.ChatMessage);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("image", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("999@lid", doc.RootElement.GetProperty("topic").GetString());
        Assert.Equal("in/p.jpg", doc.RootElement.GetProperty("path").GetString());
        Assert.False(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void Mapper_falls_back_to_ChatUnknown_for_unrecognized_kind()
    {
        using var doc = JsonDocument.Parse("""{"topic":"999@lid","kind":"poll","id":"9","by":"999@lid","label":"poll"}""");
        var ev = EventMapper.TryMap(doc.RootElement);

        var unknown = Assert.IsType<ChatUnknown>(ev);
        Assert.Equal("unknown", unknown.Kind);
        Assert.Equal("999@lid", unknown.Topic);
        Assert.Equal("9", unknown.Id);
        Assert.Equal("poll", unknown.Label);
    }
}
