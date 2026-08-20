# wd

WhatsApp companion REPL. Install the pointer package; the CLI picks the
matching RID package (`wd.win-x64`, `wd.linux-x64`, …) automatically:

```bash
dotnet tool install -g wd
wd
```

The command is `wd`. RID matrix matches WhatsBox: `win-x64`, `win-arm64`,
`linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.
