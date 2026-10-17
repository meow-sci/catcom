#!/usr/bin/env python3
"""Verify a native shared library built for one of the ksa-scripting runtime ids.

    tools/natives-verify.py --rid <win-x64|linux-x64|osx-arm64> --lib <path>
        [--require-export NAME]... [--forbid-export NAME]...
        [--allow-dep PATTERN]... [--max-glibc X.Y] [--min-macos X.Y] [--json]

The file bytes are parsed with the standard library only (struct; no nm, objdump
or otool, no pip packages) and the inspected file is never executed, so the
checker behaves identically on macOS and Linux hosts. The zig cross-compilation
scripts call it after each LuaJIT or tree-sitter parser build. Per rid:
  win-x64     PE32+ (COFF) x86-64 DLL: export-directory names; import and
              delay-load DLL names as dependencies.
  linux-x64   ELF64 little-endian x86-64 ET_DYN: defined GLOBAL/WEAK
              FUNC/OBJECT/IFUNC .dynsym symbols; DT_NEEDED; highest GLIBC_x.y in
              .gnu.version_r (a GLIBC_PRIVATE reference always fails).
  osx-arm64   Mach-O 64-bit arm64 MH_DYLIB, whatever the extension: external
              N_SECT LC_SYMTAB symbols compared with one leading underscore
              removed; LC_LOAD_DYLIB / LC_LOAD_WEAK_DYLIB / LC_REEXPORT_DYLIB;
              LC_ID_DYLIB, LC_BUILD_VERSION (or LC_VERSION_MIN_MACOSX) minos and
              LC_CODE_SIGNATURE presence are reported. Fat binaries are rejected.

--allow-dep takes case-insensitive fnmatch globs and every dependency must match
one; without it dependencies are only listed. --max-glibc is linux-x64 only and
--min-macos osx-arm64 only; both are inclusive, compared as version tuples.

Output is one line per fact or check prefixed "ok:", "FAIL:" or "info:" plus a
summary line, or one JSON object with --json. Exit status: 0 when every check
passes, 1 when a check fails or the file is truncated or malformed, 2 for usage
errors.
"""

import argparse
import fnmatch
import json
import os
import re
import struct
import sys

# rid -> (format, arch, file kind) that the library must have.
RID_EXPECT = {
    "win-x64": ("PE32+", "x86-64", "DLL"),
    "linux-x64": ("ELF64", "x86-64", "shared object"),
    "osx-arm64": ("Mach-O 64", "arm64", "dylib"),
}

ELF_MACHINES = {3: "i386", 40: "arm", 62: "x86-64", 183: "aarch64"}
ELF_TYPES = {1: "relocatable", 2: "executable", 3: "shared object", 4: "core"}
PE_MACHINES = {0x14C: "i386", 0x1C0: "arm", 0x8664: "x86-64", 0xAA64: "arm64"}
MACHO_CPUS = {7: "i386", 12: "arm", 0x01000007: "x86-64", 0x0100000C: "arm64"}
MACHO_FILETYPES = {1: "object", 2: "executable", 6: "dylib", 8: "bundle", 9: "dylib stub"}
MACHO_PLATFORMS = {1: "macOS", 2: "iOS", 3: "tvOS", 4: "watchOS", 5: "bridgeOS", 6: "macCatalyst"}
GLIBC_VERSION_RE = re.compile(r"^GLIBC_(\d+)\.(\d+)(?:\.(\d+))?$")


class ParseError(Exception):
    """A truncated or malformed file; reported as a FAIL line, never a traceback."""


class LibInfo:
    """Facts the parsers learned about one library (verdicts are made in run_checks)."""

    def __init__(self, size):
        self.size = size
        self.fmt = "unknown"        # "ELF64", "PE32+", "PE32", "Mach-O 64"
        self.arch = "unknown"       # "x86-64", "arm64", ...
        self.kind = "unknown"       # "shared object", "DLL", "dylib", ...
        self.exports = []           # raw exported symbol names, file order
        self.deps = []              # dynamic dependency names, file order, deduplicated
        self.facts = []             # extra (level, message) lines from the parser
        self.glibc_max = None       # highest GLIBC_x.y referenced, as a 3-tuple (ELF)
        self.glibc_private = False  # True when a GLIBC_PRIVATE version is referenced (ELF)
        self.minos = None           # minimum macOS version as a 3-tuple (Mach-O)
        self.install_name = None    # LC_ID_DYLIB name (Mach-O)
        self.code_signature = False  # LC_CODE_SIGNATURE present (Mach-O)

    def add_dep(self, name):
        if name not in self.deps:
            self.deps.append(name)


