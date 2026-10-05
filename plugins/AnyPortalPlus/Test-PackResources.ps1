[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$fixture = Join-Path $root ('.cache/anyportal-pack-resources-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $fixture, (Join-Path $fixture 'scripts'), (Join-Path $fixture 'config'), (Join-Path $fixture 'local-plugins'), (Join-Path $fixture 'resources') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'scripts/Build.ps1') -Destination (Join-Path $fixture 'scripts/Build.ps1')
Copy-Item -LiteralPath (Join-Path $root 'scripts/Verify.ps1') -Destination (Join-Path $fixture 'scripts/Verify.ps1')
$script:checks = 0
function Check([bool]$Passed, [string]$Label) { $script:checks++; if (-not $Passed) { throw $Label } }
function TextFile([string]$Path, [string]$Value) { [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false)) }
function HashFile([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Save-Lock { TextFile (Join-Path $fixture 'mods.lock.json') ($script:lock | ConvertTo-Json -Depth 12) }
function Reject([scriptblock]$Action, [string]$Expected, [string]$Label) {
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
    Check ($null -ne $failure -and $failure.Contains($Expected)) ($Label + '; failure=' + $failure)
}
TextFile (Join-Path $fixture 'VERSION') '1.0.0'
TextFile (Join-Path $fixture 'config/Test.cfg') "[Test]`nEnabled = true`n"
TextFile (Join-Path $fixture 'local-plugins/Base.dll') 'isolated-base-dll-payload'
TextFile (Join-Path $fixture 'local-plugins/Fork.dll') 'isolated-fork-dll-payload'
TextFile (Join-Path $fixture 'resources/LICENSE') 'isolated-resource-license-payload'
$base = [ordered]@{ id = 'Test-Base'; version = '1.0.0'; source = 'local-plugins/Base.dll'; destination = 'BepInEx/plugins/Base.dll'; sha256 = HashFile (Join-Path $fixture 'local-plugins/Base.dll') }
$resource = [ordered]@{ source = 'resources/LICENSE'; destination = 'BepInEx/plugins/Fork/LICENSE'; sha256 = HashFile (Join-Path $fixture 'resources/LICENSE') }
$fork = [ordered]@{ id = 'Test-Fork'; version = '1.0.0'; source = 'local-plugins/Fork.dll'; destination = 'BepInEx/plugins/Fork/Fork.dll'; sha256 = HashFile (Join-Path $fixture 'local-plugins/Fork.dll'); dependencies = @('Test-Base-1.0.0'); resourceFiles = @($resource) }
$script:lock = [ordered]@{ packVersion = '1.0.0'; packages = @(); localPlugins = @($base, $fork) }; Save-Lock
$pack = Join-Path $fixture 'built'
& (Join-Path $fixture 'scripts/Build.ps1') -OutputDirectory $pack | Out-Null
Check (Test-Path -LiteralPath (Join-Path $pack $resource.destination) -PathType Leaf) 'clean build includes fork resource'
Check ((HashFile (Join-Path $pack $resource.destination)) -eq $resource.sha256) 'clean build resource exact hash'
$inventory = @(Get-ChildItem -LiteralPath $pack -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($pack.Length + 1).Replace('\','/'); sha256 = HashFile $_.FullName }
})
TextFile (Join-Path $fixture 'files.sha256.json') ($inventory | ConvertTo-Json -Depth 8)
function Verify-Fixture { & (Join-Path $fixture 'scripts/Verify.ps1') -PackDirectory $pack -ManifestDirectory $fixture }
Verify-Fixture | Out-Null; Check $true 'local dependency can resolve to another local plugin'
$fork.dependencies = @('Test-Missing-1.0.0'); Save-Lock
Reject { Verify-Fixture } 'Missing/incompatible dependency:' 'missing local dependency rejected'
$fork.dependencies = @('Test-Base-2.0.0'); Save-Lock
Reject { Verify-Fixture } 'Missing/incompatible dependency:' 'old local dependency rejected'
$fork.dependencies = @('Test-Base-1.0.0'); Save-Lock
TextFile (Join-Path $fixture 'local-plugins/Fork.dll') 'wrong-source-payload'
Reject { Verify-Fixture } 'local plugin source:' 'changed local DLL source rejected despite correct pack'
TextFile (Join-Path $fixture 'local-plugins/Fork.dll') 'isolated-fork-dll-payload'
$originalHash = $resource.sha256; $resource.sha256 = '0' * 64; Save-Lock
Reject { Verify-Fixture } 'local resource destination:' 'resource hash lock mismatch rejected independently of manifest'
Reject { & (Join-Path $fixture 'scripts/Build.ps1') -OutputDirectory (Join-Path $fixture 'bad-resource-build') } 'Local resource hash mismatch:' 'clean build rejects incorrect source resource hash'
$resource.sha256 = $originalHash; Save-Lock
TextFile (Join-Path $fixture 'resources/LICENSE') 'modified-resource-source'
Reject { Verify-Fixture } 'local resource source:' 'changed resource source rejected despite correct pack'
TextFile (Join-Path $fixture 'resources/LICENSE') 'isolated-resource-license-payload'
$resource.destination = '../escape.txt'; Save-Lock
Reject { Verify-Fixture } 'Unsafe locked path:' 'resource destination traversal rejected by verifier'
Reject { & (Join-Path $fixture 'scripts/Build.ps1') -OutputDirectory (Join-Path $fixture 'unsafe-resource-build') } 'Unsafe locked path:' 'resource destination traversal rejected by builder'
Check (-not (Test-Path -LiteralPath (Join-Path $fixture 'escape.txt'))) 'traversal produced no outside file'
$resource.destination = 'BepInEx/plugins/Fork/LICENSE'; $fork.source = 'not-in-distribution/Fork.dll'; $resource.source = 'not-in-distribution/LICENSE'; Save-Lock
Verify-Fixture | Out-Null; Check $true 'manifest plus Game distribution supported without optional source payloads'
$fork.source = 'local-plugins/Fork.dll'; $resource.source = 'resources/LICENSE'; $fork.id = 'Test-Base'; Save-Lock
Reject { Verify-Fixture } 'Duplicate/missing locked plugin id:' 'duplicate package/local identities rejected'
$fork.id = 'Test-Fork'; $script:lock.packages = @([ordered]@{ id = 'Test-Upstream'; version = '1.0.0'; sha256 = '0' * 64 }); Save-Lock
$cache = Join-Path $fixture '.cache/locked'; New-Item -ItemType Directory -Path $cache -Force | Out-Null
TextFile (Join-Path $cache 'Test-Upstream-1.0.0.zip') 'bad-cached-package'
Reject { Verify-Fixture } 'cached package:' 'cached package hash mismatch rejected'
Write-Output "PASS: $script:checks pack resource and dependency assertions; isolated fixtures, no game install or network requests."
