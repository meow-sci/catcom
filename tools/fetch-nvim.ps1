#Requires -Version 5.1
# fetch-nvim.ps1: PowerShell twin of tools/fetch-nvim.sh. Same Neovim version, sources,
# tools/assets.lock pins, upstream cross-checks and editor/<rid>/nvim/ layout.
#
# Usage: tools/fetch-nvim.ps1 [-Rid] <win-x64|linux-x64|osx-arm64> ... [-Force]
#   -Rid    one or more rids (default: win-x64 on Windows, otherwise the host platform)
#   -Force  replace an existing install even when the pinned version is already present
# Env: KSA_EDITOR_DIR overrides the editor root (default <repo>/editor), for test copies.
#
# Works on Windows PowerShell 5.1 and PowerShell 7. Archives: Expand-Archive for .zip,
# tar for .tar.gz (tar.exe ships with Windows 10 1803+).
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

$NvimVersion = '0.12.6'
$RepoRoot    = Split-Path -Parent $PSScriptRoot
$EditorDir   = $env:KSA_EDITOR_DIR
if (-not $EditorDir) { $EditorDir = Join-Path $RepoRoot 'editor' }
$Lock        = Join-Path (Join-Path $RepoRoot 'tools') 'assets.lock'
$ReleaseApi  = "https://api.github.com/repos/neovim/neovim/releases/tags/v$NvimVersion"
$ReleaseUrl  = "https://github.com/neovim/neovim/releases/download/v$NvimVersion"
$LicenseUrl  = "https://raw.githubusercontent.com/neovim/neovim/v$NvimVersion/LICENSE.txt"
$Utf8NoBom   = New-Object System.Text.UTF8Encoding($false)
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
$OnWindows  = ($HostOs -eq 'windows')
$OnMac      = ($HostOs -eq 'mac')

$RidAssets = @{
    'win-x64'   = @{ Asset = 'nvim-win64.zip';           Bin = 'bin/nvim.exe' }
    'linux-x64' = @{ Asset = 'nvim-linux-x86_64.tar.gz'; Bin = 'bin/nvim' }
    'osx-arm64' = @{ Asset = 'nvim-macos-arm64.tar.gz';  Bin = 'bin/nvim' }
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

function Invoke-FetchNvim([string]$RidName) {
    $info = $RidAssets[$RidName]
    $asset = [string]$info['Asset']
    $dest = Join-Path (Join-Path $EditorDir $RidName) 'nvim'
    $destBin = Join-Path $dest $info['Bin']

    if ((-not $Force) -and (Test-Path -LiteralPath $destBin) -and ((Get-InstalledVersion $dest) -eq $NvimVersion)) {
        Write-Host "nvim $NvimVersion already present for $RidName"
        $script:Sizes.Add(('{0,-10} nvim  unpacked {1} (already present, not re-fetched)' -f $RidName, (Get-TreeSizeText $dest)))
        return
    }

    $work = Join-Path (Join-Path $script:WorkRoot $RidName) 'nvim'
    $extract = Join-Path $work 'extract'
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    $archive = Join-Path $work $asset
    Write-Host "[$RidName] downloading $ReleaseUrl/$asset"
    Get-Remote "$ReleaseUrl/$asset" $archive
    Test-AssetVerified $archive $asset $ReleaseApi $work

    if ($RidName -eq 'win-x64') {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
        try {
            $hasVcRuntime = @($zip.Entries | Where-Object { $_.FullName -match 'vcruntime' }).Count -gt 0
        } finally {
            $zip.Dispose()
        }
        if (-not $hasVcRuntime) {
            Warn "$asset contains no vcruntime*.dll: nvim.exe imports VCRUNTIME140.dll, so machines without the VC++ redistributable need vc_redist.x64.exe or an app-local copy"
        }
    }

    if ($asset.EndsWith('.zip')) {
        Expand-Archive -LiteralPath $archive -DestinationPath $extract -Force
    } else {
        & tar -xzf $archive -C $extract
        if ($LASTEXITCODE -ne 0) { Fail "tar could not extract $asset" }
    }
    $root = Find-Root $extract $info['Bin']
    if (-not $root) { Fail "$asset does not contain $($info['Bin'])" }

    # Replace any previous install by moving it into the temp dir (removed on exit).
    $parent = Split-Path -Parent $dest
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    if (Test-Path -LiteralPath $dest) {
        $old = Join-Path $script:WorkRoot ("replaced-$RidName-nvim")
        New-Item -ItemType Directory -Force -Path $old | Out-Null
        Move-Item -LiteralPath $dest -Destination (Join-Path $old 'previous')
    }
    Move-Item -LiteralPath $root -Destination $dest

    if ($RidName -eq 'win-x64') {
        Remove-Item -LiteralPath (Join-Path $dest 'bin/xxd.exe') -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $dest 'bin/win32yank.exe') -Force -ErrorAction SilentlyContinue
    } elseif (-not $OnWindows) {
        & chmod +x $destBin
    }

    $license = Join-Path $dest 'LICENSE.txt'
    if (-not (Test-Path -LiteralPath $license)) {
        # Top level only: nested LICENSE files (e.g. share/nvim/runtime/pack/dist/opt/netrw) belong to plugins.
        $topLicense = @(Get-ChildItem -LiteralPath $dest -File | Where-Object { $_.Name -like 'LICENSE*' })
        if ($topLicense.Count -gt 0) {
            Copy-Item -LiteralPath $topLicense[0].FullName -Destination $license
        } else {
            Write-Host "    archive has no top-level LICENSE; fetching $LicenseUrl"
            Get-Remote $LicenseUrl $license
        }
    }

    [System.IO.File]::WriteAllText((Join-Path $dest '.ksa-version'), "$NvimVersion`n", $Utf8NoBom)
    if ($OnMac) {
        try { & xattr -dr com.apple.quarantine $dest | Out-Null } catch { }
    }

    $compressed = Get-SizeText ((Get-Item -LiteralPath $archive).Length)
    $unpacked = Get-TreeSizeText $dest
    $script:Sizes.Add(('{0,-10} nvim  compressed {1,-10} unpacked {2}' -f $RidName, $compressed, $unpacked))
    Write-Host "[$RidName] nvim $NvimVersion installed at $dest"
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

$script:WorkRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('ksa-fetch-nvim-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $script:WorkRoot | Out-Null
foreach ($r in $requested) { Invoke-FetchNvim $r }
Remove-WorkDir

Write-Host ''
Write-Host 'sizes (compressed = downloaded archive, unpacked = installed folder):'
foreach ($line in $script:Sizes) { Write-Host $line }
