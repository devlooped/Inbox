using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace WhatsBox;

/// <summary>
/// Owns native whatsbox stdio: unary JSON-RPC methods as <see cref="Task{TResult}"/>,
/// and a single-consumer pull stream of typed <c>event</c> notifications.
/// </summary>
public sealed class WhatsBoxClient : IDisposable, IAsyncDisposable
{
    const int EventQueueBound = 256;

    readonly Channel<WhatsEvent> events = Channel.CreateBounded<WhatsEvent>(new BoundedChannelOptions(EventQueueBound)
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
    readonly WhatsBoxHost? host;
    readonly Task reader;
    int nextId;
    bool disposed;

    /// <summary>Starts native <c>whatsbox</c> from <see cref="AppContext.BaseDirectory"/>.</summary>
    public WhatsBoxClient() : this(WhatsBoxHost.Start()) { }

    /// <summary>Uses an already-started host (owns and disposes it).</summary>
    public WhatsBoxClient(WhatsBoxHost host) : this(host.StandardOutput, host.StandardInput, host)
    {
        DrainStderr(host.StandardError, lifetime.Token);
    }

    /// <summary>
    /// Uses an existing NDJSON pair: <paramref name="childOutput"/> is the child's stdout,
    /// <paramref name="childInput"/> is the child's stdin.
    /// </summary>
    public WhatsBoxClient(TextReader childOutput, TextWriter childInput)
        : this(childOutput, childInput, host: null) { }

    WhatsBoxClient(TextReader childOutput, TextWriter childInput, WhatsBoxHost? host)
    {
        output = childOutput ?? throw new ArgumentNullException(nameof(childOutput));
        input = childInput ?? throw new ArgumentNullException(nameof(childInput));
        this.host = host;
        reader = Task.Run(() => ReadLoopAsync(lifetime.Token));
    }

    /// <summary>Starts native <c>whatsbox</c> resolved next to <paramref name="baseDirectory"/>.</summary>
    public static WhatsBoxClient Start(string? baseDirectory = null)
        => new(WhatsBoxHost.Start(baseDirectory));

    /// <summary>
    /// Single-consumer pull stream of typed PRODUCT.md §6 events.
    /// Enumerate once. Completes when the child stdout ends or the client is disposed.
    /// </summary>
    public IAsyncEnumerable<WhatsEvent> Events => events.Reader.ReadAllAsync();

    /// <summary>
    /// PRODUCT.md §5.1 <c>initialize</c>. <paramref name="store"/> must be an absolute path.
    /// Uses <see cref="InitializeOptions.DefaultDeviceName"/> as the Linked-devices label.
    /// </summary>
    public Task<SessionSnapshot> InitializeAsync(string store, CancellationToken cancellationToken = default)
        => InitializeAsync(new InitializeOptions { Store = store, Connect = false }, cancellationToken);

    /// <summary>PRODUCT.md §5.1 <c>initialize</c>.</summary>
    public Task<SessionSnapshot> InitializeAsync(InitializeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return InvokeAsync(
            "initialize",
            options,
            JsonRpcContext.Default.JsonRpcRequestInitializeOptions,
            WhatsJsonContext.Default.SessionSnapshot,
            cancellationToken);
    }

    /// <summary>PRODUCT.md §5.2 <c>session.connect</c>.</summary>
    public Task<SessionSnapshot> ConnectAsync(CancellationToken cancellationToken = default)
        => InvokeAsync("session.connect", WhatsJsonContext.Default.SessionSnapshot, cancellationToken);

    /// <summary>PRODUCT.md §5.3 <c>session.pair</c>.</summary>
    public Task<SessionSnapshot> PairAsync(CancellationToken cancellationToken = default)
        => InvokeAsync("session.pair", WhatsJsonContext.Default.SessionSnapshot, cancellationToken);

    /// <summary>PRODUCT.md §5.4 <c>session.disconnect</c>.</summary>
    public Task<SessionSnapshot> DisconnectAsync(CancellationToken cancellationToken = default)
        => InvokeAsync("session.disconnect", WhatsJsonContext.Default.SessionSnapshot, cancellationToken);

    /// <summary>PRODUCT.md §5.5 <c>session.logout</c>.</summary>
    public Task<SessionSnapshot> LogoutAsync(CancellationToken cancellationToken = default)
        => InvokeAsync("session.logout", WhatsJsonContext.Default.SessionSnapshot, cancellationToken);

    /// <summary>PRODUCT.md §5.6 <c>session.status</c>.</summary>
    public Task<SessionSnapshot> StatusAsync(CancellationToken cancellationToken = default)
        => InvokeAsync("session.status", WhatsJsonContext.Default.SessionSnapshot, cancellationToken);

    /// <summary>PRODUCT.md §5.7 <c>subscribe</c>.</summary>
    public Task<TopicsResult> SubscribeAsync(IReadOnlyList<string> topics, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topics);
        return InvokeAsync(
            "subscribe",
            new TopicsParams { Topics = topics },
            JsonRpcContext.Default.JsonRpcRequestTopicsParams,
            WhatsJsonContext.Default.TopicsResult,
            cancellationToken);
    }

