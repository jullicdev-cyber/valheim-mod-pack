[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$output = Join-Path $root '.cache/pin-preset-localization-tests'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$executable = Join-Path $output 'PinPresetLocalizationTests.exe'
$sources = @('PinPresetStore.cs', 'PinPresetCatalog.cs', 'PinPresetLocalization.cs', 'PinPresetLocalizationTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /codepage:65001 /target:exe /optimize+ /reference:System.Runtime.Serialization.dll ('/out:' + $executable) @sources
if ($LASTEXITCODE -ne 0) { throw 'Preset localization tests failed to compile.' }
& $executable
if ($LASTEXITCODE -ne 0) { throw 'Preset localization tests failed.' }
