#!/usr/bin/env bash
# build-natives.sh: cross-compiles every native library ksa-scripting ships, for every platform,
# on ONE host (Linux or macOS). No per-platform build machines: the pinned zig toolchain
# (tools/zig.pin) targets win-x64, linux-x64 and osx-arm64 from anywhere. CI runs this on a single
# Linux job and publishes the result as a GitHub release that tools/fetch-natives.* downloads.
#
#   LuaJIT (tools/luajit.pin, tools/host_shim.c compiled in, FFI disabled, CHECKHOOK on)
#       -> lua51.dll | libluajit-5.1.so.2 | libluajit-5.1.2.dylib  (+ the luajit CLI, for checks)
#   tree-sitter-typescript parser (tools/ts-parser.pin)
#       -> typescript.dll | typescript.so | typescript.dylib
#
# Usage: tools/build-natives.sh [--force] [--no-runtime-checks] [rid...]
#   rid                  win-x64 | linux-x64 | osx-arm64   (default: all three)
#   --force              rebuild even when the output is up to date for the current pins
#   --no-runtime-checks  skip running the built luajit CLI (it runs natively, in docker for
#                        linux-x64 on other hosts, and under wine for win-x64 when available)
#
# Output: tools/.cache/natives-out/natives-<rid>.tar.gz, SHA256SUMS and NATIVES.txt. Install them
# into the repo with `tools/fetch-natives.sh --from tools/.cache/natives-out` (tools/fetch-all.sh
# --natives-from does the same).
#
# Needs: bash, make, tar (with xz for the zig tarball), curl, python3, git (for the LuaJIT checkout).
# No C compiler: zig is both the host and the cross compiler. Env: KSA_ZIG=/path/to/zig uses that
# zig instead of the pinned download (for experiments only; the pin is what CI uses).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
CACHE="$SCRIPT_DIR/.cache"
OUT="$CACHE/natives-out"
LUAJIT_SRC="$CACHE/luajit-src"
VERIFY="$SCRIPT_DIR/natives-verify.py"
XCFLAGS_LUAJIT="-DLUAJIT_DISABLE_FFI -DLUAJIT_ENABLE_CHECKHOOK"
LUAJIT_EXPORTS="host_timeout_hook host_raise host_traceback luaL_newstate lua_sethook luaJIT_setmode"
ALL_RIDS="win-x64 linux-x64 osx-arm64"
HOST_SYS="$(uname -s)"
HOST_MACH="$(uname -m)"

FORCE=0
RUNTIME_CHECKS=1
RIDS=""
ZIG=""
LUAJIT_COMMIT=""
LUAJIT_TS=""
TS_VERSION=""
TS_SRC=""
STAMP=""

die()  { echo "build-natives: error: $*" >&2; exit 1; }
log()  { echo "build-natives: $*"; }
warn() { echo "build-natives: WARNING: $*" >&2; }

usage() { sed -n '2,22p' "$0" | sed 's/^# \{0,1\}//'; }

# pin_value <file> <key>: the value of a `key=value` line (first match), trimmed of CR.
pin_value() {
  local v
  v="$(sed -n "s/^$2=//p" "$SCRIPT_DIR/$1" | tr -d '\r' | sed -n '1p')"
  [ -n "$v" ] || die "tools/$1 has no '$2=' line"
  printf '%s' "$v"
}

sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'
  else shasum -a 256 "$1" | awk '{print $1}'; fi
}

ncpu() {
  if command -v nproc >/dev/null 2>&1; then nproc; else sysctl -n hw.ncpu 2>/dev/null || echo 4; fi
}

fetch() { # <url> <dest>
  curl -fsSL --retry 3 --retry-delay 2 --proto '=https' --tlsv1.2 -o "$2" "$1" || die "download failed: $1"
}

host_key() {
  case "$HOST_SYS/$HOST_MACH" in
    Linux/x86_64)  echo x86_64-linux ;;
    Linux/aarch64) echo aarch64-linux ;;
    Darwin/arm64)  echo aarch64-macos ;;
    Darwin/x86_64) echo x86_64-macos ;;
    *) die "unsupported host $HOST_SYS/$HOST_MACH (Linux x86_64/aarch64 or macOS arm64/x86_64); Windows hosts download natives with tools/fetch-all.ps1" ;;
  esac
}

