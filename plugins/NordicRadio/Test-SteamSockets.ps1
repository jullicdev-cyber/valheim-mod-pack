[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$output = Join-Path $env:TEMP ('NordicRadio-SteamSocketTests-' + [Guid]::NewGuid().ToString('N') + '.exe')
$arguments = @('/nologo', '/codepage:65001', '/target:exe', ('/out:' + $output))
$arguments += @('RadioProtocol.cs','RadioBulkTransport.cs','SteamSocketRadioTransport.cs','SteamSocketTestHost.cs','SteamSocketTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Steam socket transport test compilation failed.' }
& $output
if ($LASTEXITCODE -ne 0) { throw 'Steam socket transport tests failed.' }
