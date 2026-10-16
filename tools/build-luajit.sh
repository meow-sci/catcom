#!/usr/bin/env bash
# Builds the LuaJIT commit pinned in tools/luajit.pin, with tools/host_shim.c compiled into
# the library, for the native runtimes under src/KsaScripting.Runtime.Lua/runtimes/<rid>/native/.
#
# Usage: tools/build-luajit.sh [--force] [rid...]
#   rids: osx-arm64, linux-x64. Default: the host rid. win-x64 is built by tools/build-luajit.ps1.
#   linux-x64 builds in an ubuntu:22.04 container (--platform linux/amd64) on any host except
#   Linux x86_64, where it builds natively. KSA_LUAJIT_USE_DOCKER=1 forces the container.
#   --force rebuilds even when the output already matches the pinned commit.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
CACHE_DIR="$SCRIPT_DIR/.cache"
SRC_DIR="$CACHE_DIR/luajit-src"
BIN_DIR="$CACHE_DIR/bin"
RUNTIMES_DIR="$REPO_ROOT/src/KsaScripting.Runtime.Lua/runtimes"
DOCKER_IMAGE="ubuntu:22.04"
HOST_SYS="$(uname -s)"
HOST_MACH="$(uname -m)"
XCFLAGS_LUAJIT="-DLUAJIT_DISABLE_FFI -DLUAJIT_ENABLE_CHECKHOOK"

FORCE=0
RIDS=()
CHECKOUT_DONE=0
COMMIT_TS=""
OUT_LIB=""

die() { echo "build-luajit: error: $*" >&2; exit 1; }
log() { echo "build-luajit: $*"; }

usage() {
  sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'
}

pin_value() {
  local key="$1" value
  value="$(sed -n "s/^${key}=//p" "$SCRIPT_DIR/luajit.pin" | tr -d '\r' | sed -n '1p')"
  [ -n "$value" ] || die "tools/luajit.pin has no '$key=' line"
  printf '%s' "$value"
}

REPO_URL="$(pin_value repo)"
COMMIT="$(pin_value commit)"
case "$COMMIT" in
  *[!0-9a-f]*) die "tools/luajit.pin commit is not a lowercase hex SHA: $COMMIT" ;;
esac
[ "${#COMMIT}" -eq 40 ] || die "tools/luajit.pin commit must be a full 40-char SHA: $COMMIT"

for arg in "$@"; do
  case "$arg" in
    --force) FORCE=1 ;;
    -h|--help) usage; exit 0 ;;
    win-x64)
      echo "build-luajit: win-x64 is built on Windows by tools/build-luajit.ps1 (not supported here)" >&2
      exit 2 ;;
    osx-arm64|linux-x64) RIDS+=("$arg") ;;
    *) echo "build-luajit: unknown argument '$arg' (rids: osx-arm64, linux-x64)" >&2; exit 2 ;;
  esac
done

if [ "${#RIDS[@]}" -eq 0 ]; then
  case "$HOST_SYS/$HOST_MACH" in
    Darwin/arm64) RIDS=(osx-arm64) ;;
    Linux/x86_64) RIDS=(linux-x64) ;;
    *) die "no default rid for host $HOST_SYS/$HOST_MACH; pass osx-arm64 or linux-x64" ;;
  esac
fi

# Source checkout: one clone, detached checkout of the pinned commit, cleaned on every build.
ensure_checkout() {
  [ "$CHECKOUT_DONE" -eq 0 ] || return 0
  mkdir -p "$CACHE_DIR"
  printf '*\n' > "$CACHE_DIR/.gitignore"
  if [ ! -d "$SRC_DIR/.git" ]; then
    log "cloning $REPO_URL into $SRC_DIR"
    rm -rf "$SRC_DIR"
    git clone --quiet "$REPO_URL" "$SRC_DIR"
  fi
  if ! git -C "$SRC_DIR" cat-file -e "${COMMIT}^{commit}" 2>/dev/null; then
    log "fetching $COMMIT"
    git -C "$SRC_DIR" fetch --quiet origin "$COMMIT" 2>/dev/null || git -C "$SRC_DIR" fetch --quiet origin
  fi
  git -C "$SRC_DIR" -c advice.detachedHead=false checkout --quiet --force "$COMMIT"
  git -C "$SRC_DIR" clean -fdxq
  [ "$(git -C "$SRC_DIR" rev-parse HEAD)" = "$COMMIT" ] || die "checkout is not at $COMMIT"
  COMMIT_TS="$(git -C "$SRC_DIR" show -s --format=%ct "$COMMIT")"
  CHECKOUT_DONE=1
}

