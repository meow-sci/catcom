---
type: Research Report
title: Neovim shipping research report (2026-10-16)
description: Verbatim research report produced by a delegated research pass on 2026-10-16; the primary source the distilled concepts in this bundle cite. Claims inside are tagged verified/inference/measured by the author of the report.
tags: [reference, report, raw]
generated: { by: claude-fable-5.1/ksa-scripting-research-subagent, at: 2026-10-16T20:00:00Z }
status: stable
---

> Mirrored from the session scratchpad into the bundle so the cited source outlives the session. Scratchpad paths mentioned inside (e.g. harness locations) no longer exist.

# Shipping Neovim inside a .NET 10 game mod (embedded libghostty-vt terminal, ConPTY / PTY)

Research date: 2026-10-16. Targets: Windows x64 + Linux x64 (required), macOS arm64 (nice-to-have).

Legend: **[V]** = verified against a primary source or by running the artifact on this machine (macOS arm64) today; **[I]** = inference / recommendation not directly stated by a source.

---

## 0. TL;DR recommendation

1. **Ship Neovim 0.12.6** (released 2026-10-09) from the official GitHub release archives: `nvim-win64.zip` (12.62 MB), `nvim-linux-x86_64.tar.gz` (11.40 MB), `nvim-macos-arm64.tar.gz` (9.60 MB) **[V]**. Each unpacks to `bin/`, `lib/nvim/parser/`, `share/nvim/runtime/` (~37–40 MB) **[V]**.
2. **Isolate config/state** by launching with `NVIM_APPNAME=<modname>` plus `XDG_CONFIG_HOME`, `XDG_DATA_HOME`, `XDG_STATE_HOME`, `XDG_CACHE_HOME` pointed at mod-owned directories; Neovim honours these on Windows too **[V]**. Put the shipped `init.lua` at `$XDG_CONFIG_HOME/<modname>/init.lua`.
3. **Save detection: use Neovim's RPC.** Start nvim with `--listen <addr>` (named pipe on Windows, Unix socket elsewhere), connect from .NET with a ~150-line hand-rolled MessagePack-RPC client on top of **MessagePack-CSharp 3.1.12** (MIT), and have the shipped `init.lua` register `BufWritePost -> vim.rpcnotify(0, 'saved', path)` **[V, tested headless]**. Keep a debounced `FileSystemWatcher` as a fallback for external editors, and set `backupcopy=yes` so saves overwrite in place (inode-stable) **[V, tested]**.
4. **Windows**: nvim.exe runs under ConPTY (minimum Windows 10 1809 / build 17763) **[V]**. If `TERM` is unset nvim guesses `vtpcon` once `ENABLE_VIRTUAL_TERMINAL_PROCESSING` succeeds **[V]**. nvim.exe needs `VCRUNTIME140.dll` (VC++ Redistributable) — it is *not* in the zip **[V]**. Prefer the `Microsoft.Windows.Console.ConPTY` NuGet (1.25.260930003, 2026-10-02) over the in-box conhost for consistent behaviour **[I]**.
5. **Linux/macOS**: set `TERM=xterm-256color` and `COLORTERM=truecolor`; nvim also has a builtin `xterm-ghostty` terminfo entry so `TERM=xterm-ghostty` works even without the ghostty terminfo installed **[V]**. Your emulator must answer DA1 (`CSI c`) because nvim uses it for key-encoding detection at startup and waits for it at exit **[V]**.
6. **Language tooling**: LuaLS 3.19.2 (~3–4.5 MB compressed, ~20–23 MB unpacked, MIT) and the native **TypeScript 7.0.2 `tsc --lsp --stdio`** binary (~9.7 MB compressed, ~27 MB unpacked, Apache-2.0, no Node required) **[V, both smoke-tested as stdio LSP servers]**. Total shipped editor stack ≈ 25–27 MB compressed / ≈ 85–90 MB unpacked per platform **[I, from measured parts]**.

---

## 1. Neovim releases in 2026

### 1.1 Versions and dates

| Item | Value | Source |
|---|---|---|
| Current stable | **Nvim 0.12.6**, tag `v0.12.6`, published 2026-10-09T15:43:25Z, marked *Latest* | GitHub API `releases/latest` **[V]**; https://github.com/neovim/neovim/releases |
| 0.12 line | 0.12.0 on 29 Mar 2026, then 0.12.1 (06 Apr), 0.12.2 (22 Apr), 0.12.3 (10 Jun), 0.12.4 (05 Jul), 0.12.5 (23 Aug), 0.12.6 (09 Oct) | https://github.com/neovim/neovim/releases **[V, dates from release list]**; 0.12.0 date corroborated by https://alternativeto.net/news/2026/3/neovim-0-12-has-been-released-with-a-built-in-plugin-manager-major-lsp-and-ui-upgrades |
| Previous line | 0.11.7 on 28 Mar 2026 | https://github.com/neovim/neovim/releases **[V]** |
| Nightly | `nightly` tag = v0.13.0-dev (pre-release) | https://github.com/neovim/neovim/releases **[V]** |
| `nvim --version` (0.12.6, macOS arm64) | `NVIM v0.12.6 / Build type: Release / LuaJIT 2.1.1774638290 / Run "nvim -V1 -v" for more info`; `-V1 -v` adds `Vim versions: 8.1, 8.2, 9.0, 9.1, 9.2`, `system vimrc file: "$VIM/sysinit.vim"`, `fall-back for $VIM: "/usr/local/share/nvim"` | run locally **[V]** |

### 1.2 Release artifacts (v0.12.6) — exact names and sizes

From the GitHub releases API (sizes in MB, decimal) **[V]**: https://api.github.com/repos/neovim/neovim/releases/latest

| Asset | Size |
|---|---|
| `nvim-win64.zip` | 12.62 |
| `nvim-win64.msi` | 12.46 |
| `nvim-win-arm64.zip` / `.msi` | 13.19 / 13.02 |
| `nvim-linux-x86_64.tar.gz` | 11.40 |
| `nvim-linux-x86_64.appimage` (+ `.zsync`) | 11.45 |
| `nvim-linux-arm64.tar.gz` / `.appimage` | 11.37 / 11.36 |
| `nvim-macos-arm64.tar.gz` | 9.60 |
| `nvim-macos-x86_64.tar.gz` | 9.84 |

The release workflow also builds `nvim-wasm-emscripten.zip` and publishes a `stable` tag alongside the version tag (`gh release create stable ...`) **[V]**: https://github.com/neovim/neovim/blob/master/.github/workflows/release.yml

### 1.3 What is inside (downloaded and extracted today) **[V]**

All three archives have the same top-level layout: `bin/`, `lib/nvim/parser/`, `share/nvim/runtime/`.

- **Windows `nvim-win64.zip`** → `nvim-win64/` (2235 files, 36.5 MB unpacked):
  `bin/nvim.exe` (6.98 MB), `bin/lua51.dll` (LuaJIT, 0.55 MB), `bin/DbgHelp.dll` (2.25 MB), `bin/tee.exe`, `bin/win32yank.exe` (1.14 MB), `bin/xxd.exe`; `lib/nvim/parser/{c,lua,markdown,markdown_inline,query,vim,vimdoc}.dll`; `share/nvim/runtime/`.
  - `nvim.exe` imports (via `strings`): `VCRUNTIME140.dll`, `api-ms-win-crt-*-l1-1-0.dll` (UCRT), `lua51.dll`, `dbghelp.dll`, plus kernel32/user32/ws2_32/etc. **No `vcruntime*.dll` is shipped in the zip.**
