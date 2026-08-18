using System.Text;
using System.Threading.Channels;

namespace Tests;

/// <summary>Test stdout: the test writes NDJSON lines; the client reads them.</summary>
sealed class LineSource : TextReader
{
    readonly Channel<string?> lines = Channel.CreateUnbounded<string?>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true,
    });

    public void WriteLine(string line) => lines.Writer.TryWrite(line);

    public void Complete() => lines.Writer.TryComplete();

    public override string? ReadLine()
        => lines.Reader.TryRead(out var line) ? line : lines.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();

    public override async Task<string?> ReadLineAsync()
        => await ReadLineAsync(CancellationToken.None).ConfigureAwait(false);

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await lines.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)
                && lines.Reader.TryRead(out var line))
            {
                return line;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        return null;
    }
}

/// <summary>Test stdin: the client writes NDJSON requests; the test reads them.</summary>
sealed class LineSink : TextWriter
{
    readonly Channel<string> lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    readonly StringBuilder buffer = new();

    public override Encoding Encoding => Encoding.UTF8;

    public async Task<string> ReadLineAsync(CancellationToken cancellationToken = default)
        => await lines.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    public override void Write(char value)
    {
        if (value is '\n')
            FlushLine();
        else if (value is not '\r')
            buffer.Append(value);
    }

    public override void Write(string? value)
    {
        if (value is null)
            return;
        foreach (var ch in value)
            Write(ch);
    }

    public override void WriteLine(string? value)
    {
        if (value is not null)
            buffer.Append(value);
        FlushLine();
    }

    public override Task WriteAsync(string? value)
    {
        Write(value);
        return Task.CompletedTask;
    }

    public override Task WriteLineAsync(string? value)
    {
        WriteLine(value);
        return Task.CompletedTask;
    }

    public override Task FlushAsync() => Task.CompletedTask;

    void FlushLine()
    {
        lines.Writer.TryWrite(buffer.ToString());
        buffer.Clear();
    }
}
