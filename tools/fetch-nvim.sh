#!/usr/bin/env bash
# fetch-nvim.sh: download the pinned Neovim release for each platform into editor/<rid>/nvim/.
#
# Usage: tools/fetch-nvim.sh [--force] [rid...]
#   rid      win-x64 | linux-x64 | osx-arm64   (default: all three; any host fetches every platform)
#   --force  replace an existing install even when the pinned version is already present
#
# Source: official archives of https://github.com/neovim/neovim/releases/tag/v0.12.6
#   win-x64    nvim-win64.zip
#   linux-x64  nvim-linux-x86_64.tar.gz
#   osx-arm64  nvim-macos-arm64.tar.gz
#
# Integrity: every archive is SHA-256 checked against tools/assets.lock. An asset with no lock
#   entry is pinned on its first download, but only after cross-checking the release's own
#   digests: a published *.sha256 / shasum.txt style asset if the release has one, and the
#   GitHub API `digest` field of the asset. Neovim 0.12.6 publishes no checksum file, so the
#   API digest is the upstream reference.
#
# Layout produced: editor/<rid>/nvim/{bin/nvim[.exe], lib/nvim/parser/, share/nvim/runtime/,
#   LICENSE.txt, .ksa-version}. win-x64 also drops bin/xxd.exe and bin/win32yank.exe.
#
# Env: KSA_EDITOR_DIR overrides the editor root (default <repo>/editor), for test copies.
set -euo pipefail

NVIM_VERSION=0.12.6
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EDITOR_DIR="${KSA_EDITOR_DIR:-$REPO_ROOT/editor}"
LOCK="$REPO_ROOT/tools/assets.lock"
RELEASE_API="https://api.github.com/repos/neovim/neovim/releases/tags/v$NVIM_VERSION"
RELEASE_URL="https://github.com/neovim/neovim/releases/download/v$NVIM_VERSION"
LICENSE_URL="https://raw.githubusercontent.com/neovim/neovim/v$NVIM_VERSION/LICENSE.txt"

FORCE=0
RIDS=""
SIZES=""
WORK=""

usage() {
  cat <<'USAGE'
Usage: tools/fetch-nvim.sh [--force] [rid...]
  rid      win-x64 | linux-x64 | osx-arm64 (default: all three)
  --force  replace an existing install even if the pinned version is present
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

fetch_nvim() {
  local rid=$1 asset="" binrel="" dest work archive root listing lic csz usz
  case "$rid" in
    win-x64)   asset=nvim-win64.zip;           binrel=bin/nvim.exe ;;
    linux-x64) asset=nvim-linux-x86_64.tar.gz; binrel=bin/nvim ;;
    osx-arm64) asset=nvim-macos-arm64.tar.gz;  binrel=bin/nvim ;;
  esac
  dest="$EDITOR_DIR/$rid/nvim"

  if [ "$FORCE" -eq 0 ] && [ -f "$dest/$binrel" ] && [ "$(installed_version "$dest")" = "$NVIM_VERSION" ]; then
    info "nvim $NVIM_VERSION already present for $rid"
    add_size "$(printf '%-10s nvim  unpacked %s (already present, not re-fetched)' "$rid" "$(du -sh "$dest" | cut -f1)")"
    return 0
  fi

  work="$WORK/$rid/nvim"
  mkdir -p "$work/extract"
  archive="$work/$asset"
  info "[$rid] downloading $RELEASE_URL/$asset"
  curl -fsSL --retry 3 --retry-delay 2 --proto '=https' --tlsv1.2 -o "$archive" "$RELEASE_URL/$asset" \
    || die "download failed: $RELEASE_URL/$asset"
  verify_asset "$archive" "$asset" "$RELEASE_API" "$work"

  if [ "$rid" = win-x64 ]; then
    listing="$(unzip -Z1 "$archive")"
    if ! grep -qi 'vcruntime' <<<"$listing"; then
      warn "$asset contains no vcruntime*.dll: nvim.exe imports VCRUNTIME140.dll, so machines without the VC++ redistributable need vc_redist.x64.exe or an app-local copy"
    fi
  fi

  if [[ $asset == *.zip ]]; then
    unzip -q "$archive" -d "$work/extract"
  else
    tar -xzf "$archive" -C "$work/extract"
  fi
  root="$(find_root "$work/extract" "$binrel")" || die "$asset does not contain $binrel"

  # Replace any previous install by moving it into the temp dir (the EXIT trap clears that),
  # so a failed fetch above never leaves the editor folder half-written.
  if [ -e "$dest" ]; then
    mkdir -p "$WORK/$rid/replaced"
    mv "$dest" "$WORK/$rid/replaced/previous"
  fi
  mkdir -p "$(dirname "$dest")"
  mv "$root" "$dest"

  if [ "$rid" = win-x64 ]; then
    rm -f "$dest/bin/xxd.exe" "$dest/bin/win32yank.exe"
  else
    chmod +x "$dest/$binrel"
  fi

  if [ ! -f "$dest/LICENSE.txt" ]; then
    # Top level only: nested LICENSE.txt files (e.g. share/nvim/runtime/pack/dist/opt/netrw) belong to plugins.
    lic="$(find "$dest" -maxdepth 1 -type f -iname 'LICENSE*' | sed -n '1p')"
    if [ -n "$lic" ]; then
      cp "$lic" "$dest/LICENSE.txt"
    else
      info "    archive has no top-level LICENSE; fetching $LICENSE_URL"
      curl -fsSL --retry 3 --retry-delay 2 --proto '=https' --tlsv1.2 -o "$dest/LICENSE.txt" "$LICENSE_URL" \
        || die "LICENSE download failed: $LICENSE_URL"
    fi
  fi

  printf '%s\n' "$NVIM_VERSION" > "$dest/.ksa-version"
  if [ "$(uname -s)" = Darwin ]; then
    xattr -dr com.apple.quarantine "$dest" 2>/dev/null || true
  fi

  csz="$(du -sh "$archive" | cut -f1)"
  usz="$(du -sh "$dest" | cut -f1)"
  add_size "$(printf '%-10s nvim  compressed %-7s unpacked %s' "$rid" "$csz" "$usz")"
  info "[$rid] nvim $NVIM_VERSION installed at $dest"
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
    fetch_nvim "$rid"
  done

  echo
  echo "sizes (compressed = downloaded archive, unpacked = installed folder):"
  printf '%s' "$SIZES"
}

main "$@"
