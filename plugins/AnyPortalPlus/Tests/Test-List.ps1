[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path $PSScriptRoot -Parent
$packRoot = Split-Path (Split-Path $pluginRoot -Parent) -Parent
$fixtureDirectory = Join-Path $packRoot ('.cache/anyportal-plus-list-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $fixtureDirectory | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$runner = Join-Path $fixtureDirectory 'PortalListTests.exe'
$sources = @(
    (Join-Path $pluginRoot 'Source/Plus/PortalListModel.cs'),
    (Join-Path $PSScriptRoot 'PortalListTests.cs')
)
& $compiler /nologo /codepage:65001 /target:exe "/out:$runner" @sources
if ($LASTEXITCODE -ne 0) { throw 'AnyPortal+ list test build failed.' }
& $runner
if ($LASTEXITCODE -ne 0) { throw 'AnyPortal+ list checks failed.' }
