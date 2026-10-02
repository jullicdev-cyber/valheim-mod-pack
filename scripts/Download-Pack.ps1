function Expand-PackArchive([string]$Archive, [string]$Destination, [string]$Commit) {
    if ($Commit -notmatch '^[0-9a-f]{40}$') { throw 'Invalid GitHub commit.' }
    if (Test-Path -LiteralPath $Destination) { throw 'Extraction directory must be new.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $prefix = 'valheim-mod-pack-' + $Commit + '/'
    $base = [IO.Path]::GetFullPath($Destination).TrimEnd('\') + '\'
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        # Validate every entry before extracting any of them.
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            if (-not $name.StartsWith($prefix, [StringComparison]::Ordinal) -or $name -match '[\\:<>|?*]' -or $name -match '(^|/)\.{1,2}(/|$)') { throw "Unsafe archive entry: $name" }
            if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw "Archive link rejected: $name" }
            $relative = $name.Substring($prefix.Length)
            if (-not $relative) { continue }
            $path = [IO.Path]::GetFullPath((Join-Path $Destination $relative))
            if (-not $path.StartsWith($base, [StringComparison]::OrdinalIgnoreCase) -or -not $seen.Add($path)) { throw "Duplicate or escaped archive path: $name" }
        }
        New-Item -ItemType Directory -Path $Destination | Out-Null
        foreach ($entry in $zip.Entries) {
            # Omit GitHub's 57-character wrapper directory to stay within Windows
            # PowerShell 5.1 path limits even with long third-party plugin names.
            $relative = $entry.FullName.Substring($prefix.Length)
            if (-not $relative) { continue }
            $path = Join-Path $Destination $relative
            if ($entry.FullName.EndsWith('/')) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
            else {
                New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $false)
            }
        }
    } finally { $zip.Dispose() }
    return [IO.Path]::GetFullPath($Destination)
}

function Save-PackArchive([string]$Uri, [string]$OutFile) {
    $curl = Get-Command curl.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($curl) {
        # curl is bundled with current Windows and streams large archives without
        # Windows PowerShell's slow/buffering Invoke-WebRequest download path.
        & $curl.Source --fail --location --silent --show-error --connect-timeout 30 --max-time 300 --output $OutFile $Uri
        if ($LASTEXITCODE -ne 0) { throw "Archive download failed (curl exit $LASTEXITCODE)." }
    } else {
        $previousProgress = $ProgressPreference
        try {
            $ProgressPreference = 'SilentlyContinue'
            Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $OutFile -TimeoutSec 300
        } finally { $ProgressPreference = $previousProgress }
    }
}

function Get-LatestPack([string]$PackRoot) {
    $updates = Join-Path $PackRoot '.updates'
    if ((Test-Path -LiteralPath $updates) -and ((Get-Item -LiteralPath $updates).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Update directory must not be a link.' }
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $headers = @{ 'User-Agent'='ValheimModPack-Updater'; 'Accept'='application/vnd.github+json' }
    $head = Invoke-RestMethod -Uri 'https://api.github.com/repos/jullicdev-cyber/valheim-mod-pack/commits/main' -Headers $headers -TimeoutSec 60
    $commit = [string]$head.sha
    if ($commit -notmatch '^[0-9a-f]{40}$') { throw 'GitHub returned an invalid commit.' }
    $job = Join-Path $updates ($commit.Substring(0,12) + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
    New-Item -ItemType Directory -Path $updates -Force | Out-Null
    # Own cleanup only after exclusive creation; never reuse an existing job.
    New-Item -ItemType Directory -Path $job | Out-Null
    if ($null -eq $script:PackDownloadJobs) { $script:PackDownloadJobs = [Collections.Generic.List[string]]::new() }
    $script:PackDownloadJobs.Add($job)
    try {
    $archive = Join-Path $job 'pack.zip'
    Write-Host "Downloading main at $commit ..."
    Save-PackArchive "https://codeload.github.com/jullicdev-cyber/valheim-mod-pack/zip/$commit" $archive
    $pack = Expand-PackArchive $archive (Join-Path $job 'pack') $commit
    foreach ($name in @('VERSION','mods.lock.json','files.sha256.json','scripts/Install-Windows.ps1')) {
        if (-not (Test-Path -LiteralPath (Join-Path $pack $name) -PathType Leaf)) { throw "Incomplete downloaded pack: $name" }
    }
    & (Join-Path $PSScriptRoot 'Verify.ps1') -PackDirectory (Join-Path $pack 'Game') -ManifestDirectory $pack | ForEach-Object { Write-Host $_ }
    $version = (Get-Content -LiteralPath (Join-Path $pack 'VERSION') -Raw).Trim()
    $lock = Get-Content -LiteralPath (Join-Path $pack 'mods.lock.json') -Raw | ConvertFrom-Json
    if ($version -notmatch '^\d+\.\d+\.\d+$' -or $version -ne $lock.packVersion) { throw 'Downloaded version metadata mismatch.' }
    $latest = Join-Path $updates 'latest.json'
    if ((Test-Path -LiteralPath $latest) -and ((Get-Item -LiteralPath $latest -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked download record refused.' }
    $recordPath = Join-Path $job 'latest.json'
    @{ commit=$commit; version=$version; directory=$pack } | ConvertTo-Json | Set-Content -LiteralPath $recordPath -Encoding UTF8
    if (Test-Path -LiteralPath $latest) { [IO.File]::Replace($recordPath, $latest, [NullString]::Value) }
    else { [IO.File]::Move($recordPath, $latest) }
    Write-Host "Downloaded and verified pack $version`: $pack"
    return $pack
    } catch {
        $downloadError = $_
        try {
            $latest = Join-Path $updates 'latest.json'
            if (Test-Path -LiteralPath $latest) {
                if ((Get-Item -LiteralPath $latest -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked download record refused.' }
                $record = Get-Content -LiteralPath $latest -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($record.directory -eq (Join-Path $job 'pack')) { Remove-Item -LiteralPath $latest -Force }
            }
        } catch { Write-Warning "Could not clean download bookkeeping: $_" }
        try { Remove-PackDownloadJob $PackRoot $job }
        catch { Write-Warning "Could not remove temporary download $job`: $_" }
        throw $downloadError
    }
}

function Remove-PackDownloadJob([string]$PackRoot, [string]$Job) {
    $updates = [IO.Path]::GetFullPath((Join-Path $PackRoot '.updates')).TrimEnd('\','/')
    $path = [IO.Path]::GetFullPath($Job).TrimEnd('\','/')
    if ([IO.Path]::GetDirectoryName($path) -ne $updates -or [IO.Path]::GetFileName($path) -notmatch '^[0-9a-f]{12}-[0-9a-f]{8}$') { throw 'Download cleanup outside its temporary job refused.' }
    foreach ($checkPath in @($updates,$path)) {
        if ((Test-Path -LiteralPath $checkPath) -and ((Get-Item -LiteralPath $checkPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked download cleanup path refused.' }
    }
    if (-not (Test-Path -LiteralPath $path)) { return }
    function Assert-DownloadEntry([string]$EntryPath) {
        $entry = Get-Item -LiteralPath $EntryPath -Force
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked download entry refused: $EntryPath" }
        if ($entry.PSIsContainer) { foreach ($child in Get-ChildItem -LiteralPath $EntryPath -Force) { Assert-DownloadEntry $child.FullName } }
    }
    Assert-DownloadEntry $path
    Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
}
