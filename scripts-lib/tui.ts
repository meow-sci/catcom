/**
 * tui.ts - escape-code toolkit for terminal scripts (ksa-scripting).
 *
 * Dependency-free, no DOM, no Node APIs, ES2022 only. Mirrors tui.lua.
 *
 *   import { Screen, parseInput, Button } from "./lib/tui.ts";
 *
 * Conventions
 *  - All coordinates are 1-based: column `x`/`col` 1..cols, row `y`/`row` 1..rows
 *    (matches CUP `CSI row;col H` and SGR mouse reports).
 *  - Widths count Unicode code points (no East-Asian wide / combining handling).
 *  - Input events are decoded from the exact byte streams purrTTY delivers
 *    (legacy xterm + kitty keyboard flag 1 + SGR mouse + engine replies).
 */

// ---------------------------------------------------------------------------
// Public types
// ---------------------------------------------------------------------------

/** The minimal terminal surface the library needs (`ctx.term` satisfies it). */
export interface TermLike {
  write(data: string | Uint8Array): void;
  read?(): Promise<Uint8Array | string | null | undefined>;
}

export interface Mods {
  shift: boolean;
  alt: boolean;
  ctrl: boolean;
  super: boolean;
}

export type MouseButtonName =
  | "left"
  | "middle"
  | "right"
  | "none"
  | "wheelUp"
  | "wheelDown"
  | "wheelLeft"
  | "wheelRight";

export type MouseAction = "press" | "release" | "motion" | "wheel";

export interface KeyInputEvent {
  kind: "key";
  name: string;
  mods: Mods;
  /** Printable text the key produced; '' for named keys and chords. */
  text: string;
}

export interface MouseInputEvent {
  kind: "mouse";
  button: MouseButtonName;
  action: MouseAction;
  col: number;
  row: number;
  mods: Mods;
}

export interface PasteInputEvent {
  kind: "text";
  text: string;
  paste: true;
}

export interface ReplyInputEvent {
  kind: "reply";
  /** Raw bytes of the whole unrecognised / engine-reply sequence. */
  bytes: Uint8Array;
}

export type InputEvent = KeyInputEvent | MouseInputEvent | PasteInputEvent | ReplyInputEvent;

export type ColorName =
  | "black"
  | "red"
  | "green"
  | "yellow"
  | "blue"
  | "magenta"
  | "cyan"
  | "white"
  | "brightBlack"
  | "brightRed"
  | "brightGreen"
  | "brightYellow"
  | "brightBlue"
  | "brightMagenta"
  | "brightCyan"
  | "brightWhite"
  | "default";

/** 0-255 palette index, a ColorName, '#rrggbb', or [r, g, b]. */
export type Color = number | ColorName | `#${string}` | readonly [number, number, number];

export interface Style {
  fg?: Color;
  bg?: Color;
  bold?: boolean;
  dim?: boolean;
  italic?: boolean;
  underline?: boolean;
  inverse?: boolean;
}

export type MouseMode = "none" | "press-release" | "drag" | "any";

export interface EnterAppOptions {
  /** Switch to the alternate screen (default true). */
  altScreen?: boolean;
  /** Hide the cursor (default true). */
  hideCursor?: boolean;
  /** Mouse tracking: 'none' | 'press-release' (1000) | 'drag' (1002) | 'any' (1003); SGR (1006) is added unless 'none'. Default 'press-release'. */
  mouse?: MouseMode;
  /** Bracketed paste (default true). */
  bracketedPaste?: boolean;
  /** Push kitty keyboard flags: true = flags 1, number = that flag set. Default false. */
  kitty?: boolean | number;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Common escape sequences. */
export const SEQ = {
  altScreenOn: "\x1b[?1049h",
  altScreenOff: "\x1b[?1049l",
  clearHome: "\x1b[2J\x1b[H",
  clearLine: "\x1b[2K",
  hideCursor: "\x1b[?25l",
  showCursor: "\x1b[?25h",
  mousePressRelease: "\x1b[?1000h",
  mouseDrag: "\x1b[?1002h",
  mouseAny: "\x1b[?1003h",
  mousePressReleaseOff: "\x1b[?1000l",
  mouseDragOff: "\x1b[?1002l",
  mouseAnyOff: "\x1b[?1003l",
  mouseSgrOn: "\x1b[?1006h",
  mouseSgrOff: "\x1b[?1006l",
  bracketedPasteOn: "\x1b[?2004h",
  bracketedPasteOff: "\x1b[?2004l",
  syncBegin: "\x1b[?2026h",
  syncEnd: "\x1b[?2026l",
  kittyPop: "\x1b[<u",
  resetStyle: "\x1b[0m",
} as const;

/** Kitty keyboard push: `CSI > flags u`. */
export function kittyPush(flags: number): string {
  return "\x1b[>" + Math.floor(flags) + "u";
}

const ESC = 0x1b;

const M_SHIFT = 1;
const M_ALT = 2;
const M_CTRL = 4;
const M_SUPER = 8;

// ---------------------------------------------------------------------------
// UTF-8 helpers (no TextEncoder / TextDecoder in the host)
// ---------------------------------------------------------------------------

/** Encode a JS string as UTF-8 (lone surrogates become U+FFFD). */
export function encodeUtf8(s: string): Uint8Array {
  const out: number[] = [];
  for (let i = 0; i < s.length; i++) {
    let c = s.charCodeAt(i);
    if (c >= 0xd800 && c <= 0xdbff && i + 1 < s.length) {
      const d = s.charCodeAt(i + 1);
      if (d >= 0xdc00 && d <= 0xdfff) {
        c = 0x10000 + ((c - 0xd800) << 10) + (d - 0xdc00);
        i++;
      }
    }
    if (c >= 0xd800 && c <= 0xdfff) c = 0xfffd;
    if (c < 0x80) {
      out.push(c);
    } else if (c < 0x800) {
      out.push(0xc0 | (c >> 6), 0x80 | (c & 0x3f));
    } else if (c < 0x10000) {
      out.push(0xe0 | (c >> 12), 0x80 | ((c >> 6) & 0x3f), 0x80 | (c & 0x3f));
    } else {
      out.push(0xf0 | (c >> 18), 0x80 | ((c >> 12) & 0x3f), 0x80 | ((c >> 6) & 0x3f), 0x80 | (c & 0x3f));
    }
  }
  return Uint8Array.from(out);
}

const U8_PENDING = -1;
const U8_REPROCESS = -2;
const REPLACEMENT = 0xfffd;

/**
 * Incremental, strict UTF-8 decoder (WHATWG-style: overlongs, surrogates and
 * > U+10FFFF are rejected at the offending byte). `step` returns a code point
 * (>= 0) when one is complete, U8_PENDING when more bytes are needed, or
 * U8_REPROCESS when an unfinished sequence was interrupted by `b`: the caller
 * owes one U+FFFD and must call `step(b)` again (b was NOT consumed).
 */
class Utf8Decoder {
  need = 0;
  cp = 0;
  lo = 0x80;
  hi = 0xbf;

