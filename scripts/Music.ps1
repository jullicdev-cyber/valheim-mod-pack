# Shared by the installer and standalone music updater. Windows PowerShell 5.1.
function Get-MusicDownloadUrl([string]$Url) {
    $uri = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -notin @('http','https') -or $uri.UserInfo) { throw 'Specify an HTTP(S) ZIP URL or public Google Drive file link.' }
    if ($uri.Host -in @('drive.google.com','drive.usercontent.google.com')) {
        $id = $null
        if ($uri.AbsolutePath -match '/file/d/([\w-]+)') { $id = $Matches[1] }
        elseif ($uri.Query -match '(?:[?&])id=([\w-]+)(?:&|$)') { $id = $Matches[1] }
        if (-not $id) { throw 'Google Drive link must identify a file, not a folder.' }
        return "https://drive.usercontent.google.com/download?id=$id&export=download&confirm=t"
    }
    return $Url
}

function Save-MusicArchive([string]$Url, [string]$Destination) {
    $ProgressPreference = 'SilentlyContinue'
    $url = Get-MusicDownloadUrl $Url
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $cookies = New-Object Net.CookieContainer
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        Write-Host 'Downloading music ZIP...'
        # Stream directly to disk: PS 5.1's web cmdlet can be slow on large files.
        $request = [Net.HttpWebRequest]::Create($url)
        $request.CookieContainer = $cookies
        $request.Timeout = 120000
        $request.ReadWriteTimeout = 120000
        $request.UserAgent = 'ValheimModPack-Music/1.0'
        $response = $request.GetResponse()
        try {
            if ($response.ContentLength -gt 2GB) { throw 'Music ZIP exceeds 2 GiB.' }
            $responseUri = $response.ResponseUri
            $inputStream = $response.GetResponseStream()
            try {
                $outputStream = [IO.File]::Create($Destination)
                try {
                    $buffer = New-Object byte[] 65536
                    [long]$received = 0
                    while (($read = $inputStream.Read($buffer,0,$buffer.Length)) -gt 0) {
                        $received += $read
                        if ($received -gt 2GB) { throw 'Music ZIP exceeds 2 GiB.' }
                        $outputStream.Write($buffer,0,$read)
                    }
                } finally { $outputStream.Dispose() }
            } finally { $inputStream.Dispose() }
        } finally { $response.Dispose() }
        $file = Get-Item -LiteralPath $Destination
        if ($file.Length -gt 2GB) { throw 'Music ZIP exceeds 2 GiB.' }
        $stream = [IO.File]::OpenRead($Destination)
        try { $header = New-Object byte[] 4; $read = $stream.Read($header,0,4) } finally { $stream.Dispose() }
        if ($read -eq 4 -and [BitConverter]::ToString($header) -eq '50-4B-03-04') { return }
        if ($file.Length -gt 1MB) { throw 'URL returned no ZIP.' }
        $html = [IO.File]::ReadAllText($Destination)
        $form = [regex]::Match($html, '(?is)<form\b[^>]*\bid=["'']download-form["''][^>]*>.*?</form>')
        $action = [regex]::Match($form.Value, '(?is)\baction=["'']([^"'']+)["'']')
        if ($attempt -eq 0 -and $form.Success -and $action.Success) {
            $next = [Uri]::new($responseUri, [Net.WebUtility]::HtmlDecode($action.Groups[1].Value))
            if ($next.Scheme -ne 'https' -or $next.Host -notin @('drive.google.com','drive.usercontent.google.com')) { throw 'Unexpected download confirmation URL.' }
            $fields = @()
            foreach ($input in [regex]::Matches($form.Value, '(?is)<input\b[^>]*>')) {
                $name = [regex]::Match($input.Value, '\bname=["'']([^"'']+)["'']')
                $value = [regex]::Match($input.Value, '\bvalue=["'']([^"'']*)["'']')
                if ($name.Success) { $fields += [Uri]::EscapeDataString([Net.WebUtility]::HtmlDecode($name.Groups[1].Value)) + '=' + [Uri]::EscapeDataString([Net.WebUtility]::HtmlDecode($value.Groups[1].Value)) }
            }
            $separator = '?'; if ($next.Query) { $separator = '&' }
            $url = $next.AbsoluteUri + $separator + ($fields -join '&')
            continue
        }
        throw 'URL returned no ZIP. Check public access and Google Drive download quota.'
    }
}

