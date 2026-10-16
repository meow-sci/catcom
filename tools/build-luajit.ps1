#Requires -Version 5.1
# Builds the LuaJIT commit pinned in tools/luajit.pin for win-x64: lua51.dll with a static CRT
# (depends only on KERNEL32), tools/host_shim.c compiled in, FFI and JIT hooks configured as in
# tools/build-luajit.sh. Must run on Windows with Visual Studio (x64 C++ tools) installed.
#
# Usage: tools/build-luajit.ps1 [-Rid win-x64] [-Force]
# Output: src/KsaScripting.Runtime.Lua/runtimes/win-x64/native/lua51.dll (+ .luajit-commit)
#         tools/.cache/bin/win-x64/luajit.exe and lua51.dll (CLI copy used for verification)
param(
    [ValidateSet('win-x64')]
    [string]$Rid = 'win-x64',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -ne 'Windows_NT') {
    throw 'tools/build-luajit.ps1 builds win-x64 and must run on Windows (use tools/build-luajit.sh on macOS and Linux)'
}

$ToolsDir    = $PSScriptRoot
$RepoRoot    = Split-Path -Parent $ToolsDir
$PinPath     = Join-Path $ToolsDir 'luajit.pin'
$CacheDir    = Join-Path $ToolsDir '.cache'
$SrcDir      = Join-Path $CacheDir 'luajit-src'
$BuildDir    = Join-Path $CacheDir 'build-win-x64'
$BuildSrc    = Join-Path $BuildDir 'src'
$BinDir      = Join-Path (Join-Path $CacheDir 'bin') 'win-x64'
$RuntimesDir = Join-Path (Join-Path (Join-Path $RepoRoot 'src') 'KsaScripting.Runtime.Lua') 'runtimes'
$Native      = Join-Path (Join-Path $RuntimesDir 'win-x64') 'native'
$LibName     = 'lua51.dll'
$LibPath     = Join-Path $Native $LibName
$MarkerPath  = Join-Path $Native '.luajit-commit'

function Get-PinValue([string]$Key) {
    foreach ($line in Get-Content -LiteralPath $PinPath) {
        $t = $line.Trim()
        if ($t -eq '' -or $t.StartsWith('#')) { continue }
        $eq = $t.IndexOf('=')
        if ($eq -gt 0 -and $t.Substring(0, $eq) -ceq $Key) {
            return $t.Substring($eq + 1).Trim()
        }
    }
    throw "tools/luajit.pin has no '$Key=' line"
}

# Replaces one whole line of msvcbuild.bat. Throws unless exactly one such line exists, so an
# upstream change is detected instead of silently skipped. The lookahead keeps the CRLF intact.
function Patch-Line([string]$Text, [string]$Old, [string]$New) {
    $pattern = '(?m)^' + [regex]::Escape($Old) + '(?=\r?$)'
    $count = [regex]::Matches($Text, $pattern).Count
    if ($count -ne 1) {
        throw "msvcbuild.bat: expected exactly one line '$Old' but found $count (upstream changed?)"
    }
    return [regex]::Replace($Text, $pattern, $New.Replace('$', '$$'))
}

$Repo = Get-PinValue 'repo'
$Commit = Get-PinValue 'commit'
if ($Commit -cnotmatch '^[0-9a-f]{40}$') {
    throw "tools/luajit.pin commit is not a 40-char lowercase SHA: $Commit"
}

if (-not $Force -and (Test-Path -LiteralPath $LibPath) -and (Test-Path -LiteralPath $MarkerPath)) {
    $have = (Get-Content -LiteralPath $MarkerPath -Raw).Trim()
    if ($have -eq $Commit) {
        Write-Host "win-x64: up to date ($Commit)"
        exit 0
    }
}

# Cache and runtime ignore files, and a marker that is only rewritten after a successful build.
New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
Set-Content -LiteralPath (Join-Path $CacheDir '.gitignore') -Value '*' -Encoding ASCII
New-Item -ItemType Directory -Force -Path $RuntimesDir | Out-Null
Set-Content -LiteralPath (Join-Path $RuntimesDir '.gitignore') -Value @('*', '!.gitignore') -Encoding ASCII
if (Test-Path -LiteralPath $MarkerPath) { Remove-Item -LiteralPath $MarkerPath -Force }

