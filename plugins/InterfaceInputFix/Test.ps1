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
$editingRunner = Join-Path $outputDirectory 'ValheimPlusEditingTests.exe'
$harmony = Join-Path $root 'Game/BepInEx/core/0Harmony.dll'
& $compiler /nologo /codepage:65001 /target:exe ('/out:' + $editingRunner) ('/reference:' + $harmony) (Join-Path $PSScriptRoot 'ValheimPlusEditingCompat.cs') (Join-Path $PSScriptRoot 'PrisonEditingAccess.cs') (Join-Path $PSScriptRoot 'ValheimPlusEditingTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Valheim Plus editing regression compilation failed' }
foreach ($dll in @('0Harmony.dll','MonoMod.RuntimeDetour.dll','MonoMod.Utils.dll','Mono.Cecil.dll')) {
    Copy-Item -LiteralPath (Join-Path $root ('Game/BepInEx/core/' + $dll)) -Destination $outputDirectory -Force
}
& $editingRunner
if ($LASTEXITCODE -ne 0) { throw 'Valheim Plus editing regressions failed' }
& (Join-Path $PSScriptRoot 'Build.ps1') -GameDirectory $GameDirectory -OutputFile (Join-Path $outputDirectory 'InterfaceInputFix.dll')
