# scripts-lib: terminal helpers (`tui.ts`, `tui.lua`)

Small, dependency-free helpers for **terminal scripts** (scripts whose context carries a `term`, see `ctx.term`). They are an **escape-code toolkit, not a widget framework**: a `Screen` that emits VT sequences, an incremental **input parser** that turns the raw byte stream purrTTY delivers into typed events, and a handful of plain-object widgets. There is no layout engine, no focus model and no event loop of its own; your script owns the loop.

| File | Language | Runtime |
|---|---|---|
| `tui.ts` | TypeScript (ES2022 module, imports nothing) | V8 / ClearScript, no DOM, no Node APIs, no `TextEncoder` |
| `tui.lua` | Lua 5.1 dialect | LuaJIT 2.1 sandbox (uses only `type tostring tonumber setmetatable pcall error unpack ipairs pairs`, `math`, `string`, `table`) |

The two files expose the same features and the same event shapes. Differences are only naming (camelCase vs snake_case) and the Lua object syntax (`Class.new(...)`, `obj:method()`).

> **Do not edit these files in place.** The mod copies `scripts-lib/` to `<user>/scripts/lib/` **and overwrites it on every start**. If you want to change something, copy the file next to your script (for example `my_tui.ts`) and import the copy.

## Importing

```ts
import { Screen, parseInput, Button } from "./lib/tui.ts";   // "./lib/tui" works too
```

```lua
local tui = require("lib.tui")   -- returns one table: tui.Screen, tui.parse_input, tui.Button, ...
```

Both libraries wrap a minimal terminal interface, which `ctx.term` satisfies:

* TypeScript: `interface TermLike { write(data: string | Uint8Array): void; read?(): Promise<Uint8Array | string | null | undefined> }`
* Lua: a table with `write` and `read` functions, called with **dot** syntax (`term.write(s)`, `term.read()`), not `term:write(s)`. `term.read()` yields the calling coroutine until input arrives and returns a byte string, or `nil` when the terminal is cancelled or closed.

## Conventions

* **Coordinates are 1-based everywhere**: column `x`/`col` is `1..cols`, row `y`/`row` is `1..rows`. This matches CUP (`CSI row;col H`) and SGR mouse reports, so a mouse event's `col`/`row` can be used directly as a draw position or hit-test coordinate.
* **Width = number of Unicode code points.** There is no East-Asian-wide or combining-character handling; box-drawing and block elements (which render identically in 2D windows and on in-world quads) are single-width. Lua strings are UTF-8 byte strings; `#s` counts bytes, use `tui.str_width(s)` for the code point count.
* Size-dependent drawing must be redone when the grid changes (the first real size can arrive after start, and in-world grids can be larger than requested): subscribe with `term.on("resize", redraw)` and read `term.size` (`{cols, rows}`).

## Example (door panel)

**TypeScript**

```ts
import { Screen, parseInput, Button } from "./lib/tui.ts";

export async function main(ctx: any) {
  const term = ctx.term;
  const screen = new Screen(term);
  const door = new Button({ x: 3, y: 3, w: 18, label: "OPEN DOOR" });
  let open = false;

  const draw = () =>
    screen.frame(() => {
      screen.clear();
      screen.box(1, 1, term.size.cols, term.size.rows, "DOOR CONTROL");
      door.draw(screen, open);
      screen.text(3, 5, open ? "door is open" : "door is closed", { fg: open ? "green" : "red" });
      screen.text(3, term.size.rows - 1, "click the button, q to quit", { dim: true });
    });

  screen.enterApp({ mouse: "press-release" });
  term.on("resize", draw);
  draw();
  try {
    for await (const ev of parseInput(term)) {       // ends when the terminal is cancelled/closed
      if (ev.kind === "mouse" && door.hit(ev)) {      // fires on left-button RELEASE
        open = !open;                                 // ...call the vessel action here...
        draw();
      } else if (ev.kind === "key" && (ev.name === "q" || (ev.name === "c" && ev.mods.ctrl))) {
        break;
      }
    }
  } finally {
    screen.leaveApp();
  }
}
```

**Lua**

