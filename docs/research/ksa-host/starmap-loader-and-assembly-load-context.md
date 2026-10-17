---
type: Platform Analysis
title: StarMap mod loader and the per-mod AssemblyLoadContext
description: How StarMap discovers, loads and invokes KSA mods, the exact managed-assembly resolution order of its ModAssemblyLoadContext, the lifecycle attributes, and what this means for a mod that must load native scripting runtimes.
resource: /Users/asherwin/repos/meow-sci/StarMap
tags: [starmap, assembly-load-context, mod-loader, native-libraries, harmony]
sources:
  - id: alc
    resource: /Users/asherwin/repos/meow-sci/StarMap/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
    title: ModAssemblyLoadContext.cs (real StarMap source, commit 4dc60f8)
  - id: runtimemod
    resource: /Users/asherwin/repos/meow-sci/StarMap/StarMap.Core/ModRepository/RuntimeMod.cs
    title: RuntimeMod.cs (real StarMap source, commit 4dc60f8)
  - id: modloader
    resource: /Users/asherwin/repos/meow-sci/StarMap/StarMap.Core/ModRepository/ModLoader.cs
    title: ModLoader.cs (real StarMap source, commit 4dc60f8)
  - id: docpath
    resource: /Users/asherwin/repos/meow-sci/StarMap/StarMap.Core/Patches/DocumentsPathPatches.cs
    title: DocumentsPathPatches.cs (real StarMap source, commit 4dc60f8)
  - id: gatoscopy
    resource: /Users/asherwin/repos/meow-sci/gatOS/thirdparty/starmap
    title: gatOS's vendored StarMap copy (pre-#86; first research pass)
  - id: patcher
    resource: /Users/asherwin/repos/meow-sci/gatOS/thirdparty/starmap/StarMap.Core/Patches/ProgramPatcher.cs
    title: ProgramPatcher.cs
  - id: attrs
    resource: /Users/asherwin/repos/meow-sci/gatOS/thirdparty/starmap/StarMap.API
    title: StarMap.API attribute sources
  - id: loaderprog
    resource: /Users/asherwin/repos/meow-sci/gatOS/thirdparty/starmap/StarMap.Loader/Program.cs
    title: StarMap.Loader Program.cs
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T20:45:00Z }
status: stable
updated: 2026-10-17 (re-verified against the real StarMap repository; cross-mod assembly timing and the instance-path override added)
---

# Summary

StarMap is a thin loader: it starts the game inside a custom `GameAssemblyLoadContext`, loads `0Harmony.dll` into the default context, then creates **one `ModAssemblyLoadContext` per mod** and invokes attribute-tagged instance methods on the mod class at fixed lifecycle points. There is no other StarMap API. Everything else a mod does, it does with Harmony and direct calls into `KSA.dll`.

# Discovery and load

- `ModLoader.PrepareMods()` calls `ModLibrary.PrepareManifest()` (a KSA type) and iterates `ModLibrary.Manifest.Mods`, skipping disabled entries.[^modloader]
- `RuntimeMod.TryCreateMod` looks for `Content/<modId>/mod.toml` under the game install first, then `ModLibrary.LocalModsFolderPath/<modId>/mod.toml` (the user mods folder). The entry DLL must be `<modDir>/<EntryAssembly>.dll` where `EntryAssembly` comes from `[StarMap]` in `mod.toml` (defaulting to the mod id).[^runtimemod]
- The mod class is the first type in the entry assembly whose attributes include one **named** `StarMapModAttribute` (matched by type name, not identity).[^runtimemod]
- Dependencies declared in `[[StarMap.ModDependencies]]` park a mod in a waiting set until the dependency has loaded; `Optional = true` dependencies that never appear let the mod load at the end.[^modloader]
- `InitializeMod` instantiates the class with `Activator.CreateInstance`, scans public methods for StarMap attributes, validates each signature, and registers them in `ModRegistry`.[^runtimemod]

