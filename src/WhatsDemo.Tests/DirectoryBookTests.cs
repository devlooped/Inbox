using Inbox;

namespace WhatsDemo.Tests;

public class DirectoryBookTests
{
    [Fact]
    public void MarkMe_displays_as_me_and_is_the_only_me_row()
    {
        var book = new DirectoryBook();
        book.Remember("me@lid", "@danielkzu", "Kzu");
        book.Remember("111@lid", null, "Analía");
        Assert.Equal("@danielkzu", book.Display("me@lid"));
        Assert.True(book.MarkMe("me@lid"));
        Assert.Equal("me", book.Display("me@lid"));
        Assert.Equal("Analía", book.Display("111@lid"));
        Assert.True(book.Snapshot()["me@lid"].Me);
        Assert.False(book.Snapshot()["111@lid"].Me);

        book.MarkMe("111@lid");
        Assert.False(book.Snapshot()["me@lid"].Me);
        Assert.True(book.Snapshot()["111@lid"].Me);
        Assert.Equal("me", book.Display("111@lid"));
        Assert.Equal("@danielkzu", book.Display("me@lid"));
    }

    [Fact]
    public void Display_is_handle_then_name_then_id()
    {
        var book = new DirectoryBook();
        Assert.Equal("999@lid", book.Display("999@lid"));
        Assert.Equal("@ada", book.Display("999@lid", handle: "@ada", name: "Ada"));
        Assert.Equal("Ada", book.Display("999@lid", name: "Ada"));

        book.Remember("999@lid", "@ada", "Ada");
        Assert.Equal("@ada", book.Display("999@lid"));

        book.Remember("888@lid", null, "Bob");
        Assert.Equal("Bob", book.Display("888@lid"));
    }

    [Fact]
    public void Remember_row_indexes_topic_pn_and_handle_but_snapshots_one_canonical_key()
    {
        var book = new DirectoryBook();
        book.Remember(new DirectoryRow
        {
            Topic = "111@lid",
            Kind = "user",
            Handle = "@ada",
            Name = "Ada",
            Pn = "5491156103511@s.whatsapp.net",
        });

        Assert.Equal("@ada", book.Display("111@lid"));
        Assert.Equal("@ada", book.Display("5491156103511"));
        Assert.Equal("@ada", book.Display("+5491156103511"));
        Assert.Equal("@ada", book.Display("5491156103511@s.whatsapp.net"));
        Assert.Equal("@ada", book.Display("@ada"));

        var snap = book.Snapshot();
        Assert.Equal(["111@lid"], snap.Keys);
        Assert.Equal("@ada", snap["111@lid"].Handle);
        Assert.Equal("Ada", snap["111@lid"].Name);
        Assert.Equal("5491156103511", snap["111@lid"].Pn);
        Assert.Equal("user", DirectoryBook.KindOf("111@lid"));
    }

    [Fact]
    public void Author_events_do_not_rename_a_group()
    {
        var book = new DirectoryBook();
        book.Remember(new DirectoryRow
        {
            Topic = "5491159278282-1472673286@g.us",
            Kind = "group",
            Name = "Nosotros",
        });
        book.Remember("5491159278282-1472673286@g.us", "@agus", "agus");
        Assert.Equal("Nosotros", book.Display("5491159278282-1472673286@g.us"));
        Assert.Equal("group", DirectoryBook.KindOf("5491159278282-1472673286@g.us"));
        Assert.Null(book.Snapshot()["5491159278282-1472673286@g.us"].Handle);
    }

    [Fact]
    public void Remember_row_indexes_group_participants_separately()
    {
        var book = new DirectoryBook();
        book.Remember(new DirectoryRow
        {
            Topic = "12036342@g.us",
            Kind = "group",
            Name = "Family",
            Participants =
            [
                new DirectoryParticipant { Topic = "111@lid", Handle = "@ada", Name = "Ada" },
            ],
        });

        Assert.Equal("Family", book.Display("12036342@g.us"));
        Assert.Equal("@ada", book.Display("111@lid"));
        Assert.Equal(["111@lid", "12036342@g.us"], book.Snapshot().Keys);
    }

