[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$outputDirectory = Join-Path $root '.cache/interface-input-tests'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$runner = Join-Path $outputDirectory 'InterfaceInputTests.exe'
& $compiler /nologo /codepage:65001 /target:exe ('/out:' + $runner) (Join-Path $PSScriptRoot 'InputLease.cs') (Join-Path $PSScriptRoot 'Tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Input lifecycle test compilation failed' }
& $runner
if ($LASTEXITCODE -ne 0) { throw 'Input lifecycle tests failed' }
& (Join-Path $PSScriptRoot 'Build.ps1') -GameDirectory $GameDirectory -OutputFile (Join-Path $outputDirectory 'InterfaceInputFix.dll')
