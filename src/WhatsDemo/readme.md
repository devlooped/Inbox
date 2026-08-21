# wd

WhatsApp companion REPL.

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
