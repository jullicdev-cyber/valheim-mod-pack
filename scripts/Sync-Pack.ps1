# In-place updates for Windows PowerShell 5.1; Python is not required.
$script:PackRepository = 'https://github.com/jullicdev-cyber/valheim-mod-pack.git'
$script:PackProtected = @('.git','.updates','.cache','dist','backups','local-settings.json','NordicRadio','ValheimModpack')
$script:PackLegacyNames = @('.gitattributes','.gitignore','CHANGELOG.md','COMPATIBILITY.md','Game','INVENTORY-DIAGNOSTIC.md','Install-Linux.sh','Install-Windows.cmd','MOD-REVIEW.md','README.md','Set-GamePath-Linux.sh','Set-GamePath-Windows.cmd','Update-Linux.sh','Update-Windows.cmd','VALIDATION.md','VERSION','audit','config','files.sha256.json','local-plugins','mods.lock.json','plugins','reference','scripts','third-party','world-settings.json')

function Assert-UnlinkedUpdatePath([string]$Path) {
    $entry = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($entry) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked update path is not supported: $Path" }
        if ($entry.PSIsContainer) {
            foreach ($child in Get-ChildItem -LiteralPath $Path -Force) { Assert-UnlinkedUpdatePath $child.FullName }
        }
    }
}

function Test-UpdatePack([string]$Pack) {
    foreach ($name in @('VERSION','mods.lock.json','files.sha256.json','scripts/Install-Windows.ps1')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Pack $name) -PathType Leaf)) { throw "Incomplete pack: $name" }
    }
    & (Join-Path $PSScriptRoot 'Verify.ps1') -PackDirectory (Join-Path $Pack 'Game') -ManifestDirectory $Pack | ForEach-Object { Write-Host $_ }
}

function Get-PackManagedNames([string]$Root, [string]$Pack) {
    $names = @(Get-ChildItem -LiteralPath $Pack -Force | Where-Object Name -ne '.git' | ForEach-Object Name)
    foreach ($name in $names) { if ($script:PackProtected -contains $name) { throw "Downloaded pack contains a reserved path: $name" } }
    $previous = Join-Path $Root '.updates/managed-names.json'
    Assert-UnlinkedUpdatePath $previous
    $old = $script:PackLegacyNames
    if (Test-Path -LiteralPath $previous) {
        $old = Get-Content -LiteralPath $previous -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($old -isnot [array]) { throw 'Invalid previous managed-file list.' }
    }
    foreach ($name in $old) {
        if ($name -isnot [string] -or -not $name -or $name -in @('.','..') -or $name -match '[\\/:]' -or $script:PackProtected -contains $name) { throw 'Invalid previous managed-file list.' }
    }
    return @{ Names=$names; Affected=@(($old + $names) | Sort-Object -Unique) }
}

function New-PackUpdateJob([string]$Root, [string]$Prefix) {
    $path = Join-Path $Root ('.updates/' + $Prefix + '-' + [guid]::NewGuid().ToString('N').Substring(0,12))
    New-Item -ItemType Directory -Path $path | Out-Null
    return $path
}

function Move-PackEntry([string]$From, [string]$To, [string]$Root) {
    # Both recursive directory moves and rollback stay in the selected pack root.
    $base = [IO.Path]::GetFullPath($Root).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    foreach ($path in @($From,$To)) {
        if (-not ([IO.Path]::GetFullPath($path)).StartsWith($base,[StringComparison]::OrdinalIgnoreCase)) { throw 'Move outside the pack directory refused.' }
    }
    Move-Item -LiteralPath $From -Destination $To -ErrorAction Stop
}

