[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory, [string]$OutputDirectory, [string]$PluginAssembly)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root '.cache/inventory-admin-native-build' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
if (-not $PluginAssembly) { $PluginAssembly = Join-Path $root 'local-plugins/InventoryAdmin.dll' }
$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
$refs = @($PluginAssembly, (Join-Path $root 'Game/BepInEx/core/BepInEx.dll'),
    (Join-Path $root 'Game/BepInEx/core/0Harmony.dll'), (Join-Path $root 'Game/BepInEx/plugins/Jotunn.dll'))
$refs += @('assembly_valheim.dll', 'assembly_guiutils.dll', 'assembly_utils.dll', 'SoftReferenceableAssets.dll',
    'UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.UI.dll', 'UnityEngine.UIModule.dll', 'UnityEngine.InputLegacyModule.dll',
    'UnityEngine.TextRenderingModule.dll', 'netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
$output = Join-Path $OutputDirectory 'InventoryAdminNativeChecks.dll'
$argsList = @('/nologo', '/target:library', '/codepage:65001', '/optimize+', ('/out:' + $output))
$argsList += $refs | ForEach-Object { '/reference:' + $_ }
$argsList += Join-Path $PSScriptRoot 'NativeChecks.cs'
$argsList += Join-Path $PSScriptRoot 'UiNativeChecks.cs'
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') @argsList
if ($LASTEXITCODE -ne 0) { throw 'Inventory administration native probe compilation failed.' }
Write-Output "Built optional native checks: $output"