function Get-MusicHash([string]$Path) {
    $hash = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
    finally { $stream.Dispose(); $hash.Dispose() }
}

function Assert-MusicPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Music path must not be a link: $current" }
        }
        $current = Split-Path $current -Parent
    }
}

function Copy-MusicTree([string]$Source, [string]$Destination) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $Source -Force) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Music entry must not be a link: $($entry.FullName)" }
        $path = Join-Path $Destination $entry.Name
        if ($entry.PSIsContainer) { Copy-MusicTree $entry.FullName $path }
        else { Copy-Item -LiteralPath $entry.FullName -Destination $path }
    }
}

function Expand-MusicArchive([string]$Archive, [string]$Stage) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        if ($zip.Entries.Count -gt 10000) { throw 'Too many ZIP entries.' }
        $names = @{}
        [long]$total = 0
        $count = 0
        foreach ($entry in $zip.Entries) {
            $raw = $entry.FullName.Replace('\','/')
            $parts = $raw.Split('/')
            if ($raw.StartsWith('/') -or $parts -contains '..' -or $raw.Contains(':') -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw "Unsafe ZIP entry: $raw" }
            $total += $entry.Length
            if ($total -gt 4GB) { throw 'Expanded ZIP exceeds 4 GiB.' }
            $name = $parts[-1]
            if (-not $name -or [IO.Path]::GetExtension($name) -ine '.mp3' -or $parts -contains '__MACOSX' -or $name.StartsWith('._')) { continue }
            if ($name -match '[<>"|?*\x00-\x1f]' -or $name.EndsWith(' ') -or $name.EndsWith('.') -or $name -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\.') { throw "Unsupported MP3 filename: $name" }
            if ($names.ContainsKey($name)) { throw "Duplicate MP3 basename in ZIP: $name" }
            if ($entry.Length -lt 4 -or $entry.Length -gt 64MB) { throw "Unsupported MP3 size: $name" }
            $names[$name] = $true
            $destination = Join-Path $Stage $name
            if (Test-Path -LiteralPath $destination -PathType Container) { throw "MP3 destination is a directory: $name" }
            $inputStream = $entry.Open()
            try {
                $outputStream = [IO.File]::Create($destination)
                try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
            } finally { $inputStream.Dispose() }
            $count++
        }
        if ($count -eq 0) { throw 'ZIP contains no MP3 files.' }
        if (@(Get-ChildItem -LiteralPath $Stage -File | Where-Object Extension -ieq '.mp3').Count -gt 256) { throw 'NordicRadio supports at most 256 tracks. Remove excess tracks first.' }
        return $count
    } finally { $zip.Dispose() }
}

function Install-RadioMusic([string]$GameDirectory, [string]$Url) {
    $radio = Join-Path $GameDirectory 'NordicRadio'
    Assert-MusicPath $radio
    if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Close Valheim before updating music.' }
    New-Item -ItemType Directory -Path $radio -Force | Out-Null
    $lock = Join-Path $radio '.music-update.lock'
    # FileMode.CreateNew provides atomic exclusion for concurrent installers.
    try { $lockStream = [IO.File]::Open($lock, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None) }
    catch { throw 'Another music update is running. Remove NordicRadio/.music-update.lock only if the previous update was interrupted.' }
    try { Invoke-RadioMusicInstall $GameDirectory $Url }
    finally { $lockStream.Dispose(); Remove-Item -LiteralPath $lock }
}

