[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$packRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$destination = Join-Path $packRoot ('.cache/anyportal-ui-tests-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$output = Join-Path $destination 'PortalUiTests.exe'
$sources = @('Source/UI/PortalConfigurationPanel.cs', 'Source/Plus/PortalListModel.cs', 'Source/Plus/PlusText.cs',
    'Tests/PortalUiTestDoubles.cs', 'Tests/PortalUiTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /codepage:65001 /target:exe "/out:$output" @sources
if ($LASTEXITCODE -ne 0) { throw 'AnyPortal+ UI test compilation failed.' }
& $output
if ($LASTEXITCODE -ne 0) { throw 'AnyPortal+ UI tests failed.' }
