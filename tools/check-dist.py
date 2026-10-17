#!/usr/bin/env python3
"""Verify a built catcom dist folder.

    python3 tools/check-dist.py <dist-dir> [--rids win-x64,linux-x64]
                                [--allow-missing-natives] [--quiet]
    python3 tools/check-dist.py --self-test

Checks the layout the GameMod dist target produces (docs/IMPLEMENTATION_GUIDE.md,
sections 1, 6 and 10). Standard library only; runs on Python 3.9 and later
(tomllib is used when present, otherwise a fallback parser that covers the TOML
subset used by mod.toml).

Every check is a function returning a list of (level, message) tuples, where
level is "OK", "INFO", "WARN" or "ERROR". The self-test calls those functions
directly on synthetic trees, without subprocesses.

Exit status: 0 when there are no errors, 1 when there is at least one error,
2 for usage errors (missing dist directory, bad arguments).
"""

import argparse
import fnmatch
import json
import os
import posixpath
import re
import struct
import sys
import tempfile
import unittest

try:
    import tomllib  # Python 3.11+
except ImportError:  # Python 3.9 and 3.10 use parse_toml_fallback
    tomllib = None


MIB = 1024.0 * 1024.0
GAME_RIDS = ("win-x64", "linux-x64")  # default required RIDs (override with --rids)
EDITOR_RIDS = ("win-x64", "linux-x64", "osx-arm64")
DEPS_FILE = "Catcom.GameMod.deps.json"

REQUIRED_MANAGED = (
    "Catcom.GameMod.dll",
    DEPS_FILE,
    "Catcom.PurrTty.dll",
    "Catcom.Core.dll",
    "Catcom.Ksa.dll",
    "Catcom.Runtime.Lua.dll",
    "Catcom.Runtime.V8.dll",
    "ClearScript.Core.dll",
    "ClearScript.V8.dll",
    "ClearScript.V8.ICUData.dll",
    "Newtonsoft.Json.dll",
    "Tomlyn.dll",
    "MessagePack.dll",
)

# Runtime assets with these file names are never required and must not be present.
HARMONY_LIKE = ("0Harmony.dll", "StarMap.API.dll")

NATIVES = {
    "win-x64": ("lua51.dll", "ClearScriptV8.win-x64.dll"),
    "linux-x64": ("libluajit-5.1.so.2", "ClearScriptV8.linux-x64.so"),
    "osx-arm64": ("libluajit-5.1.2.dylib", "ClearScriptV8.osx-arm64.dylib"),
}

PE_MACHINES = {0x8664: "x86-64", 0xAA64: "arm64", 0x014C: "x86"}
ELF_MACHINES = {62: "x86-64", 183: "arm64", 3: "x86"}
MACHO_CPUS = {0x01000007: "x86-64", 0x0100000C: "arm64", 0x00000007: "x86"}

LEVEL_LABEL = {"OK": "OK   ", "INFO": "INFO ", "WARN": "WARN ", "ERROR": "ERROR"}


def _fmt_size(n):
    if n >= MIB:
        return "%.1f MiB" % (n / MIB)
    return "%.1f KiB" % (n / 1024.0)


def _listdir(path):
    if not os.path.isdir(path):
        return []
    return sorted(os.listdir(path))


def _tree_size(path):
    if os.path.isfile(path) and not os.path.islink(path):
        return os.path.getsize(path)
    total = 0
    for root, _dirs, files in os.walk(path):
        for name in files:
            full = os.path.join(root, name)
            if not os.path.islink(full):
                total += os.path.getsize(full)
    return total


# ---------------------------------------------------------------- TOML

def parse_toml(text):
    """Parse TOML with tomllib when available, else with the fallback parser."""
    if tomllib is not None:
        return tomllib.loads(text)
    return parse_toml_fallback(text)


_JSON_ESCAPES = {'"': '"', "\\": "\\", "b": "\b", "f": "\f", "n": "\n", "r": "\r", "t": "\t"}
_BARE_KEY_RE = re.compile(r"^[A-Za-z0-9_-]+$")
_KEY_PART_RE = re.compile(r'"(?:[^"\\]|\\.)*"|[^.\s"]+')


def _read_basic_string(text, start):
    """text[start] is a double quote. Returns (decoded value, index after the closing quote)."""
    out = []
    i = start + 1
    while i < len(text):
        ch = text[i]
        if ch == '"':
            return "".join(out), i + 1
        if ch == "\\":
            nxt = text[i + 1:i + 2]
            if nxt in _JSON_ESCAPES:
                out.append(_JSON_ESCAPES[nxt])
                i += 2
                continue
            if nxt in ("u", "U"):
                width = 4 if nxt == "u" else 8
                digits = text[i + 2:i + 2 + width]
                if len(digits) != width:
                    raise ValueError("truncated unicode escape in string")
                out.append(chr(int(digits, 16)))
                i += 2 + width
                continue
            raise ValueError("unsupported escape in string: backslash %s" % nxt)
        out.append(ch)
        i += 1
    raise ValueError("unterminated string")


def _strip_comment(line):
    out = []
    quote = None
    esc = False
    for ch in line:
        if quote == '"':
            out.append(ch)
            if esc:
                esc = False
            elif ch == "\\":
                esc = True
            elif ch == '"':
                quote = None
            continue
        if quote == "'":
            out.append(ch)
            if ch == "'":
                quote = None
            continue
        if ch == "#":
            break
        if ch in "\"'":
            quote = ch
        out.append(ch)
    return "".join(out)


