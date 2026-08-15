<#
.SYNOPSIS
    Downloads and verifies the pinned GBDK-2020 toolchain into tools/gbdk.

.DESCRIPTION
    Reads tools/gbdk.lock.json, selects the archive matching the host OS and
    architecture, downloads it, verifies its SHA256 against the lock file, and
    extracts it to tools/gbdk (which is gitignored).

    Runs under both Windows PowerShell 5.1 and PowerShell 7+ (pwsh) on Linux
    and macOS, so CI uses the same script on every platform.

.PARAMETER Force
    Re-download and re-extract even if a matching install is already present.

.EXAMPLE
    pwsh tools/get-gbdk.ps1
#>
[CmdletBinding()]
param(
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$toolsDir   = $PSScriptRoot
$lockPath   = Join-Path $toolsDir 'gbdk.lock.json'
$installDir = Join-Path $toolsDir 'gbdk'
$stampPath  = Join-Path $installDir '.gbsharp-version'

if (-not (Test-Path $lockPath)) {
    throw "Lock file not found: $lockPath"
}

$lock = Get-Content -Raw -Path $lockPath | ConvertFrom-Json

# --- Host detection -------------------------------------------------------
# Windows PowerShell 5.1 does not define $IsWindows; it is always Windows.
$onWindows = if ($null -eq (Get-Variable -Name IsWindows -ErrorAction SilentlyContinue)) { $true } else { $IsWindows }

$arch = try {
    [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
} catch {
    if ([Environment]::Is64BitOperatingSystem) { 'X64' } else { 'X86' }
}
$isArm64 = $arch -eq 'Arm64'
$is64Bit = $arch -in @('X64', 'Arm64')

if ($onWindows) {
    $assetKey = if ($is64Bit) { 'win64' } else { 'win32' }
}
elseif ($IsMacOS) {
    $assetKey = if ($isArm64) { 'macos-arm64' } else { 'macos' }
}
else {
    $assetKey = if ($isArm64) { 'linux-arm64' } else { 'linux64' }
}

$asset = $lock.assets.$assetKey
if ($null -eq $asset) {
    throw "No GBDK asset pinned for platform '$assetKey' in $lockPath"
}

$expectedStamp = "$($lock.version)/$assetKey"

# Every tool GB# shells out to. Checked here rather than at the point of use:
# a tool missing from one platform's archive is otherwise discovered as a link
# failure on that platform alone, which is the most expensive place to find it.
function Assert-GbdkTools {
    param([string] $Root, [bool] $Windows)

    $exe = if ($Windows) { '.exe' } else { '' }
    $binDir = Join-Path $Root 'bin'

    foreach ($tool in @('lcc', 'bankpack', 'romusage')) {
        $toolPath = Join-Path $binDir ($tool + $exe)
        if (-not (Test-Path $toolPath)) {
            throw "'$toolPath' is missing. The archive layout may have changed; re-run with -Force."
        }
    }
}

# --- Skip if already installed -------------------------------------------
if (-not $Force -and (Test-Path $stampPath)) {
    $existing = (Get-Content -Raw -Path $stampPath).Trim()
    if ($existing -eq $expectedStamp) {
        # Verified on this path too: the stamp says which version was fetched,
        # not that the install is still intact.
        Assert-GbdkTools -Root $installDir -Windows $onWindows
        Write-Host "GBDK-2020 $($lock.version) already present at $installDir"
        exit 0
    }
    Write-Host "Replacing GBDK '$existing' with '$expectedStamp'"
}

Write-Host "Fetching GBDK-2020 $($lock.version) ($assetKey)"
Write-Host "  $($asset.url)"

$tempRoot   = Join-Path ([System.IO.Path]::GetTempPath()) ("gbsharp-gbdk-" + [Guid]::NewGuid().ToString('N'))
$archiveExt = if ($asset.url.EndsWith('.zip')) { '.zip' } else { '.tar.gz' }
$archive    = Join-Path $tempRoot ("gbdk" + $archiveExt)
$stageDir   = Join-Path $tempRoot 'stage'

New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

try {
    $previousProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is far slower with a progress bar
    try {
        Invoke-WebRequest -Uri $asset.url -OutFile $archive -UseBasicParsing
    }
    finally {
        $ProgressPreference = $previousProgress
    }

    # --- Verify ----------------------------------------------------------
    $actualHash = (Get-FileHash -Path $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = $asset.sha256.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "SHA256 mismatch for $($asset.url).`n  expected: $expectedHash`n  actual:   $actualHash"
    }
    Write-Host "  sha256 verified"

    # --- Extract ---------------------------------------------------------
    if ($archiveExt -eq '.zip') {
        Expand-Archive -Path $archive -DestinationPath $stageDir -Force
    }
    else {
        & tar -xzf $archive -C $stageDir
        if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }
    }

    # GBDK archives contain a single top-level 'gbdk' directory. Tolerate both
    # that layout and a flat one.
    $entries = @(Get-ChildItem -Path $stageDir)
    $payload = if ($entries.Count -eq 1 -and $entries[0].PSIsContainer) { $entries[0].FullName } else { $stageDir }

    if (Test-Path $installDir) {
        Remove-Item -Path $installDir -Recurse -Force
    }
    Move-Item -Path $payload -Destination $installDir

    Set-Content -Path $stampPath -Value $expectedStamp -Encoding utf8 -NoNewline

    # Restore the executable bit that zip/tar extraction can drop on Unix.
    if (-not $onWindows) {
        & chmod -R +x (Join-Path $installDir 'bin')
    }
}
finally {
    if (Test-Path $tempRoot) {
        Remove-Item -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Assert-GbdkTools -Root $installDir -Windows $onWindows

$lccName = if ($onWindows) { 'lcc.exe' } else { 'lcc' }
$lccPath = Join-Path (Join-Path $installDir 'bin') $lccName

Write-Host "GBDK-2020 $($lock.version) installed to $installDir"
Write-Host "  compiler driver: $lccPath"