# ---------------------------------------------------------------- toolchain

ensure_zig() {
  if [ -n "${KSA_ZIG:-}" ]; then
    [ -x "$KSA_ZIG" ] || die "KSA_ZIG=$KSA_ZIG is not executable"
    ZIG="$KSA_ZIG"
    warn "using KSA_ZIG=$ZIG ($("$ZIG" version)) instead of the pinned zig $(pin_value zig.pin version)"
    return 0
  fi
  local version key entry url sha dir tarball actual tmp
  version="$(pin_value zig.pin version)"
  key="$(host_key)"
  entry="$(pin_value zig.pin "$key")"
  url="${entry%% *}"
  sha="${entry##* }"
  dir="$CACHE/zig/zig-$key-$version"
  ZIG="$dir/zig"
  if [ -x "$ZIG" ] && [ "$("$ZIG" version 2>/dev/null)" = "$version" ]; then
    log "zig $version ($key) at $ZIG"
    return 0
  fi
  mkdir -p "$CACHE/zig"
  tarball="$CACHE/zig/$(basename "$url")"
  if [ ! -f "$tarball" ] || [ "$(sha256_of "$tarball")" != "$sha" ]; then
    log "downloading zig $version for $key: $url"
    fetch "$url" "$tarball.part"
    actual="$(sha256_of "$tarball.part")"
    [ "$actual" = "$sha" ] || { rm -f "$tarball.part"; die "zig tarball SHA-256 mismatch: expected $sha, got $actual"; }
    mv -f "$tarball.part" "$tarball"
  fi
  log "sha256 ok ($(basename "$tarball"))"
  tmp="$(mktemp -d "$CACHE/zig/extract.XXXXXX")"
  tar -xf "$tarball" -C "$tmp"   # xz; tar detects the format (needs xz-utils on Linux)
  rm -rf "$dir"
  mv "$tmp"/zig-* "$dir"
  rmdir "$tmp" 2>/dev/null || true
  [ -x "$ZIG" ] || die "zig tarball did not contain a zig executable"
  [ "$("$ZIG" version)" = "$version" ] || die "extracted zig reports $("$ZIG" version), expected $version"
  log "zig $version ($key) installed at $ZIG"
}

# ---------------------------------------------------------------- sources

ensure_luajit_src() {
  local repo
  repo="$(pin_value luajit.pin repo)"
  LUAJIT_COMMIT="$(pin_value luajit.pin commit)"
  case "$LUAJIT_COMMIT" in *[!0-9a-f]*) die "tools/luajit.pin commit is not lowercase hex: $LUAJIT_COMMIT" ;; esac
  [ "${#LUAJIT_COMMIT}" -eq 40 ] || die "tools/luajit.pin commit must be a full 40-char SHA"
  if [ ! -d "$LUAJIT_SRC/.git" ]; then
    log "cloning $repo into $LUAJIT_SRC"
    rm -rf "$LUAJIT_SRC"
    git clone --quiet "$repo" "$LUAJIT_SRC"
  fi
  if ! git -C "$LUAJIT_SRC" cat-file -e "${LUAJIT_COMMIT}^{commit}" 2>/dev/null; then
    log "fetching LuaJIT $LUAJIT_COMMIT"
    git -C "$LUAJIT_SRC" fetch --quiet origin "$LUAJIT_COMMIT" 2>/dev/null || git -C "$LUAJIT_SRC" fetch --quiet origin
  fi
  git -C "$LUAJIT_SRC" -c advice.detachedHead=false checkout --quiet --force "$LUAJIT_COMMIT"
  git -C "$LUAJIT_SRC" clean -fdxq
  [ "$(git -C "$LUAJIT_SRC" rev-parse HEAD)" = "$LUAJIT_COMMIT" ] || die "LuaJIT checkout is not at $LUAJIT_COMMIT"
  LUAJIT_TS="$(git -C "$LUAJIT_SRC" show -s --format=%ct "$LUAJIT_COMMIT")"
  log "LuaJIT $LUAJIT_COMMIT (relver $LUAJIT_TS)"
}

