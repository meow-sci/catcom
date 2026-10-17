#!/usr/bin/env bash
# fetch-lsp.sh: download the language servers the shipped Neovim uses, into editor/<rid>/lsp/.
#
# Usage: tools/fetch-lsp.sh [--force] [rid...]
#   rid      win-x64 | linux-x64 | osx-arm64   (default: all three; any host fetches every platform)
#   --force  replace existing installs even when the pinned versions are already present
#
# Sources (no Node.js needed at runtime):
#   LuaLS 3.19.2: https://github.com/LuaLS/lua-language-server/releases/tag/3.19.2
#     win-x64    lua-language-server-3.19.2-win32-x64.zip
#     linux-x64  lua-language-server-3.19.2-linux-x64.tar.gz
#     osx-arm64  lua-language-server-3.19.2-darwin-arm64.tar.gz
#   TypeScript 7.0.2 native tsc (`tsc --lsp --stdio`): GitHub release typescript/v7.0.2 of
#     https://github.com/microsoft/typescript-go (the release exists with platform tarballs, so
#     the npm registry fallback is not needed)
#     win-x64    typescript-win32-x64.tgz
#     linux-x64  typescript-linux-x64.tgz
#     osx-arm64  typescript-darwin-arm64.tgz
#
# Integrity: as fetch-nvim.sh. Every archive is SHA-256 checked against tools/assets.lock, and an
#   unpinned asset is cross-checked against the release's digests before its first pin.
#
# Layout produced:
#   editor/<rid>/lsp/lua-language-server/{bin/lua-language-server[.exe], LICENSE, locale/, meta/, ...}
#   editor/<rid>/lsp/typescript/{lib/tsc[.exe], lib/*.d.ts, package.json, LICENSE, .ksa-version}
#   The TypeScript build finds its default lib files (lib/lib.*.d.ts) relative to the binary, so
#   they must stay next to it.
#
# Env: KSA_EDITOR_DIR overrides the editor root (default <repo>/editor), for test copies.
set -euo pipefail

LUALS_VERSION=3.19.2
TSGO_VERSION=7.0.2
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EDITOR_DIR="${KSA_EDITOR_DIR:-$REPO_ROOT/editor}"
LOCK="$REPO_ROOT/tools/assets.lock"
LUALS_RELEASE_API="https://api.github.com/repos/LuaLS/lua-language-server/releases/tags/$LUALS_VERSION"
LUALS_URL="https://github.com/LuaLS/lua-language-server/releases/download/$LUALS_VERSION"
TSGO_RELEASE_API="https://api.github.com/repos/microsoft/typescript-go/releases/tags/typescript/v$TSGO_VERSION"
TSGO_URL="https://github.com/microsoft/typescript-go/releases/download/typescript/v$TSGO_VERSION"

FORCE=0
RIDS=""
SIZES=""
WORK=""

usage() {
  cat <<'USAGE'
Usage: tools/fetch-lsp.sh [--force] [rid...]
  rid      win-x64 | linux-x64 | osx-arm64 (default: all three)
  --force  replace existing installs even if the pinned versions are present
USAGE
}

die()  { echo "error: $*" >&2; exit 1; }
warn() { echo "WARNING: $*" >&2; }
info() { echo "$*"; }
add_size() { SIZES="$SIZES$1"$'\n'; }

known_rid() {
  case "$1" in
    win-x64|linux-x64|osx-arm64) return 0 ;;
    *) return 1 ;;
  esac
}

host_rid() {
  case "$(uname -s)/$(uname -m)" in
    Darwin/arm64) echo osx-arm64 ;;
    Linux/x86_64) echo linux-x64 ;;
    *) die "cannot detect a supported host platform ($(uname -s)/$(uname -m)); pass an explicit rid: win-x64 linux-x64 osx-arm64" ;;
  esac
}

sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  else
    shasum -a 256 "$1" | awk '{print $1}'
  fi
}

# lock_lookup <asset>: prints the pinned SHA-256 for an asset file name (empty if none).
lock_lookup() {
  [ -f "$LOCK" ] || return 0
  awk -v a="$1" '$0 !~ /^[[:space:]]*#/ && NF >= 2 && $2 == a { print $1; exit }' "$LOCK"
}