    [Fact]
    public void Import_coalesces_legacy_phone_jid_keys_onto_the_lid()
    {
        var book = new DirectoryBook();
        book.Import(new Dictionary<string, DirectoryAlias>(StringComparer.Ordinal)
        {
            ["79388259385548@lid"] = new("@danielkzu", "@danielkzu"),
            ["5491159278282@s.whatsapp.net"] = new("@danielkzu", "@danielkzu"),
        });

        var snap = book.Snapshot();
        Assert.Equal(["79388259385548@lid"], snap.Keys);
        Assert.Equal("5491159278282", snap["79388259385548@lid"].Pn);
        Assert.Equal("@danielkzu", book.Display("5491159278282"));
        Assert.Equal("@danielkzu", book.Display("5491159278282@s.whatsapp.net"));
    }

    [Fact]
    public void Same_display_name_does_not_merge_two_lids()
    {
        var book = new DirectoryBook();
        book.Remember("111@lid", null, "Kzu", "5491159278282");
        book.Remember("222@lid", null, "Kzu", "5491156103511");
        var snap = book.Snapshot();
        Assert.Equal("Kzu", snap["111@lid"].Name);
        Assert.Equal("Kzu", snap["222@lid"].Name);
        Assert.Equal("5491159278282", snap["111@lid"].Pn);
        Assert.Equal("5491156103511", snap["222@lid"].Pn);
    }

    [Fact]
    public void Directory_get_reclaims_pn_from_a_wrong_lid()
    {
        var book = new DirectoryBook();
        book.Remember("me@lid", "@danielkzu", "Kzu", "5491156103511");
        book.Remember(new DirectoryRow
        {
            Topic = "111@lid",
            Kind = "user",
            Name = "Any",
            Pn = "5491156103511@s.whatsapp.net",
        });
        var snap = book.Snapshot();
        Assert.Equal("5491156103511", snap["111@lid"].Pn);
        Assert.Equal("Any", snap["111@lid"].Name);
        Assert.Null(snap["me@lid"].Pn);
        Assert.Equal("Any", book.Display("5491156103511"));
        Assert.Equal("Any", book.Display("541156103511"));
    }

    [Fact]
    public void Merge_keeps_existing_fields_when_new_row_omits_them()
    {
        var book = new DirectoryBook();
        book.Remember("111@lid", "@ada", "Ada");
        book.Remember("111@lid", null, "Ada Lovelace");
        Assert.Equal("@ada", book.Display("111@lid"));
        Assert.Equal("Ada Lovelace", book.Snapshot()["111@lid"].Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("me")]
    [InlineData("$session")]
    [InlineData("$directory")]
    public void System_and_empty_ids_are_not_lookup_keys(string? id)
        => Assert.False(DirectoryBook.IsLookupId(id));

    [Theory]
    [InlineData("111@lid", "user")]
    [InlineData("12036342@g.us", "group")]
    [InlineData("5491159278282-1472673286@g.us", "group")]
    [InlineData("15551234567@s.whatsapp.net", "user")]
    [InlineData("15551234567", "user")]
    [InlineData("$directory", null)]
    [InlineData("me", null)]
    public void KindOf_is_the_jid_suffix(string? id, string? kind)
        => Assert.Equal(kind, DirectoryBook.KindOf(id));

    [Theory]
    [InlineData("5491156103511@s.whatsapp.net", "5491156103511")]
    [InlineData("+5491156103511", "5491156103511")]
    [InlineData("5491156103511", "5491156103511")]
    [InlineData("  +5491156103511  ", "5491156103511")]
    [InlineData("541156103511", "5491156103511")]
    [InlineData("+541156103511", "5491156103511")]
    [InlineData("541156103511@s.whatsapp.net", "5491156103511")]
    [InlineData("15551234567", "15551234567")]
    [InlineData("111@lid", "111@lid")]
    [InlineData("@ada", "@ada")]
    [InlineData("12036342@g.us", "12036342@g.us")]
    public void NormalizeTopic_strips_phone_jid_and_plus(string input, string expected)
        => Assert.Equal(expected, DirectoryBook.NormalizeTopic(input));

    [Fact]
    public void Argentina_local_54_11_looks_up_the_whatsapp_54_9_11_form()
    {
        var book = new DirectoryBook();
        book.Remember("111@lid", null, "Analía Carvallo", "5491156103511");
        Assert.Equal("Analía Carvallo", book.Display("541156103511"));
        Assert.Equal("Analía Carvallo", book.Display("+541156103511"));
        Assert.Equal("Analía Carvallo", book.Display("5491156103511"));
        Assert.Equal("5491156103511", book.Snapshot()["111@lid"].Pn);
    }
}
