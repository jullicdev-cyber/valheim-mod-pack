[CmdletBinding()]
param([string]$GameDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
try {
    if (-not $GameDirectory) { $GameDirectory = Read-Host 'Valheim directory (contains valheim.exe)' }
    $GameDirectory = $GameDirectory.Trim().Trim('"')
    if (-not $GameDirectory) { throw 'No directory specified.' }
    $target = (Resolve-Path -LiteralPath $GameDirectory).Path
    if (-not (Test-Path -LiteralPath (Join-Path $target 'valheim.exe') -PathType Leaf)) { throw 'valheim.exe not found. Select the game directory.' }
    $source = (Resolve-Path -LiteralPath (Join-Path $root 'Game')).Path
    if ($target -eq $source -or $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $target -eq $root) { throw 'Cannot install inside the pack repository.' }
    if (Get-Process -Name valheim -ErrorAction SilentlyContinue) { throw 'Close Valheim before installing.' }
    $names = @('BepInEx', 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version')
    foreach ($name in $names) {
        $path = Join-Path $target $name
        if (Test-Path -LiteralPath $path) {
            if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing linked target: $path" }
        }
    }
    & (Join-Path $PSScriptRoot 'Verify.ps1')
    $backupRoot = Join-Path $target 'ValheimModpack-backups'
    if ((Test-Path -LiteralPath $backupRoot) -and ((Get-Item -LiteralPath $backupRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Backup directory must not be a link.' }
    $existing = @(Get-ChildItem -LiteralPath $target -Force | Where-Object Name -ne 'ValheimModpack-backups')
    # Do not follow junctions into other disks or silently create an incomplete backup.
    foreach ($item in $existing) {
        $check = @($item)
        if ($item.PSIsContainer) { $check += @(Get-ChildItem -LiteralPath $item.FullName -Force -Recurse) }
        foreach ($entry in $check) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Full backup cannot copy a linked entry safely: $($entry.FullName)" }
        }
    }
    $backup = Join-Path $backupRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
    $stage = Join-Path $backup 'staged'
    $original = Join-Path $backup 'original'
    $snapshot = Join-Path $backup 'full-backup'
    New-Item -ItemType Directory -Path $stage, $original, $snapshot -Force | Out-Null
    Write-Host "Creating full backup: $snapshot"
    foreach ($item in $existing) { Copy-Item -LiteralPath $item.FullName -Destination $snapshot -Recurse -Force }
    Set-Content -LiteralPath (Join-Path $backup 'BACKUP-COMPLETE.txt') -Value 'Full backup completed before installation.'
    # Complete copying before changing any existing files.
    foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $source $name) -Destination $stage -Recurse -Force }
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
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
