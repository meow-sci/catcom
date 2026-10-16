--- tui.lua - escape-code toolkit for terminal scripts (ksa-scripting).
--
-- Dependency-free (LuaJIT 2.1 / Lua 5.1 dialect, sandbox-safe: uses only
-- type, tostring, tonumber, setmetatable, pcall, error, unpack, ipairs, pairs,
-- math.*, string.*, table.concat). Mirrors tui.ts.
--
--   local tui = require("lib.tui")
--
-- Conventions
--  * All coordinates are 1-based: column x/col 1..cols, row y/row 1..rows
--    (matches CUP `CSI row;col H` and SGR mouse reports).
--  * Widths count Unicode code points (no East-Asian wide / combining handling).
--  * Strings are byte strings; text is UTF-8.
--  * `term` is any table with `write(s)` and (for parse_input) `read()`,
--    called with DOT syntax: term.write(s), term.read().
--  * Methods are snake_case and use `obj:method()`; constructors are `Class.new(...)`.
--  * Input events are decoded from the exact byte streams purrTTY delivers
--    (legacy xterm + kitty keyboard flag 1 + SGR mouse + engine replies).

local type, tostring, tonumber = type, tostring, tonumber
local setmetatable, pcall, error, unpack = setmetatable, pcall, error, unpack
local ipairs, pairs = ipairs, pairs
local floor, huge = math.floor, math.huge
local sbyte, schar, srep, ssub = string.byte, string.char, string.rep, string.sub
local sformat, sgsub, slower, smatch = string.format, string.gsub, string.lower, string.match
local concat = table.concat

local tui = {}

-- ---------------------------------------------------------------------------
-- Constants
-- ---------------------------------------------------------------------------

--- Common escape sequences.
tui.SEQ = {
  alt_screen_on = "\27[?1049h",
  alt_screen_off = "\27[?1049l",
  clear_home = "\27[2J\27[H",
  clear_line = "\27[2K",
  hide_cursor = "\27[?25l",
  show_cursor = "\27[?25h",
  mouse_press_release = "\27[?1000h",
  mouse_drag = "\27[?1002h",
  mouse_any = "\27[?1003h",
  mouse_press_release_off = "\27[?1000l",
  mouse_drag_off = "\27[?1002l",
  mouse_any_off = "\27[?1003l",
  mouse_sgr_on = "\27[?1006h",
  mouse_sgr_off = "\27[?1006l",
  bracketed_paste_on = "\27[?2004h",
  bracketed_paste_off = "\27[?2004l",
  sync_begin = "\27[?2026h",
  sync_end = "\27[?2026l",
  kitty_pop = "\27[<u",
  reset_style = "\27[0m",
}
local SEQ = tui.SEQ

--- Kitty keyboard push: CSI > flags u.
function tui.kitty_push(flags)
  return "\27[>" .. sformat("%d", floor(flags)) .. "u"
end

local ESC = 0x1b
local M_SHIFT, M_ALT, M_CTRL, M_SUPER = 1, 2, 4, 8

local function has(bits, v)
  return floor(bits / v) % 2 == 1
end

local function to_int(n)
  n = tonumber(n)
  if n == nil or n ~= n or n == huge or n == -huge then
    return 0
  end
  return floor(n)
end

local function fmt(n)
  return sformat("%d", n)
end

-- ---------------------------------------------------------------------------
-- UTF-8 helpers (strings are already bytes; these operate on code points)
-- ---------------------------------------------------------------------------

local function utf8_char(cp)
  if cp < 0x80 then
    return schar(cp)
  elseif cp < 0x800 then
    return schar(0xC0 + floor(cp / 64), 0x80 + cp % 64)
  elseif cp < 0x10000 then
    return schar(0xE0 + floor(cp / 4096), 0x80 + floor(cp / 64) % 64, 0x80 + cp % 64)
  end
  return schar(0xF0 + floor(cp / 262144), 0x80 + floor(cp / 4096) % 64, 0x80 + floor(cp / 64) % 64, 0x80 + cp % 64)
end
tui.utf8_char = utf8_char

local U8_PENDING, U8_REPROCESS, REPLACEMENT = -1, -2, 0xFFFD

-- Incremental strict UTF-8 decoder (WHATWG-style). step(b) returns a code
-- point (>= 0) when one is complete, U8_PENDING when more bytes are needed, or
-- U8_REPROCESS when an unfinished sequence was interrupted by b: the caller
-- owes one U+FFFD and must call step(b) again (b was NOT consumed).
local Utf8Decoder = {}
Utf8Decoder.__index = Utf8Decoder

local function new_utf8_decoder()
  return setmetatable({ need = 0, cp = 0, lo = 0x80, hi = 0xBF }, Utf8Decoder)
end

function Utf8Decoder:step(b)
  if self.need > 0 then
    if b >= self.lo and b <= self.hi then
      self.cp = self.cp * 64 + b % 64
      self.need = self.need - 1
      self.lo, self.hi = 0x80, 0xBF
      if self.need == 0 then
        return self.cp
      end
      return U8_PENDING
    end
    self.need = 0
    self.lo, self.hi = 0x80, 0xBF
    return U8_REPROCESS
  end
  if b < 0x80 then
    return b
  end
  if b >= 0xC2 and b <= 0xDF then
    self.need = 1
    self.cp = b % 32
    return U8_PENDING
  end
  if b >= 0xE0 and b <= 0xEF then
    self.need = 2
    self.cp = b % 16
    if b == 0xE0 then
      self.lo = 0xA0
    elseif b == 0xED then
      self.hi = 0x9F
    end
    return U8_PENDING
  end
  if b >= 0xF0 and b <= 0xF4 then
    self.need = 3
    self.cp = b % 8
    if b == 0xF0 then
      self.lo = 0x90
    elseif b == 0xF4 then
      self.hi = 0x8F
    end
    return U8_PENDING
  end
  return REPLACEMENT
