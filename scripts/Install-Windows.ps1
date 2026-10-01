[CmdletBinding()]
param([string]$GameDirectory, [string]$SettingsDirectory, [string]$MusicUrl, [switch]$SkipMusic)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'GamePath.ps1')

function Assert-BackupEntry($Entry) {
    if ($Entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        if ($Entry.PSIsContainer -or $Entry.LinkType -ne 'SymbolicLink') {
            throw "Unsupported directory link or reparse point: $($Entry.FullName)"
        }
        # File links from Vortex are backed up as independent file contents.
        # Opening now also detects dangling links before any installation changes.
        $stream = [IO.File]::OpenRead($Entry.FullName)
        $stream.Dispose()
    } elseif ($Entry.PSIsContainer) {
        foreach ($child in Get-ChildItem -LiteralPath $Entry.FullName -Force) { Assert-BackupEntry $child }
    }
}

function Copy-BackupEntry($Entry, [string]$Destination) {
    $path = Join-Path $Destination $Entry.Name
    if ($Entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        # Do not recreate links that depend on Vortex's staging directory.
        $inputStream = [IO.File]::OpenRead($Entry.FullName)
        try {
            $outputStream = [IO.File]::Create($path)
            try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
        } finally { $inputStream.Dispose() }
    } elseif ($Entry.PSIsContainer) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        foreach ($child in Get-ChildItem -LiteralPath $Entry.FullName -Force) { Copy-BackupEntry $child $path }
    } else {
        Copy-Item -LiteralPath $Entry.FullName -Destination $path -Force
    }
}

try {
    $target = Get-GameDirectory -PackRoot $root -GameDirectory $GameDirectory -SettingsDirectory $SettingsDirectory
    $source = (Resolve-Path -LiteralPath (Join-Path $root 'Game')).Path
    if ($target -eq $source -or $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $target -eq $root) { throw 'Cannot install inside the pack repository.' }
    if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Close Valheim and its server before installing.' }
    $names = @('BepInEx', 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version')
    foreach ($name in $names) {
        $path = Join-Path $target $name
        if (Test-Path -LiteralPath $path) {
            Assert-BackupEntry (Get-Item -LiteralPath $path -Force)
        }
    }
    & (Join-Path $PSScriptRoot 'Verify.ps1')
    $backupRoot = Join-Path $target 'ValheimModpack-backups'
    if ((Test-Path -LiteralPath $backupRoot) -and ((Get-Item -LiteralPath $backupRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Backup directory must not be a link.' }
    $existing = @(Get-ChildItem -LiteralPath $target -Force | Where-Object Name -ne 'ValheimModpack-backups')
    # Inspect one directory at a time; never descend through a directory link.
    foreach ($item in $existing) { Assert-BackupEntry $item }
    $backup = Join-Path $backupRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
    $stage = Join-Path $backup 'staged'
    $original = Join-Path $backup 'original'
    $snapshot = Join-Path $backup 'full-backup'
    New-Item -ItemType Directory -Path $stage, $original, $snapshot -Force | Out-Null
    Write-Host "Creating full backup: $snapshot"
    foreach ($item in $existing) { Copy-BackupEntry $item $snapshot }
    Set-Content -LiteralPath (Join-Path $backup 'BACKUP-COMPLETE.txt') -Value 'Full backup completed before installation.'
    # Complete copying before changing any existing files.
    foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $source $name) -Destination $stage -Recurse -Force }
    # Preserve Quick Stack's personal favorites and the two legacy Azu files
    # needed for one-time migration, not old shared mod settings.
    $previousConfig = Join-Path $target 'BepInEx/config'
    if (Test-Path -LiteralPath $previousConfig -PathType Container) {
        $stagedConfig = Join-Path $stage 'BepInEx/config'
        foreach ($personal in Get-ChildItem -LiteralPath $previousConfig -File -Force) {
            if ($personal.Name -match '^(QuickStackStore|AzuAutoStore|AzuExtendedPlayerInventory)_player_-?\d+\.dat$') {
                Copy-BackupEntry $personal $stagedConfig
            }
        }
    }
    # Bindrune keeps personal key overrides outside config; replacing BepInEx must retain them.
    foreach ($relative in @('BepInEx/bindrune.keys', 'BepInEx/bindrune.spare', 'BepInEx/config/Bindrune/situations.txt', 'BepInEx/config/isimp.Bindrune.cfg')) {
        $personalPath = Join-Path $target $relative
        if (-not (Test-Path -LiteralPath $personalPath)) { continue }
        $personalEntry = Get-Item -LiteralPath $personalPath -Force
        if ($personalEntry.PSIsContainer -or ($personalEntry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Personal bindings must be a regular file: $relative" }
        $personalParent = Split-Path (Join-Path $stage $relative) -Parent
        New-Item -ItemType Directory -Force -Path $personalParent | Out-Null
        Copy-Item -LiteralPath $personalPath -Destination (Join-Path $stage $relative)
    }
    if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Game started during backup; installation stopped.' }
    $saved = @()
    $installed = @()
    try {
        foreach ($name in $names) {
            $path = Join-Path $target $name
            if (Test-Path -LiteralPath $path) {
                Move-Item -LiteralPath $path -Destination (Join-Path $original $name)
                $saved += $name
            }
            Move-Item -LiteralPath (Join-Path $stage $name) -Destination $path
            $installed += $name
        }
    } catch {
        $failed = Join-Path $backup 'failed-install'
        New-Item -ItemType Directory -Path $failed -Force | Out-Null
        foreach ($name in $installed) { Move-Item -LiteralPath (Join-Path $target $name) -Destination (Join-Path $failed $name) }
        foreach ($name in $saved) { Move-Item -LiteralPath (Join-Path $original $name) -Destination (Join-Path $target $name) }
        throw
    }
    @("Installed: $(Get-Date -Format o)", "Target: $target", "Replaced entries: $($saved -join ', ')", 'Rollback: close the game, move the installed entries aside, then copy original/* back to the game directory.') | Set-Content -LiteralPath (Join-Path $backup 'INSTALL.txt')
    Write-Host "Installed successfully. Full backup: $snapshot"
    Write-Host "Replaced files for quick rollback: $original"
    Write-Host 'Start Valheim through Steam. Set world Resources to x2 and Portals to Casual.'
    if (-not $SkipMusic) {
        . (Join-Path $PSScriptRoot 'Music.ps1')
        if (-not $MusicUrl) { $MusicUrl = (Get-Content -LiteralPath (Join-Path $root 'music-source.txt') -Raw).Trim() }
        try { Install-RadioMusic $target $MusicUrl }
        catch { throw "Mods installed successfully, but music update failed: $_. Retry with Update-Music-Windows.cmd." }
    }
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
