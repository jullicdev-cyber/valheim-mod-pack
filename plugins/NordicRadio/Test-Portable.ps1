[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$outputDirectory = Join-Path $root '.cache/nordic-radio-portable-tests'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$runner = Join-Path $outputDirectory 'PortableControllerTests.exe'
$arguments = @('/nologo', '/codepage:65001', '/target:exe', ('/out:' + $runner))
$arguments += @('IRadioTarget.cs','PortableController.cs','PortableControllerTestStubs.cs','PortableControllerTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Portable controller test compilation failed.' }
& $runner
if ($LASTEXITCODE -ne 0) { throw 'Portable controller behavior tests failed.' }
