[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$folder = Join-Path $root '.cache/renewable-resource-timers-tests'
New-Item -ItemType Directory -Force -Path $folder | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$runner = Join-Path $folder 'Tests.exe'
$sources = @('TimerPolicy.cs', 'Tests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /codepage:65001 /target:exe "/out:$runner" @sources
if ($LASTEXITCODE -ne 0) { throw 'Timer test build failed' }
& $runner
if ($LASTEXITCODE -ne 0) { throw 'Timer policy tests failed' }
& (Join-Path $PSScriptRoot 'Build.ps1') -GameDirectory $GameDirectory -OutputFile (Join-Path $folder 'RenewableResourceTimers.dll')
