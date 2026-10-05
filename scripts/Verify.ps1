[CmdletBinding()]
param([string]$PackDirectory, [string]$ManifestDirectory)
$ErrorActionPreference = 'Stop'
function Resolve-PackChild([string]$BaseDirectory, [string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath -match '(^|[\\/])\.\.?([\\/]|$)') { throw "Unsafe locked path: $RelativePath" }
    $base = [IO.Path]::GetFullPath($BaseDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $file = [IO.Path]::GetFullPath((Join-Path $base $RelativePath))
    if (-not $file.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Locked path escapes its directory: $RelativePath"
    }
    return $file
}
function Require-LockedHash([string]$File, [string]$Expected, [string]$Label) {
    if (-not (Test-Path -LiteralPath $File -PathType Leaf)) { throw "Missing $Label" }
    if ($Expected -notmatch '^[a-fA-F0-9]{64}$') { throw "Hash mismatch: $Label" }
    # Keep the installer usable in Windows PowerShell even when its inherited
    # PSModulePath does not expose the Get-FileHash module.
    $stream = [IO.File]::OpenRead($File)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
    if ($hash -ne $Expected) {
        throw "Hash mismatch: $Label"
    }
}
$root = Split-Path $PSScriptRoot -Parent
if ($ManifestDirectory) { $root = (Resolve-Path -LiteralPath $ManifestDirectory).ProviderPath }
$version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
$lock = Get-Content -LiteralPath (Join-Path $root 'mods.lock.json') -Raw | ConvertFrom-Json
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $version -ne $lock.packVersion) { throw "Pack version mismatch: VERSION=$version, mods.lock.json=$($lock.packVersion)" }
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
$allMods = @($lock.packages) + @($lock.localPlugins) | Where-Object { $null -ne $_ }
$ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($mod in $allMods) {
    if ([string]::IsNullOrWhiteSpace($mod.id) -or -not $ids.Add($mod.id)) { throw "Duplicate/missing locked plugin id: $($mod.id)" }
}
foreach ($mod in $allMods) {
    foreach ($dependency in $mod.dependencies) {
        if ($dependency -notmatch '^(.*)-(\d+\.\d+\.\d+)$') { throw "Unknown dependency format: $dependency" }
        $id = $Matches[1]
        $minimum = [version]$Matches[2]
        $found = @($allMods | Where-Object id -eq $id)
        if ($found.Count -ne 1 -or [version]$found[0].version -lt $minimum) { throw "Missing/incompatible dependency: $dependency" }
    }
}
foreach ($local in $lock.localPlugins) {
    $destination = Resolve-PackChild $pack $local.destination
    Require-LockedHash $destination $local.sha256 ("local plugin destination: " + $local.id)
    $source = Resolve-PackChild $root $local.source
    # Some distribution manifests contain only Game plus metadata. If source
    # payloads are included, they must agree; Build always requires them.
    if (Test-Path -LiteralPath $source -PathType Leaf) { Require-LockedHash $source $local.sha256 ("local plugin source: " + $local.id) }
    foreach ($resource in $local.resourceFiles) {
        $resourceDestination = Resolve-PackChild $pack $resource.destination
        Require-LockedHash $resourceDestination $resource.sha256 ("local resource destination: " + $resource.destination)
        $resourceSource = Resolve-PackChild $root $resource.source
        if (Test-Path -LiteralPath $resourceSource -PathType Leaf) {
            Require-LockedHash $resourceSource $resource.sha256 ("local resource source: " + $resource.source)
        }
    }
}
foreach ($mod in $lock.packages) {
    $archive = Join-Path $root ('.cache/locked/' + $mod.id + '-' + $mod.version + '.zip')
    if (Test-Path -LiteralPath $archive -PathType Leaf) { Require-LockedHash $archive $mod.sha256 ("cached package: " + $mod.id) }
}
Write-Output "OK: $(@($inventory).Count) file hashes, $(@($lock.packages).Count) locked packages, $(@($lock.localPlugins).Count) local plugins and declared dependencies."
Write-Output 'This verifies the package, not runtime gameplay. World modifiers must be applied separately.'