- **Linux `nvim-linux-x86_64.tar.gz`** → `nvim-linux-x86_64/` (40 MB on disk): `bin/nvim` (11 MB), `lib/nvim/parser/*.so` (same 7 parsers; `lib/` = 2.6 MB), `share/nvim/runtime/` (26 MB). Dynamic deps: only `libc.so.6`, `libm.so.6`, `libgcc_s.so.1`.
  - **Highest glibc symbol version referenced: `GLIBC_2.34`** → needs glibc ≥ 2.34 (built on the `ubuntu-22.04` runner per `release.yml`).
- **macOS `nvim-macos-arm64.tar.gz`** → `nvim-macos-arm64/` (37 MB): `bin/nvim` (7.2 MB), same parsers (`.so`), runtime. `LC_BUILD_VERSION minos 11.0` (and `MACOSX_DEPLOYMENT_TARGET: 11.0` in `release.yml`); links only system libs (`libSystem`, `libiconv`, `libutil`, CoreServices).
- Bundled tree-sitter parsers in 0.12.6: **c, lua, markdown, markdown_inline, query, vim, vimdoc** (no TypeScript). Parser ABI: `vim.treesitter.language_version = 15`, `minimum_language_version = 13` **[V, run locally]**.

### 1.4 Platform requirements and portability

- **Windows**: INSTALL.md says "Windows 8+ is required", that the zip can be unzipped anywhere ("administrator privileges are *not* required" and "`$VIMRUNTIME` will be set to that location automatically"), and "If you are missing `VCRUNTIME140.dll`, install the Visual Studio C++ redistributable" **[V]**: https://github.com/neovim/neovim/blob/master/INSTALL.md. The release page repeats "On Windows 'Server' you may need to install `vcruntime*.dll`" **[V]**. Open issue confirming the failure mode: https://github.com/neovim/neovim/issues/29516 ("fails to start if vcruntime140.dll is missing", open) **[V]**.
  - The UCRT (`api-ms-win-crt-*`) "is a Microsoft Windows operating system component. It's included as part of the operating system in Windows 10 or later" **[V]**: https://learn.microsoft.com/en-us/cpp/windows/universal-crt-deployment. `vcruntime140.dll` comes from the VC++ v14 Redistributable (one package covers VS 2017–2026) **[V]**: https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist.
  - **Verdict [I]**: the zip is "portable" in the no-installer/no-admin/no-registry sense, but **not** fully self-contained: ship `vc_redist.x64.exe` with the game (most games already do) or place an app-local `vcruntime140.dll` beside `nvim.exe`.
  - Note: issue https://github.com/neovim/neovim/issues/38595 (open) reports `xxd.exe` in the 0.12 zip being flagged by Defender as a false positive **[V]** — consider omitting `xxd.exe`/`win32yank.exe` from your bundle **[I]**.
  - 0.12 security change: "Nvim on Windows no longer searches the current directory for external command executables" **[V]**: https://neovim.io/doc/user/news-0.12.html
- **Linux**: supported builds need a modern glibc (≥ 2.34 measured). For older systems Neovim publishes "unsupported" builds "built with glibc 2.17" **[V]**: https://github.com/neovim/neovim-releases. The AppImage "might not work if your Linux distribution is more than 4 years old" **[V]**: INSTALL.md.
- **macOS**: INSTALL.md says "pre-built binaries for macOS 10.15+" but the 0.12.6 binary is built with deployment target 11.0 **[V, otool]** — treat 11.0 as the real floor **[I]**. INSTALL.md recommends `xattr -c ./nvim-macos-arm64.tar.gz` to avoid the "unknown developer" warning **[V]**; a mod loader that writes the files itself will normally not set the quarantine attribute, but test it **[I]**.

### 1.5 Licenses and redistribution

- `LICENSE.txt`: "Neovim is licensed under the terms of the Apache 2.0 license, except for parts of Neovim that were contributed under the Vim license"; the Vim license text follows; and "different license conditions may apply to some runtime files included with Vim; these will be specified in the header of each respective file" **[V]**: https://github.com/neovim/neovim/blob/master/LICENSE.txt. The file also lists bundled third-party components (e.g. "Luv: Apache 2.0 license") **[V]**.
- Practical requirement **[I]**: ship `LICENSE.txt` unmodified next to the binaries (Apache-2.0 §4 requires the license text and notices; Vim license §IV forbids removing it), and keep runtime files' headers intact.

### 1.6 `nvim --clean` and friends **[V]**

From https://neovim.io/doc/user/starting.html (`runtime/doc/starting.txt`):
- `--clean`: "Mimics a fresh install of Nvim: Skips initializations from files and environment variables. No 'shada' file is read or written. Excludes user directories from 'runtimepath'. Loads builtin plugins, unlike "-u NONE -i NONE"."
- Tested: `nvim --clean -u init.lua` **does** source the given `init.lua` (printed `INIT_RAN`) while `runtimepath` contains only system dirs (`/etc/xdg/nvim`, `/usr/local/share/nvim/site`, `/usr/share/nvim/site`) plus the bundled `share/nvim/runtime` **[V, run locally]**.

---

## 2. Isolating the shipped config from the user's own

### 2.1 Standard paths and XDG on all platforms **[V]**

https://neovim.io/doc/user/starting.html#standard-path: "The $XDG_CONFIG_HOME, $XDG_DATA_HOME, $XDG_RUNTIME_DIR, $XDG_STATE_HOME, $XDG_CACHE_HOME, $XDG_CONFIG_DIRS and $XDG_DATA_DIRS environment variables are used if defined, else default values (listed below) are used." The defaults table:

| | Unix | Windows |
|---|---|---|
| config `stdpath("config")` | `~/.config/nvim` | `~/AppData/Local/nvim` |
| data `stdpath("data")` | `~/.local/share/nvim` | `~/AppData/Local/nvim-data` |
| state `stdpath("state")` | `~/.local/state/nvim` | `~/AppData/Local/nvim-data` |
| cache `stdpath("cache")` | `~/.cache/nvim` | `~/AppData/Local/Temp/nvim-data` |
| run `stdpath("run")` | `/tmp/nvim.user/xxx` | `$TMP/nvim.user/xxx` |

So yes: Neovim honours XDG variables on Windows; it only falls back to `~/AppData/Local/...` when they are unset **[V]**.

### 2.2 `NVIM_APPNAME` **[V]**

"This variable controls the sub-directory that Nvim will read from (and auto-create) in each of the base directories. For example, setting `$NVIM_APPNAME` to "foo" ... will cause Nvim to look for configuration files in `$XDG_CONFIG_HOME/foo` instead of `$XDG_CONFIG_HOME/nvim`. `$NVIM_APPNAME` must be a name, such as "foo", or a relative path, such as "foo/bar"." The doc explicitly calls this out as the way to "isolate" Nvim applications (`*state-isolation*`). Source: https://neovim.io/doc/user/starting.html#$NVIM_APPNAME