```lua
local tui = require("lib.tui")

local M = {}

function M.main(ctx)
  local term = ctx.term
  local screen = tui.Screen.new(term)
  local door = tui.Button.new({ x = 3, y = 3, w = 18, label = "OPEN DOOR" })
  local open = false

  local function draw()
    screen:frame(function()
      screen:clear()
      screen:box(1, 1, term.size.cols, term.size.rows, "DOOR CONTROL")
      door:draw(screen, open)
      screen:text(3, 5, open and "door is open" or "door is closed", { fg = open and "green" or "red" })
      screen:text(3, term.size.rows - 1, "click the button, q to quit", { dim = true })
    end)
  end

  screen:enter_app({ mouse = "press-release" })
  term.on("resize", draw)
  draw()
  for ev in tui.parse_input(term) do                  -- yields in term.read(); ends on cancel/close
    if ev.kind == "mouse" and door:hit(ev) then       -- fires on left-button RELEASE
      open = not open                                 -- ...call the vessel action here...
      draw()
    elseif ev.kind == "key" and (ev.name == "q" or (ev.name == "c" and ev.mods.ctrl)) then
      break
    end
  end
  screen:leave_app()
end

return M
```

## Screen

`new Screen(term)` / `tui.Screen.new(term)`. Drawing methods write immediately, or into the current frame (below).

| TypeScript | Lua | Effect |
|---|---|---|
| `enterApp(opts?)` | `enter_app(opts)` | One write, in this order: alt screen `ESC[?1049h`, clear `ESC[2J ESC[H`, hide cursor `ESC[?25l`, mouse mode + SGR `ESC[?1006h` (unless `mouse` is `'none'`), bracketed paste `ESC[?2004h`, kitty push `ESC[>{flags}u`. Remembers what it enabled. |
| `leaveApp()` | `leave_app()` | One write, reverse order: kitty pop `ESC[<u` (if pushed), `ESC[?2004l`, `ESC[?1006l` + the mouse mode `l`, `ESC[0m`, show cursor `ESC[?25h`, leave alt screen `ESC[?1049l`. Idempotent: the second call (or a call before `enterApp`) writes nothing. |
| `frame(fn)` | `frame(fn)` | Buffers everything the Screen draws while `fn` runs, then does a single `term.write("ESC[?2026h" + buffer + "ESC[?2026l")` (DEC 2026 synchronised update). Nested `frame` calls join the outer frame (no nested markers). If `fn` throws/raises the buffered output is still written and the error is rethrown. Returns `fn`'s result. `fn` must be synchronous (do not `await`/yield inside it). |
| `write(text)` | `write(text)` | Raw text, no cursor movement. |
| `moveTo(x, y)` | `move_to(x, y)` | `ESC[{y};{x}H`; both clamped to at least 1. |
| `clear()` | `clear()` | `ESC[0m ESC[2J ESC[H`. |
| `clearLine()` | `clear_line()` | `ESC[2K`. |
| `style(s)` / `resetStyle()` | `style(s)` / `reset_style()` | Writes the SGR for a style / `ESC[0m`. |
| `text(x, y, str, style?)` | `text(x, y, str, style)` | moveTo + optional style + text + reset (when styled). |
| `hline(x, y, len, ch='─', style?)` | `hline(...)` | Horizontal run. `len <= 0` is a no-op. |
| `vline(x, y, len, ch='│', style?)` | `vline(...)` | Vertical run. |
| `fill(x, y, w, h, ch=' ', style?)` | `fill(...)` | Rectangle of `ch`. |
| `box(x, y, w, h, title?, style?)` | `box(...)` | Single-line box `┌ ┐ └ ┘ ─ │`; `w < 2` or `h < 2` is a no-op. The title is drawn as ` title ` starting at `x+2` on the top border, truncated to `w-4` code points. The interior is not touched. |
| `showCursor(on)` | `show_cursor(on)` | `ESC[?25h` / `ESC[?25l`. |

`enterApp` options (all optional):

| Option | Default | Meaning |
|---|---|---|
| `altScreen` | `true` | Use the alternate screen. |
| `hideCursor` | `true` | Hide the cursor. |
| `mouse` | `'press-release'` | `'none'`, `'press-release'` (`?1000h`), `'drag'` (`?1002h`, adds drag motion) or `'any'` (`?1003h`, adds hover). SGR encoding (`?1006h`) is always added unless `'none'`; without a mode no mouse bytes are ever sent. |
| `bracketedPaste` | `true` | Enable bracketed paste. |
| `kitty` | `false` | `true` pushes kitty keyboard flags `1` (unambiguous Esc/Alt/Ctrl chords); a number pushes that flag set. |

Lua option keys are spelled the same (`altScreen`, `hideCursor`, `mouse`, `bracketedPaste`, `kitty`); the snake_case spellings (`alt_screen`, `hide_cursor`, `bracketed_paste`) are accepted too. Calling `enterApp` while already entered first undoes the previous state in the same write, so kitty pushes never stack.

