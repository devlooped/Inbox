[![EULA](https://img.shields.io/badge/EULA-OSMF-blue?labelColor=black&color=C9FF30)](osmfeula.txt)
[![OSS](https://img.shields.io/github/license/devlooped/oss.svg?color=blue)](license.txt)
[![GitHub](https://img.shields.io/badge/-source-181717.svg?logo=GitHub)](https://github.com/devlooped/Inbox)

The [`Inbox`](https://www.nuget.org/packages/Inbox) package is the managed
**Inbox Client Protocol (ICP)** client (`InboxClient`) plus non-transitive MSBuild targets
for adapters that ship a native sidecar.

Apps that only want WhatsApp should PackageReference `WhatsBox` instead — these
targets are **not** transitive.

<!-- include ../../readme.md#inbox -->

## Adapter packing

`Inbox.targets` is imported only by a **direct** reference (nupkg `build/`, not
`buildTransitive/`). In this repo, `WhatsBox` ProjectReferences Inbox and
imports the file by hand.

Pointer + RID packing is **opt-in**. Declare `RuntimeIdentifiers` on the adapter
(and set `InboxNativeBinary`). Without that property, Inbox packs as a plain
managed library — no `runtime.json`, no `PackageId` suffix, no native RID assets.

The adapter:

1. Sets `RuntimeIdentifiers` and builds its native binary (`InboxNativeBinary`,
   optionally `InboxNativeName`, `InboxPackNativeDependsOn`,
   `InboxIncludeNativeAfterTargets`).
2. Packs the pointer: `dotnet pack` → adapter DLL + `runtime.json`.
3. Packs each RID: `dotnet pack -r {rid}` → `runtimes/{rid}/native/` only.

<!-- include https://github.com/devlooped/.github/raw/main/osmf.md -->

<!-- include https://github.com/devlooped/sponsors/raw/main/footer.md -->

<!-- exclude -->