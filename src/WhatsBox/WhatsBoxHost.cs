using System.IO.Pipelines;
using System.Text;
using CliWrap;

namespace WhatsBox;

/// <summary>
/// Starts and owns the native <c>whatsbox</c> child process.
/// The executable is resolved next to the app base directory, never <see cref="Directory.GetCurrentDirectory"/>.
/// </summary>
public sealed class WhatsBoxHost : IDisposable, IAsyncDisposable
{
    static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    readonly CommandTask<CommandResult> execution;
    readonly CancellationTokenSource forceful;
    bool disposed;

    WhatsBoxHost(
        CommandTask<CommandResult> execution,
        CancellationTokenSource forceful,
        TextWriter input,
        TextReader output,
        TextReader error)
    {
        this.execution = execution;
        this.forceful = forceful;
        StandardInput = input;
        StandardOutput = output;
        StandardError = error;
        ProcessId = execution.ProcessId;
    }

    /// <summary>Child stdin (JSON-RPC requests).</summary>
    public TextWriter StandardInput { get; }

    /// <summary>Child stdout (JSON-RPC responses and <c>event</c> notifications).</summary>
    public TextReader StandardOutput { get; }

    /// <summary>Child stderr (logs only; never protocol).</summary>
    public TextReader StandardError { get; }

    /// <summary>Absolute path of the running binary.</summary>
    public string ExecutablePath { get; private init; } = "";

    /// <summary>Operating-system process id of the native child.</summary>
    internal int ProcessId { get; }

    /// <summary>
    /// Resolves <c>whatsbox</c> / <c>whatsbox.exe</c> next to <paramref name="baseDirectory"/>
    /// (defaults to <see cref="AppContext.BaseDirectory"/>).
    /// </summary>
    public static string ResolveBinaryPath(string? baseDirectory = null)
    {
        var dir = baseDirectory ?? AppContext.BaseDirectory;
        var name = OperatingSystem.IsWindows() ? "whatsbox.exe" : "whatsbox";
        var path = Path.GetFullPath(Path.Combine(dir, name));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Native whatsbox was not found next to the app base directory '{dir}'.",
                path);
        }
        return path;
    }

    /// <summary>Starts the native binary from the app base directory.</summary>
    public static WhatsBoxHost Start(string? baseDirectory = null)
    {
        var path = ResolveBinaryPath(baseDirectory);
        var stdin = new Pipe();
        var stdout = new Pipe();
        var stderr = new Pipe();
        var forceful = new CancellationTokenSource();

        var execution = Cli.Wrap(path)
            .WithWorkingDirectory(Path.GetDirectoryName(path)!)
            .WithValidation(CommandResultValidation.None)
            .WithStandardInputPipe(PipeSource.FromStream(stdin.Reader.AsStream()))
            .WithStandardOutputPipe(CopyThenComplete(stdout.Writer))
            .WithStandardErrorPipe(CopyThenComplete(stderr.Writer))
            .ExecuteAsync(forceful.Token);

        return new WhatsBoxHost(
            execution,
            forceful,
            new StreamWriter(stdin.Writer.AsStream(), Utf8NoBom) { AutoFlush = true },
            new StreamReader(stdout.Reader.AsStream(), Encoding.UTF8),
            new StreamReader(stderr.Reader.AsStream(), Encoding.UTF8))
        {
            ExecutablePath = path,
        };
    }

    /// <summary>
    /// Incremental copy of child stdout/stderr into a <see cref="Pipe"/> so the parent
    /// can read NDJSON for the life of the process. Completing the writer on copy-end
    /// (exit or cancel) yields EOF to the matching <see cref="StreamReader"/>.
    /// </summary>
    static PipeTarget CopyThenComplete(PipeWriter writer)
    {
        var copy = PipeTarget.ToStream(writer.AsStream(leaveOpen: true), autoFlush: true);
        return PipeTarget.Create(async (origin, cancellation) =>
        {
            try
            {
                await copy.CopyFromAsync(origin, cancellation).ConfigureAwait(false);
            }
            finally
            {
                await writer.CompleteAsync().ConfigureAwait(false);
            }
        });
    }

    /// <inheritdoc />
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;

        try
        {
            try { await StandardInput.DisposeAsync().ConfigureAwait(false); }
            catch { /* already closed */ }

            try
            {
                await execution.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await forceful.CancelAsync().ConfigureAwait(false);
                try { await execution.Task.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
        catch
        {
            try { await forceful.CancelAsync().ConfigureAwait(false); } catch { /* already canceled */ }
            try { await execution.Task.ConfigureAwait(false); } catch { /* gone */ }
        }
        finally
        {
            try { StandardOutput.Dispose(); } catch { /* closing */ }
            try { StandardError.Dispose(); } catch { /* closing */ }
            forceful.Dispose();
        }
    }
}
