namespace WhatsDemo.Tests;

public class WhatsBoxTomlTests
{
    [Fact]
    public void Round_trips_subscribe_and_directory_aliases()
    {
        var original = new WhatsBoxDocument(
            ["111@lid", "12036342@g.us"],
            new Dictionary<string, DirectoryAlias>(StringComparer.Ordinal)
            {
                ["111@lid"] = new("@ada", "Ada", "5491156103511", Me: true),
                ["12036342@g.us"] = new(null, "Family"),
            });

        var text = WhatsBoxToml.Format(original);
        Assert.Contains("subscribe = [", text);
        Assert.Contains("\"111@lid\"", text);
        Assert.Contains("[directory]", text);
        Assert.Contains("handle = \"@ada\"", text);
        Assert.Contains("pn = \"5491156103511\"", text);
        Assert.DoesNotContain("kind =", text);
        Assert.Contains("me = true", text);
        Assert.Contains("name = \"Family\"", text);
        Assert.DoesNotContain("@s.whatsapp.net\" =", text);

        var parsed = WhatsBoxToml.Parse(text);
        Assert.Equal(original.Subscribe, parsed.Subscribe);
        Assert.Equal("@ada", parsed.Directory["111@lid"].Handle);
        Assert.Equal("Ada", parsed.Directory["111@lid"].Name);
        Assert.Equal("5491156103511", parsed.Directory["111@lid"].Pn);
        Assert.True(parsed.Directory["111@lid"].Me);
        Assert.False(parsed.Directory["12036342@g.us"].Me);
        Assert.Equal("Family", parsed.Directory["12036342@g.us"].Name);
        Assert.Null(parsed.Directory["12036342@g.us"].Handle);
        Assert.Equal("user", DirectoryBook.KindOf("111@lid"));
        Assert.Equal("group", DirectoryBook.KindOf("12036342@g.us"));
    }

    [Fact]
    public void Load_missing_file_is_empty()
    {
        var doc = WhatsBoxToml.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "wb.toml"));
        Assert.Empty(doc.Subscribe);
        Assert.Empty(doc.Directory);
    }

    [Fact]
    public void Save_then_load_from_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "whatsbox-toml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = WhatsBoxToml.PathIn(dir);
            WhatsBoxToml.Save(path, new WhatsBoxDocument(
                ["999@lid"],
                new Dictionary<string, DirectoryAlias>(StringComparer.Ordinal)
                {
                    ["999@lid"] = new("@bob", "Bob"),
                }));

            var loaded = WhatsBoxToml.Load(path);
            Assert.Equal(["999@lid"], loaded.Subscribe);
            Assert.Equal("@bob", loaded.Directory["999@lid"].Handle);
            Assert.Equal("wb.toml", Path.GetFileName(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Parses_dotted_directory_tables_and_comments()
    {
        var parsed = WhatsBoxToml.Parse("""
            # demo state
            subscribe = ["111@lid"]

            [directory."111@lid"]
            handle = "@ada"
            name = "Ada"
            """);

        Assert.Equal(["111@lid"], parsed.Subscribe);
        Assert.Equal("@ada", parsed.Directory["111@lid"].Handle);
        Assert.Equal("Ada", parsed.Directory["111@lid"].Name);
    }

    [Fact]
    public void Drops_system_topics_from_subscribe()
    {
        var parsed = WhatsBoxToml.Parse("""
            subscribe = ["$session", "111@lid", "$directory"]
            """);
        Assert.Equal(["111@lid"], parsed.Subscribe);
    }

    [Fact]
    public void Parse_ignores_legacy_kind_fields()
    {
        var parsed = WhatsBoxToml.Parse("""
            subscribe = ["111@lid", "12036342@g.us"]

            [directory]
            "111@lid" = { name = "Ada", kind = "user" }
            "12036342@g.us" = { name = "Family", kind = "group" }
            """);

        Assert.Equal("Ada", parsed.Directory["111@lid"].Name);
        Assert.Equal("Family", parsed.Directory["12036342@g.us"].Name);
        Assert.DoesNotContain("kind =", WhatsBoxToml.Format(parsed));
    }

    [Fact]
    public void Format_drops_phone_jid_keys()
    {
        var text = WhatsBoxToml.Format(new WhatsBoxDocument(
            ["111@lid"],
            new Dictionary<string, DirectoryAlias>(StringComparer.Ordinal)
            {
                ["111@lid"] = new("@ada", "Ada", "5491156103511@s.whatsapp.net"),
                ["5491156103511@s.whatsapp.net"] = new("@ada", "Ada"),
            }));

        Assert.Contains("\"111@lid\" = {", text);
        Assert.Contains("pn = \"5491156103511\"", text);
        Assert.DoesNotContain("\"5491156103511@s.whatsapp.net\" =", text);
    }
}
