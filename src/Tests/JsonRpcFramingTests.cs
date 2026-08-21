using Inbox;

namespace Tests;

public class JsonRpcFramingTests
{
    [Fact]
    public void Parses_event_notification()
    {
        const string line = """{"jsonrpc":"2.0","method":"event","params":{"topic":"$session","kind":"qr","code":"2@fixture"}}""";
        Assert.True(JsonRpc.TryParse(line, out var msg));
        Assert.True(msg.IsEvent);
        Assert.Equal("event", msg.Method);
        Assert.Equal("2@fixture", msg.EventParams!.Value.GetProperty("code").GetString());
    }

    [Fact]
    public void Parses_result_response()
    {
        const string line = """{"jsonrpc":"2.0","id":"7","result":{"status":"new","topics":["$session"]}}""";
        Assert.True(JsonRpc.TryParse(line, out var msg));
        Assert.True(msg.IsResponse);
        Assert.Equal("7", msg.Id);
        Assert.Equal("new", msg.Result!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public void Parses_error_response()
    {
        const string line = """{"jsonrpc":"2.0","id":"1","error":{"code":-32001,"message":"not_initialized"}}""";
        Assert.True(JsonRpc.TryParse(line, out var msg));
        Assert.True(msg.IsResponse);
        Assert.Equal(-32001, msg.Error!.Value.Code);
        Assert.Equal("not_initialized", msg.Error.Value.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("warn: stderr-shaped log line")]
    [InlineData("""{"method":"event"}""")]
    [InlineData("""{"jsonrpc":"1.0","method":"event"}""")]
    public void Ignores_non_protocol_lines(string line)
        => Assert.False(JsonRpc.TryParse(line, out _));

    [Fact]
    public void Request_is_compact_ndjson_without_null_params()
    {
        var line = JsonRpc.Request("3", "session.status");
        Assert.DoesNotContain('\n', line);
        Assert.Contains("\"jsonrpc\":\"2.0\"", line);
        Assert.Contains("\"id\":\"3\"", line);
        Assert.Contains("\"method\":\"session.status\"", line);
        Assert.DoesNotContain("params", line);
    }
}

