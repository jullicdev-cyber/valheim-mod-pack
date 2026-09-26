[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root ('dist/rebuild-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Output directory already exists: $output" }
$lock = Get-Content (Join-Path $root 'mods.lock.json') -Raw | ConvertFrom-Json
$cache = Join-Path $root '.cache/locked'
New-Item -ItemType Directory -Force $cache, $output | Out-Null
foreach ($mod in $lock.packages) {
    $archive = Join-Path $cache ($mod.id + '-' + $mod.version + '.zip')
    if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $mod.downloadUrl -OutFile $archive }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $mod.sha256) { throw "Archive hash mismatch: $($mod.id)" }
    $unpack = Join-Path $cache ($mod.id + '-' + $mod.version)
    Expand-Archive -LiteralPath $archive -DestinationPath $unpack -Force
    $manifest = Get-Content (Join-Path $unpack 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.version_number -ne $mod.version) { throw "Manifest version mismatch: $($mod.id)" }
    $source = if ($mod.sourceSubdirectory) { Join-Path $unpack $mod.sourceSubdirectory } else { $unpack }
    $target = if ($mod.destination) { Join-Path $output $mod.destination } else { $output }
    New-Item -ItemType Directory -Force $target | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $source -Force) {
        if (-not $mod.sourceSubdirectory -and $entry.Name -match '^(manifest\.json|icon\.png|README\.md|CHANGELOG\.md|LICENSE.*)$') { continue }
        Copy-Item -LiteralPath $entry.FullName -Destination $target -Recurse -Force
    }
}
foreach ($local in $lock.localPlugins) {
    $source = Join-Path $root $local.source
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $local.sha256) { throw "Local plugin hash mismatch: $($local.id)" }
    $target = Join-Path $output $local.destination
    New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}
$config = Join-Path $output 'BepInEx/config'
New-Item -ItemType Directory -Force $config | Out-Null
Copy-Item -Path (Join-Path $root 'config/*.cfg') -Destination $config -Force
Write-Output "Built locked pack: $output"