  step(b: number): number {
    if (this.need > 0) {
      if (b >= this.lo && b <= this.hi) {
        this.cp = (this.cp << 6) | (b & 0x3f);
        this.need--;
        this.lo = 0x80;
        this.hi = 0xbf;
        return this.need === 0 ? this.cp : U8_PENDING;
      }
      this.need = 0;
      this.lo = 0x80;
      this.hi = 0xbf;
      return U8_REPROCESS;
    }
    if (b < 0x80) return b;
    if (b >= 0xc2 && b <= 0xdf) {
      this.need = 1;
      this.cp = b & 0x1f;
      return U8_PENDING;
    }
    if (b >= 0xe0 && b <= 0xef) {
      this.need = 2;
      this.cp = b & 0x0f;
      if (b === 0xe0) this.lo = 0xa0;
      else if (b === 0xed) this.hi = 0x9f;
      return U8_PENDING;
    }
    if (b >= 0xf0 && b <= 0xf4) {
      this.need = 3;
      this.cp = b & 0x07;
      if (b === 0xf0) this.lo = 0x90;
      else if (b === 0xf4) this.hi = 0x8f;
      return U8_PENDING;
    }
    return REPLACEMENT; // stray continuation, C0 overlong lead, F5..FF
  }

  get pending(): boolean {
    return this.need > 0;
  }

