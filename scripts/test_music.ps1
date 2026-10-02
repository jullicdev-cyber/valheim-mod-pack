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
Assert (-not (Test-Path -LiteralPath (Join-Path $GameDirectory 'NordicRadio/Music-backups'))) 'Automatic backup created'
Assert (Test-Path -LiteralPath (Join-Path $GameDirectory 'NordicRadio/last-music-install.txt')) 'Install record missing'
Assert (@(Get-ChildItem -LiteralPath (Join-Path $GameDirectory 'NordicRadio') -Force -Filter '.music-update-*').Count -eq 0) 'Successful transaction leaked'
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
Assert (@(Get-ChildItem -LiteralPath (Join-Path $GameDirectory 'NordicRadio') -Force -Filter '.music-update-*').Count -eq 0) 'Rejected transaction leaked'
$script:GoodMusicZip = $Fixture
function Save-MusicArchive { param($Url,$Destination); Copy-Item -LiteralPath $script:GoodMusicZip -Destination $Destination }
$script:OriginalMusicMove = (Get-Command Move-MusicEntry).ScriptBlock
$script:MusicFailureMode = 'record'
function Move-MusicEntry([string]$From, [string]$To, [string]$Radio) {
    if ((Split-Path $From -Leaf) -eq 'install.txt') { throw 'Injected install record failure.' }
    if ($script:MusicFailureMode -eq 'rollback' -and (Split-Path $From -Leaf) -eq 'original') { throw 'Injected rollback failure.' }
    & $script:OriginalMusicMove $From $To $Radio
}
$record = Join-Path $GameDirectory 'NordicRadio/last-music-install.txt'
$recordHash = Get-MusicHash $record
$rejected = $false
try { Install-RadioMusic $GameDirectory 'https://example.test/new.zip' } catch { $rejected = $true }
Assert $rejected 'Record failure must fail installation'
Assert ((Get-MusicHash $record) -eq $recordHash) 'Previous install record lost'
Assert ((Get-MusicHash $active) -eq $activeHash) 'Successful rollback changed music'
Assert (@(Get-ChildItem -LiteralPath (Join-Path $GameDirectory 'NordicRadio') -Force -Filter '.music-update-*').Count -eq 0) 'Rollback transaction leaked'
$oldBackup = Join-Path $GameDirectory 'NordicRadio/Music-backups/user.txt'
New-Item -ItemType Directory -Path (Split-Path $oldBackup -Parent) -Force | Out-Null
'user backup' | Set-Content -LiteralPath $oldBackup
$script:MusicFailureMode = 'rollback'
$rejected = $false
try { Install-RadioMusic $GameDirectory 'https://example.test/new.zip' } catch { $rejected = $_.Exception.Message -like '*recovery files retained*' }
Assert $rejected 'Rollback failure must report recovery'
$jobs = @(Get-ChildItem -LiteralPath (Join-Path $GameDirectory 'NordicRadio') -Force -Filter '.music-update-*')
Assert ($jobs.Count -eq 1) 'Failed rollback recovery job missing'
Assert (Test-Path -LiteralPath (Join-Path $jobs[0].FullName 'RECOVERY.txt')) 'Recovery instructions missing'
Assert (Test-Path -LiteralPath (Join-Path $jobs[0].FullName 'original/replace.mp3')) 'Recovery original missing'
Assert ((Get-Content -LiteralPath $oldBackup -Raw).Trim() -eq 'user backup') 'Old user backup changed'
Assert (-not (Test-Path -LiteralPath (Join-Path $GameDirectory 'NordicRadio/.music-update.lock'))) 'Recovery lock leaked'
Write-Host 'Windows music download, cleanup, rollback, recovery, and HTML rejection: PASS'
