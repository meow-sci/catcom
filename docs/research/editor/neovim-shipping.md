---
type: Integration Analysis
title: Shipping Neovim inside the mod and launching it in purrTTY
description: Verified facts for bundling Neovim 0.12.6 per platform (artifacts, sizes, runtime deps, licenses), isolating its config via NVIM_APPNAME and XDG variables, running it under ConPTY and POSIX PTYs with libghostty-vt, the language servers to ship for Lua and TypeScript without Node, a minimal dependency-free init.lua, and total size estimates.
resource: https://github.com/neovim/neovim/releases
tags: [editor, neovim, nvim, conpty, lsp, lua-language-server, tsc, packaging]
sources:
  - id: report
    resource: /references/nvim-shipping-report-2026-10-16.md
    title: Neovim shipping research report (2026-10-16)
  - id: releases
    resource: https://api.github.com/repos/neovim/neovim/releases/latest
    title: Neovim latest release (GitHub API)
  - id: install
    resource: https://github.com/neovim/neovim/blob/master/INSTALL.md
    title: Neovim INSTALL.md
  - id: starting
    resource: https://neovim.io/doc/user/starting.html
    title: Neovim :help starting (NVIM_APPNAME, XDG, --listen, --clean)
  - id: tui
    resource: https://neovim.io/doc/user/tui.html
    title: Neovim :help tui
  - id: lsp
    resource: https://neovim.io/doc/user/lsp.html
    title: Neovim :help lsp
  - id: luals
    resource: https://github.com/LuaLS/lua-language-server/releases
    title: lua-language-server releases
  - id: tsgo
    resource: https://github.com/microsoft/typescript-go/releases
    title: TypeScript 7 native release tarballs
  - id: issue41853
    resource: https://github.com/neovim/neovim/issues/41853
    title: neovim #41853 exit stalls when DA1 is not answered
  - id: issue29516
    resource: https://github.com/neovim/neovim/issues/29516
    title: neovim #29516 vcruntime140.dll missing
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:30:00Z }
status: stable
---

# What to ship

**Neovim 0.12.6** (released 2026-10-09) from the official release archives: `nvim-win64.zip` 12.6 MB, `nvim-linux-x86_64.tar.gz` 11.4 MB, `nvim-macos-arm64.tar.gz` 9.6 MB; each unpacks to `bin/`, `lib/nvim/parser/` (c, lua, markdown, markdown_inline, query, vim, vimdoc) and `share/nvim/runtime/`, 37–40 MB.[^releases][^report]

Platform notes:

- **Windows:** no installer or admin needed; `$VIMRUNTIME` is auto-detected from the archive layout. `nvim.exe` imports **`VCRUNTIME140.dll`, which is not in the zip** (UCRT is in-box on Windows 10+): ship the VC++ redistributable or an app-local `vcruntime140.dll`.[^install][^issue29516] Consider omitting `xxd.exe` (Defender false positive) and `win32yank.exe`. 0.12 no longer searches the current directory for executables.
- **Linux:** the tarball needs **glibc ≥ 2.34** (built on ubuntu-22.04); "unsupported" glibc-2.17 builds exist in `neovim/neovim-releases`. Dynamic deps are only libc/libm/libgcc_s.
- **macOS:** deployment target 11.0; links only system libs.
- **License:** Apache-2.0 plus Vim-license parts; ship `LICENSE.txt` unmodified and keep runtime-file headers.[^report]

# Isolating the shipped config

Neovim honours `NVIM_APPNAME` and `XDG_CONFIG_HOME / XDG_DATA_HOME / XDG_STATE_HOME / XDG_CACHE_HOME` **on Windows too** (defaults are `~/AppData/Local/nvim*` only when unset). Verified locally: with `NVIM_APPNAME=ksa-editor` and the four XDG vars pointed at mod-owned folders, `runtimepath` contains no user directories. `--clean -u init.lua` still sources the given file.[^starting][^report]

Recommended launch:

