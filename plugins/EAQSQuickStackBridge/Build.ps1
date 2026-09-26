[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GameDirectory,
    [string]$OutputFile
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputFile) { $OutputFile = Join-Path $root 'local-plugins/EAQSQuickStackBridge.dll' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler required for this source build on Windows.' }
$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
$refs = @(
    (Join-Path $root 'Game/BepInEx/core/BepInEx.dll'),
    (Join-Path $root 'Game/BepInEx/core/0Harmony.dll'),
    (Join-Path $root 'Game/BepInEx/plugins/RandyKnapp-EquipmentAndQuickSlots/EquipmentAndQuickSlots.dll'),
    (Join-Path $managed 'assembly_valheim.dll'),
    (Join-Path $managed 'assembly_utils.dll'),
    (Join-Path $managed 'UnityEngine.dll'),
    (Join-Path $managed 'UnityEngine.CoreModule.dll'),
    (Join-Path $managed 'UnityEngine.InputLegacyModule.dll'),
    (Join-Path $managed 'netstandard.dll')
)
foreach ($ref in $refs) { if (-not (Test-Path -LiteralPath $ref)) { throw "Missing reference: $ref" } }
New-Item -ItemType Directory -Force (Split-Path $OutputFile -Parent) | Out-Null
$argsList = @('/nologo','/target:library','/optimize+',('/out:' + $OutputFile))
$argsList += $refs | ForEach-Object { '/reference:' + $_ }
$argsList += @((Join-Path $PSScriptRoot 'Plugin.cs'), (Join-Path $PSScriptRoot 'SlotPolicy.cs'))
& $compiler @argsList
if ($LASTEXITCODE -ne 0) { throw 'Bridge compilation failed.' }
Write-Output "Built bridge: $OutputFile"