function Invoke-RadioMusicInstall([string]$GameDirectory, [string]$Url) {
    $radio = Join-Path $GameDirectory 'NordicRadio'
    $music = Join-Path $radio 'Music'
    $record = Join-Path $radio 'last-music-install.txt'
    foreach ($path in @($radio,$music,$record)) { Assert-MusicPath $path }
    if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Close Valheim before updating music.' }
    $job = Join-Path $radio ('.music-update-' + [guid]::NewGuid().ToString('N'))
    $stage = Join-Path $job 'staged'
    $original = Join-Path $job 'original'
    $archive = Join-Path $job 'music.zip'
    $oldRecord = Join-Path $job 'original-install.txt'
    $preparedRecord = Join-Path $job 'install.txt'
    $recovery = $false
    try {
        New-Item -ItemType Directory -Path $stage -Force | Out-Null
        Save-MusicArchive $Url $archive
        if (Test-Path -LiteralPath $music) { Copy-MusicTree $music $stage }
        $count = Expand-MusicArchive $archive $stage
        @("Source: $Url", "ZIP SHA256: $(Get-MusicHash $archive)", "Tracks: $count") | Set-Content -LiteralPath $preparedRecord -Encoding UTF8
        if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Game started during download; music installation stopped.' }
        foreach ($path in @($radio,$music,$record)) { Assert-MusicPath $path }
        $recovery = $true
        try {
            if (Test-Path -LiteralPath $music) { Move-MusicEntry $music $original $radio }
            if (Test-Path -LiteralPath $record) { Move-MusicEntry $record $oldRecord $radio }
            Move-MusicEntry $stage $music $radio
            Move-MusicEntry $preparedRecord $record $radio
        } catch {
            $cause = $_; $failures = @()
            if (-not (Test-Path -LiteralPath $preparedRecord) -and (Test-Path -LiteralPath $record)) {
                try { Move-MusicEntry $record (Join-Path $job 'failed-install.txt') $radio } catch { $failures += $_.Exception.Message }
            }
            if (-not (Test-Path -LiteralPath $stage) -and (Test-Path -LiteralPath $music)) {
                try { Move-MusicEntry $music (Join-Path $job 'failed') $radio } catch { $failures += $_.Exception.Message }
            }
            foreach ($restore in @(@($original,$music),@($oldRecord,$record))) {
                if (Test-Path -LiteralPath $restore[0]) {
                    try {
                        if (Test-Path -LiteralPath $restore[1]) { throw "Rollback destination is occupied: $($restore[1])" }
                        Move-MusicEntry $restore[0] $restore[1] $radio
                    } catch { $failures += $_.Exception.Message }
                }
            }
            if ($failures.Count) {
                $recovery = $true
                @('Music rollback failed. Original music is in original/; the previous install record is original-install.txt when present.', 'Close the updater, move conflicting destinations aside, and restore these entries to NordicRadio.', $failures) | Set-Content -LiteralPath (Join-Path $job 'RECOVERY.txt') -Encoding UTF8
                throw "Music rollback failed; recovery files retained at $job. Initial failure: $($cause.Exception.Message)"
            }
            $recovery = $false
            throw $cause
        }
        $recovery = $false
        Write-Host "Music installed: $count tracks. Folder: $music"
    } finally {
        if ($recovery) {
            $instructions = Join-Path $job 'RECOVERY.txt'
            if (-not (Test-Path -LiteralPath $instructions)) { [IO.File]::WriteAllText($instructions, 'Music transaction was interrupted. Original music is in original/ and the previous install record is original-install.txt when present. Close the updater before restoring these entries to NordicRadio.') }
        } else { Remove-MusicJob $radio $job }
    }
}

function Move-MusicEntry([string]$From, [string]$To, [string]$Radio) {
    $base = [IO.Path]::GetFullPath($Radio).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    foreach ($path in @($From,$To)) {
        if (-not ([IO.Path]::GetFullPath($path)).StartsWith($base,[StringComparison]::OrdinalIgnoreCase)) { throw 'Music move outside NordicRadio refused.' }
    }
    Move-Item -LiteralPath $From -Destination $To -ErrorAction Stop
}

function Remove-MusicJob([string]$Radio, [string]$Job) {
    $base = [IO.Path]::GetFullPath($Radio).TrimEnd('\','/')
    $target = [IO.Path]::GetFullPath($Job)
    if ((Split-Path $target -Parent) -ne $base -or (Split-Path $target -Leaf) -notlike '.music-update-*') { throw 'Music cleanup outside its transaction refused.' }
    Assert-MusicPath $target
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop }
}