```
env:  NVIM_APPNAME=ksa-editor
      XDG_CONFIG_HOME=<ModDir>/editor/config      # contains ksa-editor/init.lua
      XDG_DATA_HOME=<UserData>/editor/data
      XDG_STATE_HOME=<UserData>/editor/state      # shada, undo, swap, logs
      XDG_CACHE_HOME=<UserData>/editor/cache
      COLORTERM=truecolor
      TERM=xterm-256color                         # Linux/macOS; leave unset on Windows (nvim guesses "vtpcon")
      KSA_LSP_DIR=<ModDir>/editor/lsp/<platform>
args: nvim --listen <addr> <scriptfile>
cwd:  the user's scripts directory (LSP root detection, :e paths)
```

# Terminal requirements (purrTTY / libghostty-vt)

- ConPTY needs Windows 10 1809+; purrTTY already uses it. On Windows nvim reads `TERM` and guesses `vtpcon` only if unset. `COLORTERM=truecolor` enables `termguicolors` everywhere.[^tui]
- **The emulator must answer DA1 (`CSI c`)**: nvim sends `CSI ? u CSI c` at startup for key-encoding detection and waits for DA1 at exit; without an answer every `:q` stalls ~1 s on 0.12.6 (fixed upstream after 0.12.6).[^issue41853] libghostty-vt answers DA1 (VT220 level 62) per the purrTTY capability matrix.
- Known Windows issues: `modifyOtherKeys` not reset on exit (#37062), cursor shape not restored in Windows Terminal (#9081, workaround `vim.o.guicursor = ''`). purrTTY owns the VT instance, so it can reset key-encoding modes when the nvim process exits.
- `Ctrl+Z` suspends nvim; map it to `<Nop>`. The purrTTY toggle hotkey (F12 by default) is consumed before nvim sees it.
- Linux: `TERM=xterm-256color` is safe (nvim also has a builtin `xterm-ghostty` entry); child tools spawned by `:!` would need terminfo for exotic `TERM` values.[^report]

# Language tooling without Node

| Tool | Version | Size (compressed → unpacked) | Notes |
|---|---|---|---|
| lua-language-server (LuaLS) | 3.19.2 (2026-10-13, MIT) | 3.3–4.5 MB → 20–23 MB | stdio LSP; `--configpath`, `--logpath`, `--metapath` for isolation; smoke-tested[^luals] |
| TypeScript 7.0.2 native `tsc --lsp --stdio` | 2026-07-08 (Apache-2.0) | 9.3–9.8 MB → 26–27 MB | full LSP (completion, hover, rename, references), **no Node required**; smoke-tested; nvim-lspconfig ships a `tsc` config and retired `tsgo`[^tsgo] |
| typescript-language-server | 6.0.2 | needs Node ≥ 22.22 (+32–53 MB) | rejected: 3–5× larger |
| tree-sitter TypeScript parser | 0.23.2 source | ~1 MB | not bundled with nvim; compile `parser.c` + `scanner.c` to `parser/typescript.{so,dll,dylib}` (ABI 13–15) and ship highlight queries |

Neovim 0.12 built-ins cover the rest: `vim.lsp.config()` / `vim.lsp.enable()`, default LSP keymaps (`K`, `grn`, `gra`, `grr`, `gri`), `vim.lsp.completion.enable(true, client.id, bufnr, { autotrigger = true })`, `vim.treesitter.start()`.[^lsp]

# Minimal dependency-free init.lua (assembled from verified APIs)

```lua
vim.o.backupcopy = 'yes'          -- overwrite in place; watcher-friendly
vim.o.swapfile = false
vim.o.number = true
vim.o.termguicolors = true
vim.o.mouse = 'a'                 -- purrTTY wheel outside mouse mode is not translated to arrows
vim.o.completeopt = 'menuone,noselect,popup'
vim.keymap.set({'n','v'}, '<C-z>', '<Nop>')

vim.api.nvim_create_autocmd('FileType', { pattern = { 'lua', 'typescript' },
  callback = function(ev) pcall(vim.treesitter.start, ev.buf) end })

local lsp_dir = vim.env.KSA_LSP_DIR
vim.lsp.config('lua_ls', {
  cmd = { lsp_dir .. '/lua-language-server/bin/lua-language-server', '--configpath=' .. lsp_dir .. '/luarc.json',
          '--logpath=' .. vim.fn.stdpath('log') .. '/luals' },
  filetypes = { 'lua' }, root_markers = { '.luarc.json', '.git' },
  settings = { Lua = { runtime = { version = 'LuaJIT' } } },
})
vim.lsp.config('tsc', { cmd = { lsp_dir .. '/typescript/lib/tsc', '--lsp', '--stdio' },
  filetypes = { 'typescript', 'javascript' }, root_markers = { 'tsconfig.json', '.git' } })
vim.lsp.enable({ 'lua_ls', 'tsc' })

vim.api.nvim_create_autocmd('LspAttach', { callback = function(ev)
  local client = assert(vim.lsp.get_client_by_id(ev.data.client_id))
  if client:supports_method('textDocument/completion') then
    vim.lsp.completion.enable(true, client.id, ev.buf, { autotrigger = true })
  end
end })

vim.api.nvim_create_autocmd('BufWritePost', { callback = function(ev)
  vim.rpcnotify(0, 'ksa_saved', vim.fn.fnamemodify(ev.file, ':p'), ev.buf)
end })

vim.api.nvim_create_autocmd('VimEnter', { callback = function()
  vim.notify('KSA script editor: :w saves & reloads, :q quits, K = docs, grn = rename, gra = actions')
end })
```

# Size per platform (compressed → unpacked)

| Component | Windows x64 | Linux x64 | macOS arm64 |
|---|---|---|---|
| Neovim 0.12.6 | 12.6 → 36.5 MB | 11.4 → 40 MB | 9.6 → 37 MB |
| LuaLS 3.19.2 | 4.5 → 23 MB | 3.7 → 21 MB | 3.3 → 20 MB |
| TypeScript 7 `tsc` | 9.8 → 27 MB | 9.7 → 27 MB | 9.3 → 26 MB |
| TS tree-sitter parser + queries | ~1 MB | ~1 MB | ~1 MB |
| **Total** | **≈ 28 → ≈ 88 MB** | **≈ 26 → ≈ 89 MB** | **≈ 23 → ≈ 84 MB** |

Lua-only is ≈ 17 MB → 60 MB. `share/nvim/runtime` can be trimmed (drop `doc/`, unused `syntax/`, `spell/`) but keep `lua/`, `plugin/`, `queries/`, `lsp/`, `filetype.lua`.[^report] Like gatOS's guest image and QEMU, these are content-stable blobs that belong outside the dist wipe step and are copied with `SkipUnchangedFiles`.

# Launch mechanics

`ProcessStartInfo` cannot attach a ConPTY or allocate a PTY; nvim must be spawned through purrTTY's PTY layer (`ProcessLaunchOptions.CreateCustom(absoluteNvimPath, file)`), which needs the launcher extension described in [purrTTY terminal launcher gap](/existing-mods/purrtty-terminal-launcher-gap.md). Do not `forkpty()` from managed code; purrTTY's fork-free `posix_spawn` path is the right one. Exit codes: `:q` → 0, `:cquit N` → N.

[^report]: Neovim shipping research report (2026-10-16)
[^releases]: Neovim latest release (GitHub API)
[^install]: Neovim INSTALL.md
[^starting]: Neovim :help starting (NVIM_APPNAME, XDG, --listen, --clean)
[^tui]: Neovim :help tui
[^lsp]: Neovim :help lsp
[^luals]: lua-language-server releases
[^tsgo]: TypeScript 7 native release tarballs
[^issue41853]: neovim #41853 exit stalls when DA1 is not answered
[^issue29516]: neovim #29516 vcruntime140.dll missing
