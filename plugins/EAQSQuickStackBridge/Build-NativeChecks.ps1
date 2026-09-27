[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory, [string]$OutputDirectory, [string]$PluginAssembly)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root '.cache/bridge-tests' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
if (-not $PluginAssembly) { $PluginAssembly = Join-Path $OutputDirectory 'EAQSQuickStackBridge.dll'; & (Join-Path $PSScriptRoot 'Build.ps1') -GameDirectory $GameDirectory -OutputFile $PluginAssembly }
$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$refs = @($PluginAssembly, (Join-Path $root 'Game/BepInEx/core/BepInEx.dll'), (Join-Path $root 'Game/BepInEx/core/0Harmony.dll'))
$refs += @('assembly_valheim.dll','assembly_utils.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.InputLegacyModule.dll','netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
$output = Join-Path $OutputDirectory 'EAQSAzuNativeChecks.dll'
$arguments = @('/nologo','/codepage:65001','/target:library','/optimize+',('/out:' + $output))
$arguments += $refs | ForEach-Object { '/reference:' + $_ }
$arguments += Join-Path $PSScriptRoot 'NativeChecks.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Native favorite bridge probe compilation failed' }
Write-Output "Built optional native checks: $output"
