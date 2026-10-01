[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory, [string]$OutputFile)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputFile) { $OutputFile = Join-Path $root '.cache/chest-search-build/ChestSearch.dll' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler required' }
$refs = @('Game/BepInEx/core/BepInEx.dll','Game/BepInEx/core/0Harmony.dll','Game/BepInEx/plugins/Jotunn.dll') | ForEach-Object { Join-Path $root $_ }
$refs += @('assembly_valheim.dll','assembly_guiutils.dll','assembly_utils.dll','SoftReferenceableAssets.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.PhysicsModule.dll','UnityEngine.InputLegacyModule.dll','UnityEngine.UI.dll','UnityEngine.UIModule.dll','UnityEngine.TextRenderingModule.dll','netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
foreach ($ref in $refs) { if (-not (Test-Path -LiteralPath $ref)) { throw "Missing reference: $ref" } }
New-Item -ItemType Directory -Force -Path (Split-Path $OutputFile -Parent) | Out-Null
$argsList = @('/nologo','/codepage:65001','/target:library','/optimize+',('/out:' + $OutputFile))
$argsList += $refs | ForEach-Object { '/reference:' + $_ }
$argsList += @('Plugin.cs','NativeChestReader.cs','SearchText.cs','NameIndex.cs','SearchService.cs','SearchWindow.cs','ChestMarker.cs','InputBlockLease.cs','ShortcutCapture.cs','GameplayInputCache.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler @argsList
if ($LASTEXITCODE -ne 0) { throw 'ChestSearch compilation failed' }
Write-Output "Built ChestSearch: $OutputFile"
