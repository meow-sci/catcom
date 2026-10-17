#Requires -Version 5.1
# fetch-natives.ps1: PowerShell twin of tools/fetch-natives.sh. Downloads the cross-built natives
# (LuaJIT + the tree-sitter TypeScript parser) for every platform from the GitHub release named in
# tools/natives.pin and installs them where the build and the editor expect them. Nothing is
# compiled on this host; CI builds the release with tools/build-natives.sh.
#
# Usage: tools/fetch-natives.ps1 [-Rid] <win-x64|linux-x64|osx-arm64> ... [-Force] [-From DIR]
#   -Rid    one or more rids (default: all three; any host fetches every platform)
#   -Force  reinstall even when the installed natives already match the pinned release
#   -From   install from locally built archives (tools/build-natives.sh output:
#           DIR/natives-<rid>.tar.gz + DIR/SHA256SUMS) instead of the release
#
# Release access: the natives repository (tools/natives.pin) is public, so no credentials are needed.
#   A logged-in gh CLI, or a token in GH_TOKEN / GITHUB_TOKEN, is used when present.
#
# Integrity: each archive is SHA-256 checked against tools/assets.lock (key "<tag>/<asset>"). A
#   first download is cross-checked against the release's SHA256SUMS asset and the GitHub API digest
#   before it is pinned. -From archives are checked against DIR/SHA256SUMS only and never pinned.
#
# Installs (per rid):
#   src/Catcom.Runtime.Lua/runtimes/<rid>/native/<luajit lib>, .luajit-commit, .ksa-natives
#   editor/<rid>/nvim/lib/nvim/parser/typescript.<ext>, .typescript.ksa-version, .ksa-natives
#       (needs that rid's Neovim: tools/fetch-nvim.ps1 runs first in tools/fetch-all.ps1)
#   tools/.cache/bin/<rid>/luajit[.exe]  (the CLI, for manual checks; not shipped)
# The archive's LuaJIT commit must equal tools/luajit.pin, otherwise the release is stale.
# Env: KSA_EDITOR_DIR / KSA_RUNTIMES_DIR override the two install roots (for test copies).
#
# Works on Windows PowerShell 5.1 and PowerShell 7. The archives are .tar.gz, extracted with tar
# (tar.exe ships with Windows 10 1803+).
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Rid,
    [switch]$Force,
    [string]$From
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ProgressPreference = 'SilentlyContinue'

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
} catch {
    # Not exposed on every PowerShell 7 build; its HTTPS defaults already include TLS 1.2.
}

$RepoRoot    = Split-Path -Parent $PSScriptRoot
$ToolsDir    = Join-Path $RepoRoot 'tools'
$Lock        = Join-Path $ToolsDir 'assets.lock'
$BinDir      = Join-Path (Join-Path $ToolsDir '.cache') 'bin'
$Utf8NoBom   = New-Object System.Text.UTF8Encoding($false)
$UserAgent   = 'catcom-fetch-natives'

# Env overrides may be relative to the shell's location, which .NET file APIs would not see.
function Resolve-FullPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return Join-Path (Get-Location).ProviderPath $Path
}
$RuntimesDir = $env:KSA_RUNTIMES_DIR
if ($RuntimesDir) { $RuntimesDir = Resolve-FullPath $RuntimesDir }
else { $RuntimesDir = Join-Path (Join-Path (Join-Path $RepoRoot 'src') 'Catcom.Runtime.Lua') 'runtimes' }
$EditorDir = $env:KSA_EDITOR_DIR
if ($EditorDir) { $EditorDir = Resolve-FullPath $EditorDir }
else { $EditorDir = Join-Path $RepoRoot 'editor' }

# Windows PowerShell 5.1 may run on .NET Framework without RuntimeInformation, so only the
# non-Windows branch (PowerShell 6+) touches it.
$HostOs = 'other'
if ($env:OS -eq 'Windows_NT') {
    $HostOs = 'windows'
} elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)) {
    $HostOs = 'mac'
} elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)) {
    $HostOs = 'linux'
}
$OnMac = ($HostOs -eq 'mac')

$RidInfo = @{
    'win-x64'   = @{ Lib = 'lua51.dll';             Ext = 'dll' }
    'linux-x64' = @{ Lib = 'libluajit-5.1.so.2';    Ext = 'so' }
    'osx-arm64' = @{ Lib = 'libluajit-5.1.2.dylib'; Ext = 'dylib' }
}

