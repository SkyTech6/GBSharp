<#
.SYNOPSIS
    Downloads and verifies the pinned GB# emulator runtime into tools/emulator.

.DESCRIPTION
    Reads tools/emulator.lock.json, selects the archive matching the host OS and
    architecture, downloads it, verifies its SHA256 against the lock file, and
    extracts it to tools/emulator (which is gitignored).

    Deliberately the same script as get-gbdk.ps1: same host detection, same
    verification, same version stamp, same "check the payload is intact even
    when the stamp matches" behaviour. There is one acquisition story to learn
    rather than two.

    The runtime is built from https://github.com/SkyTech6/gbsharp-emulator, a
    fork of binjgb. Cloning that repository is only needed to change the
    emulator; this script is how everyone else gets it.

.PARAMETER Force
    Re-download and re-extract even if a matching install is already present.

.EXAMPLE
    pwsh tools/get-emulator.ps1
#>
[CmdletBinding()]
param(
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$toolsDir   = $PSScriptRoot
$lockPath   = Join-Path $toolsDir 'emulator.lock.json'
$installDir = Join-Path $toolsDir 'emulator'
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

# Keyed by .NET runtime identifier, which is also what the emulator's release
# archives are named after and what 'gbsharp publish <rid>' will take.
if ($onWindows) {
    $assetKey = 'win-x64'
}
elseif ($IsMacOS) {
    $assetKey = if ($isArm64) { 'osx-arm64' } else { 'osx-x64' }
}
else {
    $assetKey = if ($isArm64) { 'linux-arm64' } else { 'linux-x64' }
}

if (-not $isArm64 -and $arch -notin @('X64', 'Arm64')) {
    throw "The GB# emulator runtime is 64-bit only; this host reports '$arch'."
}

$asset = $lock.assets.$assetKey
if ($null -eq $asset) {
    throw "No emulator runtime pinned for platform '$assetKey' in $lockPath"
}

$expectedStamp = "$($lock.version)/$assetKey"

# The two library flavours the runtime ships, and the header they implement.
# Checked here rather than at the point of use: a library missing from one
# platform's archive is otherwise discovered as a P/Invoke failure on that
# platform alone, which is the most expensive place to find it.
function Assert-EmulatorLibraries {
    param([string] $Root, [string] $AssetKey)

    $prefix, $extension = switch -Wildcard ($AssetKey) {
        'win-*'   { '',    '.dll'   }
        'osx-*'   { 'lib', '.dylib' }
        default   { 'lib', '.so'    }
    }

    $binDir = Join-Path $Root 'bin'

    foreach ($flavour in @('gbsharp_emulator', 'gbsharp_emulator_debug')) {
        $libraryPath = Join-Path $binDir ($prefix + $flavour + $extension)
        if (-not (Test-Path $libraryPath)) {
            throw "'$libraryPath' is missing. The archive layout may have changed; re-run with -Force."
        }
    }

    $headerPath = Join-Path (Join-Path $Root 'include') 'gbsharp.h'
    if (-not (Test-Path $headerPath)) {
        throw "'$headerPath' is missing. The archive layout may have changed; re-run with -Force."
    }
}

# --- Skip if already installed -------------------------------------------
if (-not $Force -and (Test-Path $stampPath)) {
    $existing = (Get-Content -Raw -Path $stampPath).Trim()
    if ($existing -eq $expectedStamp) {
        # Verified on this path too: the stamp says which version was fetched,
        # not that the install is still intact.
        Assert-EmulatorLibraries -Root $installDir -AssetKey $assetKey
        Write-Host "GB# emulator runtime $($lock.version) already present at $installDir"
        exit 0
    }
    Write-Host "Replacing emulator runtime '$existing' with '$expectedStamp'"
}

Write-Host "Fetching GB# emulator runtime $($lock.version) ($assetKey)"
Write-Host "  $($asset.url)"

$tempRoot   = Join-Path ([System.IO.Path]::GetTempPath()) ("gbsharp-emulator-" + [Guid]::NewGuid().ToString('N'))
$archiveExt = if ($asset.url.EndsWith('.zip')) { '.zip' } else { '.tar.gz' }
$archive    = Join-Path $tempRoot ("emulator" + $archiveExt)
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

    # The release archives hold bin/ and include/ at the top level. Tolerate a
    # single wrapping directory as well, the way get-gbdk.ps1 does.
    $entries = @(Get-ChildItem -Path $stageDir)
    $payload = if ($entries.Count -eq 1 -and $entries[0].PSIsContainer) { $entries[0].FullName } else { $stageDir }

    if (Test-Path $installDir) {
        Remove-Item -Path $installDir -Recurse -Force
    }
    Move-Item -Path $payload -Destination $installDir

    Set-Content -Path $stampPath -Value $expectedStamp -Encoding utf8 -NoNewline
}
finally {
    if (Test-Path $tempRoot) {
        Remove-Item -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Assert-EmulatorLibraries -Root $installDir -AssetKey $assetKey

Write-Host "GB# emulator runtime $($lock.version) installed to $installDir"
Write-Host "  from $($lock.releaseUrl)"