def unpack(fmt, data, off):
    """struct.unpack_from with a bounds check that raises ParseError instead of struct.error."""
    size = struct.calcsize(fmt)
    if off < 0 or off + size > len(data):
        raise ParseError("truncated: %d bytes needed at offset 0x%x, file has %d" % (size, off, len(data)))
    return struct.unpack_from(fmt, data, off)


def cstring(data, off, limit=4096):
    """The NUL-terminated string at off, decoded as UTF-8 with replacement."""
    if off < 0 or off >= len(data):
        raise ParseError("string offset 0x%x is outside the file" % off)
    end = data.find(b"\0", off, off + limit)
    if end < 0:
        raise ParseError("unterminated string at offset 0x%x" % off)
    return data[off:end].decode("utf-8", "replace")


def version_tuple(text):
    """'2.17' or '15.0.1' -> (2, 17, 0) / (15, 0, 1), for inclusive comparisons."""
    m = re.match(r"^(\d+)\.(\d+)(?:\.(\d+))?$", text)
    if not m:
        raise argparse.ArgumentTypeError("expected a version like 2.17 or 11.0, got %r" % text)
    return (int(m.group(1)), int(m.group(2)), int(m.group(3) or 0))


def version_text(v):
    """(15, 0, 0) -> '15.0'; (2, 2, 5) -> '2.2.5'."""
    return "%d.%d" % v[:2] if v[2] == 0 else "%d.%d.%d" % v


# ---------------------------------------------------------------- ELF64

