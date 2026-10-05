[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory, [string]$OutputFile)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputFile) { $OutputFile = Join-Path $root 'local-plugins/InventoryAdmin.dll' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
$refs = @('Game/BepInEx/core/BepInEx.dll','Game/BepInEx/core/0Harmony.dll','Game/BepInEx/plugins/Jotunn.dll','local-plugins/WorldCharacters.dll') | ForEach-Object { Join-Path $root $_ }
$refs += @('assembly_valheim.dll','assembly_guiutils.dll','assembly_utils.dll','SoftReferenceableAssets.dll','Splatform.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.UI.dll','UnityEngine.UIModule.dll','UnityEngine.TextRenderingModule.dll','UnityEngine.InputLegacyModule.dll','UnityEngine.IMGUIModule.dll','netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
New-Item -ItemType Directory -Force (Split-Path $OutputFile -Parent) | Out-Null
$argsList = @('/nologo','/codepage:65001','/target:library','/optimize+',('/out:' + $OutputFile))
$argsList += $refs | ForEach-Object { '/reference:' + $_ }
$argsList += @('Policy.cs','PermissionStore.cs','Protocol.cs','TransactionJournal.cs','NativeAdapter.cs','AdminUiBindings.cs','AdminInputLease.cs','AdminWindow.cs','GameplayInputCache.cs','PlayerLocations.cs','AdminMapOverlay.cs','WireTransport.cs','Plugin.cs','Plugin.Locations.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler @argsList
if ($LASTEXITCODE -ne 0) { throw 'InventoryAdmin compilation failed.' }
Write-Output "Built InventoryAdmin: $OutputFile"