# Fresh copy of the checkout (no .git), so osx and linux builds never share objects.
# LuaJIT takes its version from .relver when there is no .git, so the commit time goes there.
prepare_build() { # <rid>; prints the build dir
  local build="$CACHE_DIR/build-$1"
  rm -rf "$build"
  mkdir -p "$build"
  tar -C "$SRC_DIR" --exclude=.git -cf - . | tar -C "$build" -xf -
  printf '%s\n' "$COMMIT_TS" > "$build/.relver"
  cp "$SCRIPT_DIR/host_shim.c" "$build/src/host_shim.c"
  printf '\n#include "host_shim.c"\n' >> "$build/src/ljamalg.c"
  printf '%s' "$build"
}

# has_line <text> <line>: true when <line> is one of the newline-separated lines of <text>.
has_line() {
  case $'\n'"$1"$'\n' in
    *$'\n'"$2"$'\n'*) return 0 ;;
  esac
  return 1
}

check_exports() { # <rid> <symbol list> <prefix>
  local rid="$1" syms="$2" prefix="$3" s
  for s in host_timeout_hook host_raise host_traceback luaL_newstate lua_sethook luaJIT_setmode; do
    has_line "$syms" "${prefix}${s}" || die "$rid: exported symbol ${prefix}${s} is missing"
  done
  if has_line "$syms" "${prefix}luaopen_ffi"; then
    die "$rid: ${prefix}luaopen_ffi is exported, so FFI was not disabled"
  fi
  log "$rid: exports ok (host_timeout_hook, host_raise, host_traceback, luaL_newstate, lua_sethook, luaJIT_setmode; no luaopen_ffi)"
}

# Copies the CLI into tools/.cache/bin/<rid>/ (verification uses that copy, not the build tree).
stage_cli() { # <rid> <cli path>
  mkdir -p "$BIN_DIR/$1"
  cp "$2" "$BIN_DIR/$1/luajit"
}

verify_osx() { # <build dir>
  local build="$1" lib="$1/src/libluajit.so" syms file_out id deps d cli_deps ver jit ffi
  [ -f "$lib" ] || die "osx-arm64: $lib was not produced"

  syms="$(nm -gU "$lib" | awk '{print $NF}')"
  check_exports osx-arm64 "$syms" "_"

  file_out="$(file -b "$lib")"
  case "$file_out" in
    *"Mach-O 64-bit dynamically linked shared library arm64"*) ;;
    *) die "osx-arm64: unexpected file type: $file_out" ;;
  esac
  log "osx-arm64: file: $file_out"

  # otool -L lists the dylib's own install name (otool -D) as its first entry; skip it.
  id="$(otool -D "$lib" | tail -n +2 | awk '{print $1}')"
  deps="$(otool -L "$lib" | tail -n +2 | awk '{print $1}' | grep -vxF "$id" || true)"
  while IFS= read -r d; do
    [ -n "$d" ] || continue
    case "$d" in
      /usr/lib/libSystem*) ;;
      *) die "osx-arm64: unexpected dependency $d" ;;
    esac
  done <<< "$deps"
  log "osx-arm64: dependencies: $(echo "$deps" | tr '\n' ' ')"
  log "osx-arm64: minos: $(otool -l "$lib" | awk '/LC_BUILD_VERSION/{f=1} f && /minos/{print $2; exit}')"
  codesign --verify "$lib" 2>/dev/null || die "osx-arm64: library signature does not verify"

  stage_cli osx-arm64 "$build/src/luajit"
  cli_deps="$(otool -L "$BIN_DIR/osx-arm64/luajit" | tail -n +2 | awk '{print $1}')"
  log "osx-arm64: cli dependencies: $(echo "$cli_deps" | tr '\n' ' ')"

  ver="$("$BIN_DIR/osx-arm64/luajit" -v 2>&1)" || die "osx-arm64: luajit -v failed"
  case "$ver" in
    "LuaJIT 2.1.$COMMIT_TS "*) ;;
    *) die "osx-arm64: unexpected version string: $ver" ;;
  esac
  jit="$("$BIN_DIR/osx-arm64/luajit" -e 'print(jit.status())' 2>&1)" || die "osx-arm64: jit.status() failed: $jit"
  case "$jit" in true*) ;; *) die "osx-arm64: jit.status() did not start with true: $jit" ;; esac
  ffi="$("$BIN_DIR/osx-arm64/luajit" -e 'print(pcall(require, "ffi"))' 2>&1)" || die "osx-arm64: ffi probe failed: $ffi"
  case "$ffi" in false*) ;; *) die "osx-arm64: require(\"ffi\") succeeded: $ffi" ;; esac
  log "osx-arm64: $ver"
  log "osx-arm64: jit.status: $jit"
  log "osx-arm64: pcall(require, \"ffi\"): $ffi"
  OUT_LIB="$lib"
}