# Source checkout: one clone, detached checkout of the pinned commit, cleaned on every build.
if (-not (Test-Path -LiteralPath (Join-Path $SrcDir '.git'))) {
    Write-Host "win-x64: cloning $Repo"
    if (Test-Path -LiteralPath $SrcDir) { Remove-Item -LiteralPath $SrcDir -Recurse -Force }
    & git clone --quiet $Repo $SrcDir
    if ($LASTEXITCODE -ne 0) { throw "git clone failed (exit $LASTEXITCODE)" }
}
& git -C $SrcDir cat-file -e "${Commit}^{commit}"
if ($LASTEXITCODE -ne 0) {
    Write-Host "win-x64: fetching $Commit"
    & git -C $SrcDir fetch --quiet origin $Commit
    if ($LASTEXITCODE -ne 0) {
        & git -C $SrcDir fetch --quiet origin
        if ($LASTEXITCODE -ne 0) { throw "git fetch failed (exit $LASTEXITCODE)" }
    }
}
& git -C $SrcDir -c advice.detachedHead=false checkout --quiet --force $Commit
if ($LASTEXITCODE -ne 0) { throw "git checkout $Commit failed (exit $LASTEXITCODE)" }
& git -C $SrcDir clean -fdxq
if ($LASTEXITCODE -ne 0) { throw "git clean failed (exit $LASTEXITCODE)" }
$Head = (& git -C $SrcDir rev-parse HEAD).Trim()
if ($Head -ne $Commit) { throw "checkout is at $Head, expected $Commit" }
$Ts = (& git -C $SrcDir show -s --format=%ct $Commit).Trim()
if ($Ts -cnotmatch '^[0-9]+$') { throw "could not read the commit timestamp for $Commit" }

# Fresh build copy without .git. LuaJIT reads the version from .relver when there is no .git.
Write-Host "win-x64: preparing $BuildDir"
if (Test-Path -LiteralPath $BuildDir) { Remove-Item -LiteralPath $BuildDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null
& robocopy $SrcDir $BuildDir /E /XD .git /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed (exit $LASTEXITCODE)" }
Set-Content -LiteralPath (Join-Path $BuildDir '.relver') -Value $Ts -Encoding ASCII
Copy-Item -LiteralPath (Join-Path $ToolsDir 'host_shim.c') -Destination (Join-Path $BuildSrc 'host_shim.c') -Force
Add-Content -LiteralPath (Join-Path $BuildSrc 'ljamalg.c') -Value ("`r`n" + '#include "host_shim.c"') -Encoding ASCII

# Patched copy of upstream msvcbuild.bat (CRLF, ASCII). Each patch is asserted to match once.
$MsvcSrc = Join-Path $BuildSrc 'msvcbuild.bat'
$msvc = [System.IO.File]::ReadAllText($MsvcSrc, [System.Text.Encoding]::ASCII)
$msvc = Patch-Line $msvc '@set LJCOMPILE=cl /nologo /c /O2 /W3 /D_CRT_SECURE_NO_DEPRECATE /D_CRT_STDIO_INLINE=__declspec(dllexport)__inline' '@set LJCOMPILE=cl /nologo /c /O2 /W3 /DLUAJIT_DISABLE_FFI /DLUAJIT_ENABLE_CHECKHOOK /D_CRT_SECURE_NO_DEPRECATE /D_CRT_STDIO_INLINE=__declspec(dllexport)__inline'
$msvc = Patch-Line $msvc '@set LJDYNBUILD=/DLUA_BUILD_AS_DLL /MD' '@set LJDYNBUILD=/DLUA_BUILD_AS_DLL /MT'
$msvc = Patch-Line $msvc '@set DASMFLAGS=-D WIN -D JIT -D FFI -D ENDIAN_LE -D FPU -D P64' '@set DASMFLAGS=-D WIN -D JIT -D ENDIAN_LE -D FPU -D P64'
$msvc = Patch-Line $msvc '@set DASMFLAGS=-D WIN -D JIT -D FFI -D ENDIAN_LE -D FPU' '@set DASMFLAGS=-D WIN -D JIT -D ENDIAN_LE -D FPU'
if ($msvc -match '-D FFI') { throw 'patched msvcbuild.bat still passes -D FFI to dynasm' }
[System.IO.File]::WriteAllText((Join-Path $BuildSrc 'msvcbuild_ksa.bat'), $msvc, [System.Text.Encoding]::ASCII)

# Visual Studio: find the x64 toolchain and its vcvars64.bat.
$ProgramFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
if ([string]::IsNullOrEmpty($ProgramFilesX86)) { throw 'the ProgramFiles(x86) environment variable is not set' }
$Vswhere = Join-Path $ProgramFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $Vswhere)) {
    throw "vswhere.exe not found at $Vswhere; install Visual Studio 2022 with the C++ desktop tools"
}
$VsFound = & $Vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0) { throw "vswhere failed (exit $LASTEXITCODE)" }
$VsPath = ([string]($VsFound | Select-Object -First 1)).Trim()
if ([string]::IsNullOrEmpty($VsPath)) { throw 'no Visual Studio installation with the x64 C++ build tools was found' }
$VcVars = Join-Path $VsPath 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path -LiteralPath $VcVars)) { throw "vcvars64.bat not found at $VcVars" }

