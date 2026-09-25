[CmdletBinding()]
param([string]$PackDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $PackDirectory) { $PackDirectory = Join-Path $root 'Game' }
$pack = [IO.Path]::GetFullPath($PackDirectory)
$inventory = Get-Content (Join-Path $root 'files.sha256.json') -Raw | ConvertFrom-Json
foreach ($entry in $inventory) {
    $file = Join-Path $pack $entry.path
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing file: $($entry.path)" }
    $stream = [IO.File]::OpenRead($file)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
    if ($hash -ne $entry.sha256) { throw "Changed file: $($entry.path)" }
}
$actual = @(Get-ChildItem -LiteralPath $pack -File -Recurse -Force)
if ($actual.Count -ne @($inventory).Count) { throw 'Unexpected extra files in pack.' }
$lock = Get-Content (Join-Path $root 'mods.lock.json') -Raw | ConvertFrom-Json
foreach ($mod in $lock.packages) {
    foreach ($dependency in $mod.dependencies) {
        if ($dependency -notmatch '^(.*)-(\d+\.\d+\.\d+)$') { throw "Unknown dependency format: $dependency" }
        $id = $Matches[1]
        $minimum = [version]$Matches[2]
        $found = @($lock.packages | Where-Object id -eq $id)
        if ($found.Count -ne 1 -or [version]$found[0].version -lt $minimum) { throw "Missing/incompatible dependency: $dependency" }
    }
}
Write-Output "OK: $($inventory.Count) file hashes, $($lock.packages.Count) locked packages and declared dependencies."
Write-Output 'This verifies the package, not runtime gameplay. World modifiers must be applied separately.'
