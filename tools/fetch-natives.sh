#!/usr/bin/env bash
# fetch-natives.sh: downloads the cross-built natives (LuaJIT + the tree-sitter TypeScript parser)
# for every platform from the GitHub release named in tools/natives.pin and installs them where the
# build and the editor expect them. Nothing is compiled on this host; CI builds the release with
# tools/build-natives.sh.
#
# Usage: tools/fetch-natives.sh [--force] [--from DIR] [rid...]
#   rid        win-x64 | linux-x64 | osx-arm64   (default: all three)
#   --force    reinstall even when the installed natives already match the pinned release
#   --from DIR install from locally built archives (tools/build-natives.sh output:
#              DIR/natives-<rid>.tar.gz + DIR/SHA256SUMS) instead of the release
#
# Release access: the repository is private, so downloads need GitHub credentials: the gh CLI
#   (`gh auth login`), or a token in GH_TOKEN / GITHUB_TOKEN. A public repository needs neither.
#
# Integrity: each archive is SHA-256 checked against tools/assets.lock (key "<tag>/<asset>"). A
#   first download is cross-checked against the release's SHA256SUMS asset and the GitHub API digest
#   before it is pinned. `--from` archives are checked against DIR/SHA256SUMS only and never pinned.
#
# Installs (per rid):
#   src/KsaScripting.Runtime.Lua/runtimes/<rid>/native/<luajit lib>, .luajit-commit, .ksa-natives
#   editor/<rid>/nvim/lib/nvim/parser/typescript.<ext>, .typescript.ksa-version, .ksa-natives
#       (needs that rid's Neovim: tools/fetch-nvim.sh runs first in tools/fetch-all.sh)
#   tools/.cache/bin/<rid>/luajit[.exe]  (the CLI, for manual checks; not shipped)
# The archive's LuaJIT commit must equal tools/luajit.pin, otherwise the release is stale.
# Env: KSA_EDITOR_DIR / KSA_RUNTIMES_DIR override the two install roots (for test copies).
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TOOLS="$REPO_ROOT/tools"
LOCK="$TOOLS/assets.lock"
RUNTIMES_DIR="${KSA_RUNTIMES_DIR:-$REPO_ROOT/src/KsaScripting.Runtime.Lua/runtimes}"   # override for test copies
EDITOR_DIR="${KSA_EDITOR_DIR:-$REPO_ROOT/editor}"
BIN_DIR="$TOOLS/.cache/bin"
ALL_RIDS="win-x64 linux-x64 osx-arm64"

FORCE=0
FROM=""
RIDS=""
WORK=""
REPO=""
TAG=""
LUAJIT_COMMIT=""
AUTH=""          # gh | token | none
RELEASE_JSON=""  # path of the release metadata, once fetched (empty if unavailable)

die()  { echo "fetch-natives: error: $*" >&2; exit 1; }
warn() { echo "fetch-natives: WARNING: $*" >&2; }
info() { echo "$*"; }

usage() { sed -n '2,25p' "$0" | sed 's/^# \{0,1\}//'; }

pin_value() { # <file> <key>
  local v
  v="$(sed -n "s/^$2=//p" "$TOOLS/$1" | tr -d '\r' | sed -n '1p')"
  [ -n "$v" ] || die "tools/$1 has no '$2=' line"
  printf '%s' "$v"
}

sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'
  else shasum -a 256 "$1" | awk '{print $1}'; fi
}

lib_name()   { case "$1" in win-x64) echo lua51.dll ;; linux-x64) echo libluajit-5.1.so.2 ;; osx-arm64) echo libluajit-5.1.2.dylib ;; esac; }
parser_ext() { case "$1" in win-x64) echo dll ;; linux-x64) echo so ;; osx-arm64) echo dylib ;; esac; }

# ---------------------------------------------------------------- tools/assets.lock

lock_lookup() { # <key>
  [ -f "$LOCK" ] || return 0
  awk -v a="$1" '$0 !~ /^[[:space:]]*#/ && NF >= 2 && $2 == a { print $1; exit }' "$LOCK"
}

