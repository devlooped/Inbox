using System.Diagnostics;
using System.Text;

namespace WhatsBox;

/// <summary>
/// Starts and owns the native <c>whatsbox</c> child process.
/// The executable is resolved next to the app base directory, never <see cref="Directory.GetCurrentDirectory"/>.
/// </summary>
public sealed class WhatsBoxHost : IDisposable, IAsyncDisposable
{
    readonly Process process;
    bool disposed;

    WhatsBoxHost(Process process)
    {
        this.process = process;
        StandardInput = process.StandardInput;
        StandardOutput = process.StandardOutput;
        StandardError = process.StandardError;
    }

    /// <summary>Child stdin (JSON-RPC requests).</summary>
    public TextWriter StandardInput { get; }

    /// <summary>Child stdout (JSON-RPC responses and <c>event</c> notifications).</summary>
    public TextReader StandardOutput { get; }

    /// <summary>Child stderr (logs only; never protocol).</summary>
    public TextReader StandardError { get; }

    /// <summary>Absolute path of the running binary.</summary>
    public string ExecutablePath { get; private init; } = "";

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
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(path)!,
        };
        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start native whatsbox at '{path}'.");
        return new WhatsBoxHost(process) { ExecutablePath = path };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try
        {
            try { process.StandardInput.Close(); } catch { /* already closed */ }
            if (!process.WaitForExit(2000))
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { /* gone */ }
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