def _bracket_depth(text):
    depth = 0
    quote = None
    esc = False
    for ch in text:
        if quote == '"':
            if esc:
                esc = False
            elif ch == "\\":
                esc = True
            elif ch == '"':
                quote = None
        elif quote == "'":
            if ch == "'":
                quote = None
        elif ch in "\"'":
            quote = ch
        elif ch in "[{":
            depth += 1
        elif ch in "]}":
            depth -= 1
    return depth


def _split_top(text):
    """Split on commas that are outside strings and brackets; drop empty items."""
    parts = []
    buf = []
    depth = 0
    quote = None
    esc = False
    for ch in text:
        if quote == '"':
            buf.append(ch)
            if esc:
                esc = False
            elif ch == "\\":
                esc = True
            elif ch == '"':
                quote = None
            continue
        if quote == "'":
            buf.append(ch)
            if ch == "'":
                quote = None
            continue
        if ch in "\"'":
            quote = ch
        elif ch in "[{":
            depth += 1
        elif ch in "]}":
            depth -= 1
        elif ch == "," and depth == 0:
            parts.append("".join(buf))
            buf = []
            continue
        buf.append(ch)
    parts.append("".join(buf))
    return [p.strip() for p in parts if p.strip()]


def _parse_key(text):
    text = text.strip()
    if text.startswith('"'):
        value, end = _read_basic_string(text, 0)
        if end != len(text):
            raise ValueError("bad quoted key: %r" % text)
        return value
    if not _BARE_KEY_RE.match(text):
        raise ValueError("unsupported key: %r" % text)
    return text


def _parse_value(text):
    text = text.strip()
    if not text:
        raise ValueError("missing value")
    head = text[0]
    if head == '"':
        value, end = _read_basic_string(text, 0)
        if text[end:].strip():
            raise ValueError("unexpected text after string: %r" % text)
        return value
    if head == "'":
        end = text.find("'", 1)
        if end < 0 or text[end + 1:].strip():
            raise ValueError("bad literal string: %r" % text)
        return text[1:end]
    if text == "true":
        return True
    if text == "false":
        return False
    if head == "[":
        if not text.endswith("]"):
            raise ValueError("unterminated array: %r" % text)
        return [_parse_value(item) for item in _split_top(text[1:-1])]
    if head == "{":
        if not text.endswith("}"):
            raise ValueError("unterminated inline table: %r" % text)
        table = {}
        for item in _split_top(text[1:-1]):
            key, sep, val = item.partition("=")
            if not sep:
                raise ValueError("bad inline table entry: %r" % item)
            table[_parse_key(key)] = _parse_value(val)
        return table
    cleaned = text.replace("_", "")
    try:
        return int(cleaned)
    except ValueError:
        pass
    try:
        return float(cleaned)
    except ValueError:
        raise ValueError("unsupported TOML value: %r" % text)


def _split_key_path(text):
    parts = _KEY_PART_RE.findall(text)
    if not parts:
        raise ValueError("empty table name")
    return [_parse_key(p) for p in parts]


def _table_at(root, path):
    node = root
    for part in path:
        nxt = node.setdefault(part, {})
        if isinstance(nxt, list):
            nxt = nxt[-1]
        if not isinstance(nxt, dict):
            raise ValueError("%s is not a table" % part)
        node = nxt
    return node


def parse_toml_fallback(text):
    """Minimal TOML reader: key = value, [table], [[array.of.tables]], strings,
    booleans, integers, floats, arrays and inline tables. Anything else raises."""
    root = {}
    current = root
    lines = text.splitlines()
    i = 0
    while i < len(lines):
        line = _strip_comment(lines[i]).strip()
        i += 1
        if not line:
            continue
        if line.startswith("[["):
            if not line.endswith("]]"):
                raise ValueError("bad array-of-tables header: %r" % line)
            path = _split_key_path(line[2:-2])
            parent = _table_at(root, path[:-1])
            array = parent.setdefault(path[-1], [])
            if not isinstance(array, list):
                raise ValueError("%s is not an array of tables" % path[-1])
            current = {}
            array.append(current)
        elif line.startswith("["):
            if not line.endswith("]"):
                raise ValueError("bad table header: %r" % line)
            current = _table_at(root, _split_key_path(line[1:-1]))
        else:
            key, sep, value = line.partition("=")
            if not sep:
                raise ValueError("expected 'key = value': %r" % line)
            while _bracket_depth(value) > 0 and i < len(lines):
                value += "\n" + _strip_comment(lines[i]).rstrip()
                i += 1
            current[_parse_key(key)] = _parse_value(value)
    return root


# ---------------------------------------------------------------- checks