lock_pin() { # <sha256> <key>
  if [ ! -f "$LOCK" ]; then
    printf '%s\n' \
      "# tools/assets.lock: SHA-256 pins for release assets fetched by tools/fetch-nvim.*, tools/fetch-lsp.* and tools/fetch-natives.*" \
      "# Format: <sha256>  <asset file name>   (two spaces, as sha256sum prints it; natives are keyed <tag>/<asset>)" \
      "# An asset missing here is pinned on its first download, after cross-checking the release's own digests." \
      "# A version bump that keeps the same file name (nvim-win64.zip, typescript-*.tgz) needs the old line removed first." \
      > "$LOCK"
  fi
  printf '%s  %s\n' "$1" "$2" >> "$LOCK"
}

# ---------------------------------------------------------------- release access

detect_auth() {
  if command -v gh >/dev/null 2>&1 && gh auth status >/dev/null 2>&1; then AUTH=gh
  elif [ -n "${GH_TOKEN:-}${GITHUB_TOKEN:-}" ]; then AUTH=token
  else AUTH=none; fi
}

token() { printf '%s' "${GH_TOKEN:-${GITHUB_TOKEN:-}}"; }

api_get() { # <path> <dest>: GET https://api.github.com/<path> with whatever credentials exist
  case "$AUTH" in
    gh)    gh api "$1" > "$2" 2>/dev/null ;;
    token) curl -fsSL --retry 3 --retry-delay 2 --proto '=https' --tlsv1.2 -H 'Accept: application/vnd.github+json' \
             -H "Authorization: Bearer $(token)" -o "$2" "https://api.github.com/$1" ;;
    none)  curl -fsSL --retry 3 --retry-delay 2 --proto '=https' --tlsv1.2 -H 'Accept: application/vnd.github+json' \
             -o "$2" "https://api.github.com/$1" ;;
  esac
}

# fetch_release_json: downloads the release metadata once (empty RELEASE_JSON if that fails).
fetch_release_json() {
  [ -z "$RELEASE_JSON" ] || return 0
  local f="$WORK/release.json"
  if api_get "repos/$REPO/releases/tags/$TAG" "$f"; then
    RELEASE_JSON="$f"
  else
    RELEASE_JSON=""
    warn "could not read the release metadata for $REPO $TAG (asset digests will not be cross-checked)"
  fi
}

# release_field <asset> <field: id|digest>: from the release JSON (empty if unknown).
release_field() {
  [ -n "$RELEASE_JSON" ] || return 0
  (cd / && python3 -I -c 'import json, sys
asset, field, path = sys.argv[1], sys.argv[2], sys.argv[3]
with open(path, encoding="utf-8") as fh:
    for a in json.load(fh).get("assets", []):
        if a.get("name") == asset:
            v = a.get(field) or ""
            if field == "digest" and isinstance(v, str) and v.startswith("sha256:"):
                v = v[7:]
            print(v)
            break
' "$1" "$2" "$RELEASE_JSON")
}

# download_asset <asset> <dest>: by gh, by token (API asset URL, redirect followed without the
# auth header, which the storage backend rejects), or anonymously (public repository).
download_asset() {
  local asset="$1" dest="$2" id url
  if [ -n "$FROM" ]; then
    [ -f "$FROM/$asset" ] || die "$FROM/$asset not found (run tools/build-natives.sh first)"
    cp "$FROM/$asset" "$dest"
    return 0
  fi
  info "    downloading $asset from $REPO release $TAG ($AUTH)"
  case "$AUTH" in
    gh)
      gh release download "$TAG" -R "$REPO" -p "$asset" -O "$dest" --clobber \
        || die "gh could not download $asset from release $TAG of $REPO (does the release exist? is the tag in tools/natives.pin right?)" ;;
    token)
      fetch_release_json
      [ -n "$RELEASE_JSON" ] || die "release $TAG of $REPO is not readable with the given token"
      id="$(release_field "$asset" id)"
      [ -n "$id" ] || die "release $TAG of $REPO has no asset named $asset"
      url="$(curl -fsS --proto '=https' --tlsv1.2 -o /dev/null -w '%{redirect_url}' \
               -H 'Accept: application/octet-stream' -H "Authorization: Bearer $(token)" \
               "https://api.github.com/repos/$REPO/releases/assets/$id")" || die "asset lookup failed for $asset"
      [ -n "$url" ] || die "GitHub returned no download location for $asset"
      curl -fsSL --retry 3 --retry-delay 2 --proto '=https' --tlsv1.2 -o "$dest" "$url" || die "download failed: $asset" ;;
    none)
      curl -fsSL --retry 3 --retry-delay 2 --proto '=https' --tlsv1.2 -o "$dest" \
        "https://github.com/$REPO/releases/download/$TAG/$asset" \
        || die "could not download $asset anonymously from https://github.com/$REPO/releases/tag/$TAG. The repository is private: install the GitHub CLI and run 'gh auth login', or export GH_TOKEN with a token that can read the repository, then rerun." ;;
  esac
}

