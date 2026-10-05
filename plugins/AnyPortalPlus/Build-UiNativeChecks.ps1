[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$packRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $packRoot '.cache/anyportal-ui-native-build' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$managed = Join-Path ([IO.Path]::GetFullPath($GameDirectory)) 'valheim_Data/Managed'
$references = @((Join-Path $packRoot 'Game/BepInEx/core/BepInEx.dll'), (Join-Path $packRoot 'Game/BepInEx/core/0Harmony.dll'),
    (Join-Path $packRoot 'Game/BepInEx/plugins/Jotunn.dll'))
$references += @('assembly_valheim.dll', 'assembly_guiutils.dll', 'assembly_utils.dll', 'SoftReferenceableAssets.dll',
    'UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.UI.dll', 'UnityEngine.UIModule.dll',
    'UnityEngine.InputLegacyModule.dll', 'UnityEngine.TextRenderingModule.dll', 'netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
$output = Join-Path $OutputDirectory 'AnyPortalPlusUiNativeChecks.dll'
$arguments = @('/nologo', '/target:library', '/codepage:65001', '/optimize+', ('/out:' + $output))
$arguments += $references | ForEach-Object { '/reference:' + $_ }
$arguments += Join-Path $PSScriptRoot 'Tests/PortalUiNativeChecks.cs'
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') @arguments
if ($LASTEXITCODE -ne 0) { throw 'AnyPortal+ native UI probe compilation failed.' }
Write-Output "Built optional native UI checks (not executed): $output"
