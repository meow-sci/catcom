# ksa-editor TypeScript queries

Tree-sitter highlight, injection, locals, indent and fold queries used by the
shipped Neovim for TypeScript (parser: tree-sitter-typescript v0.23.2).

- Source: https://github.com/nvim-treesitter/nvim-treesitter
- Commit: ca581c3436fc2b236847f5b739ebc263739e5ca6 (committed 2026-10-14T11:47:00Z)
- Languages: typescript,ecma (runtime/queries/<lang>/ at the commit; typescript inherits the others)
- Files are byte-for-byte copies of the upstream files.
- License: nvim-treesitter is Apache-2.0 (see its LICENSE at that commit). The
  grammar these queries target, tree-sitter-typescript, is MIT.
- Refresh: tools/build-ts-parser.sh --queries (reads tools/nvim-treesitter.pin).
  Do not edit these files by hand.
