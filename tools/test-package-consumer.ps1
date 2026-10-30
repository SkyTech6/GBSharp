<#
.SYNOPSIS
Validates the exact release packages from an isolated installed-tool consumer.
.DESCRIPTION
Requires PowerShell 7, .NET 10, and the pinned GBDK/runtime dependencies. With
-PackageDirectory, never repacks: release CI must pass the feed it will publish.
All game commands execute outside the checkout. NuGet source mapping forbids
GBSharp dependencies from public feeds. Logs and browser exports are retained.
#>
[CmdletBinding()]
param(
    [string] $PackageDirectory,
    [string] $ArtifactsDirectory,
    [string] $SampleDirectory = (Join-Path $PSScriptRoot '../Samples/ComposedScrolling'),
    [string] $SaveProjectDirectory = (Join-Path $PSScriptRoot 'browser-delivery/save-fixture'),
    [string] $GbdkPath = (Join-Path $PSScriptRoot 'gbdk'),
    [int] $ProcessTimeoutSeconds = 240
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $ArtifactsDirectory) { $ArtifactsDirectory = Join-Path $repository 'artifacts/package-consumer' }
$ArtifactsDirectory = [IO.Path]::GetFullPath($ArtifactsDirectory)
New-Item -ItemType Directory -Force -Path $ArtifactsDirectory | Out-Null
$workspace = Join-Path ([IO.Path]::GetTempPath()) ('gbsharp-consumer-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workspace | Out-Null
$logIndex = 0

function Invoke-Checked([string] $Executable, [string[]] $Arguments, [string] $Directory, [string] $ExpectedError = '') {
    $script:logIndex++
    $logPath = Join-Path $ArtifactsDirectory ('{0:d3}.log' -f $script:logIndex)
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.WorkingDirectory = $Directory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    try {
        if (-not $process.Start()) { throw "Could not start $Executable" }
        $started = $true
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = $false
        if (-not $process.WaitForExit($ProcessTimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            $timedOut = $true
        }
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        "Directory: $Directory`nCommand: $Executable $Arguments`n$output" | Set-Content $logPath
        if ($timedOut) { throw "Timed out: $Executable $Arguments; see $logPath" }
        if ($ExpectedError) {
            if ($process.ExitCode -eq 0 -or $output -notmatch [regex]::Escape($ExpectedError)) {
                throw "Expected failure $ExpectedError; see $logPath"
            }
        } elseif ($process.ExitCode -ne 0) { throw "Exit $($process.ExitCode); see $logPath" }
        Write-Host "Passed: $Executable $Arguments"
        return $output
    } finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
}

function Export-Game([string] $Name, [string] $Directory) {
    $folder = Join-Path $ArtifactsDirectory "$Name/folder"
    $single = Join-Path $ArtifactsDirectory "$Name/single"
    Invoke-Checked $cli @('build', $Directory, '--gbdk-path', $GbdkPath) $workspace | Out-Null
    $roms = @(Get-ChildItem (Join-Path $Directory 'build') -File | Where-Object { $_.Extension -in '.gb', '.gbc' })
    if ($roms.Count -ne 1) { throw "Expected a ROM in $Directory/build" }
    Invoke-Checked $cli @('publish', 'web', $Directory, '--gbdk-path', $GbdkPath, '--out', $folder) $workspace | Out-Null
    Invoke-Checked $cli @('publish', 'web', $Directory, '--gbdk-path', $GbdkPath, '--out', $single, '--single-file') $workspace | Out-Null
    $singlePages = @(Get-ChildItem $single -Filter '*.html')
    if (-not (Test-Path (Join-Path $folder 'index.html')) -or $singlePages.Count -ne 1) {
        throw "Missing published page for $Name"
    }
    return @{ folder = (Join-Path $folder 'index.html'); single = $singlePages[0].FullName }
}

$environmentNames = @('NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'NUGET_FALLBACK_PACKAGES', 'MSBuildSDKsPath')
$savedEnvironment = @{}
foreach ($name in $environmentNames) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    if (-not $PackageDirectory) {
        $PackageDirectory = Join-Path $workspace 'packed'
        Invoke-Checked 'dotnet' @('pack', (Join-Path $repository 'GBSharp.slnx'), '-c', 'Release', '-o', $PackageDirectory) $repository | Out-Null
    }
    $PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
    $packages = @(Get-ChildItem $PackageDirectory -Filter '*.nupkg')
    $expectedIds = @('gbsharp', 'GBSharp.Sdk', 'GBSharp.Framework', 'GBSharp.Analyzers')
    $versions = @{}
    $packageFiles = @{}
    $feed = Join-Path $workspace 'feed'
    New-Item -ItemType Directory -Path $feed | Out-Null
    foreach ($package in $packages) {
        $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
        try {
            $nuspec = @($archive.Entries | Where-Object { $_.FullName -match '^[^/]+\.nuspec$' })
            if ($nuspec.Count -ne 1) { throw "Invalid package: $package" }
            $reader = [IO.StreamReader]::new($nuspec[0].Open())
            try { [xml] $metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $id = [string] $metadata.package.metadata.id
            if ($id -notin $expectedIds -or $versions.ContainsKey($id)) { throw "Unexpected/duplicate package $id" }
            $versions[$id] = [string] $metadata.package.metadata.version
            $packageFiles[$id] = $package.FullName
        } finally { $archive.Dispose() }
        Copy-Item -LiteralPath $package.FullName -Destination $feed
    }
    if ($versions.Count -ne 4 -or @($versions.Values | Select-Object -Unique).Count -ne 1) {
        throw 'Expected exactly the four release packages, with matching versions.'
    }
    $version = $versions['gbsharp']
    $cache = Join-Path $workspace 'packages'
    $env:NUGET_PACKAGES = $cache
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $workspace 'http-cache'
    $env:NUGET_FALLBACK_PACKAGES = ''
    # An inherited SDK override must not bypass this consumer's SDK resolver.
    $env:MSBuildSDKsPath = $null
    $config = Join-Path $workspace 'NuGet.Config'
    $escapedFeed = [Security.SecurityElement]::Escape($feed)
    @"
<configuration>
  <packageSources><clear /><add key="staged" value="$escapedFeed" /><add key="nuget" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <fallbackPackageFolders><clear /></fallbackPackageFolders>
  <packageSourceMapping>
    <packageSource key="staged"><package pattern="GBSharp*" /><package pattern="gbsharp" /></packageSource>
    <packageSource key="nuget"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content $config
    # Both the generated Sdk attribute and resolver's global.json pin this feed's version.
    @{ 'msbuild-sdks' = @{ 'GBSharp.Sdk' = $version } } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $workspace 'global.json')
    $toolDirectory = Join-Path $workspace 'tool'
    Invoke-Checked 'dotnet' @('tool', 'install', 'gbsharp', '--version', $version, '--tool-path', $toolDirectory, '--configfile', $config, '--no-cache') $workspace | Out-Null
    $cli = Join-Path $toolDirectory $(if ($IsWindows) { 'gbsharp.exe' } else { 'gbsharp' })
    $toolAssembly = @(Get-ChildItem $toolDirectory -Recurse -Filter 'gbsharp.dll' | Where-Object { $_.Directory.Name -eq 'any' })
    if ($toolAssembly.Count -ne 1) { throw 'Could not identify the installed tool assembly.' }
    $toolContent = $toolAssembly[0].Directory.FullName
    foreach ($file in @('GBSharp.Framework.dll', 'gbdk.lock.json', 'emulator.lock.json', 'WebPlayer/index.html', 'WebPlayer/player.js')) {
        if (-not (Test-Path (Join-Path $toolContent $file))) { throw "Installed tool is missing $file" }
    }
    $doctor = Invoke-Checked $cli @('doctor', '--gbdk-path', $GbdkPath) $workspace
    if (-not $doctor.Contains((Join-Path $toolContent 'GBSharp.Framework.dll'))) { throw 'Framework did not resolve from the installed tool.' }
    foreach ($template in @('empty', 'sprite', 'background')) {
        $game = Join-Path $workspace $template
        Invoke-Checked $cli @('new', $template, '--template', $template, '--out', $game) $workspace | Out-Null
        $project = Join-Path $game "$template.csproj"
        [xml] $projectXml = Get-Content $project -Raw
        if ($projectXml.Project.Sdk -cne "GBSharp.Sdk/$version") { throw 'Scaffolded SDK version differs from staged package.' }
        foreach ($reference in $projectXml.Project.ItemGroup.PackageReference) {
            if ($reference.Version -cne $version) { throw 'Scaffolded dependency version differs from staged package.' }
        }
        Invoke-Checked 'dotnet' @('restore', $project, '--configfile', $config, '--no-cache', '-p:RestoreFallbackFolders=') $workspace | Out-Null
        Invoke-Checked 'dotnet' @('build', $project, '--no-restore', '-p:DesignTimeBuild=true') $workspace | Out-Null
        $assets = Get-Content (Join-Path $game 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
        if ($assets.packageFolders.Keys.Count -ne 1 -or -not $assets.packageFolders.Keys[0].TrimEnd('/','\').Equals($cache.TrimEnd('/','\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Consumer used an unexpected package cache.' }
        foreach ($id in @('GBSharp.Framework', 'GBSharp.Analyzers')) {
            if (-not $assets.libraries.ContainsKey("$id/$version")) { throw "Consumer did not bind $id/$version" }
        }
        Invoke-Checked 'dotnet' @('build', $project, '--no-restore') $workspace 'GBS0509' | Out-Null
        # The managed build override exercises actual Roslyn analyzer loading.
        $probe = Join-Path $game 'AnalyzerProbe.cs'
        'public static class AnalyzerProbe { public static double Ratio; }' | Set-Content $probe
        try { Invoke-Checked 'dotnet' @('build', $project, '--no-restore', '-p:GBSharpAllowManagedBuild=true') $workspace 'GBS0002' | Out-Null }
        finally { Remove-Item -LiteralPath $probe }
        Invoke-Checked $cli @('build', $game, '--gbdk-path', $GbdkPath) $workspace | Out-Null
        if (@(Get-ChildItem (Join-Path $game 'build') -Filter '*.gb').Count -ne 1) { throw "Template $template produced no ROM." }
    }
    if (-not (Test-Path (Join-Path $cache "gbsharp.sdk/$version/gbsharp.sdk.$version.nupkg"))) { throw 'SDK resolver did not install the staged SDK into the isolated cache.' }
    foreach ($id in $expectedIds) {
        $lowerId = $id.ToLowerInvariant()
        $packageRoot = if ($id -eq 'gbsharp') { Join-Path $toolDirectory ".store/gbsharp/$version" } else { $cache }
        $resolved = Join-Path $packageRoot "$lowerId/$version/$lowerId.$version.nupkg"
        if (-not (Test-Path $resolved) -or (Get-FileHash $resolved).Hash -cne (Get-FileHash $packageFiles[$id]).Hash) {
            throw "Resolved $id package bytes differ from the staged release package."
        }
    }
    if (-not (Test-Path $SampleDirectory)) { throw "Reference sample is missing: $SampleDirectory" }
    $sample = Join-Path $workspace 'ComposedScrolling'
    New-Item -ItemType Directory -Path $sample | Out-Null
    Get-ChildItem $SampleDirectory -Force | Where-Object { $_.Name -notin 'build','bin','obj','publish' } | Copy-Item -Destination $sample -Recurse
    $manifest = @{ version = $version; workspace = $workspace; tool = $cli; sample = (Export-Game 'sample' $sample); packages = @{} }
    foreach ($package in $packages) { $manifest.packages[$package.Name] = (Get-FileHash $package.FullName -Algorithm SHA256).Hash }
    if ($SaveProjectDirectory) {
        $save = Join-Path $workspace 'SaveFixture'
        New-Item -ItemType Directory -Path $save | Out-Null
        Get-ChildItem $SaveProjectDirectory -Force | Where-Object { $_.Name -notin 'build','bin','obj','publish' } | Copy-Item -Destination $save -Recurse
        $manifest.save = Export-Game 'save' $save
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $ArtifactsDirectory 'manifest.json')
    Write-Host "Installed package validation passed. Evidence: $ArtifactsDirectory"
} finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name]) }
    # Retain isolated workspace for inspection; record it even when an earlier check fails.
    $workspace | Set-Content (Join-Path $ArtifactsDirectory 'workspace.txt')
}
