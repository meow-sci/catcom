---
type: Research Report
title: purrTTY internals research report (2026-10-16)
description: Verbatim research report produced by a delegated research pass on 2026-10-16; the primary source the distilled concepts in this bundle cite. Claims inside are tagged verified/inference/measured by the author of the report.
tags: [reference, report, raw]
generated: { by: claude-fable-5.1/ksa-scripting-research-subagent, at: 2026-10-16T20:00:00Z }
status: stable
---

> Mirrored from the session scratchpad into the bundle so the cited source outlives the session. Scratchpad paths mentioned inside (e.g. harness locations) no longer exist.

# purrTTY internals — reference for the catcom mod

Scope: how purrTTY (repo `/Users/asherwin/repos/meow-sci/purrtty`, all paths below relative to it unless
prefixed) loads native libs inside StarMap's ALC, how its PTY/session/shell layers are shaped, what a second
mod can and cannot reach today, and what the minimal purrTTY-side change would be to let catcom open
a terminal running `nvim <file>`. Cross-references: gatOS (`/Users/asherwin/repos/meow-sci/gatOS`) as the
existing consumer; StarMap sources at `gatOS/thirdparty/starmap`.

Architecture (from `AGENTS.md`):

```
FRONTEND   purrTTY.Display (ImGui)      GhosttyTerminalController + TerminalWindow + FrameGridRenderer
BACKEND    purrTTY.Terminal (headless)  ITerminalSurface + GhosttyTerminalSurface + Sessions/ + Pty/
BINDING    vendor/Ghostty.Vt            Terminal/RenderState/KeyEncoder/MouseEncoder + NativeLibraryResolver
NATIVE     vendor/Ghostty.Vt/native/<rid>/   libghostty-vt (osx-arm64, win-x64, linux-x64), checked in
CONTRACT   purrTTY.CustomShellContract  ICustomShell, CustomShellRegistry (namespace purrTTY.Core.Terminal)
GAME MOD   purrTTY.GameMod              StarMap hooks, Harmony patches, menus, layouts, in-world terminals
```

Assembly / namespace map (matters for `ImportedAssemblies` and `using`s):

| Assembly | Namespaces | Exported over StarMap? |
|---|---|---|
| `purrTTY.CustomShellContract` | `purrTTY.Core.Terminal` | **yes** |
| `purrTTY.Logging` | `purrTTY.Logging` (`ModLog`) | **yes** |
| `purrTTY.Terminal` | `purrTTY.Core.Terminal` (Pty/), `PurrTTY.Terminal`, `PurrTTY.Terminal.Sessions`, `PurrTTY.Terminal.Input`, `PurrTTY.Terminal.Ghostty`, `PurrTTY.Terminal.Rendering` | no |
| `purrTTY.Display` | `purrTTY.Display.Ghostty`, `.Layouts`, `.Theming`, `.Configuration`, `.Controllers` | no |
| `purrTTY.CustomShells` | `purrTTY.CustomShells` (`GameConsoleShell`) | no |
| `purrTTY.GameMod` | `purrTTY.GameMod[.Layouts|.InWorld|.Patches|.UI]` | no (entry assembly) |
| `Ghostty.Vt` | `Ghostty.Vt[.Enums|.Native|.Types]` | no |

---

## 1. Native library loading (vendor/Ghostty.Vt)

### 1.1 Runtime resolution

File: `vendor/Ghostty.Vt/src/Native/NativeLibraryResolver.cs` (purrtty addition, not upstream). Mechanism:
**`[ModuleInitializer]` + `NativeLibrary.SetDllImportResolver`**, probing the directory of the *binding
assembly* (`Assembly.Location`) with an explicit per-OS filename, then `runtimes/<rid>/native/`, then falling
back to the OS loader. P/Invokes use a plain logical name (`"ghostty-vt"`) via `[LibraryImport]`.

```csharp
internal static class NativeLibraryResolver
{
    private const string LibraryName = "ghostty-vt";
    private static int _registered;

    [ModuleInitializer]
    internal static void Register()
    {
        // Idempotent: SetDllImportResolver throws if called twice for one assembly.
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal)) return nint.Zero;
        var fileName = PlatformFileName();
        var assemblyDir = Path.GetDirectoryName(assembly.Location);
        foreach (var candidate in CandidatePaths(assemblyDir, fileName))
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        // Fall back to the OS loader's default search
        return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var fallback) ? fallback : nint.Zero;
    }

    private static IEnumerable<string> CandidatePaths(string? assemblyDir, string fileName)
    {
        if (string.IsNullOrEmpty(assemblyDir)) yield break;
        yield return Path.Combine(assemblyDir, fileName);                                   // 1. flat, beside the DLL
        var rid = RuntimeInformation.RuntimeIdentifier;
        yield return Path.Combine(assemblyDir, "runtimes", rid, "native", fileName);        // 2. NuGet layout
    }

    private static string PlatformFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "ghostty-vt.dll";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))     return "libghostty-vt.dylib";
        return "libghostty-vt.so";
    }
}
```

P/Invoke declaration style (`vendor/Ghostty.Vt/src/Native/NativeMethods.cs`):

```csharp
internal static unsafe partial class NativeMethods
{
    private const string LibraryName = "ghostty-vt";
    [LibraryImport(LibraryName)]
    internal static partial int ghostty_terminal_new(nint allocator, nint* terminal, GhosttyTerminalOptionsNative options);
    ...
}
```

Why the explicit resolver (doc comment in the file): "required inside hosts (e.g. KSA's plugin
`AssemblyLoadContext`) where the default probing logic does not reliably search the assembly directory or
`runtimes/<rid>/native`." Concretely: StarMap's `ModAssemblyLoadContext` (see §3.4) does **not** override
`LoadUnmanagedDll`, so `AssemblyDependencyResolver.ResolveUnmanagedDllToPath` (the thing that reads
`runtimes/<rid>/native` out of `deps.json`) is never consulted; the only reliable path is an explicit
`SetDllImportResolver` on the binding assembly that `TryLoad`s an absolute path.

A **second** resolver of the same shape lives in `purrTTY.Terminal/Pty/Process/UnixPtyNative.cs`
(`[ModuleInitializer] RegisterResolver`) mapping the logical import `"libc"` to `/usr/lib/libSystem.B.dylib`
(macOS) or `libc.so.6` (glibc — `libc.so` is a linker script and does not dlopen). It skips registration on
Windows and returns `IntPtr.Zero` for any other library name so `kernel32.dll` imports fall through to default
probing.

### 1.2 Build: csproj copy of the natives

`vendor/Ghostty.Vt/Ghostty.Vt.csproj` copies **all three** RID binaries flat into every build output
(filenames are platform-distinct so they coexist):

```xml
<PropertyGroup>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  <IsAotCompatible>true</IsAotCompatible>
  <!-- CA2255: ModuleInitializer in a library is intentional. IL3000: KSA is not single-file, Assembly.Location is valid. -->
  <NoWarn>$(NoWarn);CS1591;CA2255;IL3000</NoWarn>
</PropertyGroup>
<ItemGroup>
  <None Include="native/osx-arm64/libghostty-vt.dylib" Link="libghostty-vt.dylib"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>
  <None Include="native/win-x64/ghostty-vt.dll"        Link="ghostty-vt.dll">       <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>
  <None Include="native/linux-x64/libghostty-vt.so"    Link="libghostty-vt.so">     <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>
</ItemGroup>
```

Checked-in sizes: `.so` 1.67 MB, `.dylib` 1.44 MB, `.dll` 1.76 MB.

`purrTTY.GameMod/purrTTY.GameMod.csproj` → target `CopyCustomContent` (`AfterTargets="AfterBuild"`):

