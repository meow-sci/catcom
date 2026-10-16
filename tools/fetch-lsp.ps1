#Requires -Version 5.1
# fetch-lsp.ps1: PowerShell twin of tools/fetch-lsp.sh. Same language-server versions, sources,
# tools/assets.lock pins, upstream cross-checks and editor/<rid>/lsp/ layout.
#
# Sources (no Node.js needed at runtime):
#   LuaLS 3.19.2: https://github.com/LuaLS/lua-language-server/releases/tag/3.19.2
#   TypeScript 7.0.2 native tsc: GitHub release typescript/v7.0.2 of microsoft/typescript-go
#
# Usage: tools/fetch-lsp.ps1 [-Rid] <win-x64|linux-x64|osx-arm64> ... [-Force]
#   -Rid    one or more rids (default: win-x64 on Windows, otherwise the host platform)
#   -Force  replace existing installs even when the pinned versions are already present
# Env: KSA_EDITOR_DIR overrides the editor root (default <repo>/editor), for test copies.
#
# Works on Windows PowerShell 5.1 and PowerShell 7. Archives: Expand-Archive for .zip,
# tar.exe for .tar.gz / .tgz (ships with Windows 10 1803+).
param(
    [string[]]$Rid,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
} catch {
    # Not exposed on every PowerShell 7 build; its HTTPS defaults already include TLS 1.2.
}

$LuaLsVersion = '3.19.2'
$TsgoVersion  = '7.0.2'
$RepoRoot     = Split-Path -Parent $PSScriptRoot
$EditorDir    = $env:KSA_EDITOR_DIR
if (-not $EditorDir) { $EditorDir = Join-Path $RepoRoot 'editor' }
$Lock         = Join-Path (Join-Path $RepoRoot 'tools') 'assets.lock'
$LuaLsApi     = "https://api.github.com/repos/LuaLS/lua-language-server/releases/tags/$LuaLsVersion"
$LuaLsUrl     = "https://github.com/LuaLS/lua-language-server/releases/download/$LuaLsVersion"
$TsgoApi      = "https://api.github.com/repos/microsoft/typescript-go/releases/tags/typescript/v$TsgoVersion"
$TsgoUrl      = "https://github.com/microsoft/typescript-go/releases/download/typescript/v$TsgoVersion"
$Utf8NoBom    = New-Object System.Text.UTF8Encoding($false)

# Windows PowerShell 5.1 may run on .NET Framework without RuntimeInformation, so only the
# non-Windows branch (PowerShell 6+) touches it.
$HostOs   = 'other'
$HostArch = ''
if ($env:OS -eq 'Windows_NT') {
    $HostOs = 'windows'
} else {
    $HostArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)) {
        $HostOs = 'mac'
    } elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)) {
        $HostOs = 'linux'
    }
}
$OnWindows = ($HostOs -eq 'windows')
$OnMac     = ($HostOs -eq 'mac')

$LuaLsAssets = @{
    'win-x64'   = @{ Asset = "lua-language-server-$LuaLsVersion-win32-x64.zip";        Bin = 'bin/lua-language-server.exe' }
    'linux-x64' = @{ Asset = "lua-language-server-$LuaLsVersion-linux-x64.tar.gz";     Bin = 'bin/lua-language-server' }
    'osx-arm64' = @{ Asset = "lua-language-server-$LuaLsVersion-darwin-arm64.tar.gz";  Bin = 'bin/lua-language-server' }
}
$TsgoAssets = @{
    'win-x64'   = @{ Asset = 'typescript-win32-x64.tgz';    Bin = 'lib/tsc.exe' }
    'linux-x64' = @{ Asset = 'typescript-linux-x64.tgz';    Bin = 'lib/tsc' }
    'osx-arm64' = @{ Asset = 'typescript-darwin-arm64.tgz'; Bin = 'lib/tsc' }
}

$script:WorkRoot = $null
$script:Sizes = New-Object 'System.Collections.Generic.List[string]'

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

