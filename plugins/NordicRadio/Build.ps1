[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory, [string]$OutputFile)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputFile) { $OutputFile = Join-Path $root 'local-plugins/NordicRadio.dll' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework C# compiler is required.' }
$refs = @('Game/BepInEx/core/BepInEx.dll','Game/BepInEx/core/0Harmony.dll','Game/BepInEx/plugins/Jotunn.dll') | ForEach-Object { Join-Path $root $_ }
$refs += @('assembly_valheim.dll','assembly_guiutils.dll','assembly_utils.dll','SoftReferenceableAssets.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.AssetBundleModule.dll','UnityEngine.AudioModule.dll','UnityEngine.PhysicsModule.dll','UnityEngine.ImageConversionModule.dll','UnityEngine.InputLegacyModule.dll','UnityEngine.UI.dll','UnityEngine.UIModule.dll','UnityEngine.TextRenderingModule.dll','UnityEngine.UnityWebRequestModule.dll','UnityEngine.UnityWebRequestAudioModule.dll','UnityEngine.ParticleSystemModule.dll','netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
foreach ($ref in $refs) { if (-not (Test-Path -LiteralPath $ref)) { throw "Missing reference: $ref" } }
New-Item -ItemType Directory -Force (Split-Path $OutputFile -Parent) | Out-Null
$argsList = @('/nologo','/codepage:65001','/target:library','/optimize+',('/out:' + $OutputFile))
$argsList += $refs | ForEach-Object { '/reference:' + $_ }
$argsList += @('Plugin.cs','HornModel.cs','PortableModel.cs','PortableController.cs','IRadioTarget.cs','RadioPiece.cs','RadioWindow.cs','RadioAudio.cs','PlaybackMath.cs','RadioGain.cs','AudioGainProcessor.cs','MusicDucking.cs','RadioService.cs','RadioProtocol.cs','RadioLibrary.cs','RadioTransfer.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler @argsList
if ($LASTEXITCODE -ne 0) { throw 'NordicRadio compilation failed.' }
Write-Output "Built NordicRadio: $OutputFile"