def parse_elf(data, info):
    """Fill info from an ELF64 little-endian file (section headers, .dynsym, .dynamic, verneed)."""
    info.fmt = "ELF64"
    ident = unpack("<16s", data, 0)[0]
    if ident[4] != 2:
        raise ParseError("ELF class %d, expected 2 (ELFCLASS64)" % ident[4])
    if ident[5] != 1:
        raise ParseError("ELF data encoding %d, expected 1 (little-endian)" % ident[5])
    e_type, e_machine = unpack("<HH", data, 16)
    shoff = unpack("<Q", data, 40)[0]
    shentsize, shnum = unpack("<HH", data, 58)
    info.arch = ELF_MACHINES.get(e_machine, "machine %d" % e_machine)
    info.kind = ELF_TYPES.get(e_type, "type %d" % e_type)
    if shoff == 0 or shnum == 0 or shentsize != 64:
        raise ParseError("no usable section header table (shoff=0x%x shnum=%d shentsize=%d)" % (shoff, shnum, shentsize))
    # Elf64_Shdr: name type flags addr offset size link info addralign entsize
    sections = [unpack("<IIQQQQIIQQ", data, shoff + i * 64) for i in range(shnum)]

    def linked_strtab(sh):
        if sh[6] >= shnum or sections[sh[6]][1] != 3:  # SHT_STRTAB
            raise ParseError("section sh_link %d is not a string table" % sh[6])
        return sections[sh[6]]

    def strtab_str(strtab, index):
        if index >= strtab[5]:
            raise ParseError("string index 0x%x is past the end of the string table" % index)
        return cstring(data, strtab[4] + index, strtab[5] - index)

    def first(sh_type):
        return next((s for s in sections if s[1] == sh_type), None)

    dynsym = first(11)  # SHT_DYNSYM
    if dynsym is None:
        raise ParseError("no .dynsym section; not a dynamically linked object")
    dynstr = linked_strtab(dynsym)
    entsize = dynsym[9] or 24
    for i in range(dynsym[5] // entsize):
        st_name, st_info, _other, st_shndx = unpack("<IBBH", data, dynsym[4] + i * entsize)
        bind, typ = st_info >> 4, st_info & 0xF
        if st_name and st_shndx != 0 and bind in (1, 2) and typ in (1, 2, 10):
            info.exports.append(strtab_str(dynstr, st_name))

    dynamic = first(6)  # SHT_DYNAMIC
    if dynamic is None:
        raise ParseError("no .dynamic section")
    dynamic_str = linked_strtab(dynamic)
    for i in range(dynamic[5] // 16):
        tag, val = unpack("<qQ", data, dynamic[4] + i * 16)
        if tag == 0:  # DT_NULL
            break
        if tag == 1:  # DT_NEEDED
            info.add_dep(strtab_str(dynamic_str, val))
        elif tag == 14:  # DT_SONAME
            info.facts.append(("info", "soname: " + strtab_str(dynamic_str, val)))

    verneed = first(0x6FFFFFFE)  # SHT_GNU_verneed
    names = []
    if verneed is not None:
        vstr = linked_strtab(verneed)
        off, remaining = verneed[4], max(verneed[7], 1)  # sh_info = number of Verneed entries
        while remaining > 0:
            _ver, vn_cnt, _file, vn_aux, vn_next = unpack("<HHIII", data, off)
            aux = off + vn_aux
            for _ in range(vn_cnt):
                _hash, _flags, _other, vna_name, vna_next = unpack("<IHHII", data, aux)
                names.append(strtab_str(vstr, vna_name))
                if vna_next == 0:
                    break
                aux += vna_next
            remaining -= 1
            if vn_next == 0:
                break
            off += vn_next
    else:
        info.facts.append(("info", "no .gnu.version_r section; GLIBC versions scanned from .dynstr"))
        blob = data[dynstr[4]:dynstr[4] + dynstr[5]]
        names = [s.decode("ascii") for s in re.findall(rb"GLIBC_[0-9.]+|GLIBC_PRIVATE", blob)]
    for name in names:
        if name == "GLIBC_PRIVATE":
            info.glibc_private = True
        m = GLIBC_VERSION_RE.match(name)
        if m:
            v = (int(m.group(1)), int(m.group(2)), int(m.group(3) or 0))
            if info.glibc_max is None or v > info.glibc_max:
                info.glibc_max = v


# ---------------------------------------------------------------- PE32+

def parse_pe(data, info):
    """Fill info from a PE (COFF) image: export names, import and delay-load DLLs."""
    e_lfanew = unpack("<I", data, 0x3C)[0]
    if unpack("<4s", data, e_lfanew)[0] != b"PE\0\0":
        raise ParseError("no PE signature at e_lfanew 0x%x" % e_lfanew)
    coff = e_lfanew + 4
    machine, nsections, _ts, _symptr, _nsyms, opt_size, characteristics = unpack("<HHIIIHH", data, coff)
    info.arch = PE_MACHINES.get(machine, "machine 0x%x" % machine)
    info.kind = "DLL" if characteristics & 0x2000 else "executable"
    opt = coff + 20
    magic = unpack("<H", data, opt)[0]
    if magic == 0x20B:
        info.fmt, image_base, ndirs_off = "PE32+", unpack("<Q", data, opt + 24)[0], opt + 108
    elif magic == 0x10B:
        info.fmt, image_base, ndirs_off = "PE32", unpack("<I", data, opt + 28)[0], opt + 92
    else:
        raise ParseError("unknown optional header magic 0x%x" % magic)
    ndirs = unpack("<I", data, ndirs_off)[0]
    dirs = [unpack("<II", data, ndirs_off + 4 + i * 8) for i in range(min(ndirs, 16))]
    # IMAGE_SECTION_HEADER: name vsize va rawsize rawptr relocptr lineptr nreloc nline flags
    sections = [unpack("<8sIIIIIIHHI", data, opt + opt_size + i * 40) for i in range(nsections)]

    def rva_to_off(rva):
        for s in sections:
            if s[2] <= rva < s[2] + max(s[1], s[3]):
                return rva - s[2] + s[4]
        raise ParseError("RVA 0x%x is not inside any section" % rva)

    def directory(index):
        return dirs[index] if index < len(dirs) and dirs[index][0] else None

    export = directory(0)
    if export is not None:
        fields = unpack("<IIHHIIIIIII", data, rva_to_off(export[0]))  # IMAGE_EXPORT_DIRECTORY
        name_rva, nnames, names_rva = fields[4], fields[7], fields[9]
        if name_rva:
            info.facts.append(("info", "export module name: " + cstring(data, rva_to_off(name_rva))))
        if nnames:
            names_off = rva_to_off(names_rva)
            for i in range(nnames):
                info.exports.append(cstring(data, rva_to_off(unpack("<I", data, names_off + i * 4)[0])))

    imports = directory(1)
    if imports is not None:
        off = rva_to_off(imports[0])
        while True:
            fields = unpack("<IIIII", data, off)  # IMAGE_IMPORT_DESCRIPTOR
            if not any(fields):
                break
            if fields[3]:
                info.add_dep(cstring(data, rva_to_off(fields[3])))
            off += 20

    delay = directory(13)
    if delay is not None:
        off = rva_to_off(delay[0])
        while True:
            fields = unpack("<IIIIIIII", data, off)  # IMAGE_DELAYLOAD_DESCRIPTOR
            if not any(fields):
                break
            name_rva = fields[1] if fields[0] & 1 else fields[1] - image_base  # attribute bit 0: RVA based
            name = cstring(data, rva_to_off(name_rva))
            info.facts.append(("info", "delay-load import: " + name))
            info.add_dep(name)
            off += 32


# ---------------------------------------------------------------- Mach-O

def packed_version(v):
    """Mach-O 0xXXXXYYZZ -> (X, Y, Z)."""
    return (v >> 16, (v >> 8) & 0xFF, v & 0xFF)


def parse_macho(data, info):
    """Fill info from a 64-bit little-endian Mach-O: load commands and LC_SYMTAB."""
    info.fmt = "Mach-O 64"
    _magic, cputype, _sub, filetype, ncmds, sizeofcmds, _flags, _res = unpack("<IiiIIIII", data, 0)
    info.arch = MACHO_CPUS.get(cputype, "cputype 0x%x" % cputype)
    info.kind = MACHO_FILETYPES.get(filetype, "filetype %d" % filetype)
    if 32 + sizeofcmds > len(data):
        raise ParseError("load commands (%d bytes) run past the end of the file" % sizeofcmds)
    symtab = None
    off = 32
    for i in range(ncmds):
        cmd, cmdsize = unpack("<II", data, off)
        if cmdsize < 8 or off + cmdsize > 32 + sizeofcmds:
            raise ParseError("load command %d at 0x%x has bad size %d" % (i, off, cmdsize))
        if cmd == 0x2:  # LC_SYMTAB
            symtab = unpack("<IIII", data, off + 8)
        elif cmd in (0xC, 0x80000018, 0x8000001F, 0xD):  # LC_LOAD_DYLIB, LC_LOAD_WEAK_DYLIB, LC_REEXPORT_DYLIB, LC_ID_DYLIB
            name_off = unpack("<I", data, off + 8)[0]
            if name_off >= cmdsize:
                raise ParseError("dylib name offset %d is outside its load command" % name_off)
            name = cstring(data, off + name_off, cmdsize - name_off)
            if cmd == 0xD:
                info.install_name = name
            else:
                if cmd == 0x80000018:
                    info.facts.append(("info", "weak dependency: " + name))
                elif cmd == 0x8000001F:
                    info.facts.append(("info", "re-exported dependency: " + name))
                info.add_dep(name)
        elif cmd == 0x32:  # LC_BUILD_VERSION
            platform, minos, sdk, _ntools = unpack("<IIII", data, off + 8)
            platform_name = MACHO_PLATFORMS.get(platform, "platform %d" % platform)
            info.facts.append(("info", "platform: %s (sdk %s)" % (platform_name, version_text(packed_version(sdk)))))
            if platform != 1:
                info.facts.append(("FAIL", "LC_BUILD_VERSION platform is not macOS"))
            info.minos = packed_version(minos)
        elif cmd == 0x24 and info.minos is None:  # LC_VERSION_MIN_MACOSX
            info.minos = packed_version(unpack("<I", data, off + 8)[0])
        elif cmd == 0x1D:  # LC_CODE_SIGNATURE
            info.code_signature = True
        off += cmdsize
    if symtab is None:
        raise ParseError("no LC_SYMTAB load command")
    symoff, nsyms, stroff, strsize = symtab
    if stroff + strsize > len(data):
        raise ParseError("string table runs past the end of the file")
    for i in range(nsyms):
        n_strx, n_type, _sect, _desc, _value = unpack("<IBBHQ", data, symoff + i * 16)  # nlist_64
        if n_type & 0xE0 or not n_type & 0x01 or (n_type & 0x0E) != 0x0E:  # N_STAB, N_EXT, N_TYPE == N_SECT
            continue
        if n_strx >= strsize:
            raise ParseError("symbol %d string index 0x%x is past the string table" % (i, n_strx))
        info.exports.append(cstring(data, stroff + n_strx, strsize - n_strx))


# ---------------------------------------------------------------- driver

def detect_format(data):
    """Identify the container from its magic; returns the parser function."""
    if len(data) < 4:
        raise ParseError("file is only %d bytes long" % len(data))
    if data[:4] == b"\x7fELF":
        return parse_elf
    if data[:2] == b"MZ":
        return parse_pe
    magic = struct.unpack_from("<I", data, 0)[0]
    if magic == 0xFEEDFACF:
        return parse_macho
    if magic in (0xCAFEBABE, 0xBEBAFECA):
        raise ParseError("fat (universal) Mach-O binary; a single-architecture dylib is required")
    if magic in (0xFEEDFACE, 0xCEFAEDFE, 0xCFFAEDFE):
        raise ParseError("Mach-O magic 0x%08x is not 64-bit little-endian" % magic)
    raise ParseError("unrecognised file format (not ELF, PE or Mach-O)")


def comparable_exports(info):
    """Export names as the checks see them: Mach-O names lose one leading underscore."""
    if info.fmt == "Mach-O 64":
        return {n[1:] if n.startswith("_") else n for n in info.exports}
    return set(info.exports)


def run_checks(info, args):
    """Combine the parsed facts with the requested checks into (level, message) lines."""
    lines = []
    exp_fmt, exp_arch, exp_kind = RID_EXPECT[args.rid]

    def check(ok, message):
        lines.append(("ok" if ok else "FAIL", message))

    def note(message):
        lines.append(("info", message))

    note("file: %s (%d bytes)" % (args.lib, info.size))
    check(info.fmt == exp_fmt, "format: %s (expected %s for %s)" % (info.fmt, exp_fmt, args.rid))
    check(info.arch == exp_arch, "arch: %s (expected %s)" % (info.arch, exp_arch))
    ext = os.path.splitext(args.lib)[1].lower()
    if exp_fmt == "PE32+" and ext != ".dll":
        note("type: %s (extension %r, IMAGE_FILE_DLL not required)" % (info.kind, ext))
    else:
        check(info.kind == exp_kind, "type: %s (expected %s)" % (info.kind, exp_kind))
    lines.extend(info.facts)
    note("dependencies: %d" % len(info.deps))
    for dep in info.deps:
        note("dependency: " + dep)
    note("exports: %d" % len(info.exports))
    if info.fmt == "ELF64":
        note("glibc max: %s" % (version_text(info.glibc_max) if info.glibc_max else "none"))
        check(not info.glibc_private, "glibc private: %s referenced" % ("GLIBC_PRIVATE" if info.glibc_private else "none"))
    elif info.fmt == "Mach-O 64":
        note("install name: %s" % (info.install_name or "none"))
        note("minos: %s" % (version_text(info.minos) if info.minos else "none"))
        note("code signature: %s" % ("present" if info.code_signature else "absent"))

    names = comparable_exports(info)
    for name in args.require_export:
        check(name in names, "export required: %s%s" % (name, "" if name in names else " (not exported)"))
    for name in args.forbid_export:
        check(name not in names, "export forbidden: %s%s" % (name, " (is exported)" if name in names else ""))
    if args.allow_dep:
        for dep in info.deps:
            ok = any(fnmatch.fnmatchcase(dep.lower(), pat.lower()) for pat in args.allow_dep)
            check(ok, "dependency %s: %s" % ("allowed" if ok else "not allowed", dep))
    if args.max_glibc is not None:
        if info.glibc_max is None:
            check(True, "glibc max: none referenced (limit %s)" % version_text(args.max_glibc))
        else:
            ok = info.glibc_max <= args.max_glibc
            check(ok, "glibc max %s %s %s" % (version_text(info.glibc_max), "<=" if ok else ">", version_text(args.max_glibc)))
    if args.min_macos is not None:
        if info.minos is None:
            check(False, "minos: not recorded (no LC_BUILD_VERSION or LC_VERSION_MIN_MACOSX)")
        else:
            ok = info.minos <= args.min_macos
            check(ok, "minos %s %s %s" % (version_text(info.minos), "<=" if ok else ">", version_text(args.min_macos)))
    return lines


def build_parser():
    """The command line described in the module docstring."""
    p = argparse.ArgumentParser(prog="natives-verify.py",
                                description="Verify the format, architecture, exports and dependencies of a native library.")
    p.add_argument("--rid", required=True, choices=sorted(RID_EXPECT), help="runtime identifier the library was built for")
    p.add_argument("--lib", required=True, metavar="PATH", help="shared library to inspect (never executed)")
    p.add_argument("--require-export", action="append", default=[], metavar="NAME",
                   help="symbol that must be exported (Mach-O names are compared without the leading underscore)")
    p.add_argument("--forbid-export", action="append", default=[], metavar="NAME", help="symbol that must not be exported")
    p.add_argument("--allow-dep", action="append", default=[], metavar="PATTERN",
                   help="case-insensitive fnmatch pattern for an allowed dependency; every dependency must match one")
    p.add_argument("--max-glibc", type=version_tuple, metavar="X.Y", help="linux-x64: highest GLIBC_x.y version allowed")
    p.add_argument("--min-macos", type=version_tuple, metavar="X.Y", help="osx-arm64: the recorded minos must be at most this")
    p.add_argument("--json", action="store_true", help="print one JSON object instead of text lines")
    return p


def main(argv=None):
    parser = build_parser()
    args = parser.parse_args(argv)
    if args.max_glibc is not None and args.rid != "linux-x64":
        parser.error("--max-glibc applies to --rid linux-x64 only")
    if args.min_macos is not None and args.rid != "osx-arm64":
        parser.error("--min-macos applies to --rid osx-arm64 only")
    try:
        with open(args.lib, "rb") as f:  # read only; the library is never loaded or executed
            data = f.read()
    except OSError as e:
        parser.error("cannot read %s: %s" % (args.lib, e.strerror or e))

    info = LibInfo(len(data))
    error = None
    try:
        detect_format(data)(data, info)
        lines = run_checks(info, args)
    except (ParseError, struct.error, IndexError, ValueError) as e:
        error = "malformed file (%s): %s" % (info.fmt, e)
        lines = [("info", "file: %s (%d bytes)" % (args.lib, info.size)), ("FAIL", error)]
    checks = sum(1 for level, _ in lines if level != "info")
    failed = sum(1 for level, _ in lines if level == "FAIL")
    passed = failed == 0

    if args.json:
        print(json.dumps({
            "file": args.lib, "rid": args.rid, "ok": passed, "error": error,
            "format": info.fmt, "arch": info.arch, "kind": info.kind,
            "dependencies": info.deps, "export_count": len(info.exports), "exports": info.exports,
            "glibc_max": version_text(info.glibc_max) if info.glibc_max else None,
            "glibc_private": info.glibc_private,
            "minos": version_text(info.minos) if info.minos else None,
            "install_name": info.install_name, "code_signature": info.code_signature,
            "checks": checks, "failed": failed,
            "lines": [{"level": level, "message": msg} for level, msg in lines],
        }, indent=2))
    else:
        for level, msg in lines:
            print("%s: %s" % (level, msg))
        print("summary: %d checks, %d failed -> %s" % (checks, failed, "PASS" if passed else "FAIL"))
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
