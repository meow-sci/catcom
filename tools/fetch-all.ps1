#Requires -Version 5.1
# fetch-all.ps1: PowerShell twin of tools/fetch-all.sh. ONE command that downloads every binary the
# mod ships, for EVERY platform (win-x64, linux-x64, osx-arm64), on this host. Nothing is compiled
# here. Run it once per clone and again whenever a pin under tools/ changes; `dotnet build` then
# produces a complete dist, and the dist target fails if anything listed below is missing.
#
#   tools/fetch-ts.ps1                  typescript.js + sucrase (V8 runtime assets, platform-neutral)
#   tools/fetch-nvim.ps1 <all rids>     Neovim                       -> editor/<rid>/nvim/
#   tools/fetch-lsp.ps1 <all rids>      lua-language-server, tsc     -> editor/<rid>/lsp/
#   tools/fetch-natives.ps1 <all rids>  LuaJIT + TypeScript parser   -> src/.../runtimes/<rid>/native/,
#                                       (cross-built by CI, GitHub release)  editor/<rid>/nvim/lib/nvim/parser/
#
# Usage: tools/fetch-all.ps1 [-Force] [-NativesFrom DIR]
#   -Force            re-download everything even when the pinned versions are present
#   -NativesFrom DIR  install natives from a local tools/build-natives.sh output folder
#                     instead of the GitHub release (what CI does after building them)
#
# Everything lands in gitignored folders. The macOS/Linux twin is tools/fetch-all.sh. The env
# overrides the sub-scripts honour (KSA_V8_ASSETS_DIR, KSA_EDITOR_DIR, KSA_RUNTIMES_DIR) also move
# the final completeness check to those folders.
#
# Works on Windows PowerShell 5.1 and PowerShell 7.
[CmdletBinding()]
param(
    [switch]$Force,
    [string]$NativesFrom
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Rids = @('win-x64', 'linux-x64', 'osx-arm64')

function Fail([string]$Message, [int]$Code = 1) {
    [Console]::Error.WriteLine("fetch-all: error: $Message")
    exit $Code
}

function Step([string]$Message) {
    Write-Host ''
    Write-Host "==> $Message"
}

# Runs one tools/*.ps1 script in this process. A thrown error or a non-zero exit stops everything;
# $LASTEXITCODE is reset first because a sub-script that ends normally leaves it untouched.
function Invoke-Fetch([string]$Script, [hashtable]$Params) {
    $path = Join-Path $PSScriptRoot $Script
    $global:LASTEXITCODE = 0
    try {
        & $path @Params
    } catch {
        Fail "tools/$Script failed: $($_.Exception.Message)"
    }
    if ($LASTEXITCODE -ne 0) { Fail "tools/$Script failed (exit code $LASTEXITCODE)" }
}

# Where a repo-relative path really lives: the repo, or the env override the sub-scripts used.
function Resolve-Shipped([string]$Rel) {
    $roots = @(
        @{ Prefix = 'src/Catcom.Runtime.V8/assets/';     Env = $env:KSA_V8_ASSETS_DIR },
        @{ Prefix = 'src/Catcom.Runtime.Lua/runtimes/';  Env = $env:KSA_RUNTIMES_DIR },
        @{ Prefix = 'editor/';                                 Env = $env:KSA_EDITOR_DIR }
    )
    foreach ($root in $roots) {
        if ($root['Env'] -and $Rel.StartsWith($root['Prefix'])) {
            return Join-Path $root['Env'] $Rel.Substring($root['Prefix'].Length)
        }
    }
    return Join-Path $RepoRoot $Rel
}

$script:Missing = 0
function Test-Shipped([string]$Rel) {
    if (Test-Path -LiteralPath (Resolve-Shipped $Rel) -PathType Leaf) {
        Write-Host ('  ok       {0}' -f $Rel)
    } else {
        Write-Host ('  MISSING  {0}' -f $Rel)
        $script:Missing++
    }
}

# ---- main ----
$common = @{}
if ($Force) { $common['Force'] = $true }
if ($NativesFrom -and -not (Test-Path -LiteralPath $NativesFrom -PathType Container)) {
    Fail "-NativesFrom: '$NativesFrom' is not a directory" 2
}

Step 'TypeScript compiler assets (tools/fetch-ts.ps1)'
Invoke-Fetch 'fetch-ts.ps1' $common

Step "Neovim for $Rids (tools/fetch-nvim.ps1)"
Invoke-Fetch 'fetch-nvim.ps1' ($common + @{ Rid = $Rids })

Step "Language servers for $Rids (tools/fetch-lsp.ps1)"
Invoke-Fetch 'fetch-lsp.ps1' ($common + @{ Rid = $Rids })

Step "Natives (LuaJIT, tree-sitter parser) for $Rids (tools/fetch-natives.ps1)"
$nativesParams = $common + @{ Rid = $Rids }
if ($NativesFrom) { $nativesParams['From'] = $NativesFrom }
Invoke-Fetch 'fetch-natives.ps1' $nativesParams

# The same list the dist target (src/Catcom.Dist) refuses to build without.
Step 'Checking that every shipped binary is present'
Test-Shipped 'src/Catcom.Runtime.V8/assets/typescript.js'
Test-Shipped 'src/Catcom.Runtime.V8/assets/sucrase.bundle.js'
foreach ($rid in $Rids) {
    switch ($rid) {
        'win-x64'   { $exe = '.exe'; $lib = 'lua51.dll';             $ext = 'dll' }
        'linux-x64' { $exe = '';     $lib = 'libluajit-5.1.so.2';    $ext = 'so' }
        'osx-arm64' { $exe = '';     $lib = 'libluajit-5.1.2.dylib'; $ext = 'dylib' }
    }
    Test-Shipped "src/Catcom.Runtime.Lua/runtimes/$rid/native/$lib"
    Test-Shipped "editor/$rid/nvim/bin/nvim$exe"
    Test-Shipped "editor/$rid/nvim/lib/nvim/parser/typescript.$ext"
    Test-Shipped "editor/$rid/lsp/lua-language-server/bin/lua-language-server$exe"
    Test-Shipped "editor/$rid/lsp/typescript/lib/tsc$exe"
}
if ($script:Missing -ne 0) { Fail "$($script:Missing) file(s) missing; see above" }
Write-Host ''
Write-Host 'fetch-all: complete. Build the mod with: dotnet build src/Catcom.Dist -c Release'
exit 0