# Set from the pins in main; read by the functions below.
$Repo = ''
$Tag = ''
$LuaJitCommit = ''

$script:WorkRoot = $null
$script:Sizes = New-Object 'System.Collections.Generic.List[string]'
$script:Auth = 'none'            # gh | token | none
$script:Release = $null          # parsed release metadata once queried ($null when unavailable)
$script:ReleaseQueried = $false
$script:NativeExit = 0

function Remove-WorkDir {
    if ($script:WorkRoot -and (Test-Path -LiteralPath $script:WorkRoot)) {
        Remove-Item -LiteralPath $script:WorkRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Fail([string]$Message, [int]$Code = 1) {
    Remove-WorkDir
    [Console]::Error.WriteLine("error: $Message")
    exit $Code
}

function Warn([string]$Message) {
    Write-Warning $Message
}

# The value of a `key=value` line in tools/<File> (`#` comments and blank lines skipped).
function Get-PinValue([string]$File, [string]$Key) {
    $path = Join-Path $ToolsDir $File
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Fail "tools/$File is missing" }
    foreach ($line in [System.IO.File]::ReadAllLines($path)) {
        $text = $line.Trim()
        if ($text -eq '' -or $text.StartsWith('#')) { continue }
        if ($text.StartsWith("$Key=")) {
            $value = $text.Substring($Key.Length + 1).Trim()
            if ($value) { return $value }
        }
    }
    Fail "tools/$File has no '$Key=' line"
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# Returns the pinned SHA-256 for an asset key, or $null when tools/assets.lock has no entry.
function Get-LockPin([string]$Asset) {
    if (-not (Test-Path -LiteralPath $Lock)) { return $null }
    foreach ($line in [System.IO.File]::ReadAllLines($Lock)) {
        $text = $line.Trim()
        if ($text -eq '' -or $text.StartsWith('#')) { continue }
        $parts = $text -split '\s+'
        if ($parts.Count -ge 2 -and $parts[1] -eq $Asset) { return $parts[0].ToLowerInvariant() }
    }
    return $null
}

function Add-LockPin([string]$Sha, [string]$Asset) {
    if (-not (Test-Path -LiteralPath $Lock)) {
        $header = @(
            '# tools/assets.lock: SHA-256 pins for release assets fetched by tools/fetch-nvim.*, tools/fetch-lsp.* and tools/fetch-natives.*',
            '# Format: <sha256>  <asset file name>   (two spaces, as sha256sum prints it; natives are keyed <tag>/<asset>)',
            '# An asset missing here is pinned on its first download, after cross-checking the release''s own digests.',
            '# A version bump that keeps the same file name (nvim-win64.zip, typescript-*.tgz) needs the old line removed first.'
        ) -join "`n"
        [System.IO.File]::WriteAllText($Lock, $header + "`n", $Utf8NoBom)
    }
    [System.IO.File]::AppendAllText($Lock, "$Sha  $Asset`n", $Utf8NoBom)
}

# The text of a marker file with all whitespace removed ('' when the file is missing).
function Get-MarkerText([string]$File) {
    if (Test-Path -LiteralPath $File -PathType Leaf) { return ([System.IO.File]::ReadAllText($File) -replace '\s', '') }
    return ''
}

# The SHA-256 a sha256sum-style file lists for $Asset ('' when absent).
function Get-SumsEntry([string]$SumFile, [string]$Asset) {
    foreach ($line in [System.IO.File]::ReadAllLines($SumFile)) {
        $parts = $line.Trim() -split '\s+'
        if ($parts.Count -ge 2 -and $parts[$parts.Count - 1].TrimStart('*') -eq $Asset) { return $parts[0].ToLowerInvariant() }
    }
    return ''
}

function Get-Remote([string]$Url, [string]$OutFile, [string]$FailMessage = '') {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $OutFile
            return
        } catch {
            if ($attempt -eq 3) {
                if ($FailMessage) { Fail "$FailMessage ($($_.Exception.Message))" }
                Fail "download failed: $Url ($($_.Exception.Message))"
            }
            Start-Sleep -Seconds 2
        }
    }
}

# Extracts $Archive into $Extract by type. Dies if the tool is missing.
function Expand-Downloaded([string]$Archive, [string]$Extract) {
    if ($Archive.EndsWith('.zip')) {
        Expand-Archive -LiteralPath $Archive -DestinationPath $Extract -Force
    } else {
        & tar -xzf $Archive -C $Extract
        if ($LASTEXITCODE -ne 0) { Fail "tar could not extract $(Split-Path -Leaf $Archive)" }
    }
}