  reset(): void {
    this.need = 0;
    this.lo = 0x80;
    this.hi = 0xbf;
  }
}

/** Decode UTF-8 bytes to a string; invalid sequences become U+FFFD. */
export function decodeUtf8(bytes: ArrayLike<number>, start = 0, end = bytes.length): string {
  const dec = new Utf8Decoder();
  let s = "";
  for (let i = start; i < end; i++) {
    const b = bytes[i];
    let r = dec.step(b);
    if (r === U8_REPROCESS) {
      s += "�";
      r = dec.step(b);
    }
    if (r >= 0) s += String.fromCodePoint(r);
  }
  if (dec.pending) s += "�";
  return s;
}

/** Number of Unicode code points in `s` (the width model used by this library). */
export function strWidth(s: string): number {
  let n = 0;
  for (const _ch of s) n++;
  return n;
}

/** Substring by code point index, `[start, end)`. */
export function strSlice(s: string, start: number, end?: number): string {
  return Array.from(s).slice(start, end).join("");
}

function toInt(n: number): number {
  n = Math.floor(Number(n));
  return Number.isFinite(n) ? n : 0;
}

// ---------------------------------------------------------------------------
// SGR styles
// ---------------------------------------------------------------------------

const COLOR_INDEX = new Map<string, number>([
  ["black", 0],
  ["red", 1],
  ["green", 2],
  ["yellow", 3],
  ["blue", 4],
  ["magenta", 5],
  ["cyan", 6],
  ["white", 7],
]);

function clamp255(v: unknown): number | null {
  if (typeof v !== "number" || !Number.isFinite(v)) return null;
  const n = Math.floor(v);
  return n < 0 ? 0 : n > 255 ? 255 : n;
}

/** SGR parameter text for one colour (no CSI/m), or null when the colour is unknown. */
function colorParam(c: unknown, bg: boolean): string | null {
  if (typeof c === "number") {
    if (!Number.isFinite(c)) return null;
    const n = Math.floor(c);
    if (n < 0 || n > 255) return null;
    return (bg ? "48;5;" : "38;5;") + n;
  }
  if (typeof c === "string") {
    if (c.charCodeAt(0) === 0x23) {
      if (!/^#[0-9a-fA-F]{6}$/.test(c)) return null;
      const r = parseInt(c.slice(1, 3), 16);
      const g = parseInt(c.slice(3, 5), 16);
      const b = parseInt(c.slice(5, 7), 16);
      return (bg ? "48;2;" : "38;2;") + r + ";" + g + ";" + b;
    }
    // accept both brightBlack and bright_black spellings
    const key = c.toLowerCase().replace(/_/g, "");
    if (key === "default") return bg ? "49" : "39";
    let bright = false;
    let base = key;
    if (key.startsWith("bright")) {
      bright = true;
      base = key.slice(6);
    }
    const idx = COLOR_INDEX.get(base);
    if (idx === undefined) return null;
    return String((bright ? (bg ? 100 : 90) : bg ? 40 : 30) + idx);
  }
  if (Array.isArray(c) && c.length >= 3) {
    const r = clamp255(c[0]);
    const g = clamp255(c[1]);
    const b = clamp255(c[2]);
    if (r === null || g === null || b === null) return null;
    return (bg ? "48;2;" : "38;2;") + r + ";" + g + ";" + b;
  }
  return null;
}

/**
 * SGR escape for a style. Always starts with a reset (`0;`) so styles do not
 * accumulate: `{fg:'red', bold:true}` -> `\x1b[0;1;31m`; `{}`/undefined -> `\x1b[0m`.
 */
export function sgr(style?: Style | null): string {
  const p: string[] = ["0"];
  if (style) {
    if (style.bold) p.push("1");
    if (style.dim) p.push("2");
    if (style.italic) p.push("3");
    if (style.underline) p.push("4");
    if (style.inverse) p.push("7");
    if (style.fg !== undefined && style.fg !== null) {
      const f = colorParam(style.fg, false);
      if (f !== null) p.push(f);
    }
    if (style.bg !== undefined && style.bg !== null) {
      const b = colorParam(style.bg, true);
      if (b !== null) p.push(b);
    }
  }
  return "\x1b[" + p.join(";") + "m";
}

// ---------------------------------------------------------------------------
// Screen
// ---------------------------------------------------------------------------

const MOUSE_MODE_NUM: Record<string, number> = {
  "press-release": 1000,
  drag: 1002,
  any: 1003,
};

interface AppState {
  alt: boolean;
  mouseMode: number; // 0 = none
  paste: boolean;
  kitty: boolean;
}

/** Output helper over a terminal. All coordinates are 1-based. */
export class Screen {
  readonly term: TermLike;
  private depth = 0;
  private buf: string[] = [];
  private app: AppState | null = null;

  constructor(term: TermLike) {
    this.term = term;
  }

  private out(s: string): void {
    if (this.depth > 0) this.buf.push(s);
    else this.term.write(s);
  }

  /** Enter "application" mode (alt screen, hidden cursor, mouse, bracketed paste, kitty keys). One write. */
  enterApp(opts: EnterAppOptions = {}): void {
    const altScreen = opts.altScreen ?? true;
    const hideCursor = opts.hideCursor ?? true;
    const mouse = opts.mouse ?? "press-release";
    const paste = opts.bracketedPaste ?? true;
    const kitty = opts.kitty ?? false;
    let kittyFlags = 0;
    if (kitty === true) kittyFlags = 1;
    else if (typeof kitty === "number" && kitty > 0) kittyFlags = Math.floor(kitty);
    const mouseMode = MOUSE_MODE_NUM[mouse] ?? 0;

    let s = this.app ? this.leaveSeq() : ""; // never stack kitty pushes
    if (altScreen) s += SEQ.altScreenOn;
    s += SEQ.clearHome;
    if (hideCursor) s += SEQ.hideCursor;
    if (mouseMode) s += "\x1b[?" + mouseMode + "h" + SEQ.mouseSgrOn;
    if (paste) s += SEQ.bracketedPasteOn;
    if (kittyFlags > 0) s += kittyPush(kittyFlags);
    this.app = { alt: altScreen, mouseMode, paste, kitty: kittyFlags > 0 };
    this.out(s);
  }

  private leaveSeq(): string {
    const a = this.app;
    if (!a) return "";
    let s = "";
    if (a.kitty) s += SEQ.kittyPop;
    if (a.paste) s += SEQ.bracketedPasteOff;
    if (a.mouseMode) s += SEQ.mouseSgrOff + "\x1b[?" + a.mouseMode + "l";
    s += SEQ.resetStyle + SEQ.showCursor;
    if (a.alt) s += SEQ.altScreenOff;
    return s;
  }

  /** Undo enterApp in reverse order, in one write. Idempotent. */
  leaveApp(): void {
    if (!this.app) return;
    const s = this.leaveSeq();
    this.app = null;
    this.out(s);
  }

  /**
   * Run `fn`; everything the Screen draws meanwhile is buffered and written as
   * one `DEC 2026` synchronised update. Nested calls join the outer frame. If
   * `fn` throws, the buffered output is still written and the error rethrown.
   */
  frame<T>(fn: () => T): T {
    if (this.depth > 0) return fn();
    this.depth = 1;
    this.buf = [];
    try {
      return fn();
    } finally {
      this.depth = 0;
      const data = this.buf.join("");
      this.buf = [];
      this.term.write(SEQ.syncBegin + data + SEQ.syncEnd);
    }
  }

  /** Raw text, no cursor movement. */
  write(text: string): void {
    this.out(text);
  }

  /** Move the cursor to column x, row y (1-based, clamped to >= 1). */
  moveTo(x: number, y: number): void {
    this.out(cup(x, y));
  }

  /** Reset style, erase the whole screen and home the cursor. */
  clear(): void {
    this.out("\x1b[0m\x1b[2J\x1b[H");
  }

  clearLine(): void {
    this.out(SEQ.clearLine);
  }

  style(s?: Style | null): void {
    this.out(sgr(s));
  }

  resetStyle(): void {
    this.out(SEQ.resetStyle);
  }

  showCursor(on: boolean): void {
    this.out(on ? SEQ.showCursor : SEQ.hideCursor);
  }

  /** moveTo + optional style + text + reset (when styled). */
  text(x: number, y: number, str: string, style?: Style | null): void {
    this.out(cup(x, y) + (style ? sgr(style) : "") + str + (style ? SEQ.resetStyle : ""));
  }

  hline(x: number, y: number, len: number, ch = "─", style?: Style | null): void {
    len = toInt(len);
    if (len <= 0) return;
    this.out(cup(x, y) + (style ? sgr(style) : "") + ch.repeat(len) + (style ? SEQ.resetStyle : ""));
  }

  vline(x: number, y: number, len: number, ch = "│", style?: Style | null): void {
    len = toInt(len);
    if (len <= 0) return;
    let s = style ? sgr(style) : "";
    for (let i = 0; i < len; i++) s += cup(x, y + i) + ch;
    if (style) s += SEQ.resetStyle;
    this.out(s);
  }

  fill(x: number, y: number, w: number, h: number, ch = " ", style?: Style | null): void {
    w = toInt(w);
    h = toInt(h);
    if (w <= 0 || h <= 0) return;
    const row = ch.repeat(w);
    let s = style ? sgr(style) : "";
    for (let i = 0; i < h; i++) s += cup(x, y + i) + row;
    if (style) s += SEQ.resetStyle;
    this.out(s);
  }

  /** Single-line box. No-op when w < 2 or h < 2. The title (` title `) starts at x+2, truncated to w-4. */
  box(x: number, y: number, w: number, h: number, title?: string | null, style?: Style | null): void {
    w = toInt(w);
    h = toInt(h);
    if (w < 2 || h < 2) return;
    let top: string;
    const room = w - 4;
    if (title && room > 0) {
      const t = strSlice(" " + title + " ", 0, room);
      top = "┌─" + t + "─".repeat(w - 3 - strWidth(t)) + "┐";
    } else {
      top = "┌" + "─".repeat(w - 2) + "┐";
    }
    let s = style ? sgr(style) : "";
    s += cup(x, y) + top;
    for (let i = 1; i < h - 1; i++) s += cup(x, y + i) + "│" + cup(x + w - 1, y + i) + "│";
    s += cup(x, y + h - 1) + "└" + "─".repeat(w - 2) + "┘";
    if (style) s += SEQ.resetStyle;
    this.out(s);
  }
}

function cup(x: number, y: number): string {
  let col = toInt(x);
  let row = toInt(y);
  if (col < 1) col = 1;
  if (row < 1) row = 1;
  return "\x1b[" + row + ";" + col + "H";
}

// ---------------------------------------------------------------------------
// Input parser
// ---------------------------------------------------------------------------

function modsOf(bits: number): Mods {
  return {
    shift: (bits & M_SHIFT) !== 0,
    alt: (bits & M_ALT) !== 0,
    ctrl: (bits & M_CTRL) !== 0,
    super: (bits & M_SUPER) !== 0,
  };
}

function keyEv(name: string, bits: number, text: string): KeyInputEvent {
  return { kind: "key", name, mods: modsOf(bits), text };
}

/** xterm modifier parameter m -> bit set (1 = shift, 2 = alt, 4 = ctrl, 8 = super); higher bits ignored. */
function modBits(m: number | null | undefined): number {
  if (m === null || m === undefined || m < 1) return 0;
  return (m - 1) & 15;
}

function charKey(cp: number, bits: number): KeyInputEvent {
  const ch = String.fromCodePoint(cp);
  return keyEv(ch, bits, (bits & M_ALT) !== 0 ? "" : ch);
}

/** Key for a single byte < 0x80 other than ESC, optionally with extra modifier bits (alt). */
function asciiKey(b: number, extra: number): KeyInputEvent {
  if (b >= 0x20 && b < 0x7f) {
    if (b === 0x20) return keyEv("space", extra, extra === 0 ? " " : "");
    const ch = String.fromCharCode(b);
    return keyEv(ch, extra, extra === 0 ? ch : "");
  }
  if (b === 0x0d || b === 0x0a) return keyEv("enter", extra, "");
  if (b === 0x09) return keyEv("tab", extra, "");
  if (b === 0x7f) return keyEv("backspace", extra, "");
  if (b === 0x08) return keyEv("backspace", extra | M_CTRL, "");
  if (b === 0x00) return keyEv("space", extra | M_CTRL, "");
  if (b >= 0x01 && b <= 0x1a) return keyEv(String.fromCharCode(b + 96), extra | M_CTRL, "");
  // 0x1c..0x1f
  return keyEv("\\]^_".charAt(b - 0x1c), extra | M_CTRL, "");
}

function buttonOf(low: number): MouseButtonName {
  return low === 0 ? "left" : low === 1 ? "middle" : low === 2 ? "right" : "none";
}

const WHEEL_BUTTONS: MouseButtonName[] = ["wheelUp", "wheelDown", "wheelLeft", "wheelRight"];

function mouseEvent(cb: number, col: number, row: number, release: boolean, x10: boolean): MouseInputEvent {
  let bits = 0;
  if ((cb & 4) !== 0) bits |= M_SHIFT;
  if ((cb & 8) !== 0) bits |= M_ALT;
  if ((cb & 16) !== 0) bits |= M_CTRL;
  const low = cb & 3;
  let button: MouseButtonName;
  let action: MouseAction;
  if ((cb & 64) !== 0) {
    action = "wheel";
    button = WHEEL_BUTTONS[low];
  } else if ((cb & 32) !== 0) {
    action = "motion";
    button = buttonOf(low);
  } else if (x10) {
    if (low === 3) {
      action = "release";
      button = "none";
    } else {
      action = "press";
      button = buttonOf(low);
    }
  } else {
    action = release ? "release" : "press";
    button = buttonOf(low);
  }
  return { kind: "mouse", button, action, col, row, mods: modsOf(bits) };
}

type Field = Array<number | null>;

interface ParsedCsi {
  marker: number;
  fields: Field[];
  inter: boolean;
  final: number;
  ok: boolean;
}

/** Parse the bytes following `ESC [` (the last one is the final byte). */
function parseCsi(p: number[]): ParsedCsi {
  const last = p.length - 1;
  const final = p[last];
  let idx = 0;
  let marker = 0;
  if (last > 0 && p[0] >= 0x3c && p[0] <= 0x3f) {
    marker = p[0];
    idx = 1;
  }
  const fields: Field[] = [];
  let cur: Field = [];
  let num = -1;
  let digits = 0;
  let sawParam = false;
  let ok = true;
  for (; idx < last; idx++) {
    const c = p[idx];
    if (c >= 0x30 && c <= 0x39) {
      num = (num < 0 ? 0 : num) * 10 + (c - 0x30);
      digits++;
      if (digits > 9) ok = false;
      sawParam = true;
    } else if (c === 0x3b) {
      cur.push(num < 0 ? null : num);
      fields.push(cur);
      cur = [];
      num = -1;
      digits = 0;
      sawParam = true;
    } else if (c === 0x3a) {
      cur.push(num < 0 ? null : num);
      num = -1;
      digits = 0;
      sawParam = true;
    } else {
      break;
    }
  }
  if (sawParam) {
    cur.push(num < 0 ? null : num);
    fields.push(cur);
  }
  let inter = false;
  for (; idx < last; idx++) {
    const c = p[idx];
    if (c >= 0x20 && c <= 0x2f) inter = true;
    else ok = false;
  }
  return { marker, fields, inter, final, ok };
}

function keyWithMods(name: string, f: Field | undefined, extra: number): KeyInputEvent | null {
  const m = f ? f[0] : null;
  const evt = f && f.length > 1 ? f[1] : null;
  if (evt === 3) return null; // key release: drop
  return keyEv(name, modBits(m) | extra, "");
}

function tildeName(n: number): string | undefined {
  switch (n) {
    case 1:
    case 7:
      return "home";
    case 2:
      return "insert";
    case 3:
      return "delete";
    case 4:
    case 8:
      return "end";
    case 5:
      return "pageUp";
    case 6:
      return "pageDown";
    case 11:
    case 12:
    case 13:
    case 14:
    case 15:
      return "f" + (n - 10);
    case 17:
    case 18:
    case 19:
    case 20:
    case 21:
      return "f" + (n - 11);
    case 23:
      return "f11";
    case 24:
      return "f12";
    default:
      return undefined;
  }
}

function validCodePoint(cp: number): boolean {
  return cp > 0 && cp <= 0x10ffff && !(cp >= 0xd800 && cp <= 0xdfff);
}

/** CSI ... u (kitty keyboard). undefined = unrecognised, null = drop. */
function kittyKey(fields: Field[]): InputEvent | null | undefined {
  if (fields.length < 1 || fields.length > 3) return undefined;
  const codeF = fields[0];
  const code = codeF[0];
  if (code === null || code === undefined || !validCodePoint(code)) return undefined;
  const shifted = codeF.length > 1 ? codeF[1] : null;
  const modF = fields.length > 1 ? fields[1] : undefined;
  const evt = modF && modF.length > 1 ? modF[1] : null;
  if (evt === 3) return null;
  const bits = modBits(modF ? modF[0] : null);
  let name: string;
  let printable = false;
  if (code === 13) name = "enter";
  else if (code === 9) name = "tab";
  else if (code === 127) name = "backspace";
  else if (code === 27) name = "escape";
  else if (code === 32) {
    name = "space";
    printable = true;
  } else if (code >= 57344 && code <= 63743) name = "kitty:" + code;
  else {
    name = String.fromCodePoint(code);
    printable = code >= 0x20 && !(code >= 0x7f && code <= 0x9f);
  }
  let text = "";
  if (printable && (bits & (M_CTRL | M_ALT | M_SUPER)) === 0) {
    const tf = fields.length > 2 ? fields[2] : undefined;
    let assoc = "";
    if (tf) {
      for (const cp of tf) {
        if (cp !== null && validCodePoint(cp)) assoc += String.fromCodePoint(cp);
      }
    }
    if (assoc !== "") text = assoc;
    else if (shifted !== null && shifted !== undefined && validCodePoint(shifted)) text = String.fromCodePoint(shifted);
    else text = code === 32 ? " " : name;
  }
  return keyEv(name, bits, text);
}

const ST_GROUND = 0;
const ST_UTF8 = 1;
const ST_CSI = 2;
const ST_X10 = 3;
const ST_STR = 4;
const ST_PASTE = 5;

const PASTE_END = [0x1b, 0x5b, 0x32, 0x30, 0x31, 0x7e]; // ESC [ 2 0 1 ~

export interface InputParserOptions {
  /** A CSI that reaches this many bytes (including `ESC [`) without a final byte is emitted as a reply. Default 256. */
  maxCsiLength?: number;
  /** An OSC/DCS/APC/PM/SOS string that reaches this many bytes without a terminator is emitted as a reply. Default 65536. */
  maxStringLength?: number;
}

/**
 * Incremental byte-level input decoder. Feed it whatever chunks the terminal
 * delivers (a chunk may end anywhere; one chunk is not one key).
 */
export class InputParser {
  private st = ST_GROUND;
  private dec = new Utf8Decoder();
  private decAlt = false;
  private pend: number[] = [];
  private strKind = 0;
  private strEsc = false;
  private cpr = 0;
  private readonly maxCsi: number;
  private readonly maxStr: number;

  constructor(opts: InputParserOptions = {}) {
    this.maxCsi = opts.maxCsiLength ?? 256;
    this.maxStr = opts.maxStringLength ?? 65536;
  }

  /** Expect `n` cursor-position reports (`CSI row;col R`); the next n such sequences are replies, not F3. Adds to any pending count. */
  expectCpr(n = 1): void {
    if (n > 0) this.cpr += Math.floor(n);
  }

  /** Decode a chunk (bytes, or text which is UTF-8 encoded first) into events. */
  feed(chunk: Uint8Array | ArrayLike<number> | string): InputEvent[] {
    const bytes: ArrayLike<number> = typeof chunk === "string" ? encodeUtf8(chunk) : chunk;
    const out: InputEvent[] = [];
    const n = bytes.length;
    let i = 0;
    while (i < n) {
      switch (this.st) {
        case ST_GROUND:
          i = this.onGround(bytes, i, n, out);
          break;
        case ST_UTF8:
          i = this.onUtf8(bytes[i], i, out);
          break;
        case ST_CSI:
          i = this.onCsi(bytes[i], i, out);
          break;
        case ST_X10:
          i = this.onX10(bytes[i], i, out);
          break;
        case ST_STR:
          i = this.onString(bytes[i], i, out);
          break;
        default:
          i = this.onPaste(bytes[i], i, out);
          break;
      }
    }
    return out;
  }

  /**
   * Resolve any pending partial state as if the stream paused, and return to
   * the ground state: partial UTF-8 -> U+FFFD key, partial CSI / string -> a
   * reply with the raw bytes, a paste in progress -> a text event with what
   * arrived so far.
   */
  flush(): InputEvent[] {
    const out: InputEvent[] = [];
    switch (this.st) {
      case ST_UTF8:
        out.push(charKey(REPLACEMENT, this.decAlt ? M_ALT : 0));
        this.dec.reset();
        break;
      case ST_CSI:
        out.push(this.csiReply());
        break;
      case ST_X10:
        out.push(this.rawReply([ESC, 0x5b, 0x4d], this.pend));
        break;
      case ST_STR:
        out.push(this.rawReply([], this.pend));
        break;
      case ST_PASTE:
        out.push({ kind: "text", text: decodeUtf8(this.pend), paste: true });
        break;
      default:
        break;
    }
    this.st = ST_GROUND;
    this.pend.length = 0;
    this.strEsc = false;
    return out;
  }

  // -- reply helpers --------------------------------------------------------

  private rawReply(prefix: number[], body: number[]): ReplyInputEvent {
    const bytes = new Uint8Array(prefix.length + body.length);
    for (let k = 0; k < prefix.length; k++) bytes[k] = prefix[k];
    for (let k = 0; k < body.length; k++) bytes[prefix.length + k] = body[k];
    return { kind: "reply", bytes };
  }

  /** Reply for the CSI collected so far (prefix `ESC [` + pend). */
  private csiReply(): ReplyInputEvent {
    return this.rawReply([ESC, 0x5b], this.pend);
  }

  private toGround(): void {
    this.st = ST_GROUND;
    this.pend.length = 0;
    this.strEsc = false;
  }

  // -- ground ---------------------------------------------------------------

  private onGround(bytes: ArrayLike<number>, i: number, n: number, out: InputEvent[]): number {
    const b = bytes[i];
    if (b === ESC) {
      if (i + 1 >= n) {
        out.push(keyEv("escape", 0, ""));
        return i + 1;
      }
      const c = bytes[i + 1];
      switch (c) {
        case 0x5b: // [
          if (i + 2 >= n) {
            out.push(keyEv("[", M_ALT, ""));
            return i + 2;
          }
          this.st = ST_CSI;
          this.pend.length = 0;
          return i + 2;
        case 0x4f: // O
          if (i + 2 >= n) {
            out.push(keyEv("O", M_ALT, ""));
            return i + 2;
          }
          out.push(this.ss3(bytes[i + 2]));
          return i + 3;
        case 0x5d: // ]
        case 0x50: // P
        case 0x5f: // _
        case 0x5e: // ^
        case 0x58: // X
          this.st = ST_STR;
          this.strKind = c;
          this.strEsc = false;
          this.pend.length = 0;
          this.pend.push(ESC, c);
          return i + 2;
        case ESC:
          out.push(keyEv("escape", 0, ""));
          return i + 1; // reprocess the second ESC
        default:
          if (c < 0x80) {
            out.push(asciiKey(c, M_ALT));
          } else {
            this.decAlt = true;
            this.startUtf8(c, out);
          }
          return i + 2;
      }
    }
    if (b < 0x80) {
      out.push(asciiKey(b, 0));
      return i + 1;
    }
    this.decAlt = false;
    this.startUtf8(b, out);
    return i + 1;
  }

  private ss3(b: number): InputEvent {
    switch (b) {
      case 0x41:
        return keyEv("up", 0, "");
      case 0x42:
        return keyEv("down", 0, "");
      case 0x43:
        return keyEv("right", 0, "");
      case 0x44:
        return keyEv("left", 0, "");
      case 0x48:
        return keyEv("home", 0, "");
      case 0x46:
        return keyEv("end", 0, "");
      case 0x50:
        return keyEv("f1", 0, "");
      case 0x51:
        return keyEv("f2", 0, "");
      case 0x52:
        return keyEv("f3", 0, "");
      case 0x53:
        return keyEv("f4", 0, "");
      case 0x4d:
        return keyEv("enter", 0, "");
      default:
        return this.rawReply([ESC, 0x4f, b], []);
    }
  }

  // -- UTF-8 ----------------------------------------------------------------

  /** First byte (>= 0x80) of a multi-byte character, from ground. */
  private startUtf8(b: number, out: InputEvent[]): void {
    const r = this.dec.step(b);
    if (r === U8_PENDING) this.st = ST_UTF8;
    else out.push(charKey(r, this.decAlt ? M_ALT : 0)); // invalid lead -> U+FFFD
  }

  private onUtf8(b: number, i: number, out: InputEvent[]): number {
    const r = this.dec.step(b);
    if (r === U8_PENDING) return i + 1;
    this.st = ST_GROUND;
    if (r === U8_REPROCESS) {
      out.push(charKey(REPLACEMENT, this.decAlt ? M_ALT : 0));
      return i; // reprocess b from ground
    }
    out.push(charKey(r, this.decAlt ? M_ALT : 0));
    return i + 1;
  }

  // -- CSI ------------------------------------------------------------------

  private onCsi(b: number, i: number, out: InputEvent[]): number {
    if (b < 0x20 || b > 0x7e) {
      // ESC or any other control / 8-bit byte aborts the sequence; it is reprocessed from ground.
      out.push(this.csiReply());
      this.toGround();
      return i;
    }
    this.pend.push(b);
    if (b >= 0x40) {
      this.csiFinal(out);
    } else if (this.pend.length + 2 >= this.maxCsi) {
      out.push(this.csiReply());
      this.toGround();
    }
    return i + 1;
  }

  private csiFinal(out: InputEvent[]): void {
    const p = this.pend;
    if (p.length === 1 && p[0] === 0x4d) {
      // CSI M with no params / marker: legacy X10 mouse, three raw bytes follow.
      p.length = 0;
      this.st = ST_X10;
      return;
    }
    this.st = ST_GROUND;
    const ev = this.decodeCsi(p);
    if (ev !== null) out.push(ev);
    if (this.st === ST_GROUND) p.length = 0;
  }

  /** Map a complete CSI to an event; null = drop silently. May switch to paste mode. */
  private decodeCsi(p: number[]): InputEvent | null {
    const c = parseCsi(p);
    if (!c.ok) return this.csiReply();
    const fields = c.fields;
    const final = c.final;
    if (c.marker === 0x3c) {
      // '<'
      if (!c.inter && (final === 0x4d || final === 0x6d) && fields.length === 3) {
        const f0 = fields[0];
        const f1 = fields[1];
        const f2 = fields[2];
        if (f0.length === 1 && f1.length === 1 && f2.length === 1) {
          const cb = f0[0];
          const x = f1[0];
          const y = f2[0];
          if (cb !== null && x !== null && y !== null) return mouseEvent(cb, x, y, final === 0x6d, false);
        }
      }
      return this.csiReply();
    }
    if (c.marker !== 0 || c.inter) return this.csiReply();

    let ev: InputEvent | null | undefined;
    switch (final) {
      case 0x75: // u  (kitty)
        ev = kittyKey(fields);
        break;
      case 0x7e: // ~
        ev = this.tildeKey(fields);
        break;
      case 0x41:
      case 0x42:
      case 0x43:
      case 0x44:
      case 0x48:
      case 0x46:
      case 0x50:
      case 0x51:
      case 0x53:
      case 0x5a: {
        if (fields.length !== 0 && fields.length !== 2) {
          ev = undefined;
          break;
        }
        const name =
          final === 0x41
            ? "up"
            : final === 0x42
              ? "down"
              : final === 0x43
                ? "right"
                : final === 0x44
                  ? "left"
                  : final === 0x48
                    ? "home"
                    : final === 0x46
                      ? "end"
                      : final === 0x50
                        ? "f1"
                        : final === 0x51
                          ? "f2"
                          : final === 0x53
                            ? "f4"
                            : "tab";
        ev = keyWithMods(name, fields[1], final === 0x5a ? M_SHIFT : 0);
        break;
      }
      case 0x52: // R: F3 or cursor position report
        if (fields.length === 0) {
          ev = keyEv("f3", 0, "");
        } else if (fields.length === 2) {
          if (this.cpr > 0) {
            this.cpr--;
            ev = undefined;
          } else if (fields[0][0] === 1) {
            ev = keyWithMods("f3", fields[1], 0);
          } else {
            ev = undefined;
          }
        } else {
          ev = undefined;
        }
        break;
      default:
        ev = undefined;
        break;
    }
    if (ev === undefined) return this.csiReply();
    return ev;
  }

  private tildeKey(fields: Field[]): InputEvent | null | undefined {
    if (fields.length < 1 || fields.length > 2) return undefined;
    const n = fields[0][0];
    if (n === null || n === undefined) return undefined;
    if (n === 200 && fields.length === 1 && fields[0].length === 1) {
      this.pend.length = 0;
      this.st = ST_PASTE;
      return null;
    }
    const name = tildeName(n);
    if (name === undefined) return undefined;
    return keyWithMods(name, fields[1], 0);
  }

  // -- X10 mouse ------------------------------------------------------------

  private onX10(b: number, i: number, out: InputEvent[]): number {
    if (b < 0x20) {
      out.push(this.rawReply([ESC, 0x5b, 0x4d], this.pend));
      this.toGround();
      return i;
    }
    this.pend.push(b);
    if (this.pend.length === 3) {
      const p = this.pend;
      out.push(mouseEvent(p[0] - 32, p[1] - 32, p[2] - 32, false, true));
      this.toGround();
    }
    return i + 1;
  }

  // -- OSC / DCS / APC / PM / SOS -------------------------------------------

  private onString(b: number, i: number, out: InputEvent[]): number {
    this.pend.push(b);
    // ST is ESC \ ; a lone ESC not followed by '\' stays part of the string. BEL also ends an OSC.
    const st = this.strEsc && b === 0x5c;
    const bel = b === 0x07 && this.strKind === 0x5d;
    this.strEsc = b === ESC;
    if (st || bel) {
      out.push(this.rawReply([], this.pend));
      this.toGround();
      return i + 1;
    }
    if (this.pend.length >= this.maxStr) {
      out.push(this.rawReply([], this.pend));
      this.toGround();
    }
    return i + 1;
  }

  // -- bracketed paste ------------------------------------------------------

  private onPaste(b: number, i: number, out: InputEvent[]): number {
    const p = this.pend;
    p.push(b);
    if (b === 0x7e && p.length >= 6) {
      const o = p.length - 6;
      let match = true;
      for (let k = 0; k < 6; k++) {
        if (p[o + k] !== PASTE_END[k]) {
          match = false;
          break;
        }
      }
      if (match) {
        out.push({ kind: "text", text: decodeUtf8(p, 0, o), paste: true });
        this.toGround();
      }
    }
    return i + 1;
  }
}

/**
 * Async iterator over decoded input events. Ends when `term.read()` resolves
 * null/undefined (terminal cancelled or closed).
 */
export async function* parseInput(term: TermLike, parser: InputParser = new InputParser()): AsyncGenerator<InputEvent> {
  if (typeof term.read !== "function") throw new Error("tui.parseInput: term.read() is not available");
  for (;;) {
    const chunk = await term.read();
    if (chunk === null || chunk === undefined) return;
    const evs = parser.feed(chunk);
    for (let k = 0; k < evs.length; k++) yield evs[k];
  }
}

// ---------------------------------------------------------------------------
// Widgets (plain objects, no layout engine)
// ---------------------------------------------------------------------------

export class Rect {
  x: number;
  y: number;
  w: number;
  h: number;

  constructor(x: number, y: number, w: number, h: number) {
    this.x = x;
    this.y = y;
    this.w = w;
    this.h = h;
  }

  /** Inclusive x..x+w-1, y..y+h-1. */
  contains(col: number, row: number): boolean {
    return col >= this.x && col <= this.x + this.w - 1 && row >= this.y && row <= this.y + this.h - 1;
  }
}

/** A left-button release: the event the helpers treat as a click (the focusing press on an in-world quad is ignored). */
export function isClick(ev: InputEvent): boolean {
  return ev.kind === "mouse" && ev.action === "release" && ev.button === "left";
}

export interface LabelOptions {
  x: number;
  y: number;
  text: string;
  style?: Style | undefined;
}

export class Label {
  x: number;
  y: number;
  text: string;
  style?: Style | undefined;

  constructor(o: LabelOptions) {
    this.x = o.x;
    this.y = o.y;
    this.text = o.text;
    this.style = o.style;
  }

  draw(screen: Screen): void {
    screen.text(this.x, this.y, this.text, this.style);
  }
}

/** `s` padded with spaces to exactly `w` code points, centred (extra space goes right). */
function centre(s: string, w: number): string {
  const pad = w - strWidth(s);
  if (pad <= 0) return s;
  const left = Math.floor(pad / 2);
  return " ".repeat(left) + s + " ".repeat(pad - left);
}

export interface ButtonOptions {
  x: number;
  y: number;
  w: number;
  h?: number;
  label: string;
  style?: Style | undefined;
  activeStyle?: Style | undefined;
}

export class Button {
  x: number;
  y: number;
  w: number;
  h: number;
  label: string;
  style?: Style | undefined;
  activeStyle?: Style | undefined;

  constructor(o: ButtonOptions) {
    this.x = o.x;
    this.y = o.y;
    this.w = o.w;
    this.h = o.h ?? 1;
    this.label = o.label;
    this.style = o.style;
    this.activeStyle = o.activeStyle;
  }

  get rect(): Rect {
    return new Rect(this.x, this.y, this.w, this.h);
  }

  /** h < 3: one line `[ label ]` centred in w; h >= 3: a box with the label centred inside. */
  draw(screen: Screen, active = false): void {
    const w = toInt(this.w);
    const h = toInt(this.h);
    if (w <= 0 || h <= 0) return;
    const st = active ? (this.activeStyle ?? { ...(this.style ?? {}), inverse: true }) : this.style;
    if (h >= 3) {
      screen.fill(this.x + 1, this.y + 1, w - 2, h - 2, " ", st);
      screen.box(this.x, this.y, w, h, null, st);
      const inner = w - 2;
      if (inner > 0) {
        const lab = strSlice(this.label, 0, inner);
        const left = Math.floor((inner - strWidth(lab)) / 2);
        screen.text(this.x + 1 + left, this.y + 1 + Math.floor((h - 3) / 2), lab, st);
      }
    } else {
      let s = "[ " + this.label + " ]";
      if (strWidth(s) > w) s = strSlice("[ " + strSlice(this.label, 0, Math.max(0, w - 4)) + " ]", 0, w);
      screen.text(this.x, this.y, centre(s, w), st);
    }
  }

  /** True for a left-button release inside the button's rect. */
  hit(ev: InputEvent): boolean {
    return ev.kind === "mouse" && isClick(ev) && this.rect.contains(ev.col, ev.row);
  }
}

export interface ToggleOptions {
  x: number;
  y: number;
  label: string;
  value?: boolean;
  style?: Style | undefined;
}

export class Toggle {
  x: number;
  y: number;
  label: string;
  value: boolean;
  style?: Style | undefined;

  constructor(o: ToggleOptions) {
    this.x = o.x;
    this.y = o.y;
    this.label = o.label;
    this.value = o.value ?? false;
    this.style = o.style;
  }

  get rect(): Rect {
    return new Rect(this.x, this.y, 4 + strWidth(this.label), 1);
  }

  draw(screen: Screen): void {
    screen.text(this.x, this.y, (this.value ? "[x] " : "[ ] ") + this.label, this.style);
  }

  /** On a left release inside the toggle: flips `value` and returns true. */
  hit(ev: InputEvent): boolean {
    if (ev.kind === "mouse" && isClick(ev) && this.rect.contains(ev.col, ev.row)) {
      this.value = !this.value;
      return true;
    }
    return false;
  }
}

export interface ProgressBarOptions {
  x: number;
  y: number;
  w: number;
  value?: number;
  style?: Style | undefined;
}

const PARTIAL_BLOCKS = ["▏", "▎", "▍", "▌", "▋", "▊", "▉"];

export class ProgressBar {
  x: number;
  y: number;
  w: number;
  value: number;
  style?: Style | undefined;

  constructor(o: ProgressBarOptions) {
    this.x = o.x;
    this.y = o.y;
    this.w = o.w;
    this.value = o.value ?? 0;
    this.style = o.style;
  }

  draw(screen: Screen): void {
    const w = toInt(this.w);
    if (w <= 0) return;
    let v = Number(this.value);
    if (!(v > 0)) v = 0;
    if (v > 1) v = 1;
    const eighths = Math.min(w * 8, Math.floor(v * w * 8 + 1e-9));
    const full = Math.floor(eighths / 8);
    const part = eighths % 8;
    let s = "█".repeat(full);
    if (part > 0) s += PARTIAL_BLOCKS[part - 1];
    s += " ".repeat(w - full - (part > 0 ? 1 : 0));
    screen.text(this.x, this.y, s, this.style);
  }
}
