namespace WhatsDemo;

/// <summary>
/// Interactive line reader. Typing <c>/</c> or <c>@</c> opens a completion popup
/// filtered by <see cref="Complete"/>.
/// </summary>
public sealed class LineEditor
{
    readonly ConsoleLock console;
    readonly System.Text.StringBuilder buffer = new();
    int cursor;
    int selected;
    int drawnLines;
    bool active;
    IReadOnlyList<CompletionItem>? picker;

    public LineEditor(ConsoleLock console, Func<string, IReadOnlyList<CompletionItem>>? complete = null)
    {
        this.console = console;
        Complete = complete ?? Completions.Slash;
    }

    /// <summary>Prefix filter for the <c>/</c> and <c>@</c> popups. Defaults to slash commands.</summary>
    public Func<string, IReadOnlyList<CompletionItem>> Complete { get; }

    /// <summary>
    /// Show <paramref name="items"/> as the completion popup (filtered as the
    /// user types) and return the selected insert, or <c>null</c> if cancelled.
    /// </summary>
    public async Task<string?> PickAsync(
        IReadOnlyList<CompletionItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            return null;

        picker = items;
        try
        {
            return await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            picker = null;
        }
    }

    /// <summary>Same lock as <see cref="ConsoleLock.WriteLine"/> so popup draws and inbound lines do not interleave.</summary>
    public Lock Sync => console.Sync;

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        if (Console.IsInputRedirected)
            return await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);

        buffer.Clear();
        cursor = 0;
        selected = 0;
        drawnLines = 0;
        active = true;
        console.Attach(this);
        try
        {
            lock (Sync)
                Redraw();
            while (!cancellationToken.IsCancellationRequested)
            {
                var key = await ReadKeyAsync(cancellationToken).ConfigureAwait(false);
                if (key is null)
                    return null;

                lock (Sync)
                {
                    if (!HandleKey(key.Value))
                    {
                        var line = buffer.ToString();
                        FinishDraw();
                        return line;
                    }
                }
            }

            lock (Sync)
                FinishDraw();
            return null;
        }
        finally
        {
            active = false;
            console.Detach(this);
        }
    }

    public void Suspend()
    {
        if (!active)
            return;
        EraseDrawn();
    }

    public void Resume()
    {
        if (!active)
            return;
        Draw();
    }

    bool HandleKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Enter)
        {
            AcceptHighlighted();
            var line = buffer.ToString();
            if (SlashCommands.IsPendingArgument(line) || AtMentions.IsPending(line))
            {
                Redraw();
                return true;
            }
            return false;
        }

        if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            buffer.Clear();
            return false;
        }

        if (key.Key == ConsoleKey.D && key.Modifiers.HasFlag(ConsoleModifiers.Control) && buffer.Length == 0)
            return false;

        var matches = Matches(buffer.ToString());

        switch (key.Key)
        {
            case ConsoleKey.Tab when matches.Count > 0:
                ApplyCompletion(matches[Math.Clamp(selected, 0, matches.Count - 1)]);
                break;
            case ConsoleKey.UpArrow when matches.Count > 0:
                selected = (selected - 1 + matches.Count) % matches.Count;
                break;
            case ConsoleKey.DownArrow when matches.Count > 0:
                selected = (selected + 1) % matches.Count;
                break;
            case ConsoleKey.LeftArrow:
                if (cursor > 0)
                    cursor--;
                break;
            case ConsoleKey.RightArrow:
                if (cursor < buffer.Length)
                    cursor++;
                break;
            case ConsoleKey.Home:
                cursor = 0;
                break;
            case ConsoleKey.End:
                cursor = buffer.Length;
                break;
            case ConsoleKey.Backspace:
                if (cursor > 0)
                {
                    buffer.Remove(cursor - 1, 1);
                    cursor--;
                    selected = 0;
                }
                break;
            case ConsoleKey.Delete:
                if (cursor < buffer.Length)
                {
                    buffer.Remove(cursor, 1);
                    selected = 0;
                }
                break;
            case ConsoleKey.Escape:
                selected = 0;
                break;
            default:
                if (!char.IsControl(key.KeyChar))
                {
                    buffer.Insert(cursor, key.KeyChar);
                    cursor++;
                    selected = 0;
                }
                break;
        }

        Redraw();
        return true;
    }

    void AcceptHighlighted()
    {
        var text = buffer.ToString();
        var matches = Matches(text);
        if (matches.Count == 0)
            return;
        ApplyCompletion(matches[Math.Clamp(selected, 0, matches.Count - 1)]);
    }

    IReadOnlyList<CompletionItem> Matches(string text)
        => picker is { Count: > 0 } items ? Completions.Filter(items, text) : Complete(text);

    void ApplyCompletion(CompletionItem item)
    {
        buffer.Clear();
        buffer.Append(item.Insert);
        cursor = buffer.Length;
        selected = 0;
    }

    void Redraw()
    {
        EraseDrawn();
        Draw();
    }

    void Draw()
    {
        var text = buffer.ToString();
        var matches = Matches(text);
        selected = matches.Count == 0 ? 0 : Math.Clamp(selected, 0, matches.Count - 1);
        var lines = 0;

        if (matches.Count > 0)
        {
            var width = Math.Max(12, matches.Max(m => DisplayLabel(m.Label).Length) + 2);
            Console.WriteLine('┌' + new string('─', width) + '┐');
            lines++;
            for (var i = 0; i < matches.Count; i++)
            {
                var name = DisplayLabel(matches[i].Label).PadRight(width);
                if (i == selected)
                    Console.WriteLine($"│\x1b[7m{name}\x1b[0m│");
                else
                    Console.WriteLine($"│{name}│");
                lines++;
            }
            Console.WriteLine('└' + new string('─', width) + '┘');
            lines++;
        }

        Console.Write("> ");
        Console.Write(text);
        var extra = buffer.Length - cursor;
        if (extra > 0)
            Console.Write($"\x1b[{extra}D");
        drawnLines = lines + 1;
    }

    void EraseDrawn()
    {
        if (drawnLines == 0)
            return;
        Console.Write("\r\x1b[2K");
        for (var i = 1; i < drawnLines; i++)
            Console.Write("\x1b[1A\x1b[2K");
        Console.Write('\r');
        drawnLines = 0;
    }

    static string DisplayLabel(string label)
    {
        var cap = 72;
        try
        {
            cap = Math.Max(12, Console.WindowWidth - 4);
        }
        catch (IOException)
        {
            // redirected / no window
        }

        return label.Length <= cap ? label : label[..Math.Max(0, cap - 3)] + "...";
    }

    void FinishDraw()
    {
        EraseDrawn();
        Console.WriteLine("> " + buffer);
        drawnLines = 0;
    }

    static async Task<ConsoleKeyInfo?> ReadKeyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (Console.KeyAvailable)
                        return Console.ReadKey(intercept: true);
                    Thread.Sleep(25);
                }
                return (ConsoleKeyInfo?)null;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