# lock_pin <sha256> <asset>: appends a pin, creating tools/assets.lock with a header if needed.
lock_pin() {
  if [ ! -f "$LOCK" ]; then
    printf '%s\n' \
      "# tools/assets.lock: SHA-256 pins for release assets fetched by tools/fetch-nvim.* and tools/fetch-lsp.*" \
      "# Format: <sha256>  <asset file name>   (two spaces, as sha256sum prints it)" \
      "# An asset missing here is pinned on its first download, after cross-checking the release's own digests." \
      "# A version bump that keeps the same file name (nvim-win64.zip, typescript-*.tgz) needs the old line removed first." \
      > "$LOCK"
  fi
  printf '%s  %s\n' "$1" "$2" >> "$LOCK"
}

# installed_version <dir>: prints the contents of <dir>/.ksa-version (empty if absent).
installed_version() {
  local v=""
  if [ -f "$1/.ksa-version" ]; then
    read -r v < "$1/.ksa-version" || true
  fi
  printf '%s' "$v"
}

# find_root <extract-dir> <marker>: prints the directory holding <marker>, which is either the
# extract dir itself or its first top-level folder that contains it.
find_root() {
  local d
  if [ -e "$1/$2" ]; then
    printf '%s\n' "$1"
    return 0
  fi
  for d in "$1"/*/; do
    if [ -e "$d$2" ]; then
      printf '%s\n' "${d%/}"
      return 0
    fi
  done
  return 1
}

# upstream_check <release-api-url> <asset> <local-sha256> <work-dir>
# Cross-checks a first-time pin against what the release itself publishes. Dies on mismatch.
upstream_check() {
  local api=$1 asset=$2 local_sha=$3 work=$4 meta kind a b up_digest="" sumurl="" sumname="" published
  if ! curl -fsSL --retry 3 --retry-delay 2 --proto '=https' --tlsv1.2 -H 'Accept: application/vnd.github+json' -o "$work/release.json" "$api"; then
    warn "could not query $api; upstream cross-check skipped for $asset"
    return 0
  fi
  # Runs from / so no interpreter state comes from the working directory; the JSON is data only.
  meta="$(cd / && python3 -I -c 'import json, sys
asset, path = sys.argv[1], sys.argv[2]
with open(path, encoding="utf-8") as fh:
    assets = json.load(fh).get("assets", [])
for a in assets:
    name = a.get("name", "")
    digest = a.get("digest") or ""
    if name == asset and digest.startswith("sha256:"):
        print("digest\t" + digest[7:] + "\t")
    low = name.lower()
    if name != asset and (low.endswith((".sha256", ".sha256sum", ".sha256sums")) or low in ("shasum.txt", "shasums.txt", "sha256sums", "sha256sum.txt", "checksums.txt")):
        print("sumfile\t" + a.get("browser_download_url", "") + "\t" + name)
' "$asset" "$work/release.json")" || die "could not parse $work/release.json"
  while IFS=$'\t' read -r kind a b; do
    case "$kind" in
      digest)  up_digest=$a ;;
      sumfile) sumurl=$a; sumname=$b ;;
    esac
  done <<EOF
$meta
EOF
  if [ -n "$sumurl" ]; then
    curl -fsSL --retry 3 --retry-delay 2 --proto '=https' --tlsv1.2 -o "$work/sumfile" "$sumurl" || die "could not download $sumname"
    published="$(awk -v a="$asset" '{ f = $NF; sub(/^\*/, "", f); if (f == a) { print $1; exit } }' "$work/sumfile")"
    [ -n "$published" ] || die "$sumname does not list $asset"
    [ "$published" = "$local_sha" ] || die "$sumname lists $published for $asset but the download is $local_sha"
    info "    upstream $sumname: matches"
  fi
  if [ -n "$up_digest" ]; then
    [ "$up_digest" = "$local_sha" ] || die "GitHub API digest for $asset is $up_digest but the download is $local_sha"
    info "    upstream GitHub API digest for $asset: matches (sha256:$up_digest)"
  fi
  if [ -z "$sumurl" ] && [ -z "$up_digest" ]; then
    warn "release publishes no checksum for $asset (no checksum asset, no API digest); pinning the local hash only"
  fi
  return 0
}

