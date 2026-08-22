namespace Tests;

/// <summary>
/// Structural checks on shipped current-facing docs after the project/repo rename to Inbox.
/// The artifacts are the markdown files; this test drives those files on disk.
/// </summary>
public class ProjectIdentityDocsTests
{
    static readonly string Repo = FindRepoRoot();

    [Fact]
    public void Root_readme_names_the_project_Inbox_and_states_the_adapter_split()
    {
        var text = File.ReadAllText(Path.Combine(Repo, "readme.md"));
        var heading = Heading(text);
        var inbox = Fragment(text, "inbox");
        var whatsbox = Fragment(text, "whatsbox");

        Assert.Contains("Inbox", heading, StringComparison.Ordinal);
        Assert.DoesNotContain("whatsbox", heading, StringComparison.OrdinalIgnoreCase);

        Assert.True(
            text.IndexOf("<!-- #inbox -->", StringComparison.Ordinal) <
            text.IndexOf("<!-- #whatsbox -->", StringComparison.Ordinal),
            "root readme must present #inbox before #whatsbox");

        Assert.Contains("Inbox Client Protocol (ICP)", inbox, StringComparison.Ordinal);
        Assert.Contains("`InboxClient`", inbox, StringComparison.Ordinal);
        Assert.DoesNotContain("whatsbox is **an Inbox Protocol", inbox, StringComparison.Ordinal);

        Assert.Contains("`whatsbox` adapter", whatsbox, StringComparison.Ordinal);
        Assert.Contains("`WhatsBox`", whatsbox, StringComparison.Ordinal);
        Assert.Contains("NuGet", whatsbox, StringComparison.Ordinal);
        Assert.Contains("managed host", whatsbox, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("it is not the protocol itself", whatsbox, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Source_and_issue_links_are_devlooped_Inbox()
    {
        var agents = File.ReadAllText(Path.Combine(Repo, "AGENTS.md"));
        Assert.Contains("`devlooped/Inbox`", agents, StringComparison.Ordinal);
        Assert.DoesNotContain("devlooped/whatsbox", agents, StringComparison.Ordinal);

        var whatsBoxReadme = File.ReadAllText(Path.Combine(Repo, "src", "WhatsBox", "readme.md"));
        Assert.Contains("https://github.com/devlooped/Inbox", whatsBoxReadme, StringComparison.Ordinal);
        Assert.DoesNotContain("devlooped/whatsbox", whatsBoxReadme, StringComparison.Ordinal);

        var inboxReadme = File.ReadAllText(Path.Combine(Repo, "src", "Inbox", "readme.md"));
        Assert.Contains("PackageReference `WhatsBox`", inboxReadme, StringComparison.Ordinal);
        Assert.Contains("readme.md#inbox", inboxReadme, StringComparison.Ordinal);
        Assert.DoesNotContain("readme.md#content", inboxReadme, StringComparison.Ordinal);
        Assert.DoesNotContain("readme.md#whatsbox", inboxReadme, StringComparison.Ordinal);

        Assert.Contains("readme.md#whatsbox", whatsBoxReadme, StringComparison.Ordinal);
        Assert.DoesNotContain("readme.md#content", whatsBoxReadme, StringComparison.Ordinal);
        Assert.DoesNotContain("readme.md#inbox", whatsBoxReadme, StringComparison.Ordinal);
    }

    [Fact]
    public void Inbox_fragment_is_box_agnostic_protocol_and_client()
    {
        var inbox = Fragment(File.ReadAllText(Path.Combine(Repo, "readme.md")), "inbox");

        Assert.Contains("Inbox Client Protocol (ICP)", inbox, StringComparison.Ordinal);
        Assert.Contains("`InboxClient`", inbox, StringComparison.Ordinal);
        Assert.Contains("`InboxEvent`", inbox, StringComparison.Ordinal);
        Assert.Contains("ChatMessage", inbox, StringComparison.Ordinal);
        Assert.Contains("contents", inbox, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Implementing a box", inbox, StringComparison.Ordinal);
        Assert.Contains("new InboxClient(stdout, stdin)", inbox, StringComparison.Ordinal);

        Assert.DoesNotContain("QR pairing", inbox, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("whatsbox sidecar", inbox, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("whatsbox.exe", inbox, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WhatsBox.win-x64", inbox, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime.json", inbox, StringComparison.Ordinal);
        Assert.DoesNotContain("WhatsBoxClient", inbox, StringComparison.Ordinal);
        Assert.DoesNotContain("whatsmeow", inbox, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhatsBox_fragment_is_the_WhatsApp_adapter()
    {
        var whatsbox = Fragment(File.ReadAllText(Path.Combine(Repo, "readme.md")), "whatsbox");

        Assert.Contains("QR pairing", whatsbox, StringComparison.Ordinal);
        Assert.Contains("store", whatsbox, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sidecar", whatsbox, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("whatsbox [--store ABSOLUTE_PATH]", whatsbox, StringComparison.Ordinal);
        Assert.Contains("WhatsBoxClient", whatsbox, StringComparison.Ordinal);
        Assert.Contains("https://www.nuget.org/packages/Inbox", whatsbox, StringComparison.Ordinal);
        Assert.Contains("whatsmeow", whatsbox, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remaining_content_markers_are_only_the_WhatsDemo_include()
    {
        var text = File.ReadAllText(Path.Combine(Repo, "readme.md"));
        var whatsboxClose = text.LastIndexOf("<!-- #whatsbox -->", StringComparison.Ordinal);
        Assert.True(whatsboxClose > 0);

        var i = 0;
        var hits = 0;
        while (true)
        {
            var at = text.IndexOf("<!-- #content -->", i, StringComparison.Ordinal);
            if (at < 0)
                break;
            hits++;
            Assert.True(at > whatsboxClose, "<!-- #content --> before #whatsbox close at " + at);
            i = at + 1;
        }

        Assert.Equal(2, hits);
        Assert.Contains("src/WhatsDemo/readme.md#content", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatsBox_profile_is_the_adapter_not_this_repository()
    {
        var text = File.ReadAllText(Path.Combine(Repo, "docs", "WHATSBOX.md"));
        var firstRule = text.IndexOf("---", StringComparison.Ordinal);
        Assert.True(firstRule > 0, "WHATSBOX.md intro missing horizontal rule");
        var intro = text[..firstRule];

        Assert.Contains("WhatsApp adapter", intro, StringComparison.Ordinal);
        Assert.Contains("Inbox Client Protocol (ICP)", intro, StringComparison.Ordinal);
        Assert.Contains("`whatsbox`", intro, StringComparison.Ordinal);
        Assert.Contains("`WhatsBox` NuGet", intro, StringComparison.Ordinal);
        Assert.DoesNotContain("greenfield repo", intro, StringComparison.Ordinal);
        Assert.DoesNotContain("this repository is whatsbox", intro, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("whatsbox [--store ABSOLUTE_PATH]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Osmf_software_name_is_Inbox()
    {
        var text = File.ReadAllText(Path.Combine(Repo, "osmfeula.txt"));
        Assert.Contains("Inbox (\"Software\")", text, StringComparison.Ordinal);
        Assert.DoesNotContain("whatsbox (\"Software\")", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Current_facing_docs_have_no_devlooped_whatsbox_links()
    {
        var hits = new List<string>();
        foreach (var file in CurrentFacingDocs())
        {
            var n = 0;
            foreach (var line in File.ReadAllLines(file))
            {
                n++;
                if (line.Contains("devlooped/whatsbox", StringComparison.Ordinal))
                    hits.Add($"{Path.GetRelativePath(Repo, file)}:{n}:{line.Trim()}");
            }
        }

        Assert.True(hits.Count == 0, string.Join(Environment.NewLine, hits));
    }

    static string Heading(string readme)
    {
        var end = readme.IndexOf("============", StringComparison.Ordinal);
        Assert.True(end > 0, "readme.md missing underline heading");
        return readme[..end];
    }

    static string Fragment(string readme, string name)
    {
        var marker = "<!-- #" + name + " -->";
        var start = readme.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "readme.md missing opening " + marker);
        start += marker.Length;
        var end = readme.IndexOf(marker, start, StringComparison.Ordinal);
        Assert.True(end > start, "readme.md missing closing " + marker);
        return readme[start..end];
    }

    static IEnumerable<string> CurrentFacingDocs()
    {
        yield return Path.Combine(Repo, "readme.md");
        yield return Path.Combine(Repo, "AGENTS.md");
        yield return Path.Combine(Repo, "osmfeula.txt");
        yield return Path.Combine(Repo, "changelog.md");

        foreach (var file in Directory.EnumerateFiles(Path.Combine(Repo, "src"), "readme.md", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file))
                continue;
            yield return file;
        }

        foreach (var file in Directory.EnumerateFiles(Path.Combine(Repo, "docs"), "*.md", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(file), "RFC-1.md", StringComparison.OrdinalIgnoreCase))
                continue;
            yield return file;
        }
    }

    static bool IsBuildOutput(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || p.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WhatsBox.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find WhatsBox.slnx from " + AppContext.BaseDirectory);
    }
}
