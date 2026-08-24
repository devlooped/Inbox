# Pubsubbox files are a blob URL in Chat `content.binary`

Chat has no upload API (`chat.upload` / `CreateMessage` External are `UnsupportedOperation`). `content.text` and `content.binary` cannot coexist (one table `Body` + `BodyType`). We upload the file to the hub’s storage account, put `base64(utf8(url))` in `content.binary`, and put the ICP caption on the blob as `x-ms-meta-text`. Pubsubbox maps that to `files: true` / `attachments: "single"` in both directions. The JSON-RPC client never sees Azure URLs or metadata keys.

**Considered:** base64 of the whole file in `content.binary` (64 KB cap); URL in `content.text` and sniff (collides with ordinary links); advertise `files: false`.
