using QRCoder;

namespace WhatsDemo;

/// <summary>Renders a pairing payload as a compact console QR (not the raw code string).</summary>
public static class QrRenderer
{
    public static string Render(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var qr = new AsciiQRCode(data);
        return qr.GetGraphicSmall(drawQuietZones: true);
    }
}