ensure_ts_src() {
  local url sha archive tmp
  TS_VERSION="$(pin_value ts-parser.pin version)"
  url="$(pin_value ts-parser.pin url)"
  sha="$(pin_value ts-parser.pin sha256)"
  archive="$CACHE/tree-sitter-typescript-$TS_VERSION.tar.xz"
  if [ ! -f "$archive" ] || [ "$(sha256_of "$archive")" != "$sha" ]; then
    log "downloading tree-sitter-typescript $TS_VERSION: $url"
    fetch "$url" "$archive.part"
    [ "$(sha256_of "$archive.part")" = "$sha" ] || { rm -f "$archive.part"; die "tree-sitter-typescript tarball SHA-256 mismatch"; }
    mv -f "$archive.part" "$archive"
  fi
  TS_SRC="$CACHE/tree-sitter-typescript-$TS_VERSION"
  if [ ! -f "$TS_SRC/typescript/src/parser.c" ]; then
    tmp="$(mktemp -d "$CACHE/extract.XXXXXX")"
    # The release asset is named .tar.xz but is gzip-compressed; let tar detect the format.
    tar -xf "$archive" -C "$tmp"
    rm -rf "$TS_SRC"
    chmod -R u+rwX,go+rX "$tmp"
    mv "$tmp" "$TS_SRC"
  fi
  [ -f "$TS_SRC/typescript/src/scanner.c" ] || die "tree-sitter-typescript tree lacks typescript/src/scanner.c"
  log "tree-sitter-typescript $TS_VERSION at $TS_SRC"
}

# The inputs that determine the output: pins, the shim, this script, the verifier.
compute_stamp() {
  STAMP="$(cat "$SCRIPT_DIR/luajit.pin" "$SCRIPT_DIR/ts-parser.pin" "$SCRIPT_DIR/zig.pin" "$SCRIPT_DIR/host_shim.c" "$0" "$VERIFY" | { if command -v sha256sum >/dev/null 2>&1; then sha256sum; else shasum -a 256; fi; } | awk '{print $1}')"
}

# ---------------------------------------------------------------- per-rid facts

lib_name() {
  case "$1" in
    win-x64) echo lua51.dll ;;
    linux-x64) echo libluajit-5.1.so.2 ;;
    osx-arm64) echo libluajit-5.1.2.dylib ;;
  esac
}

parser_ext() {
  case "$1" in win-x64) echo dll ;; linux-x64) echo so ;; osx-arm64) echo dylib ;; esac
}

zig_target() {
  case "$1" in
    win-x64) echo x86_64-windows-gnu ;;
    linux-x64) echo x86_64-linux-gnu.2.17 ;;   # glibc floor 2.17 (the build only needs 2.14)
    osx-arm64) echo aarch64-macos.11.0 ;;      # macOS 11 minimum, like the game
  esac
}

# verify_lib <rid> <file> <kind: luajit|parser>
verify_lib() {
  local rid="$1" file="$2" kind="$3" args="" e
  case "$rid" in
    win-x64)   args="--allow-dep KERNEL32.dll --allow-dep api-ms-win-crt-*-l1-1-0.dll" ;;
    linux-x64) args="--allow-dep libc.so.6 --allow-dep libm.so.6 --allow-dep libdl.so.2 --allow-dep libpthread.so.0 --allow-dep ld-linux-x86-64.so.2 --max-glibc 2.17" ;;
    osx-arm64) args="--allow-dep /usr/lib/libSystem.B.dylib --min-macos 11.0" ;;
  esac
  if [ "$kind" = luajit ]; then
    for e in $LUAJIT_EXPORTS; do args="$args --require-export $e"; done
    args="$args --forbid-export luaopen_ffi"
  else
    args="$args --require-export tree_sitter_typescript"
  fi
  # shellcheck disable=SC2086
  (cd / && python3 -I "$VERIFY" --rid "$rid" --lib "$file" $args) || die "$rid: $(basename "$file") failed verification"
}

# ---------------------------------------------------------------- LuaJIT

