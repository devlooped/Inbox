using System.Text.Json;
using Inbox;

namespace Tests;

public class InboxJsonContextTests
{
    [Fact]
    public void Default_options_use_source_generated_resolver()
    {
        Assert.Same(InboxJsonContext.Default, JsonRpc.SerializerOptions.TypeInfoResolver);
        Assert.NotNull(InboxJsonContext.Default.ChatEvent);
        Assert.NotNull(InboxJsonContext.Default.ChatMessage);
        Assert.NotNull(InboxJsonContext.Default.ContentPart);
        Assert.NotNull(InboxJsonContext.Default.SessionSnapshot);
        Assert.NotNull(JsonRpcContext.Default.JsonRpcRequest);
        Assert.NotNull(JsonRpcContext.Default.JsonRpcRequestInitializeOptions);
    }

    [Fact]
    public void Request_with_params_uses_context()
    {
        var line = JsonRpc.Request(
            "9",
            "initialize",
            new InitializeOptions { Store = @"D:\data\whatsbox", Connect = false },
            JsonRpcContext.Default.JsonRpcRequestInitializeOptions);

        Assert.DoesNotContain('\n', line);
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal("9", root.GetProperty("id").GetString());
        Assert.Equal("initialize", root.GetProperty("method").GetString());
        var p = root.GetProperty("params");
        Assert.Equal("0.1", p.GetProperty("version").GetString());
        Assert.Equal(@"D:\data\whatsbox", p.GetProperty("store").GetString());
        Assert.False(p.GetProperty("connect").GetBoolean());
        Assert.False(p.TryGetProperty("files", out _));
        Assert.Equal(InitializeOptions.DefaultDeviceName, p.GetProperty("deviceName").GetString());
        Assert.StartsWith("whatsbox on ", p.GetProperty("deviceName").GetString());
    }

    [Fact]
    public void InitializeOptions_sends_custom_device_name()
    {
        var line = JsonRpc.Request(
            "9",
            "initialize",
            new InitializeOptions { Store = @"D:\data\whatsbox", DeviceName = "Lab Box" },
            JsonRpcContext.Default.JsonRpcRequestInitializeOptions);

        using var doc = JsonDocument.Parse(line);
        Assert.Equal("Lab Box", doc.RootElement.GetProperty("params").GetProperty("deviceName").GetString());
    }

    [Fact]
    public void SessionSnapshot_round_trips_through_context()
    {
        var json = """{"status":"new","topics":["$session"],"version":"0.1","product":"whatsapp","identity":"user","capabilities":{"auth":["qr"],"reply":"quote","react":true,"read":"message","ack":true,"files":true,"attachments":"single"}}""";
        var snap = JsonSerializer.Deserialize(json, InboxJsonContext.Default.SessionSnapshot);
        Assert.NotNull(snap);
        Assert.Equal("new", snap.Status);
        Assert.Equal(["$session"], snap.Topics);
        Assert.Equal("0.1", snap.Version);
        Assert.Equal("whatsapp", snap.Product);
        Assert.Equal("user", snap.Identity);
        Assert.NotNull(snap.Capabilities);
        Assert.Equal("quote", snap.Capabilities.Reply);
        Assert.Equal("single", snap.Capabilities.Attachments);

        var back = JsonSerializer.Serialize(snap, InboxJsonContext.Default.SessionSnapshot);
        using var doc = JsonDocument.Parse(back);
        Assert.Equal("new", doc.RootElement.GetProperty("status").GetString());
        Assert.False(doc.RootElement.TryGetProperty("self", out _));
    }
}