    /// <summary>PRODUCT.md §5.7 <c>unsubscribe</c>.</summary>
    public Task<TopicsResult> UnsubscribeAsync(IReadOnlyList<string> topics, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topics);
        return InvokeAsync(
            "unsubscribe",
            new TopicsParams { Topics = topics },
            JsonRpcContext.Default.JsonRpcRequestTopicsParams,
            WhatsJsonContext.Default.TopicsResult,
            cancellationToken);
    }

    /// <summary>PRODUCT.md §5.8 <c>directory.list</c>.</summary>
    public Task<DirectoryListResult> ListDirectoryAsync(DirectoryListOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new DirectoryListOptions();
        return InvokeAsync(
            "directory.list",
            options,
            JsonRpcContext.Default.JsonRpcRequestDirectoryListOptions,
            WhatsJsonContext.Default.DirectoryListResult,
            cancellationToken);
    }

    /// <summary>PRODUCT.md §5.9 <c>directory.get</c>.</summary>
    public Task<DirectoryRow> GetDirectoryAsync(string id, CancellationToken cancellationToken = default)
        => GetDirectoryAsync(id, icon: null, cancellationToken);

    /// <summary>PRODUCT.md §5.9 <c>directory.get</c> with an explicit <paramref name="icon"/> fetch flag.</summary>
    public Task<DirectoryRow> GetDirectoryAsync(string id, bool? icon, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return InvokeAsync(
            "directory.get",
            new DirectoryGetParams { Id = id, Icon = icon },
            JsonRpcContext.Default.JsonRpcRequestDirectoryGetParams,
            WhatsJsonContext.Default.DirectoryRow,
            cancellationToken);
    }

    /// <summary>PRODUCT.md §5.10 <c>messages.send</c>.</summary>
    public Task<SendResult> SendAsync(
        string to,
        string? text = null,
        string? path = null,
        MessageReply? reply = null,
        MessageReact? react = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        return InvokeAsync(
            "messages.send",
            new MessagesSendParams { To = to, Text = text, Path = path, Reply = reply, React = react },
            JsonRpcContext.Default.JsonRpcRequestMessagesSendParams,
            WhatsJsonContext.Default.SendResult,
            cancellationToken);
    }

    /// <summary>PRODUCT.md §5.11 <c>messages.read</c>.</summary>
    public Task<ReadResult> ReadAsync(string to, IReadOnlyList<string> ids, string? by = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        ArgumentNullException.ThrowIfNull(ids);
        return InvokeAsync(
            "messages.read",
            new MessagesReadParams { To = to, Ids = ids, By = by },
            JsonRpcContext.Default.JsonRpcRequestMessagesReadParams,
            WhatsJsonContext.Default.ReadResult,
            cancellationToken);
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
            throw new ObjectDisposedException(nameof(WhatsBoxClient));
        }

        if (response.Error is { } err)
            throw new WhatsRpcException(err.Code, err.Message, err.Data);

        if (response.Result is not { } result)
            throw new WhatsRpcException(-32603, "invalid_params", null);

        return result.Deserialize(resultType)
            ?? throw new WhatsRpcException(-32603, "invalid_params", null);
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
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        lifetime.Cancel();
        try { await input.FlushAsync().ConfigureAwait(false); } catch { /* closing */ }
        if (host is not null)
            await host.DisposeAsync().ConfigureAwait(false);
        try { await reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* shutting down */ }
        events.Writer.TryComplete();
        writeLock.Dispose();
        lifetime.Dispose();
    }
}