```xml
<RemoveDir Directories="$(DistDir)"/>                     <!-- wipe first: no stale DLLs in the mod ALC -->
<MakeDir Directories="$(DistDir)"/>
<Copy SourceFiles="$(TargetDir)mod.toml" DestinationFolder="$(DistDir)" .../>
<Copy SourceFiles="$(ProjectDepsFilePath)" DestinationFolder="$(DistDir)" .../>   <!-- purrTTY.GameMod.deps.json -->
<ItemGroup>
  <ModManagedDll Include="$(TargetDir)purrTTY.*.dll"/>
  <ModManagedDll Include="$(TargetDir)Ghostty.Vt.dll"/>
  <ModManagedDll Include="$(TargetDir)ModMenu.Attributes.dll"/>
  <ModManagedDll Include="$(TargetDir)Tomlyn.dll"/>
  <ModManagedDll Include="$(TargetDir)Microsoft.Extensions.Logging.Abstractions.dll"/>
  <ModManagedDll Include="$(TargetDir)StbImageSharp.dll"/>
</ItemGroup>
<Copy SourceFiles="@(ModManagedDll)" DestinationFolder="$(DistDir)"/>
<ItemGroup>
  <GhosttyNative Include="$(TargetDir)libghostty-vt.dylib" Condition="Exists('$(TargetDir)libghostty-vt.dylib')" />
  <GhosttyNative Include="$(TargetDir)libghostty-vt.so"    Condition="Exists('$(TargetDir)libghostty-vt.so')" />
  <GhosttyNative Include="$(TargetDir)ghostty-vt.dll"      Condition="Exists('$(TargetDir)ghostty-vt.dll')" />
</ItemGroup>
<Copy SourceFiles="@(GhosttyNative)" DestinationFolder="$(DistDir)" Condition="'@(GhosttyNative)' != ''" />
<!-- then LICENSE, THIRD-PARTY-NOTICES.md, third-party-licenses/*, TerminalFonts/**, TerminalThemes/** -->
```

`DistDir = $(SelectedDistModDir)purrTTY\`. `0Harmony.dll` and `StarMap.API.dll` are deliberately **not**
shipped (`PrivateAssets=all`; the loader supplies them); KSA reference DLLs are `Private=false`.

### 1.3 Dist folder layout (what lands in `<mods>/purrTTY/`)

```
purrTTY/
  mod.toml
  purrTTY.GameMod.deps.json
  purrTTY.GameMod.dll  purrTTY.Display.dll  purrTTY.Terminal.dll  purrTTY.CustomShells.dll
  purrTTY.CustomShellContract.dll  purrTTY.Logging.dll
  Ghostty.Vt.dll  Tomlyn.dll  ModMenu.Attributes.dll  Microsoft.Extensions.Logging.Abstractions.dll  StbImageSharp.dll
  libghostty-vt.dylib  ghostty-vt.dll  libghostty-vt.so        <- all three, flat
  LICENSE  THIRD-PARTY-NOTICES.md  third-party-licenses/
  TerminalFonts/  TerminalThemes/
