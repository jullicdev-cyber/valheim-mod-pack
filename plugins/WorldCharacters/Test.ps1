[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory, [string]$PlayerDataFixture)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Test-AdministrativeCheckpoint.ps1')
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$folder = Join-Path $root ('.cache/worldcharacters-tests-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $folder | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$runner = Join-Path $folder 'Tests.exe'
$sources = @('State.cs','NativeInventory.cs','StateStore.cs','Session.cs','Tests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /codepage:65001 /target:exe "/out:$runner" @sources
if ($LASTEXITCODE -ne 0) { throw 'World Characters test build failed' }
& $runner (Join-Path $folder 'state') $PlayerDataFixture
if ($LASTEXITCODE -ne 0) { throw 'World Characters tests failed' }
foreach ($suite in @('SnapshotWriterTests','SessionAsyncTests','StateAllocationTests','SnapshotFlowTests','SnapshotRateLimitTests')) {
    $asyncRunner = Join-Path $folder ($suite + '.exe')
    $asyncSources = @('State.cs','NativeInventory.cs','StateStore.cs','Session.cs','SnapshotWriter.cs','SnapshotRateLimit.cs',($suite + '.cs')) | ForEach-Object { Join-Path $PSScriptRoot $_ }
    & $compiler /nologo /codepage:65001 /target:exe "/out:$asyncRunner" @asyncSources
    if ($LASTEXITCODE -ne 0) { throw "$suite build failed" }
    & $asyncRunner (Join-Path $folder ($suite + '-state'))
    if ($LASTEXITCODE -ne 0) { throw "$suite failed" }
}
& (Join-Path $PSScriptRoot 'Build.ps1') -GameDirectory $GameDirectory -OutputFile (Join-Path $folder 'WorldCharacters.dll')
& (Join-Path $PSScriptRoot 'Test-Contracts.ps1') -GameDirectory $GameDirectory -PluginAssembly (Join-Path $folder 'WorldCharacters.dll')
