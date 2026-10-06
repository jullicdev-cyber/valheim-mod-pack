[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GameDirectory,
    [string]$PluginAssembly
)
$ErrorActionPreference = 'Stop'
$packRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $PluginAssembly) { $PluginAssembly = Join-Path $packRoot 'local-plugins/PartyPrison.dll' }
$nativeAssembly = Join-Path $GameDirectory 'valheim_Data/Managed/assembly_valheim.dll'
$cecil = Join-Path $packRoot 'Game/BepInEx/core/Mono.Cecil.dll'
foreach ($required in @($nativeAssembly, $cecil, $PluginAssembly)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing contract input: $required" }
}
$fixtureDirectory = Join-Path $packRoot ('.cache/party-prison-terrain-contracts-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $fixtureDirectory | Out-Null
Copy-Item -LiteralPath $cecil -Destination (Join-Path $fixtureDirectory 'Mono.Cecil.dll')
$runner = Join-Path $fixtureDirectory 'TerrainContractChecks.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /codepage:65001 /target:exe "/out:$runner" "/reference:$cecil" (Join-Path $PSScriptRoot 'TerrainContractChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'PartyPrison native terrain contract checks did not compile.' }
& $runner $nativeAssembly $PluginAssembly
if ($LASTEXITCODE -ne 0) { throw 'PartyPrison native terrain contracts failed.' }