```

The release zip (`.github/workflows/release.yml`, lines 100-162) is exactly this folder:
`cd "$PURRTTY_DIST_DIR" && zip -r "purrTTY-$VERSION.zip" purrTTY` — **one zip for all RIDs**, built on
ubuntu-latest. The version is stamped into `mod.toml` with `sed` first.

### 1.4 Documented gotchas (native)

From `vendor/Ghostty.Vt/README.md`, `docs/build-and-test.md`, `docs/gotchas.md`:

- **Shared library only.** Never vendor `ghostty-vt-static.lib` or the import lib `ghostty-vt.lib`; neither is loadable by P/Invoke.
- **Windows must be the `gnu` ABI** (`x86_64-windows-gnu`); msvc fails to compile the C++ deps.
- **Never `-mcpu native`** — produced an AV inside `vt_write` on other machines; SIMD deps runtime-dispatch anyway.
- **Linux pins glibc 2.31** (`x86_64-linux-gnu.2.31`); strip DWARF with `llvm-strip --strip-debug` (7.6 MB → 1.7 MB).
- **Flat filenames collide if two RIDs of one OS are ever added** (osx-x64, linux-arm64…); switch to `runtimes/<rid>/native`, which the resolver already probes. **This is directly relevant to LuaJIT/V8 if catcom ships both osx-arm64 and osx-x64 or win-x64 + win-arm64.**
- **No unload.** The ALC is never unloaded; StarMap can "reload" a mod without an ALC unload, so statics (including the `_registered` flag and the Harmony instance) survive — the resolver guard exists because `SetDllImportResolver` throws on a second call for the same assembly. There is no `NativeLibrary.Free` anywhere.
- **No Windows DLL-search-order or RPATH tricks** — absolute-path `TryLoad` sidesteps both. The native lib statically links its deps (highway, simdutf; zig's own libc++), so it has no sibling-DLL dependencies to resolve. If a catcom native (e.g. V8) has sibling DLL deps, load them explicitly in dependency order with `NativeLibrary.Load(absPath)` before the main one, or use `runtimes/<rid>/native` + the same resolver.
- `RawCellLayout.Validate()` runs once per process (`GhosttyTerminalSurface` ctor) to catch a native/managed layout drift after a pin bump — a model for a "native ABI self-check at first use".
- Local native patches are vendored as `vendor/Ghostty.Vt/native-patches/*.patch` (fork-free workflow).

**Pattern to copy for catcom:** put the LuaJIT/V8 bindings in their own assembly; add a
`[ModuleInitializer]` that registers a `DllImportResolver` on that assembly, mapping the logical name(s) to
`Path.Combine(Path.GetDirectoryName(assembly.Location), <platform file>)` and `runtimes/<rid>/native/...`;
copy natives flat (or under `runtimes/<rid>/native/`) with `<None Include=... Link=... CopyToOutputDirectory>`;
in the GameMod csproj copy them into `$(DistDir)` next to the entry DLL. `NoWarn CA2255;IL3000`.

---

## 2. PTY / process layer (`purrTTY.Terminal/Pty/`, namespace `purrTTY.Core.Terminal`)

### 2.1 Launch options

`purrTTY.Terminal/Pty/ProcessLaunchOptions.cs`:

```csharp
public enum ShellType { Auto, PowerShell, Wsl, PowerShellCore, Cmd, Custom, CustomGame }

public class ProcessLaunchOptions
{
    public ShellType ShellType { get; set; } = ShellType.Auto;
    public string? CustomShellPath { get; set; }      // ShellType.Custom: path OR bare exe name (PATH-resolved)
    public string? CustomShellId { get; set; }        // ShellType.CustomGame: CustomShellRegistry id
    public List<string> Arguments { get; set; } = new();
    public string? WorkingDirectory { get; set; }     // null -> Environment.CurrentDirectory
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();   // merged over parent env
    public int InitialWidth { get; set; } = 80;
    public int InitialHeight { get; set; } = 24;
    public string? StartupCommand { get; set; }       // typed into the shell as stdin + "\n" after start
    public ProcessLaunchOptions Clone();
    public static ProcessLaunchOptions CreateDefault();
    public static ProcessLaunchOptions CreatePowerShell();
    public static ProcessLaunchOptions CreatePowerShellCore();      // args: -NoLogo -NoProfile
    public static ProcessLaunchOptions CreateCmd();
    public static ProcessLaunchOptions CreateWsl(string? distribution = null, string? workingDirectory = null);
    public static ProcessLaunchOptions CreateCustom(string shellPath, params string[] arguments);
    public static ProcessLaunchOptions CreateCustomGame(string customShellId);
}
```

`CreateDefault()` sets: Windows → `ShellType.PowerShell`; Linux/macOS → `ShellType.Auto`;
`WorkingDirectory = UserProfile`; and these env vars (same list in `CustomShellStartOptions.CreateDefault()`):

```
TERM=xterm-256color   TERM_PROGRAM=ghostty   TERM_PROGRAM_VERSION=1.3.2
COLORTERM=truecolor   FORCE_COLOR=1          CLICOLOR_FORCE=1
```

Rationale in-file: advertise as Ghostty so chafa/viu/yazi pick kitty graphics, but keep `TERM=xterm-256color`
so **no terminfo is ever needed** (no `xterm-ghostty` terminfo shipped or installed; there is no terminfo
handling anywhere). `TERM_PROGRAM` is not forwarded over SSH. On Unix, `UnixPtySpawner.BuildEnvironment`
additionally sets `SHELL=<shellPath>` when the launched program's basename is in
`{sh,bash,zsh,fish,dash,ksh,csh,tcsh,ash,nu,elvish,pwsh}` and SHELL was not overridden.

`StartupCommand` is applied in `purrTTY.Terminal/Sessions/TerminalSession.cs::InitializeAsync` immediately after
`ProcessManager.StartAsync`: `ProcessManager.Write(cmd.EndsWith('\n') ? cmd : cmd + "\n")` — no readiness wait;
the PTY line discipline buffers it.

### 2.2 Process manager contract

`purrTTY.Terminal/Pty/IProcessManager.cs`:

```csharp
public interface IProcessManager : IDisposable
{
    bool IsRunning { get; }
    int? ProcessId { get; }
    int? ExitCode { get; }
    Task StartAsync(ProcessLaunchOptions options, CancellationToken cancellationToken = default);  // throws ProcessStartException
    Task StopAsync(CancellationToken cancellationToken = default);
    void Write(ReadOnlySpan<byte> data);   // never blocks: queued to a writer thread; failures -> ProcessError
    void Write(string text);
    void Resize(int width, int height);
    event EventHandler<DataReceivedEventArgs>? DataReceived;      // (ReadOnlyMemory<byte> Data, bool IsError)
    event EventHandler<ProcessExitedEventArgs>? ProcessExited;    // (int ExitCode, int ProcessId, DateTime ExitTime)
    event EventHandler<ProcessErrorEventArgs>? ProcessError;      // (Exception Error, string Message, int? ProcessId)
}
```

Selection (`purrTTY.Terminal/Sessions/TerminalSessionFactory.cs::CreateProcessManager`):
`ShellType.CustomGame` → `new CustomShellPtyBridge(CustomShellRegistry.Instance.CreateShell(id))`;
otherwise `OperatingSystem.IsWindows() ? new ProcessManager(logger) : new UnixProcessManager(logger)`.

Start semantics shared by both real backends: start failure = resolve/spawn/attach failure (throws
`ProcessStartException`); a child that spawns then dies reports **once** via `ProcessExited` with the real code.

### 2.3 Windows — ConPTY (`Pty/ProcessManager.cs` + `Pty/Process/ConPty*.cs`, `StartupInfoBuilder.cs`, `AttributeListBuilder.cs`, `ProcessLifecycleManager.cs`)

APIs (`ConPtyNative.cs`, all `kernel32.dll`): `CreatePseudoConsole(COORD size, hInput, hOutput, dwFlags, out hPC)`
called with **`dwFlags = 0`** (no `PSEUDOCONSOLE_INHERIT_CURSOR`), `ResizePseudoConsole`, `ClosePseudoConsole`,
`CreatePipe`, `InitializeProcThreadAttributeList`/`UpdateProcThreadAttribute` with
`PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016`, `CreateProcessW` with
`EXTENDED_STARTUPINFO_PRESENT (0x80000) | CREATE_UNICODE_ENVIRONMENT (0x400)`, `ReadFile`/`WriteFile`,
`GetExitCodeProcess`.

Key details:
- `StartupInfoBuilder`: `dwFlags = STARTF_USESTDHANDLES` with **null** std handles (node-pty / Windows Terminal pattern) — otherwise a parent with redirected stdio (test host, the game launched with pipes) hands the child *its* pipes and the terminal renders empty (gotcha 14).
- Command line: `ShellCommandResolver.ResolveShellCommandLine` quotes every token per `CommandLineToArgvW` rules (`AppendQuotedArgument`); never bare-space-join.
- Environment block: parent env merged case-insensitively with overrides, sorted case-insensitively, UTF-16 `NAME=VALUE\0...\0\0`.
- Exit detection: `System.Diagnostics.Process.GetProcessById(pid)` with `EnableRaisingEvents = true` → `Exited`; generation-gated so a late callback can't tear down a restarted session.
- Output pump: dedicated `LongRunning` thread, blocking `ReadFile` with a 64 KB buffer, **never sleeps** (a `Task.Delay(1)` previously capped throughput at single-digit MB/s and caused tearing).
- Teardown order (gotcha 14): `ClosePseudoConsole` **first** (conhost flushes and breaks the pipes — the only unblock path), wait ≤2 s for the pump to drain, join the writer, then close pipe handles; a handle whose thread didn't stop is **leaked, never closed** (Win32 handle recycling).
- Stop: `CloseMainWindow()` → `WaitForExit(2000)` → `Kill(true)`.
- Known: `ClosePseudoConsole` can wedge on a slow child (WSL2), so session dispose backgrounds process teardown.

### 2.4 Linux/macOS — POSIX pty, **fork-free** (`Pty/UnixProcessManager.cs`, `Pty/Process/UnixPtyNative.cs`, `UnixPtySpawner.cs`, `UnixPtyOutputPump.cs`)

Not `forkpty`/`openpty`. Sequence in `UnixPtySpawner.Spawn(shellPath, argv, workingDirectory, envOverrides, width, height)`:
1. `posix_openpt(O_RDWR|O_NOCTTY)` → `grantpt` → `unlockpt` → `ptsname_r`.
2. Parent opens the slave `O_RDWR|O_NOCTTY`, `ioctl(TIOCSWINSZ)` **on the slave fd** (set on the master before the slave opens, it does not stick), closes it after spawn.
3. `posix_spawnp` with file actions `addchdir_np(workingDirectory)` (glibc ≥ 2.29 / macOS ≥ 10.15, `EntryPointNotFoundException` tolerated), `addopen(0, slave, O_RDWR)`, `adddup2(0,1)`, `adddup2(0,2)`; attrs `POSIX_SPAWN_SETSID | SETSIGDEF | SETSIGMASK` (resets SIG_IGN on SIGPIPE that .NET sets process-wide). A fresh session leader opening a tty acquires it as controlling terminal on both Linux and macOS.
4. Why not fork: forking the multi-GB heavily-threaded game process risks allocator-lock deadlocks and ENOMEM (gotcha 16).

Notable ABI hack: `ioctl` is variadic and **Apple arm64 passes variadic args on the stack** — `WinSizeIoctl` uses an 8-filler-arg overload so the `winsize*` lands at `sp`. `TIOCSWINSZ` = `0x5414` Linux / `0x80087467` macOS.

- Resize: `ioctl(masterFd, TIOCSWINSZ)` under the state lock; kernel delivers SIGWINCH.
- Output pump: `poll(100 ms)` loop then `read` (so cancellation never closes an fd under a blocked read); EOF (macOS) or `EIO` (Linux) after the child dies ends the loop; `POLLNVAL` exits.
- Exit: dedicated waiter thread `waitpid`; `DecodeExitStatus`: `WEXITSTATUS`, or `128 + signal`.
- Stop: `kill(-pid, SIGHUP)` (child is session leader so pgid == pid), after 2 s `SIGKILL`.
- Cleanup closes the master fd only when both reader and writer threads have joined; otherwise **leaks** it.
- `_pid` reset to 0 so `SessionManager.RestartSessionAsync` can reuse the manager.

### 2.5 Shell resolution and defaults (`Pty/Process/ShellCommandResolver.cs`)

- `Auto` on Windows: WSL **only if ≥1 distro detected** → PowerShell → cmd. `Auto` on Unix: `$SHELL` if it exists → `zsh`/`bash`/`sh` from PATH → `ProcessStartException("No suitable shell found")`.
- `Custom`: `CustomShellPath` used as-is if it exists; a **bare name** (no directory component) is PATH-resolved (`.exe` appended on Windows). `Arguments` passed as discrete argv on Unix, quoted on Windows. **This is the path to run an arbitrary program (e.g. `nvim`) as the PTY child.**
- `CustomGame` → `ProcessStartException` here; must go through `CustomShellPtyBridge`.
- Config default (`ThemeConfiguration.DefaultShellType`) is `Auto`; `ThemeConfiguration.CreateLaunchOptions()` maps it; `CreateGameShellLaunchOptions()` stamps `PURRTTY_GAME_PROMPT` into env for the Game Console.
- Menus use `ShellAvailabilityChecker.IsShellAvailable(ShellType)` (cached per process; "available" == resolvable), `WslDistributionDetector`, `UnixShellDetector` (`/etc/shells` + `$SHELL`).

### 2.6 Input queue (`Pty/Process/PtyInputQueue.cs`)

Both real managers enqueue `Write` into a bounded queue (1 MiB) drained by a dedicated writer thread; overflow drops the chunk and reports once per episode via `ProcessError` (gotcha 20). `Write` only throws synchronously for "no process running".

---

## 3. Custom shell contract (`purrTTY.CustomShellContract`, namespace `purrTTY.Core.Terminal`)

### 3.1 Interfaces and types — verbatim

`ICustomShell.cs`:

```csharp
public interface ICustomShell : IDisposable
{
    CustomShellMetadata Metadata { get; }
    bool IsRunning { get; }
    event EventHandler<ShellOutputEventArgs>? OutputReceived;
    event EventHandler<ShellTerminatedEventArgs>? Terminated;
    Task StartAsync(CustomShellStartOptions options, CancellationToken cancellationToken = default); // InvalidOperationException if running; CustomShellStartException on failure
    Task StopAsync(CancellationToken cancellationToken = default);
    Task WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default); // InvalidOperationException if not running
    void NotifyTerminalResize(int width, int height);
    void RequestCancellation();
    void SendInitialOutput();   // called by CustomShellPtyBridge once per successful StartAsync, after events are wired
}
```

`CustomShellMetadata.cs`:

```csharp
public record CustomShellMetadata(string Name, string Description, Version Version, string Author, string[] SupportedFeatures)
{
    public static CustomShellMetadata Create(string name, string description);
    public static CustomShellMetadata Create(string name, string description, Version version, string author);
    public static CustomShellMetadata Create(string name, string description, Version version, string author, params string[] supportedFeatures);
}
```

`CustomShellStartOptions.cs`:

```csharp
public class CustomShellStartOptions
{
    public int InitialWidth { get; set; } = 80;
    public int InitialHeight { get; set; } = 24;
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    public Dictionary<string, object> Configuration { get; set; } = new();
    public static CustomShellStartOptions CreateDefault();              // UserProfile + the TERM/COLORTERM/... set from §2.1
    public static CustomShellStartOptions CreateWithDimensions(int width, int height);
    public static CustomShellStartOptions CreateWithWorkingDirectory(string workingDirectory);
}
```

Note: `CustomShellPtyBridge.StartAsync` builds the options from `ProcessLaunchOptions` as
`{ InitialWidth, InitialHeight, WorkingDirectory, EnvironmentVariables = new(options.EnvironmentVariables) }` —
`Configuration` is **not** populated and `StartupCommand` is not passed to the shell (it is written to
`WriteInputAsync` afterwards like any stdin).

`WellKnownShellEnvironment.cs`:

```csharp
public static class WellKnownShellEnvironment
{
    public const string GameShellPrompt = "PURRTTY_GAME_PROMPT";
}
```

`ShellEventArgs.cs`:

```csharp
public enum ShellOutputType { Stdout, Stderr }
public class ShellOutputEventArgs : EventArgs
{
    public ShellOutputEventArgs(ReadOnlyMemory<byte> data, ShellOutputType outputType = ShellOutputType.Stdout);
    public ShellOutputEventArgs(string text, ShellOutputType outputType = ShellOutputType.Stdout);   // UTF-8
    public ReadOnlyMemory<byte> Data { get; }
    public ShellOutputType OutputType { get; }
    public DateTime Timestamp { get; }
}
public class ShellTerminatedEventArgs : EventArgs
{
    public ShellTerminatedEventArgs(int exitCode, string? reason = null);
    public int ExitCode { get; }  public string? Reason { get; }  public DateTime Timestamp { get; }
}
```

`CustomShellExceptions.cs` / `CustomShellRegistry.cs`:

```csharp
public class CustomShellStartException : Exception       { ctor(string message, string? shellId = null); ctor(string message, Exception inner, string? shellId = null); public string? ShellId { get; } }
public class CustomShellRegistrationException : Exception   { ctor(string); ctor(string, Exception) }
public class CustomShellInstantiationException : Exception  { ctor(string); ctor(string, Exception) }
```

`CustomShellRegistry.cs`:

```csharp
public class CustomShellRegistry
{
    public static CustomShellRegistry Instance { get; } = new CustomShellRegistry();
    public CustomShellRegistry();                                   // public for tests
    public void RegisterShell<T>(string shellId, Func<T> factory) where T : class, ICustomShell;
    public IEnumerable<(string Id, CustomShellMetadata Metadata)> GetAvailableShells();
    public CustomShellMetadata? GetShellMetadata(string shellId);
    public ICustomShell CreateShell(string shellId);                // ArgumentException unknown; CustomShellInstantiationException
    public bool IsShellRegistered(string shellId);
    public void DiscoverShells();                                   // reflection over assemblies named "purrTTY.CustomShells" ONLY
}
```

`RegisterShell` **probe-instantiates** `factory()` inside a `using`, validates `Metadata` (non-null;
`Name`/`Description`/`Author` non-blank; `Version` non-null) and `!IsRunning`, then `TryAdd`s (duplicate id →
`ArgumentException`). Any other failure → `CustomShellRegistrationException`. So the ctor must be trivial and
`Dispose` safe on a never-started instance (gatOS's "T0.5" rule). **Auto-discovery only inspects the
`purrTTY.CustomShells` assembly** (`DiscoveryAssemblyNames`) — a shell in another mod is never discovered;
it must call `RegisterShell`. There is **no unregister**.

### 3.2 Base classes (`Base/`)

`BaseCustomShell : ICustomShell` — `protected readonly object _lock`; `IsRunning { get; protected set; }`;
`protected void RaiseOutputReceived(ReadOnlyMemory<byte> data, ShellOutputType outputType = Stdout)`;
`protected void RaiseTerminated(int exitCode, string? reason = null)`; abstract `StartAsync/StopAsync/WriteInputAsync`;
virtual no-op `NotifyTerminalResize`, `RequestCancellation`, `SendInitialOutput`; virtual `Dispose()`.

`BaseChannelOutputShell : BaseCustomShell` — unbounded `Channel<(byte[] Data, ShellOutputType Type)>` + a
`Task.Run` pump raising `OutputReceived` on a pool thread. Hooks:
`protected abstract Task OnStartingAsync(CustomShellStartOptions options, CancellationToken ct)` (before pump start),
`protected abstract Task OnStoppingAsync(CancellationToken ct)` (channel is completed even if it throws;
`StopAsync` drains the pump with a 2 s cap). Emit with `protected void QueueOutput(byte[] data)`,
`QueueOutput(string text)`, `QueueOutput(byte[] data, ShellOutputType type)`, `QueueOutput(string text, ShellOutputType type)`.

`BaseLineBufferedShell : BaseChannelOutputShell` — a cooked-mode line editor (history, cursor movement,
Ctrl+C/W/H/L, Backspace, arrows/Home/End/Delete via a small ESC/CSI state machine; **ASCII only — non-ASCII
bytes are ignored**). `WriteInputAsync` is **fully synchronous** (returns `Task.CompletedTask`). Abstracts:
`protected abstract void ExecuteCommandLine(string commandLine)`, `protected abstract void HandleClearScreen()`,
`protected abstract string GetPrompt()`. Helpers: `SendPrompt()`, `SendOutput(string|byte[])`,
`SendError(string|byte[])`, `CancelCurrentLine()`; inspection `CommandHistory`, `CurrentLine`, `CursorPosition`.

A REPL shell for catcom fits `BaseLineBufferedShell` (one line → `ExecuteCommandLine`) or
`BaseChannelOutputShell` if it needs raw-mode/multi-line input (then it must handle its own echo and editing —
the terminal does no local echo).

### 3.3 Registering from another mod — what gatOS does

`gatOS/gatOS.GameMod/mod.toml`:

```toml
[StarMap]
EntryAssembly = "gatOS.GameMod"

[[StarMap.ModDependencies]]
ModId = "purrTTY"
Optional = true
ImportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]
```

purrTTY's side (`purrTTY.GameMod/mod.toml`):

```toml
[StarMap]
EntryAssembly = "purrTTY.GameMod"
ExportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]
```

**Both must be listed**: StarMap computes the usable set as
`dependency.ExportedAssemblies ∩ dependencyInfo.ImportedAssemblies` when both are non-empty
(`StarMap.Core/ModRepository/RuntimeMod.cs::CalculateUseableAssemblies`). `purrTTY.Logging` rides along because
the contract assembly references it (`ModLog`) and both must come from the same ALC. The assembly to import
for shells is **`purrTTY.CustomShellContract`** — *not* `purrTTY.Terminal`/`purrTTY.Display`, which are not
exported and therefore cannot be imported today.

Compile-time reference (gatOS `gatOS.GameMod.csproj` and `gatOS.Ssh.csproj`), against vendored pinned DLLs:

```xml
<Reference Include="purrTTY.CustomShellContract">
  <HintPath>$(MSBuildThisFileDirectory)../vendor/purrTTY/purrTTY.CustomShellContract.dll</HintPath>
  <Private>true</Private>
</Reference>
<Reference Include="purrTTY.Logging">
  <HintPath>$(MSBuildThisFileDirectory)../vendor/purrTTY/purrTTY.Logging.dll</HintPath>
  <Private>true</Private>
</Reference>
```

`Private=true` so the copies land in the build output **and** in `gatOS.GameMod.deps.json` — the no-purrTTY
fallback loads the mod-local copies (and the registration lands in a registry nobody reads). Refresh recipe
is in `gatOS/vendor/purrTTY/README.md` (`dotnet build purrTTY.CustomShellContract -c Release` then copy the two
DLLs; pinned at purrtty commit `dd8e4e5`). gatOS also lifted `Microsoft.Extensions.Logging.Abstractions` to
10.0.0 to match purrTTY.Logging and the game.

Registration call (`gatOS/gatOS.GameMod/Mod.cs:989-1030`, run from `[StarMapAllModsLoaded]`):

```csharp
CustomShellRegistry.Instance.RegisterShell(ShellId, () => new SshShellSession(broker));
// then: compare typeof(CustomShellRegistry).Assembly.Location's directory with the mod's own folder —
// if equal, purrTTY is absent (own vendored copy loaded) -> warn "terminal UI unavailable".
```

`SshShellSession : ICustomShell` (`gatOS/gatOS.Ssh/SshShellSession.cs`) is the fully-async reference
implementation (trivial ctor; `StartAsync` boots the VM lazily; `OutputReceived` fires on connection threads;
handles a resize that arrives before start).

### 3.4 ALC resolution order and lifecycle timing

`StarMap.Core/ModRepository/ModAssemblyLoadContext.cs::Load(AssemblyName)` resolves, in order:
1. an assembly of that name already in `AssemblyLoadContext.Default`;
2. one already in the game core ALC; then `coreALC.LoadFromAssemblyName` (catching FileNotFound);
3. for each `[[ModDependencies]]` whose usable set contains the name: `dependency.ModAssemblyLoadContext.LoadFromAssemblyName`;
4. the mod's own `AssemblyDependencyResolver` (rooted at the entry assembly path, reads `<Entry>.deps.json`) → `LoadFromAssemblyPath`.

Consequences: (a) any name the game already loaded wins (so shared NuGets must match game versions);
(b) imported assemblies come from the dependency's ALC → one `Type` identity → one
`CustomShellRegistry.Instance`; (c) `LoadUnmanagedDll` is **not** overridden → see §1.

Load order (`ModLoader.PrepareMods`): a mod whose dependencies are not yet loaded is parked in `WaitingMods`
and initialized right after the dependency loads (`CheckForDependentMods`), or at the end if the dependency is
`Optional` and never appears (`TryLoadWaitingMods`). So with `ModId = "purrTTY"` listed, catcom's
`[StarMapAllModsLoaded]` always runs after purrTTY's. purrTTY runs shell discovery inside its own
`OnFullyLoaded → InitializeTerminal → GhosttySessionManagerFactory.EnsureGameShellsDiscovered()`
(`purrTTY.GameMod/TerminalMod.cs:131-157, 264-323`). The New Tab / New Window menus enumerate
`CustomShellRegistry.Instance.GetAvailableShells()` **live on every draw** (`TerminalMenus.DrawRegisteredCustomShellItems`,
`purrTTY.GameMod/TerminalMenus.cs:899-913`), skipping the id `"GameConsoleShell"`, and launch via
`ProcessLaunchOptions.CreateCustomGame(id)` — so registering at any time is fine; no refresh hook needed.
Each menu launch creates a new shell instance via the factory; in-world terminals and layouts can also pick
registered shells (`FilterCombo` over `ShellMenuCache.Current + CustomShellRegistry`).

StarMap lifecycle attributes (`StarMap.API/*.cs`, all instance methods on the `[StarMapMod]` class):
`[StarMapBeforeMain] void M()`, `[StarMapImmediateLoad] void M(KSA.Mod)`, `[StarMapAllModsLoaded] void M()`,
`[StarMapUnload] void M()`, plus per-frame `[StarMapAfterGui] void M(double dt)` (used by purrTTY) and the
other OnFrame/OnGui attributes.

---

## 4. Programmatic terminal creation — what exists, what is reachable

### 4.1 Public API that exists (in `purrTTY.Display`, assembly NOT exported)

`purrTTY.Display/Ghostty/GhosttyTerminalController.cs` (`public sealed class GhosttyTerminalController : ITerminalController`):

```csharp
public static bool IsAnyTerminalActive { get; }
public IReadOnlyList<TerminalWindow> Windows { get; }
public TerminalWindow? FocusTarget { get; }
public TerminalWindow OpenWindow(ProcessLaunchOptions? launchOptions = null, string? sessionTitle = null);
public TerminalWindow CreateConfiguredWindow(WindowLayoutRecord spec, ThemeDefinition? theme);   // exact name + geometry
public void CloseWindow(TerminalWindow window);
public IReadOnlyList<WindowLayoutRecord> CaptureWindows();
public void OpenTab(ProcessLaunchOptions? launchOptions = null, string? sessionTitle = null);    // into FocusTarget, else new window
public bool ApplyTheme(ThemeDefinition theme);
public bool IsVisible { get; set; }
public void Update(float deltaTime);  public void Render();
```

`OpenWindow` builds a `SessionManager` (`GhosttySessionManagerFactory.CreateSessionManager(config)`), a
`TerminalWindow`, then `StartSession` = `Task.Run(() => window.Sessions.CreateSessionAsync(title, launchOptions))`
(failure → `window.NotifySessionStartFailed(msg)` shown in-window), then `window.RequestFocus()`.

`purrTTY.Display/Ghostty/TerminalWindow.cs` (`public sealed partial class TerminalWindow : INamedTerminal, IDisposable`):

```csharp
public TerminalWindow(int id, SessionManager sessions, TerminalWindowSettings settings,
                      float2? initialPosition = null, float2? initialSize = null, string? desiredName = null);
public int Id { get; }  public SessionManager Sessions { get; }  public TerminalWindowSettings Settings { get; }
public bool IsOpen { get; }  public bool HasFocus { get; }  public string Name { get; }  public string Title { get; }
public event Action<TerminalWindow, bool>? FocusChanged;   public event Action? InputSent;
public void RequestFocus();   public void RequestPlacement(float2 position, float2 size);   public void Close();
public bool TrySetGridSize(int cols, int rows) => false;   // 2D windows derive cols/rows from pixel size
public bool TryRename(string newName);   public void ApplyTheme(ThemeDefinition theme);
```

`purrTTY.Display/Ghostty/TerminalTargetRegistry.cs`:

```csharp
public enum TerminalKind { Window, InWorld }
public interface INamedTerminal { string Name {get;} TerminalKind Kind {get;} bool HasFocus {get;}
    void ApplyTheme(ThemeDefinition theme); bool TrySetGridSize(int cols, int rows); bool TryRename(string newName); }
public static class TerminalTargetRegistry {   // MAIN-THREAD ONLY, lock-free
    public static IReadOnlyList<INamedTerminal> All { get; }   public static INamedTerminal? Focused { get; }
    public static bool Register(INamedTerminal t);  public static void Unregister(INamedTerminal t);
    public static INamedTerminal? Resolve(string? name);  public static bool IsNameAvailable(string? name, INamedTerminal? excluding = null);
    public static string SuggestUniqueName(string baseName); }
```

Session layer (`purrTTY.Terminal`, namespace `PurrTTY.Terminal.Sessions`):

```csharp
public sealed class SessionManager : IDisposable {
    public SessionManager(int maxSessions = 20, ProcessLaunchOptions? defaultLaunchOptions = null, ILogger? logger = null);
    public event EventHandler<SessionCreatedEventArgs>? SessionCreated;  // post-publication, maybe pool thread — don't touch Surface
    public event EventHandler<SessionClosedEventArgs>? SessionClosed;
    public event EventHandler<ActiveSessionChangedEventArgs>? ActiveSessionChanged;
    public Action<TerminalSession>? SessionConfigurator { get; set; }      // pre-publication hook (theme push, surface events)
    public (int cols, int rows) LastKnownTerminalDimensions { get; }       // stamped into InitialWidth/Height on create (80x25 default)
    public Task<TerminalSession> CreateSessionAsync(string? title = null, ProcessLaunchOptions? launchOptions = null, CancellationToken ct = default);
    public void SwitchToSession(Guid sessionId);  public Task CloseSessionAsync(Guid sessionId, CancellationToken ct = default);
    public Task RestartSessionAsync(Guid sessionId, ProcessLaunchOptions? launchOptions = null, CancellationToken ct = default);
}
public sealed class TerminalSession : IDisposable {
    public Guid Id { get; }  public ITerminalSurface Surface { get; }  public IProcessManager ProcessManager { get; }
    public ProcessLaunchOptions? LaunchOptions { get; }  public SessionState State { get; }  public string Title { get; set; }
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    public event EventHandler<SessionTitleChangedEventArgs>? TitleChanged;
    public event EventHandler<SessionProcessExitedEventArgs>? ProcessExited;   // (int ExitCode, int ProcessId, DateTime Timestamp)
    public Task InitializeAsync(ProcessLaunchOptions launchOptions, CancellationToken ct = default);
    public void SendInput(ReadOnlySpan<byte> data);  public Task CloseAsync(CancellationToken ct = default);  // tick thread only
}
```

What happens when the child exits: `TerminalWindow.TabLabel` appends `" (exit N)"` to the tab title
(`TerminalWindow.cs:670-687`); the tab stays open with the last frame; there is **no auto-restart and no
auto-close** (`RestartSessionAsync` exists but no UI calls it). The user closes the tab.

### 4.2 What is NOT reachable from another mod today

- `purrTTY.Terminal` and `purrTTY.Display` are not in `ExportedAssemblies`, so `ProcessLaunchOptions`,
  `GhosttyTerminalController`, `TerminalWindow`, `TerminalTargetRegistry` cannot be imported.
- Even if they were, the live controller is private state: `TerminalMod._controller` (private field) and
  `TerminalMenus.MenuController` (**`internal static`**, `purrTTY.GameMod/TerminalMenus.cs:750`). There is no public
  static accessor, no event bus, no main-thread dispatcher.
- `TerminalTargetRegistry`, `TerminalWindow` construction and `TerminalMod` dialogs are main-thread-only; a
  cross-mod caller on another thread would need a queue pumped from `OnAfterUi`.
- Layouts (`<Documents>/My Games/Kitten Space Agency/.purrTTY/layouts/<name>.toml`; data model
  `purrTTY.Display/Layouts/LayoutDefinition.cs`, `ShellSpec.cs`; orchestrator
  `purrTTY.GameMod/Layouts/LayoutManager.cs` — `Apply(string)`, `TeardownSet(string)`, `CaptureCurrentAs(name, desc)`)
  are **user-initiated only** by design (gotcha 29: no auto-apply, no env gate). `ShellSpec` carries
  `ShellType, CustomShellPath, CustomShellId, Arguments, WorkingDirectory, StartupCommand` (TOML key
  `startup_command`). The `LayoutManager` instance is private to `TerminalMod` too.
- In-world terminals are also private (`InWorldTerminalManager` instance in `TerminalMod`).

**Conclusion: today the only cross-mod mechanisms are (1) register an `ICustomShell` and let the user pick it
from New Tab / New Window / In-World / Layouts, and (2) have the user save a layout whose entry is
`ShellType.Custom` + `CustomShellPath="nvim"` + args, or default shell + `startup_command = "nvim <file>"`.**

### 4.3 Two ways to run nvim once a launch path exists

1. **nvim as the PTY child**: `ProcessLaunchOptions.CreateCustom("nvim", "/path/to/file.lua")`
   (+ `WorkingDirectory`, env). Exits → `TerminalSession.ProcessExited` fires with nvim's exit code; tab shows
   `(exit N)`. Clean for "open editor, wait for exit, reload script". Bare `nvim` is PATH-resolved from the
   **game process's PATH** (on macOS a GUI-launched app has a minimal PATH — pass an absolute path or set
   `EnvironmentVariables["PATH"]`; note `ShellCommandResolver.FindExecutableInPath` reads the *parent*
   `PATH`, not the override).
2. **Shell + StartupCommand**: `CreateDefault()` with `StartupCommand = "nvim '/path'"`. Shell survives nvim.
   Exit of nvim is not observable from outside (only the shell's exit is).

### 4.4 Proposed minimal purrTTY-side extension (recommended)

Keep the ABI in the already-exported contract assembly so catcom needs no new imports and purrTTY's
Display/Terminal stay private:

```csharp
// purrTTY.CustomShellContract (exported) — new file TerminalLauncher.cs, namespace purrTTY.Core.Terminal
public sealed class TerminalLaunchRequest
{
    public string? TerminalName { get; set; }          // INamedTerminal name; null = auto "Terminal N"
    public string? Title { get; set; }                 // tab title
    public string? Program { get; set; }               // null = configured default shell
    public List<string> Arguments { get; set; } = new();
    public string? WorkingDirectory { get; set; }
    public Dictionary<string,string> Environment { get; set; } = new();
    public string? CustomShellId { get; set; }         // run a registered ICustomShell instead of a process
    public string? StartupCommand { get; set; }
    public bool NewWindow { get; set; } = true;        // else tab in the focused window
    public bool Focus { get; set; } = true;
}
public interface ITerminalHandle
{
    string TerminalName { get; }  Guid SessionId { get; }  bool IsRunning { get; }
    event Action<int>? Exited;          // raised off-thread (PTY waiter) — consumer marshals
    void Focus();  void Close();  void WriteInput(ReadOnlyMemory<byte> bytes);
}
public interface ITerminalLauncher
{
    Task<ITerminalHandle> LaunchAsync(TerminalLaunchRequest request);   // safe from any thread
}
public static class TerminalLauncherRegistry
{
    public static ITerminalLauncher? Current { get; set; }   // purrTTY sets at init, clears at unload
}
```

purrTTY side: `TerminalMod.InitializeTerminal` installs an implementation that queues the request onto a
`ConcurrentQueue<Action>` drained at the top of `OnAfterUi` (main thread), maps it to
`ProcessLaunchOptions.CreateCustom(Program, args)` / `CreateCustomGame(id)` / `CreateDefault()` +
`StartupCommand`, calls `controller.OpenWindow(opts, title)` or `OpenTab`, sets `controller.IsVisible = true`,
`window.RequestFocus()`, and wires `session.ProcessExited` to the handle. ~150 lines, no new exports,
versioned with the existing contract. (Alternative — adding `purrTTY.Terminal`/`purrTTY.Display` to
`ExportedAssemblies` plus a public static `TerminalMenus.MenuController` — exposes far more surface and still
needs the main-thread marshalling.)

---

## 5. VT capability (libghostty-vt + purrTTY frontend) for running neovim

Terminal emulation is entirely libghostty-vt (pinned ghostty main `c22df09da` + 3 local patches, built with
zig 0.15.2; `vendor/Ghostty.Vt/README.md`). purrTTY only decides what to *encode/advertise/render*.

| Feature | Status | Where |
|---|---|---|
| True color / 256 color / 16 | **Yes** (engine); `COLORTERM=truecolor`; fg/bg resolved per cell in `GhosttyTerminalSurface.FillCell` | gotcha 4 |
| Alternate screen (1049/47/1047) | **Yes** (engine); `TerminalMode.AltScreen = 1049` enumerated | `vendor/Ghostty.Vt/src/Enums/TerminalMode.cs` |
| Scroll regions (DECSTBM), insert/delete line, etc. | **Yes** (engine, conformance-tested; "trusted, not re-tested") | `docs/build-and-test.md` |
| Bracketed paste (2004) | **Yes**: `ITerminalSurface.IsBracketedPasteEnabled`, `EncodePaste` wraps via `Ghostty.Vt.Paste.Encode` | `purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs:718` |
| Mouse 1000/1002/1003 + SGR 1006 | **Yes**: engine mouse encoder, mode-aware; motion reported per cell change; wheel = buttons 4/5; **Shift bypasses tracking** (select/copy inside nvim/tmux) | `TerminalWindow.Input.cs:47-229`, gotchas 10-11 |
| Kitty keyboard protocol | **Yes** (engine key encoder; `KeyEvent.UnshiftedCodepoint` set so Ctrl/Alt+letter encode) | gotcha 3, `GhosttyTerminalSurface.EncodeKeyOnce` |
| Focus events (1004) | **No** — mode is enumerated but the frontend never sends `CSI I`/`CSI O`; the binding's `Focus` module was pruned | README "Pruned surface" |
| OSC 52 clipboard | **Set: yes** (`OscSidecar` → `ClipboardRequested` → `ImGui.SetClipboardText`); **query (read): not answered** | `OscSidecar.cs`, `TerminalWindow.cs:279-297` |
| OSC 8 hyperlinks | Parsed by the engine; **not surfaced/clickable** in the frame renderer | — |
| OSC 0/2 title, OSC 1 icon | Yes (`TitleChanged`, `IconNameChanged`) | `ITerminalSurface` |
| Synchronized output (DEC 2026) | **Yes**: frame held while set, 1 s safety timeout | gotcha 13, `GhosttyTerminalSurface.cs:843-860` |
| Kitty graphics | **Yes** (raw RGB/RGBA/gray, zlib, PNG/JPEG via StbImageSharp decode; engine storage limit raised to 256 MiB); virtual placeholders/animation not yet | gotchas 23, 33-36, `KITTY_PLAN.md` |
| Sixel | **No** — DA deliberately omits feature 4 | `GhosttyTerminalSurface.cs:176-186` |
| DECSCUSR cursor shapes | **Yes**: engine; app's DECSCUSR wins; `CSI 0 q` returns to the configured default; shapes Block/Bar/Underline/BlockHollow | gotcha 22 |
| DA1/DA2/DSR/CPR | DA1 = VT220 level 62, features `22` (ANSI color) and `52` (clipboard); DA2 = `\x1b[>1;10;0c`; DSR/CPR by engine; **XTVERSION not answered** (`OnXtversion` unset) | same |
| Underline styles/colors, strikethrough, italic, bold | Engine + renderer (bold/italic font variants via `FrameFonts`) | `FrameGridRenderer.cs` |
| Block elements U+2580-259F, Braille U+2800-28FF | Drawn as geometry (fonts lack braille) — ratatui plots render | code-navigation.md |
| Scrollback | `TerminalOptions.MaxScrollback` default 10 MiB (**bytes**, not lines) | `TerminalOptions.cs` |

Key encoding in the ImGui frontend (`purrTTY.Display/Ghostty/TerminalInputEncoder.cs`):
- Named keys always encoded via the engine (with Shift/Ctrl/Alt mods): Enter, KeypadEnter→Enter, Backspace, Tab,
  Escape, arrows, Home, End, Delete, Insert, PageUp/Down, **F1-F12** (not F13+).
- Letters, digits and `Space [ ] \` are encoded via the engine **only while Ctrl or Alt is held** (Ctrl+letter
  controls, Alt+letter = ESC-prefixed meta, Ctrl+Space=NUL, Ctrl+[=ESC, Ctrl+\=FS, Ctrl+]=GS). Other
  Ctrl+punctuation (`Ctrl+-`, `Ctrl+/`, `Ctrl+;`…) is **not** mapped.
- Plain text flows from the ImGui character queue as UTF-8 (surrogate pairs joined; control chars < 32 and 127 dropped).
- Super/Cmd modifier is **not read** (`ReadModifiers` → Shift/Ctrl/Alt only).
- **Ctrl+Shift+C / Ctrl+Shift+V are reserved** (copy selection / bracketed paste) and never reach the app.
- Esc timing: the engine emits a lone `0x1B` immediately; Alt+key emits `ESC <key>`; there is no ambiguity timer. nvim's `ttimeoutlen` handles this as with any xterm-class terminal.
- AltGr (Ctrl+Alt + queued char) delivered as text.
- Diagnostics: `PURRTTY_KEY_DIAG=1` env logs `[keydiag]` lines.

Game-vs-terminal key capture (`purrTTY.GameMod/Patcher.cs`):
- `Patch01` (prefix on `KSA.Program.OnKey`): while `GhosttyTerminalController.IsAnyTerminalActive || InWorldTerminalManager.IsInputFocused`,
  **every key press is swallowed** from the game; releases are forwarded only for keys whose press the game saw (`s_gameHeldKeys`).
- `Patch04_MouseBindingGate` (prefix on private `Program.DispatchKeyEvent`): mouse-bound game actions gated the same way.
- `Patch03_HotkeyGuard` (prefix on `GameSettings.OnKeyAll`): blocks game hotkeys while `ImGui.GetIO().WantTextInput` or in-world focused (console window exempt).
- **The purrTTY toggle hotkey (default F12, `ToggleHotkeyBinding`) is read via `ImGui.IsKeyPressed` in `TerminalMod.OnAfterUi` before the terminal encodes keys** — so pressing the toggle key inside nvim hides the terminal instead of reaching nvim (suppressed only while an ImGui text field has focus or an in-world terminal is focused). Rebind if a TUI needs F12.
- Lock mode (`TerminalWindowSettings.LockMode`): unfocused window becomes click-through (`NoMouseInputs`); refocus via hot-zone window.
- Focus follows ImGui window focus; clicking the game world unfocuses and returns keys to the game.

Documented full-screen-TUI issues and their status:
- `plans/CODE_REVIEW_2026-06-10.md`: hidden-terminal inbox growth under htop/builds (**fixed** — gotcha 18 backpressure + 4 Hz hidden drain), mouse cell drift on fractional cell widths (**fixed**, gotcha 10), no Shift-override selection in tmux/nvim (**fixed**), wheel in alt-screen non-tracking not translated to arrows (**still open**: wheel scrolls scrollback only; `less`/nvim without mouse mode get nothing).
- gotcha 14: pump sleep caused tearing (**fixed**).
- gotcha 3: Ctrl/Alt+letter dropped under kitty keyboard protocol (**fixed**).
- gotcha 34/36: kitty zlib corruption and storage limit (**fixed** in the rebuilt native).
- Fonts: bundled Nerd Fonts (`.iamttf`); no braille glyphs (drawn as dots); no documented CJK coverage.
- `plans/OS_ANALYSIS.md:215` notes a gatOS-only TIOCSWINSZ limitation on virtio console — not purrTTY's PTY.

---

## 6. Game console shell and the ConsoleWindow Harmony patch

`purrTTY.CustomShells/GameConsoleShell.cs` — `public class GameConsoleShell : BaseLineBufferedShell`:
- `Metadata = CustomShellMetadata.Create("Game Console", "...", new Version(1,0,0), "purrTTY", "colors","clear-screen","command-execution")`.
- `OnStartingAsync`: throws `InvalidOperationException` unless `Program.ConsoleWindow != null && Program.TerminalInterface != null`; reads prompt from `options.EnvironmentVariables[WellKnownShellEnvironment.GameShellPrompt]`.
- `ExecuteCommandLine(commandLine)`: sets `_activeInstance = this; _isExecutingCommand = true` under `_activeLock`, calls **`Program.TerminalInterface.Execute(commandLine)`** (synchronous), then `SendPrompt()`; output arrives via the patch below during that window only.
- `public static bool IsCapturing => _activeInstance is { _isExecutingCommand: true };`
- `public static void OnConsolePrint(string output, ImColor8 color)` — classifies by color (`ConsoleWindow.ErrorColor` / `CriticalColor` → stderr red) and `SendOutput`/`SendError` with `\r\n` appended.
- Built-in `clear` → `ESC[3J`; `RequestCancellation` → cancel line + `^C` + prompt.
- Known limits (doc comment): each `Print` call becomes a line; async output after `Execute` returns is lost; unrelated cross-thread prints during `Execute` are mis-attributed.

`purrTTY.GameMod/Patches/ConsoleWindowPrintPatch.cs` — the Harmony target, verbatim:

```csharp
[HarmonyPatch(typeof(ConsoleWindow))]
[HarmonyPatch(nameof(ConsoleWindow.Print))]
[HarmonyPatch(new[] { typeof(ReadOnlySpan<char>), typeof(ImColor8), typeof(int) })]
public static class ConsoleWindowPrintPatch
{
    [HarmonyPostfix]
    public static void Postfix(ReadOnlySpan<char> text, ImColor8 color)   // param names must match the original
    {
        if (!GameConsoleShell.IsCapturing) return;
        try { GameConsoleShell.OnConsolePrint(new string(text), color); } catch (Exception) { }
    }
}
```

Brutal-version note: current API funnels every string/char print and the `ILogger OnLog` path into the single
`Print(ReadOnlySpan<char>, ImColor8, int)` sink (older API: `Print(string, uint, int, ConsoleLineType)`);
byte-span/`ImString` overloads bypass it. `Patcher.patch()` applies this as an **optional** patch via
`harmony.CreateClassProcessor(type).Patch()` with per-class try/catch; `Patcher.unload()` → `UnpatchAll("purrTTY")`.
Other targets: `KSA.Program.OnKey` (prefix), `KSA.Program.DispatchKeyEvent` (private, prefix),
`GameSettings.OnKeyAll` (prefix), `KSA.Program.DrawProgramMenusHook` (postfix, fallback menu when ModMenu is absent),
`SuperMeshRenderSystem.RenderTranslucencyPass` (postfix, in-world quads).

**Important for a scripting REPL:** because `CustomShellPtyBridge.Write` → `BaseLineBufferedShell.WriteInputAsync`
→ `ExecuteCommandLine` is synchronous and `Write` is called on the render/tick thread, **a line-buffered custom
shell's command handler runs on the game's main thread** — exactly what `GameConsoleShell` relies on to call
`TerminalInterface.Execute`. A Lua/JS REPL built on `BaseLineBufferedShell` can touch game state in
`ExecuteCommandLine` without marshalling, but must not block there (it stalls the frame).

---

## 7. Threading model

| Stage | Thread | Where |
|---|---|---|
| PTY read | dedicated `LongRunning` thread per session (`ConPtyOutputPump` / `UnixPtyOutputPump`) | `Pty/Process/*OutputPump.cs` |
| PTY bytes → engine inbox | same pump thread → `TerminalSession.OnProcessDataReceived` → `ITerminalSurface.Write` (the **only** thread-safe surface member; bounded 24 MiB inbox; a full inbox waits ≤500 ms for the drain, then drops with CAN/ST heal) | gotcha 1, 18 |
| Engine (`VTWrite`, render-state update, frame populate) | **main/render thread only**, inside `BuildFrame()` ← `TerminalWindow.Render` ← `GhosttyTerminalController.Render` ← `TerminalMod.OnAfterUi` (`[StarMapAfterGui]`); hidden windows drained at ~4 Hz by `Update()` on the same thread | gotcha 1, 18, 21 |
| Engine replies (DA/DSR) | raised during `BuildFrame` on the tick thread → `ProcessManager.Write` (queued) | `TerminalSession.OnSurfacePtyReply` |
| ImGui draw + kitty texture upload | render thread | gotcha 23 |
| User input encode | tick thread → `session.SendInput` → `IProcessManager.Write` → **never blocks** (PtyInputQueue writer thread) | gotcha 20 |
| Custom shell input | `CustomShellPtyBridge.Write` calls `WriteInputAsync` **synchronously on the tick thread**; for `BaseLineBufferedShell` the whole command executes there | `CustomShellPtyBridge.cs:939-978` |
| Custom shell output | any thread (`BaseChannelOutputShell` pump = thread pool; gatOS SSH = connection threads) → `OutputReceived` → bridge `DataReceived` → `Surface.Write` | gotcha 18 ("tick thread never waits — a custom shell can emit on it") |
| `Terminated` / `ProcessExited` | shell's thread / PTY waiter thread / `Process.Exited` thread — **not** the game thread | — |
| Session create | `GhosttyTerminalController.StartSession` runs `CreateSessionAsync` on `Task.Run`; `SessionConfigurator` runs pre-publication (safe to touch the surface); `SessionCreated` post-publication (must not touch the surface) | gotcha 19 |
| Session close | `TerminalSession.CloseAsync` completes synchronously and **must be on the tick thread**; process teardown is backgrounded | gotcha 19 |
| Registry / windows / dialogs | main thread only, lock-free (`TerminalTargetRegistry`, `TerminalWindow` ctor, `LayoutManager`) | code-navigation.md |

There is no requirement that `ICustomShell` callbacks run on the game thread; purrTTY tolerates output and
termination from any thread. The one implicit requirement is the inverse: anything a shell does inside
`WriteInputAsync` happens on the game thread (when input comes from the UI) and must be fast.

---

## 8. Build / deploy

`Directory.Build.props` (shared): net10.0, C# 13, nullable, warnings-as-errors (CS1591 excluded).
`KSAFolder` resolution: `KSA_DLL_DIR` env → `../ksa-game-assemblies/current/dll/` next to the repo → per-OS
default (`C:\Program Files\Kitten Space Agency\` on Windows; `$(HOME)/repos/meow-sci/ksa-game-assemblies/current/dll/`
otherwise). `ValidateKSAAssemblies` target fails fast when `RequiresKSAAssemblies=true` and `KSA.dll` is missing.

Deploy destination `SelectedDistModDir`: `PURRTTY_DIST_DIR` env (CI) → else `$(KSAUserModDir)` =
`%USERPROFILE%\Documents\My Games\Kitten Space Agency\mods\` on Windows, or the dev default
`$(HOME)/repos/meow-sci/mods/` elsewhere. The game's real mod folder (KSA decomp `KSA/ModLibrary.cs:178`,
`KSA/Constants.cs:281`):

```
ModLibrary.LocalModsFolderPath = Path.Combine(Constants.DocumentsFolderPath, "mods")
Constants.DocumentsFolderPath   = Path.Combine(Environment.GetFolderPath(SpecialFolder.Personal), "My Games", "Kitten Space Agency")
```

→ Windows `C:\Users\<u>\Documents\My Games\Kitten Space Agency\mods\<modId>\`;
macOS/Linux `~/My Games/Kitten Space Agency/mods/<modId>/` (`SpecialFolder.Personal` = `$HOME`).
StarMap also checks `<game install>/Content/<modId>/mod.toml` first (`RuntimeMod.TryCreateMod`).
purrTTY's own config lives at `<DocumentsFolderPath>/.purrTTY/` (`purrtty.toml`, `themes/`, `layouts/`,
`startup-command-history.toml`).

`mod.toml` (full, `purrTTY.GameMod/mod.toml`):

```toml
name = "purrTTY"
description = "A terminal emulator for KSA with mostly VT100/xterm compatibility"
version = "0.1.0"
author = "purrTTY Project"

[StarMap]
EntryAssembly = "purrTTY.GameMod"
ExportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]
```

StarMap's schema (`StarMap.Core/Config/StarMapConfig.cs`): `[StarMap] EntryAssembly (required)`,
`ExportedAssemblies: List<string>`, `[[StarMap.ModDependencies]] { ModId (required), Optional = false, ImportedAssemblies: List<string> }`.
The entry DLL must be `<modDir>/<EntryAssembly>.dll`; the mod class is found by the `[StarMapMod]` attribute **name**.

Release pipeline (`.github/workflows/release.yml`): test matrix on ubuntu/windows/macos-14 (each runner loads
its own native), then a single ubuntu build job: `dotnet build purrTTY.GameMod -c Release` with
`PURRTTY_DIST_DIR=.tmp/dist` → `zip -r purrTTY-<version>.zip purrTTY`. Versions `1.<run>.0` from `main`,
`-beta` from `feature/**`. Install note (README): delete the old `purrTTY/` folder before unzipping.

Packaging contrasts worth copying for catcom:
- purrTTY ships an **allow-list** of DLLs; gatOS ships a **deny-list glob** (`$(TargetDir)*.dll` minus
  `0Harmony.dll;StarMap.API.dll`) so new NuGet deps ship automatically — prefer the gatOS form.
- Both copy `<Entry>.deps.json` (StarMap's ALC uses `AssemblyDependencyResolver`, which reads it).
- gatOS keeps large content-stable blobs out of the wipe (`SkipUnchangedFiles`) — relevant if V8 binaries are big.
- Both reference KSA DLLs with `<Private>false</Private>` and `Condition="Exists(...)"`.

---

## 9. Quick answers for catcom

- **Native libs**: copy `NativeLibraryResolver` verbatim into the LuaJIT/V8 binding assembly (change the logical
  name and filenames); flat copy natives beside the entry DLL; add `runtimes/<rid>/native` if two RIDs share an
  OS. No ALC changes possible/needed.
- **Register a shell**: import `purrTTY.CustomShellContract` + `purrTTY.Logging` (mod.toml both sides already
  support it; `Optional = true`); reference the vendored DLLs with `Private=true`; in `[StarMapAllModsLoaded]`
  call `CustomShellRegistry.Instance.RegisterShell("lua", () => new LuaReplShell(...))`; trivial ctor,
  Dispose-safe, non-blank Metadata. It appears in New Tab / New Window / In-World / Layouts automatically.
- **Launch nvim in a purrTTY terminal from code**: **not possible today** — Display/Terminal are not exported and
  the controller is private. Needs the §4.4 launcher (or exporting Display + a public static controller). Until
  then: user-driven layout with `ShellType.Custom`/`CustomShellPath = "nvim"` or `startup_command`.
- **nvim will run fine** in terms of VT (truecolor, alt screen, 2026, kitty kbd, SGR mouse, bracketed paste,
  DECSCUSR). Caveats: no focus events, no OSC 52 *read*, no hyperlinks click, F13+/Super unmapped,
  Ctrl+Shift+C/V reserved, the F12 toggle hotkey steals F12, wheel outside mouse-mode does not map to arrows.
