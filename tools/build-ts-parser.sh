#!/usr/bin/env bash
# Builds the tree-sitter TypeScript parser that the shipped Neovim loads:
#   editor/<rid>/nvim/lib/nvim/parser/typescript.{dylib,so,dll}
# from tree-sitter-typescript v0.23.2 (typescript/src/parser.c + scanner.c), and
# refreshes the committed queries in editor/config/ksa-editor/queries/ from
# nvim-treesitter (pinned in tools/nvim-treesitter.pin).
#
# Usage: tools/build-ts-parser.sh [--force] [rid...]   rid: osx-arm64 | linux-x64 | win-x64 (default: host)
#        tools/build-ts-parser.sh --queries            refresh the committed queries
#
# The rid's Neovim must already exist (tools/fetch-nvim.sh <rid>); this script never creates it.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CACHE="$ROOT/tools/.cache"
TS_VERSION="0.23.2"
TS_ARCHIVE_URL="https://github.com/tree-sitter/tree-sitter-typescript/releases/download/v$TS_VERSION/tree-sitter-typescript.tar.xz"
# SHA-256 of that release asset, computed from the first download (2026-10-16) and checked on every run.
TS_ARCHIVE_SHA256="2d324af0616a692cc6fcaea35442a816decb2ef0d05242953cb1feb15a5dc72d"
# Dot-prefixed so Neovim's parser/typescript.* lookup never returns it as a parser.
SIDECAR_NAME=".typescript.ksa-version"
SRC=""

die() { echo "build-ts-parser: $*" >&2; exit 1; }

usage() {
  cat <<USAGE
usage: tools/build-ts-parser.sh [--force] [rid...]   rid: osx-arm64 | linux-x64 | win-x64 (default: host)
       tools/build-ts-parser.sh --queries
USAGE
}

sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d' ' -f1
  else
    shasum -a 256 "$1" | cut -d' ' -f1
  fi
}

host_rid() {
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) echo osx-arm64 ;;
    Linux-x86_64) echo linux-x64 ;;
    MINGW*-x86_64|MSYS*-x86_64|CYGWIN*-x86_64) echo win-x64 ;;
    *) die "unsupported host $(uname -s)-$(uname -m); pass a rid explicitly" ;;
  esac
}

# Downloads the release tarball into the cache (re-downloads if the cached copy fails
# the pinned SHA-256) and extracts it to $CACHE/tree-sitter-typescript-<ver>/.
ensure_source() {
  mkdir -p "$CACHE"
  [ -f "$CACHE/.gitignore" ] || printf '*\n' > "$CACHE/.gitignore"
  local archive="$CACHE/tree-sitter-typescript-$TS_VERSION.tar.xz"
  if [ ! -f "$archive" ] || [ "$(sha256_of "$archive")" != "$TS_ARCHIVE_SHA256" ]; then
    local part
    part="$(mktemp "$CACHE/download.XXXXXX")"
    curl -fsSL --proto '=https' --tlsv1.2 --retry 3 "$TS_ARCHIVE_URL" -o "$part"
    if [ "$(sha256_of "$part")" != "$TS_ARCHIVE_SHA256" ]; then
      rm -f "$part"
      die "SHA-256 mismatch for $TS_ARCHIVE_URL"
    fi
    chmod 0644 "$part"
    mv -f "$part" "$archive"
  fi
  SRC="$CACHE/tree-sitter-typescript-$TS_VERSION"
  if [ ! -f "$SRC/typescript/src/parser.c" ]; then
    local tmp
    tmp="$(mktemp -d "$CACHE/extract.XXXXXX")"
    tar -xJf "$archive" -C "$tmp"
    rm -rf "$SRC"
    chmod -R u+rwX,go+rX "$tmp"
    mv "$tmp" "$SRC"
  fi
  [ -f "$SRC/common/scanner.h" ] || die "extracted tree lacks common/scanner.h"
}