# ---------------------------------------------------------------- verification

# sums_lookup <sumfile> <asset>: the SHA-256 listed for an asset in a sha256sum-style file.
sums_lookup() {
  awk -v a="$2" '{ f = $NF; sub(/^\*/, "", f); if (f == a) { print $1; exit } }' "$1"
}

# verify_archive <file> <asset>: lock pin (release) or SHA256SUMS (--from); pins first downloads.
verify_archive() {
  local file="$1" asset="$2" actual expected key published digest
  actual="$(sha256_of "$file")"
  if [ -n "$FROM" ]; then
    [ -f "$FROM/SHA256SUMS" ] || die "$FROM/SHA256SUMS is missing"
    published="$(sums_lookup "$FROM/SHA256SUMS" "$asset")"
    [ -n "$published" ] || die "$FROM/SHA256SUMS does not list $asset"
    [ "$published" = "$actual" ] || die "SHA-256 mismatch for $asset: $FROM/SHA256SUMS says $published, file is $actual"
    info "    sha256 ok (matches $FROM/SHA256SUMS)"
    return 0
  fi
  key="$TAG/$asset"
  expected="$(lock_lookup "$key")"
  if [ -n "$expected" ]; then
    [ "$expected" = "$actual" ] || die "SHA-256 mismatch for $key: tools/assets.lock has $expected, download is $actual. If the release was republished on purpose, remove that line from tools/assets.lock."
    info "    sha256 ok ($key is pinned in tools/assets.lock)"
    return 0
  fi
  warn "no SHA-256 pinned for $key; cross-checking the release before pinning it"
  [ -f "$WORK/SHA256SUMS" ] || download_asset SHA256SUMS "$WORK/SHA256SUMS"
  published="$(sums_lookup "$WORK/SHA256SUMS" "$asset")"
  [ -n "$published" ] || die "the release's SHA256SUMS does not list $asset"
  [ "$published" = "$actual" ] || die "the release's SHA256SUMS lists $published for $asset but the download is $actual"
  info "    release SHA256SUMS: matches"
  fetch_release_json
  digest="$(release_field "$asset" digest)"
  if [ -n "$digest" ]; then
    [ "$digest" = "$actual" ] || die "GitHub API digest for $asset is $digest but the download is $actual"
    info "    GitHub API digest: matches (sha256:$digest)"
  fi
  lock_pin "$actual" "$key"
  warn "pinned $key = $actual in tools/assets.lock; review that line before committing it"
}

# ---------------------------------------------------------------- install

installed_tag() { # <marker file>
  [ -f "$1" ] && tr -d '[:space:]' < "$1" || true
}

