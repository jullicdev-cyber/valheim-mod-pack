param([string]$GameDirectory, [string]$Fixture, [string]$BaseUrl)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Music.ps1')
function Assert($condition, $message) { if (-not $condition) { throw $message } }
# This test changes only a temporary fixture, never the real game.
function Get-Process { param($Name,$ErrorAction); return @() }
$download = Join-Path (Split-Path $Fixture -Parent) 'windows-download.zip'
Save-MusicArchive ($BaseUrl + '/fixture.zip') $download
Assert ((Get-MusicHash $download) -eq (Get-MusicHash $Fixture)) 'Downloaded ZIP mismatch'
$rejected = $false
try { Save-MusicArchive ($BaseUrl + '/error.html') $download } catch { $rejected = $true }
Assert $rejected 'HTML must be rejected'
Install-RadioMusic $GameDirectory ($BaseUrl + '/fixture.zip')
Assert (Test-Path -LiteralPath (Join-Path $GameDirectory 'NordicRadio/Music/new.mp3')) 'Track not installed'
Assert (@(Get-ChildItem -LiteralPath (Join-Path $GameDirectory 'NordicRadio/Music') -File -Filter '*.mp3').Count -eq 3) 'Personal track lost'
Assert (@(Get-ChildItem -LiteralPath (Join-Path $GameDirectory 'NordicRadio/Music-backups') -Recurse -Filter replace.mp3).Count -ge 1) 'Backup missing'
Assert ((Get-MusicDownloadUrl 'https://drive.google.com/file/d/abc_123/view') -like '*id=abc_123*') 'Drive conversion failed'
# A rejected ZIP must leave the active library unchanged even after staging.
$active = Join-Path $GameDirectory 'NordicRadio/Music/new.mp3'
$activeHash = Get-MusicHash $active
$script:BadMusicZip = Join-Path (Split-Path $Fixture -Parent) 'unsafe-music.zip'
$zip = [IO.Compression.ZipFile]::Open($script:BadMusicZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in @('okay.mp3','../escape.mp3')) {
        $entry = $zip.CreateEntry($name)
        $stream = $entry.Open()
        try { $bytes = [Text.Encoding]::ASCII.GetBytes('test audio'); $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
    }
} finally { $zip.Dispose() }
function Save-MusicArchive { param($Url,$Destination); Copy-Item -LiteralPath $script:BadMusicZip -Destination $Destination }
$rejected = $false
try { Install-RadioMusic $GameDirectory 'https://example.test/unsafe.zip' } catch { $rejected = $true }
Assert $rejected 'Unsafe ZIP must be rejected'
Assert ((Get-MusicHash $active) -eq $activeHash) 'Failed install changed active music'
Assert (-not (Test-Path -LiteralPath (Join-Path $GameDirectory 'NordicRadio/.music-update.lock'))) 'Update lock leaked'
Write-Host 'Windows music download, install, backup, and HTML rejection: PASS'