# Replaces $Dest with a copy of the extracted folder $Source. Copied, not moved: the temp dir may
# sit on another volume, and a cross-volume Move-Item fails on files tar extracted with a foreign
# owner (root extracting a CI-built archive); the temp copy goes with the work dir.
function Install-Dir([string]$Source, [string]$Dest) {
    if (Test-Path -LiteralPath $Dest) { Remove-Item -LiteralPath $Dest -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Dest) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Dest -Recurse
}

function Get-SizeText([long]$Bytes) {
    return ('{0:N1} MB' -f ($Bytes / 1MB))
}

function Get-FileSizeText([string]$Path) {
    return Get-SizeText ((Get-Item -LiteralPath $Path).Length)
}

# ---- release access ----

function Get-Token {
    if ($env:GH_TOKEN) { return $env:GH_TOKEN }
    if ($env:GITHUB_TOKEN) { return $env:GITHUB_TOKEN }
    return ''
}

# Runs a native command with its stderr discarded and returns its stdout lines; the exit code is
# left in $script:NativeExit (1 when the command could not start). $ErrorActionPreference is relaxed
# for the call because Windows PowerShell 5.1 turns redirected stderr into terminating errors under 'Stop'.
function Invoke-Quiet([string]$Exe, [string[]]$ArgList) {
    $ErrorActionPreference = 'Continue'
    $script:NativeExit = 1
    try {
        $out = @(& $Exe @ArgList 2>$null)
        $script:NativeExit = $LASTEXITCODE
        return $out
    } catch {
        return @()
    }
}

function Find-Auth {
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        $null = Invoke-Quiet 'gh' @('auth', 'status')
        if ($script:NativeExit -eq 0) { return 'gh' }
    }
    if (Get-Token) { return 'token' }
    return 'none'
}

# Reads the release metadata once, with whatever credentials exist ($null when that fails).
function Get-Release {
    if ($script:ReleaseQueried) { return $script:Release }
    $script:ReleaseQueried = $true
    $apiPath = "repos/$Repo/releases/tags/$Tag"
    try {
        if ($script:Auth -eq 'gh') {
            $lines = Invoke-Quiet 'gh' @('api', $apiPath)
            if ($script:NativeExit -ne 0) { throw "gh api $apiPath failed" }
            $text = $lines -join "`n"
        } else {
            $headers = @{ Accept = 'application/vnd.github+json' }
            if ($script:Auth -eq 'token') { $headers['Authorization'] = "Bearer $(Get-Token)" }
            $relFile = Join-Path $script:WorkRoot 'release.json'
            Invoke-WebRequest -UseBasicParsing -Uri "https://api.github.com/$apiPath" -OutFile $relFile -Headers $headers
            $text = [System.IO.File]::ReadAllText($relFile)
        }
        $script:Release = $text | ConvertFrom-Json
    } catch {
        $script:Release = $null
        Warn "could not read the release metadata for $Repo $Tag (asset digests will not be cross-checked)"
    }
    return $script:Release
}

# The 'id' or 'digest' (sha256 hex) of a release asset from the release metadata ('' when unknown).
function Get-ReleaseField([string]$Asset, [string]$Field) {
    $release = Get-Release
    if ($null -eq $release) { return '' }
    $assetsProp = $release.PSObject.Properties['assets']
    if (-not $assetsProp) { return '' }
    foreach ($item in @($assetsProp.Value)) {
        $nameProp = $item.PSObject.Properties['name']
        if (-not $nameProp -or [string]$nameProp.Value -ne $Asset) { continue }
        $prop = $item.PSObject.Properties[$Field]
        if (-not $prop -or $null -eq $prop.Value) { return '' }
        $value = [string]$prop.Value
        if ($Field -eq 'digest') {
            if (-not $value.StartsWith('sha256:')) { return '' }
            $value = $value.Substring(7).ToLowerInvariant()
        }
        return $value
    }
    return ''
}