# Written into each build dir and run there, either natively or inside the container. It builds,
# then records probe files that verify_linux reads on the host.
linux_inner_script() {
  cat <<'__INNER__'
set -euo pipefail
build="$1"
if [ -n "${HOST_UID:-}" ]; then
  trap 'chown -R "$HOST_UID:$HOST_GID" "$build"' EXIT
fi
cd "$build"
make amalg -j"$(nproc)" XCFLAGS="-DLUAJIT_DISABLE_FFI -DLUAJIT_ENABLE_CHECKHOOK"
cd src
nm -D --defined-only libluajit.so | awk '{print $NF}' > ksa-probe.dynsyms
ldd libluajit.so > ksa-probe.ldd
ldd luajit > ksa-probe.cli-ldd
objdump -T libluajit.so | grep -o 'GLIBC_[0-9.]*' | sort -uV > ksa-probe.glibc || true
./luajit -v > ksa-probe.version 2>&1
./luajit -e 'print(jit.status())' > ksa-probe.jit 2>&1
./luajit -e 'print(pcall(require, "ffi"))' > ksa-probe.ffi 2>&1
__INNER__
}

verify_linux() { # <build dir>
  local build="$1" lib="$1/src/libluajit.so" file_out ldd_out d base ver jit ffi floor f
  [ -f "$lib" ] || die "linux-x64: $lib was not produced"
  for f in ksa-probe.dynsyms ksa-probe.ldd ksa-probe.version ksa-probe.jit ksa-probe.ffi; do
    [ -f "$build/src/$f" ] || die "linux-x64: probe file $f is missing"
  done

  check_exports linux-x64 "$(cat "$build/src/ksa-probe.dynsyms")" ""

  file_out="$(file -b "$lib")"
  case "$file_out" in
    *"ELF 64-bit LSB shared object, x86-64"*) ;;
    *) die "linux-x64: unexpected file type: $file_out" ;;
  esac
  log "linux-x64: file: $file_out"

  ldd_out="$(awk '{print $1}' "$build/src/ksa-probe.ldd")"
  while IFS= read -r d; do
    [ -n "$d" ] || continue
    base="$(basename "$d")"
    case "$base" in
      linux-vdso.so.1|ld-linux-x86-64.so.2|libc.so.6|libm.so.6|libdl.so.2|libpthread.so.0|libgcc_s.so.1) ;;
      *) die "linux-x64: unexpected dependency $d" ;;
    esac
  done <<< "$ldd_out"
  log "linux-x64: dependencies: $(echo "$ldd_out" | tr '\n' ' ')"
  floor="$(tail -n 1 "$build/src/ksa-probe.glibc" 2>/dev/null || true)"
  log "linux-x64: highest GLIBC symbol version required: ${floor:-unknown}"

  stage_cli linux-x64 "$build/src/luajit"

  ver="$(cat "$build/src/ksa-probe.version")"
  case "$ver" in
    "LuaJIT 2.1.$COMMIT_TS "*) ;;
    *) die "linux-x64: unexpected version string: $ver" ;;
  esac
  jit="$(cat "$build/src/ksa-probe.jit")"
  case "$jit" in true*) ;; *) die "linux-x64: jit.status() did not start with true: $jit" ;; esac
  ffi="$(cat "$build/src/ksa-probe.ffi")"
  case "$ffi" in false*) ;; *) die "linux-x64: require(\"ffi\") succeeded: $ffi" ;; esac
  log "linux-x64: $ver"
  log "linux-x64: jit.status: $jit"
  log "linux-x64: pcall(require, \"ffi\"): $ffi"
  OUT_LIB="$lib"
}

