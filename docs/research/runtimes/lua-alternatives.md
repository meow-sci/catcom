---
type: Runtime Evaluation
title: Lua alternatives to LuaJIT (pure-C# interpreters and other bindings)
description: Short verdicts on Lua-CSharp, MoonSharp, NeoLua, NuLua, Lua.NET, KeraLua/NLua, Laylua and WattleScript as alternatives or fallbacks to a direct LuaJIT binding, with versions, Lua dialect, performance and licensing.
tags: [runtime, lua, lua-csharp, moonsharp, nlua, keralua, fallback]
sources:
  - id: report
    resource: /references/lua-runtimes-report-2026-10-16.md
    title: Lua/LuaJIT embedding research report (2026-10-16)
  - id: luacsharp
    resource: https://github.com/nuskey8/Lua-CSharp
    title: Lua-CSharp
  - id: nulua
    resource: https://github.com/nuskey8/NuLua
    title: NuLua
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T22:00:00Z }
status: stable
---

| Library | Version (date) | Lua dialect | Model | License | Verdict |
|---|---|---|---|---|---|
| **Lua-CSharp** (`LuaCSharp`) | 0.5.7 (2026-09-26) | **5.2** interpreter (not 5.4), UTF-16 strings | pure C#; async/await host functions with `CancellationToken`; `LuaPlatform` for sandboxing; debug hooks ("a timeout checked by a hook cannot interrupt a blocking C# function"); not thread-safe | MIT | **best pure-managed fallback** for Lua: no native code, ≈320 ns per host call (vendor chart), far slower than LuaJIT on Lua code; different dialect from LuaJIT (scripts may not be portable both ways)[^luacsharp] |
| MoonSharp | 2.0.0 (2016); 3.0.0-beta.1 (2026-07-04) | "99% Lua 5.2" (no weak tables) | pure C# | BSD-3 | revived as a beta; slower than Lua-CSharp per its chart; watch, don't adopt yet |
| NeoLua | 1.3.19 (2025-12) | ~5.3 on the DLR, not 100% compatible | compiles Lua to IL | Apache-2.0 | DLR dependency and dialect drift; no |
| **NuLua.LuaJit** | 0.1.0 preview (2026-06-22) | 5.1–5.5, LuaJIT, Luau | csbindgen `DllImport("luajit")` + Rust-built natives with protected C shims; per-RID runtime packages incl. osx-arm64 | MIT | preview; **borrow its protected-shim design** (`nulua_pgetfield` etc.), do not depend on it[^nulua] |
| Lua.NET / JAJ.Packages.LuaNET | 6.0.1 (2025-06) / 1.1.0 (2025-01) | includes LuaJIT (header says 2.1.0-beta3) | delegate-based `DllImport("lua51")`; opaque bundled binaries | MIT | stale; use as a signature reference only |
| KeraLua / NLua | 1.4.10 (2026-09-12) / 1.7.9 (2026-05-01) | 5.4 | P/Invoke to `lua54`; NLua adds a reflection CLR bridge | MIT | cannot target LuaJIT (5.4-only imports); NLua's README itself says "use plain Lua to do your sandbox" |
| Laylua | 1.0.0 (2025-01) | 5.4 | wrapper with memory/instruction sandbox | **LGPL-3.0** | license and 5.4; no |
| WattleScript | 0.6.0-beta1 (2022) | MoonSharp-derived 5.2 | pure C# | BSD-3 | abandoned |

Measured context: LuaJIT via direct P/Invoke costs ≈1 ns per native call and 5–7 ns per host callback on this workstation, so the managed interpreters trade one to two orders of magnitude of script throughput for the absence of a native library. For catcom the native library problem is already solved by the purrTTY resolver pattern, so **LuaJIT direct is the primary Lua runtime and Lua-CSharp is the designated fallback** (same `IScriptRuntime` adapter, different dialect caveats surfaced in docs).[^report]

[^report]: Lua/LuaJIT embedding research report (2026-10-16)
[^luacsharp]: Lua-CSharp
[^nulua]: NuLua
