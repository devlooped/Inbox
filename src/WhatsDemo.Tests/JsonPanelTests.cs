using System.Text.Json;
using WhatsBox;

namespace WhatsDemo.Tests;

public class JsonPanelTests
{
    [Fact]
    public void Topics_result_is_indented_json_inside_a_box()
    {
        var panel = JsonPanel.Render(
            new TopicsResult { Topics = ["$session", "111@lid"] },
            WhatsJsonContext.Default.TopicsResult);

        Assert.StartsWith("┌", panel);
        Assert.EndsWith("┘", panel);
        Assert.Contains("│ {", panel);
        Assert.Contains("\"topics\"", panel);
        Assert.Contains("111@lid", panel);
        Assert.DoesNotContain('\r', panel);

        var inner = string.Join('\n', panel.Split('\n')
            .Where(line => line.StartsWith("│ "))
            .Select(line => line[2..^2].TrimEnd()));
        using var doc = JsonDocument.Parse(inner);
        Assert.Equal(["$session", "111@lid"], doc.RootElement.GetProperty("topics").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Directory_row_omits_nulls_and_keeps_camel_case()
    {
        var panel = JsonPanel.Render(
            new DirectoryRow { Topic = "111@lid", Kind = "user", Name = "Ada" },
            WhatsJsonContext.Default.DirectoryRow);

        Assert.Contains("\"topic\": \"111@lid\"", panel);
        Assert.Contains("\"kind\": \"user\"", panel);
        Assert.Contains("\"name\": \"Ada\"", panel);
        Assert.DoesNotContain("handle", panel);
        Assert.DoesNotContain("participants", panel);
    }

    [Fact]
    public void Renders_spanish_accents_as_unicode_not_escapes()
    {
        var panel = JsonPanel.Render(
            new DirectoryRow { Topic = "111@lid", Kind = "user", Name = "Analía Carvallo" },
            WhatsJsonContext.Default.DirectoryRow);

        Assert.Contains("Analía Carvallo", panel);
        Assert.DoesNotContain("\\u00ED", panel);
        Assert.DoesNotContain("u00ED", panel);
    }
}