Tested with `NVIM_APPNAME=ksa-editor` and the four XDG vars pointed into a scratch dir: `stdpath('config')` → `<cfg>/ksa-editor`, data/state/cache likewise, and `runtimepath` became `<cfg>/ksa-editor, /etc/xdg/ksa-editor, <data>/ksa-editor/site, /usr/local/share/ksa-editor/site, /usr/share/ksa-editor/site, <bundle>/share/nvim/runtime, ..., <bundle>/lib/nvim` — i.e. no user `~/.config/nvim` and no user plugins **[V, run locally]**.

### 2.3 `-u`, `--cmd`, `-c`, `+cmd` **[V]**

- `-u {vimrc}`: "The file {vimrc} is read for initializations. Most other initializations are skipped"; `-u NONE` skips all file/env initializations.
- `--cmd {command}`: "executed before processing any vimrc file ... up to 10 of these".
- `-c {command}` / `+{command}`: "executed after the first file has been read (and after autocommands and modelines for that file have been processed)".
- Example launch: `nvim --cmd "set backupcopy=yes" -c "normal! gg" <scriptfile>`.
Source: https://neovim.io/doc/user/starting.html

### 2.4 `$VIMRUNTIME` / `$VIM` **[V]**

Lookup order for `$VIMRUNTIME`: "1. Environment variable $VIMRUNTIME, if it is set. 2. Directory path "$VIM/runtime", if it exists. 3. Value of $VIM ... 4. If "../share/nvim/runtime" exists relative to |v:progpath|, it is used. 5. Path derived from the 'helpfile' option". Source: https://neovim.io/doc/user/starting.html#$VIMRUNTIME. Confirmed locally: running the extracted `bin/nvim` with no env set `$VIMRUNTIME` to `<extract>/share/nvim/runtime` **[V]**. So keeping the archive layout intact means you never need to set `VIMRUNTIME` **[I]**.

### 2.5 Recommended launch recipe **[I, built from verified pieces]**

```
env:  NVIM_APPNAME=ksa-editor
      XDG_CONFIG_HOME=<ModDir>/editor/config       # contains ksa-editor/init.lua (read-only is fine)
      XDG_DATA_HOME=<UserWritable>/editor/data
      XDG_STATE_HOME=<UserWritable>/editor/state   # shada, undo, swap, logs
      XDG_CACHE_HOME=<UserWritable>/editor/cache
      COLORTERM=truecolor
      TERM=xterm-256color          # Linux/macOS only; leave unset on Windows (nvim guesses "vtpcon")
      PATH=<ModDir>/editor/lsp/bin:$PATH  (or give absolute cmd paths in vim.lsp.config)
args: nvim --listen <addr> -c "<initial command>" <scriptfile>
cwd:  the scripts directory (so LSP root detection and :e paths work)
```

---

## 3. Save detection: options and trade-offs

### 3.1 (a) Neovim RPC — **recommended**

