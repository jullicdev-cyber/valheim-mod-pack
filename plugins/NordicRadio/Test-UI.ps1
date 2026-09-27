[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$outputDirectory = Join-Path $root '.cache/nordic-radio-ui-tests'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
$refs = @('Game/BepInEx/core/BepInEx.dll','Game/BepInEx/core/0Harmony.dll','Game/BepInEx/plugins/Jotunn.dll') | ForEach-Object { Join-Path $root $_ }
$refs += @('assembly_guiutils.dll','assembly_valheim.dll','assembly_utils.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.InputLegacyModule.dll','UnityEngine.UI.dll','UnityEngine.UIModule.dll','UnityEngine.TextRenderingModule.dll','netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
foreach ($ref in $refs) { if (-not (Test-Path -LiteralPath $ref)) { throw "Missing reference: $ref" } }
$argsList = @('/nologo','/codepage:65001','/target:library','/optimize+',('/out:' + (Join-Path $outputDirectory 'UIContract.dll')))
$argsList += $refs | ForEach-Object { '/reference:' + $_ }
$argsList += @((Join-Path $PSScriptRoot 'RadioWindowTestStubs.cs'), (Join-Path $PSScriptRoot 'RadioWindow.cs'))
& $compiler @argsList
if ($LASTEXITCODE -ne 0) { throw 'Radio window API contract compilation failed' }
$runner = Join-Path $outputDirectory 'UIHelperTests.exe'
& $compiler /nologo /codepage:65001 /target:exe ("/out:" + $runner) (Join-Path $PSScriptRoot 'UIHelperTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Radio window test runner compilation failed' }
& $runner $outputDirectory $managed (Join-Path $root 'Game/BepInEx/plugins') (Join-Path $root 'Game/BepInEx/core')
if ($LASTEXITCODE -ne 0) { throw 'Radio window formatting tests failed' }
