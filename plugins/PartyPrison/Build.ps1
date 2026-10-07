[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory, [string]$OutputFile)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputFile) { $OutputFile = Join-Path $root 'local-plugins/PartyPrison.dll' }
$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
$refs = @('Game/BepInEx/core/BepInEx.dll','Game/BepInEx/core/0Harmony.dll','Game/BepInEx/plugins/Jotunn.dll','local-plugins/WorldCharacters.dll') | ForEach-Object { Join-Path $root $_ }
$refs += @('assembly_valheim.dll','assembly_guiutils.dll','assembly_utils.dll','SoftReferenceableAssets.dll','Splatform.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.UI.dll','UnityEngine.UIModule.dll','UnityEngine.TextRenderingModule.dll','UnityEngine.InputLegacyModule.dll','UnityEngine.PhysicsModule.dll','netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
foreach ($ref in $refs) { if (-not (Test-Path -LiteralPath $ref)) { throw "Missing reference: $ref" } }
New-Item -ItemType Directory -Force -Path (Split-Path $OutputFile -Parent) | Out-Null
$buildDirectory = Join-Path $root ('.cache/partyprison-build-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $buildDirectory | Out-Null
$assemblyOutput = Join-Path $buildDirectory 'PartyPrison.dll'
# CSC derives CLR identity from /out. Keep the public assembly name stable even
# when a validation caller requests a candidate filename.
$arguments = @('/nologo','/codepage:65001','/target:library','/optimize+',('/out:' + $assemblyOutput))
$arguments += $refs | ForEach-Object { '/reference:' + $_ }
$arguments += @('SentenceState.cs','SentenceStore.cs','CustodyStore.cs','CustodyWithdrawal.cs','BackpackAccess.cs','CustodyInventory.cs','Protocol.cs','PrisonWire.cs','PlacementPlan.cs','ArenaSpawnGeometry.cs','ArenaWaveProgression.cs','ArenaWaveRuntime.cs','TerrainPlan.cs','TerrainLeveler.cs','ClearanceFootprint.cs','SiteClearer.cs','ForceClearance.cs','ArenaBuilder.cs','ArenaLayout.cs','ArenaDefeatPenalty.cs','CombatCatalog.cs','ArenaCombat.cs','PrisonKitStorage.cs','CombatRuntime.cs','PrisonWindow.cs','PrisonContent.cs','PrisonConsole.cs','Plugin.cs','CustodyRuntime.cs','WithdrawalRuntime.cs','Patches.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') @arguments
if ($LASTEXITCODE -ne 0) { throw 'PartyPrison compilation failed.' }
Copy-Item -LiteralPath $assemblyOutput -Destination $OutputFile -Force
Remove-Item -LiteralPath $assemblyOutput
Remove-Item -LiteralPath $buildDirectory
Write-Output "Built PartyPrison: $OutputFile"
