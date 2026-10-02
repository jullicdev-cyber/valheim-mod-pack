[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$folder=Join-Path $root '.cache/map-action-tests'
New-Item -ItemType Directory -Force $folder | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$exe=Join-Path $folder 'Tests.exe'
$sources=@('PinActionController.cs','DeathPinController.cs','Confirmation.cs','RemovalDialog.cs','MapActionTestDoubles.cs','MapActionTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /codepage:65001 /target:exe "/out:$exe" @sources
if ($LASTEXITCODE -ne 0) { throw 'Action/death controller regression build failed' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Action/death controller regression failed' }
Add-Type -Path (Join-Path $root 'Game/BepInEx/core/Mono.Cecil.dll')
$asm=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDirectory 'valheim_Data/Managed/assembly_valheim.dll'))
try {
    $map=$asm.MainModule.GetType('Minimap')
    $constant=$map.Fields | Where-Object Name -eq 'm_enableLastDeathAutoPin'
    if (-not $constant.HasConstant -or $constant.Constant -ne $false) { throw 'Native last-death autopin behavior changed' }
    $enum=$map.NestedTypes | Where-Object Name -eq 'PinType'
    $death=$enum.Fields | Where-Object Name -eq 'Death'
    if ($death.Constant -ne 4) { throw 'Native death pin type changed' }
    $profile=$map.Methods | Where-Object Name -eq 'UpdateProfilePins'
    if ($profile.Body.Instructions | Where-Object { "$($_.Operand)" -match 'GetDeathPoint|SetDeathPoint' }) { throw 'Native death pin may now regenerate from profile; review bulk removal' }
    $remove=$map.Methods | Where-Object { $_.Name -eq 'RemovePin' -and $_.Parameters.Count -eq 1 -and $_.Parameters[0].ParameterType.FullName -eq 'Minimap/PinData' }
    if (-not ($remove.Body.Instructions | Where-Object { "$($_.Operand)" -match 'List.*::Remove\(' })) { throw 'Native pin removal no longer removes stored list entry' }
    if ($remove.Body.Instructions | Where-Object { "$($_.Operand)" -match 'PlayerProfile|TombStone|Inventory|ZNetView|ZDOMan|InvokeRPC' }) { throw 'Native pin removal gained world/inventory effects' }
    $mapData=$map.Methods | Where-Object Name -eq 'GetMapData'
    if (-not ($mapData.Body.Instructions | Where-Object { "$($_.Operand)" -eq 'System.Boolean Minimap/PinData::m_save' })) { throw 'Saved pin persistence contract changed' }
    Write-Output 'OK: native death pin type, disabled profile auto-pin, list persistence and removal isolation verified.'
} finally { $asm.Dispose() }
