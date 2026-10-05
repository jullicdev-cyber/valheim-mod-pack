[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path $PSScriptRoot -Parent
$packRoot = Split-Path (Split-Path $pluginRoot -Parent) -Parent
$buildCache = Join-Path $packRoot '.cache/anyportal-build'
$compiler = Join-Path $buildCache 'compiler/tasks/net472/csc.exe'
$archive = Join-Path $buildCache 'compilers.zip'
$expectedHash = 'fe24ef31a6ffcb7c49383d2fd362763dee291ad9b9d98cc0c19ef80203b99ebc'
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
    New-Item -ItemType Directory -Force -Path $buildCache | Out-Null
    $download = Join-Path $buildCache ('compiler-tests-' + [guid]::NewGuid().ToString('N') + '.zip')
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/4.12.0/microsoft.net.compilers.toolset.4.12.0.nupkg' -OutFile $download -UseBasicParsing
        if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Pinned compiler test archive hash mismatch.' }
        Move-Item -LiteralPath $download -Destination $archive
    }
    finally { if (Test-Path -LiteralPath $download -PathType Leaf) { Remove-Item -LiteralPath $download -Force } }
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Pinned compiler test archive hash mismatch.' }
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $buildCache 'compiler') -Force
}
$fixtureDirectory = Join-Path $packRoot ('.cache/anyportal-plus-core-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $fixtureDirectory | Out-Null
$runner = Join-Path $fixtureDirectory 'CoreTests.exe'
$sources = @(
    'KnownPortal.cs', 'KnownPortalsManager.cs', 'Plus/PlusPortalMetadata.cs', 'Plus/PlusRpcAuthority.cs',
    'RPC/ServerEvents.cs', 'RPC/ClientEvents.cs', 'QueuedAction.cs', 'ZdoTools.cs', 'Extension/Vector3.cs',
    'XPortal.cs', 'XPortalConfig.cs', 'ModInfo.cs'
) | ForEach-Object { Join-Path $pluginRoot ('Source/' + $_) }
$sources += @('CoreTestDoubles.cs', 'CoreTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /shared:false /codepage:65001 /target:exe "/out:$runner" @sources
if ($LASTEXITCODE -ne 0) { throw 'AnyPortal+ core test build failed.' }
& $runner
if ($LASTEXITCODE -ne 0) { throw 'AnyPortal+ core checks failed.' }
