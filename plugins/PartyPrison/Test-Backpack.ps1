[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$packRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$fixture = Join-Path $packRoot ('.cache/party-prison-backpack-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $fixture | Out-Null
$runner = Join-Path $fixture 'BackpackAccessTests.exe'
$sources = @('BackpackAccess.cs','BackpackAccessTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /codepage:65001 /target:exe "/out:$runner" @sources
if ($LASTEXITCODE -ne 0) { throw 'Backpack adapter managed test build failed.' }
& $runner
if ($LASTEXITCODE -ne 0) { throw 'Backpack adapter managed checks failed.' }
