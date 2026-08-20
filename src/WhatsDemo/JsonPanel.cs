using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;

namespace WhatsDemo;

/// <summary>Pretty-printed JSON in a box-drawing panel for slash-command RPC results.</summary>
public static class JsonPanel
{
    public static string Render<T>(T value, JsonTypeInfo<T> typeInfo)
        => RenderJson(JsonSerializer.Serialize(value, typeInfo));

    public static string RenderJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var doc = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            SkipValidation = true,
        }))
        {
            doc.RootElement.WriteTo(writer);
        }

        var pretty = Encoding.UTF8.GetString(stream.ToArray());
        var lines = pretty.Replace("\r\n", "\n").Split('\n');
        var inner = Math.Max(1, lines.Max(static line => line.Length));
        var rule = new string('─', inner + 2);
        var sb = new StringBuilder();
        sb.Append('┌').Append(rule).Append('┐').Append('\n');
        foreach (var line in lines)
        {
            sb.Append("│ ");
            sb.Append(line);
            if (line.Length < inner)
                sb.Append(' ', inner - line.Length);
            sb.Append(" │").Append('\n');
        }

        sb.Append('└').Append(rule).Append('┘');
        return sb.ToString();
    }
}
