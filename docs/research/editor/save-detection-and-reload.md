---
type: Design Decision
title: Save detection and script reload (nvim RPC first, file watcher second)
description: How the mod learns that the user saved a script - Neovim msgpack-RPC over --listen with a BufWritePost autocmd as the primary signal, a debounced FileSystemWatcher as the fallback for external editors, and backupcopy=yes to keep inodes stable - with the verified gotchas and the .NET client shape.
tags: [editor, neovim, rpc, msgpack, filesystemwatcher, hot-reload]
sources:
  - id: report
    resource: /references/nvim-shipping-report-2026-10-16.md
    title: Neovim shipping research report (2026-10-16)
  - id: api
    resource: https://neovim.io/doc/user/api.html
    title: Neovim :help api (api-rpc, nvim_create_autocmd, nvim_get_api_info, nvim_set_client_info)
  - id: lua
    resource: https://neovim.io/doc/user/lua.html
    title: Neovim :help lua (vim.rpcnotify)
  - id: options
    resource: https://neovim.io/doc/user/options.html
    title: Neovim :help options (backupcopy, autoread)
  - id: msgpack
    resource: https://www.nuget.org/packages/MessagePack
    title: MessagePack-CSharp on NuGet
  - id: fsw
    resource: https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher
    title: FileSystemWatcher documentation
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:30:00Z }
status: stable
---

# Decision

1. **Primary: Neovim RPC over loopback TCP** (see [neovim TCP launch](/editor/neovim-tcp-launch.md) for the verified flags). Launch `nvim --listen 127.0.0.1:<port>`; the shipped `init.lua` registers `BufWritePost → vim.rpcnotify(0, 'ksa_saved', path, buf)`; the mod connects as an RPC client and reloads the script on that notification. Tested headless on this workstation.[^report]
2. **Fallback: `FileSystemWatcher`** on the scripts directory with a 100–200 ms debounce for users who edit with their own `$EDITOR`.
3. **Always `backupcopy=yes`** so saves overwrite in place; the default `auto` renames the file (inode changes, measured on APFS), which turns one save into Renamed/Created/Deleted churn for a watcher.[^options][^report]
4. Reload on `BufWritePost`, never on buffer-change events.

# RPC details (verified)

- `--listen {addr}`: a colon means TCP (chosen: portable, verified with `127.0.0.1:<port>` and `127.0.0.1:0`); otherwise a named pipe path on Windows or a Unix socket, where **long Unix socket paths fail** with "Failed to --listen: invalid argument" (sun_path limit ~104–108 bytes).[^report]
- `--embed` is the wrong flag here (it is for GUIs that render the UI and waits for `nvim_ui_attach`); purrTTY renders nvim's own TUI.
- Protocol: MessagePack-RPC with two Neovim constraints (responses in reverse order of requests; messages processed in order). Frames: `[0, msgid, method, params]` request, `[1, msgid, error, result]` response, `[2, method, params]` notification.[^api]
- `vim.rpcnotify(0, ...)` broadcasts to every channel; to target one client, read the channel id from `nvim_get_api_info()` and call `nvim_set_client_info` after connecting.[^lua][^api]
- `nvim_buf_attach` reports content changes, not writes; not the right tool.
- No maintained .NET Neovim client exists (2016-era repos); hand-roll ~150 lines on **MessagePack-CSharp 3.1.12** (2026-10-12, MIT) using `MessagePackReader`/`MessagePackWriter` over a `TcpClient` to the loopback port.[^msgpack]
- Bonus: the same channel lets the mod push compile diagnostics into the editor (`vim.diagnostic.set` via `nvim_exec_lua`), jump to the failing line (`nvim_win_set_cursor`), or show banners.

# Watcher details

`FileSystemWatcher` raises multiple events per logical save and can overflow its buffer; debounce by path and verify the file's content hash changed before reloading. `autoread` is on by default, so if the mod rewrites a script (e.g. formatting) nvim picks it up.[^fsw][^options]

# Reload semantics (host side)

- Transpile/compile the saved file first; on failure keep the previous version running and surface the error in the editor (diagnostic) and the console. On success swap atomically: unregister the old hooks for that script, dispose its scope, register the new one.
- Scripts with persistent state should expose an optional `onReload(previousState)` entry so hot reload can carry state across.
- A reload is a game-thread operation: the RPC listener thread posts to the game-thread queue; the swap happens in the Frame lane.

[^report]: Neovim shipping research report (2026-10-16)
[^api]: Neovim :help api (api-rpc, nvim_create_autocmd, nvim_get_api_info, nvim_set_client_info)
[^lua]: Neovim :help lua (vim.rpcnotify)
[^options]: Neovim :help options (backupcopy, autoread)
[^msgpack]: MessagePack-CSharp on NuGet
[^fsw]: FileSystemWatcher documentation
