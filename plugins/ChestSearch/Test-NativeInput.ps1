[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory, [Parameter(Mandatory=$true)][string]$PluginAssembly)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Add-Type -Path (Join-Path $root 'Game/BepInEx/core/Mono.Cecil.dll')
$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
$utils = [Mono.Cecil.ModuleDefinition]::ReadModule((Join-Path $managed 'assembly_utils.dll'))
$game = [Mono.Cecil.ModuleDefinition]::ReadModule((Join-Path $managed 'assembly_valheim.dll'))
$checks = 0
function Assert-Contract([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "ChestSearch native input contract: $Message" }
    $script:checks++
}
function Method($Type, [string]$Name, [string[]]$Arguments, [string]$Returns, [bool]$Static) {
    $matches = @($Type.Methods | Where-Object {
        $_.Name -eq $Name -and $_.ReturnType.FullName -eq $Returns -and $_.IsStatic -eq $Static -and
        (($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ',') -eq ($Arguments -join ',')
    })
    Assert-Contract ($matches.Count -eq 1) "$($Type.FullName).$Name signature"
    return $matches[0]
}
function Calls($Method, [string]$Target) { return @($Method.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.FullName -eq $Target }) }
try {
    $zinput = $utils.Types | Where-Object Name -eq ZInput
    $button = $zinput.NestedTypes | Where-Object Name -eq ButtonDef
    foreach ($name in @('m_heldDynamic','m_heldFixed','m_wasPressedDynamic','m_wasPressedFixed','m_pressedDynamic','m_pressedFixed','m_releasedDynamic','m_releasedFixed')) {
        Assert-Contract (@($button.Fields | Where-Object { $_.Name -eq $name -and $_.FieldType.FullName -eq 'System.Boolean' -and -not $_.IsStatic }).Count -eq 1) "ButtonDef.$name boolean field"
    }
    Assert-Contract (@($zinput.Fields | Where-Object { $_.Name -eq 'm_instance' -and $_.FieldType.FullName -eq 'ZInput' -and $_.IsStatic }).Count -eq 1) 'ZInput instance field'
    Assert-Contract (@($zinput.Fields | Where-Object { $_.Name -eq 'm_buttons' -and $_.FieldType.FullName -eq 'System.Collections.Generic.Dictionary`2<System.String,ZInput/ButtonDef>' }).Count -eq 1) 'native button dictionary'
    foreach ($name in @('GetButton','GetButtonDown','GetButtonUp')) { $null = Method $zinput $name @('System.String') 'System.Boolean' $true }
    foreach ($name in @('GetKey','GetKeyDown')) { $null = Method $zinput $name @('UnityEngine.KeyCode','System.Boolean') 'System.Boolean' $true }
    foreach ($name in @('Update','FixedUpdate')) { $null = Method $zinput $name @('System.Single') 'System.Void' $true }
    $pressed = $button.Methods | Where-Object Name -eq get_Pressed
    foreach ($name in @('m_pressedDynamic','m_pressedFixed')) {
        Assert-Contract (@($pressed.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq $name }).Count -eq 1) "native pressed getter reads $name"
    }
    $callbacks = $zinput.NestedTypes | Where-Object Name -eq '<>c'
    $rawDown = $callbacks.Methods | Where-Object Name -match '^<GetKeyDown>'
    Assert-Contract ((Calls $rawDown 'System.Boolean UnityEngine.InputSystem.Controls.ButtonControl::get_wasPressedThisFrame()').Count -eq 1) 'raw shortcut edge uses InputSystem phase state'
    $buttonDown = $callbacks.Methods | Where-Object Name -match '^<GetButtonDown>'
    Assert-Contract ((Calls $buttonDown 'System.Boolean ZInput/ButtonDef::get_Pressed()').Count -eq 1) 'native action edge uses cached button state'
    $player = $game.Types | Where-Object Name -eq Player
    $playerGate = Method $player 'TakeInput' @() 'System.Boolean' $false
    Assert-Contract ($playerGate.IsVirtual -and -not $playerGate.IsNewSlot) 'Player.TakeInput overrides the Character virtual gate'
    $null = Method $player 'StartGuardianPower' @() 'System.Boolean' $false
    $playerUpdate = $player.Methods | Where-Object Name -eq Update
    $gate = Calls $playerUpdate 'System.Boolean Character::TakeInput()'
    $guardian = Calls $playerUpdate 'System.Boolean Player::StartGuardianPower()'
    Assert-Contract ($gate.Count -eq 1 -and $guardian.Count -eq 1 -and $gate[0].Offset -lt $guardian[0].Offset) 'Player.Update has its own input gate before guardian consumption'
    Assert-Contract (@($playerUpdate.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq 'GP' }).Count -eq 1) 'Player.Update reads the native GP action'
    $controller = $game.Types | Where-Object Name -eq PlayerController
    $null = Method $controller 'TakeInput' @('System.Boolean') 'System.Boolean' $false
    $gameUpdate = ($game.Types | Where-Object Name -eq Game).Methods | Where-Object Name -eq Update
    Assert-Contract ((Calls $gameUpdate 'System.Void ZInput::Update(System.Single)').Count -eq 1) 'Game.Update refreshes native dynamic input'
    $gameFixed = ($game.Types | Where-Object Name -eq Game).Methods | Where-Object Name -eq FixedUpdate
    Assert-Contract ((Calls $gameFixed 'System.Void ZInput::FixedUpdate(System.Single)').Count -eq 1) 'Game.FixedUpdate refreshes native fixed input'
} finally { $utils.Dispose(); $game.Dispose() }

# Compile optional runtime probes, but never start a game process here.
$probeDirectory = Join-Path $root ('.cache/chest-input-contracts-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $probeDirectory | Out-Null
$probe = Join-Path $probeDirectory 'ChestSearchNativeChecks.dll'
$refs = @($PluginAssembly)
$refs += @('Game/BepInEx/core/BepInEx.dll','Game/BepInEx/core/0Harmony.dll','Game/BepInEx/plugins/Jotunn.dll') | ForEach-Object { Join-Path $root $_ }
$refs += @('assembly_valheim.dll','assembly_guiutils.dll','assembly_utils.dll','SoftReferenceableAssets.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.PhysicsModule.dll','UnityEngine.InputLegacyModule.dll','UnityEngine.UI.dll','UnityEngine.UIModule.dll','UnityEngine.TextRenderingModule.dll','netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
$argsList = @('/nologo','/target:library','/codepage:65001',('/out:' + $probe))
$argsList += $refs | ForEach-Object { '/reference:' + $_ }
$argsList += Join-Path $PSScriptRoot 'NativeChecks.cs'
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') @argsList
if ($LASTEXITCODE -ne 0) { throw 'Native input probe compilation failed.' }
Write-Output "PASS $checks native input contracts and optional probe compilation; no game process started."