end

function Utf8Decoder:reset()
  self.need = 0
  self.lo, self.hi = 0x80, 0xBF
end

local CHUNK = 2048

-- Convert t[1..n] (byte values) to a string.
local function bytes_to_string(t, n)
  if n <= 0 then
    return ""
  end
  local parts, np, i = {}, 0, 1
  while i <= n do
    local j = i + CHUNK - 1
    if j > n then
      j = n
    end
    np = np + 1
    parts[np] = schar(unpack(t, i, j))
    i = j + 1
  end
  return concat(parts)
end

-- Decode t[1..n] (UTF-8 bytes) into a valid UTF-8 string; invalid input becomes U+FFFD.
local function decode_utf8_bytes(t, n)
  local dec = new_utf8_decoder()
  local parts, np = {}, 0
  for i = 1, n do
    local b = t[i]
    local r = dec:step(b)
    if r == U8_REPROCESS then
      np = np + 1
      parts[np] = "\239\191\189"
      r = dec:step(b)
    end
    if r >= 0 then
      np = np + 1
      parts[np] = utf8_char(r)
    end
  end
  if dec.need > 0 then
    np = np + 1
    parts[np] = "\239\191\189"
  end
  return concat(parts)
end

--- Decode a UTF-8 byte string, replacing invalid sequences with U+FFFD.
function tui.decode_utf8(s)
  local t, n = {}, #s
  for i = 1, n do
    t[i] = sbyte(s, i)
  end
  return decode_utf8_bytes(t, n)
end

--- Number of Unicode code points in `s` (the width model used by this library).
local function str_width(s)
  local _, n = sgsub(s, "[^\128-\191]", "")
  return n
end
tui.str_width = str_width

--- Substring by code point index, 1-based inclusive (like string.sub, no negatives).
local function str_sub(s, i, j)
  local starts, k = {}, 0
  for p = 1, #s do
    local b = sbyte(s, p)
    if b < 0x80 or b >= 0xC0 then
      k = k + 1
      starts[k] = p
    end
  end
  if j == nil or j > k then
    j = k
  end
  if i < 1 then
    i = 1
  end
  if i > j then
    return ""
  end
  local to = (j < k) and (starts[j + 1] - 1) or #s
  return ssub(s, starts[i], to)
end
tui.str_sub = str_sub

-- ---------------------------------------------------------------------------
-- SGR styles
-- ---------------------------------------------------------------------------

local COLOR_INDEX = {
  black = 0, red = 1, green = 2, yellow = 3, blue = 4, magenta = 5, cyan = 6, white = 7,
}

local function clamp255(v)
  if type(v) ~= "number" or v ~= v or v == huge or v == -huge then
    return nil
  end
  local n = floor(v)
  if n < 0 then
    return 0
  elseif n > 255 then
    return 255
  end
  return n
end

-- SGR parameter text for one colour (no CSI / m), or nil when the colour is unknown.
local function color_param(c, bg)
  local t = type(c)
  if t == "number" then
    if c ~= c or c == huge or c == -huge then
      return nil
    end
    local n = floor(c)
    if n < 0 or n > 255 then
      return nil
    end
    return (bg and "48;5;" or "38;5;") .. fmt(n)
  elseif t == "string" then
    if ssub(c, 1, 1) == "#" then
      local r, g, b = smatch(c, "^#(%x%x)(%x%x)(%x%x)$")
      if not r then
        return nil
      end
      return (bg and "48;2;" or "38;2;") .. fmt(tonumber(r, 16)) .. ";" .. fmt(tonumber(g, 16)) .. ";" .. fmt(tonumber(b, 16))
    end
    -- accept both bright_black and brightBlack spellings
    local key = (sgsub(slower(c), "_", ""))
    if key == "default" then
      return bg and "49" or "39"
    end
    local bright = false
    local base = key
    if ssub(key, 1, 6) == "bright" then
      bright = true
      base = ssub(key, 7)
    end
    local idx = COLOR_INDEX[base]
    if idx == nil then
      return nil
    end
    local code
    if bright then
      code = (bg and 100 or 90) + idx
    else
      code = (bg and 40 or 30) + idx
    end
    return fmt(code)
  elseif t == "table" then
    local r, g, b = clamp255(c[1]), clamp255(c[2]), clamp255(c[3])
    if r == nil or g == nil or b == nil then
      return nil
    end
    return (bg and "48;2;" or "38;2;") .. fmt(r) .. ";" .. fmt(g) .. ";" .. fmt(b)
  end
  return nil
end

--- SGR escape for a style table. Always starts with a reset (`0;`) so styles
-- do not accumulate: {fg="red", bold=true} -> "\27[0;1;31m"; nil/{} -> "\27[0m".
function tui.sgr(style)
  local p, n = { "0" }, 1
  if style then
    if style.bold then n = n + 1; p[n] = "1" end
    if style.dim then n = n + 1; p[n] = "2" end
    if style.italic then n = n + 1; p[n] = "3" end
    if style.underline then n = n + 1; p[n] = "4" end
    if style.inverse then n = n + 1; p[n] = "7" end
    if style.fg ~= nil then
      local f = color_param(style.fg, false)
      if f then n = n + 1; p[n] = f end
    end
    if style.bg ~= nil then
      local b = color_param(style.bg, true)
      if b then n = n + 1; p[n] = b end
    end
  end
  return "\27[" .. concat(p, ";") .. "m"