**Server side** **[V]**:
- `--listen {addr}`: "Start |RPC| server on pipe or TCP address {addr}. Sets the primary listen address |v:servername| to {addr}." https://neovim.io/doc/user/starting.html#--listen
- Address rules (`serverstart()`): "If {address} has a colon it is a TCP/IPv4/IPv6 address ... Else {address} is the path to a named pipe (except on Windows). If {address} has no slashes it is treated as the "name" part of a generated path `stdpath("run").."/{name}.{pid}.{counter}"`". Windows example in the docs: `serverstart('//./pipe/nvim-pipe-1234')`. https://neovim.io/doc/user/builtin.html#serverstart()
- `--embed` is *not* what you want here: it "Use[s] stdin/stdout as a msgpack-RPC channel" and "Waits for a client to call |nvim_ui_attach()| before sourcing startup files" — i.e. it is for GUIs that render the UI themselves; your terminal renders nvim's own TUI, so use `--listen` **[V]**.
- Protocol: "Nvim implements the MessagePack-RPC protocol with these extra (out-of-spec) constraints: 1. Responses must be given in reverse order of requests ... 2. Nvim processes all messages (requests and notifications) in the order they are received." https://neovim.io/doc/user/api.html#api-rpc. The spec it points to: https://github.com/msgpack-rpc/msgpack-rpc/blob/master/spec.md
- `$NVIM_LISTEN_ADDRESS` is deprecated ("Ignored if --listen is given") **[V]**: https://neovim.io/doc/user/deprecated.html
- Gotcha found while testing **[V]**: `--listen <very long unix path>` failed with `Failed to --listen: invalid argument` because the socket path exceeded the OS `sun_path` limit; passing a short name (e.g. `--listen ksa-editor`) made nvim generate `$TMPDIR/nvim.<user>/<rand>/ksa-editor.<pid>.0`, which the .NET side can read back via `v:servername` or compute. (Limit is 108 bytes on Linux / 104 on macOS **[I]**, see https://man7.org/linux/man-pages/man7/unix.7.html.)

**Hook in the shipped `init.lua`** **[V, tested headless]**:
```lua
vim.api.nvim_create_autocmd('BufWritePost', {
  pattern = '*',
  callback = function(ev)
    -- channel 0 = broadcast to every connected RPC client
    vim.rpcnotify(0, 'ksa_saved', vim.fn.fnamemodify(ev.file, ':p'), ev.buf)
  end,
})
```
- `BufWritePost`: "After writing the whole buffer to a file" https://neovim.io/doc/user/autocmd.html#BufWritePost
- `nvim_create_autocmd` callback "receives one argument, a table with keys: buf, data, event, file, match ..." (Since 0.7.0) https://neovim.io/doc/user/api.html#nvim_create_autocmd()
- `vim.rpcnotify({channel}, {method}, {...})`: "Sends {event} to {channel} via |RPC| and returns immediately. If {channel} is 0, the event is broadcast to all channels." https://neovim.io/doc/user/lua.html#vim.rpcnotify()
- If you prefer targeting one channel, the .NET client can learn its own channel id from `nvim_get_api_info()` ("item 0 is the current channel id") and store it in nvim via `nvim_exec_lua` **[V]**: https://neovim.io/doc/user/api.html#nvim_get_api_info()
- Identify your client with `nvim_set_client_info` ("Clients should call this just after connecting") **[V]**: https://neovim.io/doc/user/api.html#nvim_set_client_info()
- `nvim_buf_attach` is for *content* updates (`on_lines`/`on_bytes`/`on_changedtick`); it does not signal writes, so it is the wrong tool for "saved" **[V]**: https://neovim.io/doc/user/api.html#nvim_buf_attach()

**.NET client side**:
- Existing Neovim .NET clients are effectively dead: `dalance/NeovimClient` (last push 2016-01-02, 4 stars) and `Pireax/neovim.cs` (2016-01-26, 14 stars) **[V, GitHub API]**; NuGet `NvimClient` 1.0.0 exists but has no description/metadata **[V]**; `knt419/nvim-winui-gui` (pushed 2026-10-04, 0 stars) is a GUI, not a library **[V]**. Verdict **[I]**: hand-roll.
- Use **MessagePack-CSharp** (`MessagePack` NuGet) — latest v3.1.12 published 2026-10-12, MIT **[V]**: https://github.com/MessagePack-CSharp/MessagePack-CSharp/releases, https://www.nuget.org/packages/MessagePack. Frame messages as 4-element arrays `[0,msgid,method,params]` / `[1,msgid,error,result]` / `[2,method,params]` per the msgpack-rpc spec; `MessagePackReader`/`MessagePackWriter` are enough **[I]**.
- Transport: Windows → `new NamedPipeClientStream(".", "ksa-nvim-<pid>", PipeDirection.InOut)` **[V, API exists]** https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeclientstream (and pass `--listen \\.\pipe\ksa-nvim-<pid>` to nvim **[I]**); Linux/macOS → `Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)` + `UnixDomainSocketEndPoint(path)` **[V, API exists]** https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.unixdomainsocketendpoint
- Bonus: the same channel lets the mod *push* information into the editor (diagnostics from the game's script compiler, `:echo` banners, jump-to-line via `nvim_win_set_cursor`) **[I]**.

### 3.2 (b) Marker file / pipe written by `init.lua`

Tested variant: `BufWritePost` → `io.open(vim.env.KSA_SAVE_MARKER,'a'):write(ev.file..'\n')` worked headless **[V]**. Pros: zero protocol code. Cons **[I]**: you still need a watcher on the marker, you lose the bidirectional channel, and Windows named-pipe writes from Lua (`io.open('\\\\.\\pipe\\x','w')`) only work if the server already has a waiting instance. Use only as a fallback.

### 3.3 (c) `FileSystemWatcher` on the scripts directory

- .NET docs: "Common file system operations might raise more than one event ... Moving a file is a complex operation that consists of multiple simple operations, therefore raising multiple events", and the internal buffer can overflow and drop events **[V]**: https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher
- Neovim write strategy (`'backupcopy'`, default `"auto"`): `"yes"` = "make a copy of the file and overwrite the original one"; `"no"` = "rename the file and write a new one"; `"auto"` = "When Vim sees that renaming the file is possible without side effects ... that is used" **[V]**: https://neovim.io/doc/user/options.html#'backupcopy'. `'writebackup'` defaults on; `'backup'` defaults off **[V]**.
- **Measured on macOS/APFS with nvim 0.12.6** (after clearing `'backupskip'`, whose default `/private/tmp/*,$TMPDIR/*` had masked the first attempt): `backupcopy=auto` → **inode changed** (rename + new file); `backupcopy=yes` → same inode (in place); `backupcopy=no` → inode changed **[V]**. So with defaults a watcher sees Renamed/Created/Deleted churn, not a clean Changed. Set `backupcopy=yes` in the shipped config **[I]**.
- `'autoread'` is on by default, so if the *mod* rewrites a script nvim will pick it up (`FileChangedShell`) **[V]**: https://neovim.io/doc/user/options.html#'autoread'

### 3.4 Recommendation **[I]**

RPC (3.1) as the primary signal, `FileSystemWatcher` with ~100–200 ms debounce as a secondary for users who edit with their own `$EDITOR`, and `backupcopy=yes` regardless. Reload on `BufWritePost`, not on buffer change events.

---

## 4. Running nvim under ConPTY (Windows) and PTYs (Linux/macOS)

### 4.1 ConPTY basics **[V]**

- `CreatePseudoConsole(size, hInput, hOutput, dwFlags, &hPC)`; minimum client "Windows 10 October 2018 Update (version 1809)", server "Windows Server 2019"; input/output are "UTF-8 ... plain text interleaved with Virtual Terminal Sequences"; "On the input stream, plain text represents standard keyboard keys ... More complicated operations are represented by encoding control keys and mouse movements as virtual terminal sequences". https://learn.microsoft.com/en-us/windows/console/createpseudoconsole
- Process creation uses `STARTUPINFOEX` + `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` + `EXTENDED_STARTUPINFO_PRESENT`; resize with `ResizePseudoConsole`; close with `ClosePseudoConsole` (terminates the attached tree); the doc strongly recommends servicing the two pipes on separate threads to avoid deadlocks. https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session
- **Redistributable ConPTY**: NuGet `Microsoft.Windows.Console.ConPTY` 1.25.260930003 (2026-10-02, MIT) "allows applications to host Windows Console sessions. It should work on all versions of Windows 10.0.17763.0 and above" **[V]**: https://www.nuget.org/packages/Microsoft.Windows.Console.ConPTY. Windows Terminal stable is v1.25.2733.0 (2026-10-02) **[V]**: https://github.com/microsoft/terminal/releases
- **Terminal 1.22 (2024-08-27) rewrite**: "We have rewritten how hosted console applications are translated for Windows Terminal (and other ConPTY consumers) to be based on direct API->VT translation rather than "rendering" a point-in-time snapshot ... plus an ability for applications to send unmodified VT directly to the terminal, but may result in some compatibility issues" and "We have brought the new Windows 24H2 API out to the ConPTY NuGet package (#17704)" **[V]**: https://github.com/microsoft/terminal/releases/tag/v1.22.2362.0. What exactly the "24H2 API" adds is not stated there **[unverified]**.
- **Kitty keyboard protocol in Terminal/ConPTY**: PR "Implement the Kitty Keyboard Protocol" (microsoft/terminal #19817) merged 2026-02-17; a quoted downstream note says ConPTY 1.25 includes it **[V, PR page]**: https://github.com/microsoft/terminal/pull/19817. Whether the ConPTY layer forwards nvim's `CSI ? u` query to *your* terminal or answers it itself is not documented **[unverified]** — test it; nvim works fine with legacy key encoding if the query goes unanswered **[I]**.

### 4.2 What nvim itself does on Windows **[V, from source]**

- `TERM`: `tui.c` reads `$TERM`; `#ifdef MSWIN` it calls `os_tty_guess_term()` and, if `TERM` is unset, sets `TERM` to the guess. `os_tty_guess_term()` tries `SetConsoleMode(stdout, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING)`; on success the term is `"vtpcon"`, else `"conemu"` (if `ConEmuANSI=ON`) else `"win32con"`. https://github.com/neovim/neovim/blob/master/src/nvim/tui/tui.c and https://github.com/neovim/neovim/blob/master/src/nvim/os/os_win_console.c. So nvim does **not** ignore `TERM` on Windows; it only guesses when it is absent. The builtin terminfo table lists `Windows/VTP console → vtpcon` and `Windows/legacy console → win32con` https://neovim.io/doc/user/tui.html
- Output tty: on Windows nvim skips `uv_tty_set_mode(UV_TTY_MODE_IO)` for stdout (`#ifndef MSWIN`) and sets `kTerm_cursor_down` to `\x1b[B` as a workaround for libuv's LF→CRLF conversion (#10558) **[V, tui.c]**.
- Input: libuv's `UV_TTY_MODE_RAW` on Windows "also enables `ENABLE_WINDOW_INPUT`" (so `ResizePseudoConsole` reaches nvim as a window-size event) and `UV_TTY_MODE_RAW_VT` sets `ENABLE_VIRTUAL_TERMINAL_INPUT` **[V]**: https://docs.libuv.org/en/v1.x/tty.html. (Which mode nvim's `tui/input.c` selects was not located in this pass **[unverified]**.)
- True colour: `term_has_truecolor()` returns true if `COLORTERM` is `truecolor`/`24bit`, or terminfo has `Tc`/`RGB`/`setrgbf+setrgbb` **[V, tui.c]**; docs: "Nvim will automatically attempt to determine if the host terminal supports 24-bit color and will enable ['termguicolors'] if it does" https://neovim.io/doc/user/options.html#'termguicolors'. → set `COLORTERM=truecolor` in the child environment on every platform **[I]**.
- Key encoding negotiation: at startup nvim writes `CSI ? u CSI c` and "If a primary device attributes response is received without first receiving an answer to the progressive enhancement query (CSI u), then the terminal does not support the Kitty keyboard protocol"; it then enables kitty flags 1+2 (`CSI > 3 u`) or xterm `modifyOtherKeys` (`CSI > 4;2 m`), and sends the matching disable sequence on clean exit **[V]**: tui.c and https://neovim.io/doc/user/tui.html#tui-csiu. This call is not guarded by `MSWIN` **[V, tui.c]**.
- Cursor shape: uses terminfo `Ss`/`Se` (DECSCUSR) with a blacklist of terminals that cannot be trusted **[V]**: https://neovim.io/doc/user/tui.html; FAQ: `'guicursor'` is on by default and can be disabled with `set guicursor=` https://neovim.io/doc/user/faq.html
- Ctrl+C: `os_enable_ctrl_c()` re-enables console Ctrl-C processing after a detached start **[V, os_win_console.c]**.

### 4.3 Known Windows/ConPTY issues (2025–2026) **[V]**

- https://github.com/neovim/neovim/issues/41853 — "TUI: exit stalls for the full 1s EXIT_TIMEOUT_MS when the terminal never answers DA1 (CSI c)" (opened 2026-09-11, closed 2026-10-14, reporter on Windows ConPTY/conhost, 0.12.5). Implication: **your emulator must answer DA1** or every `:q` costs ~1 s on 0.12.6 (fix landed after 0.12.6 **[I]**).
- https://github.com/neovim/neovim/issues/37062 — "modifyOtherKeys is not disabled on exit on Windows" (open). Mitigation **[I]**: your pane owns the libghostty-vt instance; reset key-encoding modes when the nvim process exits.
- https://github.com/neovim/neovim/issues/38630 — "Windows: Ctrl+C does not interrupt running processes in :terminal (0.12 regression)" (closed 2026-04-07).
- https://github.com/microsoft/terminal/issues/9081 — cursor shape not restored after nvim exits in Windows Terminal (open since 2021, labelled Resolution-External); workaround `vim.o.guicursor = ''`.
- https://github.com/neovim/neovim/issues/29516 — missing `vcruntime140.dll` (open).

### 4.4 Linux / macOS **[V unless marked]**

- "The |$TERM| environment variable is the primary hint that determines the terminal type"; nvim reads terminfo "from the system terminfo database (or builtin defaults if terminfo is not found)"; builtin entries exist for `xterm`, `xterm-256color`, `ghostty`, `xterm-ghostty`, `tmux`, `screen`, `vte`, `kitty`, etc.; `$NVIM_TERMDEFS` (JSON) can override builtin definitions. https://neovim.io/doc/user/tui.html
- The release binary still uses Unibilium (0.12 only *added the option* to build without it) **[V]**: https://neovim.io/doc/user/news-0.12.html
- Therefore **[I]**: `TERM=xterm-256color` + `COLORTERM=truecolor` is the safe default; `TERM=xterm-ghostty` is fine for nvim itself (builtin entry) but child shells/tools spawned from `:!` would need the ghostty terminfo on the system (`TERMINFO`/`~/.terminfo` is honoured by ncurses/unibilium **[unverified here]**).
- 256 colours are assumed for anything claiming to be `xterm` or when `$COLORTERM`/`$TERM` contains "256" **[V]**: https://neovim.io/doc/user/tui.html#tui-colors
- `CTRL-Z` "Suspend Nvim, like ":stop"" **[V]**: https://neovim.io/doc/user/starting.html#CTRL-Z. Inside your own PTY this leaves a stopped child; either handle `SIGCHLD`/`WIFSTOPPED` + `SIGCONT`, or map `<C-z>` to `<Nop>` in `init.lua` **[I]**.

### 4.5 libghostty-vt specifics

- The C API header states it is "an incomplete, work-in-progress API" that "is not yet stable" and "is definitely going to change"; it covers terminal state, parsers (OSC/SGR), **key encoding including the Kitty keyboard protocol**, mouse/focus/paste encoding, render state for custom renderers, snapshots, search; **no PTY and no rendering** **[V]**: https://github.com/ghostty-org/ghostty/blob/main/include/ghostty/vt.h. Key encoder workflow (`ghostty_key_encoder_new` → `ghostty_key_encoder_setopt_from_terminal` → `ghostty_key_event_new` → `ghostty_key_encoder_encode`) is documented in https://github.com/ghostty-org/ghostty/blob/main/include/ghostty/vt/key.h **[V]**.
- Ghostty app releases: 1.3.1 (2026-03-13) latest; release notes do not mention a separate libghostty-vt tag **[V]**: https://ghostty.org/docs/install/release-notes
- **LibGhostty.Net 1.0.2** (NuGet, 2026-08-02, MIT, `net10.0`): bindings to the VT C ABI **plus** "A platform-native pseudo-terminal (PTY) process host"; RIDs `win-x64` (bundles `conpty.dll` + `OpenConsole.exe`), `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` (macOS ≥ 11.0; "Unix PTY helper"); `GhosttyPtyOptions` takes App, CommandLine, Cwd, Cols/Rows, environment variables; `Resize`, `Kill`, `WaitForExit`, `ProcessExited`, `ExitCode` **[V]**: https://www.nuget.org/packages/LibGhostty.Net and https://github.com/jkelin/libghostty-net (2 stars, last push 2026-08-02 **[V]** — young project; vendor it or be ready to fork **[I]**).
- Because nvim's startup query (`CSI ? u CSI c`) and exit path depend on a **DA1 reply**, make sure the VT layer's "response bytes" are written back to the PTY (the README describes the loop "reads bytes from a PTY, writes those bytes into a Ghostty terminal, renders ... and writes encoded user input back to the PTY"; whether query *responses* are auto-generated is not stated) **[V quote / I conclusion]**.

---

## 5. Language tooling to ship alongside nvim

### 5.1 Neovim built-ins used (0.11+/0.12) **[V]**

- `vim.lsp.config()` + `vim.lsp.enable()`: "Use |vim.lsp.config()| to define or modify LSP configurations, and |vim.lsp.enable()| to auto-activate them ... To create a new config you can either use `vim.lsp.config()` or create a `lsp/<config-name>.lua` file" on `runtimepath`. https://neovim.io/doc/user/lsp.html#lsp-config
- Default keymaps created at startup: `gra` (code action), `gri` (implementation), `grn` (rename), `grr` (references), `grt` (type definition), `grx` (codelens), `gO` (document symbols), insert-mode `CTRL-S` (signature help); buffer-local: `'omnifunc'` = `vim.lsp.omnifunc()` (`CTRL-X CTRL-O`), `K` = hover, `'tagfunc'`, `'formatexpr'`, document colours. https://neovim.io/doc/user/lsp.html#lsp-defaults
- Completion: `vim.lsp.completion.enable(true, client.id, bufnr, { autotrigger = true })` "enables insert-mode completion driven by an LSP server ... Use CTRL-Y to select an item"; 0.12 added `cmp` ordering, coloured previews, `completionItem/resolve` popup and `textDocument/inlineCompletion`. https://neovim.io/doc/user/lsp.html#lsp-completion and https://neovim.io/doc/user/news-0.12.html
- Tree-sitter: "Parsers are searched for as `parser/{lang}.*` in any 'runtimepath' directory"; `vim.treesitter.language.add('python', { path = "/path/to/python.so" })`; `vim.treesitter.start({buf}, {lang})` enables highlighting. https://neovim.io/doc/user/treesitter.html. 0.12 enabled tree-sitter highlighting for Markdown by default **[V]**: https://neovim.io/doc/user/news-0.12.html

### 5.2 lua-language-server (LuaLS) **[V]**

- Latest **3.19.2**, 2026-10-13, MIT. Assets: `lua-language-server-3.19.2-win32-x64.zip` 4.46 MB, `-linux-x64.tar.gz` 3.70 MB, `-linux-arm64.tar.gz` 2.63 MB, `-darwin-arm64.tar.gz` 3.29 MB, `-darwin-x64.tar.gz` 2.34 MB, `-win32-ia32.zip` 4.24 MB. https://github.com/LuaLS/lua-language-server/releases
- Unpacked (measured): linux-x64 21 MB (`bin/lua-language-server` 2.0 MB + `meta/`, `locale/`, `script/`), win32-x64 23 MB (bundles `msvcp140.dll`, `msvcp140_1.dll`, `concrt140.dll`; `lua-language-server.exe` 1.4 MB), darwin-arm64 20 MB.
- Runs as a stdio LSP server with no args (smoke-tested: replied to `initialize`); useful flags for isolation: `--configpath=<file>` ("When provided, config files from elsewhere (such as from VS Code) will no longer be loaded"), `--logpath`, `--metapath`, `--locale`, `--loglevel`.
- nvim-lspconfig reference config: `cmd = { 'lua-language-server' }`, `filetypes = { 'lua' }` https://github.com/neovim/nvim-lspconfig/blob/master/lsp/lua_ls.lua

### 5.3 TypeScript: native `tsc --lsp` (TypeScript 7) vs typescript-language-server

- **TypeScript 7.0** (native Go port) announced 2026-07-08; "npm install -D typescript ... will get you the new `tsc` executable"; editor support "built on the Language Server Protocol (LSP)"; no programmatic API until 7.1; Vue/MDX/Astro/Svelte not yet supported **[V]**: https://devblogs.microsoft.com/typescript/announcing-typescript-7-0/
- The GitHub release `typescript/v7.0.2` (2026-07-08) publishes **platform tarballs** `typescript-win32-x64.tgz` 9.78 MB, `typescript-linux-x64.tgz` 9.67 MB, `typescript-darwin-arm64.tgz` 9.27 MB, etc. **[V]**: https://github.com/microsoft/typescript-go/releases. Extracted (measured): `@typescript/typescript-linux-x64` → 27 MB (`lib/tsc` 24 MB static binary + `lib/*.d.ts`), win32-x64 → 27 MB (`lib/tsc.exe` 25 MB), darwin-arm64 → 26 MB; `license: Apache-2.0`. The wrapper `typescript` package lists these as `optionalDependencies` **[V]**.
- **Smoke test [V]**: `tsc --lsp --stdio` answered an LSP `initialize` with full capabilities (completion with trigger chars, hover, signature help, definition, references, ...); `tsc --lsp` without `--stdio` prints `only stdio is supported`. So **no Node.js is required** for TS language service. nvim-lspconfig has retired `tsgo` in favour of a `tsc` config **[V]**: https://github.com/neovim/nvim-lspconfig/blob/master/lsp/tsgo.lua. typescript-go repo will be "permanently archived in September 2026" after the merge into microsoft/TypeScript **[V]**: https://github.com/microsoft/typescript-go
- **typescript-language-server 6.0.2** (2026-10-09, Apache-2.0) requires `node >= 22.22.2` and launches as `typescript-language-server --stdio` **[V]**: https://github.com/typescript-language-server/typescript-language-server (package.json). Node 24.22.0 LTS (2026-10-14) downloads alone are 32.0 MB (linux-x64 .tar.xz), 37.7 MB (win-x64 .zip), 53.0 MB (darwin-arm64 .tar.gz) **[V]**: https://nodejs.org/dist/. → the native `tsc --lsp` route is ~3–5x smaller and simpler **[I]**.
- Tree-sitter TypeScript parser: not bundled with nvim. `tree-sitter-typescript` latest release v0.23.2 (2024-11-11) ships `tree-sitter-typescript.tar.xz` (1.03 MB source) and `.wasm` (nvim needs a native `.so/.dll`, not wasm) **[V]**: https://github.com/tree-sitter/tree-sitter-typescript/releases. Build `src/parser.c` + `src/scanner.c` into `parser/typescript.{so,dll,dylib}` at mod build time (ABI 13–15 accepted by 0.12.6) and ship highlight queries (`queries/typescript/highlights.scm`, e.g. from nvim-treesitter) **[I]**. nvim-treesitter is **not** archived (pushed 2026-10-14, default branch `main`) despite blog claims **[V]**: https://github.com/nvim-treesitter/nvim-treesitter

### 5.4 Minimal dependency-free `init.lua` **[I, assembled from verified APIs]**

```lua
-- editor basics
vim.o.backupcopy = 'yes'          -- overwrite in place (watcher-friendly)   [V option semantics]
vim.o.swapfile = false            -- state dir is mod-owned anyway
vim.o.number = true
vim.o.termguicolors = true        -- COLORTERM=truecolor is set by the host
vim.o.completeopt = 'menuone,noselect,popup'
vim.keymap.set({'n','v'}, '<C-z>', '<Nop>')   -- no SIGTSTP inside the pane

-- treesitter highlighting for bundled lua + shipped typescript parser
vim.api.nvim_create_autocmd('FileType', {
  pattern = { 'lua', 'typescript' },
  callback = function(ev) pcall(vim.treesitter.start, ev.buf) end,
})

-- LSP (binaries shipped under <mod>/editor/lsp/<platform>/)
local lsp_dir = vim.env.KSA_LSP_DIR
vim.lsp.config('lua_ls', {
  cmd = { lsp_dir .. '/lua-language-server/bin/lua-language-server',
          '--configpath=' .. lsp_dir .. '/luarc.json',
          '--logpath=' .. vim.fn.stdpath('log') .. '/luals' },
  filetypes = { 'lua' },
  root_markers = { '.luarc.json', '.git' },
  settings = { Lua = { runtime = { version = 'Lua 5.4' } } },
})
vim.lsp.config('tsc', {
  cmd = { lsp_dir .. '/typescript/lib/tsc', '--lsp', '--stdio' },
  filetypes = { 'typescript', 'javascript' },
  root_markers = { 'tsconfig.json', 'package.json', '.git' },
})
vim.lsp.enable({ 'lua_ls', 'tsc' })

vim.api.nvim_create_autocmd('LspAttach', {
  callback = function(ev)
    local client = assert(vim.lsp.get_client_by_id(ev.data.client_id))
    if client:supports_method('textDocument/completion') then
      vim.lsp.completion.enable(true, client.id, ev.buf, { autotrigger = true })
    end
  end,
})

-- save hook -> the mod (broadcast to all RPC clients)
vim.api.nvim_create_autocmd('BufWritePost', {
  callback = function(ev)
    vim.rpcnotify(0, 'ksa_saved', vim.fn.fnamemodify(ev.file, ':p'), ev.buf)
  end,
})

-- help banner
vim.api.nvim_create_autocmd('VimEnter', {
  callback = function()
    vim.notify('KSA script editor: :w saves & reloads, :q quits, K = docs, grn = rename, gra = actions, <C-x><C-o> = complete')
  end,
})
```

### 5.5 Size estimate per platform (compressed download → unpacked) **[I, summing measured values]**

| Component | Windows x64 | Linux x64 | macOS arm64 |
|---|---|---|---|
| Neovim 0.12.6 | 12.6 → 36.5 MB | 11.4 → ~40 MB | 9.6 → ~37 MB |
| LuaLS 3.19.2 | 4.5 → 23 MB | 3.7 → 21 MB | 3.3 → 20 MB |
| TypeScript 7.0.2 native `tsc` | 9.8 → 27 MB | 9.7 → 27 MB | 9.3 → 26 MB |
| TS tree-sitter parser + queries | ~1 MB | ~1 MB | ~1 MB |
| **Total** | **≈ 28 → ≈ 88 MB** | **≈ 26 → ≈ 89 MB** | **≈ 23 → ≈ 84 MB** |

Lua-only (no TS): ≈ 17–18 MB → ≈ 60 MB. You can trim `share/nvim/runtime` (e.g. drop `doc/`, unused `syntax/`, `ftplugin/`, `spell/`) if size matters, but keep `lua/`, `plugin/`, `queries/`, `lsp/`, `filetype.lua` **[I]**.

---

## 6. Alternatives worth a comparison row

| Editor | Latest release | Binary / size (x64) | Windows | Built-in tree-sitter | Built-in LSP | Config isolation | License | Notes |
|---|---|---|---|---|---|---|---|---|
| **Neovim 0.12.6** | 2026-10-09 **[V]** | 9.6–12.6 MB archive → 37–40 MB; bin 7–11 MB **[V]** | yes (ConPTY, needs vcruntime140) **[V]** | yes (7 bundled parsers) **[V]** | yes (`vim.lsp`) **[V]** | `NVIM_APPNAME` + XDG **[V]** | Apache-2.0 + Vim **[V]** | Needs shipped LSP servers for completion |
| **Helix 25.07.1** | 2025-07-18 (no newer release as of today) **[V]** | `helix-25.07.1-x86_64-linux.tar.xz` 16.64 MB → **206 MB** (hx 18 MB + 245 prebuilt grammars 183 MB); `x86_64-windows.zip` 28.55 MB; `aarch64-macos.tar.xz` 16.69 MB **[V]** | yes **[V]** | yes, grammars shipped **[V]** | yes **[V]** | `HELIX_RUNTIME` env; config dir fixed at `~/.config/helix` / `%AppData%\helix` ("TODO: allow env var override" in source) but `hx -c <file>` works **[V]** | MPL-2.0 **[V]** | Modal but *not* vim keys; glibc ≤ 2.34 symbols **[V]** https://github.com/helix-editor/helix/releases, https://docs.helix-editor.com/install.html, https://docs.helix-editor.com/building-from-source.html, https://docs.helix-editor.com/configuration.html, https://github.com/helix-editor/helix/blob/master/helix-loader/src/lib.rs |
| **micro 2.0.15** | 2025-12-31 **[V]** | ~5 MB per platform (`micro-2.0.15-win64.zip` 5.15, `linux64-static.tar.gz` 5.09, `macos-arm64.tar.gz` 4.88) **[V]** | yes **[V]** | no (regex syntax for 130+ langs) **[V]** | no (plugins only) **[I]** | `MICRO_CONFIG_HOME` env, else `XDG_CONFIG_HOME` **[V]** | MIT **[V]** | "single, batteries-included, static binary with no dependencies"; nano-like keys https://github.com/zyedidia/micro |
| **Kakoune v2026.05.21** | 2026-05-21 **[V]** | 1.61 MB linux tarball **[V]** | **no**: "no native Windows version is planned" **[V]** | no | no (kak-lsp external) | `XDG_CONFIG_HOME/kak` **[V]** | Unlicense **[V]** | https://github.com/mawww/kakoune |
| **vis v0.9** | 2024-05-01 **[V]** | small (curses + Lua/LPeg) | no (curses/POSIX) **[I]** | no (LPeg lexers) **[V]** | no | — | ISC **[V]** | https://github.com/martanne/vis |
| **GNU nano 9.2** / nano-win | nano 9.2 2026-07-31 **[V]** | small | nano-win (lhmouse, GPL-3.0, pushed 2026-08-28) has no GitHub releases; README not fetchable **[V partial]** | no | no | `-f rcfile` **[I]** | GPL-3.0 **[V]** | https://www.nano-editor.org/history.php, https://github.com/lhmouse/nano-win |

"Pick your editor" / `$EDITOR`: honouring `$VISUAL`/`$EDITOR` for power users costs nothing when the mod already owns a PTY; the save-notification path then degrades to the `FileSystemWatcher` fallback (3.3) **[I]**.

---

## 7. Process-level details

### 7.1 Launching with cwd, env, args

- `ProcessStartInfo` offers `WorkingDirectory`, `Environment` (dictionary), `ArgumentList` ("Strings added to the list don't need to be previously escaped"), `UseShellExecute`, `CreateNoWindow`, `StandardInput/Output/ErrorHandle` **[V]**: https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo. It has **no pseudoconsole attribute**, so `Process.Start` cannot attach a child to a ConPTY or allocate a Unix PTY **[I, from the property list]**.
- Windows: spawn through your PTY layer — `CreateProcessW` with `STARTUPINFOEX`/`PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` (P/Invoke), or `LibGhostty.Net`'s `GhosttyPtyConnectionFactory.StartAsync(new GhosttyPtyOptions { App, CommandLine, Cwd, Cols, Rows, ... })` which also exposes `ProcessExited`/`ExitCode` **[V]**.
- Linux/macOS: do **not** call `forkpty()` via P/Invoke from managed code. .NET's own spawner lives in native C (`SystemNative_ForkAndExecProcess`), uses `vfork()`/`fork()` with signal masks and only async-signal-safe work before `exec` **[V]**: https://github.com/dotnet/runtime/blob/main/src/native/libs/System.Native/pal_process.c. A managed callback in a forked child is undefined behaviour **[I]**. Use a tiny native helper (LibGhostty.Net ships a "Unix PTY helper" **[V]**) or `openpty()` + `posix_spawn()` with `POSIX_SPAWN_SETSID` and file actions that open the slave as stdio **[I]**.

### 7.2 Exit codes **[V]**

- "When using `:cquit` or when there was an error message Vim exits with exit code 1." https://neovim.io/doc/user/starting.html. Measured: `:cquit 3` → 3, `:cquit` → 1, `:q` → 0 (0.12.6). `-l script.lua` "Exits 1 on Lua error".
- `nvim_get_chan_info()` on `:terminal` buffers now includes `exitcode` (0.12) https://neovim.io/doc/user/news-0.12.html — not relevant to the host, but `ProcessExited`/`WaitForExit` from the PTY layer is **[I]**.
- ConPTY: `ClosePseudoConsole` terminates the attached process tree; drain the output pipe after close to avoid deadlock **[V]**: https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session

### 7.3 Preventing `:terminal` / `:!` from spawning a shell (note only)

- `'shell'` "default $SHELL or "sh", Win32: "cmd.exe"" — "Name of the shell to use for ! and :! commands" **[V]**: https://neovim.io/doc/user/options.html#'shell'. You can point it at a stub (`--cmd "set shell=<mod>/bin/noshell"`) or `vim.keymap.set` `:!`/`:terminal` to no-ops, but Lua (`vim.system`, `jobstart`) can still spawn processes **[I]**.
- Vim's restricted mode (`rvim`, `-Z`) was **removed** in Nvim **[V]**: https://neovim.io/doc/user/vim_diff.html. Treat the editor as untrusted-script-capable, same as the scripts it edits **[I]**.

---

## 8. Primary sources consulted

- Neovim releases: https://github.com/neovim/neovim/releases ; API: https://api.github.com/repos/neovim/neovim/releases/latest ; INSTALL.md: https://github.com/neovim/neovim/blob/master/INSTALL.md ; release workflow: https://github.com/neovim/neovim/blob/master/.github/workflows/release.yml ; legacy builds: https://github.com/neovim/neovim-releases ; LICENSE: https://github.com/neovim/neovim/blob/master/LICENSE.txt
- Neovim docs (rendered at neovim.io, text from `runtime/doc/*.txt` on master): starting https://neovim.io/doc/user/starting.html ; tui https://neovim.io/doc/user/tui.html ; options https://neovim.io/doc/user/options.html ; api https://neovim.io/doc/user/api.html ; lua https://neovim.io/doc/user/lua.html ; lsp https://neovim.io/doc/user/lsp.html ; treesitter https://neovim.io/doc/user/treesitter.html ; autocmd https://neovim.io/doc/user/autocmd.html ; builtin https://neovim.io/doc/user/builtin.html ; vvars https://neovim.io/doc/user/vvars.html ; remote https://neovim.io/doc/user/remote.html ; channel https://neovim.io/doc/user/channel.html ; deprecated https://neovim.io/doc/user/deprecated.html ; vim_diff https://neovim.io/doc/user/vim_diff.html ; news-0.12 https://neovim.io/doc/user/news-0.12.html ; faq https://neovim.io/doc/user/faq.html
- Neovim source: https://github.com/neovim/neovim/blob/master/src/nvim/tui/tui.c ; https://github.com/neovim/neovim/blob/master/src/nvim/tui/input.c ; https://github.com/neovim/neovim/blob/master/src/nvim/os/os_win_console.c ; https://github.com/neovim/neovim/blob/master/src/nvim/main.c
- Neovim issues: #41853, #37062, #38630, #29516, #38595 (links in §4.3 / §1.4)
- Microsoft: CreatePseudoConsole https://learn.microsoft.com/en-us/windows/console/createpseudoconsole ; pseudoconsole session https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session ; ConPTY NuGet https://www.nuget.org/packages/Microsoft.Windows.Console.ConPTY ; Terminal 1.22 https://github.com/microsoft/terminal/releases/tag/v1.22.2362.0 ; Terminal releases https://github.com/microsoft/terminal/releases ; kitty PR https://github.com/microsoft/terminal/pull/19817 ; cursor issue https://github.com/microsoft/terminal/issues/9081 ; UCRT https://learn.microsoft.com/en-us/cpp/windows/universal-crt-deployment ; VC redist https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist ; ProcessStartInfo https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo ; FileSystemWatcher https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher ; NamedPipeClientStream https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeclientstream ; UnixDomainSocketEndPoint https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.unixdomainsocketendpoint ; dotnet pal_process.c https://github.com/dotnet/runtime/blob/main/src/native/libs/System.Native/pal_process.c
- libuv tty: https://docs.libuv.org/en/v1.x/tty.html
- Ghostty: https://github.com/ghostty-org/ghostty/blob/main/include/ghostty/vt.h ; https://github.com/ghostty-org/ghostty/blob/main/include/ghostty/vt/key.h ; https://ghostty.org/docs/install/release-notes ; LibGhostty.Net https://www.nuget.org/packages/LibGhostty.Net ; https://github.com/jkelin/libghostty-net
- MessagePack: https://github.com/MessagePack-CSharp/MessagePack-CSharp/releases ; https://www.nuget.org/packages/MessagePack ; msgpack-rpc spec https://github.com/msgpack-rpc/msgpack-rpc/blob/master/spec.md ; old .NET clients https://github.com/dalance/NeovimClient , https://github.com/Pireax/neovim.cs
- LuaLS: https://github.com/LuaLS/lua-language-server/releases ; nvim-lspconfig lua_ls https://github.com/neovim/nvim-lspconfig/blob/master/lsp/lua_ls.lua
- TypeScript: https://devblogs.microsoft.com/typescript/announcing-typescript-7-0/ ; https://github.com/microsoft/typescript-go/releases ; https://github.com/microsoft/typescript-go ; https://github.com/typescript-language-server/typescript-language-server ; https://github.com/neovim/nvim-lspconfig/blob/master/lsp/tsgo.lua ; https://github.com/neovim/nvim-lspconfig/blob/master/lsp/ts_ls.lua ; Node https://nodejs.org/dist/ ; tree-sitter-typescript https://github.com/tree-sitter/tree-sitter-typescript/releases ; nvim-treesitter https://github.com/nvim-treesitter/nvim-treesitter
- Alternatives: Helix https://github.com/helix-editor/helix/releases , https://docs.helix-editor.com/install.html , https://docs.helix-editor.com/building-from-source.html , https://docs.helix-editor.com/configuration.html , https://github.com/helix-editor/helix/blob/master/helix-loader/src/lib.rs ; micro https://github.com/zyedidia/micro , https://github.com/zyedidia/micro/blob/master/internal/config/config.go ; Kakoune https://github.com/mawww/kakoune ; vis https://github.com/martanne/vis ; nano https://www.nano-editor.org/history.php ; nano-win https://github.com/lhmouse/nano-win