# build_luajit <rid> <stage dir>: builds into a fresh copy of the checkout and stages the outputs.
build_luajit() {
  local rid="$1" stage="$2" build target lib cli
  build="$CACHE/build-$rid"
  rm -rf "$build"
  mkdir -p "$build"
  tar -C "$LUAJIT_SRC" --exclude=".git" -cf - . | tar -C "$build" -xf -
  printf '%s\n' "$LUAJIT_TS" > "$build/.relver"        # the version string when no .git is present
  cp "$SCRIPT_DIR/host_shim.c" "$build/src/host_shim.c"
  printf '\n#include "host_shim.c"\n' >> "$build/src/ljamalg.c"
  target="$(zig_target "$rid")"
  lib="$(lib_name "$rid")"

  log "$rid: make amalg (zig cc -target $target)"
  case "$rid" in
    osx-arm64)
      MACOSX_DEPLOYMENT_TARGET=11.0 make -C "$build" amalg -j"$(ncpu)" \
        HOST_CC="$ZIG cc" CC="$ZIG cc -target $target" CROSS= TARGET_SYS=Darwin \
        TARGET_AR="$ZIG ar rcus" TARGET_STRIP=true TARGET_CFLAGS=-g0 XCFLAGS="$XCFLAGS_LUAJIT" >"$build/make.log" 2>&1 \
        || { tail -n 40 "$build/make.log" >&2; die "$rid: make failed (full log: $build/make.log)"; }
      cp "$build/src/libluajit.so" "$stage/luajit/$lib"
      mkdir -p "$stage/luajit/cli"
      cp "$build/src/luajit" "$stage/luajit/cli/luajit"
      ;;
    linux-x64)
      # LuaJIT uses external (DWARF) unwinding on Linux; gcc builds take _Unwind_* and
      # __register_frame from libgcc_s, zig links its bundled LLVM libunwind statically instead.
      make -C "$build" amalg -j"$(ncpu)" \
        HOST_CC="$ZIG cc" CC="$ZIG cc -target $target" CROSS= TARGET_SYS=Linux \
        TARGET_AR="$ZIG ar rcus" TARGET_STRIP=true TARGET_CFLAGS=-g0 \
        TARGET_LDFLAGS=-lunwind TARGET_SHLDFLAGS=-lunwind XCFLAGS="$XCFLAGS_LUAJIT" >"$build/make.log" 2>&1 \
        || { tail -n 40 "$build/make.log" >&2; die "$rid: make failed (full log: $build/make.log)"; }
      cp "$build/src/libluajit.so" "$stage/luajit/$lib"
      mkdir -p "$stage/luajit/cli"
      cp "$build/src/luajit" "$stage/luajit/cli/luajit"
      ;;
    win-x64)
      # HOST_XCFLAGS is set explicitly: the Makefile's default adds -malign-double (an x86-only
      # gcc flag) for Windows targets, which clang rejects on arm64 hosts.
      make -C "$build" amalg -j"$(ncpu)" \
        HOST_CC="$ZIG cc" HOST_XCFLAGS="-I. -DLUAJIT_OS=LUAJIT_OS_WINDOWS" \
        CC="$ZIG cc -target $target" CROSS= TARGET_SYS=Windows \
        TARGET_AR="$ZIG ar rcus" TARGET_STRIP=true TARGET_CFLAGS=-g0 XCFLAGS="$XCFLAGS_LUAJIT" >"$build/make.log" 2>&1 \
        || { tail -n 40 "$build/make.log" >&2; die "$rid: make failed (full log: $build/make.log)"; }
      cp "$build/src/lua51.dll" "$stage/luajit/$lib"
      mkdir -p "$stage/luajit/cli"
      cp "$build/src/luajit.exe" "$stage/luajit/cli/luajit.exe"
      cp "$build/src/lua51.dll" "$stage/luajit/cli/lua51.dll"   # the CLI imports it from its own folder
      ;;
  esac
  printf '%s\n' "$LUAJIT_COMMIT" > "$stage/luajit/.luajit-commit"
  verify_lib "$rid" "$stage/luajit/$lib" luajit
  runtime_check "$rid" "$stage/luajit/cli"
  log "$rid: $lib ($(wc -c < "$stage/luajit/$lib" | tr -d ' ') bytes)"
}

