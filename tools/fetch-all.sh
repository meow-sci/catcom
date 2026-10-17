#!/usr/bin/env bash
# fetch-all.sh: ONE command that downloads every binary the mod ships, for EVERY platform
# (win-x64, linux-x64, osx-arm64), on this host. Nothing is compiled here. Run it once per clone and
# again whenever a pin under tools/ changes; `dotnet build` then produces a complete dist, and the
# dist target fails if anything listed below is missing.
#
#   tools/fetch-ts.sh                  typescript.js + sucrase (V8 runtime assets, platform-neutral)
#   tools/fetch-nvim.sh <all rids>     Neovim                       -> editor/<rid>/nvim/
#   tools/fetch-lsp.sh <all rids>      lua-language-server, tsc     -> editor/<rid>/lsp/
#   tools/fetch-natives.sh <all rids>  LuaJIT + TypeScript parser   -> src/.../runtimes/<rid>/native/,
#                                      (cross-built by CI, GitHub release)  editor/<rid>/nvim/lib/nvim/parser/
#
# Usage: tools/fetch-all.sh [--force] [--natives-from DIR]
#   --force             re-download everything even when the pinned versions are present
#   --natives-from DIR  install natives from a local tools/build-natives.sh output folder
#                       instead of the GitHub release (what CI does after building them)
#
# Everything lands in gitignored folders. The Windows twin is tools/fetch-all.ps1.
set -euo pipefail

TOOLS="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$TOOLS/.." && pwd)"
RIDS="win-x64 linux-x64 osx-arm64"

FORCE=""
NATIVES_FROM=""

die() { echo "fetch-all: error: $*" >&2; exit 1; }
step() { echo; echo "==> $*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --force) FORCE="--force" ;;
    --natives-from) shift; [ $# -gt 0 ] || die "--natives-from needs a directory"; NATIVES_FROM="$1" ;;
    -h|--help) sed -n '2,19p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "fetch-all: unknown argument '$1'" >&2; exit 2 ;;
  esac
  shift
done

step "TypeScript compiler assets (tools/fetch-ts.sh)"
bash "$TOOLS/fetch-ts.sh" $FORCE

step "Neovim for $RIDS (tools/fetch-nvim.sh)"
# shellcheck disable=SC2086
bash "$TOOLS/fetch-nvim.sh" $FORCE $RIDS

step "Language servers for $RIDS (tools/fetch-lsp.sh)"
# shellcheck disable=SC2086
bash "$TOOLS/fetch-lsp.sh" $FORCE $RIDS

step "Natives (LuaJIT, tree-sitter parser) for $RIDS (tools/fetch-natives.sh)"
if [ -n "$NATIVES_FROM" ]; then
  # shellcheck disable=SC2086
  bash "$TOOLS/fetch-natives.sh" $FORCE --from "$NATIVES_FROM" $RIDS
else
  # shellcheck disable=SC2086
  bash "$TOOLS/fetch-natives.sh" $FORCE $RIDS
fi

# The same list the dist target (src/KsaScripting.Dist) refuses to build without.
step "Checking that every shipped binary is present"
missing=0
check() {
  if [ -f "$REPO_ROOT/$1" ]; then
    printf '  ok       %s\n' "$1"
  else
    printf '  MISSING  %s\n' "$1"
    missing=$((missing + 1))
  fi
}
check src/KsaScripting.Runtime.V8/assets/typescript.js
check src/KsaScripting.Runtime.V8/assets/sucrase.bundle.js
for rid in $RIDS; do
  case "$rid" in
    win-x64)   exe=.exe; lib=lua51.dll;            ext=dll ;;
    linux-x64) exe="";   lib=libluajit-5.1.so.2;   ext=so ;;
    osx-arm64) exe="";   lib=libluajit-5.1.2.dylib; ext=dylib ;;
  esac
  check "src/KsaScripting.Runtime.Lua/runtimes/$rid/native/$lib"
  check "editor/$rid/nvim/bin/nvim$exe"
  check "editor/$rid/nvim/lib/nvim/parser/typescript.$ext"
  check "editor/$rid/lsp/lua-language-server/bin/lua-language-server$exe"
  check "editor/$rid/lsp/typescript/lib/tsc$exe"
done
[ "$missing" -eq 0 ] || die "$missing file(s) missing; see above"
echo
echo "fetch-all: complete. Build the mod with: dotnet build src/KsaScripting.Dist -c Release"
