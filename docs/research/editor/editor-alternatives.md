---
type: Comparison
title: Terminal editor alternatives to Neovim
description: Comparison of Helix, micro, Kakoune, vis and nano as shipped editors against Neovim, and the recommendation to ship Neovim while honouring $EDITOR for users with their own editor.
tags: [editor, helix, micro, kakoune, comparison]
sources:
  - id: report
    resource: /references/nvim-shipping-report-2026-10-16.md
    title: Neovim shipping research report (2026-10-16)
  - id: helix
    resource: https://github.com/helix-editor/helix/releases
    title: Helix releases
  - id: micro
    resource: https://github.com/zyedidia/micro
    title: micro editor
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:30:00Z }
status: stable
---

| Editor | Latest | Size (x64) | Windows | Tree-sitter | LSP | Config isolation | License | Verdict |
|---|---|---|---|---|---|---|---|---|
| **Neovim 0.12.6** | 2026-10-09 | 9.6–12.6 MB → 37–40 MB | yes (ConPTY; needs vcruntime140) | 7 parsers bundled | built-in client | `NVIM_APPNAME` + XDG | Apache-2.0 + Vim | **ship it**; needs shipped LSP servers |
| Helix 25.07.1 | 2025-07-18 | 16.6 MB → **206 MB** (183 MB of grammars) | yes | 245 grammars bundled | built-in | `HELIX_RUNTIME`, but config dir is fixed (`hx -c <file>` works) | MPL-2.0 | single binary but 5× larger and non-vim keys |
| micro 2.0.15 | 2025-12-31 | ~5 MB | yes | no (regex) | no | `MICRO_CONFIG_HOME` | MIT | good "simple mode" fallback for non-vim users |
| Kakoune 2026.05.21 | 2026-05-21 | 1.6 MB | **no Windows** | no | external | XDG | Unlicense | out |
| vis 0.9 | 2024-05-01 | small | no | LPeg lexers | no | — | ISC | out |
| GNU nano 9.2 / nano-win | 2026-07-31 | small | nano-win only (no releases) | no | no | `-f rcfile` | GPL-3.0 | out |

Recommendation from the research: ship Neovim; optionally also ship micro as a "simple" choice; honour `$VISUAL`/`$EDITOR` for power users (costs nothing since the mod already owns a PTY), with save detection degrading to the file watcher.[^report]

[^report]: Neovim shipping research report (2026-10-16)
[^helix]: Helix releases
[^micro]: micro editor
