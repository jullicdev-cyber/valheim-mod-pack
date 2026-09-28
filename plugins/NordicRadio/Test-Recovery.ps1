[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$output = Join-Path $env:TEMP ('NordicRadio-RecoveryTests-' + [Guid]::NewGuid().ToString('N') + '.exe')
$arguments = @('/nologo','/codepage:65001','/target:exe',('/out:'+$output))
$arguments += @('RadioProtocol.cs','RadioLibrary.cs','RadioTransfer.cs','RadioService.cs','RadioBulkTransport.cs','RoutedRadioTransport.cs','NetworkTestHost.cs','RecoveryTransportTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') @arguments
if ($LASTEXITCODE -ne 0) { throw 'Recovery transport test compilation failed.' }
& $output
if ($LASTEXITCODE -ne 0) { throw 'Recovery transport tests failed.' }