# Downloads a release asset by id with a token. GitHub answers the API asset URL with a redirect to
# its storage backend, which must be fetched WITHOUT the Authorization header (it rejects it), so
# the redirect is read by hand instead of followed.
function Get-AssetByToken([string]$Asset, [string]$Id, [string]$Dest) {
    try { Add-Type -AssemblyName System.Net.Http -ErrorAction Stop } catch { }
    $location = ''
    $failure = ''
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $client = New-Object System.Net.Http.HttpClient($handler)
    try {
        $client.Timeout = [TimeSpan]::FromMinutes(10)
        $uri = "https://api.github.com/repos/$Repo/releases/assets/$Id"
        $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, $uri)
        $null = $request.Headers.TryAddWithoutValidation('Accept', 'application/octet-stream')
        $null = $request.Headers.TryAddWithoutValidation('Authorization', "Bearer $(Get-Token)")
        $null = $request.Headers.TryAddWithoutValidation('User-Agent', $UserAgent)
        $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            $status = [int]$response.StatusCode
            if ($status -ge 300 -and $status -lt 400) {
                $redirect = $response.Headers.Location
                if ($null -eq $redirect) {
                    $failure = "GitHub returned no download location for $Asset"
                } else {
                    if (-not $redirect.IsAbsoluteUri) { $redirect = New-Object System.Uri($request.RequestUri, $redirect) }
                    $location = $redirect.AbsoluteUri
                }
            } elseif ($status -eq 200) {
                # Served directly, no redirect: stream it to disk.
                $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                $file = [System.IO.File]::Create($Dest)
                try { $stream.CopyTo($file) } finally { $file.Dispose(); $stream.Dispose() }
            } else {
                $failure = "asset lookup failed for ${Asset}: HTTP $status from $uri (can the token read ${Repo}?)"
            }
        } finally {
            $response.Dispose()
        }
    } catch {
        $failure = "asset lookup failed for ${Asset}: $($_.Exception.Message)"
    } finally {
        $client.Dispose()
        $handler.Dispose()
    }
    if ($failure) { Fail $failure }
    if ($location) { Get-Remote $location $Dest "download failed: $Asset" }
}

# Fetches a release asset to $Dest: copied from -From, else by gh, by token (API asset URL, redirect
# followed without the auth header), or anonymously (public repository).
function Get-ReleaseAsset([string]$Asset, [string]$Dest) {
    if ($From) {
        $src = Join-Path $From $Asset
        if (-not (Test-Path -LiteralPath $src -PathType Leaf)) { Fail "$src not found (run tools/build-natives.sh first)" }
        Copy-Item -LiteralPath $src -Destination $Dest -Force
        return
    }
    Write-Host "    downloading $Asset from $Repo release $Tag ($($script:Auth))"
    switch ($script:Auth) {
        'gh' {
            & gh release download $Tag -R $Repo -p $Asset -O $Dest --clobber | Out-Host
            if ($LASTEXITCODE -ne 0) { Fail "gh could not download $Asset from release $Tag of $Repo (does the release exist? is the tag in tools/natives.pin right?)" }
        }
        'token' {
            if ($null -eq (Get-Release)) { Fail "release $Tag of $Repo is not readable with the given token" }
            $id = Get-ReleaseField $Asset 'id'
            if (-not $id) { Fail "release $Tag of $Repo has no asset named $Asset" }
            Get-AssetByToken $Asset $id $Dest
        }
        default {
            Get-Remote "https://github.com/$Repo/releases/download/$Tag/$Asset" $Dest "could not download $Asset anonymously from https://github.com/$Repo/releases/tag/$Tag (does the release exist? is the repo/tag in tools/natives.pin right?)"
        }
    }
}

# ---- verification ----

