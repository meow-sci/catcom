---
type: Integration Analysis
title: Launching the shipped Neovim over TCP with no OS shell exposure
description: Verified launch recipe for the mod-owned Neovim - RPC over loopback TCP (portable across Windows and Linux), OS-assigned or host-chosen port, --clean plus a shipped init.lua, a stubbed 'shell' so :! and :terminal cannot drop the user into an OS shell, and the limits of that isolation.
tags: [editor, neovim, tcp, rpc, launch-flags, no-shell]
sources:
  - id: local
    resource: /references/nvim-shipping-report-2026-10-16.md
    title: Neovim shipping research report (2026-10-16)
  - id: starting
    resource: https://neovim.io/doc/user/starting.html
    title: Neovim :help starting (--listen, --clean, -u, --cmd)
  - id: builtin
    resource: https://neovim.io/doc/user/builtin.html#serverstart()
    title: Neovim :help serverstart() (TCP address rules)
  - id: options
    resource: https://neovim.io/doc/user/options.html#'shell'
    title: Neovim :help 'shell'
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T23:30:00Z }
status: stable
---

# Decision

Use **loopback TCP** for the nvim RPC channel on every platform. It removes the named-pipe (Windows) versus Unix-socket (Linux/macOS) split, the Unix socket path-length failure, and works identically from the .NET side (`TcpClient`). Verified on this workstation with Neovim 0.12.6:[^local][^starting][^builtin]

```
nvim --headless --clean -u init.lua --listen 127.0.0.1:48621 ...   → v:servername = 127.0.0.1:48621
nvim --headless --clean -u init.lua --listen 127.0.0.1:0     ...   → v:servername = 127.0.0.1:65436   (OS-assigned port)
```

Port selection: the host picks a free loopback port (bind to port 0, read it back, close, pass it as `--listen 127.0.0.1:<port>`), which avoids having to read `v:servername` back from inside nvim. If an OS-assigned port is preferred, the shipped `init.lua` can write `vim.v.servername` to a file given in `KSA_SERVER_FILE` on `VimEnter`.

# Launch recipe (the user never sees an OS shell)

```
program : <ModDir>/editor/<rid>/nvim/bin/nvim[.exe]          # spawned directly as the PTY child, no shell in between
args    : --clean -u <ModDir>/editor/config/init.lua
          --listen 127.0.0.1:<port>
          --cmd "set shell=<ModDir>/editor/noshell shellcmdflag="   # or via init.lua (below)
          -c "<optional initial command>"
          <scripts dir>/<file>.ts|.lua
cwd     : <scripts dir>
env     : NVIM_APPNAME=ksa-editor  XDG_CONFIG_HOME=<ModDir>/editor/config  XDG_DATA_HOME/STATE/CACHE=<UserData>/editor/...
          COLORTERM=truecolor  TERM=xterm-256color (Unix only)  KSA_LSP_DIR=<ModDir>/editor/lsp/<rid>  KSA_SCRIPTS_DIR=<scripts dir>
```

`--clean` skips user initialisation and shada but still sources the `-u` file; the XDG variables keep data/state/cache in mod-owned folders and `NVIM_APPNAME` keeps `runtimepath` free of the user's own config.[^starting]

# Blocking the OS shell

Setting `'shell'` to a non-existent stub makes `:!cmd` fail with "shell failed to start" and makes `:terminal` fail; verified headless:[^options]

```lua
vim.o.shell = vim.env.KSA_NOSHELL or '/nonexistent/noshell'
vim.o.shellcmdflag = ''
```

Limits: `vim.system()` / `jobstart()` from Lua still spawn processes (verified), so this prevents the *shell UI* from appearing, not arbitrary process execution; the editor is as trusted as the scripts it edits. Additionally map `<C-z>` to `<Nop>` (suspend) and unbind nothing else; the purrTTY toggle key (F12 by default) is consumed by purrTTY before nvim sees it.

# What the mod does with the channel

1. Connect `TcpClient("127.0.0.1", port)` with retries for ~2 s after spawn.
2. Send `nvim_set_client_info("ksa-scripting", …)`, then `nvim_get_api_info` to learn the channel id.
3. Receive `ksa_saved` notifications from the shipped `BufWritePost` autocmd (broadcast with channel 0) and reload the script on the game thread.
4. Push diagnostics (`vim.diagnostic.set` via `nvim_exec_lua`) when a load fails; jump to the line with `nvim_win_set_cursor`; show banners with `nvim_echo`.
5. On `ProcessExited` from the PTY layer, close the socket and release the port.

Frames are MessagePack-RPC arrays (`[0,id,method,params]`, `[1,id,error,result]`, `[2,method,params]`), encoded with MessagePack-CSharp; responses must be matched in reverse order of requests per Neovim's extra constraint.[^local]

[^local]: Neovim shipping research report (2026-10-16)
[^starting]: Neovim :help starting (--listen, --clean, -u, --cmd)
[^builtin]: Neovim :help serverstart() (TCP address rules)
[^options]: Neovim :help 'shell'
