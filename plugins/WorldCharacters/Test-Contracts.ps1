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
function Get-NativeBodyHash([byte[]]$image, [int]$rva) {
    $peOffset = [BitConverter]::ToInt32($image,0x3c)
    $sectionCount = [BitConverter]::ToUInt16($image,$peOffset+6)
    $sections = $peOffset+24+[BitConverter]::ToUInt16($image,$peOffset+20)
    $body = -1
    for ($i=0; $i -lt $sectionCount; ++$i) {
        $section = $sections+40*$i
        $address = [BitConverter]::ToUInt32($image,$section+12)
        $size = [Math]::Max([BitConverter]::ToUInt32($image,$section+8),[BitConverter]::ToUInt32($image,$section+16))
        if ($rva -ge $address -and $rva -lt $address+$size) { $body = [int]([BitConverter]::ToUInt32($image,$section+20)+$rva-$address); break }
    }
    if ($body -lt 0) { throw 'Native method RVA is outside the PE sections' }
    $format = $image[$body] -band 3
    if ($format -eq 2) { $size = $image[$body] -shr 2; $body++ }
    elseif ($format -eq 3) { $size = [BitConverter]::ToInt32($image,$body+4); $body += ([BitConverter]::ToUInt16($image,$body) -shr 12)*4 }
    else { throw 'Unknown native method body format' }
    $il = New-Object byte[] $size
    [Array]::Copy($image,$body,$il,0,$size)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($il)).Replace('-','') } finally { $sha.Dispose() }
}
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
    # A skipped periodic map must cover every persisted native input. Checking
    # only the BitArray field names would miss added native pin/public data.
    $minimap = $game.MainModule.Types | Where-Object Name -eq 'Minimap'
    $getMap = @($minimap.Methods | Where-Object { $_.Name -eq 'GetMapData' -and $_.Parameters.Count -eq 0 })
    $saveMap = @($minimap.Methods | Where-Object { $_.Name -eq 'SaveMapData' -and $_.Parameters.Count -eq 0 })
    if ($getMap.Count -ne 1 -or $saveMap.Count -ne 1 -or -not $getMap[0].HasBody -or -not $saveMap[0].HasBody) { throw 'Native map capture methods changed' }
    $version = $getMap[0].Body.Instructions | Where-Object { $_.OpCode.Name -like 'ldc.i4*' } | Select-Object -First 1
    if ($version.OpCode.Name -ne 'ldc.i4.8') { throw 'Unsupported native minimap save version' }
    $pinData = $minimap.NestedTypes | Where-Object Name -eq 'PinData'
    foreach ($spec in @(
        @($minimap,'m_textureSize','System.Int32',$true),
        @($minimap,'m_explored','System.Collections.BitArray',$false),
        @($minimap,'m_exploredOthers','System.Collections.BitArray',$false),
        @($minimap,'m_pins','System.Collections.Generic.List`1<Minimap/PinData>',$false),
        @($pinData,'m_save','System.Boolean',$true),@($pinData,'m_name','System.String',$true),
        @($pinData,'m_pos','UnityEngine.Vector3',$true),@($pinData,'m_type','Minimap/PinType',$true),
        @($pinData,'m_checked','System.Boolean',$true),@($pinData,'m_ownerID','System.Int64',$true),
        @($pinData,'m_author','Splatform.PlatformUserID',$true))) {
        $matches = @($spec[0].Fields | Where-Object { $_.Name -eq $spec[1] -and $_.FieldType.FullName -ceq $spec[2] -and -not $_.IsStatic -and $_.IsPublic -eq $spec[3] })
        if ($matches.Count -ne 1) { throw "Native map input ABI changed: $($spec[0].FullName).$($spec[1])" }
        $count++
    }
    $expectedMapInputs = @('Minimap.m_textureSize','Minimap.m_explored','Minimap.m_exploredOthers','Minimap.m_pins',
        'Minimap/PinData.m_save','Minimap/PinData.m_name','Minimap/PinData.m_pos','Minimap/PinData.m_type',
        'Minimap/PinData.m_checked','Minimap/PinData.m_ownerID','Minimap/PinData.m_author') | Sort-Object
    $actualMapInputs = @($getMap[0].Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.DeclaringType.FullName -in 'Minimap','Minimap/PinData' } | ForEach-Object { $_.Operand.DeclaringType.FullName + '.' + $_.Operand.Name } | Sort-Object -Unique)
    if (($actualMapInputs -join '|') -cne ($expectedMapInputs -join '|')) { throw 'Native GetMapData inputs changed; periodic-map comparison could omit persisted data' }
    $savedPredicate = @($getMap[0].Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldftn' } | ForEach-Object { $_.Operand.Resolve() })
    if ($savedPredicate.Count -ne 1 -or ($savedPredicate[0].Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.FullName -ceq 'System.Boolean Minimap/PinData::m_save' }).Count -ne 1) { throw 'Native saved pin filter changed' }
    $mapCalls = @($getMap[0].Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object { $_.Operand.DeclaringType.FullName + '.' + $_.Operand.Name })
    if ('ZNet.IsReferencePositionPublic' -notin $mapCalls -or 'ZPackage.WriteCompressed' -notin $mapCalls) { throw 'Native map position/compression contract changed' }
    $saveMapCalls = @($saveMap[0].Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object { $_.Operand.DeclaringType.FullName + '.' + $_.Operand.Name })
    if (($saveMapCalls -join '|') -cne 'Game.get_instance|Game.GetPlayerProfile|Minimap.GetMapData|PlayerProfile.SetMapData') { throw 'Native SaveMapData side effects changed; periodic save cannot safely be skipped' }
    $count += 5
    $compatibility = $plugin.MainModule.Types | Where-Object FullName -eq 'ValheimModPack.WorldCharacters.MapCaptureCompatibility'
    if (-not $compatibility) { throw 'Missing runtime guard for exact native map serialization' }
    $nativeImage = [IO.File]::ReadAllBytes((Join-Path $GameDirectory 'valheim_Data/Managed/assembly_valheim.dll'))
    foreach ($spec in @(@('GetMapHash',$getMap[0]),@('SaveMapHash',$saveMap[0]),@('SavedPinHash',$savedPredicate[0]))) {
        $expected = @($compatibility.Fields | Where-Object { $_.Name -eq $spec[0] -and $_.HasConstant })
        if ($expected.Count -ne 1 -or $expected[0].Constant -cne (Get-NativeBodyHash $nativeImage $spec[1].RVA)) { throw "Runtime native map fingerprint mismatch: $($spec[0]); update requires reviewing every persisted input" }
        $count++
    }
    $compatibilityCalls = @($compatibility.Methods | Where-Object Name -eq 'HasIntervention' | ForEach-Object { $_.Body.Instructions } | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object { $_.Operand.DeclaringType.FullName + '.' + $_.Operand.Name })
    if ('HarmonyLib.Harmony.GetPatchInfo' -notin $compatibilityCalls) { throw 'Runtime map fallback must observe Harmony serialization interventions' }
    $count++
    $pluginType = $plugin.MainModule.Types | Where-Object FullName -eq 'ValheimModPack.WorldCharacters.Plugin'
    $publish = @($pluginType.Methods | Where-Object Name -eq 'Publish')
    if ($publish.Count -ne 1) { throw 'Missing WorldCharacters.Publish map policy integration' }
    $publishInstructions = @($publish[0].Body.Instructions)
    $mapRead = @($publishInstructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'ValheimModPack.WorldCharacters.GameMapCapture' -and $_.Operand.Name -eq 'TryRead' })
    $nativeCapture = @($publishInstructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'Minimap' -and $_.Operand.Name -eq 'SaveMapData' })
    $enqueue = @($publishInstructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'TryEnqueue' })
    $admit = @($publishInstructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'ValheimModPack.WorldCharacters.MapCapturePolicy' -and $_.Operand.Name -eq 'Admit' })
    if ($mapRead.Count -ne 1 -or $nativeCapture.Count -ne 1 -or $enqueue.Count -ne 2 -or $admit.Count -ne 1 -or $mapRead[0].Offset -ge $nativeCapture[0].Offset -or $admit[0].Offset -le ($enqueue | Measure-Object Offset -Maximum).Maximum) { throw 'Map baseline must follow successful durable snapshot admission and preflight the native capture' }
    $reset = $pluginType.Methods | Where-Object Name -eq 'ResetSession'
    if (-not ($reset.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'ValheimModPack.WorldCharacters.MapCapturePolicy' -and $_.Operand.Name -eq 'Reset' })) { throw 'Map comparison baseline must be reset between sessions' }
    $count += 2
    $profile = $game.MainModule.Types | Where-Object Name -eq 'PlayerProfile'
    if (-not ($profile.Fields | Where-Object { $_.Name -eq 'm_playerData' -and $_.FieldType.FullName -eq 'System.Byte[]' })) { throw 'Player data field changed' }
    $worldGetter = @($profile.Methods | Where-Object { $_.Name -eq 'GetWorldData' -and $_.Parameters.Count -eq 1 -and $_.Parameters[0].ParameterType.FullName -eq 'System.Int64' })
    if ($worldGetter.Count -ne 1) { throw 'World profile getter changed' }
    $count += 2
    $worldData = $worldGetter[0].ReturnType.Resolve()
    foreach ($spec in @(
        @('m_haveCustomSpawnPoint','System.Boolean'),@('m_haveLogoutPoint','System.Boolean'),@('m_haveDeathPoint','System.Boolean'),
        @('m_spawnPoint','UnityEngine.Vector3'),@('m_logoutPoint','UnityEngine.Vector3'),@('m_deathPoint','UnityEngine.Vector3'),@('m_homePoint','UnityEngine.Vector3'),
        @('m_mapData','System.Byte[]'))) {
        $matches = @($worldData.Fields | Where-Object { $_.Name -eq $spec[0] -and $_.FieldType.FullName -eq $spec[1] -and -not $_.IsStatic })
        if ($matches.Count -ne 1) { throw "Cached native world-profile field changed: $($spec[0])" }
        $count++
    }
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
