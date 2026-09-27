[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$folder = Join-Path $root '.cache/quick-pin-controller-tests'
New-Item -ItemType Directory -Force $folder | Out-Null
$output = Join-Path $folder 'QuickPinControllerTests.exe'
$reference = Join-Path (Split-Path $compiler -Parent) 'System.Runtime.Serialization.dll'
$sources = @('QuickPinController.cs', 'PinPresetStore.cs', 'PinPresetCatalog.cs', 'PinPresetLocalization.cs',
    'QuickPinHostDoubles.cs', 'QuickPinHostTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /codepage:65001 /target:exe "/reference:$reference" "/out:$output" @sources
if ($LASTEXITCODE -ne 0) { throw 'Quick pin controller host build failed.' }
& $output
if ($LASTEXITCODE -ne 0) { throw 'Quick pin controller host tests failed.' }