function Remove-PackUpdateJob([string]$Root, [string]$Job) {
    $base = [IO.Path]::GetFullPath((Join-Path $Root '.updates')).TrimEnd('\','/')
    $target = [IO.Path]::GetFullPath($Job)
    if ((Split-Path $target -Parent) -ne $base) { throw 'Update cleanup outside its transaction refused.' }
    Assert-UnlinkedUpdatePath $target
    Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop
}

function Set-PackFolder([string]$Root, [string]$Pack, [switch]$IncludeGit) {
    Test-UpdatePack $Pack
    $managed = Get-PackManagedNames $Root $Pack
    $names = @($managed.Names); $affected = @($managed.Affected)
    if ($IncludeGit) {
        if (Test-Path -LiteralPath (Join-Path $Root '.git')) { throw 'Refusing to replace existing Git history.' }
        $names += '.git'; $affected += '.git'
    }
    foreach ($name in $affected) { Assert-UnlinkedUpdatePath (Join-Path $Root $name) }
    foreach ($name in $names) { Assert-UnlinkedUpdatePath (Join-Path $Pack $name) }
    $manifest = Join-Path $Root '.updates/managed-names.json'
    $oldManifest = if (Test-Path -LiteralPath $manifest) { [IO.File]::ReadAllBytes($manifest) } else { $null }
    $job = New-PackUpdateJob $Root 'replace'
    $stage = Join-Path $job 'staged'; $original = Join-Path $job 'original'; $failed = Join-Path $job 'failed'
    $saved = @(); $installed = @(); $recovery = $false
    try {
        New-Item -ItemType Directory -Path $stage,$original,$failed | Out-Null
        foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $Pack $name) -Destination (Join-Path $stage $name) -Recurse -Force }
        $recovery = $true
        try {
            foreach ($name in ($affected | Sort-Object -Unique)) {
                if (Test-Path -LiteralPath (Join-Path $Root $name)) {
                    $saved += $name
                    Move-PackEntry (Join-Path $Root $name) (Join-Path $original $name) $Root
                }
                if ($names -contains $name) {
                    $installed += $name
                    Move-PackEntry (Join-Path $stage $name) (Join-Path $Root $name) $Root
                }
            }
            Test-UpdatePack $Root
            $record = @($names | Where-Object { $_ -ne '.git' } | Sort-Object)
            ConvertTo-Json -InputObject $record | Set-Content -LiteralPath $manifest -Encoding UTF8
        } catch {
            $cause = $_; $failures = @()
            foreach ($name in $installed) {
                try {
                    if (-not (Test-Path -LiteralPath (Join-Path $stage $name)) -and (Test-Path -LiteralPath (Join-Path $Root $name))) { Move-PackEntry (Join-Path $Root $name) (Join-Path $failed $name) $Root }
                } catch { $failures += $_.Exception.Message }
            }
            foreach ($name in $saved) {
                try {
                    if (Test-Path -LiteralPath (Join-Path $original $name)) {
                        if (Test-Path -LiteralPath (Join-Path $Root $name)) { throw "Rollback destination is occupied: $name" }
                        Move-PackEntry (Join-Path $original $name) (Join-Path $Root $name) $Root
                    }
                } catch { $failures += $_.Exception.Message }
            }
            try {
                if ($null -eq $oldManifest) {
                    if (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest -ErrorAction Stop }
                } else { [IO.File]::WriteAllBytes($manifest, [byte[]]$oldManifest) }
            } catch { $failures += $_.Exception.Message }
            if ($failures.Count) {
                $recovery = $true
                @('Pack rollback failed. Previous replaced entries are in original/.', 'Close the updater, move conflicting new entries aside, and restore original/ to the pack root.', 'Personal root folders were not replaced.', $failures) | Set-Content -LiteralPath (Join-Path $job 'RECOVERY.txt') -Encoding UTF8
                throw "Pack rollback failed; recovery files retained at $job. Initial failure: $($cause.Exception.Message)"
            }
            $recovery = $false
            throw $cause
        }
        $recovery = $false
        Write-Host 'Pack folder updated.'
        return $Root
    } finally {
        if ($recovery) {
            $instructions = Join-Path $job 'RECOVERY.txt'
            if (-not (Test-Path -LiteralPath $instructions)) { [IO.File]::WriteAllText($instructions, 'Pack transaction was interrupted. Previous replaced entries are in original/. Close the updater and restore original/ after moving conflicting new entries aside.') }
        } else { Remove-PackUpdateJob $Root $job }
    }
}

function Invoke-PackGit([string]$Git, [string]$Root, [string[]]$Arguments) {
    $oldPrompt = $env:GIT_TERMINAL_PROMPT; $oldInteractive = $env:GCM_INTERACTIVE
    $oldEncoding = [Console]::OutputEncoding
    $oldPreference = $ErrorActionPreference
    try {
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $env:GIT_TERMINAL_PROMPT='0'; $env:GCM_INTERACTIVE='Never'
        # Native stderr is progress text, not a PowerShell terminating error.
        $ErrorActionPreference = 'Continue'
        $output = @(& $Git -C $Root -c core.quotepath=false -c merge.autoStash=false -c rebase.autoStash=false @Arguments 2>&1)
        $code = $LASTEXITCODE
        $ErrorActionPreference = $oldPreference
        if ($code -ne 0) { throw "Git $($Arguments[0]) failed: $($output -join [Environment]::NewLine)" }
        # stdout is used by queries. Progress on stderr must not pollute returned paths.
        return (($output | Where-Object { $_ -isnot [Management.Automation.ErrorRecord] }) -join "`n").Trim()
    } finally {
        [Console]::OutputEncoding = $oldEncoding
        $ErrorActionPreference = $oldPreference
        $env:GIT_TERMINAL_PROMPT=$oldPrompt; $env:GCM_INTERACTIVE=$oldInteractive
    }
}

function Get-NormalizedPackOrigin([string]$Url) {
    $value = $Url.Trim().TrimEnd('/') -replace '\.git$',''
    if ($value -match '^(https://github\.com/|ssh://git@github\.com/|git@github\.com:)(.+)$') { return 'github:' + $Matches[2].ToLowerInvariant() }
    return $value
}

