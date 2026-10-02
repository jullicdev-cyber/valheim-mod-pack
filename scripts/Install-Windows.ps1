[CmdletBinding()]
param([string]$GameDirectory, [string]$SettingsDirectory, [string]$MusicUrl, [switch]$SkipMusic)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'GamePath.ps1')

function Assert-InstallEntry($Entry) {
    if ($Entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        if ($Entry.PSIsContainer -or $Entry.LinkType -ne 'SymbolicLink') {
            throw "Unsupported directory link or reparse point: $($Entry.FullName)"
        }
        # Vortex file links are accepted; directory links are never traversed.
        # Opening now also detects dangling links before any installation changes.
        $stream = [IO.File]::OpenRead($Entry.FullName)
        $stream.Dispose()
    } elseif ($Entry.PSIsContainer) {
        foreach ($child in Get-ChildItem -LiteralPath $Entry.FullName -Force) { Assert-InstallEntry $child }
    }
}

function Copy-PersonalEntry($Entry, [string]$Destination) {
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
        foreach ($child in Get-ChildItem -LiteralPath $Entry.FullName -Force) { Copy-PersonalEntry $child $path }
    } else {
        Copy-Item -LiteralPath $Entry.FullName -Destination $path -Force
    }
}

function Assert-CleanupEntry($Entry) {
    if ($Entry.PSIsContainer) {
        if ($Entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Cannot clean a linked directory: $($Entry.FullName)" }
        foreach ($child in Get-ChildItem -LiteralPath $Entry.FullName -Force) { Assert-CleanupEntry $child }
    }
    # File symlinks are removed as links; their staging targets remain untouched.
}

function Remove-InstallTransaction([string]$Directory, [string]$GameRoot) {
    $absolute = [IO.Path]::GetFullPath($Directory).TrimEnd('\','/')
    $boundary = [IO.Path]::GetFullPath($GameRoot).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if (-not $absolute.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path $absolute -Leaf) -notmatch '^\.valheim-modpack-install-[a-f0-9]{8}$') {
        throw "Refusing to remove an unexpected transaction path: $absolute"
    }
    $entry = Get-Item -LiteralPath $absolute -Force -ErrorAction SilentlyContinue
    if ($null -ne $entry) {
        Assert-CleanupEntry $entry
        Remove-Item -LiteralPath $absolute -Recurse -Force
    }
}

function Merge-RadioPersonalAudio([string]$Previous, [string]$Staged) {
    # Only two local preferences survive shared-config replacement. Never copy
    # old distance, amplification, recipe or networking settings into a new pack.
    $previousEntry = Get-Item -LiteralPath $Previous -Force -ErrorAction SilentlyContinue
    if ($null -eq $previousEntry) { return }
    if ($previousEntry.PSIsContainer -or ($previousEntry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Personal NordicRadio audio settings must be an unlinked regular file.'
    }
    $ancestor = Split-Path $previousEntry.FullName -Parent
    while ($ancestor) {
        $ancestorEntry = Get-Item -LiteralPath $ancestor -Force -ErrorAction Stop
        if ($ancestorEntry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Personal NordicRadio audio settings must be an unlinked regular file.' }
        $ancestor = Split-Path $ancestor -Parent
    }
    if (-not (Test-Path -LiteralPath $Staged -PathType Leaf) -or $previousEntry.Length -gt 1MB) { return }
    $values = @{}
    $seen = @{}
    $audio = $false
    foreach ($line in [IO.File]::ReadAllLines($Previous)) {
        $trimmed = $line.Trim()
        if ($trimmed -match '^\[([^\]]+)\]$') { $audio = $Matches[1] -ceq 'Audio'; continue }
        if (-not $audio -or $trimmed.StartsWith('#') -or $trimmed.StartsWith(';') -or $trimmed -notmatch '^([^=]+)=(.*)$') { continue }
        $key = $Matches[1].Trim(); $value = $Matches[2].Trim()
        if ($key -cne 'PersonalVolume' -and $key -cne 'PersonalMuted') { continue }
        if ($seen.ContainsKey($key)) { $values.Remove($key); continue }
        $seen[$key] = $true
        if ($key -ceq 'PersonalMuted') {
            if ($value -match '^(true|false)$') { $values[$key] = $value.ToLowerInvariant() }
        } else {
            [double]$number = 0
            if ($value -match '^[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$' -and
                [double]::TryParse($value, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -and
                -not [double]::IsNaN($number) -and -not [double]::IsInfinity($number) -and $number -ge 0 -and $number -le 1) {
                $values[$key] = $number.ToString('R', [Globalization.CultureInfo]::InvariantCulture)
            }
        }
    }
    if ($values.Count -eq 0) { return }
    $lines = New-Object 'System.Collections.Generic.List[string]'
    $audio = $false; $foundAudio = $false; $written = @{}
    foreach ($line in [IO.File]::ReadAllLines($Staged)) {
        $trimmed = $line.Trim()
        if ($trimmed -match '^\[([^\]]+)\]$') {
            $nextAudio = $Matches[1] -ceq 'Audio'
            if ($audio -and -not $nextAudio) {
                foreach ($key in @('PersonalVolume','PersonalMuted')) {
                    if ($values.ContainsKey($key) -and -not $written.ContainsKey($key)) { $lines.Add($key + ' = ' + $values[$key]); $written[$key] = $true }
                }
            }
            $audio = $nextAudio; if ($audio) { $foundAudio = $true }
        }
        if ($audio -and $trimmed -match '^(PersonalVolume|PersonalMuted)\s*=') {
            $key = $Matches[1]
            if ($values.ContainsKey($key)) { $lines.Add($key + ' = ' + $values[$key]); $written[$key] = $true; continue }
        }
        $lines.Add($line)
    }
    if (-not $foundAudio) { $lines.Add(''); $lines.Add('[Audio]') }
    if ($audio -or -not $foundAudio) {
        foreach ($key in @('PersonalVolume','PersonalMuted')) {
            if ($values.ContainsKey($key) -and -not $written.ContainsKey($key)) { $lines.Add($key + ' = ' + $values[$key]) }
        }
    }
    [IO.File]::WriteAllLines($Staged, $lines, (New-Object System.Text.UTF8Encoding($false)))
}

$transaction = $null
$retainRecovery = $false

try {
    $target = Get-GameDirectory -PackRoot $root -GameDirectory $GameDirectory -SettingsDirectory $SettingsDirectory
    $source = (Resolve-Path -LiteralPath (Join-Path $root 'Game')).Path
    if ($target -eq $source -or $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $target -eq $root) { throw 'Cannot install inside the pack repository.' }
    if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Close Valheim and its server before installing.' }
    $names = @('BepInEx', 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version')
    foreach ($name in $names) {
        $path = Join-Path $target $name
        $entry = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        if ($null -ne $entry) { Assert-InstallEntry $entry }
    }
    & (Join-Path $PSScriptRoot 'Verify.ps1')
    # Staging and rename-only rollback stay on the game volume. This is temporary,
    # not an automatic retained backup of the entire game directory.
    $candidate = Join-Path $target ('.valheim-modpack-install-' + [guid]::NewGuid().ToString('N').Substring(0,8))
    if (Test-Path -LiteralPath $candidate) { throw 'Installation transaction path already exists.' }
    # Assign cleanup ownership only after creating our directory exclusively.
    New-Item -ItemType Directory -Path $candidate | Out-Null
    $transaction = $candidate
    $stage = Join-Path $transaction 'staged'
    $original = Join-Path $transaction 'original'
    New-Item -ItemType Directory -Path $stage, $original -Force | Out-Null
    # Complete copying before changing any existing files.
    foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $source $name) -Destination $stage -Recurse -Force }
    # Preserve Quick Stack's personal favorites and the two legacy Azu files
    # needed for one-time migration, not old shared mod settings.
    $previousConfig = Join-Path $target 'BepInEx/config'
    if (Test-Path -LiteralPath $previousConfig -PathType Container) {
        $stagedConfig = Join-Path $stage 'BepInEx/config'
        foreach ($personal in Get-ChildItem -LiteralPath $previousConfig -File -Force) {
            if ($personal.Name -match '^(QuickStackStore|AzuAutoStore|AzuExtendedPlayerInventory)_player_-?\d+\.dat$') {
                Copy-PersonalEntry $personal $stagedConfig
            }
        }
        Merge-RadioPersonalAudio (Join-Path $previousConfig 'valheimmodpack.nordicradio.cfg') (Join-Path $stagedConfig 'valheimmodpack.nordicradio.cfg')
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
    if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Game started during staging; installation stopped.' }
    $saved = @()
    $installed = @()
    try {
        # Keep originals through raw interruption as well as caught move failures.
        $retainRecovery = $true
        @("Target: $target", 'Installation is in progress or was interrupted.',
            'Close Valheim before recovery. Do not delete this directory until recovery is verified.',
            "Original replaced entries are in: $original",
            "New payload awaiting installation is in: $stage",
            'Move conflicting game entries aside, then restore remaining original/ entries to the game root.') |
            Set-Content -LiteralPath (Join-Path $transaction 'RECOVERY.txt') -Encoding UTF8
        foreach ($name in $names) {
            $path = Join-Path $target $name
            if ($null -ne (Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue)) {
                Move-Item -LiteralPath $path -Destination (Join-Path $original $name)
                $saved += $name
            }
            Move-Item -LiteralPath (Join-Path $stage $name) -Destination $path
            $installed += $name
        }
    } catch {
        $installationError = $_
        $rollbackErrors = @()
        $failed = Join-Path $transaction 'failed-install'
        try { New-Item -ItemType Directory -Path $failed -Force | Out-Null }
        catch { $rollbackErrors += $_.Exception.Message }
        foreach ($name in $installed) {
            try { Move-Item -LiteralPath (Join-Path $target $name) -Destination (Join-Path $failed $name) }
            catch { $rollbackErrors += $_.Exception.Message }
        }
        foreach ($name in $saved) {
            try {
                $restore = Join-Path $target $name
                if ($null -ne (Get-Item -LiteralPath $restore -Force -ErrorAction SilentlyContinue)) { throw "Restore destination is still occupied: $restore" }
                Move-Item -LiteralPath (Join-Path $original $name) -Destination $restore
            } catch { $rollbackErrors += $_.Exception.Message }
        }
        # A stop between a successful rename and tracking its name must never
        # make cleanup discard an unrecorded original.
        $remainingOriginals = @(Get-ChildItem -LiteralPath $original -Force)
        if ($remainingOriginals.Count -gt 0) {
            $rollbackErrors += ('Original entries still require recovery: ' + (($remainingOriginals | ForEach-Object Name) -join ', '))
        }
        if ($rollbackErrors.Count -gt 0) {
            $retainRecovery = $true
            $recovery = @("Target: $target", "Installation error: $($installationError.Exception.Message)",
                'Automatic rollback did not complete. Close Valheim before recovery.',
                "Original replaced entries remaining for recovery: $original",
                "New entries moved aside: $failed",
                'Move any conflicting game entries aside, then restore the remaining entries from original/ to the game root.',
                'Do not delete this transaction directory until you have verified recovery.',
                ('Rollback errors: ' + ($rollbackErrors -join ' | ')))
            try { $recovery | Set-Content -LiteralPath (Join-Path $transaction 'RECOVERY.txt') -Encoding UTF8 }
            catch { Write-Warning 'Could not write RECOVERY.txt; keep the transaction directory shown below.' }
            throw "Installation failed and rollback needs attention. Recovery files retained at $transaction. $($rollbackErrors -join ' | ')"
        }
        $retainRecovery = $false
        throw $installationError
    }
    $retainRecovery = $false
    Remove-InstallTransaction $transaction $target
    $transaction = $null
    Write-Host 'Installed successfully. Temporary installation files removed.'
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
} finally {
    if ($transaction -and $retainRecovery) {
        Write-Warning "Installation recovery files retained at $transaction. Keep this directory and follow RECOVERY.txt before removing it."
    }
    if ($transaction -and -not $retainRecovery) {
        try { Remove-InstallTransaction $transaction $target }
        catch { Write-Warning "Temporary installation files could not be removed: $transaction. $($_.Exception.Message)" }
    }
}