function Get-HostRid {
    if ($HostOs -eq 'windows') { return 'win-x64' }
    if ($HostOs -eq 'mac' -and $HostArch -eq 'Arm64') { return 'osx-arm64' }
    if ($HostOs -eq 'linux' -and $HostArch -eq 'X64') { return 'linux-x64' }
    Fail "cannot detect a supported host platform ($HostOs/$HostArch); pass -Rid explicitly: win-x64, linux-x64, osx-arm64"
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# Returns the pinned SHA-256 for an asset file name, or $null when tools/assets.lock has no entry.
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
            '# tools/assets.lock: SHA-256 pins for release assets fetched by tools/fetch-nvim.* and tools/fetch-lsp.*',
            '# Format: <sha256>  <asset file name>   (two spaces, as sha256sum prints it)',
            '# An asset missing here is pinned on its first download, after cross-checking the release''s own digests.',
            '# A version bump that keeps the same file name (nvim-win64.zip, typescript-*.tgz) needs the old line removed first.'
        ) -join "`n"
        [System.IO.File]::WriteAllText($Lock, $header + "`n", $Utf8NoBom)
    }
    [System.IO.File]::AppendAllText($Lock, "$Sha  $Asset`n", $Utf8NoBom)
}

function Get-InstalledVersion([string]$Dir) {
    $file = Join-Path $Dir '.ksa-version'
    if (Test-Path -LiteralPath $file) { return [System.IO.File]::ReadAllText($file).Trim() }
    return ''
}

# Returns the directory holding $Marker: the extract dir itself, or its first top-level folder containing it.
function Find-Root([string]$Extract, [string]$Marker) {
    if (Test-Path -LiteralPath (Join-Path $Extract $Marker)) { return $Extract }
    foreach ($dir in @(Get-ChildItem -LiteralPath $Extract -Directory)) {
        if (Test-Path -LiteralPath (Join-Path $dir.FullName $Marker)) { return $dir.FullName }
    }
    return $null
}

function Get-Remote([string]$Url, [string]$OutFile) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $OutFile
            return
        } catch {
            if ($attempt -eq 3) { Fail "download failed: $Url ($($_.Exception.Message))" }
            Start-Sleep -Seconds 2
        }
    }
}

# Cross-checks a first-time pin against what the release publishes. Fails on any mismatch.
function Test-Upstream([string]$Api, [string]$Asset, [string]$LocalSha, [string]$Work) {
    $relFile = Join-Path $Work 'release.json'
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $Api -OutFile $relFile -Headers @{ Accept = 'application/vnd.github+json' }
        $release = [System.IO.File]::ReadAllText($relFile) | ConvertFrom-Json
    } catch {
        Warn "could not query $Api; upstream cross-check skipped for $Asset"
        return
    }

    $upDigest = ''
    $sumUrl = ''
    $sumName = ''
    foreach ($item in @($release.assets)) {
        $name = [string]$item.name
        $digestProp = $item.PSObject.Properties['digest']
        $digest = ''
        if ($digestProp -and $digestProp.Value) { $digest = [string]$digestProp.Value }
        if ($name -eq $Asset -and $digest.StartsWith('sha256:')) {
            $upDigest = $digest.Substring(7).ToLowerInvariant()
        }
        $low = $name.ToLowerInvariant()
        $looksLikeSums = $low.EndsWith('.sha256') -or $low.EndsWith('.sha256sum') -or $low.EndsWith('.sha256sums') -or (@('shasum.txt', 'shasums.txt', 'sha256sums', 'sha256sum.txt', 'checksums.txt') -contains $low)
        if ($name -ne $Asset -and $looksLikeSums) {
            $sumUrl = [string]$item.browser_download_url
            $sumName = $name
        }
    }

    if ($sumUrl) {
        $sumFile = Join-Path $Work 'sumfile'
        Get-Remote $sumUrl $sumFile
        $published = ''
        foreach ($line in [System.IO.File]::ReadAllLines($sumFile)) {
            $parts = $line.Trim() -split '\s+'
            if ($parts.Count -ge 2 -and $parts[$parts.Count - 1].TrimStart('*') -eq $Asset) {
                $published = $parts[0].ToLowerInvariant()
                break
            }
        }
        if (-not $published) { Fail "$sumName does not list $Asset" }
        if ($published -ne $LocalSha) { Fail "$sumName lists $published for $Asset but the download is $LocalSha" }
        Write-Host "    upstream ${sumName}: matches"
    }
    if ($upDigest) {
        if ($upDigest -ne $LocalSha) { Fail "GitHub API digest for $Asset is $upDigest but the download is $LocalSha" }
        Write-Host "    upstream GitHub API digest for ${Asset}: matches (sha256:$upDigest)"
    }
    if ((-not $sumUrl) -and (-not $upDigest)) {
        Warn "release publishes no checksum for $Asset (no checksum asset, no API digest); pinning the local hash only"
    }
}