install_rid() {
  local rid="$1" asset lib ext native parser_dir extract archive commit marker_tag
  asset="natives-$rid.tar.gz"
  lib="$(lib_name "$rid")"
  ext="$(parser_ext "$rid")"
  native="$RUNTIMES_DIR/$rid/native"
  parser_dir="$EDITOR_DIR/$rid/nvim/lib/nvim/parser"
  marker_tag="$TAG"
  [ -z "$FROM" ] || marker_tag="local:$(sums_lookup "$FROM/SHA256SUMS" "$asset" 2>/dev/null | cut -c1-12)"

  if [ "$FORCE" -eq 0 ] && [ -z "$FROM" ] \
     && [ -f "$native/$lib" ] && [ "$(installed_tag "$native/.ksa-natives")" = "$TAG" ] \
     && [ "$(installed_tag "$native/.luajit-commit")" = "$LUAJIT_COMMIT" ] \
     && [ -f "$parser_dir/typescript.$ext" ] && [ "$(installed_tag "$parser_dir/.ksa-natives")" = "$TAG" ]; then
    info "[$rid] natives $TAG already installed"
    return 0
  fi

  [ -d "$EDITOR_DIR/$rid/nvim/lib/nvim" ] \
    || die "$rid: $EDITOR_DIR/$rid/nvim is not installed; run tools/fetch-nvim.sh $rid first (tools/fetch-all.sh does this in order)"

  extract="$WORK/$rid"
  mkdir -p "$extract"
  archive="$WORK/$asset"
  download_asset "$asset" "$archive"
  verify_archive "$archive" "$asset"
  tar -xzf "$archive" -C "$extract"
  [ -f "$extract/luajit/$lib" ] || die "$asset does not contain luajit/$lib"
  [ -f "$extract/parser/typescript.$ext" ] || die "$asset does not contain parser/typescript.$ext"
  commit="$(installed_tag "$extract/luajit/.luajit-commit")"
  [ "$commit" = "$LUAJIT_COMMIT" ] \
    || die "$asset was built from LuaJIT $commit but tools/luajit.pin says $LUAJIT_COMMIT: the natives release is stale. Push a new natives-v* tag (CI builds and publishes it), then update tools/natives.pin."

  # LuaJIT library: copy then rename, so a library a running test host has mapped is never
  # rewritten in place (macOS kills processes whose mapped code pages change).
  mkdir -p "$native"
  rm -f "$native/.ksa-natives"
  cp "$extract/luajit/$lib" "$native/$lib.tmp.$$"
  mv -f "$native/$lib.tmp.$$" "$native/$lib"
  cp "$extract/luajit/.luajit-commit" "$native/.luajit-commit"
  printf '%s\n' "$marker_tag" > "$native/.ksa-natives"

  # Parser next to Neovim's own parsers (the sidecars are dot-prefixed so nvim's parser/typescript.*
  # lookup never matches them).
  mkdir -p "$parser_dir"
  rm -f "$parser_dir/.ksa-natives" "$parser_dir"/typescript.*
  cp "$extract/parser/typescript.$ext" "$parser_dir/typescript.$ext"
  cp "$extract/parser/.typescript.ksa-version" "$parser_dir/.typescript.ksa-version"
  printf '%s\n' "$marker_tag" > "$parser_dir/.ksa-natives"

  # The CLI (not shipped): tools/.cache/bin/<rid>/
  if [ -d "$extract/luajit/cli" ]; then
    rm -rf "$BIN_DIR/$rid"
    mkdir -p "$BIN_DIR"
    mv "$extract/luajit/cli" "$BIN_DIR/$rid"
  fi
  if [ "$(uname -s)" = Darwin ]; then
    xattr -dr com.apple.quarantine "$native" "$parser_dir" "$BIN_DIR/$rid" 2>/dev/null || true
  fi
  info "[$rid] installed $lib ($(wc -c < "$native/$lib" | tr -d ' ') bytes) and typescript.$ext ($(wc -c < "$parser_dir/typescript.$ext" | tr -d ' ') bytes) from $asset"
}

# ---------------------------------------------------------------- main

main() {
  local arg rid
  while [ $# -gt 0 ]; do
    arg=$1
    case "$arg" in
      --force) FORCE=1 ;;
      --from) shift; [ $# -gt 0 ] || die "--from needs a directory"; FROM="$(cd "$1" && pwd)" ;;
      -h|--help) usage; exit 0 ;;
      -*) echo "fetch-natives: unknown option '$arg'" >&2; usage >&2; exit 2 ;;
      win-x64|linux-x64|osx-arm64)
        case " $RIDS " in *" $arg "*) ;; *) RIDS="$RIDS $arg" ;; esac ;;
      *) echo "fetch-natives: unknown rid '$arg' (expected: win-x64 linux-x64 osx-arm64)" >&2; exit 2 ;;
    esac
    shift
  done
  [ -n "$RIDS" ] || RIDS=" $ALL_RIDS"

  REPO="$(pin_value natives.pin repo)"
  TAG="$(pin_value natives.pin tag)"
  LUAJIT_COMMIT="$(pin_value luajit.pin commit)"
  mkdir -p "$RUNTIMES_DIR"
  printf '*\n!.gitignore\n' > "$RUNTIMES_DIR/.gitignore"

  if [ -n "$FROM" ]; then
    info "installing natives from $FROM"
  else
    detect_auth
    info "natives release: $REPO $TAG (access: $AUTH)"
  fi

  WORK="$(mktemp -d)"
  trap 'rm -rf "$WORK"' EXIT
  for rid in $RIDS; do
    install_rid "$rid"
  done
}

main "$@"
