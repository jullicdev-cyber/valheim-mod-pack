[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out = Join-Path $root '.cache/nordic-radio-tests'
New-Item -ItemType Directory -Force $out | Out-Null
$exe = Join-Path $out 'PlaybackTransportTests.exe'
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /codepage:65001 /target:exe "/out:$exe" (Join-Path $PSScriptRoot 'PlaybackMath.cs') (Join-Path $PSScriptRoot 'RadioPlayback.cs') (Join-Path $PSScriptRoot 'PlaybackTransportTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Playback transport tests compilation failed' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Playback transport tests failed' }
