# Vendored purrTTY contract DLLs (PLACEHOLDER)

These two assemblies are the pinned inter-mod ABI between ksa-scripting and purrTTY.
`KsaScripting.GameMod` references them with `Private=true` (so they land in the build output and in
`KsaScripting.GameMod.deps.json`); at runtime, with purrTTY installed, `mod.toml`
`ImportedAssemblies` makes StarMap resolve them from purrTTY's ALC instead.

**Status: placeholder.** These copies come from gatOS's vendored pin (purrtty commit `dd8e4e5`,
purrTTY 0.1.0) so the GameMod compiles before Plan 00b lands. WP7 (Manager B) replaces them with
the extended contract built from purrtty `feature/scripting` and updates this README with the
commit pin, then creates `LAUNCHER_CONTRACT_READY` next to this file.

| File | Purpose |
|---|---|
| `purrTTY.CustomShellContract.dll` | `ICustomShell`, `CustomShellRegistry`, `CustomShellMetadata`, `CustomShellStartOptions`, shell event args (namespace `purrTTY.Core.Terminal`). |
| `purrTTY.Logging.dll` | Referenced transitively by the contract assembly. |
