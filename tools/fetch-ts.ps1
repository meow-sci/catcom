# PowerShell twin of tools/fetch-ts.sh. Fetches typescript.js (+ licences) and the
# sucrase bundle into src/KsaScripting.Runtime.V8/assets/. npm tarballs are verified
# against the registry's SHA-512 integrity value before extraction. Downloaded
# content is only copied and read, never executed.
#
# Usage: tools/fetch-ts.ps1 [-Force]
# Env:   KSA_V8_ASSETS_DIR  overrides the destination (for testing).
param([switch]$Force)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ProgressPreference = 'SilentlyContinue'

if ($PSVersionTable.PSEdition -ne 'Core') {
    # Windows PowerShell 5.1 may default to TLS 1.0/1.1, which npm rejects.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}

$Root = Split-Path -Parent $PSScriptRoot
$Registry = 'https://registry.npmjs.org'
$SucraseVersion = '3.35.1'

if ($env:KSA_V8_ASSETS_DIR) {
    $Assets = [IO.Path]::GetFullPath($env:KSA_V8_ASSETS_DIR)
} else {
    $Assets = Join-Path $Root 'src/KsaScripting.Runtime.V8/assets'
}

$TsVersion = (Get-Content -LiteralPath (Join-Path $Root 'tools/typescript.pin') -Raw).Trim()
if ($TsVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "tools/typescript.pin must hold a plain x.y.z version, got '$TsVersion'"
}

New-Item -ItemType Directory -Force -Path $Assets | Out-Null
# Everything in this folder is generated; only the .gitignore itself is tracked.
[IO.File]::WriteAllText((Join-Path $Assets '.gitignore'), "*`n!.gitignore`n")

$Marker = Join-Path $Assets '.typescript-version'
$TsJs = Join-Path $Assets 'typescript.js'
$UpToDate = (-not $Force) -and (Test-Path -LiteralPath $TsJs) -and (Test-Path -LiteralPath $Marker) `
    -and ((Get-Content -LiteralPath $Marker -Raw).Trim() -eq $TsVersion)
if ($UpToDate) {
    Write-Output "fetch-ts: up to date (typescript $TsVersion in $Assets)"
    exit 0
}

# Downloads the package tarball into a fresh folder under $WorkDir, checks SHA-512
# against dist.integrity, extracts it, and returns the path to the package root.
function Get-VerifiedPackage {
    param([string]$Package, [string]$Version, [string]$WorkDir)

    $dir = Join-Path $WorkDir ($Package + '-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir | Out-Null

    $meta = Invoke-RestMethod -Uri "$Registry/$Package/$Version" -UseBasicParsing
    $tarball = [string]$meta.dist.tarball
    $integrity = [string]$meta.dist.integrity
    if (-not $tarball.StartsWith("$Registry/")) { throw "${Package}@${Version}: unexpected tarball URL $tarball" }
    if (-not $integrity.StartsWith('sha512-')) { throw "${Package}@${Version}: unsupported integrity value (expected sha512-...)" }

    $tgz = Join-Path $dir 'package.tgz'
    Invoke-WebRequest -UseBasicParsing -Uri $tarball -OutFile $tgz

    # Get-FileHash gives hex; convert to raw bytes, then base64, to match the registry's format.
    $hex = (Get-FileHash -LiteralPath $tgz -Algorithm SHA512).Hash
    $bytes = [byte[]]@(for ($i = 0; $i -lt $hex.Length; $i += 2) { [Convert]::ToByte($hex.Substring($i, 2), 16) })
    $actual = 'sha512-' + [Convert]::ToBase64String($bytes)
    if ($actual -ne $integrity) {
        throw "SHA-512 mismatch for ${Package}@${Version}`n  registry: $integrity`n  download: $actual"
    }

    $extract = Join-Path $dir 'x'
    New-Item -ItemType Directory -Path $extract | Out-Null
    & tar -xzf $tgz -C $extract
    if ($LASTEXITCODE -ne 0) { throw "tar failed for ${Package}@${Version}" }
    return (Join-Path $extract 'package')
}

$Work = Join-Path ([IO.Path]::GetTempPath()) ('ksa-ts-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $Work | Out-Null
try {
    $TsPkg = Get-VerifiedPackage -Package 'typescript' -Version $TsVersion -WorkDir $Work
    $TsJsSrc = Join-Path $TsPkg 'lib/typescript.js'
    if (-not (Test-Path -LiteralPath $TsJsSrc)) { throw "typescript@$TsVersion has no lib/typescript.js" }
    Copy-Item -LiteralPath $TsJsSrc -Destination $TsJs -Force
    Copy-Item -LiteralPath (Join-Path $TsPkg 'LICENSE.txt') -Destination (Join-Path $Assets 'typescript.LICENSE.txt') -Force
    $Notice = Join-Path $TsPkg 'ThirdPartyNoticeText.txt'
    if (Test-Path -LiteralPath $Notice) {
        Copy-Item -LiteralPath $Notice -Destination (Join-Path $Assets 'typescript.ThirdPartyNoticeText.txt') -Force
    }

    $BundleSrc = Join-Path $Root 'spikes/assets/sucrase.bundle.js'
    if (-not (Test-Path -LiteralPath $BundleSrc)) { throw 'missing spikes/assets/sucrase.bundle.js' }
    Copy-Item -LiteralPath $BundleSrc -Destination (Join-Path $Assets 'sucrase.bundle.js') -Force

    $SucPkg = Get-VerifiedPackage -Package 'sucrase' -Version $SucraseVersion -WorkDir $Work
    if (-not (Test-Path -LiteralPath (Join-Path $SucPkg 'LICENSE'))) { throw "sucrase@$SucraseVersion has no LICENSE" }
    Copy-Item -LiteralPath (Join-Path $SucPkg 'LICENSE') -Destination (Join-Path $Assets 'sucrase.LICENSE') -Force

    # Written last: a run that dies part-way leaves no marker, so the next run redoes everything.
    [IO.File]::WriteAllText($Marker, "$TsVersion`n")
}
finally {
    Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output "fetch-ts: wrote typescript $TsVersion and sucrase $SucraseVersion to $Assets"
foreach ($name in @('typescript.js', 'typescript.LICENSE.txt', 'typescript.ThirdPartyNoticeText.txt',
                    'sucrase.bundle.js', 'sucrase.LICENSE', '.typescript-version', '.gitignore')) {
    $p = Join-Path $Assets $name
    if (Test-Path -LiteralPath $p) {
        '{0,10} bytes  {1}' -f (Get-Item -LiteralPath $p -Force).Length, $p | Write-Output   # -Force: dot-files are hidden on macOS/Linux
    }
}
