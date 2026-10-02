[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory, [string]$OutputDirectory, [string]$PluginAssembly)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root '.cache/pin-history-build' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$plugin = $PluginAssembly
if (-not $plugin) {
    $plugin = Join-Path $OutputDirectory 'ConfirmMapPinRemoval.dll'
    & (Join-Path $PSScriptRoot 'Build.ps1') -GameDirectory $GameDirectory -OutputFile $plugin
}
$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$refs = @($plugin, (Join-Path $root 'Game/BepInEx/plugins/Jotunn.dll'))
$refs += @('assembly_valheim.dll','assembly_guiutils.dll','assembly_utils.dll','SoftReferenceableAssets.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.InputLegacyModule.dll','UnityEngine.PhysicsModule.dll','UnityEngine.UI.dll','UnityEngine.UIModule.dll','UnityEngine.TextRenderingModule.dll','netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
$output = Join-Path $OutputDirectory 'PinHistoryNativeChecks.dll'
$arguments = @('/nologo','/codepage:65001','/target:library','/optimize+',('/out:' + $output))
$arguments += $refs | ForEach-Object { '/reference:' + $_ }
$arguments += @('NativeChecks.cs', 'PresetUiNativeChecks.cs', 'PinPresentationNativeChecks.cs', 'SuggestionRuntimeNativeChecks.cs', 'SuggestionShortcutNativeChecks.cs', 'MapLauncherNativeChecks.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Native map history probe compilation failed' }
Write-Output "Built optional native checks: $output"