# The build runs in a generated .cmd so that paths with spaces need no nested quoting from PowerShell.
# msvcbuild_ksa.bat can exit 0 after an internal failure, so the lua51.dll check below is required.
$BuildCmd = Join-Path $BuildDir 'ksa-msvc-build.cmd'
$buildLines = @(
    '@echo off',
    ('call "' + $VcVars + '"'),
    'if errorlevel 1 exit /b 1',
    ('cd /d "' + $BuildSrc + '"'),
    'if errorlevel 1 exit /b 1',
    'call msvcbuild_ksa.bat amalg',
    'exit /b %ERRORLEVEL%'
)
Set-Content -LiteralPath $BuildCmd -Value $buildLines -Encoding ASCII
Write-Host "win-x64: running msvcbuild_ksa.bat amalg in the x64 developer environment"
& $env:ComSpec /d /c $BuildCmd
if ($LASTEXITCODE -ne 0) { throw "msvcbuild_ksa.bat failed (exit $LASTEXITCODE)" }
$BuiltDll = Join-Path $BuildSrc $LibName
$BuiltExe = Join-Path $BuildSrc 'luajit.exe'
if (-not (Test-Path -LiteralPath $BuiltDll) -or -not (Test-Path -LiteralPath $BuiltExe)) {
    throw "msvcbuild_ksa.bat did not produce $LibName and luajit.exe; see the output above"
}

# dumpbin: exports and dependencies of the built DLL, captured to text files.
$VerifyCmd = Join-Path $BuildDir 'ksa-msvc-verify.cmd'
$verifyLines = @(
    '@echo off',
    ('call "' + $VcVars + '"'),
    'if errorlevel 1 exit /b 1',
    ('cd /d "' + $BuildSrc + '"'),
    'if errorlevel 1 exit /b 1',
    'dumpbin /nologo /exports lua51.dll > ksa-exports.txt',
    'if errorlevel 1 exit /b 1',
    'dumpbin /nologo /dependents lua51.dll > ksa-dependents.txt',
    'if errorlevel 1 exit /b 1',
    'exit /b 0'
)
Set-Content -LiteralPath $VerifyCmd -Value $verifyLines -Encoding ASCII
& $env:ComSpec /d /c $VerifyCmd
if ($LASTEXITCODE -ne 0) { throw "dumpbin failed (exit $LASTEXITCODE)" }

$Exports = Get-Content -LiteralPath (Join-Path $BuildSrc 'ksa-exports.txt') -Raw
foreach ($Sym in @('host_timeout_hook', 'host_raise', 'host_traceback', 'luaL_newstate', 'lua_sethook', 'luaJIT_setmode')) {
    if ($Exports -notmatch ('(?m)\s' + [regex]::Escape($Sym) + '\r?$')) {
        throw "lua51.dll does not export $Sym"
    }
}
if ($Exports -match '(?m)\sluaopen_ffi\r?$') { throw 'lua51.dll exports luaopen_ffi, so FFI was not disabled' }
Write-Host 'win-x64: exports ok (host_timeout_hook, host_raise, host_traceback, luaL_newstate, lua_sethook, luaJIT_setmode; no luaopen_ffi)'

$DepLines = Get-Content -LiteralPath (Join-Path $BuildSrc 'ksa-dependents.txt')
$Deps = @($DepLines | Where-Object { $_ -match '^\s+\S+\.dll\s*$' } | ForEach-Object { $_.Trim() })
if ($Deps.Count -ne 1 -or $Deps[0] -ine 'KERNEL32.dll') {
    throw "lua51.dll depends on [$($Deps -join ', ')]; expected only KERNEL32.dll (is the static CRT in effect?)"
}
Write-Host 'win-x64: dependencies: KERNEL32.dll'

# CLI checks, run from the staged copy in tools/.cache/bin/win-x64 (lua51.dll must sit beside it).
New-Item -ItemType Directory -Force -Path $BinDir | Out-Null
Copy-Item -LiteralPath $BuiltDll -Destination (Join-Path $BinDir $LibName) -Force
Copy-Item -LiteralPath $BuiltExe -Destination (Join-Path $BinDir 'luajit.exe') -Force
$Cli = Join-Path $BinDir 'luajit.exe'

$Ver = (& $Cli -v | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or -not $Ver.StartsWith('LuaJIT 2.1.' + $Ts + ' ')) {
    throw "unexpected luajit -v output: $Ver"
}
$Jit = (& $Cli '-e' 'print(jit.status())' | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or -not $Jit.StartsWith('true')) {
    throw "jit.status() did not start with true: $Jit"
}
$Ffi = (& $Cli '-e' "print(pcall(require, 'ffi'))" | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or -not $Ffi.StartsWith('false')) {
    throw "require('ffi') did not fail: $Ffi"
}
Write-Host "win-x64: $Ver"
Write-Host "win-x64: jit.status: $Jit"
Write-Host "win-x64: pcall(require, 'ffi'): $Ffi"

# Publish the verified library and marker.
New-Item -ItemType Directory -Force -Path $Native | Out-Null
Copy-Item -LiteralPath $BuiltDll -Destination $LibPath -Force
Set-Content -LiteralPath $MarkerPath -Value $Commit -Encoding ASCII
Write-Host "win-x64: wrote $LibPath ($((Get-Item -LiteralPath $LibPath).Length) bytes)"
Write-Host "win-x64: cli copy $Cli ($((Get-Item -LiteralPath $Cli).Length) bytes)"