# verify_asset <file> <asset> <release-api-url> <work-dir>
verify_asset() {
  local file=$1 asset=$2 api=$3 work=$4 actual expected
  actual="$(sha256_of "$file")"
  expected="$(lock_lookup "$asset")"
  if [ -n "$expected" ]; then
    if [ "$expected" != "$actual" ]; then
      die "SHA-256 mismatch for $asset: tools/assets.lock has $expected, download is $actual. If the version was bumped on purpose, remove the stale line from tools/assets.lock."
    fi
    info "    sha256 ok ($asset is pinned in tools/assets.lock)"
    return 0
  fi
  warn "no SHA-256 pinned for $asset; cross-checking upstream before pinning it"
  upstream_check "$api" "$asset" "$actual" "$work"
  lock_pin "$actual" "$asset"
  warn "pinned $asset = $actual in tools/assets.lock; review that line before committing it"
}

# fetch_verified <url> <asset> <release-api-url> <work-dir>: downloads and verifies <work-dir>/<asset>.
fetch_verified() {
  info "    downloading $1"
  curl -fsSL --retry 3 --retry-delay 2 --proto '=https' --tlsv1.2 -o "$4/$2" "$1" || die "download failed: $1"
  verify_asset "$4/$2" "$2" "$3" "$4"
}

mac_unquarantine() {
  if [ "$(uname -s)" = Darwin ]; then
    xattr -dr com.apple.quarantine "$1" 2>/dev/null || true
  fi
}

fetch_luals() {
  local rid=$1 asset="" binrel="" dest work archive root csz usz
  case "$rid" in
    win-x64)   asset="lua-language-server-$LUALS_VERSION-win32-x64.zip";       binrel=bin/lua-language-server.exe ;;
    linux-x64) asset="lua-language-server-$LUALS_VERSION-linux-x64.tar.gz";    binrel=bin/lua-language-server ;;
    osx-arm64) asset="lua-language-server-$LUALS_VERSION-darwin-arm64.tar.gz"; binrel=bin/lua-language-server ;;
  esac
  dest="$EDITOR_DIR/$rid/lsp/lua-language-server"

  if [ "$FORCE" -eq 0 ] && [ -f "$dest/$binrel" ] && [ "$(installed_version "$dest")" = "$LUALS_VERSION" ]; then
    info "lua-language-server $LUALS_VERSION already present for $rid"
    add_size "$(printf '%-10s luals unpacked %s (already present, not re-fetched)' "$rid" "$(du -sh "$dest" | cut -f1)")"
    return 0
  fi

  work="$WORK/$rid/luals"
  mkdir -p "$work/extract"
  archive="$work/$asset"
  fetch_verified "$LUALS_URL/$asset" "$asset" "$LUALS_RELEASE_API" "$work"

  # The archive has no top-level folder (bin/ sits at the root); find_root also accepts one if present.
  if [[ $asset == *.zip ]]; then
    unzip -q "$archive" -d "$work/extract"
  else
    tar -xzf "$archive" -C "$work/extract"
  fi
  root="$(find_root "$work/extract" "$binrel")" || die "$asset does not contain $binrel"
  [ -f "$root/LICENSE" ] || die "$asset has no LICENSE file"

  # Replace any previous install by moving it into the temp dir (the EXIT trap clears that).
  if [ -e "$dest" ]; then
    mkdir -p "$WORK/$rid/luals-replaced"
    mv "$dest" "$WORK/$rid/luals-replaced/previous"
  fi
  mkdir -p "$(dirname "$dest")"
  mv "$root" "$dest"

  if [ "$rid" != win-x64 ]; then
    chmod +x "$dest/$binrel"
  fi
  printf '%s\n' "$LUALS_VERSION" > "$dest/.ksa-version"
  mac_unquarantine "$dest"

  csz="$(du -sh "$archive" | cut -f1)"
  usz="$(du -sh "$dest" | cut -f1)"
  add_size "$(printf '%-10s luals compressed %-7s unpacked %s' "$rid" "$csz" "$usz")"
  info "[$rid] lua-language-server $LUALS_VERSION installed at $dest"
}