def check_mod_toml(dist):
    path = os.path.join(dist, "mod.toml")
    if not os.path.isfile(path):
        return [("ERROR", "mod.toml is missing from the dist root")]
    try:
        with open(path, "rb") as fh:
            data = parse_toml(fh.read().decode("utf-8-sig"))
    except Exception as exc:  # a parse failure is a finding, not a crash
        return [("ERROR", "mod.toml does not parse: %s" % exc)]

    out = []
    name = data.get("name")
    version = data.get("version")
    starmap = data.get("StarMap") if isinstance(data.get("StarMap"), dict) else {}
    entry = starmap.get("EntryAssembly")
    ok = True
    if name != "catcom":
        ok = False
        out.append(("ERROR", "mod.toml name is %r, expected 'catcom'" % (name,)))
    if not isinstance(version, str) or not version:
        ok = False
        out.append(("ERROR", "mod.toml has no version"))
    if entry != "Catcom.GameMod":
        ok = False
        out.append(("ERROR", "mod.toml StarMap.EntryAssembly is %r, expected 'Catcom.GameMod'" % (entry,)))
    if ok:
        out.append(("OK", "mod.toml: name=%s version=%s StarMap.EntryAssembly=%s" % (name, version, entry)))

    deps = starmap.get("ModDependencies") or []
    if not isinstance(deps, list):
        out.append(("ERROR", "mod.toml StarMap.ModDependencies is not an array of tables"))
        deps = []
    for dep in deps:
        if not isinstance(dep, dict):
            out.append(("ERROR", "mod.toml ModDependencies entry is not a table"))
            continue
        imported = ", ".join(dep.get("ImportedAssemblies") or [])
        out.append(("OK", "mod.toml dependency: ModId=%s Optional=%s imports [%s]"
                    % (dep.get("ModId"), dep.get("Optional"), imported)))
    if not deps:
        out.append(("INFO", "mod.toml declares no ModDependencies"))
    return out


def check_managed(dist):
    missing = [n for n in REQUIRED_MANAGED if not os.path.isfile(os.path.join(dist, n))]
    if not missing:
        return [("OK", "managed files: all %d required files are in the dist root" % len(REQUIRED_MANAGED))]
    return [("ERROR", "missing required file in dist root: %s" % n) for n in missing]


def check_deps(dist):
    path = os.path.join(dist, DEPS_FILE)
    if not os.path.isfile(path):
        return [("ERROR", "missing required file in dist root: %s" % DEPS_FILE)]
    try:
        with open(path, "rb") as fh:
            deps = json.loads(fh.read().decode("utf-8-sig"))
    except (OSError, ValueError) as exc:
        return [("ERROR", "%s is not valid JSON: %s" % (DEPS_FILE, exc))]
    if not isinstance(deps, dict):
        return [("ERROR", "%s: top level is not an object" % DEPS_FILE)]

    target_name = (deps.get("runtimeTarget") or {}).get("name")
    targets = deps.get("targets") or {}
    libraries = deps.get("libraries") or {}
    if target_name not in targets or not isinstance(targets.get(target_name), dict):
        return [("ERROR", "%s: runtimeTarget.name %r is not a key of targets" % (DEPS_FILE, target_name))]

    checked = []
    missing = []
    for lib_key, entry in targets[target_name].items():
        ltype = (libraries.get(lib_key) or {}).get("type")
        if ltype not in ("package", "project"):
            continue  # reference (and anything else) is not shipped
        runtime = entry.get("runtime") if isinstance(entry, dict) else None
        for asset in runtime or {}:
            fname = posixpath.basename(asset)
            if fname in HARMONY_LIKE:
                continue  # never shipped; check_forbidden reports them if present
            checked.append(fname)
            if not os.path.isfile(os.path.join(dist, fname)):
                missing.append((lib_key, fname))

    if missing:
        return [("ERROR", "runtime asset %s (from %s) is not in the dist root" % (fname, lib_key))
                for lib_key, fname in missing]
    return [("OK", "deps.json (%s): %d runtime assets from packages and projects are present"
             % (target_name, len(checked)))]


def check_forbidden(dist):
    out = []
    names = sorted(n for n in os.listdir(dist) if os.path.isfile(os.path.join(dist, n)))
    for name in names:
        low = name.lower()
        if low in ("0harmony.dll", "starmap.api.dll", "ksa.dll") or fnmatch.fnmatchcase(low, "brutal*.dll"):
            out.append(("ERROR", "forbidden file in dist root: %s (compile-time or game assembly; must not be shipped)"
                        % name))
        elif fnmatch.fnmatchcase(low, "modmenu*.dll"):
            if low == "modmenu.attributes.dll":
                out.append(("WARN", "ModMenu.Attributes.dll in dist root: not expected in this dist "
                            "(the ModMenu mod provides it)"))
            else:
                out.append(("ERROR", "forbidden file in dist root: %s (ModMenu assemblies must not be shipped)"
                            % name))
        elif low in ("purrtty.customshellcontract.dll", "purrtty.logging.dll"):
            out.append(("ERROR", "forbidden file in dist root: %s (purrTTY assemblies must come from the purrTTY mod "
                        "through StarMap ImportedAssemblies; a local copy splits type identity and makes "
                        "purrTTY's launcher and shell registries invisible)" % name))
        elif low == "catcom.dist.dll":
            out.append(("ERROR", "forbidden file in dist root: %s (the deploy project's own assembly; "
                        "the dist target must exclude it)" % name))
    if not out:
        out.append(("OK", "no forbidden files in dist root"))
    return out


def expected_arch(rid):
    if rid.endswith("-arm64"):
        return "arm64"
    if rid.endswith("-x64"):
        return "x86-64"
    return None


def native_kind(name):
    if name.endswith(".dll"):
        return "pe"
    if name.endswith(".dylib"):
        return "macho"
    if ".so" in name:  # ClearScriptV8.linux-x64.so, libluajit-5.1.so.2
        return "elf"
    return None


