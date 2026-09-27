[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$output = Join-Path $root '.cache/expedition-tests'
New-Item -ItemType Directory -Force $output | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$exe = Join-Path $output 'PresetStoreTests.exe'
& $compiler /nologo /codepage:65001 /target:exe "/out:$exe" /reference:System.Runtime.Serialization.dll (Join-Path $PSScriptRoot 'PresetStore.cs') (Join-Path $PSScriptRoot 'PresetStoreTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Preset tests compilation failed.' }
& $exe $output
if ($LASTEXITCODE -ne 0) { throw 'Preset persistence tests failed.' }
