[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$outputDirectory = Join-Path $root '.cache/chest-search-tests'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$runner = Join-Path $outputDirectory 'ChestSearchTests.exe'
$argsList = @('/nologo','/codepage:65001','/target:exe','/nowarn:0414',('/out:' + $runner))
$argsList += @('SearchText.cs','NameIndex.cs','NativeChestReader.cs','SearchService.cs','InputBlockLease.cs','ReaderTestStubs.cs','Tests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler @argsList
if ($LASTEXITCODE -ne 0) { throw 'ChestSearch test compilation failed' }
& $runner
if ($LASTEXITCODE -ne 0) { throw 'ChestSearch tests failed' }
& (Join-Path $PSScriptRoot 'Build.ps1') -GameDirectory $GameDirectory -OutputFile (Join-Path $outputDirectory 'ChestSearch.dll')
Write-Output 'Actual game/Jotunn API compilation passed. Native rendering, keyboard input and live replicated-container behavior require an in-game check.'
