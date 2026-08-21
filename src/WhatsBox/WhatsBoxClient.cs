using Inbox;

namespace WhatsBox;

/// <summary>
/// WhatsApp Inbox Protocol wiring: starts the native <c>whatsbox</c> sidecar
/// and speaks the protocol through <see cref="InboxClient"/>.
/// </summary>
public sealed class WhatsBoxClient : InboxClient
{
    /// <summary>Starts native <c>whatsbox</c> from <see cref="AppContext.BaseDirectory"/>.</summary>
    public WhatsBoxClient() : this(WhatsBoxHost.Start()) { }

    /// <summary>Uses an already-started host (owns and disposes it).</summary>
    public WhatsBoxClient(WhatsBoxHost host)
        : base(
            (host ?? throw new ArgumentNullException(nameof(host))).StandardOutput,
            host.StandardInput,
            host,
            host.StandardError)
    {
    }

    /// <summary>Starts native <c>whatsbox</c> resolved next to <paramref name="baseDirectory"/>.</summary>
    public static WhatsBoxClient Start(string? baseDirectory = null)
        => new(WhatsBoxHost.Start(baseDirectory));
}
