---
type: Runtime Evaluation
title: Node.js embedded via node-api-dotnet (libnode)
description: Why hosting Node.js in-process through Microsoft.JavaScript.NodeApi is not the "TypeScript just works" answer today - the only published libnode is Node 20 (EOL, predates type stripping), the upstream embedding API is unmerged and experimental, Node owns a dedicated thread, one platform per process, and full fs/net is exposed - plus what would have to change to revisit it.
resource: https://github.com/microsoft/node-api-dotnet
tags: [runtime, javascript, nodejs, libnode, node-api-dotnet, rejected]
sources:
  - id: report
    resource: /references/js-runtimes-report-2026-10-16.md
    title: JS/TS runtime embedding research report (2026-10-16)
  - id: libnode
    resource: https://www.nuget.org/packages/Microsoft.JavaScript.LibNode
    title: Microsoft.JavaScript.LibNode on NuGet
  - id: embeddoc
    resource: https://raw.githubusercontent.com/microsoft/node-api-dotnet/main/docs/scenarios/dotnet-js.md
    title: node-api-dotnet embedding scenario doc
  - id: issue472
    resource: https://github.com/microsoft/node-api-dotnet/issues/472
    title: node-api-dotnet #472 "Add support for the current version of Node"
  - id: pr54660
    resource: https://github.com/nodejs/node/pull/54660
    title: nodejs/node #54660 (C-based libnode embedding API, closed unmerged)
  - id: nodets
    resource: https://nodejs.org/api/typescript.html
    title: Node.js TypeScript docs
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:00:00Z }
status: stable
---

# Facts

- `Microsoft.JavaScript.NodeApi` **0.9.27** (2026-09-03, MIT, "Public Preview") is actively maintained, but the only published shared library is `Microsoft.JavaScript.LibNode` **20.1800.215** (2025-04-02) = **Node 20.18.0**; no newer LibNode exists on NuGet.[^libnode]
- Node 20 reached end of life on 2026-04-30 and **predates type stripping** (added v22.6.0), so the embedded Node cannot run `.ts` at all.[^nodets][^report]
- The embedding API (`NodeEmbeddingPlatform(libnodePath)`, `CreateThreadRuntime`, `RunAsync`, `Import`, `StartInspector`) is documented as **experimental**, depends on the upstream PR nodejs/node#54660 which was **closed unmerged** in 2025, and "libnode has breaking changes between versions". Only one `NodeEmbeddingPlatform` may exist per process; all JS runs on a dedicated Node thread (`Run/Post`), so every game-thread callback is a cross-thread hop.[^embeddoc][^pr54660]
- Issue #472 (open, 2026) confirms patched libnode builds are stuck on Node 20; a community shim (alethic/libnode-dotnet, Node 26 + C shim, 0 stars) exists but its author "would rather not maintain this long-term".[^issue472]
- Package weight ~28 MB per RID; the full Node API (fs, net, child_process) is present, so sandboxing would rely on Node's permission model, unverified in embedded mode.

# What would make it viable

A Microsoft-published LibNode ≥ 22.18 (or 24/26 LTS), or owning a Node build pipeline (~1 h per RID per the alethic README) plus an unstable C-shim ABI. Even then: dedicated-thread model, one platform per process (conflicts with any other mod using it), and no sandbox by default.

# Verdict

**Rejected for now.** The appeal (Node's own type stripping, npm ecosystem, `chrome://inspect` debugging) does not survive contact with the packaging reality. Revisit if a current LibNode appears on NuGet. The in-isolate transpile approach with V8 or Jint delivers the same user experience today; see [TypeScript execution options](/runtimes/typescript-execution-options.md).

[^report]: JS/TS runtime embedding research report (2026-10-16)
[^libnode]: Microsoft.JavaScript.LibNode on NuGet
[^embeddoc]: node-api-dotnet embedding scenario doc
[^issue472]: node-api-dotnet #472 "Add support for the current version of Node"
[^pr54660]: nodejs/node #54660 (C-based libnode embedding API, closed unmerged)
[^nodets]: Node.js TypeScript docs
