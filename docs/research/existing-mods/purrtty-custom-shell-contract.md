---
type: API Contract
title: purrTTY custom shell contract (ICustomShell) and cross-mod registration
description: The exported purrTTY.CustomShellContract ABI, its base classes, how a second mod registers a shell (gatOS pattern, mod.toml on both sides), threading expectations, and how a Lua/JS REPL shell for ksa-scripting fits it.
resource: /Users/asherwin/repos/meow-sci/purrtty/purrTTY.CustomShellContract
tags: [purrtty, custom-shell, cross-mod, repl, starmap-dependencies]
sources:
  - id: report
    resource: /references/purrtty-internals-report-2026-10-16.md
    title: purrTTY internals research report (2026-10-16)
  - id: gatos-modtoml
    resource: /Users/asherwin/repos/meow-sci/gatOS/gatOS.GameMod/mod.toml
    title: gatOS mod.toml
  - id: purrtty-modtoml
    resource: /Users/asherwin/repos/meow-sci/purrtty/purrTTY.GameMod/mod.toml
    title: purrTTY mod.toml
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T20:45:00Z }
status: stable
---

# The ABI (namespace `purrTTY.Core.Terminal`)

```csharp
public interface ICustomShell : IDisposable
{
    CustomShellMetadata Metadata { get; }
    bool IsRunning { get; }
    event EventHandler<ShellOutputEventArgs>? OutputReceived;
    event EventHandler<ShellTerminatedEventArgs>? Terminated;
    Task StartAsync(CustomShellStartOptions options, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    void NotifyTerminalResize(int width, int height);
    void RequestCancellation();
    void SendInitialOutput();
}
public record CustomShellMetadata(string Name, string Description, Version Version, string Author, string[] SupportedFeatures);
public class CustomShellStartOptions { int InitialWidth; int InitialHeight; string? WorkingDirectory; Dictionary<string,string> EnvironmentVariables; Dictionary<string,object> Configuration; }
public class CustomShellRegistry
{
    public static CustomShellRegistry Instance { get; }
    public void RegisterShell<T>(string shellId, Func<T> factory) where T : class, ICustomShell;   // probe-instantiates factory()
    public IEnumerable<(string Id, CustomShellMetadata Metadata)> GetAvailableShells();
    public ICustomShell CreateShell(string shellId);
    public bool IsShellRegistered(string shellId);
}
```

`RegisterShell` instantiates the factory once inside a `using` and validates metadata, so the constructor must be trivial and `Dispose` must be safe on a never-started instance. There is no unregister, and auto-discovery only scans the `purrTTY.CustomShells` assembly, so other mods must call `RegisterShell` explicitly.[^report]

Base classes: `BaseCustomShell` (lock, `RaiseOutputReceived`, `RaiseTerminated`), `BaseChannelOutputShell` (channel + pool-thread pump; `OnStartingAsync`/`OnStoppingAsync`, `QueueOutput`), `BaseLineBufferedShell` (cooked-mode line editor with history; `ExecuteCommandLine(string)`, `GetPrompt()`, `HandleClearScreen()`; **ASCII-only** input; `WriteInputAsync` fully synchronous).[^report]

# Registering from another mod (gatOS pattern)

purrTTY exports exactly two assemblies and the consumer must import the same two (StarMap uses the intersection when both lists are non-empty):[^purrtty-modtoml][^gatos-modtoml]

```toml
# purrTTY/mod.toml
[StarMap]
EntryAssembly = "purrTTY.GameMod"
ExportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]

# consumer mod.toml (gatOS; ksa-scripting identical)
[[StarMap.ModDependencies]]
ModId = "purrTTY"
Optional = true
ImportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]
```

The consumer references vendored copies of the two DLLs with `<Private>true</Private>` so they land in its output **and** `deps.json`; with purrTTY installed, the dependency ALC wins (one `Type` identity, one `CustomShellRegistry.Instance`), without it the local copies load and registration lands in a registry nobody reads. gatOS detects the fallback by comparing `typeof(CustomShellRegistry).Assembly.Location` with its own folder. Because the dependency is declared, the consumer's `[StarMapAllModsLoaded]` always runs after purrTTY's; menus enumerate the registry live on every draw, so registration timing is free.[^report]

> **Caveat (2026-10-17, verified in-game and against the real StarMap source):** this gatOS recipe is unsafe. StarMap builds before PR #86 call `GetTypes()` on the entry assembly before the dependency wiring exists, so an entry assembly that *defines* a shell class resolves the contract through its local copy even with purrTTY installed, and every registration lands in a registry nobody reads (and the launcher registry reads as empty). ksa-scripting therefore ships **no** purrTTY DLL (`Private=false`, compile-only) and keeps every purrTTY-typed definition in a separate `KsaScripting.PurrTty.dll` loaded by path from a lifecycle hook once purrTTY is a loaded mod. See [StarMap loader](/ksa-host/starmap-loader-and-assembly-load-context.md) § "When the wiring exists".

# Threading facts that matter for a scripting REPL

- `CustomShellPtyBridge.Write` calls `WriteInputAsync` **synchronously on the game/tick thread**. For `BaseLineBufferedShell` the entire `ExecuteCommandLine` runs on the game thread — this is exactly how `GameConsoleShell` can call `Program.TerminalInterface.Execute` directly. A Lua/JS REPL can touch game state in `ExecuteCommandLine` **without marshalling**, but must return quickly (it stalls the frame otherwise).
- Output (`OutputReceived`) and `Terminated` may be raised from any thread; purrTTY's surface `Write` is the one thread-safe member.[^report]

# Fit for ksa-scripting

A `ScriptReplShell : BaseLineBufferedShell` per runtime ("Lua REPL", "TypeScript REPL") is the cheapest in-game console to ship: it needs no purrTTY change, appears in New Tab / New Window / In-World / Layouts automatically, and its command handler already runs on the game thread. Long-running evaluations must be scheduled onto the hook scheduler rather than executed inline.

[^report]: purrTTY internals research report (2026-10-16)
[^gatos-modtoml]: gatOS mod.toml
[^purrtty-modtoml]: purrTTY mod.toml