end
local sgr = tui.sgr

local RESET = SEQ.reset_style

-- ---------------------------------------------------------------------------
-- Screen
-- ---------------------------------------------------------------------------

local MOUSE_MODE_NUM = { ["press-release"] = 1000, drag = 1002, any = 1003 }

local function cup(x, y)
  local col, row = to_int(x), to_int(y)
  if col < 1 then col = 1 end
  if row < 1 then row = 1 end
  return "\27[" .. fmt(row) .. ";" .. fmt(col) .. "H"
end

local Screen = {}
Screen.__index = Screen
tui.Screen = Screen

--- Output helper over a terminal. All coordinates are 1-based.
function Screen.new(term)
  return setmetatable({ term = term, _depth = 0, _buf = {}, _app = nil }, Screen)
end

function Screen:_out(s)
  if self._depth > 0 then
    local b = self._buf
    b[#b + 1] = s
  else
    self.term.write(s)
  end
end

local function opt(o, camel, snake, default)
  local v = o[camel]
  if v == nil and snake then
    v = o[snake]
  end
  if v == nil then
    return default
  end
  return v
end

function Screen:_leave_seq()
  local a = self._app
  if not a then
    return ""
  end
  local s = ""
  if a.kitty then s = s .. SEQ.kitty_pop end
  if a.paste then s = s .. SEQ.bracketed_paste_off end
  if a.mouse_mode ~= 0 then s = s .. SEQ.mouse_sgr_off .. "\27[?" .. fmt(a.mouse_mode) .. "l" end
  s = s .. RESET .. SEQ.show_cursor
  if a.alt then s = s .. SEQ.alt_screen_off end
  return s
end

--- Enter "application" mode (alt screen, hidden cursor, mouse, bracketed paste,
-- kitty keys). One write. opts: altScreen=true, hideCursor=true,
-- mouse="press-release" ("none"|"press-release"|"drag"|"any"),
-- bracketedPaste=true, kitty=false (true = flags 1, number = that flag set).
-- snake_case spellings (alt_screen, hide_cursor, bracketed_paste) also work.
function Screen:enter_app(opts)
  opts = opts or {}
  local alt = opt(opts, "altScreen", "alt_screen", true)
  local hide = opt(opts, "hideCursor", "hide_cursor", true)
  local mouse = opt(opts, "mouse", nil, "press-release")
  local paste = opt(opts, "bracketedPaste", "bracketed_paste", true)
  local kitty = opt(opts, "kitty", nil, false)
  local kflags = 0
  if kitty == true then
    kflags = 1
  elseif type(kitty) == "number" and kitty > 0 then
    kflags = floor(kitty)
  end
  local mode = MOUSE_MODE_NUM[mouse] or 0

  local s = self._app and self:_leave_seq() or "" -- never stack kitty pushes
  if alt then s = s .. SEQ.alt_screen_on end
  s = s .. SEQ.clear_home
  if hide then s = s .. SEQ.hide_cursor end
  if mode ~= 0 then s = s .. "\27[?" .. fmt(mode) .. "h" .. SEQ.mouse_sgr_on end
  if paste then s = s .. SEQ.bracketed_paste_on end
  if kflags > 0 then s = s .. tui.kitty_push(kflags) end
  self._app = { alt = alt and true or false, mouse_mode = mode, paste = paste and true or false, kitty = kflags > 0 }
  self:_out(s)
end

--- Undo enter_app in reverse order, in one write. Idempotent.
function Screen:leave_app()
  if not self._app then
    return
  end
  local s = self:_leave_seq()
  self._app = nil
  self:_out(s)
end

--- Run fn(); everything the Screen draws meanwhile is buffered and written as
-- one DEC 2026 synchronised update. Nested calls join the outer frame. If fn
-- raises, the buffered output is still written and the error re-raised.
function Screen:frame(fn)
  if self._depth > 0 then
    return fn()
  end
  self._depth = 1
  self._buf = {}
  local ok, res = pcall(fn)
  self._depth = 0
  local data = concat(self._buf)
  self._buf = {}
  self.term.write(SEQ.sync_begin .. data .. SEQ.sync_end)
  if not ok then
    error(res, 0)
  end
  return res
end

--- Raw text, no cursor movement.
function Screen:write(text)
  self:_out(tostring(text))
end

--- Move the cursor to column x, row y (1-based, clamped to >= 1).
function Screen:move_to(x, y)
  self:_out(cup(x, y))
end

--- Reset style, erase the whole screen and home the cursor.
function Screen:clear()
  self:_out("\27[0m\27[2J\27[H")
end

function Screen:clear_line()
  self:_out(SEQ.clear_line)
end

function Screen:style(s)
  self:_out(sgr(s))
end

function Screen:reset_style()
  self:_out(RESET)
end

function Screen:show_cursor(on)
  self:_out(on and SEQ.show_cursor or SEQ.hide_cursor)
end

--- move_to + optional style + text + reset (when styled).
function Screen:text(x, y, str, style)
  self:_out(cup(x, y) .. (style and sgr(style) or "") .. tostring(str) .. (style and RESET or ""))
end

function Screen:hline(x, y, len, ch, style)
  len = to_int(len)
  if len <= 0 then
    return
  end
  ch = ch or "─"
  self:_out(cup(x, y) .. (style and sgr(style) or "") .. srep(ch, len) .. (style and RESET or ""))
end

function Screen:vline(x, y, len, ch, style)
  len = to_int(len)
  if len <= 0 then
    return
  end
  ch = ch or "│"
  local parts, n = {}, 0
  if style then n = n + 1; parts[n] = sgr(style) end
  for i = 0, len - 1 do
    n = n + 1
    parts[n] = cup(x, y + i) .. ch
  end
  if style then n = n + 1; parts[n] = RESET end
  self:_out(concat(parts))
end

function Screen:fill(x, y, w, h, ch, style)
  w, h = to_int(w), to_int(h)
  if w <= 0 or h <= 0 then
    return
  end
  local row = srep(ch or " ", w)
  local parts, n = {}, 0
  if style then n = n + 1; parts[n] = sgr(style) end
  for i = 0, h - 1 do
    n = n + 1
    parts[n] = cup(x, y + i) .. row
  end
  if style then n = n + 1; parts[n] = RESET end
  self:_out(concat(parts))
end

--- Single-line box. No-op when w < 2 or h < 2. The title (" title ") starts at
-- x+2, truncated to w-4.
function Screen:box(x, y, w, h, title, style)
  w, h = to_int(w), to_int(h)
  if w < 2 or h < 2 then
    return
  end
  local top
  local room = w - 4
  if title and title ~= "" and room > 0 then
    local t = str_sub(" " .. tostring(title) .. " ", 1, room)
    top = "┌─" .. t .. srep("─", w - 3 - str_width(t)) .. "┐"
  else
    top = "┌" .. srep("─", w - 2) .. "┐"
  end
  local parts, n = {}, 0
  if style then n = n + 1; parts[n] = sgr(style) end
  n = n + 1
  parts[n] = cup(x, y) .. top
  for i = 1, h - 2 do
    n = n + 1
    parts[n] = cup(x, y + i) .. "│" .. cup(x + w - 1, y + i) .. "│"
  end
  n = n + 1
  parts[n] = cup(x, y + h - 1) .. "└" .. srep("─", w - 2) .. "┘"
  if style then n = n + 1; parts[n] = RESET end
  self:_out(concat(parts))
end

-- ---------------------------------------------------------------------------
-- Input parser
-- ---------------------------------------------------------------------------

local function mods_of(bits)
  return {
    shift = has(bits, M_SHIFT),
    alt = has(bits, M_ALT),
    ctrl = has(bits, M_CTRL),
    super = has(bits, M_SUPER),
  }
end

local function key_ev(name, bits, text)
  return { kind = "key", name = name, mods = mods_of(bits), text = text }
end

-- xterm modifier parameter m -> bit set (1 shift, 2 alt, 4 ctrl, 8 super); higher bits ignored.
local function mod_bits(m)
  if not m or m < 1 then
    return 0
  end
  return (m - 1) % 16
end

local function char_key(cp, bits)
  local ch = utf8_char(cp)
  return key_ev(ch, bits, has(bits, M_ALT) and "" or ch)
end

local CTRL_PUNCT = { [0x1c] = "\\", [0x1d] = "]", [0x1e] = "^", [0x1f] = "_" }

-- Key for a single byte < 0x80 other than ESC, with extra modifier bits (alt).
local function ascii_key(b, extra)
  if b >= 0x20 and b < 0x7f then
    if b == 0x20 then
      return key_ev("space", extra, extra == 0 and " " or "")
    end
    local ch = schar(b)
    return key_ev(ch, extra, extra == 0 and ch or "")
  end
  if b == 0x0d or b == 0x0a then return key_ev("enter", extra, "") end
  if b == 0x09 then return key_ev("tab", extra, "") end
  if b == 0x7f then return key_ev("backspace", extra, "") end
  -- `extra` is 0 or M_ALT here, so adding M_CTRL never double-counts.
  if b == 0x08 then return key_ev("backspace", extra + M_CTRL, "") end
  if b == 0x00 then return key_ev("space", extra + M_CTRL, "") end
  if b >= 0x01 and b <= 0x1a then
    return key_ev(schar(b + 96), extra + M_CTRL, "")
  end
  return key_ev(CTRL_PUNCT[b], extra + M_CTRL, "") -- 0x1c..0x1f
end

local function button_of(low)
  if low == 0 then return "left" elseif low == 1 then return "middle" elseif low == 2 then return "right" end
  return "none"
end

local WHEEL_BUTTONS = { [0] = "wheel_up", "wheel_down", "wheel_left", "wheel_right" }

local function mouse_event(cb, col, row, release, x10)
  local bits = 0
  if has(cb, 4) then bits = bits + M_SHIFT end
  if has(cb, 8) then bits = bits + M_ALT end
  if has(cb, 16) then bits = bits + M_CTRL end
  local low = cb % 4
  local button, action
  if has(cb, 64) then
    action = "wheel"
    button = WHEEL_BUTTONS[low]
  elseif has(cb, 32) then
    action = "motion"
    button = button_of(low)
  elseif x10 then
    if low == 3 then
      action, button = "release", "none"
    else
      action, button = "press", button_of(low)
    end
  else
    action = release and "release" or "press"
    button = button_of(low)
  end
  return { kind = "mouse", button = button, action = action, col = col, row = row, mods = mods_of(bits) }
end

-- A parsed numeric sub-parameter: the number, or `false` when it was empty (num < 0).
local function param_val(num)
  if num < 0 then
    return false
  end
  return num
end

-- Parse the bytes following `ESC [` (p[1..n], the last one is the final byte).
-- Returns marker (0 or the byte), fields, inter, final, ok. fields is an array
-- of arrays of numbers; an empty sub-parameter is `false`.
local function parse_csi(p, n)
  local last = n
  local final = p[last]
  local idx = 1
  local marker = 0
  if last > 1 and p[1] >= 0x3c and p[1] <= 0x3f then
    marker = p[1]
    idx = 2
  end
  local fields, nf = {}, 0
  local cur, nc = {}, 0
  local num, digits = -1, 0
  local saw, ok = false, true
  while idx < last do
    local c = p[idx]
    if c >= 0x30 and c <= 0x39 then
      num = (num < 0 and 0 or num) * 10 + (c - 0x30)
      digits = digits + 1
      if digits > 9 then ok = false end
      saw = true
    elseif c == 0x3b then
      nc = nc + 1
      cur[nc] = param_val(num)
      nf = nf + 1
      fields[nf] = cur
      cur, nc = {}, 0
      num, digits = -1, 0
      saw = true
    elseif c == 0x3a then
      nc = nc + 1
      cur[nc] = param_val(num)
      num, digits = -1, 0
      saw = true
    else
      break
    end
    idx = idx + 1
  end
  if saw then
    nc = nc + 1
    cur[nc] = param_val(num)
    nf = nf + 1
    fields[nf] = cur
  end
  local inter = false
  while idx < last do
    local c = p[idx]
    if c >= 0x20 and c <= 0x2f then
      inter = true
    else
      ok = false
    end
    idx = idx + 1
  end
  return marker, fields, inter, final, ok
end

local DROP = {} -- sentinel: drop the event silently

-- Key with modifiers from a `m[:event]` field; event type 3 (release) is dropped.
local function key_with_mods(name, f, extra)
  local m = f and f[1] or nil
  local evt = f and f[2] or nil
  if evt == 3 then
    return DROP
  end
  local bits = mod_bits(m or nil)
  -- OR in `extra` (bits are distinct flags)
  if extra ~= 0 and not has(bits, extra) then
    bits = bits + extra
  end
  return key_ev(name, bits, "")
end

local function tilde_name(n)
  if n == 1 or n == 7 then return "home" end
  if n == 2 then return "insert" end
  if n == 3 then return "delete" end
  if n == 4 or n == 8 then return "end" end
  if n == 5 then return "page_up" end
  if n == 6 then return "page_down" end
  if n >= 11 and n <= 15 then return "f" .. fmt(n - 10) end
  if n >= 17 and n <= 21 then return "f" .. fmt(n - 11) end
  if n == 23 then return "f11" end
  if n == 24 then return "f12" end
  return nil
end

local function valid_cp(cp)
  return cp > 0 and cp <= 0x10ffff and not (cp >= 0xd800 and cp <= 0xdfff)
end

-- CSI ... u (kitty keyboard). Returns nil = unrecognised, DROP = drop.
local function kitty_key(fields)
  local nf = #fields
  if nf < 1 or nf > 3 then
    return nil
  end
  local code_f = fields[1]
  local code = code_f[1]
  if not code or not valid_cp(code) then
    return nil
  end
  local shifted = code_f[2]
  local mod_f = fields[2]
  local evt = mod_f and mod_f[2] or nil
  if evt == 3 then
    return DROP
  end
  local bits = mod_bits(mod_f and mod_f[1] or nil)
  local name
  local printable = false
  if code == 13 then
    name = "enter"
  elseif code == 9 then
    name = "tab"
  elseif code == 127 then
    name = "backspace"
  elseif code == 27 then
    name = "escape"
  elseif code == 32 then
    name = "space"
    printable = true
  elseif code >= 57344 and code <= 63743 then
    name = "kitty:" .. fmt(code)
  else
    name = utf8_char(code)
    printable = code >= 0x20 and not (code >= 0x7f and code <= 0x9f)
  end
  local text = ""
  if printable and not (has(bits, M_CTRL) or has(bits, M_ALT) or has(bits, M_SUPER)) then
    local tf = fields[3]
    local assoc = ""
    if tf then
      for _, cp in ipairs(tf) do
        if cp and valid_cp(cp) then
          assoc = assoc .. utf8_char(cp)
        end
      end
    end
    if assoc ~= "" then
      text = assoc
    elseif shifted and valid_cp(shifted) then
      text = utf8_char(shifted)
    else
      text = (code == 32) and " " or name
    end
  end
  return key_ev(name, bits, text)
end

local NAV_FINAL = {
  [0x41] = "up", [0x42] = "down", [0x43] = "right", [0x44] = "left",
  [0x48] = "home", [0x46] = "end",
  [0x50] = "f1", [0x51] = "f2", [0x53] = "f4",
  [0x5a] = "tab",
}

local SS3_FINAL = {
  [0x41] = "up", [0x42] = "down", [0x43] = "right", [0x44] = "left",
  [0x48] = "home", [0x46] = "end",
  [0x50] = "f1", [0x51] = "f2", [0x52] = "f3", [0x53] = "f4",
  [0x4d] = "enter",
}

local ST_GROUND, ST_UTF8, ST_CSI, ST_X10, ST_STR, ST_PASTE = 0, 1, 2, 3, 4, 5

local PASTE_END = { 0x1b, 0x5b, 0x32, 0x30, 0x31, 0x7e } -- ESC [ 2 0 1 ~

--- Incremental byte-level input decoder. Feed it whatever chunks the terminal
-- delivers (a chunk may end anywhere; one chunk is not one key).
local InputParser = {}
InputParser.__index = InputParser
tui.InputParser = InputParser

--- opts: max_csi_length (256), max_string_length (65536); camelCase also accepted.
function InputParser.new(opts)
  opts = opts or {}
  return setmetatable({
    st = ST_GROUND,
    dec = new_utf8_decoder(),
    dec_alt = false,
    pend = {},
    pn = 0,
    str_kind = 0,
    str_esc = false,
    cpr = 0,
    max_csi = opt(opts, "max_csi_length", "maxCsiLength", 256),
    max_str = opt(opts, "max_string_length", "maxStringLength", 65536),
  }, InputParser)
end

--- Expect n cursor-position reports (`CSI row;col R`); the next n such
-- sequences are replies, not F3. Adds to any pending count.
function InputParser:expect_cpr(n)
  n = n == nil and 1 or n
  if type(n) == "number" and n > 0 then
    self.cpr = self.cpr + floor(n)
  end
end

local function reply_ev(prefix, body, bn)
  return { kind = "reply", bytes = prefix .. bytes_to_string(body, bn) }
end

function InputParser:_csi_reply()
  return reply_ev("\27[", self.pend, self.pn)
end

function InputParser:_to_ground()
  self.st = ST_GROUND
  self.pn = 0
  self.str_esc = false
end

local function push_ev(out, ev)
  out[#out + 1] = ev
end

function InputParser:_ss3(b)
  local name = SS3_FINAL[b]
  if name then
    return key_ev(name, 0, "")
  end
  return { kind = "reply", bytes = "\27O" .. schar(b) }
end

function InputParser:_start_utf8(b, out)
  local r = self.dec:step(b)
  if r == U8_PENDING then
    self.st = ST_UTF8
  else
    push_ev(out, char_key(r, self.dec_alt and M_ALT or 0)) -- invalid lead -> U+FFFD
  end
end

function InputParser:_on_ground(chunk, i, n, out)
  local b = sbyte(chunk, i)
  if b == ESC then
    if i + 1 > n then
      push_ev(out, key_ev("escape", 0, ""))
      return i + 1
    end
    local c = sbyte(chunk, i + 1)
    if c == 0x5b then -- [
      if i + 2 > n then
        push_ev(out, key_ev("[", M_ALT, ""))
        return i + 2
      end
      self.st = ST_CSI
      self.pn = 0
      return i + 2
    elseif c == 0x4f then -- O
      if i + 2 > n then
        push_ev(out, key_ev("O", M_ALT, ""))
        return i + 2
      end
      push_ev(out, self:_ss3(sbyte(chunk, i + 2)))
      return i + 3
    elseif c == 0x5d or c == 0x50 or c == 0x5f or c == 0x5e or c == 0x58 then -- ] P _ ^ X
      self.st = ST_STR
      self.str_kind = c
      self.str_esc = false
      self.pend[1] = ESC
      self.pend[2] = c
      self.pn = 2
      return i + 2
    elseif c == ESC then
      push_ev(out, key_ev("escape", 0, ""))
      return i + 1 -- reprocess the second ESC
    end
    if c < 0x80 then
      push_ev(out, ascii_key(c, M_ALT))
    else
      self.dec_alt = true
      self:_start_utf8(c, out)
    end
    return i + 2
  end
  if b < 0x80 then
    push_ev(out, ascii_key(b, 0))
    return i + 1
  end
  self.dec_alt = false
  self:_start_utf8(b, out)
  return i + 1
end

function InputParser:_on_utf8(b, i, out)
  local r = self.dec:step(b)
  if r == U8_PENDING then
    return i + 1
  end
  self.st = ST_GROUND
  if r == U8_REPROCESS then
    push_ev(out, char_key(REPLACEMENT, self.dec_alt and M_ALT or 0))
    return i -- reprocess b from ground
  end
  push_ev(out, char_key(r, self.dec_alt and M_ALT or 0))
  return i + 1
end

function InputParser:_on_csi(b, i, out)
  if b < 0x20 or b > 0x7e then
    -- ESC or any other control / 8-bit byte aborts the sequence; it is reprocessed from ground.
    push_ev(out, self:_csi_reply())
    self:_to_ground()
    return i
  end
  local pn = self.pn + 1
  self.pn = pn
  self.pend[pn] = b
  if b >= 0x40 then
    self:_csi_final(out)
  elseif pn + 2 >= self.max_csi then
    push_ev(out, self:_csi_reply())
    self:_to_ground()
  end
  return i + 1
end

function InputParser:_csi_final(out)
  local p = self.pend
  if self.pn == 1 and p[1] == 0x4d then
    -- CSI M with no params / marker: legacy X10 mouse, three raw bytes follow.
    self.pn = 0
    self.st = ST_X10
    return
  end
  self.st = ST_GROUND
  local ev = self:_decode_csi(p, self.pn)
  if ev ~= DROP then
    push_ev(out, ev)
  end
  if self.st == ST_GROUND then
    self.pn = 0
  end
end

-- Map a complete CSI to an event (or DROP). May switch to paste mode.
function InputParser:_decode_csi(p, n)
  local marker, fields, inter, final, ok = parse_csi(p, n)
  if not ok then
    return self:_csi_reply()
  end
  local nf = #fields
  if marker == 0x3c then -- '<'
    if not inter and (final == 0x4d or final == 0x6d) and nf == 3 then
      local f0, f1, f2 = fields[1], fields[2], fields[3]
      if #f0 == 1 and #f1 == 1 and #f2 == 1 and f0[1] and f1[1] and f2[1] then
        return mouse_event(f0[1], f1[1], f2[1], final == 0x6d, false)
      end
    end
    return self:_csi_reply()
  end
  if marker ~= 0 or inter then
    return self:_csi_reply()
  end

  local ev
  if final == 0x75 then -- u (kitty)
    ev = kitty_key(fields)
  elseif final == 0x7e then -- ~
    ev = self:_tilde_key(fields)
  elseif final == 0x52 then -- R: F3 or cursor position report
    if nf == 0 then
      ev = key_ev("f3", 0, "")
    elseif nf == 2 then
      if self.cpr > 0 then
        self.cpr = self.cpr - 1
        ev = nil
      elseif fields[1][1] == 1 then
        ev = key_with_mods("f3", fields[2], 0)
      else
        ev = nil
      end
    end
  elseif NAV_FINAL[final] then
    if nf == 0 or nf == 2 then
      ev = key_with_mods(NAV_FINAL[final], fields[2], final == 0x5a and M_SHIFT or 0)
    end
  end
  if ev == nil then
    return self:_csi_reply()
  end
  return ev
end

function InputParser:_tilde_key(fields)
  local nf = #fields
  if nf < 1 or nf > 2 then
    return nil
  end
  local n = fields[1][1]
  if not n then
    return nil
  end
  if n == 200 and nf == 1 and #fields[1] == 1 then
    self.pn = 0
    self.st = ST_PASTE
    return DROP
  end
  local name = tilde_name(n)
  if not name then
    return nil
  end
  return key_with_mods(name, fields[2], 0)
end

function InputParser:_on_x10(b, i, out)
  if b < 0x20 then
    push_ev(out, reply_ev("\27[M", self.pend, self.pn))
    self:_to_ground()
    return i
  end
  local pn = self.pn + 1
  self.pn = pn
  self.pend[pn] = b
  if pn == 3 then
    local p = self.pend
    push_ev(out, mouse_event(p[1] - 32, p[2] - 32, p[3] - 32, false, true))
    self:_to_ground()
  end
  return i + 1
end

function InputParser:_on_string(b, i, out)
  local pn = self.pn + 1
  self.pn = pn
  self.pend[pn] = b
  -- ST is ESC \ ; a lone ESC not followed by '\' stays part of the string. BEL also ends an OSC.
  local st = self.str_esc and b == 0x5c
  local bel = b == 0x07 and self.str_kind == 0x5d
  self.str_esc = (b == ESC)
  if st or bel then
    push_ev(out, reply_ev("", self.pend, pn))
    self:_to_ground()
    return i + 1
  end
  if pn >= self.max_str then
    push_ev(out, reply_ev("", self.pend, pn))
    self:_to_ground()
  end
  return i + 1
end

function InputParser:_on_paste(b, i, out)
  local p = self.pend
  local pn = self.pn + 1
  self.pn = pn
  p[pn] = b
  if b == 0x7e and pn >= 6 then
    local o = pn - 6
    local match = true
    for k = 1, 6 do
      if p[o + k] ~= PASTE_END[k] then
        match = false
        break
      end
    end
    if match then
      push_ev(out, { kind = "text", text = decode_utf8_bytes(p, o), paste = true })
      self:_to_ground()
    end
  end
  return i + 1
end

--- Decode a chunk (byte string) into an array of events.
function InputParser:feed(chunk)
  if type(chunk) ~= "string" then
    error("InputParser:feed expects a byte string", 2)
  end
  local out = {}
  local n = #chunk
  local i = 1
  while i <= n do
    local st = self.st
    if st == ST_GROUND then
      i = self:_on_ground(chunk, i, n, out)
    elseif st == ST_UTF8 then
      i = self:_on_utf8(sbyte(chunk, i), i, out)
    elseif st == ST_CSI then
      i = self:_on_csi(sbyte(chunk, i), i, out)
    elseif st == ST_X10 then
      i = self:_on_x10(sbyte(chunk, i), i, out)
    elseif st == ST_STR then
      i = self:_on_string(sbyte(chunk, i), i, out)
    else
      i = self:_on_paste(sbyte(chunk, i), i, out)
    end
  end
  return out
end

--- Resolve any pending partial state as if the stream paused, and return to
-- the ground state: partial UTF-8 -> U+FFFD key, partial CSI / string -> a
-- reply with the raw bytes, a paste in progress -> a text event with what
-- arrived so far.
function InputParser:flush()
  local out = {}
  local st = self.st
  if st == ST_UTF8 then
    push_ev(out, char_key(REPLACEMENT, self.dec_alt and M_ALT or 0))
    self.dec:reset()
  elseif st == ST_CSI then
    push_ev(out, self:_csi_reply())
  elseif st == ST_X10 then
    push_ev(out, reply_ev("\27[M", self.pend, self.pn))
  elseif st == ST_STR then
    push_ev(out, reply_ev("", self.pend, self.pn))
  elseif st == ST_PASTE then
    push_ev(out, { kind = "text", text = decode_utf8_bytes(self.pend, self.pn), paste = true })
  end
  self:_to_ground()
  return out
end

--- Iterator over decoded input events for `for ev in tui.parse_input(term) do ... end`.
-- Calls term.read() (which yields the calling coroutine until input arrives)
-- only when its event queue is empty; ends when read() returns nil (terminal
-- cancelled or closed).
function tui.parse_input(term, parser)
  parser = parser or InputParser.new()
  local queue, head, tail = {}, 1, 0
  local done = false
  return function()
    while true do
      if head <= tail then
        local ev = queue[head]
        queue[head] = nil
        head = head + 1
        return ev
      end
      if done then
        return nil
      end
      local chunk = term.read()
      if chunk == nil then
        done = true
        return nil
      end
      local evs = parser:feed(chunk)
      for k = 1, #evs do
        tail = tail + 1
        queue[tail] = evs[k]
      end
    end
  end
end

-- ---------------------------------------------------------------------------
-- Widgets (plain tables, no layout engine)
-- ---------------------------------------------------------------------------

local Rect = {}
Rect.__index = Rect
tui.Rect = Rect

function Rect.new(x, y, w, h)
  return setmetatable({ x = x, y = y, w = w, h = h }, Rect)
end

--- Inclusive x..x+w-1, y..y+h-1.
function Rect:contains(col, row)
  return col >= self.x and col <= self.x + self.w - 1 and row >= self.y and row <= self.y + self.h - 1
end

--- A left-button release: the event the helpers treat as a click (the
-- focusing press on an in-world quad is ignored).
local function is_click(ev)
  return ev.kind == "mouse" and ev.action == "release" and ev.button == "left"
end
tui.is_click = is_click

local Label = {}
Label.__index = Label
tui.Label = Label

function Label.new(o)
  return setmetatable({ x = o.x, y = o.y, text = o.text, style = o.style }, Label)
end

function Label:draw(screen)
  screen:text(self.x, self.y, self.text, self.style)
end

-- s padded with spaces to exactly w code points, centred (extra space goes right).
local function centre(s, w)
  local pad = w - str_width(s)
  if pad <= 0 then
    return s
  end
  local left = floor(pad / 2)
  return srep(" ", left) .. s .. srep(" ", pad - left)
end

local Button = {}
Button.__index = Button
tui.Button = Button

--- o: x, y, w, h=1, label, style, activeStyle (or active_style).
function Button.new(o)
  local self = setmetatable({
    x = o.x, y = o.y, w = o.w, h = o.h or 1, label = o.label,
    style = o.style, activeStyle = o.activeStyle or o.active_style,
  }, Button)
  self.rect = Rect.new(self.x, self.y, self.w, self.h)
  return self
end

--- Current rect (also refreshed into self.rect).
function Button:get_rect()
  local r = self.rect
  r.x, r.y, r.w, r.h = self.x, self.y, self.w, self.h
  return r
end

--- h < 3: one line `[ label ]` centred in w; h >= 3: a box with the label centred inside.
function Button:draw(screen, active)
  self:get_rect()
  local w, h = to_int(self.w), to_int(self.h)
  if w <= 0 or h <= 0 then
    return
  end
  local st = self.style
  if active then
    st = self.activeStyle
    if not st then
      st = {}
      if self.style then
        for k, v in pairs(self.style) do
          st[k] = v
        end
      end
      st.inverse = true
    end
  end
  if h >= 3 then
    screen:fill(self.x + 1, self.y + 1, w - 2, h - 2, " ", st)
    screen:box(self.x, self.y, w, h, nil, st)
    local inner = w - 2
    if inner > 0 then
      local lab = str_sub(self.label, 1, inner)
      local left = floor((inner - str_width(lab)) / 2)
      screen:text(self.x + 1 + left, self.y + 1 + floor((h - 3) / 2), lab, st)
    end
  else
    local s = "[ " .. self.label .. " ]"
    if str_width(s) > w then
      local keep = w - 4
      if keep < 0 then keep = 0 end
      s = str_sub("[ " .. str_sub(self.label, 1, keep) .. " ]", 1, w)
    end
    screen:text(self.x, self.y, centre(s, w), st)
  end
end

--- True for a left-button release inside the button's rect.
function Button:hit(ev)
  return is_click(ev) and self:get_rect():contains(ev.col, ev.row)
end

local Toggle = {}
Toggle.__index = Toggle
tui.Toggle = Toggle

--- o: x, y, label, value=false, style.
function Toggle.new(o)
  local self = setmetatable({
    x = o.x, y = o.y, label = o.label, value = o.value and true or false, style = o.style,
  }, Toggle)
  self.rect = Rect.new(self.x, self.y, 4 + str_width(self.label), 1)
  return self
end

function Toggle:get_rect()
  local r = self.rect
  r.x, r.y, r.w, r.h = self.x, self.y, 4 + str_width(self.label), 1
  return r
end

function Toggle:draw(screen)
  self:get_rect()
  screen:text(self.x, self.y, (self.value and "[x] " or "[ ] ") .. self.label, self.style)
end

--- On a left release inside the toggle: flips `value` and returns true.
function Toggle:hit(ev)
  if is_click(ev) and self:get_rect():contains(ev.col, ev.row) then
    self.value = not self.value
    return true
  end
  return false
end

local ProgressBar = {}
ProgressBar.__index = ProgressBar
tui.ProgressBar = ProgressBar

local PARTIAL_BLOCKS = { "▏", "▎", "▍", "▌", "▋", "▊", "▉" }

--- o: x, y, w, value=0, style.
function ProgressBar.new(o)
  return setmetatable({ x = o.x, y = o.y, w = o.w, value = o.value or 0, style = o.style }, ProgressBar)
end

function ProgressBar:draw(screen)
  local w = to_int(self.w)
  if w <= 0 then
    return
  end
  local v = tonumber(self.value) or 0
  if not (v > 0) then v = 0 end
  if v > 1 then v = 1 end
  local eighths = floor(v * w * 8 + 1e-9)
  if eighths > w * 8 then eighths = w * 8 end
  local full = floor(eighths / 8)
  local part = eighths % 8
  local s = srep("█", full)
  if part > 0 then
    s = s .. PARTIAL_BLOCKS[part]
  end
  s = s .. srep(" ", w - full - (part > 0 and 1 or 0))
  screen:text(self.x, self.y, s, self.style)
end

return tui
