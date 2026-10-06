$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out = Join-Path $root '.cache/worldcharacters-administration-input'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$runner = Join-Path $out 'AdministrationInput.exe'
$argsList = @('/nologo', '/target:exe', '/codepage:65001', '/nowarn:0649', ('/out:' + $runner))
$argsList += @('AdministrationInput.cs', 'ShortcutCapture.cs', 'GameplayInputCache.cs', 'AdministrationInputTestDoubles.cs', 'AdministrationInputTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') @argsList
if ($LASTEXITCODE -ne 0) { throw 'World Characters administration input test compilation failed.' }
& $runner
if ($LASTEXITCODE -ne 0) { throw 'World Characters administration input regression failed.' }
