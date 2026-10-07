[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory, [string]$OutputFile)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputFile) { $OutputFile = Join-Path $root 'local-plugins/WorldCharacters.dll' }
if ([IO.Path]::GetFileName($OutputFile) -cne 'WorldCharacters.dll') { throw 'Keep the output name WorldCharacters.dll; other mods reference this CLR assembly identity. Use a separate output directory for candidates.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
$refs = @('Game/BepInEx/core/BepInEx.dll','Game/BepInEx/core/0Harmony.dll','Game/BepInEx/plugins/Jotunn.dll') | ForEach-Object { Join-Path $root $_ }
$refs += @('assembly_valheim.dll','assembly_guiutils.dll','assembly_utils.dll','SoftReferenceableAssets.dll','Splatform.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.IMGUIModule.dll','UnityEngine.TextRenderingModule.dll','UnityEngine.InputLegacyModule.dll','UnityEngine.UI.dll','UnityEngine.UIModule.dll','netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
foreach ($ref in $refs) { if (-not (Test-Path -LiteralPath $ref)) { throw "Missing reference: $ref" } }
New-Item -ItemType Directory -Force (Split-Path $OutputFile -Parent) | Out-Null
$argsList = @('/nologo','/codepage:65001','/target:library','/optimize+',('/out:' + $OutputFile))
$argsList += $refs | ForEach-Object { '/reference:' + $_ }
$argsList += @('State.cs','NativeInventory.cs','StateStore.cs','Session.cs','SnapshotWriter.cs','SnapshotRateLimit.cs','PeerMaintenance.cs','GameState.cs','MapCapturePolicy.cs','MapCaptureCompatibility.cs','GameMapCapture.cs','AdministrativeCheckpoint.cs','Administration.cs','AdministrationWindow.cs','AdministrationRuntime.cs','AdministrationInput.cs','ShortcutCapture.cs','GameplayInputCache.cs','Plugin.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler @argsList
if ($LASTEXITCODE -ne 0) { throw 'WorldCharacters compilation failed.' }
Write-Output "Built WorldCharacters: $OutputFile"