# Checks an archive's SHA-256: against DIR/SHA256SUMS for -From, else against the tools/assets.lock
# pin, cross-checking the release and pinning a first download. Returns the hash.
function Test-ArchiveVerified([string]$File, [string]$Asset) {
    $actual = Get-Sha256 $File
    if ($From) {
        $sums = Join-Path $From 'SHA256SUMS'
        if (-not (Test-Path -LiteralPath $sums -PathType Leaf)) { Fail "$sums is missing" }
        $published = Get-SumsEntry $sums $Asset
        if (-not $published) { Fail "$sums does not list $Asset" }
        if ($published -ne $actual) { Fail "SHA-256 mismatch for ${Asset}: $sums says $published, file is $actual" }
        Write-Host "    sha256 ok (matches $sums)"
        return $actual
    }
    $key = "$Tag/$Asset"
    $expected = Get-LockPin $key
    if ($expected) {
        if ($expected -ne $actual) {
            Fail "SHA-256 mismatch for ${key}: tools/assets.lock has $expected, download is $actual. If the release was republished on purpose, remove that line from tools/assets.lock."
        }
        Write-Host "    sha256 ok ($key is pinned in tools/assets.lock)"
        return $actual
    }
    Warn "no SHA-256 pinned for $key; cross-checking the release before pinning it"
    $sums = Join-Path $script:WorkRoot 'SHA256SUMS'
    if (-not (Test-Path -LiteralPath $sums)) { Get-ReleaseAsset 'SHA256SUMS' $sums }
    $published = Get-SumsEntry $sums $Asset
    if (-not $published) { Fail "the release's SHA256SUMS does not list $Asset" }
    if ($published -ne $actual) { Fail "the release's SHA256SUMS lists $published for $Asset but the download is $actual" }
    Write-Host '    release SHA256SUMS: matches'
    $digest = Get-ReleaseField $Asset 'digest'
    if ($digest) {
        if ($digest -ne $actual) { Fail "GitHub API digest for $Asset is $digest but the download is $actual" }
        Write-Host "    GitHub API digest: matches (sha256:$digest)"
    }
    Add-LockPin $actual $key
    Warn "pinned $key = $actual in tools/assets.lock; review that line before committing it"
    return $actual
}

# ---- install ----

