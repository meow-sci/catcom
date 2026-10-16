-- ksa-scripting shipped Neovim configuration (NVIM_APPNAME=ksa-editor).
-- The mod launches:  nvim --clean -u <ModDir>/editor/config/ksa-editor/init.lua --listen 127.0.0.1:<port> <file>
-- with env: NVIM_APPNAME=ksa-editor, XDG_*_HOME (mod-owned dirs), KSA_LSP_DIR=<ModDir>/editor/<rid>/lsp,
--           KSA_NOSHELL=<ModDir>/editor/noshell[.cmd], optional KSA_SCRIPTS_DIR and KSA_SERVER_FILE.

local config_dir = vim.fn.fnamemodify(debug.getinfo(1, 'S').source:sub(2), ':p:h')
-- --clean drops user dirs from 'runtimepath'; add ours so queries/ (TypeScript highlighting) is found.
vim.opt.runtimepath:prepend(config_dir)

local is_windows = vim.fn.has('win32') == 1
local exe = is_windows and '.exe' or ''

-- No OS shell from inside the editor: :! and :terminal run a stub that prints a message and exits 1.
-- Fallback when KSA_NOSHELL is unset: the stub shipped beside config/ (<ModDir>/editor/noshell[.cmd]).
local noshell_fallback = vim.fn.fnamemodify(config_dir, ':h:h') .. '/noshell' .. (is_windows and '.cmd' or '')
vim.o.shell = (vim.env.KSA_NOSHELL and vim.env.KSA_NOSHELL ~= '') and vim.env.KSA_NOSHELL or noshell_fallback
vim.o.shellcmdflag = ''
vim.o.shellxquote = ''
vim.o.shellquote = ''

vim.o.backupcopy = 'yes'          -- overwrite in place (file watchers see one write)
vim.o.swapfile = false
vim.o.number = true
vim.o.signcolumn = 'yes'
vim.o.termguicolors = true
vim.o.mouse = 'a'                 -- purrTTY forwards SGR mouse; wheel outside mouse mode is not translated
vim.o.expandtab = true
vim.o.shiftwidth = 2
vim.o.tabstop = 2
vim.o.updatetime = 300
vim.o.completeopt = 'menuone,noselect,popup'
vim.keymap.set({ 'n', 'v' }, '<C-z>', '<Nop>')   -- never suspend inside the game
vim.diagnostic.config({ virtual_text = true, severity_sort = true })

vim.api.nvim_create_autocmd('FileType', {
  pattern = { 'lua', 'typescript' },
  callback = function(ev) pcall(vim.treesitter.start, ev.buf) end,
})

local lsp_dir = vim.env.KSA_LSP_DIR
if lsp_dir and lsp_dir ~= '' then
  local luals = lsp_dir .. '/lua-language-server/bin/lua-language-server' .. exe
  if vim.fn.executable(luals) == 1 then
    vim.lsp.config('lua_ls', {
      cmd = { luals,
              '--logpath=' .. vim.fn.stdpath('log') .. '/luals',
              '--metapath=' .. vim.fn.stdpath('cache') .. '/luals-meta' },
      filetypes = { 'lua' },
      root_markers = { '.luarc.json', '.git' },
      settings = { Lua = {
        runtime = { version = 'LuaJIT' },
        diagnostics = { globals = { 'ksa' } },
        workspace = { checkThirdParty = false },
        telemetry = { enable = false },
      } },
    })
    vim.lsp.enable('lua_ls')
  end
  local tsc = lsp_dir .. '/typescript/lib/tsc' .. exe
  if vim.fn.executable(tsc) == 1 then
    vim.lsp.config('tsc', {
      cmd = { tsc, '--lsp', '--stdio' },
      filetypes = { 'typescript', 'javascript' },
      root_markers = { 'tsconfig.json', '.git' },
    })
    vim.lsp.enable('tsc')
  end
end

vim.api.nvim_create_autocmd('LspAttach', {
  callback = function(ev)
    local client = vim.lsp.get_client_by_id(ev.data.client_id)
    if client and client:supports_method('textDocument/completion') then
      vim.lsp.completion.enable(true, client.id, ev.buf, { autotrigger = true })
    end
  end,
})

-- Tell the mod a script was saved (it reloads on the game thread).
vim.api.nvim_create_autocmd('BufWritePost', {
  callback = function(ev)
    vim.rpcnotify(0, 'ksa_saved', vim.fn.fnamemodify(ev.file, ':p'), ev.buf)
  end,
})

vim.api.nvim_create_autocmd('VimEnter', {
  callback = function()
    local f = vim.env.KSA_SERVER_FILE
    if f and f ~= '' and vim.v.servername ~= '' then
      pcall(vim.fn.writefile, { vim.v.servername }, f)
    end
    vim.api.nvim_echo({ { 'KSA script editor: :w saves & reloads, :q quits, K docs, grn rename, gra actions', 'Comment' } }, false, {})
  end,
})
