# ksa-scripting — Implementation Guide (the build book)

This document is the single source of truth for everyone (human or agent) implementing ksa-scripting. It pins the project layout, conventions, exact C# contracts, the script-facing API, the work packages and their acceptance criteria. When this guide and a research page disagree about *what to build*, this guide wins; when they disagree about *a fact about KSA/purrTTY/gatOS*, the research page wins and this guide must be corrected.

Research bundle: [`docs/research/index.md`](research/index.md). Plans: [`plans/README.md`](../plans/README.md).

---

## 0. Environment facts

| Item | Value |
|---|---|
| Game | Kitten Space Agency build 2026.10.10.5554, `net10.0`, self-contained .NET 10.0.10 |
| Reference DLLs (compile only) | `/Users/asherwin/repos/meow-sci/ksa-game-assemblies/current/dll/` (also `KSA_DLL_DIR` env) |
| Decompiled sources | `/Users/asherwin/repos/meow-sci/ksa-game-assemblies/current/decomp/` |
| purrTTY repo (branch `feature/scripting`) | `/Users/asherwin/repos/meow-sci/purrtty` |
| gatOS repo (MIT; port source) | `/Users/asherwin/repos/meow-sci/gatOS` |
| StarMap loader source | `/Users/asherwin/repos/meow-sci/gatOS/thirdparty/starmap` |
| NuGet feeds | nuget.org + `starmap` (GitHub packages, credentials already in the user's NuGet.Config) |
| Dev box | macOS arm64, .NET SDK 10.0.100, clang 21, GNU make 3.81, Docker 29, Homebrew `luajit` 2.1 (do **not** ship it), `nvim` 0.12.6, node 24, bun 1.4 |
| Cannot do here | run the game (Windows/Linux only). In-game validation is a checklist for the user; everything else must be proven by builds, unit tests, and runtime spikes on macOS. |
| CI (GitHub Actions, to be added) | ubuntu-latest, windows-latest, macos-14; game DLLs provided via `KSA_DLL_DIR` secret or compile-removal of game code |

Mod folder at runtime: `<Documents>/My Games/Kitten Space Agency/mods/ksa-scripting/` (dist). User data: same folder (`scripts/`, `bindings.toml`, `terminals.toml`, `editor/`, `ksa-scripting.toml`). Dev deploy target: `$(KSA_SCRIPTING_DIST_DIR)` else `$(HOME)/repos/meow-sci/mods/ksa-scripting/` on macOS/Linux, `%USERPROFILE%\Documents\My Games\Kitten Space Agency\mods\ksa-scripting\` on Windows.

---

## 1. Solution layout

```
ksa-scripting.slnx
Directory.Build.props                      # copy gatOS's; rename GATOS_DIST_DIR → KSA_SCRIPTING_DIST_DIR; keep KSAFolder resolution
src/
  KsaScripting.Core/                       # GAME-FREE. No KSA/Brutal/StarMap/Harmony/ImGui/purrTTY references.
  KsaScripting.Ksa/                        # refs KSA DLLs + Lib.Harmony + Core. Readers, actuators, executor, patches, slots, bridge.
  KsaScripting.GameMod/                    # StarMap entry. refs Ksa, Core, Runtime.Lua, Runtime.V8, purrTTY contract (vendored), StarMap.API, Lib.Harmony, KSA DLLs (ImGui).
  KsaScripting.Runtime.Lua/                # refs Core only. LibraryImport bindings to lua51 + adapter.
  KsaScripting.Runtime.V8/                 # refs Core + Microsoft.ClearScript.V8 (+ native packages for win-x64, linux-x64, osx-arm64).
tests/
  KsaScripting.Core.Tests/                 # NUnit; refs Core
  KsaScripting.Runtime.Lua.Tests/          # NUnit; refs Runtime.Lua + Core; needs runtimes/<rid>/native/<luajit lib> (built by tools/build-luajit.sh)
  KsaScripting.Runtime.V8.Tests/           # NUnit; refs Runtime.V8 + Core
  KsaScripting.Ksa.CompileCheck/           # class library, build-only: one Harmony stub per patched member (grows from spikes/HookCompileCheck)
editor/
  config/ksa-editor/init.lua               # shipped nvim config (see §10.5)
  noshell / noshell.cmd                    # stub 'shell' that prints a message and exits 1
  luarc.json, tsconfig.template.json
scripts-lib/                               # shipped script libraries, copied to <user>/scripts/lib on every start (overwrite)
  tui.ts  tui.lua  README.md
tools/
  build-luajit.sh  build-luajit.ps1  luajit.pin  host_shim.c
  fetch-nvim.sh  fetch-nvim.ps1  fetch-lsp.sh  fetch-lsp.ps1  fetch-ts.sh  fetch-ts.ps1  build-ts-parser.sh
  check-dist.py
vendor/purrTTY/                            # pinned purrTTY.CustomShellContract.dll + purrTTY.Logging.dll (refresh after Plan 00b merges)
spikes/                                    # existing, keep building
docs/research/                             # existing OKF bundle (update log.md when facts change)
```

Project settings (all projects): `net10.0`, `LangVersion 13.0`, `Nullable enable`, `ImplicitUsings enable`, `TreatWarningsAsErrors true`, `WarningsNotAsErrors CS1591`, `GenerateDocumentationFile true` (test projects false). Game DLL references use `<Private>false</Private>` and `Condition="Exists('$(KSAFolder)/X.dll')"`. `Lib.Harmony 2.4.2` and `StarMap.API 0.3.6` with `PrivateAssets=all` (never shipped). Game-coupled source in `KsaScripting.Ksa` and `KsaScripting.GameMod` is compile-removed when `KSA.dll` is absent (`<Compile Remove="**/*.cs" />` guarded by `KsaAssembliesPresent`) so game-free CI still builds Core/runtimes/tests.

Dist (`CopyCustomContent` in `KsaScripting.GameMod.csproj`, after build): wipe `$(DistDir)*` except `ksa-scripting.toml`, `scripts/`, `bindings.toml`, `terminals.toml`, `editor/`; copy `mod.toml`, `$(ProjectDepsFilePath)`, all `$(TargetDir)*.dll` except `0Harmony.dll;StarMap.API.dll`; copy `runtimes/**` (natives), `scripts-lib/**` → `scripts-lib/`, `editor/config/**`, `editor/noshell*`, `LICENSE`, `THIRD-PARTY-NOTICES.md`, `third-party-licenses/*`; copy `editor/<rid>/**` (fetched blobs) with `SkipUnchangedFiles` and no wipe.

`mod.toml`:

```toml
name = "ksa-scripting"
description = "In-band Lua (LuaJIT) and TypeScript scripting for KSA: game-loop hooks, terminal scripts on purrTTY surfaces, and a shipped Neovim editor."
version = "0.1.0"
author = "meow sci"

[StarMap]
EntryAssembly = "KsaScripting.GameMod"

[[StarMap.ModDependencies]]
ModId = "purrTTY"
Optional = true
ImportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]
```

---

## 2. Conventions

- **File-scoped namespaces**; one public type per file; `sealed` by default; records for DTOs; `readonly record struct` for small values.
- **No KSA types** (nor Brutal, Harmony, ImGui, purrTTY) in `KsaScripting.Core`, `Runtime.Lua`, `Runtime.V8`. Only handles (strings) and `ScriptValue` cross runtime boundaries.
- **Every member that touches a KSA type** carries `[KsaAnchor("<Type.Member> …", SourceFile = "KSA/X.cs", GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Low|Medium|High)]` (ported from gatOS `Game/Ksa/KsaAnchor.cs`).
- **Nothing on the sim path may throw.** Harmony patch bodies and slot bodies are `try/catch` with log-once + self-disable (`_faulted` flag). Executors return `CommandResult.Fault(message)` instead of throwing.
- **Game thread only:** all script execution, all `Vehicle`/`PartTree`/`FlightComputer` access, all purrTTY window/registry calls. Off-thread entry points (purrTTY tick-thread input, nvim RPC thread, file watcher, PTY exit callbacks) go through `GameThreadQueue.Post(...)`.
- **Logging:** `KsaScripting.Core.Logging.Log` static with a pluggable `ILogSink`; GameMod installs a sink that writes `Console.WriteLine` (stdout) **and** `Program.ConsoleWindow.Print` (in-game, thread-safe). Format `[ksa-scripting] <level>: <message>`. Log once for repeated faults (`LogOnce(key, …)`).
- **Config:** Tomlyn 2.10.1, snake_case keys, clamp-and-log, atomic temp+rename save (`AtomicFile` ported from gatOS). Keys and defaults in §9.
- **Tests:** NUnit 4, `dotnet test --nologo -v quiet` must print nothing on pass, **no fixed sleeps** (use `TaskCompletionSource`/`SemaphoreSlim`/polling with deadlines), no Console output from passing tests, deterministic.
- **Commits:** conventional commits (`feat(core): …`, `feat(ksa): …`, `feat(lua): …`, `feat(v8): …`, `feat(gamemod): …`, `feat(purrtty): …`, `test(...)`, `build(...)`, `docs(...)`, `chore(...)`); commit only your own paths (`git add <paths>`); retry on `index.lock`; never rewrite history; end every commit message with the two attribution trailer lines used in this repo's first commit (`Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` and `Claude-Session: https://claude.ai/code/session_014EZSZCFHBbCrMjM7L1WBMM`).
- **Zero warnings.** Fix, do not suppress (except documented `NoWarn` for third-party-derived files, e.g. `CA2255;IL3000` on the native resolver).

---

## 3. Core contracts (`KsaScripting.Core`) — exact shapes

Namespaces: `KsaScripting.Core.Values`, `.Runtime`, `.Hosting`, `.Hooks`, `.Scheduling`, `.Catalog`, `.Terminals`, `.Snapshots` (ported gatOS DTOs), `.Commands` (ported gatOS command model), `.Api` (schema + generators), `.Editor` (nvim RPC client), `.Config`, `.Logging`, `.Persistence`.

### 3.1 Values

```csharp
namespace KsaScripting.Core.Values;
public enum ScriptValueKind { Null, Bool, Number, String, Bytes, List, Map, Function, Handle }
public sealed class ScriptValue
{
    public ScriptValueKind Kind { get; }
    public static readonly ScriptValue Null;
    public static ScriptValue From(bool v); From(double v); From(string v); From(ReadOnlyMemory<byte> v);
    public static ScriptValue List(IReadOnlyList<ScriptValue> items); Map(IReadOnlyDictionary<string, ScriptValue> items);
    public static ScriptValue Function(ScriptCallable fn); Handle(string handle);
    public bool AsBool(); double AsNumber(); string AsString(); ReadOnlyMemory<byte> AsBytes();
    public IReadOnlyList<ScriptValue> AsList(); IReadOnlyDictionary<string, ScriptValue> AsMap(); ScriptCallable AsFunction(); string AsHandle();
    public bool IsNull { get; }
    public string ToJson();                                 // System.Text.Json; functions/handles serialise as {"$fn":id} / {"$handle":"..."}
    public static ScriptValue FromJson(string json);
    public static ScriptValue FromObject(object? o);        // reflection over records/primitives/lists/dictionaries; camelCase keys; NaN/Inf → 0
}
public sealed class ScriptCallable : IDisposable           // opaque runtime-owned function handle
{
    public string RuntimeId { get; }  public long Token { get; }  public string? Name { get; }
    public bool IsDisposed { get; }  public void Dispose();  // runtime releases its reference
    internal ScriptCallable(string runtimeId, long token, string? name, Action<ScriptCallable> onDispose);
    public static ScriptCallable Create(string runtimeId, long token, string? name, Action<ScriptCallable> onDispose);
}
```

### 3.2 Runtime abstraction

```csharp
namespace KsaScripting.Core.Runtime;
public sealed record ScriptSource(string Name, string Path, string Code, string Language);   // Language: "lua" | "typescript"
public sealed record ScriptModule(string RuntimeId, string Name, long Token);                   // opaque
public sealed record RuntimeStats(long HeapBytes, double LastCallMs, long CallsTotal, bool JitEnabled, string Version);
public sealed class ScriptLoadException : Exception { public string File { get; } public int Line { get; } public int Column { get; } public string Snippet { get; } … }
public sealed class ScriptRuntimeException : Exception { public string Traceback { get; } public string? File { get; } public int? Line { get; } … }
public sealed record RuntimeOptions(string ScriptsDir, string StubsDir, long HeapLimitBytes, bool JitEnabled, int? DebugPort);

public interface IScriptRuntime : IDisposable
{
    string Id { get; }                       // "lua" | "v8"
    string Language { get; }                 // "lua" | "typescript"
    bool IsAvailable { get; }                // false when the native library failed to load (reason in UnavailableReason)
    string? UnavailableReason { get; }
    void Initialize(IHostFacade host, RuntimeOptions options);       // game thread; idempotent
    ScriptModule Load(ScriptSource source);                            // compile/transpile + evaluate top level; throws ScriptLoadException
    IReadOnlyList<string> Exports(ScriptModule module);                // exported function names
    ScriptCallable GetExport(ScriptModule module, string name);        // throws KeyNotFoundException
    ScriptValue Invoke(ScriptCallable fn, ReadOnlySpan<ScriptValue> args);   // synchronous call on the game thread; throws ScriptRuntimeException
    bool Advance(TimeSpan budget);                                     // run pending continuations (promises/coroutines); true if work remains
    void Unload(ScriptModule module);                                  // release everything owned by the module
    void Interrupt();                                                  // any thread; aborts the current Invoke/Advance
    RuntimeStats Stats { get; }
}
```

Rules every runtime implements: `Invoke` of an async function returns immediately with `ScriptValue.Null` (the continuation is scheduled); host waits (`IHostFacade.Wait`) are turned into the runtime's native await; exceptions inside scripts never escape as anything but `ScriptRuntimeException`; `Interrupt` makes the current call throw `ScriptRuntimeException("interrupted")`.

### 3.3 Host facade (what runtimes bind) and the game bridge (what Ksa implements)

```csharp
namespace KsaScripting.Core.Hosting;
public sealed class HostWait { public long Id { get; } public string Kind { get; } public Task<ScriptValue> Task { get; } internal TaskCompletionSource<ScriptValue> Source { get; } }
public sealed record ActResult(CommandResult? Immediate, HostWait? Pending);
public sealed record BindingOptions(bool Sync = false, string? Vessel = null, string Clock = "ut", double IntervalSeconds = 0);

public interface IHostFacade
{
    // reads
    ScriptValue Read(string handle, string path);          // "vessel:<id>" + "position.cci" | "time" + "ut" | "body:<id>" + "mass" | "system" + "home"
    ScriptValue Snapshot(string handle);                   // whole record as a map (vessel, body, time, system, term:<n>)
    ScriptValue List(string kind);                         // "vessels" | "bodies" → list of handles; "events" → event catalog maps; "exports" → …
    // writes (gatOS action vocabulary)
    ActResult Act(string action, string? vesselId, int ordinal, double value, double[]? values, string? token, string? aux);
    // waits (resolved on the game thread in the Step slot)
    HostWait Wait(string kind, ScriptValue arg);           // "sleep"(seconds,clock) | "nextStep" | "nextFrame" | "until"(fn) | "waitEvent"(name,vessel) | "termRead"(termId)
    // hooks
    Guid Subscribe(string kind, string key, ScriptCallable fn, BindingOptions opts);   // kind: "event" | "tick" | "service" | "command"
    void Unsubscribe(Guid id);
    // misc
    void Log(int level, string message);                   // 0 debug, 1 info, 2 warn, 3 error
    void ConsoleExec(string command);                      // Program.TerminalInterface.Execute on the game thread
    ScriptValue StateGet(string key);  void StateSet(string key, ScriptValue value);
    // terminals
    void TermWrite(int termId, ReadOnlySpan<byte> bytes);  ScriptValue TermInfo(int termId);   // {cols, rows, attached}
    void TermSetTitle(int termId, string title);  void TermPolicy(int termId, bool stopWhenDetached);
    // identity of the calling script (set by the scheduler before each invocation)
    string CurrentScript { get; }
}

public interface IGameBridge                                // implemented by KsaScripting.Ksa.KsaBridge; game thread only
{
    ScriptValue Read(string handle, string path);
    ScriptValue Snapshot(string handle);
    IReadOnlyList<string> List(string kind);
    CommandResult Execute(SimCommand command);              // Frame-phase only; never throws
    void QueueSolver(SimCommand command, Action<CommandResult> onResult);   // Solver-phase; result delivered in a later Step slot
    double UtSeconds { get; }  double SimDt { get; }  double Warp { get; }  bool Paused { get; }  ulong FrameNumber { get; }
    IReadOnlyList<EventDescriptor> EventCatalog { get; }
    void ConsoleExec(string command);
}
public sealed record EventDescriptor(string Name, string Description, IReadOnlyList<string> PayloadFields, string Producer);   // Producer: "harmony" | "differ"
```

`HostFacade : IHostFacade` (Core) composes `IGameBridge`, `Scheduler`, `HookRegistry`, `TerminalRegistry`, `ScriptStateStore` and `Log`. `Act` builds `new SimCommand(vesselId ?? "", action, ordinal, value) { Values = values, Token = token, Aux = aux }`; `PhaseFor(action) == Frame` → `Immediate = bridge.Execute(cmd)`; `Solver` → `QueueSolver` and a `HostWait` of kind `"act"` completed with the result map.

### 3.4 Events, hooks, scheduler

```csharp
namespace KsaScripting.Core.Hooks;
public sealed record ScriptEvent(double Ut, string Type, string? VesselId, IReadOnlyDictionary<string, ScriptValue> Payload);
public enum BindingKind { Event, Tick, Service, Command }
public sealed record Binding(Guid Id, string Script, string Export, BindingKind Kind, string Key, BindingOptions Options, bool FromCode, bool Enabled);
public sealed class HookRegistry
{
    public Guid Add(string script, BindingKind kind, string key, ScriptCallable fn, BindingOptions opts, bool fromCode);
    public void Remove(Guid id);  public void RemoveAll(string script);
    public IReadOnlyList<(Binding Binding, ScriptCallable Fn)> ForEvent(string type, string? vesselId, bool sync);
    public IReadOnlyList<(Binding Binding, ScriptCallable Fn)> ForTick(string key);          // "frame" | "step" | "every"
    public IReadOnlyList<(Binding Binding, ScriptCallable Fn)> Services();
    public IReadOnlyList<Binding> All();  public IReadOnlyList<Binding> ForScript(string script);
}
public sealed class BindingStore   // bindings.toml: [[binding]] script, export, kind, key, sync, vessel, clock, interval_seconds, enabled
{
    public BindingStore(string path);  public IReadOnlyList<BindingRecord> Load();  public void Save(IReadOnlyList<BindingRecord> records);
}
public sealed record BindingRecord(string Script, string Export, string Kind, string Key, bool Sync, string? Vessel, string Clock, double IntervalSeconds, bool Enabled);
public sealed class EventBus
{
    public void PublishSync(ScriptEvent ev);     // called inside a Harmony patch: invokes sync subscriptions under the sync budget, then queues for async
    public void Publish(ScriptEvent ev);         // queues for async delivery in the next Step slot
    public IReadOnlyDictionary<string, long> Counters { get; }   // per type, for the UI
}

namespace KsaScripting.Core.Scheduling;
public sealed record StepInfo(double Ut, double Dt, long StepIndex, ulong Frame, bool StepLanded);
public sealed class Scheduler
{
    public Scheduler(ScheduleConfig cfg, HookRegistry hooks, EventBus events, IRuntimeSet runtimes, Watchdog watchdog, Action<string, Exception> onScriptError);
    public void StepSlot(StepInfo step);         // game thread, from Universe.ApplyVehicleSolvers postfix (or the AfterOnFrame fallback)
    public HostWait CreateWait(string kind, ScriptValue arg, string script);
    public void CompleteWait(HostWait wait, ScriptValue result);   // game thread
    public ScheduleStats Stats { get; }
}
```

`StepSlot` order (all budgeted by `cfg.FrameBudget`): 1) resolve due waits (`sleep` on ut/wall/render clocks, `nextStep` if `StepLanded`, `nextFrame`, `until` predicates, `waitEvent` matches, `act` results, `termRead` with queued input); 2) deliver queued async events to their subscriptions (one `Invoke` each, FIFO per script); 3) tick hooks (`frame` every slot; `step` when `StepLanded`; `every` when its interval elapsed on its clock); 4) start not-yet-started services; 5) `runtime.Advance(remaining)` for each runtime, round-robin until budget exhausted or no work; 6) flush terminals. Each `Invoke` is wrapped: set `host.CurrentScript`, arm the watchdog (sync budget for sync hooks, call budget otherwise), catch `ScriptRuntimeException` → `onScriptError(script, ex)` and fault-count.

### 3.5 Catalog, terminals, persistence

```csharp
namespace KsaScripting.Core.Catalog;
public enum ScriptState { Idle, Loading, Running, Service, Error, Disabled }
public sealed record ScriptInfo(string Name, string Path, string Language, ScriptState State, string? LastError, IReadOnlyList<string> Exports, bool HasTerminal, bool Autostart);
public sealed class ScriptCatalog
{
    public ScriptCatalog(string scriptsDir, IRuntimeSet runtimes, HookRegistry hooks, BindingStore bindings, Scheduler scheduler, IHostFacade host);
    public IReadOnlyList<ScriptInfo> Scan();                      // files under scriptsDir (recursive, excluding lib/ and stubs/), .lua and .ts
    public ScriptInfo Load(string name);                          // compile + evaluate; register code-declared hooks; apply stored bindings; previous version stays live on failure
    public void Run(string name);                                 // ad-hoc: Load then invoke `main(ctx)` once (if exported)
    public void Start(string name);  public void Stop(string name);   // service: invoke `main(ctx)` as a service; Stop flips ctx.signal and unloads
    public void Reload(string name);  public void Unload(string name);
    public ScriptInfo? Get(string name);
    public event Action<ScriptInfo>? Changed;
    public void OnFileChanged(string path);                       // from watcher/nvim RPC (already marshalled to the game thread)
}
public sealed class ScriptWatcher : IDisposable { public ScriptWatcher(string dir, Action<string> onChanged /* debounced 150 ms, content-hash checked, posted to game thread by caller */); }

