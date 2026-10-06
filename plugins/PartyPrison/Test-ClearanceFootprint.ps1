[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$packRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$fixtureDirectory = Join-Path $packRoot ('.cache/party-prison-clearance-footprint-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $fixtureDirectory | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$runner = Join-Path $fixtureDirectory 'ClearanceFootprintTests.exe'
$sources = @('ClearanceFootprint.cs','ClearanceFootprintTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /codepage:65001 /target:exe "/out:$runner" @sources
if ($LASTEXITCODE -ne 0) { throw 'PartyPrison clearance footprint test build failed.' }
& $runner 2>&1 | Tee-Object -FilePath (Join-Path $fixtureDirectory 'result.log')
if ($LASTEXITCODE -ne 0) { throw 'PartyPrison clearance footprint checks failed.' }