# runtime_check <rid> <cli dir>: runs the luajit CLI where this host can, and checks the version,
# that the JIT is on and that FFI is compiled out. Skips (loudly) when the binary cannot run here.
runtime_check() {
  local rid="$1" clidir="$2" how="" ver jit ffi
  [ "$RUNTIME_CHECKS" -eq 1 ] || { log "$rid: runtime checks skipped (--no-runtime-checks)"; return 0; }
  case "$rid" in
    osx-arm64)
      if [ "$HOST_SYS/$HOST_MACH" = "Darwin/arm64" ]; then how=native; fi ;;
    linux-x64)
      if [ "$HOST_SYS/$HOST_MACH" = "Linux/x86_64" ]; then how=native
      elif [ "${KSA_NATIVES_DOCKER:-1}" = 1 ] && command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then how=docker; fi ;;
    win-x64)
      if command -v wine64 >/dev/null 2>&1; then how=wine64
      elif command -v wine >/dev/null 2>&1; then how=wine; fi ;;
  esac
  if [ -z "$how" ]; then
    log "$rid: runtime checks skipped (no way to run $rid binaries on $HOST_SYS/$HOST_MACH; the static checks above still ran)"
    return 0
  fi
  case "$how" in
    native)
      ver="$("$clidir/luajit" -v 2>&1)" || die "$rid: luajit -v failed: $ver"
      jit="$("$clidir/luajit" -e 'print(jit.status())' 2>&1)" || die "$rid: jit.status() failed: $jit"
      ffi="$("$clidir/luajit" -e 'print(pcall(require, "ffi"))' 2>&1 | head -n 1)" ;;
    docker)
      log "$rid: running the CLI in docker (ubuntu:22.04, linux/amd64)"
      ver="$(docker run --rm --platform linux/amd64 -v "$clidir:/cli:ro" ubuntu:22.04 /cli/luajit -v 2>&1)" || die "$rid: luajit -v failed in docker: $ver"
      jit="$(docker run --rm --platform linux/amd64 -v "$clidir:/cli:ro" ubuntu:22.04 /cli/luajit -e 'print(jit.status())' 2>&1)" || die "$rid: jit.status() failed in docker: $jit"
      ffi="$(docker run --rm --platform linux/amd64 -v "$clidir:/cli:ro" ubuntu:22.04 /cli/luajit -e 'print(pcall(require, "ffi"))' 2>&1 | head -n 1)" ;;
    wine*)
      export WINEDEBUG=-all
      ver="$("$how" "$clidir/luajit.exe" -v 2>/dev/null | tr -d '\r')" || die "$rid: luajit.exe -v failed under $how"
      jit="$("$how" "$clidir/luajit.exe" -e 'print(jit.status())' 2>/dev/null | tr -d '\r')" || die "$rid: jit.status() failed under $how"
      ffi="$("$how" "$clidir/luajit.exe" -e 'print(pcall(require, "ffi"))' 2>/dev/null | tr -d '\r' | head -n 1)" ;;
  esac
  case "$ver" in "LuaJIT 2.1.$LUAJIT_TS "*) ;; *) die "$rid: unexpected version string: $ver" ;; esac
  case "$jit" in true*) ;; *) die "$rid: jit.status() did not start with true: $jit" ;; esac
  case "$ffi" in false*) ;; *) die "$rid: require(\"ffi\") did not fail: $ffi" ;; esac
  log "$rid: runtime ok ($how): $ver"
  log "$rid: jit.status: $jit"
  log "$rid: pcall(require, \"ffi\"): $ffi"
}

# ---------------------------------------------------------------- tree-sitter parser

