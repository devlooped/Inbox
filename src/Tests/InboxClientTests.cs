using System.Text.Json;
using Inbox;
using WhatsBox;

namespace Tests;

public class InboxClientTests
{
    [Fact]
    public async Task Events_maps_session_qr_and_chat_text()
    {
        var stdout = new LineSource();
        var stdin = new LineSink();
        await using var client = new InboxClient(stdout, stdin);

        var seen = CollectEvents(client);

        stdout.WriteLine("""{"jsonrpc":"2.0","method":"event","params":{"topic":"$session","kind":"qr","code":"2@fixture"}}""");
        stdout.WriteLine("""{"jsonrpc":"2.0","method":"event","params":{"topic":"999@lid","kind":"message","id":"3EB0","by":"999@lid","contents":[{"type":"text","text":"hi"}]}}""");
        stdout.WriteLine("""{"jsonrpc":"2.0","method":"event","params":{"topic":"999@lid","kind":"message","id":"3EB1","by":"999@lid","contents":[{"type":"image","path":"in/p.jpg"},{"type":"text","text":"pic"}]}}""");
        stdout.Complete();

        var collected = await seen.WaitAsync(TimeSpan.FromSeconds(5));

        var qr = Assert.Single(collected.OfType<SessionQr>());
        Assert.Equal("2@fixture", qr.Code);
        var messages = collected.OfType<ChatMessage>().ToList();
        Assert.Equal(2, messages.Count);
        var text = messages[0];
        Assert.Equal("999@lid", text.Topic);
        Assert.Equal("3EB0", text.Id);
        Assert.Equal("999@lid", text.By);
        Assert.Equal("hi", text.Text);
        var image = messages[1];
        Assert.Equal("message", image.Kind);
        var media = Assert.IsType<ImagePart>(image.Contents[0]);
        Assert.Equal("in/p.jpg", media.Path);
        Assert.Equal("pic", image.Text);
    }

    [Fact]
    public async Task Events_ignore_non_protocol_lines()
    {
        var stdout = new LineSource();
        var stdin = new LineSink();
        await using var client = new InboxClient(stdout, stdin);

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
        await using var client = new InboxClient(stdout, stdin);

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
        stdout.WriteLine(RpcResult(id!));

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
        await using var client = new InboxClient(stdout, stdin);

        var task = client.StatusAsync();
        var request = await stdin.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var req = JsonDocument.Parse(request);
        var id = req.RootElement.GetProperty("id").GetString();

        stdout.WriteLine(RpcError(id!, -32001, "not_initialized"));

        var ex = await Assert.ThrowsAsync<InboxRpcException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(-32001, ex.Code);
        Assert.Equal("not_initialized", ex.Token);
    }

    [Fact]
    public async Task GetDirectory_omits_icon_when_null_and_writes_when_set()
    {
        var stdout = new LineSource();
        var stdin = new LineSink();
        await using var client = new InboxClient(stdout, stdin);

        var omitted = client.GetDirectoryAsync("999@lid");
        var line1 = await stdin.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using (var req = JsonDocument.Parse(line1))
        {
            Assert.Equal("directory.get", req.RootElement.GetProperty("method").GetString());
            var p = req.RootElement.GetProperty("params");
            Assert.Equal("999@lid", p.GetProperty("id").GetString());
            Assert.False(p.TryGetProperty("icon", out _));
            var id = req.RootElement.GetProperty("id").GetString();
            stdout.WriteLine($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":{{\"topic\":\"999@lid\",\"kind\":\"user\"}}}}");
        }
        var row = await omitted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("999@lid", row.Topic);

        var withIcon = client.GetDirectoryAsync("999@lid", icon: false);
        var line2 = await stdin.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using (var req = JsonDocument.Parse(line2))
        {
            var p = req.RootElement.GetProperty("params");
            Assert.False(p.GetProperty("icon").GetBoolean());
            var id = req.RootElement.GetProperty("id").GetString();
            stdout.WriteLine($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":{{\"topic\":\"999@lid\",\"kind\":\"user\",\"handle\":\"@ada\"}}}}");
        }
        var row2 = await withIcon.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("@ada", row2.Handle);
        stdout.Complete();
    }