# Managed assembly resolution order

`ModAssemblyLoadContext.Load(AssemblyName)` resolves in this order:[^alc]

1. An assembly of the same simple name already loaded in `AssemblyLoadContext.Default`.
2. An assembly already loaded in the game's core ALC, then `coreALC.LoadFromAssemblyName` (so anything the game ships, including `System.*`, `Tomlet`, `MemoryPack`, is shared).
3. For each dependency mod whose usable-assembly set contains the name, `dependency.ModAssemblyLoadContext.LoadFromAssemblyName` (this is how purrTTY's contract assembly is shared with one type identity).
4. The mod's own `AssemblyDependencyResolver`, rooted at the entry assembly path, which reads `<EntryAssembly>.deps.json` and loads from the mod folder.

Step 3's name set per dependency is the *usable set* computed by `RuntimeMod.CalculateUseableAssemblies`:[^runtimemod] the dependency's `[StarMap] ExportedAssemblies` ∩ the importer's `[[StarMap.ModDependencies]] ImportedAssemblies` when both are non-empty; only the importer's list when the dependency exports nothing; only the exporter's list when the importer imports nothing; and just the dependency's entry assembly when neither is set. When the dependency is created *after* the importer (manifest order), the late-wiring path (`CheckForDependentMods`) uses the importer's `ImportedAssemblies` alone. `ImportedAssemblies` entries are simple assembly names, matched case-sensitively against `AssemblyName.Name`.

## When the wiring exists (the type-identity trap)

`TryCreateMod` creates the load context and loads the entry assembly (`LoadFromAssemblyName`, which does not yet resolve any of its references); only afterwards does `AllDependenciesLoaded` populate `RuntimeMod.Dependencies` and thereby step 3. `InitializeMod` then calls `ModAssembly.GetTypes()`, which forces every base class and interface used by a type *defined* in the entry assembly to resolve.[^runtimemod] StarMap PR #86 (commit `4dc60f8`, 2026-09-15, "fix dependency loading") moved that `GetTypes()` from `TryCreateMod` to `InitializeMod`; the gatOS-vendored copy predates it, so on those builds the contract types are resolved **before** the dependency wiring exists and step 3 can never win.[^gatoscopy] Two consequences for an entry assembly that defines a type deriving from a dependency's contract type (purrTTY's `BaseChannelOutputShell`, `ICustomShell`, …):

- With a local copy of the contract DLL in the mod folder, the pre-#86 loader loads that copy (step 4), splitting type identity: purrTTY's `CustomShellRegistry.Instance` and `TerminalLauncherRegistry.Current` are then different types from the ones the mod sees, and every registration goes into a registry nobody reads. On a post-#86 loader the dependency copy wins while purrTTY is present, but the two copies still coexist in memory whenever purrTTY is absent.
- Without a local copy, the pre-#86 loader throws `ReflectionTypeLoadException` from `GetTypes()` and the mod fails to load altogether.

The robust layout is therefore: ship no copy of the dependency's DLLs, keep every type that mentions a contract type out of the entry assembly (and out of anything the entry assembly's `deps.json` lists), put them in a bridge assembly that is **not** referenced by the entry assembly, and load that bridge with `AssemblyLoadContext.GetLoadContext(entryAssembly).LoadFromAssemblyPath(...)` from a lifecycle hook (`[StarMapImmediateLoad]` or later), only when the dependency mod is actually loaded (`KSA.ModLibrary.Find(modId)` returns it). By then step 3 is wired on every StarMap build, and the bridge's references resolve through the same `Load` override, so the contract comes from the dependency's context. ksa-scripting does this with `KsaScripting.PurrTty.dll` (see the implementation guide §8).

## Documents folder override

