---
type: Platform Analysis
title: StarMap mod loader and the per-mod AssemblyLoadContext
description: How StarMap discovers, loads and invokes KSA mods, the exact managed-assembly resolution order of its ModAssemblyLoadContext, the lifecycle attributes, and what this means for a mod that must load native scripting runtimes.
resource: /Users/asherwin/repos/meow-sci/gatOS/thirdparty/starmap
tags: [starmap, assembly-load-context, mod-loader, native-libraries, harmony]
sources:
  - id: alc
    resource: /Users/asherwin/repos/meow-sci/gatOS/thirdparty/starmap/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
    title: ModAssemblyLoadContext.cs
  - id: runtimemod
    resource: /Users/asherwin/repos/meow-sci/gatOS/thirdparty/starmap/StarMap.Core/ModRepository/RuntimeMod.cs
    title: RuntimeMod.cs
  - id: modloader
    resource: /Users/asherwin/repos/meow-sci/gatOS/thirdparty/starmap/StarMap.Core/ModRepository/ModLoader.cs
    title: ModLoader.cs
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

- User mods: `ModLibrary.LocalModsFolderPath` = `<Documents>/My Games/Kitten Space Agency/mods/<modId>/` (`SpecialFolder.Personal`, so `~/My Games/...` on Linux/macOS).
- Bundled content mods: `<game>/Content/<modId>/` is checked first.

[^alc]: ModAssemblyLoadContext.cs
[^runtimemod]: RuntimeMod.cs
[^modloader]: ModLoader.cs
[^patcher]: ProgramPatcher.cs
[^attrs]: StarMap.API attribute sources
[^loaderprog]: StarMap.Loader Program.cs