function Install-Rid([string]$RidName) {
    $info = $RidInfo[$RidName]
    $lib = [string]$info['Lib']
    $ext = [string]$info['Ext']
    $asset = "natives-$RidName.tar.gz"
    $native = Join-Path (Join-Path $RuntimesDir $RidName) 'native'
    $nvimDir = Join-Path (Join-Path $EditorDir $RidName) 'nvim'
    $nvimLib = Join-Path (Join-Path $nvimDir 'lib') 'nvim'
    $parserDir = Join-Path $nvimLib 'parser'
    $libPath = Join-Path $native $lib
    $parserPath = Join-Path $parserDir "typescript.$ext"

    if ((-not $Force) -and (-not $From) `
        -and (Test-Path -LiteralPath $libPath) -and ((Get-MarkerText (Join-Path $native '.ksa-natives')) -eq $Tag) `
        -and ((Get-MarkerText (Join-Path $native '.luajit-commit')) -eq $LuaJitCommit) `
        -and (Test-Path -LiteralPath $parserPath) -and ((Get-MarkerText (Join-Path $parserDir '.ksa-natives')) -eq $Tag)) {
        Write-Host "[$RidName] natives $Tag already installed"
        $script:Sizes.Add(('{0,-10} natives  luajit {1,-10} parser {2} (already installed, not re-fetched)' -f $RidName, (Get-FileSizeText $libPath), (Get-FileSizeText $parserPath)))
        return
    }

    if (-not (Test-Path -LiteralPath $nvimLib -PathType Container)) {
        Fail "${RidName}: $nvimDir is not installed; run tools/fetch-nvim.ps1 $RidName first (tools/fetch-all.ps1 does this in order)"
    }

    $extract = Join-Path $script:WorkRoot $RidName
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    $archive = Join-Path $script:WorkRoot $asset
    Get-ReleaseAsset $asset $archive
    $sha = Test-ArchiveVerified $archive $asset
    Expand-Downloaded $archive $extract
    $srcLuajit = Join-Path $extract 'luajit'
    $srcParser = Join-Path $extract 'parser'
    $srcLib = Join-Path $srcLuajit $lib
    $srcParserLib = Join-Path $srcParser "typescript.$ext"
    if (-not (Test-Path -LiteralPath $srcLib -PathType Leaf)) { Fail "$asset does not contain luajit/$lib" }
    if (-not (Test-Path -LiteralPath $srcParserLib -PathType Leaf)) { Fail "$asset does not contain parser/typescript.$ext" }
    $commit = Get-MarkerText (Join-Path $srcLuajit '.luajit-commit')
    if ($commit -ne $LuaJitCommit) {
        Fail "$asset was built from LuaJIT $commit but tools/luajit.pin says ${LuaJitCommit}: the natives release is stale. Publish a new natives-vN release (run the Build and test workflow with publish_tag; CI builds it), then update tools/natives.pin."
    }

    $markerTag = $Tag
    if ($From) { $markerTag = 'local:' + $sha.Substring(0, 12) }

    # LuaJIT library: copy then rename, so a library a running test host has mapped is never
    # rewritten in place (macOS kills processes whose mapped code pages change).
    New-Item -ItemType Directory -Force -Path $native | Out-Null
    Remove-Item -LiteralPath (Join-Path $native '.ksa-natives') -Force -ErrorAction SilentlyContinue
    $tmp = Join-Path $native "$lib.tmp.$PID"
    Copy-Item -LiteralPath $srcLib -Destination $tmp -Force
    Move-Item -LiteralPath $tmp -Destination $libPath -Force
    Copy-Item -LiteralPath (Join-Path $srcLuajit '.luajit-commit') -Destination (Join-Path $native '.luajit-commit') -Force
    [System.IO.File]::WriteAllText((Join-Path $native '.ksa-natives'), "$markerTag`n", $Utf8NoBom)

    # Parser next to Neovim's own parsers (the sidecars are dot-prefixed so nvim's parser/typescript.*
    # lookup never matches them).
    New-Item -ItemType Directory -Force -Path $parserDir | Out-Null
    Remove-Item -LiteralPath (Join-Path $parserDir '.ksa-natives') -Force -ErrorAction SilentlyContinue
    foreach ($old in @(Get-ChildItem -LiteralPath $parserDir -File | Where-Object { $_.Name -like 'typescript.*' })) {
        Remove-Item -LiteralPath $old.FullName -Force
    }
    Copy-Item -LiteralPath $srcParserLib -Destination $parserPath -Force
    Copy-Item -LiteralPath (Join-Path $srcParser '.typescript.ksa-version') -Destination (Join-Path $parserDir '.typescript.ksa-version') -Force
    [System.IO.File]::WriteAllText((Join-Path $parserDir '.ksa-natives'), "$markerTag`n", $Utf8NoBom)

    # The CLI (not shipped): tools/.cache/bin/<rid>/
    $cli = Join-Path $srcLuajit 'cli'
    $binRid = Join-Path $BinDir $RidName
    if (Test-Path -LiteralPath $cli -PathType Container) {
        Install-Dir $cli $binRid
    }
    if ($OnMac) {
        foreach ($dir in @($native, $parserDir, $binRid)) {
            if (Test-Path -LiteralPath $dir) {
                try { & xattr -dr com.apple.quarantine $dir 2>$null | Out-Null } catch { }
            }
        }
    }

    $libBytes = (Get-Item -LiteralPath $libPath).Length
    $parserBytes = (Get-Item -LiteralPath $parserPath).Length
    $script:Sizes.Add(('{0,-10} natives  compressed {1,-10} luajit {2,-10} parser {3}' -f $RidName, (Get-FileSizeText $archive), (Get-SizeText $libBytes), (Get-SizeText $parserBytes)))
    Write-Host "[$RidName] installed $lib ($libBytes bytes) and typescript.$ext ($parserBytes bytes) from $asset"
}

# ---- main ----
$requested = @()
if ($Rid) {
    $requested = @($Rid | ForEach-Object { $_ -split '[,\s]+' } | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)
}
$known = @('win-x64', 'linux-x64', 'osx-arm64')
foreach ($r in $requested) {
    if ($known -notcontains $r) { Fail "unknown rid '$r' (expected: win-x64 linux-x64 osx-arm64)" 2 }
}
if ($requested.Count -eq 0) { $requested = $known }

$Repo = Get-PinValue 'natives.pin' 'repo'
$Tag = Get-PinValue 'natives.pin' 'tag'
$LuaJitCommit = Get-PinValue 'luajit.pin' 'commit'

if ($From) {
    if (-not (Test-Path -LiteralPath $From -PathType Container)) { Fail "-From: '$From' is not a directory" 2 }
    $From = (Resolve-Path -LiteralPath $From).ProviderPath
}
New-Item -ItemType Directory -Force -Path $RuntimesDir | Out-Null
[System.IO.File]::WriteAllText((Join-Path $RuntimesDir '.gitignore'), "*`n!.gitignore`n", $Utf8NoBom)

if ($From) {
    Write-Host "installing natives from $From"
} else {
    $script:Auth = Find-Auth
    Write-Host "natives release: $Repo $Tag (access: $($script:Auth))"
}

$script:WorkRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('ksa-fetch-natives-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $script:WorkRoot | Out-Null
try {
    foreach ($r in $requested) { Install-Rid $r }
} finally {
    # Also runs after Fail's exit and after an unexpected exception, so no temp dir is left behind.
    Remove-WorkDir
}

Write-Host ''
Write-Host 'sizes (compressed = downloaded archive, luajit/parser = installed files):'
foreach ($line in $script:Sizes) { Write-Host $line }
exit 0