`ModLoader.Init` applies `DocumentsPathPatches` first: with a `-InstancePath <dir>` command-line argument or a `STARMAP_INSTANCE_PATH` environment variable, a Harmony prefix on the getter of `KSA.Constants.DocumentsFolderPath` returns that directory instead.[^docpath] `ModLibrary.LocalModsFolderPath` derives from it, so a mod's data folder must be derived from `Constants.DocumentsFolderPath` too (never from `Environment.SpecialFolder.MyDocuments` directly) to land inside the same instance.

Consequences for a scripting mod:

- **Ship `<EntryAssembly>.deps.json`.** Without it step 4 finds nothing and every NuGet dependency fails to load.
- **Version collisions resolve toward the game.** If the game already loaded `Microsoft.Extensions.Logging.Abstractions`, the mod gets that copy regardless of what it compiled against.
- **`LoadUnmanagedDll` is not overridden.** `AssemblyDependencyResolver.ResolveUnmanagedDllToPath` (which understands `runtimes/<rid>/native` in `deps.json`) is never consulted for `DllImport`. Native libraries must be resolved by the mod itself. See [native library loading in mods](/existing-mods/purrtty-native-library-loading.md).
- **The ALC is never unloaded.** `ModLoader.Dispose` only invokes `[StarMapUnload]` methods and clears the registry; statics and native handles survive.[^modloader]

# Lifecycle attributes and frame hooks

All hook methods are **instance** methods on the mod class with exact signatures:[^attrs]

| Attribute | Signature | When |
|---|---|---|
| `[StarMapBeforeMain]` | `void M()` | before the game's `Main`; almost nothing is initialized |
| `[StarMapImmediateLoad]` | `void M(KSA.Mod definingMod)` | when the mod is loaded; renderer not live |
| `[StarMapAllModsLoaded]` | `void M()` | after all mods loaded; apply Harmony patches here |
| `[StarMapBeforeGui]` | `void M(double dt)` | Harmony **prefix** on `Program.OnDrawUiFrame` |
| `[StarMapAfterGui]` | `void M(double dt)` | Harmony **postfix** on `Program.OnDrawUiViewports` |
| `[StarMapAfterOnFrame]` | `void M(double currentPlayerTime, double dtPlayer)` | Harmony **postfix** on `Program.OnFrame` |
| `[StarMapUnload]` | `void M()` | on loader dispose |

`ProgramPatcher` applies these three Harmony patches once from `StarMapCore.Init()`; each hook iterates the registry and calls `MethodInfo.Invoke` per mod per frame.[^patcher] A scripting mod should therefore keep its own per-frame work cheap and drain its game-thread queue in one of these hooks.

# Harmony

StarMap loads `0Harmony.dll` into the default ALC before anything else, and `Lib.Harmony` is referenced by mods with `PrivateAssets=all` so a second copy is never shipped.[^loaderprog] gatOS and purrTTY both pin `Lib.Harmony 2.4.2` and `StarMap.API 0.3.6` (StarMap.API comes from a private GitHub NuGet feed configured in the developer's NuGet config). NuGet currently lists `Lib.Harmony 2.4.3` (2026-10-11) and a `3.0.0-rc.3`; stay on the loader-supplied 2.x line.

# Mod folder locations

- User mods: `ModLibrary.LocalModsFolderPath` = `<Documents>/My Games/Kitten Space Agency/mods/<modId>/` (`SpecialFolder.Personal`, so `~/My Games/...` on Linux/macOS), where `<Documents>` is `KSA.Constants.DocumentsFolderPath` and may be redirected by StarMap's instance-path override (above).
- Bundled content mods: `<game>/Content/<modId>/` is checked first.

[^alc]: ModAssemblyLoadContext.cs
[^runtimemod]: RuntimeMod.cs
[^modloader]: ModLoader.cs
[^patcher]: ProgramPatcher.cs
[^attrs]: StarMap.API attribute sources
[^loaderprog]: StarMap.Loader Program.cs
[^docpath]: DocumentsPathPatches.cs
[^gatoscopy]: gatOS's vendored StarMap copy (pre-#86)
