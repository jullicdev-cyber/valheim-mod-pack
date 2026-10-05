[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$fixture = Join-Path $root ('.cache/group-radius-motion-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $fixture | Out-Null
$output = Join-Path $fixture 'GroupRadiusMotionTests.exe'
$sources = @('GroupRadiusMotion.cs','GroupRadiusMotionTestStubs.cs','GroupRadiusMotionTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /target:exe /codepage:65001 "/out:$output" @sources
if ($LASTEXITCODE -ne 0) { throw 'Group radius motion test compilation failed.' }
& $output
if ($LASTEXITCODE -ne 0) { throw 'Group radius motion assertions failed.' }
