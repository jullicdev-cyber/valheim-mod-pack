[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$output = Join-Path $root '.cache/ExpeditionLoadoutsBackendTests.exe'
$compileArgs = @('/nologo','/codepage:65001','/target:exe',('/out:' + $output))
$compileArgs += @('SupplyTarget.cs','TransferPolicy.cs','ChestService.cs','BackendTestStubs.cs','BackendTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler @compileArgs
if ($LASTEXITCODE -ne 0) { throw 'Loadout backend test compilation failed.' }
& $output
if ($LASTEXITCODE -ne 0) { throw 'Loadout backend tests failed.' }