    [Fact]
    public async Task Dispose_stops_native_child()
    {
        var host = WhatsBoxHost.Start();
        var pid = host.ProcessId;
        Assert.True(pid > 0);
        Assert.True(IsProcessRunning(pid));

        await using (var client = new InboxClient(host.StandardOutput, host.StandardInput, host, host.StandardError))
        {
            var store = Path.Combine(Path.GetTempPath(), "whatsbox-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(store);
            try
            {
                var snap = await client.InitializeAsync(store).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal("new", snap.Status);
            }
            finally
            {
                try { Directory.Delete(store, recursive: true); } catch { /* best effort */ }
            }
        }

        Assert.False(IsProcessRunning(pid));
        await Assert.ThrowsAnyAsync<Exception>(() => host.StandardInput.WriteLineAsync("{}"));
    }

    [Fact]
    public async Task Send_and_react_write_contents_parts()
    {
        var stdout = new LineSource();
        var stdin = new LineSink();
        await using var client = new InboxClient(stdout, stdin);

        var send = client.SendAsync("999@lid", text: "hello", reply: new MessageReply("t1", "me", "orig"));
        var line1 = await stdin.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using (var req = JsonDocument.Parse(line1))
        {
            Assert.Equal("messages.send", req.RootElement.GetProperty("method").GetString());
            var p = req.RootElement.GetProperty("params");
            Assert.Equal("999@lid", p.GetProperty("to").GetString());
            var part = Assert.Single(p.GetProperty("contents").EnumerateArray());
            Assert.Equal("text", part.GetProperty("type").GetString());
            Assert.Equal("hello", part.GetProperty("text").GetString());
            Assert.Equal("t1", p.GetProperty("reply").GetProperty("id").GetString());
            Assert.False(p.TryGetProperty("text", out _));
            Assert.False(p.TryGetProperty("path", out _));
            Assert.False(p.TryGetProperty("react", out _));
            stdout.WriteLine($"{{\"jsonrpc\":\"2.0\",\"id\":\"{req.RootElement.GetProperty("id").GetString()}\",\"result\":{{\"id\":\"m1\",\"topic\":\"999@lid\"}}}}");
        }
        Assert.Equal("m1", (await send.WaitAsync(TimeSpan.FromSeconds(5))).Id);

        var react = client.ReactAsync("999@lid", "t1", "999@lid", "👍");
        var line2 = await stdin.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using (var req = JsonDocument.Parse(line2))
        {
            var p = req.RootElement.GetProperty("params");
            var part = Assert.Single(p.GetProperty("contents").EnumerateArray());
            Assert.Equal("reaction", part.GetProperty("type").GetString());
            Assert.Equal("t1", part.GetProperty("target").GetString());
            Assert.Equal("👍", part.GetProperty("emoji").GetString());
            stdout.WriteLine($"{{\"jsonrpc\":\"2.0\",\"id\":\"{req.RootElement.GetProperty("id").GetString()}\",\"result\":{{\"id\":\"r1\",\"topic\":\"999@lid\"}}}}");
        }
        Assert.Equal("r1", (await react.WaitAsync(TimeSpan.FromSeconds(5))).Id);

        var read = client.ReadAsync(new ChatMessage { Topic = "999@lid", Id = "3EB0", By = "999@lid" });
        var line3 = await stdin.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using (var req = JsonDocument.Parse(line3))
        {
            Assert.Equal("messages.read", req.RootElement.GetProperty("method").GetString());
            var p = req.RootElement.GetProperty("params");
            Assert.Equal("999@lid", p.GetProperty("to").GetString());
            Assert.Equal("3EB0", Assert.Single(p.GetProperty("ids").EnumerateArray()).GetString());
            Assert.Equal("999@lid", p.GetProperty("by").GetString());
            stdout.WriteLine($"{{\"jsonrpc\":\"2.0\",\"id\":\"{req.RootElement.GetProperty("id").GetString()}\",\"result\":{{\"topic\":\"999@lid\"}}}}");
        }
        Assert.Equal("999@lid", (await read.WaitAsync(TimeSpan.FromSeconds(5))).Topic);
        stdout.Complete();
    }

    [Fact]
    public async Task Initialize_writes_default_device_name()
    {
        var stdout = new LineSource();
        var stdin = new LineSink();
        await using var client = new InboxClient(stdout, stdin);

        var task = client.InitializeAsync(@"D:\data\whatsbox");
        var line = await stdin.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var req = JsonDocument.Parse(line);
        Assert.Equal("initialize", req.RootElement.GetProperty("method").GetString());
        var p = req.RootElement.GetProperty("params");
        Assert.Equal(InitializeOptions.DefaultDeviceName, p.GetProperty("deviceName").GetString());
        var id = req.RootElement.GetProperty("id").GetString();
        stdout.WriteLine(RpcResult(id!));

        var snap = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("new", snap.Status);
        stdout.Complete();
    }

    [Fact]
    public async Task Initialize_fresh_store_status_new()
    {
        var store = Path.Combine(Path.GetTempPath(), "whatsbox-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(store);
        try
        {
            var host = WhatsBoxHost.Start();
            await using var client = new InboxClient(host.StandardOutput, host.StandardInput, host, host.StandardError);
            var snap = await client.InitializeAsync(store).WaitAsync(TimeSpan.FromSeconds(30));
            Console.WriteLine($"initialize status={snap.Status}");
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

    static Task<List<InboxEvent>> CollectEvents(InboxClient client)
        => Task.Run(async () =>
        {
            var list = new List<InboxEvent>();
            await foreach (var ev in client.Events)
                list.Add(ev);
            return list;
        });

    static string RpcResult(string id)
        => $"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":{{\"status\":\"new\",\"topics\":[\"$session\"]}}}}";

    static string RpcError(string id, int code, string token)
        => $"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"error\":{{\"code\":{code},\"message\":\"{token}\"}}}}";

    static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