def inspect_native(path, kind):
    """Return (format description, [architectures]). Raises ValueError (or struct.error) on bad data."""
    with open(path, "rb") as fh:
        data = fh.read(65536)
    if kind == "pe":
        if data[:2] != b"MZ":
            raise ValueError("not a PE file (no MZ header)")
        if len(data) < 0x40:
            raise ValueError("truncated PE header")
        (lfanew,) = struct.unpack_from("<I", data, 0x3C)
        if lfanew + 6 > len(data):
            raise ValueError("truncated PE header")
        if data[lfanew:lfanew + 4] != b"PE\x00\x00":
            raise ValueError("bad PE signature")
        (machine,) = struct.unpack_from("<H", data, lfanew + 4)
        return "PE", [PE_MACHINES.get(machine, "machine 0x%04x" % machine)]
    if kind == "elf":
        if data[:4] != b"\x7fELF":
            raise ValueError("not an ELF file (no \\x7fELF magic)")
        if len(data) < 20:
            raise ValueError("truncated ELF header")
        if data[4] != 2:
            raise ValueError("ELF class %d, expected ELF64" % data[4])
        endian = "<" if data[5] == 1 else ">"
        (machine,) = struct.unpack_from(endian + "H", data, 18)
        return "ELF64", [ELF_MACHINES.get(machine, "e_machine %d" % machine)]
    if kind == "macho":
        if len(data) < 8:
            raise ValueError("truncated Mach-O header")
        if data[:4] == b"\xcf\xfa\xed\xfe":  # MH_MAGIC_64, little-endian
            (cpu,) = struct.unpack_from("<I", data, 4)
            return "Mach-O 64", [MACHO_CPUS.get(cpu, "cputype 0x%08x" % cpu)]
        if data[:4] == b"\xca\xfe\xba\xbe":  # fat (universal), big-endian
            (count,) = struct.unpack_from(">I", data, 4)
            archs = []
            for index in range(min(count, 16)):
                if 8 + 20 * index + 4 > len(data):
                    raise ValueError("truncated universal Mach-O header")
                (cpu,) = struct.unpack_from(">I", data, 8 + 20 * index)
                archs.append(MACHO_CPUS.get(cpu, "cputype 0x%08x" % cpu))
            if not archs:
                raise ValueError("empty universal Mach-O header")
            return "Mach-O universal", archs
        raise ValueError("not a 64-bit Mach-O file")
    raise ValueError("unknown native format")


def _check_native(rid, name, path):
    kind = native_kind(name)
    want = expected_arch(rid)
    try:
        desc, archs = inspect_native(path, kind)
    except (ValueError, OSError, struct.error) as exc:
        return [("ERROR", "native %s/%s: %s" % (rid, name, exc))]
    size = _fmt_size(os.path.getsize(path))
    if want is None or want in archs:
        return [("OK", "native %s/%s: %s %s, %s" % (rid, name, desc, "/".join(archs), size))]
    return [("ERROR", "native %s/%s: wrong architecture: %s is %s, expected %s"
             % (rid, name, desc, "/".join(archs), want))]


def check_natives(dist, rids=GAME_RIDS, allow_missing_natives=False):
    out = []
    runtimes = os.path.join(dist, "runtimes")
    found = [d for d in _listdir(runtimes) if os.path.isdir(os.path.join(runtimes, d))]
    order = list(dict.fromkeys(list(rids) + list(NATIVES) + found))
    missing_level = "WARN" if allow_missing_natives else "ERROR"
    for rid in order:
        native_dir = os.path.join(runtimes, rid, "native")
        required = rid in rids
        if rid in NATIVES:
            names = list(NATIVES[rid])
        else:
            names = [n for n in _listdir(native_dir) if native_kind(n)]
        if not names:
            if required:
                out.append((missing_level, "no natives found for required RID %s (runtimes/%s/native/)"
                            % (rid, rid)))
            continue
        present = [n for n in names if os.path.isfile(os.path.join(native_dir, n))]
        missing = [n for n in names if n not in present]
        for name in present:
            out.extend(_check_native(rid, name, os.path.join(native_dir, name)))
        for name in missing:
            if required:
                out.append((missing_level, "missing native for required RID %s: runtimes/%s/native/%s"
                            % (rid, rid, name)))
            else:
                out.append(("INFO", "native %s/%s not present (optional RID; checked if present)" % (rid, name)))
    return out


def check_v8_assets(dist):
    ts = os.path.join(dist, "assets", "typescript.js")
    sc = os.path.join(dist, "assets", "sucrase.bundle.js")
    have_ts = os.path.isfile(ts)
    have_sc = os.path.isfile(sc)
    out = []
    if have_ts:
        out.append(("OK", "assets/typescript.js: %s" % _fmt_size(os.path.getsize(ts))))
    if have_sc:
        out.append(("OK", "assets/sucrase.bundle.js: %s" % _fmt_size(os.path.getsize(sc))))
    if not have_ts and not have_sc:
        out.append(("ERROR", "assets/typescript.js and assets/sucrase.bundle.js are both missing: "
                    "no in-engine TypeScript transpiler"))
    elif not have_ts:
        out.append(("WARN", "assets/typescript.js missing: TypeScript falls back to sucrase "
                    "(no enums downlevel diagnostics...)"))
    elif not have_sc:
        out.append(("WARN", "assets/sucrase.bundle.js missing: the sucrase fallback is unavailable"))
    return out


def check_script_libs(dist):
    names = ("scripts-lib/tui.ts", "scripts-lib/tui.lua", "scripts-lib/README.md")
    missing = [n for n in names if not os.path.isfile(os.path.join(dist, *n.split("/")))]
    if missing:
        return [("ERROR", "missing script library file: %s" % n) for n in missing]
    return [("OK", "scripts-lib: tui.ts, tui.lua and README.md present")]


