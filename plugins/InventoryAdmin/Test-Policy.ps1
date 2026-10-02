[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$packRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$fixtureDirectory = Join-Path $packRoot ('.cache/inventory-admin-policy-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $fixtureDirectory | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$runner = Join-Path $fixtureDirectory 'PolicyTests.exe'
$sources = @('Policy.cs','PermissionStore.cs','Protocol.cs','TransactionJournal.cs','PolicyTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /codepage:65001 /target:exe "/out:$runner" @sources
if ($LASTEXITCODE -ne 0) { throw 'Inventory administration policy test build failed' }
& $runner (Join-Path $fixtureDirectory 'state')
if ($LASTEXITCODE -ne 0) { throw 'Inventory administration policy checks failed' }
