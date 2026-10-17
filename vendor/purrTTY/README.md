# Vendored purrTTY contract DLLs

These assemblies are the pinned inter-mod ABI between catcom and purrTTY. They are built from
purrTTY's `purrTTY.CustomShellContract` project (MIT, see `third-party-licenses/purrTTY-LICENSE.txt`).

`Catcom.GameMod` and `Catcom.PurrTty` compile against them (`Private=false`); **they are never
shipped in the dist**. At runtime, with purrTTY installed, `mod.toml` `ImportedAssemblies` makes StarMap resolve
both assemblies from purrTTY's load context, so catcom and purrTTY share one `CustomShellRegistry.Instance`
and one `TerminalLauncherRegistry.Current`. A local copy would be loaded instead by StarMap builds that scan the
entry assembly's types before the dependency wiring exists, splitting type identity — which is why only
`Catcom.PurrTty.dll` (loaded lazily by path) references these types.

| File | Purpose |
|---|---|
| `purrTTY.CustomShellContract.dll` (+ `.xml` docs) | `ICustomShell`, `CustomShellRegistry`, `CustomShellStartOptions`, shell event args, and the launcher contract: `ITerminalLauncher`, `ITerminalHandle`, `TerminalLauncherRegistry`, `TerminalLaunchRequest`, `TerminalProgramSpec`, `TerminalWindowOptions`, `TerminalInWorldOptions`, `TerminalPlacementKind`, `TerminalLifecycleEvent` (namespace `purrTTY.Core.Terminal`). Assembly version 1.1.0.0. |
| `purrTTY.Logging.dll` | Referenced by the contract assembly. |

## Pin

| | |
|---|---|
| purrTTY repository | https://github.com/meow-sci/purrtty (local checkout `/Users/asherwin/repos/meow-sci/purrtty`) |
| Branch | `feature/scripting` |
| Commit | `16c6d9165ca93a0cac6ac87cf2390a1750c845eb` (feat(purrtty): cross-mod terminal launcher contract) |
| Build | `dotnet build purrTTY.CustomShellContract -c Release` (.NET SDK 10.0.100) |
| SHA-256 `purrTTY.CustomShellContract.dll` | `09e46e66c3642ac01dff31a55f35b6159f3c720f7b058c042357c6f7fafd9e87` |
| SHA-256 `purrTTY.Logging.dll` | `77b7f40242ba5be143f363fd34bc1d6efe6f8f108c6bf665cb281147d6eb8b49` |

## Refresh recipe

```bash
cd /path/to/purrtty
git checkout feature/scripting && git pull          # or the release branch once Plan 00b is merged
dotnet build purrTTY.CustomShellContract -c Release -nologo -v quiet
cp purrTTY.CustomShellContract/bin/Release/net10.0/purrTTY.CustomShellContract.{dll,xml} \
   purrTTY.CustomShellContract/bin/Release/net10.0/purrTTY.Logging.dll \
   /path/to/catcom/vendor/purrTTY/
shasum -a 256 /path/to/catcom/vendor/purrTTY/*.dll   # update the table above with the new commit + hashes
```

Only additive contract changes are allowed (existing consumers such as gatOS keep binding to the newer
assembly). `LAUNCHER_CONTRACT_READY` marks that these DLLs include the launcher contract.