def check_editor(dist):
    out = []
    required = ("editor/config/ksa-editor/init.lua", "editor/noshell", "editor/noshell.cmd")
    missing = [n for n in required if not os.path.isfile(os.path.join(dist, *n.split("/")))]
    for rel in missing:
        out.append(("ERROR", "missing editor file: %s" % rel))
    noshell = os.path.join(dist, "editor", "noshell")
    if os.path.isfile(noshell) and not os.access(noshell, os.X_OK):
        out.append(("WARN", "editor/noshell is not executable (chmod +x editor/noshell)"))
    if not missing:
        out.append(("OK", "editor: config/ksa-editor/init.lua, noshell and noshell.cmd present"))

    # The per-platform editor payload is required: every dist ships the editor for every platform
    # (tools/fetch-all.* fetches it; the dist target refuses to build without it).
    parser_ext = {"win-x64": "dll", "linux-x64": "so", "osx-arm64": "dylib"}
    for rid in EDITOR_RIDS:
        exe = ".exe" if rid == "win-x64" else ""
        items = (
            "editor/%s/nvim/bin/nvim%s" % (rid, exe),
            "editor/%s/nvim/lib/nvim/parser/typescript.%s" % (rid, parser_ext[rid]),
            "editor/%s/lsp/lua-language-server/bin/lua-language-server%s" % (rid, exe),
            "editor/%s/lsp/typescript/lib/tsc%s" % (rid, exe),
        )
        for rel in items:
            path = os.path.join(dist, *rel.split("/"))
            if os.path.isfile(path):
                out.append(("OK", "editor %s: %s (%s)" % (rid, rel, _fmt_size(os.path.getsize(path)))))
            else:
                out.append(("ERROR", "editor %s: %s missing (run tools/fetch-all.sh or tools/fetch-all.ps1, then rebuild)" % (rid, rel)))
        parser_dir = os.path.join(dist, "editor", rid, "nvim", "lib", "nvim", "parser")
        strays = [n for n in _listdir(parser_dir)
                  if fnmatch.fnmatchcase(n, "typescript.*") and n != "typescript." + parser_ext[rid]]
        for n in strays:
            out.append(("ERROR", "editor %s: stray parser editor/%s/nvim/lib/nvim/parser/%s (another platform's build; nvim's parser/typescript.* lookup could pick it)" % (rid, rid, n)))
    return out


def check_legal(dist):
    out = []
    for name in ("LICENSE", "THIRD-PARTY-NOTICES.md"):
        if not os.path.isfile(os.path.join(dist, name)):
            out.append(("ERROR", "missing legal file in dist root: %s" % name))
    tpl = os.path.join(dist, "third-party-licenses")
    entries = [n for n in _listdir(tpl) if not n.startswith(".")]
    if not os.path.isdir(tpl) or not entries:
        out.append(("ERROR", "third-party-licenses/ is missing or empty"))
    if not out:
        out.append(("OK", "legal: LICENSE, THIRD-PARTY-NOTICES.md and %d file%s in third-party-licenses/"
                    % (len(entries), "" if len(entries) == 1 else "s")))
    return out


def size_report(dist):
    """Returns (INFO lines, total bytes of the dist folder)."""
    total = _tree_size(dist)
    count = 0
    for _root, _dirs, files in os.walk(dist):
        count += len(files)
    entries = []
    for name in os.listdir(dist):
        full = os.path.join(dist, name)
        entries.append((_tree_size(full), name, os.path.isdir(full)))
    entries.sort(key=lambda e: (-e[0], e[1]))
    lines = [("INFO", "dist total: %.1f MiB in %d files" % (total / MIB, count))]
    for size, name, isdir in entries:
        lines.append(("INFO", "  %8.1f MiB  %s%s" % (size / MIB, name, "/" if isdir else "")))
    return lines, total


def run_checks(dist, rids=GAME_RIDS, allow_missing_natives=False):
    """Run every check. Returns (results, total dist bytes)."""
    steps = [
        ("mod.toml", lambda: check_mod_toml(dist)),
        ("managed files", lambda: check_managed(dist)),
        ("deps.json", lambda: check_deps(dist)),
        ("forbidden files", lambda: check_forbidden(dist)),
        ("natives", lambda: check_natives(dist, rids, allow_missing_natives)),
        ("V8 assets", lambda: check_v8_assets(dist)),
        ("script libraries", lambda: check_script_libs(dist)),
        ("editor", lambda: check_editor(dist)),
        ("legal", lambda: check_legal(dist)),
    ]
    results = []
    for label, step in steps:
        try:
            results.extend(step())
        except Exception as exc:  # a checker bug must not look like a clean dist
            results.append(("ERROR", "internal error in check '%s': %r" % (label, exc)))
    try:
        sizes, total = size_report(dist)
        results.extend(sizes)
    except OSError as exc:
        results.append(("ERROR", "could not measure dist: %s" % exc))
        total = 0
    return results, total


# ---------------------------------------------------------------- self-test fixtures

GUIDE_MOD_TOML = '''name = "catcom"
description = "In-band Lua (LuaJIT) and TypeScript scripting for KSA: game-loop hooks, terminal scripts on purrTTY surfaces, and a shipped Neovim editor."
version = "0.1.0"
author = "meow sci"

[StarMap]
EntryAssembly = "Catcom.GameMod"

[[StarMap.ModDependencies]]
ModId = "purrTTY"
Optional = true
ImportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]
'''