fetch_tsgo() {
  local rid=$1 plat="" binname="" asset="" dest work archive root stage csz usz
  case "$rid" in
    win-x64)   plat=win32-x64;     binname=tsc.exe ;;
    linux-x64) plat=linux-x64;     binname=tsc ;;
    osx-arm64) plat=darwin-arm64;  binname=tsc ;;
  esac
  asset="typescript-$plat.tgz"
  dest="$EDITOR_DIR/$rid/lsp/typescript"

  if [ "$FORCE" -eq 0 ] && [ -f "$dest/lib/$binname" ] && [ "$(installed_version "$dest")" = "$TSGO_VERSION" ]; then
    info "typescript (tsc) $TSGO_VERSION already present for $rid"
    add_size "$(printf '%-10s tsc   unpacked %s (already present, not re-fetched)' "$rid" "$(du -sh "$dest" | cut -f1)")"
    return 0
  fi

  work="$WORK/$rid/typescript"
  mkdir -p "$work/extract"
  archive="$work/$asset"
  fetch_verified "$TSGO_URL/$asset" "$asset" "$TSGO_RELEASE_API" "$work"
  tar -xzf "$archive" -C "$work/extract"
  root="$(find_root "$work/extract" "lib/$binname")" || die "$asset does not contain lib/$binname"

  # Stage only what the LSP needs, then move the staged folder into place.
  stage="$work/stage"
  mkdir -p "$stage/lib"
  cp -p "$root/lib/$binname" "$stage/lib/$binname"
  cp -p "$root"/lib/*.d.ts "$stage/lib/"
  cp -p "$root/package.json" "$stage/package.json"
  [ -f "$root/LICENSE" ] || die "$asset has no LICENSE file"
  cp -p "$root/LICENSE" "$stage/LICENSE"
  if [ "$rid" != win-x64 ] && [ ! -x "$stage/lib/$binname" ]; then
    die "$asset: lib/$binname is not executable after extraction"
  fi
  printf '%s\n' "$TSGO_VERSION" > "$stage/.ksa-version"

  if [ -e "$dest" ]; then
    mkdir -p "$WORK/$rid/typescript-replaced"
    mv "$dest" "$WORK/$rid/typescript-replaced/previous"
  fi
  mkdir -p "$(dirname "$dest")"
  mv "$stage" "$dest"
  mac_unquarantine "$dest"

  csz="$(du -sh "$archive" | cut -f1)"
  usz="$(du -sh "$dest" | cut -f1)"
  add_size "$(printf '%-10s tsc   compressed %-7s unpacked %s' "$rid" "$csz" "$usz")"
  info "[$rid] typescript (tsc) $TSGO_VERSION installed at $dest"
}

main() {
  local arg rid
  while [ $# -gt 0 ]; do
    arg=$1
    case "$arg" in
      --force)   FORCE=1 ;;
      -h|--help) usage; exit 0 ;;
      -*)        echo "error: unknown option '$arg'" >&2; usage >&2; exit 2 ;;
      *)
        known_rid "$arg" || { echo "error: unknown rid '$arg' (expected: win-x64 linux-x64 osx-arm64)" >&2; exit 2; }
        case " $RIDS " in
          *" $arg "*) ;;
          *) RIDS="$RIDS $arg" ;;
        esac
        ;;
    esac
    shift
  done

  if [ -z "$RIDS" ]; then
    RIDS=" win-x64 linux-x64 osx-arm64"
  fi

  WORK="$(mktemp -d)"
  trap 'rm -rf "$WORK"' EXIT

  for rid in $RIDS; do
    fetch_luals "$rid"
    fetch_tsgo "$rid"
  done

  echo
  echo "sizes (compressed = downloaded archive, unpacked = installed folder):"
  printf '%s' "$SIZES"
}

main "$@"
