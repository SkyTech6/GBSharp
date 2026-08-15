# Builds the GB# documentation site into docs/_site.
#
# This is the entry point, not `docfx` alone: the diagnostics reference is
# generated from the compiler's own descriptors before docfx runs, and
# llms.txt / llms-full.txt are generated from the built site afterwards.
#
#   pwsh docs/build-docs.ps1            # build the site
#   pwsh docs/build-docs.ps1 -Serve     # build and preview on http://localhost:8080

param(
    [switch]$Serve
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

Push-Location $repo
try {
    # The framework's XML doc file feeds both the docfx API pages and the
    # API section of llms-full.txt.
    dotnet build GBSharp.Framework/GBSharp.Framework.csproj -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "GBSharp.Framework build failed." }

    dotnet run --project tools/GBSharp.DocsGen -c Release -- diagnostics docs/reference/diagnostics
    if ($LASTEXITCODE -ne 0) { throw "Diagnostics generation failed." }

    dotnet tool restore | Out-Null
    dotnet docfx docs/docfx.json --warningsAsErrors
    if ($LASTEXITCODE -ne 0) { throw "docfx build failed." }

    dotnet run --project tools/GBSharp.DocsGen -c Release -- llms docs docs/_site GBSharp.Framework/bin/Release/netstandard2.0/GBSharp.Framework.xml
    if ($LASTEXITCODE -ne 0) { throw "llms.txt generation failed." }

    Write-Host "Site built: docs/_site"

    if ($Serve) {
        dotnet docfx serve docs/_site --open-browser
    }
}
finally {
    Pop-Location
}