# Compiles $SRC/typescript/src/{parser,scanner}.c into the file $2 (inside work dir $3).
compile_rid() {
  local rid="$1" out="$2" work="$3"
  cd "$SRC/typescript"
  case "$rid" in
    osx-arm64)
      MACOSX_DEPLOYMENT_TARGET=11.0 cc -shared -fPIC -O2 -arch arm64 -I src src/parser.c src/scanner.c -o "$out"
      ;;
    linux-x64)
      if command -v zig >/dev/null 2>&1; then
        zig cc -target x86_64-linux-gnu.2.17 -shared -fPIC -O2 -I src src/parser.c src/scanner.c -o "$out"
      else
        need_docker
        docker run --rm --platform linux/amd64 -v "$SRC:/src:ro" -v "$work:/out" \
          -e HOST_UID="$(id -u)" -e HOST_GID="$(id -g)" ubuntu:22.04 bash -c '
            set -e
            apt-get update -qq >/dev/null
            apt-get install -y -qq gcc >/dev/null
            cd /src/typescript
            gcc -shared -fPIC -O2 -I src src/parser.c src/scanner.c -o /out/typescript.so
            chown "$HOST_UID:$HOST_GID" /out/typescript.so'
      fi
      ;;
    win-x64)
      if command -v zig >/dev/null 2>&1; then
        zig cc -target x86_64-windows-gnu -shared -O2 -I src src/parser.c src/scanner.c -o "$out"
      else
        need_docker
        docker run --rm --platform linux/amd64 -v "$SRC:/src:ro" -v "$work:/out" \
          -e HOST_UID="$(id -u)" -e HOST_GID="$(id -g)" ubuntu:22.04 bash -c '
            set -e
            apt-get update -qq >/dev/null
            apt-get install -y -qq gcc-mingw-w64-x86-64 >/dev/null
            cd /src/typescript
            x86_64-w64-mingw32-gcc -shared -O2 -I src src/parser.c src/scanner.c -o /out/typescript.dll
            chown "$HOST_UID:$HOST_GID" /out/typescript.dll'
      fi
      ;;
  esac
  cd "$ROOT"
}

need_docker() {
  command -v docker >/dev/null 2>&1 || die "neither zig nor docker is available for $rid"
}

# Verifies the built file: container type, exported entry point, and (linux) the glibc baseline.
verify_build() {
  local rid="$1" out="$2" work="$3" desc
  [ -s "$out" ] || die "$rid: no output was produced"
  desc="$(file -b "$out")"
  case "$rid" in
    osx-arm64)
      echo "$desc" | grep -Eq 'Mach-O 64-bit.*arm64' || die "$rid: unexpected file type: $desc"
      nm -gU "$out" | grep -Eq ' _tree_sitter_typescript$' || die "$rid: tree_sitter_typescript is not exported"
      ;;
    linux-x64)
      echo "$desc" | grep -Eq 'ELF 64-bit LSB shared object, x86-64' || die "$rid: unexpected file type: $desc"
      local report max
      report="$(docker run --rm --platform linux/amd64 -v "$work:/o:ro" ubuntu:22.04 bash -c '
        set -e
        apt-get update -qq >/dev/null
        apt-get install -y -qq binutils >/dev/null
        nm -D --defined-only /o/typescript.so | grep -Eq " T tree_sitter_typescript$" && echo EXPORT_OK
        objdump -T /o/typescript.so | grep -o "GLIBC_[0-9.]*" | sort -uV | tail -n 1 | sed "s/^/GLIBC_MAX /"')"
      echo "$report" | grep -qx 'EXPORT_OK' || die "$rid: tree_sitter_typescript is not exported"
      max="$(echo "$report" | sed -n 's/^GLIBC_MAX GLIBC_//p')"
      [ -n "$max" ] || die "$rid: could not read the glibc baseline"
      if [ "$(printf '%s\n%s\n' "$max" "2.17" | sort -V | tail -n 1)" != "2.17" ]; then
        die "$rid: needs glibc $max (> 2.17)"
      fi
      echo "build-ts-parser: $rid exports tree_sitter_typescript; glibc baseline $max"
      ;;
    win-x64)
      echo "$desc" | grep -Eq 'PE32\+ executable \(DLL\).*x86-64' || die "$rid: unexpected file type: $desc"
      docker run --rm --platform linux/amd64 -v "$work:/o:ro" ubuntu:22.04 bash -c '
        set -e
        apt-get update -qq >/dev/null
        apt-get install -y -qq binutils-mingw-w64-x86-64 >/dev/null
        x86_64-w64-mingw32-objdump -p /o/typescript.dll | sed -n "/Export Tables/,\$p" \
          | grep -Eq "\\] tree_sitter_typescript[[:space:]]*\$" || exit 3' \
        || die "$rid: tree_sitter_typescript is not in the export table"
      ;;
  esac
  echo "build-ts-parser: $rid verified: $desc"
}