build_parser() { # <rid> <stage dir>
  local rid="$1" stage="$2" ext target out pic=""
  ext="$(parser_ext "$rid")"
  target="$(zig_target "$rid")"
  out="$stage/parser/typescript.$ext"
  [ "$rid" = win-x64 ] || pic="-fPIC"
  log "$rid: compiling tree-sitter-typescript $TS_VERSION (zig cc -target $target)"
  # shellcheck disable=SC2086
  (cd "$TS_SRC/typescript" && "$ZIG" cc -target "$target" -shared $pic -O2 -g0 -I src src/parser.c src/scanner.c -o "$out") \
    || die "$rid: parser compile failed"
  rm -f "$stage/parser"/*.pdb "$stage/parser"/*.lib "$stage/parser"/*.exp   # zig's Windows side products; only the DLL ships
  printf '%s\n' "$TS_VERSION" > "$stage/parser/.typescript.ksa-version"
  verify_lib "$rid" "$out" parser
  log "$rid: typescript.$ext ($(wc -c < "$out" | tr -d ' ') bytes)"
}

# ---------------------------------------------------------------- packaging

build_rid() {
  local rid="$1" stage archive
  archive="$OUT/natives-$rid.tar.gz"
  if [ "$FORCE" -eq 0 ] && [ -f "$archive" ] && [ -f "$OUT/$rid.stamp" ] && [ "$(cat "$OUT/$rid.stamp")" = "$STAMP" ]; then
    log "$rid: up to date ($archive)"
    return 0
  fi
  rm -f "$OUT/$rid.stamp" "$archive"
  stage="$CACHE/stage-$rid"
  rm -rf "$stage"
  mkdir -p "$stage/luajit" "$stage/parser"

  build_luajit "$rid" "$stage"
  build_parser "$rid" "$stage"

  {
    echo "rid=$rid"
    echo "luajit.commit=$LUAJIT_COMMIT"
    echo "luajit.relver=$LUAJIT_TS"
    echo "luajit.flags=$XCFLAGS_LUAJIT"
    echo "tree-sitter-typescript=$TS_VERSION"
    echo "zig=$("$ZIG" version) target=$(zig_target "$rid")"
    echo "host=$HOST_SYS/$HOST_MACH"
    echo "built=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  } > "$stage/NATIVES.txt"

  mkdir -p "$OUT"
  tar -C "$stage" -czf "$archive.part" .
  mv -f "$archive.part" "$archive"
  printf '%s\n' "$STAMP" > "$OUT/$rid.stamp"
  log "$rid: wrote $archive ($(wc -c < "$archive" | tr -d ' ') bytes)"
}

write_sums() {
  local f
  (cd "$OUT" && rm -f SHA256SUMS && for f in natives-*.tar.gz; do
    [ -f "$f" ] || continue
    printf '%s  %s\n' "$(sha256_of "$f")" "$f" >> SHA256SUMS
  done)
  {
    echo "luajit.commit=$LUAJIT_COMMIT"
    echo "tree-sitter-typescript=$TS_VERSION"
    echo "zig=$("$ZIG" version)"
    echo "built=$(date -u +%Y-%m-%dT%H:%M:%SZ) on $HOST_SYS/$HOST_MACH"
  } > "$OUT/NATIVES.txt"
  log "wrote $OUT/SHA256SUMS:"
  sed 's/^/  /' "$OUT/SHA256SUMS"
}

# ---------------------------------------------------------------- main

for arg in "$@"; do
  case "$arg" in
    --force) FORCE=1 ;;
    --no-runtime-checks) RUNTIME_CHECKS=0 ;;
    -h|--help) usage; exit 0 ;;
    win-x64|linux-x64|osx-arm64)
      case " $RIDS " in *" $arg "*) ;; *) RIDS="$RIDS $arg" ;; esac ;;
    *) echo "build-natives: unknown argument '$arg'" >&2; usage >&2; exit 2 ;;
  esac
done
[ -n "$RIDS" ] || RIDS=" $ALL_RIDS"

command -v make >/dev/null 2>&1 || die "make is required (apt install make / Xcode command line tools)"
command -v python3 >/dev/null 2>&1 || die "python3 is required (for tools/natives-verify.py)"
[ -f "$VERIFY" ] || die "missing $VERIFY"

mkdir -p "$CACHE"
printf '*\n' > "$CACHE/.gitignore"
ensure_zig
ensure_luajit_src
ensure_ts_src
compute_stamp

for rid in $RIDS; do
  build_rid "$rid"
done
write_sums
log "done: $(echo $RIDS | tr ' ' ',') in $OUT"