Default `enterApp()` writes `ESC[?1049h ESC[2J ESC[H ESC[?25l ESC[?1000h ESC[?1006h ESC[?2004h`; `leaveApp()` writes `ESC[?2004l ESC[?1006l ESC[?1000l ESC[0m ESC[?25h ESC[?1049l`.

### Styles

`Style` is `{ fg?, bg?, bold?, dim?, italic?, underline?, inverse? }` (a plain table in Lua). `sgr(style)` / `tui.sgr(style)` returns the escape. The sequence **always starts with a reset** (`0;`) so styles never accumulate: `{fg:'red', bold:true}` gives `ESC[0;1;31m`, an empty style gives `ESC[0m`. Parameters are emitted in the order bold, dim, italic, underline, inverse, fg, bg.

A colour is one of:

* a palette index `0..255` (`38;5;n` / `48;5;n`),
* `'#rrggbb'` (`38;2;r;g;b`),
* `[r, g, b]` (Lua `{r, g, b}`), each clamped to `0..255` (`38;2;r;g;b`),
* a name: `black red green yellow blue magenta cyan white` (30-37 / 40-47), the bright variants `brightBlack` ... `brightWhite` (Lua spelling `bright_black` ...; both spellings are accepted in both languages; 90-97 / 100-107), or `'default'` (39 / 49).

An unknown colour is ignored (no error).

## Input

### `InputParser` and `parseInput`

```ts
const parser = new InputParser();                  // Lua: tui.InputParser.new()
const events = parser.feed(chunk);                 // chunk: Uint8Array | string (text is UTF-8 encoded first); Lua: byte string
const rest = parser.flush();                       // resolve any partial sequence as if the stream paused
parser.expectCpr(1);                               // Lua: parser:expect_cpr(1), see "CPR"
```

* TypeScript: `for await (const ev of parseInput(term, parser?)) { ... }` (`async function*`; ends when `term.read()` resolves `null`/`undefined`).
* Lua: `for ev in tui.parse_input(term, parser) do ... end` (a plain iterator; it calls `term.read()` only when its internal queue is empty, so it yields inside your coroutine exactly when it needs input; it returns `nil` when `read()` returns `nil`).

A chunk may end anywhere and **one chunk is not one key**: typed text arrives one scalar per chunk, a mouse report is one chunk, a paste is one huge chunk, engine replies are batched. The parser is an incremental byte-level state machine, so it does not matter how the stream is split (apart from the explicit end-of-chunk Escape rules below).

### Event shapes

Identical field names in both languages (Lua tables, TS discriminated union `InputEvent`). The only naming differences are Lua's `page_up`/`page_down` key names and `wheel_up`/`wheel_down`/`wheel_left`/`wheel_right` button names.

```ts
{ kind: 'key',   name: string, mods: Mods, text: string }
{ kind: 'mouse', button: 'left'|'middle'|'right'|'none'|'wheelUp'|'wheelDown'|'wheelLeft'|'wheelRight',
                 action: 'press'|'release'|'motion'|'wheel', col: number, row: number, mods: Mods }
{ kind: 'text',  text: string, paste: true }
{ kind: 'reply', bytes: Uint8Array /* Lua: string */ }

Mods = { shift: boolean, alt: boolean, ctrl: boolean, super: boolean }   // always all four fields
```

* **key**: `name` is the character itself for text (`'a'`, `'é'`, `'A'`; case is kept, shift is not inferred) or one of `space enter tab backspace escape up down left right home end insert delete pageUp pageDown f1`..`f12` (Lua `page_up`, `page_down`), or `kitty:<code>` for kitty functional keys. `text` is the printable text the key produced: the character for plain typing (`' '` for space), and `''` for named keys and for chords (anything with ctrl/alt/super). Test `ev.name`, not `ev.text`, for shortcuts.
* **mouse**: `col`/`row` are 1-based cells. `action` is `'press'`, `'release'`, `'motion'` (drag with a button, or hover with `button: 'none'`) or `'wheel'` (one event per notch).
* **text**: the contents of one bracketed paste, delivered as a single event when the end marker arrives.
* **reply**: the raw bytes of an engine reply (DA1, DA2, DSR, CPR, kitty query/graphics, DECRQM, ...) or of any escape sequence the parser does not recognise. Ignore it unless you asked a question.

### Decoding rules (summary)

Exactly the encodings purrTTY produces (`docs/research/existing-mods/purrtty-virtual-pty-bridge.md`).

