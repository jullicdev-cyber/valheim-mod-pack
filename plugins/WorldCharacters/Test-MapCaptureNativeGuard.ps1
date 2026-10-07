[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory, [Parameter(Mandatory=$true)][string]$PluginAssembly)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$folder = Join-Path $root ('.cache/worldcharacters-map-guard-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $folder | Out-Null
$runner = Join-Path $folder 'MapCaptureNativeGuardTests.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$harmony = Join-Path $root 'Game/BepInEx/core/0Harmony.dll'
& $compiler /nologo /codepage:65001 /target:exe ("/out:$runner") ("/reference:$harmony") (Join-Path $PSScriptRoot 'MapCaptureNativeGuardTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Native map guard test compilation failed' }
& $runner (Join-Path $GameDirectory 'valheim_Data/Managed') (Split-Path $harmony -Parent) (Resolve-Path -LiteralPath $PluginAssembly).ProviderPath
if ($LASTEXITCODE -ne 0) { throw 'Native map guard regressions failed' }
