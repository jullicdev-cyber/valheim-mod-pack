[CmdletBinding()]
param([string]$GameDirectory, [string]$SettingsDirectory, [string]$BackupDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'GamePath.ps1')
$excludedNames = @('ValheimModpack-backups', 'Music-backups', 'backups')
$files = New-Object 'System.Collections.Generic.List[object]'
$directories = New-Object 'System.Collections.Generic.List[string]'
$materializedLinks = New-Object 'System.Collections.Generic.List[object]'
$backup = $null

function Get-BackupFileHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha256.Dispose() }
}

function Is-ExcludedBackupDirectory($Entry) {
    return $Entry.PSIsContainer -and ($Entry.Name -in $excludedNames -or
        $Entry.Name -match '^\.valheim-modpack-install-' -or $Entry.Name -match '^\.music-update-')
}

function Assert-BackupEntry($Entry) {
    if (Is-ExcludedBackupDirectory $Entry) { return }
    if ($Entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        if ($Entry.PSIsContainer -or $Entry.LinkType -ne 'SymbolicLink') {
            throw "Unsupported directory link or reparse point: $($Entry.FullName)"
        }
        # Materialize readable Vortex file links; never depend on its staging folder.
        $stream = [IO.File]::OpenRead($Entry.FullName)
        $stream.Dispose()
    } elseif ($Entry.PSIsContainer) {
        foreach ($child in Get-ChildItem -LiteralPath $Entry.FullName -Force) { Assert-BackupEntry $child }
    }
}

function Assert-BackupDestination([string]$Directory) {
    $current = [IO.Path]::GetFullPath($Directory)
    while ($current) {
        $entry = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($null -ne $entry) {
            if (-not $entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Backup destination and its existing parents must be regular directories: $current"
            }
        }
        $parent = Split-Path $current -Parent
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Copy-BackupEntry($Entry, [string]$Destination) {
    if (Is-ExcludedBackupDirectory $Entry) { return }
    $path = Join-Path $Destination $Entry.Name
    $relative = $Entry.FullName.Substring($sourceBoundary.Length).Replace('\','/')
    if ($Entry.PSIsContainer) {
        New-Item -ItemType Directory -Path $path | Out-Null
        $directories.Add($relative)
        foreach ($child in Get-ChildItem -LiteralPath $Entry.FullName -Force) { Copy-BackupEntry $child $path }
    } else {
        $sha256 = Get-BackupFileHash $Entry.FullName
        if ($Entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            $inputStream = [IO.File]::OpenRead($Entry.FullName)
            try {
                $outputStream = [IO.File]::Create($path)
                try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
            } finally { $inputStream.Dispose() }
            $materializedLinks.Add(@{ path=$relative; target=@($Entry.Target) })
        } else { Copy-Item -LiteralPath $Entry.FullName -Destination $path }
        $files.Add(@{ path=$relative; sha256=$sha256 })
    }
}

try {
    $target = (Get-GameDirectory -PackRoot $root -GameDirectory $GameDirectory -SettingsDirectory $SettingsDirectory).TrimEnd('\','/')
    if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Close Valheim and its server before making a full backup.' }
    if (-not $BackupDirectory) { $BackupDirectory = Join-Path $root 'backups' }
    $destination = [IO.Path]::GetFullPath($BackupDirectory)
    if ($destination -ne [IO.Path]::GetPathRoot($destination)) { $destination = $destination.TrimEnd('\','/') }
    $sourceBoundary = $target + [IO.Path]::DirectorySeparatorChar
    if ($destination -eq $target -or $destination.StartsWith($sourceBoundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Keep full backups outside the Valheim game directory.'
    }
    Assert-BackupDestination $destination
    $existing = @(Get-ChildItem -LiteralPath $target -Force)
    foreach ($entry in $existing) { Assert-BackupEntry $entry }
    $backup = Join-Path $destination ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
    if (Test-Path -LiteralPath $backup) { throw 'Backup path already exists.' }
    $snapshot = Join-Path $backup 'full-game-backup'
    New-Item -ItemType Directory -Path $snapshot -Force | Out-Null
    Write-Host "Creating full game backup: $snapshot"
    foreach ($entry in $existing) { Copy-BackupEntry $entry $snapshot }
    foreach ($record in $files) {
        $copied = Join-Path $snapshot $record.path
        $copiedEntry = Get-Item -LiteralPath $copied -Force
        if ($copiedEntry.PSIsContainer -or ($copiedEntry.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            (Get-BackupFileHash $copied) -ne $record.sha256 -or
            (Get-BackupFileHash (Join-Path $target $record.path)) -ne $record.sha256) {
            throw "Backup verification failed or source changed: $($record.path)"
        }
    }
    if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Game started during backup; the backup is incomplete.' }
    $metadata = @{ format=1; gameDirectory=$target; createdAt=(Get-Date -Format o); files=@($files.ToArray());
        directories=@($directories.ToArray()); links=@(); materializedFileLinks=@($materializedLinks.ToArray());
        excludedDirectoryNames=$excludedNames; excludedDirectoryPrefixes=@('.valheim-modpack-install-', '.music-update-') }
    [IO.File]::WriteAllText((Join-Path $backup 'backup.json'), (($metadata | ConvertTo-Json -Depth 8) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    Set-Content -LiteralPath (Join-Path $backup 'BACKUP-COMPLETE.txt') -Value 'Full game backup copied and SHA256 verified.' -Encoding UTF8
    Write-Host "Backup complete: $snapshot"
    Write-Host "Verified files: $($files.Count). This backs up the game folder; Steam Cloud and AppData saves outside it are separate."
} catch {
    if ($backup) { Write-Warning "Backup did not complete. Partial files retained at $backup; there is no BACKUP-COMPLETE.txt marker." }
    Write-Error $_ -ErrorAction Continue
    exit 1
}