* **Text**: incremental UTF-8; a code point split across chunks is completed by the next chunk; invalid bytes (stray continuation, overlong, surrogates, > U+10FFFF, truncated) become one `U+FFFD` per invalid sequence. Space is `name 'space', text ' '`.
* **C0 controls**: `\r` and `\n` are `enter`; `\t` is `tab`; `0x7f` is `backspace`; `0x08` is `backspace`+ctrl; `0x00` is `space`+ctrl; `0x01..0x1a` (other than the above) are ctrl+`a..z` (`0x03` is ctrl+`c`); `0x1c..0x1f` are ctrl+`\`, `]`, `^`, `_`.
* **Escape policy** (purrTTY sends Escape alone, with no timer):
  * `ESC` at the very end of a chunk is an `escape` key immediately.
  * `ESC ESC` is `escape` followed by the second `ESC` reprocessed.
  * `ESC x` (any other byte or full UTF-8 character) is that key with `alt = true` (`text` is `''`); Alt+Enter is `enter`+alt.
  * `ESC [` ends the chunk: key `[`+alt; `ESC O` ends the chunk: key `O`+alt. Any other partial sequence (CSI with parameters, string sequences, partial UTF-8) stays pending for the next chunk. Consequently Alt+`]`, Alt+`P`, Alt+`_`, Alt+`^` and Alt+`X` (which start OSC/DCS/APC/PM/SOS) are ambiguous with those introducers and stay pending until more input arrives or `flush()` is called (then they come out as a `reply`).
  * `flush()` resolves everything pending and returns to the ground state: partial UTF-8 becomes `U+FFFD`, a partial CSI or string sequence becomes a `reply` with the raw bytes, a paste in progress becomes a `text` event with what has arrived so far.
* **CSI keys (legacy)**: finals `A B C D` are up/down/right/left, `H` home, `F` end, `P Q R S` are f1-f4, `Z` is shift+tab; `CSI n ~` is 1/7 home, 2 insert, 3 delete, 4/8 end, 5 pageUp, 6 pageDown, 11-15 f1-f5, 17-21 f6-f10, 23 f11, 24 f12. `SS3` (`ESC O x`, sent under DECCKM) gives the same arrows/home/end/f1-f4, plus `M` = enter. Modifiers: `CSI 1;m X` / `CSI n;m ~` with `m = 1 + bits` where shift = 1, alt = 2, ctrl = 4, super = 8 (higher bits such as caps/num lock are ignored; a missing `m` means none). A `:3` event type after `m` (key release) drops the event.
* **Kitty keyboard** (`kitty: true`): `CSI code[:shifted[:base]];mods[:event][;text] u`. `13` enter, `9` tab, `127` backspace, `27` escape, `32` space, `57344..63743` is `kitty:<code>`, otherwise the name is the character for `code`. Release events (`:3`) are dropped. `text` is `''` for chords and non-printing keys; otherwise it is the associated text if given, else the shifted code point if given, else the character (so `CSI 97;2u` is `a` with `shift` and text `'a'`). Examples: Ctrl+C is `CSI 99;5u`, Escape `CSI 27u`, Enter `CSI 13u`, Alt+x `CSI 120;3u`.
* **SGR mouse** (`CSI < b;x;y M|m`): button bits `b&3` (0 left, 1 middle, 2 right, 3 none), `+4` shift, `+8` alt, `+16` ctrl, `+32` motion, `+64` wheel (0 up, 1 down, 2 left, 3 right). `M` is press, `m` is release. **Legacy X10 mouse** (`CSI M` + three raw bytes `b+32, x+32, y+32`, only if SGR was not enabled) is decoded the same way, with button `3` meaning release (`button 'none'`).
* **CPR / F3 ambiguity**: `CSI row;col R` is also xterm's modified F3 (`CSI 1;m R`). If you sent a cursor-position request, call `parser.expectCpr()` first (it accepts a count and adds to any pending count): the next such sequence(s) are returned as `reply`. Without an expectation, `CSI 1;m R` is F3 with modifiers, and any other `CSI row;col R` is a `reply`. purrTTY never needs this unless you query the cursor.
* **Bracketed paste**: `CSI 200 ~` starts paste mode; the raw bytes are collected verbatim (ESC and control bytes included) until `ESC [ 2 0 1 ~`, then one `text` event is emitted with the UTF-8-decoded content. It may span any number of chunks.
* **Replies and unknown sequences**: CSI with a `? > =` marker (DA1 `ESC[?62;22;52c`, DA2, kitty flags `ESC[?1u`, DECRQM ...), `CSI n`, focus `CSI I`/`CSI O`, any unrecognised final, and OSC/DCS/APC/PM/SOS strings (terminated by `BEL` for OSC only, or `ESC \`; for example the kitty graphics reply `ESC_Gi=1;OK ESC\`) are emitted whole as `reply`.
* **Guards**: a CSI that reaches 256 bytes (including `ESC [`) without a final byte is emitted as a `reply` and parsing resumes in the ground state; likewise a string sequence that reaches 65536 bytes without a terminator. A CSI or X10 sequence interrupted by `ESC` or another control byte is emitted as a `reply` and the interrupting byte is reprocessed. Both limits can be changed with `new InputParser({ maxCsiLength, maxStringLength })` (Lua `max_csi_length`, `max_string_length`).

### Why buttons fire on release

An in-world quad is focused by clicking it, and that focusing click is also delivered to the script as a mouse press. `Button.hit`, `Toggle.hit` and `isClick` therefore act on **left-button release** (the press that merely focused the quad does nothing). Pair it with `mouse: 'press-release'` (the default).

## Widgets

Plain objects with public fields; you draw them and feed them events.

| Widget | Construct | Methods |
|---|---|---|
| `Rect(x, y, w, h)` | `new Rect(x, y, w, h)` / `tui.Rect.new(x, y, w, h)` | `contains(col, row)` (inclusive `x..x+w-1`, `y..y+h-1`) |
| `Label` | `{ x, y, text, style? }` | `draw(screen)` |
| `Button` | `{ x, y, w, h = 1, label, style?, activeStyle? }` | `draw(screen, active = false)`, `hit(ev)`, `rect` (Lua also `get_rect()`) |
| `Toggle` | `{ x, y, label, value = false, style? }` | `draw(screen)`, `hit(ev)` (flips `value`), `rect` |
| `ProgressBar` | `{ x, y, w, value = 0, style? }` | `draw(screen)` |
| `isClick(ev)` / `tui.is_click(ev)` | | true for a left-button release |

* `Button`: with `h < 3` it draws `[ label ]` centred in `w` (padded with spaces to exactly `w`, label truncated if too long); with `h >= 3` it fills and boxes the rectangle and centres the label. `active` selects `activeStyle` (default: `style` plus `inverse`). `hit(ev)` is true iff `ev` is a mouse event with `action 'release'`, `button 'left'`, inside the rect.
* `Toggle`: draws `[x] label` / `[ ] label`; its rect is `4 + width(label)` by 1; `hit(ev)` flips `value` and returns true on a left release inside it.
* `ProgressBar`: `value` is clamped to `0..1`; it draws full blocks `█` plus one partial eighth block (`▏▎▍▌▋▊▉`) for the remainder, padded with spaces to `w`.

In Lua, widget constructors take a table (`tui.Button.new({ x = 2, y = 2, w = 16, label = "OPEN" })`) and are called with colon syntax (`button:draw(screen)`, `button:hit(ev)`); the button option `activeStyle` may also be spelled `active_style`.

## Constants and helpers

* `SEQ` (Lua `tui.SEQ`, snake_case keys such as `alt_screen_on`): the common sequences (`altScreenOn/Off`, `clearHome`, `clearLine`, `hideCursor`, `showCursor`, `mousePressRelease`/`mouseDrag`/`mouseAny` and their `...Off`, `mouseSgrOn/Off`, `bracketedPasteOn/Off`, `syncBegin/End`, `kittyPop`, `resetStyle`); `kittyPush(flags)` / `tui.kitty_push(flags)` builds `ESC[>{flags}u`.
* `sgr(style)`, `strWidth(s)` / `tui.str_width(s)` (code points), `strSlice(s, start, end)` (TS, 0-based half-open) / `tui.str_sub(s, i, j)` (Lua, 1-based inclusive, by code point), `encodeUtf8(s)` / `decodeUtf8(bytes)` (TS, since the host has no `TextEncoder`) and `tui.utf8_char(cp)` / `tui.decode_utf8(s)` (Lua).

## Limitations

* No East-Asian width, grapheme-cluster or bidi handling (widths are code point counts).
* No timers: an Escape key is recognised purely by chunk boundaries (as purrTTY sends it), never by a timeout.
* No layout engine, focus handling or text-input widget: compose with `Rect`, `box`, `hline` and plain code.
* Output is not clipped to the screen; drawing past `term.size` is the caller's concern.