build_rid() {
  local rid="$1"
  local parser_dir="$ROOT/editor/$rid/nvim/lib/nvim/parser"
  local ext
  case "$rid" in
    osx-arm64) ext=dylib ;;
    linux-x64) ext=so ;;
    win-x64) ext=dll ;;
    *) die "unknown rid '$rid' (expected osx-arm64, linux-x64 or win-x64)" ;;
  esac
  if [ ! -d "$parser_dir" ]; then
    echo "run tools/fetch-nvim.sh $rid first" >&2
    exit 1
  fi
  local out="$parser_dir/typescript.$ext"
  local sidecar="$parser_dir/$SIDECAR_NAME"
  if [ "$FORCE" -eq 0 ] && [ -f "$out" ] && [ -f "$sidecar" ] \
     && [ "$(tr -d '[:space:]' < "$sidecar")" = "$TS_VERSION" ]; then
    echo "build-ts-parser: $rid up to date ($out)"
    return 0
  fi

  local work
  work="$(mktemp -d "$CACHE/build-$rid.XXXXXX")"
  compile_rid "$rid" "$work/typescript.$ext" "$work"
  verify_build "$rid" "$work/typescript.$ext" "$work"
  cp "$work/typescript.$ext" "$out"
  printf '%s\n' "$TS_VERSION" > "$sidecar"
  rm -rf "$work"
  echo "build-ts-parser: wrote $out ($(wc -c < "$out" | tr -d ' ') bytes)"
}

pin_get() {
  sed -n "s/^$1=//p" "$ROOT/tools/nvim-treesitter.pin" | head -n 1 | tr -d '[:space:]'
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

refresh_queries() {
  local repo commit date raw_base dest stage langs changed lang dep
  repo="$(pin_get repo)"; commit="$(pin_get commit)"; date="$(pin_get date)"
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
shipped Neovim for TypeScript (parser: tree-sitter-typescript v$TS_VERSION).

- Source: $repo
- Commit: $commit (committed $date)
- Languages: $(echo $langs | tr ' ' ',') (runtime/queries/<lang>/ at the commit; typescript inherits the others)
- Files are byte-for-byte copies of the upstream files.
- License: nvim-treesitter is Apache-2.0 (see its LICENSE at that commit). The
  grammar these queries target, tree-sitter-typescript, is MIT.
- Refresh: tools/build-ts-parser.sh --queries (reads tools/nvim-treesitter.pin).
  Do not edit these files by hand.
README

  dest="$ROOT/editor/config/ksa-editor/queries"
  mkdir -p "$(dirname "$dest")"
  rm -rf "$dest"
  mv "$stage" "$dest"
  echo "build-ts-parser: queries from nvim-treesitter $commit written to $dest"
  for lang in $langs; do
    for f in "$dest/$lang"/*.scm; do
      printf '  %8s bytes  %s\n' "$(wc -c < "$f" | tr -d ' ')" "$f"
    done
  done
}

MODE=build
FORCE=0
RIDS=""
for arg in "$@"; do
  case "$arg" in
    --force) FORCE=1 ;;
    --queries) MODE=queries ;;
    -h|--help) usage; exit 0 ;;
    -*) usage >&2; die "unknown option $arg" ;;
    *) RIDS="$RIDS $arg" ;;
  esac
done

if [ "$MODE" = queries ]; then
  [ -z "$RIDS" ] || die "--queries takes no rid"
  refresh_queries
  exit 0
fi

if [ -z "$RIDS" ]; then
  RIDS=" $(host_rid)"
fi
ensure_source
for rid in $RIDS; do
  build_rid "$rid"
done
