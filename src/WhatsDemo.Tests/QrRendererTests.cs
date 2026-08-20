namespace WhatsDemo.Tests;

public class QrRendererTests
{
    [Fact]
    public void Render_is_a_qr_graphic_not_the_raw_payload()
    {
        const string payload = "2@fixture-pair-code";
        var graphic = QrRenderer.Render(payload);

        Assert.DoesNotContain(payload, graphic);
        Assert.True(graphic.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length > 5);
        Assert.True(
            graphic.Contains('█') || graphic.Contains('▀') || graphic.Contains('▄') || graphic.Contains('#'),
            "expected a QR module graphic");
    }
}
