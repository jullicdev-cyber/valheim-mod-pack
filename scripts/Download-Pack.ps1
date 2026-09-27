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
            $path = [IO.Path]::GetFullPath((Join-Path $Destination $name))
            if (-not $path.StartsWith($base, [StringComparison]::OrdinalIgnoreCase) -or -not $seen.Add($path)) { throw "Duplicate or escaped archive path: $name" }
        }
        New-Item -ItemType Directory -Path $Destination | Out-Null
        foreach ($entry in $zip.Entries) {
            $path = Join-Path $Destination $entry.FullName
            if ($entry.FullName.EndsWith('/')) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
            else {
                New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $false)
            }
        }
    } finally { $zip.Dispose() }
    return Join-Path $Destination $prefix.TrimEnd('/')
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
    New-Item -ItemType Directory -Path $job -Force | Out-Null
    $archive = Join-Path $job 'pack.zip'
    Write-Host "Downloading main at $commit ..."
    Invoke-WebRequest -UseBasicParsing -Uri "https://codeload.github.com/jullicdev-cyber/valheim-mod-pack/zip/$commit" -Headers $headers -OutFile $archive -TimeoutSec 300
    $pack = Expand-PackArchive $archive (Join-Path $job 'extracted') $commit
    foreach ($name in @('VERSION','mods.lock.json','files.sha256.json','scripts/Install-Windows.ps1')) {
        if (-not (Test-Path -LiteralPath (Join-Path $pack $name) -PathType Leaf)) { throw "Incomplete downloaded pack: $name" }
    }
    & (Join-Path $PSScriptRoot 'Verify.ps1') -PackDirectory (Join-Path $pack 'Game') -ManifestDirectory $pack | ForEach-Object { Write-Host $_ }
    $version = (Get-Content -LiteralPath (Join-Path $pack 'VERSION') -Raw).Trim()
    $lock = Get-Content -LiteralPath (Join-Path $pack 'mods.lock.json') -Raw | ConvertFrom-Json
    if ($version -notmatch '^\d+\.\d+\.\d+$' -or $version -ne $lock.packVersion) { throw 'Downloaded version metadata mismatch.' }
    @{ commit=$commit; version=$version; directory=$pack } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $updates 'latest.json') -Encoding UTF8
    Write-Host "Downloaded and verified pack $version`: $pack"
    return $pack
}
