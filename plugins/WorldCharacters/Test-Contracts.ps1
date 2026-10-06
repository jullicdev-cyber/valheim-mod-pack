[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory, [string]$PluginAssembly)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Add-Type -Path (Join-Path $root 'Game/BepInEx/core/Mono.Cecil.dll')
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDirectory 'valheim_Data/Managed/assembly_valheim.dll'))
$jotunn = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $root 'Game/BepInEx/plugins/Jotunn.dll'))
$gui = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDirectory 'valheim_Data/Managed/assembly_guiutils.dll'))
$utils = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDirectory 'valheim_Data/Managed/assembly_utils.dll'))
if (-not $PluginAssembly) { $PluginAssembly = Join-Path $root 'local-plugins/WorldCharacters.dll' }
$plugin = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($PluginAssembly)
$count = 0
try {
    if ($plugin.Name.Name -ne 'WorldCharacters') { throw 'WorldCharacters CLR assembly identity changed; dependent mods cannot bind safely.' }
    $count++
    foreach ($type in $plugin.MainModule.GetTypes()) {
        $attribute = $type.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch' }
        if (-not $attribute) { continue }
        $targetTypeName = $attribute.ConstructorArguments[0].Value.FullName
        $methodName = [string]$attribute.ConstructorArguments[1].Value
        $targetTypes = @(@($game.MainModule.GetTypes()) + @($jotunn.MainModule.GetTypes()) + @($gui.MainModule.GetTypes()) + @($utils.MainModule.GetTypes()) | Where-Object FullName -eq $targetTypeName)
        if ($targetTypes.Count -ne 1) { throw "Missing or ambiguous native type: $targetTypeName" }
        $targetType = $targetTypes[0]
        $targets = @($targetType.Methods | Where-Object Name -eq $methodName)
        if ($targets.Count -ne 1) { throw "Missing or ambiguous native target: $targetTypeName.$methodName" }
        $target = $targets[0]
        foreach ($patch in $type.Methods | Where-Object Name -in 'Prefix','Postfix','Finalizer') {
            foreach ($parameter in $patch.Parameters) {
                $name = $parameter.Name
                if ($name -in '__instance','__runOriginal','__exception') { continue }
                $expected = $null
                if ($name -eq '__result') { $expected = $target.ReturnType.FullName }
                elseif ($name.StartsWith('___', [StringComparison]::Ordinal)) {
                    $fieldName = $name.Substring(3)
                    $expected = ($targetType.Fields | Where-Object Name -eq $fieldName).FieldType.FullName
                }
                elseif ($name -match '^__(\d+)$') { $expected = $target.Parameters[[int]$Matches[1]].ParameterType.FullName }
                else { $expected = ($target.Parameters | Where-Object Name -eq $name).ParameterType.FullName }
                $actual = $parameter.ParameterType.FullName.TrimEnd('&')
                if (-not $expected -or $actual -ne $expected) { throw "Invalid patch parameter $($type.Name).$name ($actual vs $expected)" }
            }
        }
        $count++
    }
    foreach ($spec in @(@('Player',33),@('Inventory',109))) {
        $type = $game.MainModule.Types | Where-Object Name -eq $spec[0]
        $save = $type.Methods | Where-Object Name -eq 'Save'
        $constant = $save.Body.Instructions | Where-Object { $_.OpCode.Name -like 'ldc.i4*' } | Select-Object -First 1
        if ([int]$constant.Operand -ne $spec[1]) { throw "Unsupported native $($spec[0]) save version" }
        $count++
    }
    $profile = $game.MainModule.Types | Where-Object Name -eq 'PlayerProfile'
    if (-not ($profile.Fields | Where-Object { $_.Name -eq 'm_playerData' -and $_.FieldType.FullName -eq 'System.Byte[]' })) { throw 'Player data field changed' }
    if (-not ($profile.Methods | Where-Object { $_.Name -eq 'GetWorldData' -and $_.Parameters.Count -eq 1 -and $_.Parameters[0].ParameterType.FullName -eq 'System.Int64' })) { throw 'World profile getter changed' }
    $count += 2
    # The shutdown checkpoint reuses the character snapshot committed before the
    # scene invalidates Player's ZDO. Fail if native lifecycle ordering changes.
    $gameType = $game.MainModule.Types | Where-Object Name -eq 'Game'
    # SavePlayerProfile is replaced for protected characters. Its native caller
    # relies on this method resetting the instance timer after an autosave.
    $saveTimer = @($gameType.Fields | Where-Object Name -eq 'm_saveTimer')
    if ($saveTimer.Count -ne 1 -or $saveTimer[0].IsStatic -or $saveTimer[0].FieldType.FullName -ne 'System.Single') {
        throw 'Game.m_saveTimer field contract changed'
    }
    $count++
    $shuttingDown = @($gameType.Methods | Where-Object Name -eq 'IsShuttingDown')
    if ($shuttingDown.Count -ne 1 -or -not $shuttingDown[0].IsPublic -or $shuttingDown[0].IsStatic -or $shuttingDown[0].ReturnType.FullName -ne 'System.Boolean') {
        throw 'Game.IsShuttingDown contract changed'
    }
    $shutdown = $gameType.Methods | Where-Object Name -eq 'Shutdown'
    $calls = @($shutdown.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object { $_.Operand.DeclaringType.FullName + '.' + $_.Operand.Name })
    $savePlayer = [array]::IndexOf($calls,'Game.SavePlayerProfile')
    $stopScene = [array]::IndexOf($calls,'ZNetScene.Shutdown')
    $saveWorld = [array]::IndexOf($calls,'ZNet.Shutdown')
    if ($savePlayer -lt 0 -or $stopScene -le $savePlayer -or $saveWorld -le $stopScene) {
        throw 'Native shutdown no longer saves character before scene teardown and world save'
    }
    $instructions = @($shutdown.Body.Instructions)
    $flagWrite = @($instructions | Where-Object { $_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq 'm_shuttingDown' })
    $playerCall = @($instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'SavePlayerProfile' })
    if ($flagWrite.Count -ne 1 -or $flagWrite[0].Offset -ge $playerCall[0].Offset) { throw 'Native shutdown flag must precede the final character save' }
    $count += 3
    Write-Output "PASS: $count native contracts (patch signatures and supported serialization versions). No game process started."
} finally { $plugin.Dispose(); $game.Dispose(); $jotunn.Dispose(); $gui.Dispose(); $utils.Dispose() }
