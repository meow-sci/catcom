#!/usr/bin/env bash
# Fetch typescript.js (+ licences) and the sucrase bundle into
# src/KsaScripting.Runtime.V8/assets/. npm tarballs are verified against the
# registry's SHA-512 integrity value before anything is extracted. Downloaded
# content is only copied and read, never executed.
#
# Usage: tools/fetch-ts.sh [--force]
# Env:   KSA_V8_ASSETS_DIR  overrides the destination (for testing).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ASSETS="${KSA_V8_ASSETS_DIR:-$ROOT/src/KsaScripting.Runtime.V8/assets}"
REGISTRY="https://registry.npmjs.org"
SUCRASE_VERSION="3.35.1"

die() { echo "fetch-ts: $*" >&2; exit 1; }

force=0
for arg in "$@"; do
  case "$arg" in
    --force) force=1 ;;
    -h|--help) echo "usage: tools/fetch-ts.sh [--force]"; exit 0 ;;
    *) die "unknown argument: $arg (usage: tools/fetch-ts.sh [--force])" ;;
  esac
done

TS_VERSION="$(tr -d '[:space:]' < "$ROOT/tools/typescript.pin")"
case "$TS_VERSION" in
  [0-9]*.[0-9]*.[0-9]*) ;;
  *) die "tools/typescript.pin must hold a plain x.y.z version, got '$TS_VERSION'" ;;
esac

mkdir -p "$ASSETS"
# Everything in this folder is generated; only the .gitignore itself is tracked.
printf '*\n!.gitignore\n' > "$ASSETS/.gitignore"

if [ "$force" -eq 0 ] && [ -f "$ASSETS/typescript.js" ] && [ -f "$ASSETS/.typescript-version" ] \
   && [ "$(tr -d '[:space:]' < "$ASSETS/.typescript-version")" = "$TS_VERSION" ]; then
  echo "fetch-ts: up to date (typescript $TS_VERSION in $ASSETS)"
  exit 0
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# fetch_verified <package> <version>
# Reads registry metadata, downloads the tarball into a fresh directory under
# $WORK, checks SHA-512 against dist.integrity, and extracts it. On success the
# package root is left in FETCHED_PKG_DIR.
fetch_verified() {
  local pkg="$1" ver="$2" dir info tarball integrity actual
  dir="$(mktemp -d "$WORK/$pkg.XXXXXX")"
  curl -fsSL --proto '=https' --tlsv1.2 --retry 3 "$REGISTRY/$pkg/$ver" -o "$dir/meta.json"
  info="$(python3 -I -c '
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
print(d["dist"]["tarball"] + "\t" + d["dist"]["integrity"])
' "$dir/meta.json")" || die "could not read registry metadata for $pkg@$ver"
  IFS=$'\t' read -r tarball integrity <<< "$info"
  case "$tarball" in
    "$REGISTRY"/*) ;;
    *) die "$pkg@$ver: unexpected tarball URL $tarball" ;;
  esac
  case "$integrity" in
    sha512-*) ;;
    *) die "$pkg@$ver: unsupported integrity value (expected sha512-...)" ;;
  esac
  curl -fsSL --proto '=https' --tlsv1.2 --retry 3 "$tarball" -o "$dir/package.tgz"
  actual="sha512-$(openssl dgst -sha512 -binary "$dir/package.tgz" | openssl base64 -A)"
  if [ "$actual" != "$integrity" ]; then
    echo "fetch-ts: SHA-512 mismatch for $pkg@$ver" >&2
    echo "  registry: $integrity" >&2
    echo "  download: $actual" >&2
    exit 1
  fi
  mkdir -p "$dir/x"
  tar -xzf "$dir/package.tgz" -C "$dir/x"
  FETCHED_PKG_DIR="$dir/x/package"
}

fetch_verified typescript "$TS_VERSION"
TS_PKG="$FETCHED_PKG_DIR"
[ -f "$TS_PKG/lib/typescript.js" ] || die "typescript@$TS_VERSION has no lib/typescript.js"
[ -f "$TS_PKG/LICENSE.txt" ] || die "typescript@$TS_VERSION has no LICENSE.txt"
cp "$TS_PKG/lib/typescript.js" "$ASSETS/typescript.js"
cp "$TS_PKG/LICENSE.txt" "$ASSETS/typescript.LICENSE.txt"
if [ -f "$TS_PKG/ThirdPartyNoticeText.txt" ]; then
  cp "$TS_PKG/ThirdPartyNoticeText.txt" "$ASSETS/typescript.ThirdPartyNoticeText.txt"
fi

[ -f "$ROOT/spikes/assets/sucrase.bundle.js" ] || die "missing spikes/assets/sucrase.bundle.js"
cp "$ROOT/spikes/assets/sucrase.bundle.js" "$ASSETS/sucrase.bundle.js"

fetch_verified sucrase "$SUCRASE_VERSION"
[ -f "$FETCHED_PKG_DIR/LICENSE" ] || die "sucrase@$SUCRASE_VERSION has no LICENSE"
cp "$FETCHED_PKG_DIR/LICENSE" "$ASSETS/sucrase.LICENSE"

# Written last: a run that dies part-way leaves no marker, so the next run redoes everything.
printf '%s\n' "$TS_VERSION" > "$ASSETS/.typescript-version"

echo "fetch-ts: wrote typescript $TS_VERSION and sucrase $SUCRASE_VERSION to $ASSETS"
for f in typescript.js typescript.LICENSE.txt typescript.ThirdPartyNoticeText.txt \
         sucrase.bundle.js sucrase.LICENSE .typescript-version .gitignore; do
  if [ -f "$ASSETS/$f" ]; then
    printf '  %10s bytes  %s\n' "$(wc -c < "$ASSETS/$f" | tr -d ' ')" "$ASSETS/$f"
  fi
done
