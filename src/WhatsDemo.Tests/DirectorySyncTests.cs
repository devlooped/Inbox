using Inbox;

namespace WhatsDemo.Tests;

public class DirectorySyncTests
{
    [Fact]
    public async Task Warm_fetches_subscriptions_and_persists_aliases()
    {
        var dir = NewTempDir();
        try
        {
            var path = WhatsBoxToml.PathIn(dir);
            var book = new DirectoryBook();
            var got = new List<string>();
            var sync = new DirectorySync(book, path, ["111@lid"], (id, _) =>
            {
                got.Add(id);
                return Task.FromResult(new DirectoryRow
                {
                    Topic = id,
                    Kind = "user",
                    Handle = "@ada",
                    Name = "Ada",
                });
            });

            await sync.WarmAsync();

            Assert.Equal(["111@lid"], got);
            Assert.Equal("@ada", book.Display("111@lid"));
            var saved = WhatsBoxToml.Load(path);
            Assert.Equal(["111@lid"], saved.Subscribe);
            Assert.Equal("@ada", saved.Directory["111@lid"].Handle);
            Assert.Equal("Ada", saved.Directory["111@lid"].Name);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Subscribe_persists_canonical_topics_and_fetches_them()
    {
        var dir = NewTempDir();
        try
        {
            var path = WhatsBoxToml.PathIn(dir);
            var book = new DirectoryBook();
            var got = new List<string>();
            var sync = new DirectorySync(book, path, get: (id, _) =>
            {
                got.Add(id);
                return Task.FromResult(new DirectoryRow
                {
                    Topic = "111@lid",
                    Kind = "user",
                    Handle = "@ada",
                    Name = "Ada",
                    Pn = "+15551234567",
                });
            });

            var row = await sync.OnSubscribeAsync("+15551234567", ["$session", "111@lid"]);

            Assert.Equal("111@lid", got[0]);
            Assert.NotNull(row);
            Assert.Equal("111@lid", row.Topic);
            Assert.Equal("@ada", row.Handle);
            Assert.Equal(["111@lid"], sync.Subscribe);
            Assert.True(sync.Contains("111@lid"));
            Assert.Equal("@ada", book.Display("111@lid"));
            Assert.Equal("@ada", book.Display("15551234567"));
            Assert.Equal("@ada", book.Display("+15551234567"));
            var saved = WhatsBoxToml.Load(path);
            Assert.Equal(["111@lid"], saved.Subscribe);
            Assert.Equal(["111@lid"], saved.Directory.Keys);
            Assert.Equal("15551234567", saved.Directory["111@lid"].Pn);
            Assert.Equal("@ada", saved.Directory["111@lid"].Handle);
            Assert.Equal("Ada", saved.Directory["111@lid"].Name);
            Assert.Equal("user", DirectoryBook.KindOf("111@lid"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Subscribe_merges_into_existing_subscriptions()
    {
        var dir = NewTempDir();
        try
        {
            var path = WhatsBoxToml.PathIn(dir);
            var book = new DirectoryBook();
            var sync = new DirectorySync(book, path, ["me-lid@lid"], (id, _) =>
                Task.FromResult(new DirectoryRow
                {
                    Topic = id.Contains("111", StringComparison.Ordinal) ? "111@lid" : id,
                    Kind = "user",
                    Handle = "@ada",
                    Name = "Ada",
                }));

            await sync.OnSubscribeAsync("111@lid", ["111@lid"]);

            Assert.Equal(["me-lid@lid", "111@lid"], sync.Subscribe);
            Assert.Equal(["me-lid@lid", "111@lid"], WhatsBoxToml.Load(path).Subscribe);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Unsubscribe_keeps_aliases_and_still_resolves_the_requested_id()
    {
        var dir = NewTempDir();
        try
        {
            var path = WhatsBoxToml.PathIn(dir);
            var book = new DirectoryBook();
            book.Remember("111@lid", "@ada", "Ada");
            var got = new List<string>();
            var sync = new DirectorySync(book, path, ["111@lid", "222@lid"], (id, _) =>
            {
                got.Add(id);
                return Task.FromResult(new DirectoryRow
                {
                    Topic = id,
                    Kind = "user",
                    Handle = "@ada",
                    Name = "Ada",
                });
            });

            await sync.OnUnsubscribeAsync("111@lid", ["$session", "222@lid"]);

            Assert.Equal(["111@lid"], got);
            Assert.Equal(["222@lid"], sync.Subscribe);
            Assert.Equal("@ada", book.Display("111@lid"));
            Assert.Equal(["222@lid"], WhatsBoxToml.Load(path).Subscribe);
            Assert.Equal("@ada", WhatsBoxToml.Load(path).Directory["111@lid"].Handle);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Resolve_skips_when_cached_unless_forced()
    {
        var dir = NewTempDir();
        try
        {
            var path = WhatsBoxToml.PathIn(dir);
            var book = new DirectoryBook();
            book.Remember("111@lid", "@ada", "Ada");
            var got = 0;
            var sync = new DirectorySync(book, path, ["111@lid"], (_, _) =>
            {
                got++;
                return Task.FromResult(new DirectoryRow { Topic = "111@lid", Kind = "user", Name = "Ada" });
            });

            await sync.ResolveAsync("111@lid");
            Assert.Equal(0, got);

            await sync.ResolveAsync("111@lid", force: true);
            Assert.Equal(1, got);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_hydrates_book_from_existing_toml()
    {
        var dir = NewTempDir();
        try
        {
            WhatsBoxToml.Save(WhatsBoxToml.PathIn(dir), new WhatsBoxDocument(
                ["111@lid"],
                new Dictionary<string, DirectoryAlias>(StringComparer.Ordinal)
                {
                    ["111@lid"] = new("@ada", "Ada"),
                }));

            var book = new DirectoryBook();
            var sync = DirectorySync.Load(book, dir, (_, _) => Task.FromResult(new DirectoryRow
            {
                Topic = "111@lid",
                Kind = "user",
            }));

            Assert.Equal(["111@lid"], sync.InitialSubscribe);
            Assert.Equal("@ada", book.Display("111@lid"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "whatsbox-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
