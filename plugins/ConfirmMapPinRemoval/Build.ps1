[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GameDirectory,
    [string]$OutputFile
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputFile) { $OutputFile = Join-Path $root 'local-plugins/ConfirmMapPinRemoval.dll' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler required for this source build on Windows.' }
$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
$refs = @(
    (Join-Path $root 'Game/BepInEx/core/BepInEx.dll'),
    (Join-Path $root 'Game/BepInEx/core/0Harmony.dll'),
    (Join-Path $root 'Game/BepInEx/plugins/Jotunn.dll'),
    (Join-Path $managed 'assembly_guiutils.dll'),
    (Join-Path $managed 'assembly_valheim.dll'),
    (Join-Path $managed 'assembly_utils.dll'),
    (Join-Path $managed 'UnityEngine.dll'),
    (Join-Path $managed 'UnityEngine.CoreModule.dll'),
    (Join-Path $managed 'UnityEngine.InputLegacyModule.dll'),
    (Join-Path $managed 'UnityEngine.UI.dll'),
    (Join-Path $managed 'UnityEngine.UIModule.dll'),
    (Join-Path $managed 'UnityEngine.TextRenderingModule.dll'),
    (Join-Path $managed 'netstandard.dll')
)
foreach ($ref in $refs) { if (-not (Test-Path -LiteralPath $ref)) { throw "Missing reference: $ref" } }
New-Item -ItemType Directory -Force (Split-Path $OutputFile -Parent) | Out-Null
$argsList = @('/nologo','/codepage:65001','/target:library','/optimize+',('/out:' + $OutputFile))
$argsList += $refs | ForEach-Object { '/reference:' + $_ }
$argsList += @('Plugin.cs', 'Confirmation.cs', 'RemovalDialog.cs', 'PinLabel.cs', 'WoodDialogView.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler @argsList
if ($LASTEXITCODE -ne 0) { throw 'Map confirmation compilation failed.' }
Write-Output "Built map confirmation: $OutputFile"