GUIDE_MOD_TOML_EXPECTED = {
    "name": "catcom",
    "description": "In-band Lua (LuaJIT) and TypeScript scripting for KSA: game-loop hooks, "
                   "terminal scripts on purrTTY surfaces, and a shipped Neovim editor.",
    "version": "0.1.0",
    "author": "meow sci",
    "StarMap": {
        "EntryAssembly": "Catcom.GameMod",
        "ModDependencies": [
            {"ModId": "purrTTY", "Optional": True,
             "ImportedAssemblies": ["purrTTY.CustomShellContract", "purrTTY.Logging"]},
        ],
    },
}


def _write(path, data):
    parent = os.path.dirname(path)
    if parent:
        os.makedirs(parent, exist_ok=True)
    with open(path, "wb") as fh:
        fh.write(data)


def _pe_bytes(machine=0x8664):
    b = bytearray(512)
    b[0:2] = b"MZ"
    struct.pack_into("<I", b, 0x3C, 0x80)
    b[0x80:0x84] = b"PE\x00\x00"
    struct.pack_into("<H", b, 0x84, machine)
    return bytes(b)


def _elf_bytes(machine=62):
    b = bytearray(256)
    b[0:4] = b"\x7fELF"
    b[4] = 2  # ELFCLASS64
    b[5] = 1  # little-endian
    b[6] = 1  # EV_CURRENT
    struct.pack_into("<H", b, 18, machine)
    return bytes(b)


def _macho_bytes(cputype=0x0100000C):
    b = bytearray(256)
    b[0:4] = b"\xcf\xfa\xed\xfe"
    struct.pack_into("<I", b, 4, cputype)
    return bytes(b)


def _fat_bytes(cputypes):
    b = bytearray(256)
    b[0:4] = b"\xca\xfe\xba\xbe"
    struct.pack_into(">I", b, 4, len(cputypes))
    for index, cpu in enumerate(cputypes):
        struct.pack_into(">I", b, 8 + 20 * index, cpu)
    return bytes(b)


def make_complete_tree(root):
    """Write a dist tree that passes every check with zero warnings."""
    def p(*parts):
        return os.path.join(root, *parts)

    _write(p("mod.toml"), GUIDE_MOD_TOML.encode("utf-8"))
    for name in REQUIRED_MANAGED:
        if name != DEPS_FILE:
            _write(p(name), b"managed assembly")
    deps = {
        "runtimeTarget": {"name": ".NETCoreApp,Version=v10.0/osx-arm64"},
        "targets": {
            ".NETCoreApp,Version=v10.0/osx-arm64": {
                "Catcom.GameMod/0.1.0": {"runtime": {"Catcom.GameMod.dll": {}}},
                "Newtonsoft.Json/13.0.3": {"runtime": {"lib/net6.0/Newtonsoft.Json.dll": {}}},
                "Lib.Harmony/2.4.2": {"runtime": {"lib/net8.0/0Harmony.dll": {}}},
                "Some.Reference/1.0.0": {"runtime": {"lib/net10.0/NotShipped.dll": {}}},
            }
        },
        "libraries": {
            "Catcom.GameMod/0.1.0": {"type": "project"},
            "Newtonsoft.Json/13.0.3": {"type": "package"},
            "Lib.Harmony/2.4.2": {"type": "package"},
            "Some.Reference/1.0.0": {"type": "reference"},
        },
    }
    _write(p(DEPS_FILE), json.dumps(deps).encode("utf-8"))
    _write(p("LICENSE"), b"MIT License\n")
    _write(p("THIRD-PARTY-NOTICES.md"), b"# Third-party notices\n")
    _write(p("third-party-licenses", "LuaJIT-COPYRIGHT.txt"), b"Copyright (c) LuaJIT\n")

    _write(p("runtimes", "win-x64", "native", "lua51.dll"), _pe_bytes(0x8664))
    _write(p("runtimes", "win-x64", "native", "ClearScriptV8.win-x64.dll"), _pe_bytes(0x8664))
    _write(p("runtimes", "linux-x64", "native", "libluajit-5.1.so.2"), _elf_bytes(62))
    _write(p("runtimes", "linux-x64", "native", "ClearScriptV8.linux-x64.so"), _elf_bytes(62))
    _write(p("runtimes", "osx-arm64", "native", "libluajit-5.1.2.dylib"), _macho_bytes(0x0100000C))
    _write(p("runtimes", "osx-arm64", "native", "ClearScriptV8.osx-arm64.dylib"),
           _fat_bytes([0x01000007, 0x0100000C]))

    _write(p("assets", "typescript.js"), b"/* typescript */")
    _write(p("assets", "sucrase.bundle.js"), b"/* sucrase */")

    _write(p("scripts-lib", "tui.ts"), b"export {};\n")
    _write(p("scripts-lib", "tui.lua"), b"return {}\n")
    _write(p("scripts-lib", "README.md"), b"# scripts-lib\n")

    _write(p("editor", "config", "ksa-editor", "init.lua"), b"-- init\n")
    _write(p("editor", "noshell"), b"#!/bin/sh\nexit 1\n")
    os.chmod(p("editor", "noshell"), 0o755)
    _write(p("editor", "noshell.cmd"), b"@exit /b 1\r\n")
    parser_ext = {"win-x64": "dll", "linux-x64": "so", "osx-arm64": "dylib"}
    for rid in EDITOR_RIDS:
        exe = ".exe" if rid == "win-x64" else ""
        _write(p("editor", rid, "nvim", "bin", "nvim" + exe), b"nvim")
        _write(p("editor", rid, "lsp", "lua-language-server", "bin", "lua-language-server" + exe), b"luals")
        _write(p("editor", rid, "lsp", "typescript", "lib", "tsc" + exe), b"tsc")
        _write(p("editor", rid, "nvim", "lib", "nvim", "parser", "typescript." + parser_ext[rid]), b"parser")
    return root


