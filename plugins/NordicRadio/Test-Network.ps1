[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$output = Join-Path $env:TEMP ('NordicRadio-NetworkTests-' + [Guid]::NewGuid().ToString('N') + '.exe')
$arguments = @('/nologo', '/codepage:65001', '/target:exe', ('/out:' + $output))
$arguments += @('RadioProtocol.cs', 'RadioLibrary.cs', 'RadioService.cs', 'RadioTransfer.cs', 'NetworkTestHost.cs', 'NetworkTests.cs', 'TransferTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$arguments += Join-Path $PSScriptRoot 'RadioBulkTransport.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Network test compilation failed.' }
& $output
if ($LASTEXITCODE -ne 0) { throw 'Network tests failed.' }