namespace KsaScripting.Core.Terminals;
public interface ITerminalSink { void Write(ReadOnlySpan<byte> bytes); void Terminate(string message); }
public sealed class ScriptTerminal
{
    public int Id { get; }  public string Script { get; }  public int Cols { get; }  public int Rows { get; }  public int Attached { get; }
    public bool StopWhenDetached { get; set; }
    public void Write(ReadOnlySpan<byte> bytes);                 // from script: appends to ring buffer + pending flush
    public void Flush();                                         // Step slot: sends pending bytes to every sink wrapped in DEC 2026
    public void Attach(ITerminalSink sink, int cols, int rows); // replays ring buffer to the new sink; raises attach + resize events
    public void Detach(ITerminalSink sink);
    public void EnqueueInput(ReadOnlyMemory<byte> bytes);        // from a sink (already on the game thread); completes a pending termRead wait or queues
    public void NotifyResize(ITerminalSink sink, int cols, int rows);   // reports the smallest attached grid
    public event Action<ScriptEvent>? Event;                     // "term.resize" {cols,rows}, "term.attach", "term.detach", "term.cancel"
}
public sealed class TerminalRegistry { public ScriptTerminal Create(string script); public ScriptTerminal? Get(int id); public ScriptTerminal? ForScript(string script); public void Remove(int id); }