def _levels(results, level):
    return [message for lvl, message in results if lvl == level]


# ---------------------------------------------------------------- self-test

class CheckDistTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.dist = make_complete_tree(os.path.join(self._tmp.name, "dist"))

    def tearDown(self):
        self._tmp.cleanup()

    def _path(self, *parts):
        return os.path.join(self.dist, *parts)

    def test_complete_tree_has_no_errors_or_warnings(self):
        results, total = run_checks(self.dist)
        self.assertEqual(_levels(results, "ERROR"), [])
        self.assertEqual(_levels(results, "WARN"), [])
        self.assertGreater(total, 0)

    def test_fallback_toml_parses_guide_mod_toml_exactly(self):
        self.assertEqual(parse_toml_fallback(GUIDE_MOD_TOML), GUIDE_MOD_TOML_EXPECTED)
        if tomllib is not None:
            self.assertEqual(tomllib.loads(GUIDE_MOD_TOML), GUIDE_MOD_TOML_EXPECTED)

    def test_mod_toml_wrong_entry_assembly_is_error(self):
        _write(self._path("mod.toml"), GUIDE_MOD_TOML.replace("Catcom.GameMod\"", "Other.Mod\"").encode())
        self.assertTrue(_levels(check_mod_toml(self.dist), "ERROR"))

    def test_missing_clearscript_v8_is_error(self):
        os.remove(self._path("ClearScript.V8.dll"))
        errors = _levels(check_managed(self.dist), "ERROR")
        self.assertEqual(len(errors), 1)
        self.assertIn("ClearScript.V8.dll", errors[0])
        results, _ = run_checks(self.dist)
        self.assertGreater(len(_levels(results, "ERROR")), 0)

    def test_missing_deps_runtime_asset_is_error(self):
        os.remove(self._path("Newtonsoft.Json.dll"))
        self.assertEqual(len(_levels(check_deps(self.dist), "ERROR")), 1)

    def test_harmony_in_dist_is_error(self):
        _write(self._path("0Harmony.dll"), b"harmony")
        self.assertTrue(_levels(check_forbidden(self.dist), "ERROR"))

    def test_purrtty_local_copy_is_error(self):
        _write(self._path("purrTTY.Logging.dll"), b"local copy")
        _write(self._path("purrTTY.CustomShellContract.dll"), b"local copy")
        errors = _levels(check_forbidden(self.dist), "ERROR")
        self.assertEqual(len(errors), 2)
        self.assertTrue(all("type identity" in e for e in errors))

    def test_dist_project_assembly_is_error(self):
        _write(self._path("Catcom.Dist.dll"), b"deploy project")
        self.assertEqual(len(_levels(check_forbidden(self.dist), "ERROR")), 1)

    def test_missing_purrtty_bridge_is_error(self):
        os.remove(self._path("Catcom.PurrTty.dll"))
        errors = _levels(check_managed(self.dist), "ERROR")
        self.assertEqual(len(errors), 1)
        self.assertIn("Catcom.PurrTty.dll", errors[0])

    def test_modmenu_attributes_is_warning_other_modmenu_is_error(self):
        _write(self._path("ModMenu.Attributes.dll"), b"x")
        results = check_forbidden(self.dist)
        self.assertEqual(_levels(results, "ERROR"), [])
        self.assertEqual(len(_levels(results, "WARN")), 1)
        _write(self._path("ModMenu.Core.dll"), b"x")
        self.assertEqual(len(_levels(check_forbidden(self.dist), "ERROR")), 1)

    def test_missing_editor_payload_is_error(self):
        os.remove(self._path("editor", "linux-x64", "lsp", "typescript", "lib", "tsc"))
        os.remove(self._path("editor", "win-x64", "nvim", "lib", "nvim", "parser", "typescript.dll"))
        errors = _levels(check_editor(self.dist), "ERROR")
        self.assertEqual(len(errors), 2)
        self.assertTrue(any("linux-x64/lsp/typescript/lib/tsc" in e for e in errors))
        self.assertTrue(any("win-x64/nvim/lib/nvim/parser/typescript.dll" in e for e in errors))

    def test_stray_parser_from_another_platform_is_error(self):
        _write(self._path("editor", "osx-arm64", "nvim", "lib", "nvim", "parser", "typescript.so"), b"linux parser")
        errors = _levels(check_editor(self.dist), "ERROR")
        self.assertEqual(len(errors), 1)
        self.assertIn("stray parser", errors[0])

    def test_wrong_arch_so_is_error(self):
        _write(self._path("runtimes", "linux-x64", "native", "ClearScriptV8.linux-x64.so"), _elf_bytes(183))
        errors = _levels(check_natives(self.dist, GAME_RIDS, False), "ERROR")
        self.assertEqual(len(errors), 1)
        self.assertIn("arm64", errors[0])

    def test_wrong_format_native_is_error(self):
        _write(self._path("runtimes", "win-x64", "native", "lua51.dll"), b"not a pe file at all")
        self.assertEqual(len(_levels(check_natives(self.dist, GAME_RIDS, False), "ERROR")), 1)

    def test_truncated_native_reports_friendly_error(self):
        _write(self._path("runtimes", "win-x64", "native", "lua51.dll"), b"MZ")
        errors = _levels(check_natives(self.dist, GAME_RIDS, False), "ERROR")
        self.assertEqual(len(errors), 1)
        self.assertIn("truncated", errors[0])

    def test_missing_win_natives_are_error_but_warning_when_allowed(self):
        os.remove(self._path("runtimes", "win-x64", "native", "lua51.dll"))
        os.remove(self._path("runtimes", "win-x64", "native", "ClearScriptV8.win-x64.dll"))
        strict = check_natives(self.dist, GAME_RIDS, False)
        self.assertEqual(len(_levels(strict, "ERROR")), 2)
        lenient = check_natives(self.dist, GAME_RIDS, True)
        self.assertEqual(_levels(lenient, "ERROR"), [])
        self.assertEqual(len(_levels(lenient, "WARN")), 2)

    def test_optional_osx_natives_absent_is_info_only(self):
        for name in NATIVES["osx-arm64"]:
            os.remove(self._path("runtimes", "osx-arm64", "native", name))
        results = check_natives(self.dist, GAME_RIDS, False)
        self.assertEqual(_levels(results, "ERROR"), [])
        self.assertEqual(_levels(results, "WARN"), [])

    def test_macho_wrong_arch_is_error(self):
        _write(self._path("runtimes", "osx-arm64", "native", "libluajit-5.1.2.dylib"), _macho_bytes(0x01000007))
        results = check_natives(self.dist, GAME_RIDS, False)
        self.assertEqual(len(_levels(results, "ERROR")), 1)

    def test_fat_macho_with_arm64_slice_is_accepted(self):
        results = check_natives(self.dist, GAME_RIDS, False)
        self.assertEqual(_levels(results, "ERROR"), [])

    def test_both_v8_assets_missing_is_error(self):
        os.remove(self._path("assets", "typescript.js"))
        os.remove(self._path("assets", "sucrase.bundle.js"))
        self.assertEqual(len(_levels(check_v8_assets(self.dist), "ERROR")), 1)

    def test_only_typescript_js_missing_is_warning(self):
        os.remove(self._path("assets", "typescript.js"))
        results = check_v8_assets(self.dist)
        self.assertEqual(_levels(results, "ERROR"), [])
        warnings = _levels(results, "WARN")
        self.assertEqual(len(warnings), 1)
        self.assertIn("falls back to sucrase", warnings[0])

    def test_missing_script_lib_is_error(self):
        os.remove(self._path("scripts-lib", "tui.lua"))
        self.assertEqual(len(_levels(check_script_libs(self.dist), "ERROR")), 1)

    def test_noshell_not_executable_is_warning(self):
        os.chmod(self._path("editor", "noshell"), 0o644)
        results = check_editor(self.dist)
        self.assertEqual(_levels(results, "ERROR"), [])
        self.assertTrue(any("noshell" in m for m in _levels(results, "WARN")))

    def test_missing_legal_file_is_error(self):
        os.remove(self._path("THIRD-PARTY-NOTICES.md"))
        self.assertEqual(len(_levels(check_legal(self.dist), "ERROR")), 1)

    def test_empty_third_party_dir_is_error(self):
        os.remove(self._path("third-party-licenses", "LuaJIT-COPYRIGHT.txt"))
        self.assertEqual(len(_levels(check_legal(self.dist), "ERROR")), 1)


