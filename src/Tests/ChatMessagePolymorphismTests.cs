using System.Text.Json;
using Inbox;

namespace Tests;

public class ChatMessagePolymorphismTests
{
    [Fact]
    public void Discriminator_selects_message_when_kind_is_not_first()
    {
        const string json = """{"topic":"999@lid","kind":"message","id":"3EB0","by":"999@lid","handle":"@ada","topicName":"Ada","byName":"Ada","contents":[{"type":"text","text":"hi"}]}""";
        var ev = JsonSerializer.Deserialize(json, InboxJsonContext.Default.ChatEvent);

        var msg = Assert.IsType<ChatMessage>(ev);
        Assert.Equal("message", msg.Kind);
        Assert.Equal("999@lid", msg.Topic);
        Assert.Equal("3EB0", msg.Id);
        Assert.Equal("999@lid", msg.By);
        Assert.Equal("@ada", msg.Handle);
        Assert.Equal("Ada", msg.TopicName);
        Assert.Equal("Ada", msg.ByName);
        Assert.Equal("hi", msg.Text);
        var part = Assert.IsType<TextPart>(Assert.Single(msg.Contents));
        Assert.Equal("hi", part.Text);
    }

    [Theory]
    [InlineData("image", typeof(ImagePart))]
    [InlineData("video", typeof(VideoPart))]
    [InlineData("audio", typeof(AudioPart))]
    [InlineData("document", typeof(DocumentPart))]
    [InlineData("sticker", typeof(StickerPart))]
    public void Discriminator_selects_media_part(string type, Type expected)
    {
        var json = $$"""{"topic":"g@g.us","kind":"message","id":"1","by":"me","contents":[{"type":"{{type}}","path":"in/a.jpg"},{"type":"text","text":"cap"}]}""";
        var ev = JsonSerializer.Deserialize(json, InboxJsonContext.Default.ChatEvent);

        var msg = Assert.IsType<ChatMessage>(ev);
        Assert.Equal("message", msg.Kind);
        Assert.Equal("g@g.us", msg.Topic);
        Assert.Equal("cap", msg.Text);
        Assert.Equal("me", msg.By);
        Assert.Equal(2, msg.Contents.Count);
        Assert.IsType(expected, msg.Contents[0]);
        var media = Assert.IsAssignableFrom<MediaPart>(msg.Contents[0]);
        Assert.Equal(type, media.Type);
        Assert.Equal("in/a.jpg", media.Path);
    }

    [Fact]
    public void Serialize_as_ChatEvent_writes_kind_discriminator()
    {
        ChatEvent ev = new ChatMessage
        {
            Topic = "999@lid",
            Id = "1",
            By = "me",
            Contents = [new ImagePart { Path = "in/p.jpg" }],
        };

        var json = JsonSerializer.Serialize(ev, InboxJsonContext.Default.ChatEvent);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("message", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("999@lid", doc.RootElement.GetProperty("topic").GetString());
        var part = Assert.Single(doc.RootElement.GetProperty("contents").EnumerateArray());
        Assert.Equal("image", part.GetProperty("type").GetString());
        Assert.Equal("in/p.jpg", part.GetProperty("path").GetString());
        Assert.False(part.TryGetProperty("error", out _));
        Assert.False(doc.RootElement.TryGetProperty("text", out _));
    }

    [Fact]
    public void Text_concatenates_text_parts_and_is_not_serialized()
    {
        var msg = new ChatMessage
        {
            Topic = "999@lid",
            Contents =
            [
                new ImagePart { Path = "in/p.jpg" },
                new TextPart { Text = "hello" },
                new TextPart { Text = " world" },
            ],
        };
        Assert.Equal("hello world", msg.Text);

        var json = JsonSerializer.Serialize(msg, InboxJsonContext.Default.ChatMessage);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("text", out _));
    }

    [Fact]
    public void Mapper_falls_back_to_ChatMessage_unknown_part_for_unrecognized_kind()
    {
        using var doc = JsonDocument.Parse("""{"topic":"999@lid","kind":"poll","id":"9","by":"999@lid","label":"poll"}""");
        var ev = EventMapper.TryMap(doc.RootElement);

        var msg = Assert.IsType<ChatMessage>(ev);
        Assert.Equal("message", msg.Kind);
        Assert.Equal("999@lid", msg.Topic);
        Assert.Equal("9", msg.Id);
        var unk = Assert.IsType<UnknownPart>(Assert.Single(msg.Contents));
        Assert.Equal("poll", unk.Label);
    }

    [Fact]
    public void Mapper_reads_reaction_ack_meta_parts()
    {
        using var re = JsonDocument.Parse(
            """{"topic":"999@lid","kind":"reaction","id":"r1","by":"me","contents":[{"type":"reaction","target":"t1","emoji":"👍"}]}""");
        var reaction = Assert.IsType<ChatReaction>(EventMapper.TryMap(re.RootElement));
        var rp = Assert.IsType<ReactionPart>(Assert.Single(reaction.Contents));
        Assert.Equal("t1", rp.Target);
        Assert.Equal("👍", rp.Emoji);

        using var ack = JsonDocument.Parse(
            """{"topic":"999@lid","kind":"ack","contents":[{"type":"ack","ids":["t1"],"ack":"read"}]}""");
        var chatAck = Assert.IsType<ChatAck>(EventMapper.TryMap(ack.RootElement));
        var ap = Assert.IsType<AckPart>(Assert.Single(chatAck.Contents));
        Assert.Equal(["t1"], ap.Ids);
        Assert.Equal("read", ap.Ack);

        using var meta = JsonDocument.Parse(
            """{"topic":"g@g.us","kind":"meta","by":"me","contents":[{"type":"meta","action":"rename","name":"New"}]}""");
        var chatMeta = Assert.IsType<ChatMeta>(EventMapper.TryMap(meta.RootElement));
        var mp = Assert.IsType<MetaPart>(Assert.Single(chatMeta.Contents));
        Assert.Equal("rename", mp.Action);
        Assert.Equal("New", mp.Name);
    }

    [Fact]
    public void Mapper_reads_directory_upsert_handle()
    {
        using var doc = JsonDocument.Parse(
            """{"topic":"$directory","kind":"upsert","jid":"999@lid","entityKind":"user","name":"Ada","handle":"@ada","pn":"1555@s.whatsapp.net"}""");
        var ev = EventMapper.TryMap(doc.RootElement);

        var upsert = Assert.IsType<DirectoryUpsert>(ev);
        Assert.Equal("999@lid", upsert.Jid);
        Assert.Equal("Ada", upsert.Name);
        Assert.Equal("@ada", upsert.Handle);
        Assert.Equal("1555@s.whatsapp.net", upsert.Pn);
    }
}

