[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory,[Parameter(Mandatory=$true)][string]$PluginAssembly,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$personalRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$personalManaged = Join-Path $GameDirectory 'valheim_Data/Managed'
$personalRefs = @($PluginAssembly,(Join-Path $personalRoot 'Game/BepInEx/core/BepInEx.dll'),(Join-Path $personalRoot 'Game/BepInEx/core/0Harmony.dll'),(Join-Path $personalRoot 'Game/BepInEx/plugins/Jotunn.dll'))
$personalRefs += @('assembly_valheim.dll','assembly_guiutils.dll','assembly_utils.dll','SoftReferenceableAssets.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.AudioModule.dll','UnityEngine.InputLegacyModule.dll','UnityEngine.UI.dll','UnityEngine.UIModule.dll','UnityEngine.TextRenderingModule.dll','netstandard.dll') | ForEach-Object { Join-Path $personalManaged $_ }
foreach ($personalRef in $personalRefs) { if (-not (Test-Path -LiteralPath $personalRef)) { throw "Missing personal audio reference: $personalRef" } }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$personalOutput = Join-Path $OutputDirectory 'NordicPersonalAudioNativeChecks.dll'
$personalArgs = @('/nologo','/codepage:65001','/target:library','/optimize+',('/out:' + $personalOutput)) + @($personalRefs | ForEach-Object { '/reference:' + $_ })
$personalArgs += Join-Path $PSScriptRoot 'PersonalAudioNativeChecks.cs'
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') @personalArgs
if ($LASTEXITCODE -ne 0) { throw 'Personal audio native probe compilation failed' }
Write-Output "Built personal audio native probe: $personalOutput"
