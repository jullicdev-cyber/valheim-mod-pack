[CmdletBinding()]
param([switch]$VerifyInstalledAssets, [string]$GameDirectory, [string]$LocationMetadata)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$output = Join-Path $root '.cache/pin-suggestion-tests'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$executable = Join-Path $output 'SuggestionPolicyTests.exe'
$sources = @('PinPresetStore.cs', 'PinPresetCatalog.cs', 'PinPresetLocalization.cs', 'SuggestionPolicy.cs', 'SuggestionPolicyTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /codepage:65001 /target:exe /optimize+ /reference:System.Runtime.Serialization.dll /reference:System.Web.Extensions.dll ('/out:' + $executable) @sources
if ($LASTEXITCODE -ne 0) { throw 'Nearby pin policy tests failed to compile.' }
if ($VerifyInstalledAssets) {
    if (-not $GameDirectory) { throw 'Asset verification requires -GameDirectory.' }
    $manifest = Join-Path $GameDirectory 'valheim_Data/StreamingAssets/SoftRef/manifest_extended'
    $locations = if ($LocationMetadata) { $LocationMetadata } else { Join-Path $root '.cache/suggestion-location-assets.json' }
    if (!(Test-Path -LiteralPath $manifest -PathType Leaf) -or !(Test-Path -LiteralPath $locations -PathType Leaf)) {
        throw 'Native asset verification requires the installed manifest and locally extracted suggestion-location-assets.json.'
    }
    & $executable $manifest $locations
} else { & $executable }
if ($LASTEXITCODE -ne 0) { throw 'Nearby pin policy tests failed.' }
