namespace WhatsDemo;

/// <summary>Serializes console writes so inbound events can interrupt the REPL line editor.</summary>
public sealed class ConsoleLock
{
    LineEditor? editor;

    /// <summary>Shared lock for <see cref="WriteLine"/> and <see cref="LineEditor"/> key handling.</summary>
    public Lock Sync { get; } = new();

    public void Attach(LineEditor editor)
    {
        lock (Sync)
            this.editor = editor;
    }

    public void Detach(LineEditor editor)
    {
        lock (Sync)
        {
            if (ReferenceEquals(this.editor, editor))
                this.editor = null;
        }
    }

    public void WriteLine(string line)
    {
        lock (Sync)
        {
            editor?.Suspend();
            Console.WriteLine(line);
            editor?.Resume();
        }
    }

    public void Write(string text)
    {
        lock (Sync)
        {
            editor?.Suspend();
            Console.Write(text);
            editor?.Resume();
        }
    }
}
