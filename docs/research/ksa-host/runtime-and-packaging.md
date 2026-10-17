---
type: Platform Analysis
title: KSA runtime facts and mod packaging layout
description: The game's .NET runtime, how a mod folder is laid out and deployed, the deny-list DLL glob, what must never be shipped, and the dist conventions gatOS and purrTTY already follow that catcom should copy.
tags: [packaging, dotnet, net10, deploy, mod-folder]
sources:
  - id: runtimeconfig
    resource: /Users/asherwin/repos/meow-sci/ksa-linux/KSA.runtimeconfig.json
    title: KSA.runtimeconfig.json (Linux install 2026.10.10.5554)
  - id: gatos-csproj
    resource: /Users/asherwin/repos/meow-sci/gatOS/gatOS.GameMod/gatOS.GameMod.csproj
    title: gatOS.GameMod.csproj
  - id: purrtty-csproj
    resource: /Users/asherwin/repos/meow-sci/purrtty/purrTTY.GameMod/purrTTY.GameMod.csproj
    title: purrTTY.GameMod.csproj
  - id: props
    resource: /Users/asherwin/repos/meow-sci/gatOS/Directory.Build.props
    title: gatOS Directory.Build.props
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T20:45:00Z }
status: stable
---

# Runtime

- KSA build 2026.10.10.5554 targets **`net10.0`** and ships **self-contained** with `Microsoft.NETCore.App 10.0.10` (`includedFrameworks` in the runtime config).[^runtimeconfig] Mods therefore compile for `net10.0` / C# 13 and may use .NET 10 APIs (`LibraryImport`, `UnmanagedCallersOnly`, function pointers, `NativeLibrary`).
- The Linux install contains the native libs KSA itself depends on (`libcoreclr.so`, `libglfw`, `libimgui`, `libfmod`, `libVulkanEx`, `libshaderc_shared`, …) flat in the game folder. Windows is the same layout with `.dll` files. No Lua, V8, or Node library is present in the game, so a scripting mod must ship its own.
- Reference assemblies for compiling mods are copied out of the install into `ksa-game-assemblies/current/dll/` (`KSA.dll`, `Brutal.*.dll`, `Planet.*.dll`, `Bepu*.dll`, `CommunityToolkit.HighPerformance.dll`, `imgui.dll`), and the decompiled sources live in `ksa-game-assemblies/current/decomp/`.

# Build conventions shared by gatOS and purrTTY

`Directory.Build.props` resolves `KSAFolder` in order: `KSA_DLL_DIR` env, a `ksa-game-assemblies` checkout next to the repo, then per-OS defaults.[^props] Game DLLs are referenced with `<Private>false</Private>` and `Condition="Exists(...)"`, so they never land in the output and a game-free CI can still build the non-game projects.

`Lib.Harmony` and `StarMap.API` use `PrivateAssets=all` and are **never copied into the mod folder**; StarMap supplies them to every mod ALC and a second copy risks a type-identity split.[^gatos-csproj]

# Mod folder layout (what the loader needs)

```
<mods>/<modId>/
  mod.toml                        # name/version/author + [StarMap] EntryAssembly, ExportedAssemblies, [[StarMap.ModDependencies]]
  <EntryAssembly>.dll
  <EntryAssembly>.deps.json       # REQUIRED: the ALC's AssemblyDependencyResolver reads it
  <every other managed dependency>.dll
  <native libs, flat or under runtimes/<rid>/native/>
  LICENSE, THIRD-PARTY-NOTICES.md, third-party-licenses/
  <content: fonts, themes, default config, scripts…>
```

Deployment is a `CopyCustomContent` target that runs after build and writes into `$(SelectedDistModDir)<modId>/` (CI overrides via an env var and zips that folder).[^gatos-csproj][^purrtty-csproj] Two styles exist:

- **purrTTY: allow-list** of DLL names. Omissions fail at runtime in-game.
- **gatOS: deny-list glob** `$(TargetDir)*.dll` minus `0Harmony.dll;StarMap.API.dll`. New NuGet dependencies ship automatically. **Prefer this.**

gatOS also keeps large content-stable blobs (guest image, QEMU) outside the wipe step and copies them with `SkipUnchangedFiles`; the same applies to V8 or libnode binaries that are tens of megabytes.[^gatos-csproj]

# Persistence paths

Per the `ksa` skill convention, mod data goes under `<Documents>/My Games/Kitten Space Agency/mods/<mod-kebab-name>/`. On Windows the install dir and data dir coincide, so a deploy wipe must exclude the live config (gatOS excludes `gatos.toml`). For catcom this is where the user's script tree should live so that nvim edits and the loader see the same files.

[^runtimeconfig]: KSA.runtimeconfig.json (Linux install 2026.10.10.5554)
[^gatos-csproj]: gatOS.GameMod.csproj
[^purrtty-csproj]: purrTTY.GameMod.csproj
[^props]: gatOS Directory.Build.props
