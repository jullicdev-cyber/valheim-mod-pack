[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$directory = Join-Path $root ('.cache/anyportal-marker-tests-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$output = Join-Path $directory 'MapMarkerTests.exe'
$sources = @('Source/Plus/MapPinBindings.cs', 'Source/Plus/PlusMapMarkers.cs', 'Tests/MapMarkerTestDoubles.cs', 'Tests/MapMarkerTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /codepage:65001 /target:exe /optimize+ /out:$output $sources
if ($LASTEXITCODE -ne 0) { throw 'AnyPortal+ marker test compilation failed' }
& $output (Join-Path $directory 'data')
if ($LASTEXITCODE -ne 0) { throw 'AnyPortal+ map marker tests failed' }