function Test-AssetVerified([string]$File, [string]$Asset, [string]$Api, [string]$Work) {
    $actual = Get-Sha256 $File
    $expected = Get-LockPin $Asset
    if ($expected) {
        if ($expected -ne $actual) {
            Fail "SHA-256 mismatch for ${Asset}: tools/assets.lock has $expected, download is $actual. If the version was bumped on purpose, remove the stale line from tools/assets.lock."
        }
        Write-Host "    sha256 ok ($Asset is pinned in tools/assets.lock)"
        return
    }
    Warn "no SHA-256 pinned for $Asset; cross-checking upstream before pinning it"
    Test-Upstream $Api $Asset $actual $Work
    Add-LockPin $actual $Asset
    Warn "pinned $Asset = $actual in tools/assets.lock; review that line before committing it"
}

function Get-SizeText([long]$Bytes) {
    return ('{0:N1} MB' -f ($Bytes / 1MB))
}

function Get-TreeSizeText([string]$Path) {
    $sum = (Get-ChildItem -LiteralPath $Path -Recurse -File | Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sum) { $sum = 0 }
    return Get-SizeText ([long]$sum)
}

# Extracts $archive into $extract by type. Dies if the tool is missing.
function Expand-Downloaded([string]$Archive, [string]$Extract) {
    if ($Archive.EndsWith('.zip')) {
        Expand-Archive -LiteralPath $Archive -DestinationPath $Extract -Force
    } else {
        & tar -xzf $Archive -C $Extract
        if ($LASTEXITCODE -ne 0) { Fail "tar could not extract $(Split-Path -Leaf $Archive)" }
    }
}

# Moves a staged or extracted folder to $Dest, moving any previous install into the temp dir.
function Set-InstallDir([string]$Source, [string]$Dest, [string]$Tag) {
    if (Test-Path -LiteralPath $Dest) {
        $old = Join-Path $script:WorkRoot "replaced-$Tag"
        New-Item -ItemType Directory -Force -Path $old | Out-Null
        Move-Item -LiteralPath $Dest -Destination (Join-Path $old 'previous')
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Dest) | Out-Null
    Move-Item -LiteralPath $Source -Destination $Dest
}

function Invoke-FetchLuaLs([string]$RidName) {
    $info = $LuaLsAssets[$RidName]
    $asset = [string]$info['Asset']
    $binRel = [string]$info['Bin']
    $dest = Join-Path (Join-Path (Join-Path $EditorDir $RidName) 'lsp') 'lua-language-server'
    $destBin = Join-Path $dest $binRel

    if ((-not $Force) -and (Test-Path -LiteralPath $destBin) -and ((Get-InstalledVersion $dest) -eq $LuaLsVersion)) {
        Write-Host "lua-language-server $LuaLsVersion already present for $RidName"
        $script:Sizes.Add(('{0,-10} luals unpacked {1} (already present, not re-fetched)' -f $RidName, (Get-TreeSizeText $dest)))
        return
    }

    $work = Join-Path (Join-Path $script:WorkRoot $RidName) 'luals'
    $extract = Join-Path $work 'extract'
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    $archive = Join-Path $work $asset
    Write-Host "[$RidName] downloading $LuaLsUrl/$asset"
    Get-Remote "$LuaLsUrl/$asset" $archive
    Test-AssetVerified $archive $asset $LuaLsApi $work

    Expand-Downloaded $archive $extract
    # The archive has no top-level folder (bin/ sits at the root); Find-Root also accepts one if present.
    $root = Find-Root $extract $binRel
    if (-not $root) { Fail "$asset does not contain $binRel" }
    if (-not (Test-Path -LiteralPath (Join-Path $root 'LICENSE'))) { Fail "$asset has no LICENSE file" }

    Set-InstallDir $root $dest "$RidName-luals"
    if (-not $OnWindows) { & chmod +x $destBin }
    [System.IO.File]::WriteAllText((Join-Path $dest '.ksa-version'), "$LuaLsVersion`n", $Utf8NoBom)
    if ($OnMac) {
        try { & xattr -dr com.apple.quarantine $dest | Out-Null } catch { }
    }

    $compressed = Get-SizeText ((Get-Item -LiteralPath $archive).Length)
    $unpacked = Get-TreeSizeText $dest
    $script:Sizes.Add(('{0,-10} luals compressed {1,-10} unpacked {2}' -f $RidName, $compressed, $unpacked))
    Write-Host "[$RidName] lua-language-server $LuaLsVersion installed at $dest"
}