function Sync-PackFolder([string]$Root, [string]$Git = 'auto', [string]$Repository = $script:PackRepository) {
    $Root = [IO.Path]::GetFullPath($Root)
    $updates = Join-Path $Root '.updates'
    $entry = Get-Item -LiteralPath $updates -Force -ErrorAction SilentlyContinue
    if ($entry -and ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Update directory must not be a link.' }
    New-Item -ItemType Directory -Path $updates -Force | Out-Null
    $lock = Join-Path $updates 'update.lock'
    try { New-Item -ItemType Directory -Path $lock -ErrorAction Stop | Out-Null }
    catch { throw 'Another update is running. If interrupted, remove .updates/update.lock after closing it.' }
    try {
        if ($Git -eq 'auto') {
            $command = Get-Command git.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            $Git = if ($command) { $command.Source } else { '' }
        }
        if (-not $Git) {
            if (Test-Path -LiteralPath (Join-Path $Root '.git')) { throw 'This folder has Git history but Git is unavailable. Install Git to update it without desynchronizing its history.' }
            Write-Host 'Git unavailable: updating this folder from a verified ZIP.'
            return Set-PackFolder $Root (Get-LatestPack $Root)
        }
        if (-not (Test-Path -LiteralPath (Join-Path $Root '.git'))) {
            Write-Host 'ZIP folder detected: connecting this folder to the GitHub repository.'
            $job = New-PackUpdateJob $Root 'clone'
            $pack = Join-Path $job 'pack'
            try {
                Invoke-PackGit $Git $Root @('clone','--branch','main','--single-branch','--depth','1',$Repository,$pack) | Out-Null
                Test-UpdatePack $pack
                return Set-PackFolder $Root $pack -IncludeGit
            } finally { Remove-PackUpdateJob $Root $job }
        }
        $top = Invoke-PackGit $Git $Root @('rev-parse','--show-toplevel')
        if ([IO.Path]::GetFullPath($top) -ne $Root) { throw 'Run the updater from the root of the pack repository.' }
        if (Invoke-PackGit $Git $Root @('status','--porcelain','--untracked-files=no')) { throw 'Tracked files have local changes. Commit or save them separately before updating; no files were overwritten.' }
        if ((Invoke-PackGit $Git $Root @('branch','--show-current')) -ne 'main') { throw 'Switch this repository to main before updating.' }
        $remotes = (Invoke-PackGit $Git $Root @('remote')) -split "`n"
        if ($remotes -notcontains 'origin') { Invoke-PackGit $Git $Root @('remote','add','origin',$Repository) | Out-Null }
        $origin = Invoke-PackGit $Git $Root @('remote','get-url','origin')
        if ((Get-NormalizedPackOrigin $origin) -ne (Get-NormalizedPackOrigin $Repository)) { throw "origin points to another repository; it was not changed: $origin" }
        Invoke-PackGit $Git $Root @('fetch','origin','main') | Out-Null
        $commit = Invoke-PackGit $Git $Root @('rev-parse','FETCH_HEAD')
        $old = Invoke-PackGit $Git $Root @('rev-parse','HEAD')
        Invoke-PackGit $Git $Root @('merge-base','--is-ancestor',$old,$commit) | Out-Null
        $job = New-PackUpdateJob $Root 'pull'; $archive = Join-Path $job 'pack.zip'
        $recovery = $false
        try {
            Invoke-PackGit $Git $Root @('archive','--format=zip',('--prefix=valheim-mod-pack-'+$commit+'/'),'-o',$archive,$commit) | Out-Null
            $pack = Expand-PackArchive $archive (Join-Path $job 'pack') $commit
            Test-UpdatePack $pack
            $managed = Get-PackManagedNames $Root $pack
            foreach ($name in $managed.Affected) { Assert-UnlinkedUpdatePath (Join-Path $Root $name) }
            $recovery = $true
            try {
                # Git itself retains the old commit, so no full filesystem backup is needed.
                Invoke-PackGit $Git $Root @('pull','--ff-only','--no-rebase','origin',$commit) | Out-Null
                if ((Invoke-PackGit $Git $Root @('rev-parse','HEAD')) -ne $commit) { throw 'Git did not advance to the verified commit; installation stopped.' }
                Test-UpdatePack $Root
            } catch {
                $cause = $_
                try { Invoke-PackGit $Git $Root @('reset','--hard',$old) | Out-Null }
                catch {
                    $recovery = $true
                    @("Git rollback failed. Previous commit: $old", 'Save any new local changes before restoring that commit. Personal root folders were not replaced.', $_.Exception.Message) | Set-Content -LiteralPath (Join-Path $job 'RECOVERY.txt') -Encoding UTF8
                    throw "Git rollback failed; recovery instructions retained at $job. Initial failure: $($cause.Exception.Message)"
                }
                $recovery = $false
                throw $cause
            }
            $recovery = $false
            Write-Host 'Git pull completed.'
            return $Root
        } finally {
            if ($recovery) {
                $instructions = Join-Path $job 'RECOVERY.txt'
                if (-not (Test-Path -LiteralPath $instructions)) { [IO.File]::WriteAllText($instructions, "Git update was interrupted. Previous commit: $old. Close the updater and save any new local changes before restoring that commit.") }
            } else { Remove-PackUpdateJob $Root $job }
        }
    } finally { Remove-Item -LiteralPath $lock -ErrorAction Stop }
}