namespace KsaScripting.Core.Persistence;
public sealed class ScriptStateStore { public ScriptValue Get(string script, string key); public void Set(string script, string key, ScriptValue v); public string SerializeToml(); public void LoadToml(string toml); }
```

### 3.6 API schema and generators

```csharp
namespace KsaScripting.Core.Api;
public enum ApiKind { Property, Method, Event }
public sealed record ApiType(string TsType, string LuaType);
public sealed record ApiParam(string Name, ApiType Type, bool Optional, string Doc);
public sealed record ApiMember(string Name, ApiKind Kind, ApiType Type, IReadOnlyList<ApiParam> Params, string Doc, string? ActionKey, string? Phase, bool Async, string? ReadPath);
public sealed record ApiClass(string Name, string Doc, IReadOnlyList<ApiMember> Members);
public static class ApiSchema { public static IReadOnlyList<ApiClass> Classes { get; } /* ksa, Time, VesselRef, EngineRef, LightRef, DockingRef, DecouplerRef, AnimationRef, RcsRef, SolarRef, BodyInfo, Terminal, Console, contexts, event payload records */ }
public static class StubGenerators
{
    public static string TypeScriptDeclarations(IReadOnlyList<ApiClass> classes);   // host.d.ts (declare namespace ksa { … })
    public static string LuaCats(IReadOnlyList<ApiClass> classes);                   // ksa.lua with ---@class / ---@field / ---@param / ---@return
    public static string TsConfigJson();  public static string LuaRcJson();
}
```

Snapshot tests pin the generated text (`tests/KsaScripting.Core.Tests/Golden/host.d.ts`, `ksa.lua`).

### 3.7 Runtime ↔ prelude ↔ host conventions (pinned 2026-10-16 by Manager A; binding for WP2, WP3, WP5, WP6)

These close gaps between §3.3, §5 and §6 so the preludes (WP3, `src/KsaScripting.Core/Api/Preludes/ksa.{js,lua}`), the host (WP2) and the runtimes (WP5/WP6) agree without further negotiation.

**Contract additions (Core, WP1):**

- `IHostFacade` gains `IDisposable EnterScript(string script)` (attributes host calls to a script until disposed; the scheduler wraps every `Invoke`, runtimes wrap every continuation they resume inside `Advance`) and `void ReportError(ScriptRuntimeException error)` (async errors — unhandled promise rejections, coroutines that error inside `Advance` — attributed to the current script and counted toward `fault_limit`). `CurrentScript` is `""` when none.
- `HostWait` has a **public** constructor `HostWait(long id, string kind)` (TCS with `RunContinuationsAsynchronously`) and `Complete(ScriptValue)`, `Fail(Exception)`, `Cancel()`, `IsCompleted`, `ToScriptValue()` → `{"$wait": id}`; test fakes may create waits.
- `StepInfo` is `(double Ut, double Dt, long StepIndex, ulong Frame, bool StepLanded, double PlayerDt = 0)`; the `render` clock accumulates `PlayerDt`.
- `IRuntimeSet`/`RuntimeSet`, `ScriptLanguages`, `CommandResult` factories (`Fault(msg)` …), `CommandResultExtensions.ToScriptValue()` → `{ok, outcome, message}`, `GameThreadQueue.PostWork/DrainWork/PostCommand`, `Core.Snapshots.ScriptViews` (the script-facing projection of `VesselSnapshot`; §5's grouped getters `position`, `velocity`, `attitude`, `altitude`, `mass`, `power`, `battery` come from it, not from raw record names), `TimeView`.

**JS host object `__host` (V8, camelCase):** `read(handle, path)`, `snapshot(handle)`, `list(kind)`, `act(action, vesselId, ordinal, value, values, token, aux)`, `wait(kind, arg)`, `subscribe(kind, key, fn, opts)`, `unsubscribe(id)`, `log(level, msg)`, `consoleExec(cmd)`, `stateGet(key)`, `stateSet(key, value)`, `termWrite(termId, stringOrBytes)`, `termInfo(termId)`, `termSetTitle(termId, title)`, `termPolicy(termId, stopWhenDetached)`. Values cross as: Null→`null`, Bool/Number/String natively, Bytes↔`Uint8Array` (strings passed to `termWrite` are UTF-8 encoded), List↔array, Map↔plain object, Handle→string, Function↔JS function (a JS function passed to the host, also inside maps, becomes a `ScriptCallable`). **`wait` returns a Promise** resolving to the wait result; **`act` returns the CommandResult object for Frame actions and a Promise of it for Solver actions** (the runtime converts a pending `HostWait`); a failed/cancelled wait rejects. Host exceptions surface as JS `Error`s. `subscribe` returns the binding id as a string (`Guid` "D" format); `unsubscribe` takes that string.

**Lua host table `ksa_host` (LuaJIT, snake_case):** `read`, `snapshot`, `list`, `act`, `wait`, `subscribe`, `unsubscribe`, `log`, `console_exec`, `state_get`, `state_set`, `term_write`, `term_info`, `term_set_title`, `term_policy`, same parameters in the same order. Every function returns `(true, value)` or `(false, "message")` and never raises. `wait` returns `(true, {["$wait"] = id})`; `act` returns `(true, resultTable)` (Frame) or `(true, {["$wait"] = id})` (Solver). The prelude raises with `error(msg, 2)` when `ok` is false and passes wait tables to `coroutine.yield(w)`. The runtime recognises a yielded table carrying `$wait`, keeps the coroutine parked, and resumes it with `(result)` when the `HostWait` completes, or with `(nil, "error message")` when it fails or is cancelled (the prelude then raises). Map keys cross **unchanged** (camelCase, as the host produced them); the Lua prelude converts host tables to snake_case keys for scripts and converts the option keys it sends back (`interval_seconds` → `intervalSeconds`, `timeout_seconds` → `timeoutSeconds`).

**Option maps** read by the runtimes when building `BindingOptions` from `subscribe(kind, key, fn, opts)`: `sync` (bool), `vessel` (string), `clock` (string), `intervalSeconds` (number); missing keys take the `BindingOptions` defaults.

**Context objects:** the scheduler invokes hooks/`main` with ONE argument, a Map containing `"$ctx": true`. When marshalling an `Invoke` argument that is such a map, the runtime converts it to a native object/table and passes it through the prelude factory of that script — JS `globalThis.ksa.makeContext(obj)`, Lua `ksa.make_context(tbl)` — and hands the result to the function. Context map keys (camelCase): `$ctx`, `kind` (`"event"|"tick"|"service"|"main"|"terminal"`), `script` `{name, path}`; events add `event {ut, type, vesselId, payload}`, `vesselId`, `sync`; ticks add `ut`, `dt`, `stepIndex`, `frame`; service/main/terminal add `signal` (the script name; the prelude builds `ctx.signal = {cancelled → read("signal:<name>", "cancelled"), onCancel(fn) → subscribe("event", "signal.cancel", fn, {})}`) and `termId` (number, when the script owns a terminal; the prelude builds `ctx.term`). `ctx.vessel` is built by the prelude from `vesselId`. Commands (`ksa.command`) are invoked with a single String argument (the argument text), not a context.

**Exports:** TS — the module namespace's function-valued members. Lua — the chunk's returned table merged with `ksa.__exports` (filled by `ksa.export(name, fn)`); on a name clash the returned table wins.

**Wait kinds** (`IHostFacade.Wait(kind, arg)`): `sleep` arg `{seconds, clock?}` → `null`; `nextStep` → `{ut, dt, stepIndex, frame}`; `nextFrame` → `null`; `until` arg `{fn, timeoutSeconds?, clock?}` → `true`, or `false` on timeout; `waitEvent` arg `{name, vessel?, timeoutSeconds?}` → `{ut, type, vesselId, payload}`, or `null` on timeout; `termRead` arg `{termId}` → bytes; `act` (created by `Act` for Solver actions) → `{ok, outcome, message}`.

**Handles and reads:** `vessel:<id>` paths are dotted paths into `ScriptViews.Vessel` (e.g. `position.cci.x`, `battery.fraction`, `engines.0.active`); `snapshot("vessel:<id>")` is the raw `VesselSnapshot` via `ScriptValue.FromObject`; `time` paths are `TimeView` keys (`ut`, `warp`, `simDt`, `paused`, `warpSpeeds`, `autoWarp`, `autoWarpTargetUt`, `activeVesselId`, `frame`, `stepIndex`); `body:<id>` → `BodySnapshot`; `system` → `{name, home, sun}`; `term:<n>` → `{cols, rows, attached}`; `signal:<script>` path `cancelled` → bool. `List("vessels")` / `List("bodies")` → list of id **strings** (not prefixed handles); `List("events")` → `EventDescriptor` maps `{name, description, payloadFields, producer}`; `List("scripts")` → script names.

**Targeted events:** an event whose payload contains `"$script": <name>` is delivered (and matched by `waitEvent`) only for that script — used for `term.*` (`term.data`, `term.resize`, `term.attach`, `term.detach`, `term.cancel`) and `signal.cancel`. Payload maps use camelCase keys.


---

## 4. Game side (`KsaScripting.Ksa`)

Namespaces `KsaScripting.Ksa.{Readers,Actuators,Commands,Events,Slots,Bridge}`.

- **Ports from gatOS (copy, re-namespace, keep behaviour, keep `[KsaAnchor]`s):**
  - Core (game-free): `gatOS.SimFs/Snapshots/SimSnapshot.cs` (all DTO records), `gatOS.SimFs/Telemetry/{Sanitize,EnumText,EventDiffer,SampleClock}.cs`, `gatOS.SimFs/Commands/{SimCommand,SimActions,CommandResult,ICommandExecutor,TranslateRules,RotateRules,ImpulseRules,ScaleRules}.cs`, plus `CommandQueue.cs` as `GameThreadQueue` (phases Frame/Solver, TCS with `RunContinuationsAsynchronously`, 2 s timeout, 64 per drain).
  - Ksa: `gatOS.GameMod/Game/Ksa/{KsaAnchor,KsaHealth,KsaCatalog}.cs` → `ActionExecutor : ICommandExecutor` (keep only the vessel/module/debug/`camera.focus` routes; remove audio/schedule/paint/fx/welds/thug_life/iva/camera-director branches and return `Unsupported("not available in ksa-scripting v1")` for those keys), `Readers/{VesselReader,PartsReader,BodyReader,AnimationLinks}.cs`, `Actuators/{Engine,Throttle,Staging,Rcs,Translate,Rotate,FlightComputer,Light,Animation,Decoupler,Docking,Scale,Debug}Actuator.cs` and `CameraActuator.Focus` only, `Render/VesselForceRender.cs` (for `vessel.always_render`), `TelemetrySampler.cs` → `VesselCache` (sample on demand, cached per step).
- **`SlotHost`:** Harmony id `ksa-scripting.slots`; postfix `Universe.ApplyVehicleSolvers()` → `StepSlot` (builds `StepInfo` from `Universe.GetElapsedSeconds()`, `Universe.GetLastSimStep().DeltaTime`, `Program.FrameNumber`; `StepLanded = dt > 0 && step index advanced`), then runs `EventDiffer` over the per-step snapshot and publishes; `Priority.First` prefix `Universe.ExecuteNextVehicleSolvers(double, SimStep)` → drains the Solver queue (`GameThreadQueue.Drain(Solver, executor, 64)`). A `[StarMapAfterOnFrame]` latch re-runs `StepSlot` with `StepLanded=false` on frames where the postfix did not fire (paused/loading).
- **`EventPatches`:** Harmony id `ksa-scripting.events`; one static class per row of `docs/research/ksa-host/event-hook-points.md` (engine.activate via `EngineController.SetIsActive` postfix, `rcs.activate`, `stage`, `decouple`, `split`, `dock`, `undock`, `vessel.spawn/remove` via `CelestialSystem.Register/Deregister`, `vessel.launch`, `impact`, `vessel.destroy`, `part.failure`, `control.change` via `Program.set_ControlledVehicle` (diffed), `editor.enter/exit`, `save.write` (postfix `UncompressedSave.Write` → persist state sidecar), `save.load` (postfix `Program.OnGameLoaded` → restore), `system.load`, `warp.change` (diffed), `burn.complete/fuel_out` via `FlightComputer.RaisePendingAlerts` prefix). Each publishes `ScriptEvent` via `EventBus.PublishSync`. Install with `CreateClassProcessor(type).Patch()` per class in try/catch so a missing target darkens one event, not all.
- **`KsaBridge : IGameBridge`:** `Read/Snapshot/List` over `VesselCache` (`vessel:<id>`), `BodyReader` (`body:<id>`), time/system; `Execute` → `ActionExecutor`; `QueueSolver` → `GameThreadQueue.Post(cmd, observer)`; `EventCatalog` static list.
- **`Watchdog`:** background thread; `Arm(IScriptRuntime rt, TimeSpan budget)` / `Disarm()`; on expiry `rt.Interrupt()` and sets a flag the scheduler reads.
- **Snapshot path grammar:** `SnapshotPath.Resolve(object record, string path)`: dotted segments; camelCase or snake_case accepted (`position.cci`, `engines.0.active`, `battery.fraction`); lists by index; unknown → `ScriptValue.Null`. Reflection cached per (type, segment).

---

## 5. Script-facing API v1 (both languages; TypeScript names shown, Lua uses the same names in snake_case where noted)

Handles: `vessel:<id>`, `body:<id>`, `term:<n>`. Preludes build objects over the coarse facade; **no per-member interop binding**.

| Member | Facade call | Notes |
|---|---|---|
| `ksa.time.{ut,warp,simDt,paused,warpSpeeds,autoWarp}` | `Read("time", …)` | |
| `ksa.time.setWarp(f)` | `Act("debug.warp", null, -1, f)` | Frame |
| `ksa.sleep(s, clock?)`, `nextStep()`, `nextFrame()`, `until(fn, {timeoutSeconds})`, `waitEvent(name, {vessel})` | `Wait(kind, arg)` | TS: Promise; Lua: yields |
| `ksa.vessels()` / `ksa.vessel(id)` / `ksa.active` | `List("vessels")`, `Read("vessel:<id>","id")`, `Read("time","activeVesselId")` | returns `VesselRef` objects |
| `VesselRef` getters (`id,name,situation,parent,controlled,controllable,isKitten,position,velocity,attitude,altitude,mass,orbit,navball,environment,power,battery,engines,tanks,rcs,solar,generators,lights,docking,decouplers,animations,srb,encounters,parts,throttleCmd,attitudeMode,attitudeFrame,rcsMode,rcsOn,engineOn`) | `Read("vessel:<id>", "<path>")` | names map 1:1 onto `VesselSnapshot` members (camelCase); `snapshot()` → `Snapshot(handle)` |
| `VesselRef.ignite()/shutdown()/engine(on)/stage()/throttle(f)/lights(on)/rcs(on)/translate(x,y,z)/rotate(x,y,z)/scale(f)/alwaysRender(on)/focus()` | `Act("vessel.ignite" …)`, `vessel.shutdown`, `vessel.engine`, `vessel.stage`, `vessel.throttle`, `vessel.lights`, `vessel.rcs`, `vessel.translate` (values[3]), `vessel.rotate`, `vessel.scale`, `vessel.always_render`, `camera.focus` | Frame → returns `CommandResult` |
| `VesselRef.attitudeMode(m)/attitudeFrame(f)/attitudeTarget(q)/rcsMode(m)/burn(ut, dv)` | `vessel.attitude_mode` (token), `vessel.attitude_frame` (token), `vessel.attitude_target` (values[4]), `vessel.rcs_mode` (token), `vessel.burn` (values[4]) | Solver → Promise / yield |
| `VesselRef.debug.teleport(pos, vel)/impulse(v, frame?, unit?)/refillFuel()/refillBattery()/control()` | `debug.teleport` (values[6]), `debug.impulse` (values[3], token=frame, aux=unit), `debug.refill_fuel`, `debug.refill_battery` (Solver); `debug.control_vessel` (Frame, token=id) | |
| `EngineRef.setActive(on)/setMinThrottle(f)` | `engine.active` (ordinal), `engine.min_throttle` | |
| `RcsRef.setActive(on)`; `LightRef.on(b)/brightness(v)/color(r,g,b)/outerAngle(d)/innerAngle(d)/goal(f)`; `AnimationRef.goal(f)`; `DockingRef.undock()`; `DecouplerRef.fire()` | `rcs.active`; `light.on/brightness/color(values[3])/outer_angle/inner_angle`, `animation.goal`; `animation.goal`; `docking.undock`; `decoupler.fire` | ordinal = index in the list |
| `ksa.bodies()/body(id)/system` | `List("bodies")`, `Snapshot("body:<id>")`, `Snapshot("system")` | |
| `ksa.on(event, fn, {sync, vessel})`, `ksa.every("frame"|"step"|seconds, fn, {clock})`, `ksa.service(fn)`, `ksa.command(name, fn, desc)`; Lua also `ksa.export(name, fn)` | `Subscribe(kind, key, fn, opts)` | returns `{ unsubscribe() }` |
| `ksa.console.log/warn/error(...)`, `ksa.console.exec(cmd)` | `Log`, `ConsoleExec` | |
| `ksa.state.get(k)/set(k,v)` (TS also proxy object) | `StateGet/Set` | persisted per script in the save sidecar |
| `ctx.term.write(s|bytes)`, `await ctx.term.read()` / Lua `ctx.term.read()`, `ctx.term.on(evt, fn)`, `ctx.term.size`, `ctx.term.attached`, `ctx.term.clear()`, `ctx.term.flush()`, `ctx.term.title(s)`, `ctx.term.policy({stopWhenDetached})` | `TermWrite`, `Wait("termRead")`, `Subscribe("event","term.*")`, `TermInfo`, `TermSetTitle`, `TermPolicy` | |

Context objects (built by the prelude from a map the scheduler passes as the single argument): `{ ksa, script: {name, path}, log, event?: {ut, type, vesselId, payload}, vessel?, sync?, ut?, dt?, stepIndex?, frame?, signal?: {cancelled, onCancel}, term? }`. Exports: TS module namespace functions; Lua returned table or `ksa.export`. Entry: `main(ctx)` for ad-hoc/service/terminal scripts.

Event names (v1): `engine.activate`, `engine.deactivate`, `engine.ignite`, `engine.shutdown`, `rcs.activate`, `stage`, `decouple`, `split`, `dock`, `undock`, `soi.change`, `vessel.spawn`, `vessel.remove`, `vessel.launch`, `situation.change`, `impact`, `vessel.destroy`, `part.failure`, `control.change`, `editor.enter`, `editor.exit`, `save.write`, `save.load`, `system.load`, `warp.change`, `burn.complete`, `burn.fuel_out`, `flameout`, `battery.depleted`, `battery.charged`, `animation.complete`, `docked`, `undocked`, `decoupled`, `active-changed`, `warp-changed`, `vessel-appeared`, `vessel-removed`, `situation-change`, `soi-changed`, `engine-state`, `term.resize`, `term.attach`, `term.detach`, `term.cancel` (gatOS differ names kept as aliases).


**Naming corrections (2026-10-16, WP3; the schema in `Core.Api.ApiSchema` and the preludes are authoritative):** JavaScript cannot have a getter and a method with the same name, and the table above collides (`attitudeMode`, `lights`, `rcs`, `scale`, `alwaysRender`, and `LightSnapshot.on` vs `LightRef.on`). Rule: every value-setting method has a `set` prefix and verbs stay verbs — `VesselRef.setThrottle/setEngine/setLights/setRcs/setScale/setAlwaysRender/setAttitudeMode/setAttitudeFrame/setAttitudeTarget/setRcsMode`, `ignite/shutdown/stage/translate/rotate/focus/burn/snapshot`; `EngineRef.setActive/setMinThrottle`, `RcsRef.setActive`, `LightRef.setOn/setBrightness/setColor/setOuterAngle/setInnerAngle/setGoal`, `AnimationRef.setGoal`, `SolarRef.setGoal`, `DockingRef.undock`, `DecouplerRef.fire`, `Time.setWarp`. Module-list refs (`engines`, `lights`, …) carry the record fields captured when the list was read plus those methods. Lua: everything snake_case (`set_throttle`, `vac_thrust_n`, `vessel_id`); `until` is reserved, so `ksa.wait_until`, plus `ksa.wait_event`, `ksa.next_step`, `ksa.next_frame`; ref methods use colon syntax (`v:set_throttle(0.5)`, `v.debug:teleport(p, q)`), module functions dot syntax (`ksa.sleep(2)`). Event/tick/command handler types are `=> void` (async functions still allowed); `ServiceContext.term` is `Terminal | undefined`.
---

## 6. Runtime adapters

### 6.1 LuaJIT (`KsaScripting.Runtime.Lua`) — see `plans/01-luajit-runtime-plan.md`

- Native: `tools/build-luajit.sh` builds LuaJIT `v2.1` at `tools/luajit.pin` with `XCFLAGS="-DLUAJIT_DISABLE_FFI -DLUAJIT_ENABLE_CHECKHOOK"` plus `tools/host_shim.c` compiled into the library (amalgamation or an extra object linked into the shared lib), into `src/KsaScripting.Runtime.Lua/runtimes/<rid>/native/` (`libluajit-5.1.2.dylib` on osx-arm64; `libluajit-5.1.so.2` on linux-x64 via a `docker run` of `ubuntu:22.04`; `tools/build-luajit.ps1` for win-x64 with `/MT`). Natives are gitignored; CI builds them. The csproj copies `runtimes/**` to output.
- `LuaNative` (`[LibraryImport("lua51")]`, `[ModuleInitializer]` resolver loading `<asmDir>/runtimes/<rid>/native/<file>` by absolute path with os-arch fallback; never `Free`).
- Shim exports: `host_timeout_hook(lua_State*, lua_Debug*)`, `host_raise(lua_State*)`, `host_traceback(lua_State*)`.
- Rules: host functions are `[UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])] static int F(nint L)`, body in try/catch, **only non-raising API calls**, errors returned as `(false, message)`; raising happens in the Lua prelude (`error(msg, 2)`) or via `host_raise`.
- One `lua_State` per runtime; sandbox env per script (§3.6 of the LuaJIT research page); each hook invocation runs in a coroutine (`lua_newthread` + `lua_resume`); `IHostFacade.Wait` → the host function stores `(thread, wait)` and `return lua_yield(L, 0)`; `Advance` resumes threads whose waits completed. Watchdog: `lua_sethook(L, &host_timeout_hook, LUA_MASKCOUNT, 1)` from the watchdog thread.
- Prelude `ksa.lua` embedded resource; stub `ksa.lua` (LuaCATS) generated.

### 6.2 V8 (`KsaScripting.Runtime.V8`) — see `plans/02-v8-clearscript-typescript-runtime-plan.md`

- Packages: `Microsoft.ClearScript.V8 7.5.1.1`, natives `win-x64`, `linux-x64`, `osx-arm64`. Dist copies `ClearScriptV8.<rid>.<ext>` under `runtimes/<rid>/native/`; set `HostSettings.AuxiliarySearchPath` to the mod dir before the first engine.
- One `V8Runtime` (constraints `MaxOldSpaceSize=256, MaxNewSpaceSize=16`), one **compiler engine** with `typescript.js` (TS 6; `tools/fetch-ts.sh` downloads the npm tarball and copies `lib/typescript.js` + LICENSE into `src/KsaScripting.Runtime.V8/assets/`, gitignored) and the `sucrase` bundle (`spikes/assets/sucrase.bundle.js` copied), code cache persisted under the user data dir; one script engine per script with `EnableTaskPromiseConversion | UseSynchronizationContexts | EnableDynamicModuleImports | EnableStringifyEnhancements` (+ debugging flags when `DebugPort` set), `DocumentAccessFlags.None`, custom `TsDocumentLoader` restricted to the scripts dir, `ksa` prelude as a system document, `__host` = the facade wrapper via `AddHostObject("__host", HostItemFlags.DirectAccess, …)`.
- Game-thread `SynchronizationContext` whose `Post` enqueues to a queue drained in `Advance`; `Invoke` of async exports returns `Null` and lets the promise run.
- Watchdog → `engine.Interrupt()`; heap soft limit per engine; `ScriptInterruptedException` → `ScriptRuntimeException("interrupted")`.
- Source-map VLQ decoder for `.ts` line numbers in errors.

---

## 7. GameMod (`KsaScripting.GameMod`)

- `Mod.cs` `[StarMapMod]`: `[StarMapImmediateLoad] void OnImmediateLoad(KSA.Mod)`, `[StarMapAllModsLoaded] void OnAllModsLoaded()` (config, paths, log sink, stub generation, runtimes, bridge, slots, event patches, console commands, purrTTY shells + launcher subscription, catalog scan + autostart, UI), `[StarMapBeforeGui]`, `[StarMapAfterGui] void OnAfterGui(double dt)` (Script Manager window, editor session pump), `[StarMapAfterOnFrame]` (latch), `[StarMapUnload]` (reverse teardown).
- `ScriptManagerWindow` per `docs/research/architecture/script-manager-ui.md` (panes, toolbar, tabs Overview/Bindings/Terminal/Output/Code, global Events/Runtimes/Settings; templates; `FilterCombo`-style pickers copied from purrTTY `ImGuiWidgets`; hotkey guard prefix on `GameSettings.OnKeyAll` gated by a mod-scoped `_wantsTextInput` flag; menu via `[ModMenuEntry("Scripting")]` with the `DrawProgramMenusHook` postfix fallback; toggle hotkey default F11, configurable).
- `ScriptTerminalShell : BaseChannelOutputShell` registered as `ksa-script` (script name from `EnvironmentVariables["KSA_SCRIPT"]`), implementing `ITerminalSink` → `ScriptTerminal` per §3.5 and the facts in `docs/research/existing-mods/purrtty-virtual-pty-bridge.md`.
- `TerminalPlacer` (`terminals.toml`) using `TerminalLauncherRegistry.Current` (Plan 00b contract); graceful "purrTTY launcher unavailable" path.
- `EditorLauncher` + `Core.Editor.NvimRpcClient` (MessagePack 3.1.12 over `TcpClient`) per `docs/research/editor/neovim-tcp-launch.md`.
- `ScriptCommands` (`[TerminalAction]`: `script run|start|stop|reload|list|edit|stubs|events`), `ScriptReplShell : BaseLineBufferedShell` per runtime (`ksa-lua-repl`, `ksa-ts-repl`).
- `ModLogSink`, `ModPaths` (install dir = `Path.GetDirectoryName(typeof(Mod).Assembly.Location)`, data dir = `<Documents>/My Games/Kitten Space Agency/mods/ksa-scripting`).

---

## 8. purrTTY side (Plan 00b) and vendoring

Implement `plans/00b-purrtty-scripting-extensions-plan.md` on `feature/scripting` in `/Users/asherwin/repos/meow-sci/purrtty` (contract POCOs/interfaces exactly as listed there; `TerminalLauncher` with the main-thread pump; `NoResize`, `Closed`, `Configuration` forwarding, `TargetPartInstanceId`). Then `dotnet build purrTTY.CustomShellContract -c Release` and copy `purrTTY.CustomShellContract.dll` + `purrTTY.Logging.dll` into `ksa-scripting/vendor/purrTTY/` with a README noting the purrtty commit.

---

## 9. Config (`ksa-scripting.toml`, Tomlyn)

| key | default | meaning |
|---|---|---|
| `frame_budget_ms` | 2.0 | total script time per Step slot |
| `sync_hook_budget_ms` | 5.0 | watchdog for sync hooks |
| `call_budget_ms` | 50.0 | watchdog for other invocations |
| `fault_limit` | 3 | consecutive faults before a script is disabled |
| `control_all_vessels` | true | authority gate |
| `debug_namespace` | true | allow `debug.*` actions |
| `autostart` | [] | script names started at load |
| `editor_enabled` | true | |
| `editor_port_min` / `editor_port_max` | 48600 / 48699 | loopback ports for nvim RPC |
| `editor_theme` | "" | purrTTY theme name for editor windows |
| `v8_enabled` / `lua_enabled` | true / true | |
| `v8_heap_limit_mb` | 64 | per script engine soft limit |
| `v8_debug_port` | 0 | 0 = off |
| `lua_jit_enabled` | true | `jit.off()` when false |
| `lua_memory_limit_kb` | 65536 | |
| `terminal_output_buffer_kib` | 256 | |
| `manager_hotkey` | "F11" | Script Manager toggle |

---

## 10. Assets and tools

1. `tools/fetch-nvim.sh|.ps1 [rid...]` → `editor/<rid>/nvim/` from GitHub releases v0.12.6 (`nvim-win64.zip`, `nvim-linux-x86_64.tar.gz`, `nvim-macos-arm64.tar.gz`); delete `bin/xxd.exe`, `bin/win32yank.exe`; keep `LICENSE.txt`; print sizes. Idempotent (skip if present).
2. `tools/fetch-lsp.sh|.ps1 [rid...]` → `editor/<rid>/lsp/lua-language-server/` (LuaLS 3.19.2 release assets) and `editor/<rid>/lsp/typescript/` (TypeScript 7.0.2 `typescript-<os>-<arch>.tgz` from the typescript-go GitHub release; keep `lib/tsc[.exe]` and `lib/*.d.ts`, LICENSE).
3. `tools/fetch-ts.sh|.ps1` → `src/KsaScripting.Runtime.V8/assets/typescript.js` + `LICENSE.txt` from `https://registry.npmjs.org/typescript/-/typescript-6.0.3.tgz` (pin in `tools/typescript.pin`); copy `spikes/assets/sucrase.bundle.js` alongside.
4. `tools/build-ts-parser.sh` → `editor/<rid>/nvim/lib/nvim/parser/typescript.{so,dylib,dll}` from `tree-sitter-typescript` v0.23.2 sources (`src/parser.c` + `src/scanner.c`, `cc -shared -fPIC -O2`); queries from `nvim-treesitter` (`queries/typescript/{highlights,injections,locals}.scm`) into `editor/config/ksa-editor/queries/typescript/`.
5. `editor/config/ksa-editor/init.lua`: the verified config (research page `editor/neovim-shipping.md` §Minimal init.lua) plus `vim.o.shell = vim.env.KSA_NOSHELL`, `shellcmdflag=''`, `mouse='a'`, `backupcopy='yes'`, LSP configs from `KSA_LSP_DIR`, completion, `BufWritePost → vim.rpcnotify(0,'ksa_saved', path, buf)`, `VimEnter` banner, `<C-z>` no-op.
6. `scripts-lib/tui.ts` and `tui.lua`: **escape-code toolkit only** (first pass): `Screen` (enterApp/leaveApp with alt screen, cursor hide, mouse press/release SGR, optional kitty flags; `frame(fn)` wrapping DEC 2026; `clear`, `moveTo`, `write`, `style(fg,bg,bold…)`, `box(x,y,w,h,title)`, `hline/vline`, `fill`), `parseInput(term)` async-iterator / Lua yielding iterator producing `{kind:'key', name, mods, text}` | `{kind:'mouse', button, action:'press'|'release'|'motion'|'wheel', col, row, mods}` | `{kind:'reply', bytes}` | `{kind:'text', text}` with an incremental CSI/SS3/APC/OSC state machine handling legacy and kitty encodings exactly per `docs/research/existing-mods/purrtty-virtual-pty-bridge.md`, plus `Rect.contains(col,row)` and `Button` (draw + `hit(ev)`), `Toggle`, `ProgressBar`, `Label`. No widget framework, no layout engine.
7. `tools/check-dist.py`: verifies the dist folder (required DLLs, `deps.json`, `mod.toml`, natives per RID, editor presence optional with warning).

---

## 11. Tests and commands

```bash
dotnet build ksa-scripting.slnx -nologo -v quiet                 # everything (game projects only when KSA_DLL_DIR / sibling checkout resolves)
dotnet test  ksa-scripting.slnx --nologo -v quiet                # Core + runtime tests (Lua tests need tools/build-luajit.sh run once)
tools/build-luajit.sh osx-arm64                                  # local native for tests
dotnet build src/KsaScripting.GameMod -c Release                 # deploys the dist folder
python3 tools/check-dist.py "$HOME/repos/meow-sci/mods/ksa-scripting"
cd /Users/asherwin/repos/meow-sci/purrtty && dotnet test purrtty.slnx --nologo -v quiet
```

Required test coverage per work package is listed in §12; tests must be quiet and sleep-free.

---

## 12. Work packages

| WP | Scope | Produces | Acceptance |
|---|---|---|---|
| **WP1 Scaffold + Core contracts** | solution, props, all csproj (empty where another WP fills), mod.toml, dist target, `Core.Values`, `Core.Runtime`, `Core.Hosting` interfaces, `Core.Logging`, `Core.Config`, ported gatOS DTOs/commands/sanitize/differ into `Core.Snapshots`/`Core.Commands`, `GameThreadQueue` (Core.Scheduling), `SnapshotPath` | builds game-free; tests for ScriptValue (json round trip, FromObject), SnapshotPath, SimCommand.PhaseFor, GameThreadQueue (timeout, batch, abandoned), EventDiffer (ported tests from gatOS if present) | `dotnet build` + `dotnet test` green on this Mac; committed |
| **WP2 Core hooks/scheduler/catalog/terminals/persistence** | `HookRegistry`, `BindingStore`, `EventBus`, `Scheduler`, `Watchdog` (Core), `ScriptCatalog`, `ScriptWatcher`, `ScriptTerminal`/`TerminalRegistry`, `ScriptStateStore`, `HostFacade`, `NullRuntime` (test runtime driven by C# lambdas) | tests: scheduler ordering/budgets/waits on all clocks; event delivery sync vs async; bindings persistence round trip; catalog load/reload-keeps-old-on-failure; terminal ring-buffer replay, smallest-grid, DEC 2026 wrapping, input queue/termRead; state store toml | green; committed |
| **WP3 ApiSchema + generators + preludes** | `Core.Api` schema for §5, generators, golden tests; `ksa.js` and `ksa.lua` preludes as embedded resources in the runtime projects (shared source under `src/KsaScripting.Core/Api/Preludes/` copied by MSBuild) | golden files match; preludes parse (checked by the runtime tests in WP5/WP6) | green; committed |
| **WP4 Ksa facade** | ports listed in §4, `ActionExecutor`, `VesselCache`, `KsaBridge`, `SlotHost`, `EventPatches`, `Watchdog` wiring, CompileCheck project with one stub per patched member | `dotnet build src/KsaScripting.Ksa` and `tests/KsaScripting.Ksa.CompileCheck` succeed against the 5554 DLLs; zero warnings | green; committed |
| **WP5 LuaJIT runtime** | §6.1 + `tools/build-luajit.*` + shim + tests (state lifecycle, sandbox escapes fail, host error protocol, coroutine wait/resume via a fake host, watchdog interrupt JIT on/off with CHECKHOOK build, memory polling, exports discovery, prelude smoke: `ksa.vessel("x").throttle(0.5)` reaches a fake facade with the right action) | osx-arm64 native built locally; tests green | committed |
| **WP6 V8 runtime** | §6.2 + `tools/fetch-ts.*` + tests (transpile fixtures incl. enums/imports, loader containment, async export → scheduler continuation via a fake host, interrupt, heap limit, exports discovery, source-map line mapping, prelude smoke identical to WP5) | tests green on osx-arm64 native | committed |
| **WP7 purrTTY extension** | Plan 00b on `feature/scripting`; vendored DLLs refreshed in ksa-scripting | `dotnet build purrtty.slnx` + `dotnet test purrtty.slnx` green; purrtty commits; vendor commit in ksa-scripting | |
| **WP8 GameMod** | §7 complete: Mod lifecycle, Script Manager window, console commands, REPL shells, ScriptTerminalShell, TerminalPlacer, EditorLauncher, NvimRpcClient (Core.Editor) + its tests (frame encode/decode, notification dispatch against an in-process fake server over TCP), templates | `dotnet build src/KsaScripting.GameMod -c Release` deploys a dist that `tools/check-dist.py` accepts | committed |
| **WP9 Assets, tools, tui libs, docs, CI** | §10 scripts (run them here for osx-arm64 and verify), `scripts-lib/tui.*` with tests where possible (TS parser tested in V8 tests; Lua parser tested in Lua tests), `.github/workflows/build.yml`, `docs/VALIDATION.md` in-game checklist, README, bundle `log.md`, THIRD-PARTY-NOTICES | scripts run; tests green | committed |

Dependency order: WP1 → {WP2, WP3, WP4, WP5, WP6, WP7, WP9-tools} in parallel → WP8 (needs WP2–WP7) → WP9 docs/CI last.
