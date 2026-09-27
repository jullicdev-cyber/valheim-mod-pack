[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$outputDirectory = Join-Path $root '.cache/nordic-radio-music-tests'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$runner = Join-Path $outputDirectory 'MusicDuckingTests.exe'
& $compiler /nologo /codepage:65001 /target:exe ("/out:" + $runner) (Join-Path $PSScriptRoot 'MusicDucking.cs') (Join-Path $PSScriptRoot 'MusicDuckingTestStubs.cs') (Join-Path $PSScriptRoot 'MusicDuckingTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Music ducking tests compilation failed' }
& $runner
if ($LASTEXITCODE -ne 0) { throw 'Music ducking tests failed' }
$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
$refs = @((Join-Path $root 'Game/BepInEx/core/0Harmony.dll'))
$refs += @('assembly_valheim.dll', 'UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.AudioModule.dll', 'netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
$argsList = @('/nologo','/codepage:65001','/target:library','/optimize+',('/out:' + (Join-Path $outputDirectory 'MusicDuckingContract.dll')))
$argsList += $refs | ForEach-Object { '/reference:' + $_ }
$argsList += Join-Path $PSScriptRoot 'MusicDucking.cs'
& $compiler @argsList
if ($LASTEXITCODE -ne 0) { throw 'Music ducking real API contract compilation failed' }
Write-Output 'Music ducking compiled against the installed Valheim and Harmony API. This test does not exercise native Harmony dispatch.'
