---
type: Pattern
title: purrTTY native library loading pattern (copy this for LuaJIT/V8)
description: The proven ModuleInitializer + DllImportResolver pattern purrTTY uses to load libghostty-vt per-RID inside StarMap's AssemblyLoadContext, with its build/dist plumbing and documented gotchas.
resource: /Users/asherwin/repos/meow-sci/purrtty/vendor/Ghostty.Vt/src/Native/NativeLibraryResolver.cs
tags: [purrtty, native-libraries, dllimport-resolver, packaging]
sources:
  - id: resolver
    resource: /Users/asherwin/repos/meow-sci/purrtty/vendor/Ghostty.Vt/src/Native/NativeLibraryResolver.cs
    title: NativeLibraryResolver.cs
  - id: vtcsproj
    resource: /Users/asherwin/repos/meow-sci/purrtty/vendor/Ghostty.Vt/Ghostty.Vt.csproj
    title: Ghostty.Vt.csproj
  - id: gotchas
    resource: /Users/asherwin/repos/meow-sci/purrtty/docs/gotchas.md
    title: purrTTY gotchas
  - id: report
    resource: /references/purrtty-internals-report-2026-10-16.md
    title: purrTTY internals research report (2026-10-16)
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T20:45:00Z }
status: stable
---

# Why a resolver is required

StarMap's `ModAssemblyLoadContext` never overrides `LoadUnmanagedDll`, so the `runtimes/<rid>/native` entries in `deps.json` are never consulted for `DllImport`/`LibraryImport`. purrTTY's binding assembly therefore registers an explicit `DllImportResolver` from a `[ModuleInitializer]` and loads an absolute path with `NativeLibrary.TryLoad`.[^resolver][^report]

# The pattern

```csharp
internal static class NativeLibraryResolver
{
    private const string LibraryName = "ghostty-vt";
    private static int _registered;

    [ModuleInitializer]
    internal static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;   // SetDllImportResolver throws on a 2nd call
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal)) return nint.Zero;
        var fileName = PlatformFileName();
        var assemblyDir = Path.GetDirectoryName(assembly.Location);
        foreach (var candidate in CandidatePaths(assemblyDir, fileName))
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var fallback) ? fallback : nint.Zero;
    }

    private static IEnumerable<string> CandidatePaths(string? assemblyDir, string fileName)
    {
        if (string.IsNullOrEmpty(assemblyDir)) yield break;
        yield return Path.Combine(assemblyDir, fileName);                                  // flat beside the DLL
        yield return Path.Combine(assemblyDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", fileName);
    }
    // PlatformFileName(): "ghostty-vt.dll" / "libghostty-vt.dylib" / "libghostty-vt.so"
}
```

P/Invokes use `[LibraryImport("ghostty-vt")]` with the plain logical name.[^resolver] A second identical resolver in `purrTTY.Terminal/Pty/Process/UnixPtyNative.cs` maps the logical `libc` to `libc.so.6` / `libSystem.B.dylib` because `libc.so` is a linker script on glibc.[^report]

# Build and dist plumbing

- `Ghostty.Vt.csproj` copies all three RID binaries flat into every build output using `<None Include="native/<rid>/<file>" Link="<file>" CopyToOutputDirectory="PreserveNewest"/>`; filenames are OS-distinct so they coexist. `NoWarn CA2255;IL3000` silences the module-initializer and `Assembly.Location` analyzers.[^vtcsproj]
- `purrTTY.GameMod.csproj` `CopyCustomContent` copies whichever of `libghostty-vt.dylib|libghostty-vt.so|ghostty-vt.dll` exist beside the entry DLL in the dist folder. One zip serves all OSes.[^report]

# Gotchas to inherit

- Ship **shared libraries only**; static libs and import libs are not loadable.
- Flat names collide if two RIDs of one OS are ever shipped (osx-x64 + osx-arm64, win-x64 + win-arm64); use `runtimes/<rid>/native/` then, which the resolver already probes. **Relevant to V8 and LuaJIT, which may need both macOS RIDs.**[^gotchas]
- No unload path exists; the ALC lives forever and `NativeLibrary.Free` is never called. Make initializers idempotent.
- Natives with sibling dependencies (V8's ICU data, libnode) must be loaded in dependency order with explicit `NativeLibrary.Load(absPath)` calls, or placed where the OS loader finds them.
- A one-time native-ABI self-check at first use (`RawCellLayout.Validate()`) catches managed/native drift after a version bump; do the same for a scripting runtime (check `jit.version` / V8 version string on startup).

# Application to catcom

Put each runtime binding in its own assembly (`Catcom.Lua`, `Catcom.V8`), register a resolver per assembly for its logical names (`luajit` → `lua51.dll`/`libluajit-5.1.so.2`/`libluajit-5.1.dylib`; ClearScript has its own loader, see [V8 ClearScript](/runtimes/v8-clearscript.md)), copy natives with the same `<None Link>` approach, and copy them into the dist folder in `CopyCustomContent`.

[^resolver]: NativeLibraryResolver.cs
[^vtcsproj]: Ghostty.Vt.csproj
[^gotchas]: purrTTY gotchas
[^report]: purrTTY internals research report (2026-10-16)