function Invoke-FetchTsgo([string]$RidName) {
    $info = $TsgoAssets[$RidName]
    $asset = [string]$info['Asset']
    $binRel = [string]$info['Bin']
    $dest = Join-Path (Join-Path (Join-Path $EditorDir $RidName) 'lsp') 'typescript'
    $destBin = Join-Path $dest $binRel

    if ((-not $Force) -and (Test-Path -LiteralPath $destBin) -and ((Get-InstalledVersion $dest) -eq $TsgoVersion)) {
        Write-Host "typescript (tsc) $TsgoVersion already present for $RidName"
        $script:Sizes.Add(('{0,-10} tsc   unpacked {1} (already present, not re-fetched)' -f $RidName, (Get-TreeSizeText $dest)))
        return
    }

    $work = Join-Path (Join-Path $script:WorkRoot $RidName) 'typescript'
    $extract = Join-Path $work 'extract'
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    $archive = Join-Path $work $asset
    Write-Host "[$RidName] downloading $TsgoUrl/$asset"
    Get-Remote "$TsgoUrl/$asset" $archive
    Test-AssetVerified $archive $asset $TsgoApi $work

    Expand-Downloaded $archive $extract
    # The tarball holds package/ with lib/tsc[.exe], lib/*.d.ts, package.json and LICENSE.
    $root = Find-Root $extract $binRel
    if (-not $root) { Fail "$asset does not contain $binRel" }
    if (-not (Test-Path -LiteralPath (Join-Path $root 'LICENSE'))) { Fail "$asset has no LICENSE file" }
    $dts = @(Get-ChildItem -LiteralPath (Join-Path $root 'lib') -Filter '*.d.ts' -File)
    if ($dts.Count -eq 0) { Fail "$asset has no lib/*.d.ts files" }

    # Stage only what the LSP needs, then move the staged folder into place.
    $stage = Join-Path $work 'stage'
    $stageLib = Join-Path $stage 'lib'
    New-Item -ItemType Directory -Force -Path $stageLib | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $binRel) -Destination (Join-Path $stage $binRel)
    foreach ($f in $dts) { Copy-Item -LiteralPath $f.FullName -Destination $stageLib }
    Copy-Item -LiteralPath (Join-Path $root 'package.json') -Destination (Join-Path $stage 'package.json')
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $stage 'LICENSE')
    [System.IO.File]::WriteAllText((Join-Path $stage '.ksa-version'), "$TsgoVersion`n", $Utf8NoBom)

    Set-InstallDir $stage $dest "$RidName-typescript"
    if (-not $OnWindows) { & chmod +x $destBin }
    if ($OnMac) {
        try { & xattr -dr com.apple.quarantine $dest | Out-Null } catch { }
    }

    $compressed = Get-SizeText ((Get-Item -LiteralPath $archive).Length)
    $unpacked = Get-TreeSizeText $dest
    $script:Sizes.Add(('{0,-10} tsc   compressed {1,-10} unpacked {2}' -f $RidName, $compressed, $unpacked))
    Write-Host "[$RidName] typescript (tsc) $TsgoVersion installed at $dest"
}

# ---- main ----
$requested = @()
if ($Rid) {
    $requested = @($Rid | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)
}
$known = @('win-x64', 'linux-x64', 'osx-arm64')
foreach ($r in $requested) {
    if ($known -notcontains $r) { Fail "unknown rid '$r' (expected: win-x64 linux-x64 osx-arm64)" 2 }
}
if ($requested.Count -eq 0) { $requested = @(Get-HostRid) }

$script:WorkRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('ksa-fetch-lsp-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $script:WorkRoot | Out-Null
foreach ($r in $requested) {
    Invoke-FetchLuaLs $r
    Invoke-FetchTsgo $r
}
Remove-WorkDir

Write-Host ''
Write-Host 'sizes (compressed = downloaded archive, unpacked = installed folder):'
foreach ($line in $script:Sizes) { Write-Host $line }
