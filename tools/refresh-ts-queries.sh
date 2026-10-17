#!/usr/bin/env bash
# refresh-ts-queries.sh: refreshes the committed tree-sitter queries the shipped Neovim uses for
# TypeScript (editor/config/ksa-editor/queries/) from the nvim-treesitter commit pinned in
# tools/nvim-treesitter.pin. The parser those queries target is built by tools/build-natives.sh
# (tools/ts-parser.pin). Usage: tools/refresh-ts-queries.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CACHE="$ROOT/tools/.cache"

die() { echo "refresh-ts-queries: $*" >&2; exit 1; }

pin_get() {
  sed -n "s/^$1=//p" "$ROOT/tools/$2" | head -n 1 | tr -d '[:space:]'
}

# Downloads <name>.scm for the five query kinds from $1 into $2; 404 means "not in this language".
fetch_lang_queries() {
  local url="$1" dest="$2" f code found=0
  mkdir -p "$dest"
  for f in highlights injections locals indents folds; do
    code="$(curl -sSL --proto '=https' --tlsv1.2 --retry 3 -o "$dest/$f.scm" -w '%{http_code}' "$url/$f.scm")" \
      || die "download failed: $url/$f.scm"
    case "$code" in
      200) found=$((found + 1)) ;;
      404) rm -f "$dest/$f.scm" ;;
      *) die "HTTP $code for $url/$f.scm" ;;
    esac
  done
  [ "$found" -gt 0 ] || die "no query files at $url"
}

# Prints the languages named by '; inherits: a,b' header lines in the .scm files of $1.
inherits_of() {
  local f
  for f in "$1"/*.scm; do
    [ -f "$f" ] || continue
    sed -n 's/^; inherits: *//p' "$f" | tr ',' '\n' | tr -d ' \t\r'
  done | grep -v '^$' || true
}

repo="$(pin_get repo nvim-treesitter.pin)"; commit="$(pin_get commit nvim-treesitter.pin)"; date="$(pin_get date nvim-treesitter.pin)"
ts_version="$(pin_get version ts-parser.pin)"
printf '%s' "$commit" | grep -Eq '^[0-9a-f]{40}$' || die "tools/nvim-treesitter.pin: bad commit '$commit'"
case "$repo" in
  https://github.com/*) raw_base="https://raw.githubusercontent.com/${repo#https://github.com/}" ;;
  *) die "tools/nvim-treesitter.pin: repo must be a github.com URL" ;;
esac
mkdir -p "$CACHE"
[ -f "$CACHE/.gitignore" ] || printf '*\n' > "$CACHE/.gitignore"
stage="$(mktemp -d "$CACHE/queries.XXXXXX")"

# Fetch typescript, then every language it (transitively) inherits from.
langs="typescript"
changed=1
while [ "$changed" -eq 1 ]; do
  changed=0
  for lang in $langs; do
    if [ ! -d "$stage/$lang" ]; then
      fetch_lang_queries "$raw_base/$commit/runtime/queries/$lang" "$stage/$lang"
      changed=1
      for dep in $(inherits_of "$stage/$lang"); do
        case " $langs " in
          *" $dep "*) ;;
          *) langs="$langs $dep" ;;
        esac
      done
    fi
  done
done

cat > "$stage/README.md" <<README
# ksa-editor TypeScript queries

Tree-sitter highlight, injection, locals, indent and fold queries used by the
shipped Neovim for TypeScript (parser: tree-sitter-typescript v$ts_version).

- Source: $repo
- Commit: $commit (committed $date)
- Languages: $(echo $langs | tr ' ' ',') (runtime/queries/<lang>/ at the commit; typescript inherits the others)
- Files are byte-for-byte copies of the upstream files.
- License: nvim-treesitter is Apache-2.0 (see its LICENSE at that commit). The
  grammar these queries target, tree-sitter-typescript, is MIT.
- Refresh: tools/refresh-ts-queries.sh (reads tools/nvim-treesitter.pin).
  Do not edit these files by hand.
README

dest="$ROOT/editor/config/ksa-editor/queries"
mkdir -p "$(dirname "$dest")"
rm -rf "$dest"
mv "$stage" "$dest"
echo "refresh-ts-queries: queries from nvim-treesitter $commit written to $dest"
for lang in $langs; do
  for f in "$dest/$lang"/*.scm; do
    printf '  %8s bytes  %s\n' "$(wc -c < "$f" | tr -d ' ')" "$f"
  done
done