def run_self_test():
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(CheckDistTests)
    result = unittest.TextTestRunner(stream=sys.stdout, verbosity=1).run(suite)
    return 0 if result.wasSuccessful() else 1


# ---------------------------------------------------------------- main

def main(argv=None):
    parser = argparse.ArgumentParser(description="Verify a built catcom dist folder.")
    parser.add_argument("dist_dir", nargs="?", help="dist folder to check")
    parser.add_argument("--rids", default=",".join(GAME_RIDS),
                        help="comma-separated RIDs whose natives are required (default: %(default)s)")
    parser.add_argument("--allow-missing-natives", action="store_true",
                        help="report missing required natives as warnings instead of errors")
    parser.add_argument("--quiet", action="store_true",
                        help="print only WARN and ERROR lines and the summary")
    parser.add_argument("--self-test", action="store_true",
                        help="run the built-in unit tests and exit with their status")
    args = parser.parse_args(argv)

    if args.self_test:
        return run_self_test()
    if not args.dist_dir:
        parser.error("a dist directory is required (or use --self-test)")
    if not os.path.isdir(args.dist_dir):
        print("check-dist: not a directory: %s" % args.dist_dir, file=sys.stderr)
        return 2
    rids = [r.strip() for r in args.rids.split(",") if r.strip()]
    if not rids:
        parser.error("--rids must name at least one RID")

    results, total = run_checks(args.dist_dir, rids, args.allow_missing_natives)
    errors = 0
    warnings = 0
    for level, message in results:
        if level == "ERROR":
            errors += 1
        elif level == "WARN":
            warnings += 1
        if args.quiet and level not in ("WARN", "ERROR"):
            continue
        print("%s %s" % (LEVEL_LABEL[level], message))
    print("check-dist: %d errors, %d warnings (%.1f MiB)" % (errors, warnings, total / MIB))
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
