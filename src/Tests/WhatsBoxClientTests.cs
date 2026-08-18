using System.Text.Json;
using WhatsBox;

namespace Tests;

public class WhatsBoxClientTests
{
    [Fact]
    public async Task Events_maps_session_qr_and_chat_text()
    {
        var stdout = new LineSource();
        var stdin = new LineSink();
        await using var client = new WhatsBoxClient(stdout, stdin);

        var seen = CollectEvents(client);

        stdout.WriteLine("""{"jsonrpc":"2.0","method":"event","params":{"topic":"$session","kind":"qr","code":"2@fixture"}}""");
        stdout.WriteLine("""{"jsonrpc":"2.0","method":"event","params":{"topic":"999@lid","kind":"text","id":"3EB0","by":"999@lid","text":"hi"}}""");
        stdout.Complete();

        var collected = await seen.WaitAsync(TimeSpan.FromSeconds(5));

        var qr = Assert.Single(collected.OfType<SessionQr>());
        Assert.Equal("2@fixture", qr.Code);
        var text = Assert.Single(collected.OfType<ChatText>());
        Assert.Equal("999@lid", text.Topic);
        Assert.Equal("3EB0", text.Id);
        Assert.Equal("999@lid", text.By);
        Assert.Equal("hi", text.Text);
    }

    [Fact]
    public async Task Events_ignore_non_protocol_lines()
    {
        var stdout = new LineSource();
        var stdin = new LineSink();
        await using var client = new WhatsBoxClient(stdout, stdin);

        var seen = CollectEvents(client);

        stdout.WriteLine("not json");
        stdout.WriteLine("warn: stderr-shaped log line");
        stdout.WriteLine("");
        stdout.WriteLine("""{"jsonrpc":"2.0","method":"event","params":{"topic":"$session","kind":"qr","code":"after-noise"}}""");
        stdout.Complete();

        var collected = await seen.WaitAsync(TimeSpan.FromSeconds(5));
        var qr = Assert.Single(collected);
        var sessionQr = Assert.IsType<SessionQr>(qr);
        Assert.Equal("after-noise", sessionQr.Code);
    }

    [Fact]
    public async Task Demuxes_event_and_rpc_reply_on_the_same_pipe()
    {
        var stdout = new LineSource();
        var stdin = new LineSink();
        await using var client = new WhatsBoxClient(stdout, stdin);

        var qrSeen = new TaskCompletionSource<SessionQr>(TaskCreationOptions.RunContinuationsAsynchronously);
        var collect = Task.Run(async () =>
        {
            await foreach (var ev in client.Events)
            {
                if (ev is SessionQr qr)
                {
                    qrSeen.TrySetResult(qr);
                    break;
                }
            }
        });

        var statusTask = client.StatusAsync();
        var request = await stdin.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var req = JsonDocument.Parse(request);
        Assert.Equal("session.status", req.RootElement.GetProperty("method").GetString());
        var id = req.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(id));

        stdout.WriteLine("""{"jsonrpc":"2.0","method":"event","params":{"topic":"$session","kind":"qr","code":"2@live"}}""");
        stdout.WriteLine(RpcResult(id!, new { status = "new", topics = new[] { "$session" } }));

        var qr = await qrSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("2@live", qr.Code);

        var status = await statusTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("new", status.Status);
        Assert.Contains("$session", status.Topics);

        stdout.Complete();
        await collect.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Rpc_error_throws_with_wire_code_and_token()
    {
        var stdout = new LineSource();
        var stdin = new LineSink();
        await using var client = new WhatsBoxClient(stdout, stdin);

        var task = client.StatusAsync();
        var request = await stdin.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var req = JsonDocument.Parse(request);
        var id = req.RootElement.GetProperty("id").GetString();

        stdout.WriteLine(RpcError(id!, -32001, "not_initialized"));

        var ex = await Assert.ThrowsAsync<WhatsRpcException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(-32001, ex.Code);
        Assert.Equal("not_initialized", ex.Token);
    }

    [Fact]
    public async Task Initialize_fresh_store_status_new()
    {
        var store = Path.Combine(Path.GetTempPath(), "whatsbox-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(store);
        try
        {
            await using var client = WhatsBoxClient.Start();
            var snap = await client.InitializeAsync(store).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("new", snap.Status);
            Assert.Contains("$session", snap.Topics);

            var status = await client.StatusAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("new", status.Status);
        }
        finally
        {
            try { Directory.Delete(store, recursive: true); } catch { /* best effort */ }
        }
    }

    static Task<List<WhatsEvent>> CollectEvents(WhatsBoxClient client)
        => Task.Run(async () =>
        {
            var list = new List<WhatsEvent>();
            await foreach (var ev in client.Events)
                list.Add(ev);
            return list;
        });

    static string RpcResult(string id, object result)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result,
        });

    static string RpcError(string id, int code, string token)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new { code, message = token },
        });
}
