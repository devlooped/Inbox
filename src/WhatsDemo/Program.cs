using System.Text;
using Inbox;
using WhatsBox;
using WhatsDemo;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

var cwd = Path.GetFullPath(Environment.CurrentDirectory);
var store = Path.GetFullPath(Path.Combine(cwd, ".store"));
Directory.CreateDirectory(store);
var deviceName = DeviceName.Current();
var book = new DirectoryBook();
var session = new DemoSession(book);
var recents = new RecentChats();
var output = new ConsoleLock();
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

InboxClient box;
try
{
    var host = WhatsBoxHost.Start();
    box = new InboxClient(host.StandardOutput, host.StandardInput, host, host.StandardError);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

await using (box)
{
    var sync = DirectorySync.Load(
        book,
        store,
        (id, ct) => box.GetDirectoryAsync(id, icon: false, ct));
    var pump = PumpAsync(box, session, sync, recents, output, cts.Token);
    try
    {
        var snap = await box.InitializeAsync(new InitializeOptions
        {
            Store = store,
            DeviceName = deviceName,
            Connect = true,
            Subscribe = sync.InitialSubscribe,
#if DEBUG
            Verbosity = "debug",
#endif
        }, cts.Token);

        session.NoteIdentity(snap.Me ?? session.Me);
        sync.NoteSelf(session.Me);
        if (session.TryAnnouncePaired())
            output.WriteLine($"{ChatLine.Checkmark} Paired");

        if (DirectoryBook.ChatTopics(snap.Topics).Count > 0)
            sync.ReplaceSubscriptions(snap.Topics);
        await EnsureSubscribedAsync(box, sync, session.Me, cts.Token);
        await sync.WarmAsync(cts.Token);

        await RunReplAsync(box, session, sync, recents, output, cts.Token);
    }
    catch (OperationCanceledException)
    {
        // Ctrl+C
    }
    catch (Exception ex)
    {
        output.WriteLine(ex.Message);
    }
    finally
    {
        cts.Cancel();
        try { await pump.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* shutting down */ }
    }
}

return 0;

static async Task PumpAsync(
    InboxClient box,
    DemoSession session,
    DirectorySync sync,
    RecentChats recents,
    ConsoleLock output,
    CancellationToken cancellation)
{
    try
    {
        await foreach (var ev in box.Events.WithCancellation(cancellation))
        {
            switch (ev)
            {
                case SessionQr qr:
                    output.WriteLine(QrRenderer.Render(qr.Code));
                    break;
                case SessionPaired paired:
                    session.NoteIdentity(paired.Me);
                    sync.NoteSelf(paired.Me);
                    if (session.TryAnnouncePaired())
                        output.WriteLine($"{ChatLine.Checkmark} Paired");
                    try { await EnsureSubscribedAsync(box, sync, paired.Me, cancellation); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        output.WriteLine(ex.Message);
                    }
                    break;
                case SessionOnline online:
                    session.NoteIdentity(online.Me);
                    sync.NoteSelf(online.Me);
                    break;
                case SessionPairError err:
                    output.WriteLine(err.Message);
                    break;
                case SessionLoggedOut loggedOut:
                    session.ClearIdentity();
                    output.WriteLine(loggedOut.Reason is { } reason ? $"logged out: {reason}" : "logged out");
                    break;
                case ChatMessage msg:
                    if (msg.By is not "me")
                        sync.RememberAuthor(msg.By, msg.Handle, msg.ByName);
                    if (msg.By is not "me" && msg.TopicName is { } topicName)
                        sync.RememberAuthor(msg.Topic, null, topicName);
                    recents.Note(msg.Topic, msg.Id, msg.By, msg.Text);
                    try { await sync.ResolveAsync(msg.By, cancellation); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        output.WriteLine(ex.Message);
                    }
                    var line = session.FormatInboundText(
                        msg.Topic, msg.Id, msg.Text, DateTimeOffset.Now, msg.By, msg.Handle, msg.ByName);
                    if (line is null)
                        break;
                    output.WriteLine(line);
                    if (msg is { Id: not null, By: not null })
                    {
                        try
                        {
                            await box.ReadAsync(msg, cancellation);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            output.WriteLine(ex.Message);
                        }
                    }
                    break;
            }
        }
    }
    catch (OperationCanceledException)
    {
        // shutting down
    }
}

static async Task RunReplAsync(
    InboxClient box,
    DemoSession session,
    DirectorySync sync,
    RecentChats recents,
    ConsoleLock output,
    CancellationToken cancellation)
{
    var editor = new LineEditor(
        output,
        input => Completions.Complete(input, sync.Subscribe, session, recents));
    while (!cancellation.IsCancellationRequested)
    {
        string? line;
        try
        {
            line = await editor.ReadLineAsync(cancellation);
        }
        catch (OperationCanceledException)
        {
            break;
        }

        if (line is null)
            break;
        if (string.IsNullOrWhiteSpace(line))
            continue;

        try
        {
            if (line.StartsWith('/'))
            {
                await DispatchAsync(box, session, sync, recents, output, editor, line, cancellation);
                continue;
            }

            if (AtMentions.TryParse(line, out var mention))
            {
                if (!mention.HasText)
                    continue;
                await SendChatAsync(
                    box, session, recents, output,
                    mention.To, mention.Text, recents.ReplyTo(mention.ReplyId), cancellation);
                continue;
            }

            if (session.Me is null)
            {
                output.WriteLine("not paired");
                continue;
            }

            await SendChatAsync(box, session, recents, output, session.Me, line, reply: null, cancellation);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            output.WriteLine(ex.Message);
        }
    }
}

static async Task DispatchAsync(
    InboxClient box,
    DemoSession session,
    DirectorySync sync,
    RecentChats recents,
    ConsoleLock output,
    LineEditor editor,
    string line,
    CancellationToken cancellation)
{
    if (!SlashCommands.TryParse(line, out var name, out var argument))
    {
        output.WriteLine("unknown command");
        return;
    }

    switch (name)
    {
        case "logout":
            {
                var snap = await box.LogoutAsync(cancellation);
                session.ClearIdentity();
                output.WriteLine($"status {snap.Status.ToString().ToLowerInvariant()}");
                break;
            }
        case "disconnect":
            {
                var snap = await box.DisconnectAsync(cancellation);
                output.WriteLine($"status {snap.Status.ToString().ToLowerInvariant()}");
                break;
            }
        case "connect":
            {
                var snap = await box.ConnectAsync(cancellation);
                session.NoteIdentity(snap.Me);
                sync.NoteSelf(snap.Me);
                output.WriteLine($"status {snap.Status.ToString().ToLowerInvariant()}");
                await EnsureSubscribedAsync(box, sync, snap.Me, cancellation);
                break;
            }
        case "subscribe":
            {
                var topic = await ReadTopicAsync(box, editor, output, argument, cancellation);
                if (topic is null)
                    break;
                var result = await box.SubscribeAsync([topic], cancellation);
                var row = await sync.OnSubscribeAsync(topic, result.Topics, cancellation);
                if (row is not null)
                    output.WriteLine(JsonPanel.Render(row, InboxJsonContext.Default.DirectoryRow));
                else
                    output.WriteLine(JsonPanel.Render(result, InboxJsonContext.Default.TopicsResult));
                break;
            }
        case "unsubscribe":
            {
                var topic = await ReadTopicAsync(box, editor, output, argument, cancellation);
                if (topic is null)
                    break;
                if (topic.Equals(session.Me, StringComparison.Ordinal) || session.AuthorLabel(topic) is "me")
                {
                    output.WriteLine("cannot unsubscribe self-chat");
                    break;
                }
                var result = await box.UnsubscribeAsync([topic], cancellation);
                await sync.OnUnsubscribeAsync(topic, result.Topics, cancellation);
                recents.ForgetTopic(topic);
                output.WriteLine(JsonPanel.Render(result, InboxJsonContext.Default.TopicsResult));
                break;
            }
        case "directory":
            {
                var id = await ReadArgumentAsync(editor, argument, cancellation);
                if (id is null)
                    break;
                var row = await box.GetDirectoryAsync(id, icon: false, cancellation);
                sync.Remember(row);
                output.WriteLine(JsonPanel.Render(row, InboxJsonContext.Default.DirectoryRow));
                break;
            }
    }
}

static async Task SendChatAsync(
    InboxClient box,
    DemoSession session,
    RecentChats recents,
    ConsoleLock output,
    string to,
    string text,
    MessageReply? reply,
    CancellationToken cancellation)
{
    session.BeginSend(text);
    try
    {
        var sent = await box.SendAsync(to, text: text, reply: reply, cancellationToken: cancellation);
        session.RememberSent(sent.Id, text);
        recents.Note(sent.Topic, sent.Id, "me", text);
        output.WriteLine(session.FormatOutbound(text, DateTimeOffset.Now, sent.Topic));
    }
    catch
    {
        session.CancelPendingSend(text);
        throw;
    }
}

static async Task EnsureSubscribedAsync(
    InboxClient box,
    DirectorySync sync,
    string? topic,
    CancellationToken cancellation)
{
    if (topic is null || !DirectoryBook.IsLookupId(topic))
        return;
    if (sync.Contains(topic))
    {
        await sync.ResolveAsync(topic, cancellation);
        return;
    }

    var result = await box.SubscribeAsync([topic], cancellation);
    await sync.OnSubscribeAsync(topic, result.Topics, cancellation);
}

static async Task<string?> ReadTopicAsync(
    InboxClient box,
    LineEditor editor,
    ConsoleLock output,
    string argument,
    CancellationToken cancellation)
{
    var query = await ReadArgumentAsync(editor, argument, cancellation);
    if (query is null)
        return null;

    var resolved = await TopicResolver.ResolveAsync(
        query,
        async (q, ct) => (await box.ListDirectoryAsync(new DirectoryListOptions { Query = q }, ct)).Items,
        (items, ct) => editor.PickAsync(items, ct),
        cancellation);

    switch (resolved.Status)
    {
        case TopicResolveStatus.Found:
            return resolved.Topic;
        case TopicResolveStatus.NotFound:
            output.WriteLine("not found");
            return null;
        default:
            return null;
    }
}

static async Task<string?> ReadArgumentAsync(
    LineEditor editor,
    string argument,
    CancellationToken cancellation)
{
    if (!string.IsNullOrWhiteSpace(argument))
        return DirectoryBook.NormalizeTopic(argument);

    while (!cancellation.IsCancellationRequested)
    {
        string? line;
        try
        {
            line = await editor.ReadLineAsync(cancellation);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (line is null)
            return null;
        if (!string.IsNullOrWhiteSpace(line))
            return DirectoryBook.NormalizeTopic(line);
    }

    return null;
}
