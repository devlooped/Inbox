![Icon](docs/logo.png) whatsbox
============

[![Version](https://img.shields.io/nuget/vpre/WhatsBox.svg?color=royalblue)](https://www.nuget.org/packages/WhatsBox)
[![Downloads](https://img.shields.io/nuget/dt/WhatsBox.svg?color=darkmagenta)](https://www.nuget.org/packages/WhatsBox)
[![EULA](https://img.shields.io/badge/EULA-OSMF-blue?labelColor=black&color=C9FF30)](https://github.com/devlooped/oss/blob/main/osmfeula.txt)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/devlooped/oss/blob/main/license.txt)

<!-- include https://github.com/devlooped/.github/raw/main/osmf.md -->
## Open Source Maintenance Fee

To ensure the long-term sustainability of this project, users of this package who generate 
revenue must pay an [Open Source Maintenance Fee](https://opensourcemaintenancefee.org). 
While the source code is freely available under the terms of the [License](license.txt), 
this package and other aspects of the project require [adherence to the Maintenance Fee](osmfeula.txt).

To pay the Maintenance Fee, [become a Sponsor](https://github.com/sponsors/devlooped) at the proper 
OSMF tier. A single fee covers all of [Devlooped packages](https://www.nuget.org/profiles/Devlooped).

<!-- https://github.com/devlooped/.github/raw/main/osmf.md -->
---
<!-- #content -->
whatsbox is **an Inbox Protocol implementation for WhatsApp**: one process
owns a linked-device session and exposes it as a **JSON-RPC 2.0 pub/sub bus
over stdio**. Clients subscribe to chats (and two system topics), send a
small set of actions, and receive live events. It is not an archive, a
search engine, or a WhatsApp CLI.

The WhatsApp connection is powered by [whatsmeow](https://github.com/tulir/whatsmeow).

Use the **managed client** from .NET, or speak the **native protocol** from any
language that can spawn a process and exchange newline-delimited JSON.

## Managed Client

The [`WhatsBox`](https://www.nuget.org/packages/WhatsBox) package is a typed
.NET host for the native `whatsbox` sidecar plus the managed **Inbox Protocol**
client (`InboxClient`). `PackageReference` it, then publish for your RID —
the matching native binary is restored and copied next to the app.

```xml
<PackageReference Include="WhatsBox" Version="*" />
```

Target framework: `net10.0`. The managed surface is AOT-compatible (source-generated JSON).

### Packaging and publish

`WhatsBox` is a **pointer package**: it ships `WhatsBox.dll` plus a
`runtime.json` that maps each runtime identifier to a RID-only package.

| Package | Contents |
|---|---|
| `WhatsBox` | Managed API (`WhatsBox.dll`) and `runtime.json` |
| `WhatsBox.win-x64` / `.win-arm64` / `.linux-x64` / `.linux-arm64` / `.osx-x64` / `.osx-arm64` | Native `whatsbox` / `whatsbox.exe` under `runtimes/{rid}/native/` |

You only reference `WhatsBox`. Restore and `dotnet publish -r <rid>` pull the
matching `WhatsBox.{rid}` package automatically. The sidecar lands next to the
app (`AppContext.BaseDirectory`); `WhatsBoxClient` starts it from there — never
from the current working directory. The pointer package depends on [`Inbox`](https://www.nuget.org/packages/Inbox)
(`InboxClient`); Inbox's RID packing targets are **not** transitive.

Adapters for other products PackageReference `Inbox` directly (or ProjectReference
it and import `Inbox.targets`) so pointer + `dotnet pack -r` packaging is shared.

```bash
dotnet add package WhatsBox
dotnet publish -c Release -r win-x64
```

Do not add `WhatsBox.win-x64` (or any other RID package) by hand. Do not treat
this as a .NET tool (`PackAsTool`); it is a `PackageReference` library plus a
native asset.

The companion REPL is a separate tool package (`wd`) with the
same pointer + RID split:

```bash
dotnet tool install -g wd
wd
```

Supported RIDs: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`,
`osx-arm64`.

### API

`WhatsBoxClient` owns the child process, turns unary JSON-RPC methods into
`Task<T>`, and exposes a single-consumer pull stream of typed events.
Disposing the client stops the sidecar.

Start consuming `Events` **before** (or concurrently with) a connecting
`InitializeAsync`. Pairing QR codes arrive as events while that call is still
waiting for a scan.

```csharp
using WhatsBox;

var store = Path.GetFullPath("whatsbox-store");
var files = Path.GetFullPath("whatsbox-files");
Directory.CreateDirectory(store);
Directory.CreateDirectory(files);

await using var box = new WhatsBoxClient();

var pump = Task.Run(async () =>
{
    await foreach (var ev in box.Events)
    {
        switch (ev)
        {
            case SessionQr qr:
                // Render qr.Code as a QR image and scan it in WhatsApp → Linked devices.
                Console.WriteLine(qr.Code);
                break;
            case SessionOnline online:
                Console.WriteLine($"online as {online.Me}");
                break;
            case SessionPairError err:
                Console.Error.WriteLine(err.Message);
                break;
            case DirectoryReady:
                var page = await box.ListDirectoryAsync(new DirectoryListOptions { Kind = "user" });
                foreach (var row in page.Items)
                    Console.WriteLine($"{row.Name ?? row.Topic}  {row.Pn}");
                break;
            case DirectoryUpsert upsert:
                Console.WriteLine($"directory: {upsert.Name ?? upsert.Jid}");
                break;
            case ChatMessage msg:
                Console.WriteLine($"{msg.ByName ?? msg.By}: {msg.Text}");
                if (msg.Id is not null)
                    await box.ReadAsync(msg);
                break;
        }
    }
});

var session = await box.InitializeAsync(new InitializeOptions
{
    Store = store,
    Files = files,
    Subscribe = ["$directory"],
    Connect = true,
});

if (session.Status == "online")
{
    var listed = await box.ListDirectoryAsync(new DirectoryListOptions { Query = "+15551234567" });
    var chat = listed.Items[0].Topic;
    await box.SubscribeAsync([chat]);
    await box.SendAsync(chat, text: "hello from whatsbox");
}

await pump;
```

`InitializeAsync(store)` is the short form: no files, no extra subscriptions,
no connect. The linked-device name defaults to `whatsbox on {machine}`. Pass
`InitializeOptions` when you want blobs, initial topics, a custom
`DeviceName`, or `Connect = true` (implicit `session.connect`, and implicit
QR pairing when the store is new).

| Method | RPC | Result |
|---|---|---|
| `InitializeAsync` | `initialize` | `SessionSnapshot` |
| `ConnectAsync` | `session.connect` | `SessionSnapshot` |
| `PairAsync` | `session.pair` | `SessionSnapshot` |
| `DisconnectAsync` | `session.disconnect` | `SessionSnapshot` |
| `LogoutAsync` | `session.logout` | `SessionSnapshot` (`new`) |
| `StatusAsync` | `session.status` | `SessionSnapshot` |
| `SubscribeAsync` / `UnsubscribeAsync` | `subscribe` / `unsubscribe` | `TopicsResult` (canonical JIDs) |
| `ListDirectoryAsync` | `directory.list` | `DirectoryListResult` |
| `GetDirectoryAsync` | `directory.get` | `DirectoryRow` |
| `SendAsync` / `ReactAsync` | `messages.send` | `SendResult` (`Id`, canonical `Topic`) |
| `ReadAsync` | `messages.read` | `ReadResult` |

`SessionSnapshot.Status` is `new` (never paired), `offline` (keys on disk,
socket down), or `online`. `Me` is the paired LID and is omitted when `new`.

`SubscribeAsync` / `UnsubscribeAsync` take canonical JIDs (LID, group, or
PN JID). Resolve names and phone numbers with `ListDirectoryAsync` first.
`SendAsync`, `ReadAsync`, and `GetDirectoryAsync` still accept a LID, a
phone-number JID, or a phone number (`+15551234567` or digits). Results and
event topics are always **canonical** (LID or group JID) once a LID is known.

Send text (sugar), a file under `files`, a reply, and/or a reaction:

```csharp
await box.SendAsync(chat, text: "hello");

await box.SendAsync(chat, [new ImagePart { Path = "out/photo.jpg" }]);

await box.SendAsync(chat, text: "agreed",
    reply: new MessageReply(id, by));

await box.ReactAsync(chat, target: id, by, "👍");
```

`by` is required on reply, react, and `ReadAsync` — copy it from the inbound
event. Use `"me"` when targeting your own message. In 1:1 the sidecar ignores
`by`; in groups every id in that call must share that author. Mark-read is
never automatic.

RPC failures throw `WhatsRpcException` with the JSON-RPC `Code` and a stable
`Token` (`not_initialized`, `files_required`, `not_found`, …).

### Events

`Events` is a single-consumer `IAsyncEnumerable<WhatsEvent>`. Enumerate it
once. It completes when the child stdout ends or the client is disposed.

| Type | Topic | Kind |
|---|---|---|
| `SessionQr` | `$session` | `qr` — string to render as QR |
| `SessionPaired` | `$session` | `paired` |
| `SessionPairError` | `$session` | `pair_error` |
| `SessionOnline` / `SessionOffline` | `$session` | `online` / `offline` |
| `SessionLoggedOut` | `$session` | `logged_out` |
| `SessionRemap` | `$session` | `remap` — subscription moved PN → LID |
| `SessionOverflow` | `$session` | `overflow` — per-topic queue dropped oldest |
| `DirectoryUpsert` / `DirectoryRemove` / `DirectoryReady` | `$directory` | catalog changes |
| `ChatMessage` | chat JID | `message` — `Contents` (text, media, location, unknown); `Text` concatenates text parts |
| `ChatReaction` | chat JID | `reaction` — one reaction part |
| `ChatAck` | chat JID | `ack` — `delivered` / `read` / `played` |
| `ChatMeta` | chat JID | `meta` — join/leave/rename/… |

Chat events share `Id`, `By` (`"me"` or a LID), `Handle` (`@username` when
known), `TopicName`, `ByName`, and `Contents`. There is no phone number on
chat events — look up `By` (or a 1:1 `Topic`) with `GetDirectoryAsync` when
you need `Pn`.

### Host

`new WhatsBoxClient()` starts `whatsbox` / `whatsbox.exe` from
`AppContext.BaseDirectory`. Use `WhatsBoxClient.Start(baseDirectory)` to
point at another folder, or construct from an already-started `WhatsBoxHost`
/ a raw NDJSON `TextReader`+`TextWriter` pair if you spawn the process
yourself.

stderr is logs only (`Debug.WriteLine` when the client owns the host). It is
never protocol.

## Native Protocol

The `whatsbox` binary is a local companion process. Spawn it, speak
**JSON-RPC 2.0** (NDJSON) on stdio, and keep the process alive for the life
of the session. The wire is **Inbox Protocol** [`docs/INBOX.md`](docs/INBOX.md)
version `"0.1"`: methods, events (`contents[]` on chat topics), files,
errors, and capabilities. WhatsApp-specific mapping (LID topics, QR pairing,
ContextInfo quotes, HistorySync headers, `attachments: "single"`) is
[`docs/WHATSBOX.md`](docs/WHATSBOX.md).

The WhatsApp Web socket behind it is
[whatsmeow](https://github.com/tulir/whatsmeow); clients never talk to
whatsmeow directly.

### Invocation

```text
whatsbox [--store ABSOLUTE_PATH] [--version] [--help]
```

stdin / stdout is NDJSON JSON-RPC; stderr is logs. No default store. One
process, one store, one WhatsApp session. Full method table, event shapes,
and error tokens: [`docs/INBOX.md`](docs/INBOX.md). LID / QR / store layout:
[`docs/WHATSBOX.md`](docs/WHATSBOX.md).

<!-- #content -->

## Demo

<!-- include src/WhatsDemo/readme.md#content -->
<!-- #content -->
`wd` is a pointer tool: it restores the matching RID package
(`wd.win-x64`, `wd.linux-x64`, …) and starts the Native AOT REPL plus
the `whatsbox` sidecar.

Run it with [`dnx`](https://learn.microsoft.com/dotnet/core/tools/dotnet-tool-exec)
(SDK 10+) or the faster native-only [`ndnx`](https://github.com/devlooped/ndnx):

```bash
dnx  wd
ndnx wd
```

`dnx` always goes through the SDK. `ndnx` starts the cached AOT binary
directly — no SDK after the first download. Pin a version (`wd@1.0.0`)
to skip latest-version lookup.

To install a `wd` command on PATH instead:

```bash
dotnet tool install -g wd
wd
```

The command is `wd`. RID matrix matches WhatsBox: `win-x64`, `win-arm64`,
`linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.

The working directory is the session root. First run creates `.store`,
prints a pairing QR, and waits for WhatsApp → Linked devices. Later runs
reuse that store.
<!-- #content -->
<!-- src/WhatsDemo/readme.md#content -->

### v1 scope

**Does:** pair via QR, connect / auto-reconnect / disconnect / logout,
directory populate + list/get + live `$directory`, subscribe by JID
(LID-first), live messages / receipts / in-chat `meta`, send `contents[]`,
reply, react, explicit mark-read.

**Does not:** message history, search, backfill, export; stored bodies or
last-message previews; typing or “available” presence; edit or revoke;
pair-code or passkey pairing; channels, status, calls, blocklist or group
admin RPCs; MCP / sockets; multi-account in one process; topic wildcards; a
default store path.

---
<!-- include https://github.com/devlooped/sponsors/raw/main/footer.md -->
# Sponsors 

<!-- sponsors.md -->
[![Clarius Org](https://avatars.githubusercontent.com/u/71888636?v=4&s=39 "Clarius Org")](https://github.com/clarius)
[![MFB Technologies, Inc.](https://avatars.githubusercontent.com/u/87181630?v=4&s=39 "MFB Technologies, Inc.")](https://github.com/MFB-Technologies-Inc)
[![SandRock](https://avatars.githubusercontent.com/u/321868?u=99e50a714276c43ae820632f1da88cb71632ec97&v=4&s=39 "SandRock")](https://github.com/sandrock)
[![DRIVE.NET, Inc.](https://avatars.githubusercontent.com/u/15047123?v=4&s=39 "DRIVE.NET, Inc.")](https://github.com/drivenet)
[![Keith Pickford](https://avatars.githubusercontent.com/u/16598898?u=64416b80caf7092a885f60bb31612270bffc9598&v=4&s=39 "Keith Pickford")](https://github.com/Keflon)
[![Thomas Bolon](https://avatars.githubusercontent.com/u/127185?u=7f50babfc888675e37feb80851a4e9708f573386&v=4&s=39 "Thomas Bolon")](https://github.com/tbolon)
[![Reuben Swartz](https://avatars.githubusercontent.com/u/724704?u=2076fe336f9f6ad678009f1595cbea434b0c5a41&v=4&s=39 "Reuben Swartz")](https://github.com/rbnswartz)
[![Jacob Foshee](https://avatars.githubusercontent.com/u/480334?v=4&s=39 "Jacob Foshee")](https://github.com/jfoshee)
[![](https://avatars.githubusercontent.com/u/33566379?u=bf62e2b46435a267fa246a64537870fd2449410f&v=4&s=39 "")](https://github.com/Mrxx99)
[![Eric Johnson](https://avatars.githubusercontent.com/u/26369281?u=41b560c2bc493149b32d384b960e0948c78767ab&v=4&s=39 "Eric Johnson")](https://github.com/eajhnsn1)
[![Jonathan ](https://avatars.githubusercontent.com/u/5510103?u=98dcfbef3f32de629d30f1f418a095bf09e14891&v=4&s=39 "Jonathan ")](https://github.com/Jonathan-Hickey)
[![Ken Bonny](https://avatars.githubusercontent.com/u/6417376?u=569af445b6f387917029ffb5129e9cf9f6f68421&v=4&s=39 "Ken Bonny")](https://github.com/KenBonny)
[![Simon Cropp](https://avatars.githubusercontent.com/u/122666?v=4&s=39 "Simon Cropp")](https://github.com/SimonCropp)
[![agileworks-eu](https://avatars.githubusercontent.com/u/5989304?v=4&s=39 "agileworks-eu")](https://github.com/agileworks-eu)
[![Zheyu Shen](https://avatars.githubusercontent.com/u/4067473?v=4&s=39 "Zheyu Shen")](https://github.com/arsdragonfly)
[![Vezel](https://avatars.githubusercontent.com/u/87844133?v=4&s=39 "Vezel")](https://github.com/vezel-dev)
[![ChilliCream](https://avatars.githubusercontent.com/u/16239022?v=4&s=39 "ChilliCream")](https://github.com/ChilliCream)
[![4OTC](https://avatars.githubusercontent.com/u/68428092?v=4&s=39 "4OTC")](https://github.com/4OTC)
[![domischell](https://avatars.githubusercontent.com/u/66068846?u=0a5c5e2e7d90f15ea657bc660f175605935c5bea&v=4&s=39 "domischell")](https://github.com/DominicSchell)
[![Adrian Alonso](https://avatars.githubusercontent.com/u/2027083?u=129cf516d99f5cb2fd0f4a0787a069f3446b7522&v=4&s=39 "Adrian Alonso")](https://github.com/adalon)
[![torutek](https://avatars.githubusercontent.com/u/33917059?v=4&s=39 "torutek")](https://github.com/torutek)
[![Ryan McCaffery](https://avatars.githubusercontent.com/u/16667079?u=c0daa64bb5c1b572130e05ae2b6f609ecc912d4d&v=4&s=39 "Ryan McCaffery")](https://github.com/mccaffers)
[![Seika Logiciel](https://avatars.githubusercontent.com/u/2564602?v=4&s=39 "Seika Logiciel")](https://github.com/SeikaLogiciel)
[![Andrew Grant](https://avatars.githubusercontent.com/devlooped-user?s=39 "Andrew Grant")](https://github.com/wizardness)
[![eska-gmbh](https://avatars.githubusercontent.com/devlooped-team?s=39 "eska-gmbh")](https://github.com/eska-gmbh)
[![Geodata AS](https://avatars.githubusercontent.com/u/5946299?v=4&s=39 "Geodata AS")](https://github.com/geodata-no)
[![Jiri Slachta](https://avatars.githubusercontent.com/u/6891947?u=802cfeb13b070d04c53269fc662b0d58963480dd&v=4&s=39 "Jiri Slachta")](https://github.com/jslachta)


<!-- sponsors.md -->
[![Sponsor this project](https://avatars.githubusercontent.com/devlooped-sponsor?s=118 "Sponsor this project")](https://github.com/sponsors/devlooped)

[Learn more about GitHub Sponsors](https://github.com/sponsors)

<!-- https://github.com/devlooped/sponsors/raw/main/footer.md -->
