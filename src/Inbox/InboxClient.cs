using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace Inbox;

/// <summary>
/// Reference client for an Inbox Protocol-implementation CLI: unary JSON-RPC
/// methods as <see cref="Task{TResult}"/>, and a single-consumer pull stream
/// of typed <c>event</c> notifications over NDJSON stdio.
/// </summary>
public class InboxClient : IDisposable, IAsyncDisposable
{
    const int EventQueueBound = 256;

    readonly Channel<InboxEvent> events = Channel.CreateBounded<InboxEvent>(new BoundedChannelOptions(EventQueueBound)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false,
    });
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonRpcMessage>> pending = new();
    readonly TextReader output;
    readonly TextWriter input;
    readonly SemaphoreSlim writeLock = new(1, 1);
    readonly CancellationTokenSource lifetime = new();
    readonly IAsyncDisposable? ownedProcess;
    readonly Task reader;
    int nextId;
    bool disposed;

    /// <summary>
    /// Uses an existing NDJSON pair: <paramref name="childOutput"/> is the child's stdout,
    /// <paramref name="childInput"/> is the child's stdin.
    /// </summary>
    public InboxClient(TextReader childOutput, TextWriter childInput)
        : this(childOutput, childInput, ownedProcess: null, stderr: null) { }

    /// <summary>
    /// Uses an existing NDJSON pair and optionally owns a child process whose
    /// lifetime is tied to this client. <paramref name="stderr"/> is logs only
    /// (never protocol) and is drained when provided.
    /// </summary>
    public InboxClient(
        TextReader childOutput,
        TextWriter childInput,
        IAsyncDisposable? ownedProcess,
        TextReader? stderr = null)
    {
        output = childOutput ?? throw new ArgumentNullException(nameof(childOutput));
        input = childInput ?? throw new ArgumentNullException(nameof(childInput));
        this.ownedProcess = ownedProcess;
        reader = Task.Run(() => ReadLoopAsync(lifetime.Token));
        if (stderr is not null)
            DrainStderr(stderr, lifetime.Token);
    }

    /// <summary>
    /// Single-consumer pull stream of typed Inbox Protocol <c>event</c> notifications.
    /// Enumerate once. Completes when the child stdout ends or the client is disposed.
    /// </summary>
    public IAsyncEnumerable<InboxEvent> Events => events.Reader.ReadAllAsync();

    /// <summary>
    /// Inbox Protocol <c>initialize</c>. <paramref name="store"/> must be an absolute path.
    /// Uses <see cref="InitializeOptions.DefaultDeviceName"/> as the Linked-devices label.
    /// </summary>
    public Task<SessionSnapshot> InitializeAsync(string store, CancellationToken cancellationToken = default)
        => InitializeAsync(new InitializeOptions { Store = store, Connect = false }, cancellationToken);

    /// <summary>Inbox Protocol <c>initialize</c>.</summary>
    public Task<SessionSnapshot> InitializeAsync(InitializeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return InvokeAsync(
            "initialize",
            options,
            JsonRpcContext.Default.JsonRpcRequestInitializeOptions,
            InboxJsonContext.Default.SessionSnapshot,
            cancellationToken);
    }

    /// <summary>Inbox Protocol <c>session.connect</c>.</summary>
    public Task<SessionSnapshot> ConnectAsync(CancellationToken cancellationToken = default)
        => InvokeAsync("session.connect", InboxJsonContext.Default.SessionSnapshot, cancellationToken);

    /// <summary>Inbox Protocol <c>session.pair</c>.</summary>
    public Task<SessionSnapshot> PairAsync(CancellationToken cancellationToken = default)
        => InvokeAsync("session.pair", InboxJsonContext.Default.SessionSnapshot, cancellationToken);

    /// <summary>Inbox Protocol <c>session.disconnect</c>.</summary>
    public Task<SessionSnapshot> DisconnectAsync(CancellationToken cancellationToken = default)
        => InvokeAsync("session.disconnect", InboxJsonContext.Default.SessionSnapshot, cancellationToken);

    /// <summary>Inbox Protocol <c>session.logout</c>.</summary>
    public Task<SessionSnapshot> LogoutAsync(CancellationToken cancellationToken = default)
        => InvokeAsync("session.logout", InboxJsonContext.Default.SessionSnapshot, cancellationToken);

    /// <summary>Inbox Protocol <c>session.status</c>.</summary>
    public Task<SessionSnapshot> StatusAsync(CancellationToken cancellationToken = default)
        => InvokeAsync("session.status", InboxJsonContext.Default.SessionSnapshot, cancellationToken);

    /// <summary>Inbox Protocol <c>subscribe</c>.</summary>
    public Task<TopicsResult> SubscribeAsync(IReadOnlyList<string> topics, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topics);
        return InvokeAsync(
            "subscribe",
            new TopicsParams { Topics = topics },
            JsonRpcContext.Default.JsonRpcRequestTopicsParams,
            InboxJsonContext.Default.TopicsResult,
            cancellationToken);
    }

    /// <summary>Inbox Protocol <c>unsubscribe</c>.</summary>
    public Task<TopicsResult> UnsubscribeAsync(IReadOnlyList<string> topics, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topics);
        return InvokeAsync(
            "unsubscribe",
            new TopicsParams { Topics = topics },
            JsonRpcContext.Default.JsonRpcRequestTopicsParams,
            InboxJsonContext.Default.TopicsResult,
            cancellationToken);
    }

    /// <summary>Inbox Protocol <c>directory.list</c>.</summary>
    public Task<DirectoryListResult> ListDirectoryAsync(DirectoryListOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new DirectoryListOptions();
        return InvokeAsync(
            "directory.list",
            options,
            JsonRpcContext.Default.JsonRpcRequestDirectoryListOptions,
            InboxJsonContext.Default.DirectoryListResult,
            cancellationToken);
    }

    /// <summary>Inbox Protocol <c>directory.get</c>.</summary>
    public Task<DirectoryRow> GetDirectoryAsync(string id, CancellationToken cancellationToken = default)
        => GetDirectoryAsync(id, icon: null, cancellationToken);

    /// <summary>Inbox Protocol <c>directory.get</c> with an explicit <paramref name="icon"/> fetch flag.</summary>
    public Task<DirectoryRow> GetDirectoryAsync(string id, bool? icon, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return InvokeAsync(
            "directory.get",
            new DirectoryGetParams { Id = id, Icon = icon },
            JsonRpcContext.Default.JsonRpcRequestDirectoryGetParams,
            InboxJsonContext.Default.DirectoryRow,
            cancellationToken);
    }

    /// <summary>Inbox Protocol <c>messages.send</c>.</summary>
    public Task<SendResult> SendAsync(
        string to,
        IReadOnlyList<ContentPart> contents,
        MessageReply? reply = null,
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        ArgumentNullException.ThrowIfNull(contents);
        return InvokeAsync(
            "messages.send",
            new MessagesSendParams { To = to, Contents = contents, Reply = reply, Context = context },
            JsonRpcContext.Default.JsonRpcRequestMessagesSendParams,
            InboxJsonContext.Default.SendResult,
            cancellationToken);
    }

    /// <summary>Inbox Protocol <c>messages.send</c> with a single text part.</summary>
    public Task<SendResult> SendAsync(
        string to,
        string text,
        MessageReply? reply = null,
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return SendAsync(to, [new TextPart { Text = text }], reply, context, cancellationToken);
    }

    /// <summary>Inbox Protocol <c>messages.send</c> with a single <see cref="ReactionPart"/>.</summary>
    public Task<SendResult> ReactAsync(
        string to,
        string target,
        string by,
        string emoji,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        ArgumentNullException.ThrowIfNull(emoji);
        return SendAsync(
            to,
            [new ReactionPart { Target = target, By = by, Emoji = emoji }],
            cancellationToken: cancellationToken);
    }

    /// <summary>Inbox Protocol <c>messages.read</c>. <paramref name="by"/> is always required; 1:1 implementations may ignore it.</summary>
    public Task<ReadResult> ReadAsync(string to, IReadOnlyList<string> ids, string by, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        ArgumentNullException.ThrowIfNull(ids);
        return InvokeAsync(
            "messages.read",
            new MessagesReadParams { To = to, Ids = ids, By = by },
            JsonRpcContext.Default.JsonRpcRequestMessagesReadParams,
            InboxJsonContext.Default.ReadResult,
            cancellationToken);
    }

    /// <summary>Inbox Protocol <c>messages.read</c> using <see cref="ChatEvent.Topic"/>, <see cref="ChatEvent.Id"/>, and <see cref="ChatEvent.By"/>.</summary>
    public Task<ReadResult> ReadAsync(ChatEvent message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.By);
        return ReadAsync(message.Topic, [message.Id], message.By, cancellationToken);
    }

    Task<TResult> InvokeAsync<TResult>(string method, JsonTypeInfo<TResult> resultType, CancellationToken cancellation)
        => InvokeCoreAsync(JsonRpc.Request(NextId(out var id), method), id, resultType, cancellation);

    Task<TResult> InvokeAsync<TResult, TParams>(
        string method,
        TParams @params,
        JsonTypeInfo<JsonRpcRequest<TParams>> requestType,
        JsonTypeInfo<TResult> resultType,
        CancellationToken cancellation)
        => InvokeCoreAsync(JsonRpc.Request(NextId(out var id), method, @params, requestType), id, resultType, cancellation);

    string NextId(out string id)
    {
        id = Interlocked.Increment(ref nextId).ToString();
        return id;
    }

    async Task<TResult> InvokeCoreAsync<TResult>(
        string line,
        string id,
        JsonTypeInfo<TResult> resultType,
        CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var tcs = new TaskCompletionSource<JsonRpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        await using var reg = linked.Token.Register(() =>
        {
            if (pending.TryRemove(id, out var pendingTcs))
                pendingTcs.TrySetCanceled(linked.Token);
        });

        await writeLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await input.WriteLineAsync(line).ConfigureAwait(false);
            await input.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }

        JsonRpcMessage response;
        try
        {
            response = await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(InboxClient));
        }

        if (response.Error is { } err)
            throw new InboxRpcException(err.Code, err.Message, err.Data);

        if (response.Result is not { } result)
            throw new InboxRpcException(-32603, "invalid_params", null);

        return result.Deserialize(resultType)
            ?? throw new InboxRpcException(-32603, "invalid_params", null);
    }

    async Task ReadLoopAsync(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await output.ReadLineAsync(cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }

                if (line is null)
                    break;
                await DispatchLineAsync(line, cancellation).ConfigureAwait(false);
            }
        }
        finally
        {
            events.Writer.TryComplete();
            foreach (var id in pending.Keys)
            {
                if (pending.TryRemove(id, out var tcs))
                    tcs.TrySetCanceled(cancellation);
            }
        }
    }

    async ValueTask DispatchLineAsync(string line, CancellationToken cancellation)
    {
        if (!JsonRpc.TryParse(line, out var message))
            return;

        if (message.IsEvent && message.EventParams is { } p)
        {
            if (EventMapper.TryMap(p) is { } ev)
                await events.Writer.WriteAsync(ev, cancellation).ConfigureAwait(false);
            return;
        }

        if (message.IsResponse && message.Id is { } id && pending.TryRemove(id, out var tcs))
            tcs.TrySetResult(message);
    }

    static void DrainStderr(TextReader stderr, CancellationToken cancellation)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var line = await stderr.ReadLineAsync(cancellation).ConfigureAwait(false);
                    if (line is null)
                        break;
                    Debug.WriteLine(line);
                    Console.Error.WriteLine(line);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
        }, CancellationToken.None);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        lifetime.Cancel();
        try { await input.FlushAsync().ConfigureAwait(false); } catch { /* closing */ }
        if (ownedProcess is not null)
            await ownedProcess.DisposeAsync().ConfigureAwait(false);
        try { await reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* shutting down */ }
        events.Writer.TryComplete();
        writeLock.Dispose();
        lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
