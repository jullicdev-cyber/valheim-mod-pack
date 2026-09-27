[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$folder = Join-Path $root '.cache/pin-preset-tests'
New-Item -ItemType Directory -Force $folder | Out-Null
$output = Join-Path $folder 'PinPresetStoreTests.exe'
$reference = Join-Path (Split-Path $compiler -Parent) 'System.Runtime.Serialization.dll'
$sources = @('PinPresetStore.cs', 'PinPresetStoreTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /codepage:65001 /target:exe "/reference:$reference" "/out:$output" @sources
if ($LASTEXITCODE -ne 0) { throw 'Pin preset test build failed.' }
& $output
if ($LASTEXITCODE -ne 0) { throw 'Pin preset persistence tests failed.' }
