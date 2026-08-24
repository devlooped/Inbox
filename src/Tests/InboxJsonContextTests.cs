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
        Assert.False(p.TryGetProperty("me", out _));
    }

    [Fact]
    public void InitializeOptions_sends_claimed_me_when_set()
    {
        var line = JsonRpc.Request(
            "9",
            "initialize",
            new InitializeOptions { Store = @"D:\data\box", Me = "alice" },
            JsonRpcContext.Default.JsonRpcRequestInitializeOptions);

        using var doc = JsonDocument.Parse(line);
        Assert.Equal("alice", doc.RootElement.GetProperty("params").GetProperty("me").GetString());
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
        var json = """{"status":"new","topics":["$session"],"version":"0.1","product":"whatsapp","identity":"user","capabilities":{"auth":["qr"],"me":"issued","membership":"none","reply":"quote","react":true,"read":"message","ack":true,"files":true,"attachments":"single"}}""";
        var snap = JsonSerializer.Deserialize(json, InboxJsonContext.Default.SessionSnapshot);
        Assert.NotNull(snap);
        Assert.Equal(SessionStatus.New, snap.Status);
        Assert.Equal(["$session"], snap.Topics);
        Assert.Equal("0.1", snap.Version);
        Assert.Equal("whatsapp", snap.Product);
        Assert.Equal(Identity.User, snap.Identity);
        Assert.NotNull(snap.Capabilities);
        Assert.Equal(MeBinding.Issued, snap.Capabilities.Me);
        Assert.Equal(MembershipCapability.None, snap.Capabilities.Membership);
        Assert.Equal(ReplyCapability.Quote, snap.Capabilities.Reply);
        Assert.Equal(ReadCapability.Message, snap.Capabilities.Read);
        Assert.Equal(AttachmentsCapability.Single, snap.Capabilities.Attachments);
        Assert.Equal([AuthKind.Qr], snap.Capabilities.Auth);

        var back = JsonSerializer.Serialize(snap, InboxJsonContext.Default.SessionSnapshot);
        using var doc = JsonDocument.Parse(back);
        Assert.Equal("new", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("0.1", doc.RootElement.GetProperty("version").GetString());
        Assert.Equal("whatsapp", doc.RootElement.GetProperty("product").GetString());
        Assert.Equal("user", doc.RootElement.GetProperty("identity").GetString());
        Assert.False(doc.RootElement.TryGetProperty("self", out _));
        var caps = doc.RootElement.GetProperty("capabilities");
        Assert.Equal("issued", caps.GetProperty("me").GetString());
        Assert.Equal("none", caps.GetProperty("membership").GetString());
        Assert.Equal("quote", caps.GetProperty("reply").GetString());
        Assert.Equal("message", caps.GetProperty("read").GetString());
        Assert.Equal("single", caps.GetProperty("attachments").GetString());
        Assert.Equal("qr", Assert.Single(caps.GetProperty("auth").EnumerateArray()).GetString());
    }

    [Fact]
    public void AuthKind_device_code_stays_snake_case_on_the_wire()
    {
        var caps = new Capabilities { Auth = [AuthKind.DeviceCode, AuthKind.Token] };
        var json = JsonSerializer.Serialize(caps, InboxJsonContext.Default.Capabilities);
        using var doc = JsonDocument.Parse(json);
        var auth = doc.RootElement.GetProperty("auth").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["device_code", "token"], auth);

        var round = JsonSerializer.Deserialize(json, InboxJsonContext.Default.Capabilities);
        Assert.Equal([AuthKind.DeviceCode, AuthKind.Token], round!.Auth);
    }
}