build_linux() { # <build dir>
  local build="$1" name
  name="$(basename "$build")"
  linux_inner_script > "$build/ksa-inner.sh"
  if [ "${KSA_LUAJIT_USE_DOCKER:-0}" = 1 ] || [ "$HOST_SYS/$HOST_MACH" != "Linux/x86_64" ]; then
    command -v docker >/dev/null 2>&1 || die "docker is required to build linux-x64 on $HOST_SYS/$HOST_MACH"
    docker info >/dev/null 2>&1 || die "docker daemon is not reachable (start Docker Desktop)"
    log "linux-x64: building in $DOCKER_IMAGE (--platform linux/amd64); first run downloads the image and apt packages"
    docker run --rm --platform linux/amd64 \
      -e HOST_UID="$(id -u)" -e HOST_GID="$(id -g)" \
      -v "$CACHE_DIR:/cache" "$DOCKER_IMAGE" \
      bash -c 'set -e; apt-get update -qq; apt-get install -y -qq build-essential >/dev/null; exec bash "$1/ksa-inner.sh" "$1"' \
      ksa-build "/cache/$name"
  else
    log "linux-x64: building natively on $HOST_SYS/$HOST_MACH (this host's glibc sets the floor)"
    bash "$build/ksa-inner.sh" "$build"
  fi
}

build_rid() {
  local rid="$1" native lib_name build
  case "$rid" in
    osx-arm64) lib_name="libluajit-5.1.2.dylib" ;;
    linux-x64) lib_name="libluajit-5.1.so.2" ;;
  esac
  native="$RUNTIMES_DIR/$rid/native"

  if [ "$FORCE" -eq 0 ] && [ -f "$native/$lib_name" ] && [ -f "$native/.luajit-commit" ] \
     && [ "$(cat "$native/.luajit-commit")" = "$COMMIT" ]; then
    log "$rid: up to date ($COMMIT)"
    return 0
  fi

  ensure_checkout
  build="$(prepare_build "$rid")"
  rm -f "$native/.luajit-commit"

  case "$rid" in
    osx-arm64)
      [ "$HOST_SYS/$HOST_MACH" = "Darwin/arm64" ] || die "osx-arm64 must be built on a Darwin arm64 host (this is $HOST_SYS/$HOST_MACH)"
      export MACOSX_DEPLOYMENT_TARGET=11.0
      log "osx-arm64: make amalg in $build"
      make -C "$build" amalg -j"$(sysctl -n hw.ncpu)" XCFLAGS="$XCFLAGS_LUAJIT"
      verify_osx "$build"
      ;;
    linux-x64)
      build_linux "$build"
      verify_linux "$build"
      ;;
  esac

  mkdir -p "$native"
  # Copy then rename: replacing the file in place would corrupt a library that a running
  # test host has mapped (macOS kills processes whose mapped code pages change).
  cp "$OUT_LIB" "$native/$lib_name.tmp.$$"
  mv -f "$native/$lib_name.tmp.$$" "$native/$lib_name"
  printf '%s\n' "$COMMIT" > "$native/.luajit-commit"
  log "$rid: wrote $native/$lib_name ($(wc -c < "$native/$lib_name" | tr -d ' ') bytes)"
  log "$rid: cli copy $BIN_DIR/$rid/luajit ($(wc -c < "$BIN_DIR/$rid/luajit" | tr -d ' ') bytes)"
}

# runtimes/.gitignore: the natives are build outputs and are never committed.
mkdir -p "$RUNTIMES_DIR"
printf '*\n!.gitignore\n' > "$RUNTIMES_DIR/.gitignore"

for rid in "${RIDS[@]}"; do
  build_rid "$rid"
done
