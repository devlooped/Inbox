using System.Text.Json;
using WhatsBox;

namespace Tests;

public class WhatsJsonContextTests
{
    [Fact]
    public void Default_options_use_source_generated_resolver()
    {
        Assert.Same(WhatsJsonContext.Default, JsonRpc.SerializerOptions.TypeInfoResolver);
        Assert.NotNull(WhatsJsonContext.Default.ChatMessage);
        Assert.NotNull(WhatsJsonContext.Default.SessionSnapshot);
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
        var json = """{"status":"new","topics":["$session"],"version":"0.1"}""";
        var snap = JsonSerializer.Deserialize(json, WhatsJsonContext.Default.SessionSnapshot);
        Assert.NotNull(snap);
        Assert.Equal("new", snap.Status);
        Assert.Equal(["$session"], snap.Topics);
        Assert.Equal("0.1", snap.Version);

        var back = JsonSerializer.Serialize(snap, WhatsJsonContext.Default.SessionSnapshot);
        using var doc = JsonDocument.Parse(back);
        Assert.Equal("new", doc.RootElement.GetProperty("status").GetString());
        Assert.False(doc.RootElement.TryGetProperty("self", out _));
    }
}
