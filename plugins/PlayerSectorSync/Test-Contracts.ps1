[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory, [Parameter(Mandatory=$true)][string]$PluginFile)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
# Metadata-only inspection: these checks do not start Unity or execute the game.
Add-Type -Path (Join-Path $root 'Game/BepInEx/core/Mono.Cecil.dll')
$script:checks = 0
function Check([bool]$Passed, [string]$Message) {
    ++$script:checks
    if (-not $Passed) { throw "Player Sector Sync native contract changed: $Message" }
}
function Method($Type, [string]$Name, [string]$Parameters, [string]$Returns) {
    $found = @($Type.Methods | Where-Object {
        $_.Name -eq $Name -and (($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ',') -eq $Parameters
    })
    Check ($found.Count -eq 1) ($Type.FullName + '::' + $Name)
    Check ($found[0].ReturnType.FullName -eq $Returns) ($Name + ' return type')
    return $found[0]
}
function Index($Method, [string]$Part) {
    for ($i=0; $i -lt $Method.Body.Instructions.Count; ++$i) {
        $operand = $Method.Body.Instructions[$i].Operand
        if ($operand -and $operand.ToString().Contains($Part)) { return $i }
    }
    return -1
}
$game = $null; $plugin = $null
try {
    $game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDirectory 'valheim_Data/Managed/assembly_valheim.dll'))
    $plugin = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($PluginFile)
    $zdo = $game.MainModule.GetType('ZDO'); $man = $game.MainModule.GetType('ZDOMan'); $net = $game.MainModule.GetType('ZNet')
    $set = Method $zdo 'InternalSetPosition' 'UnityEngine.Vector3' 'System.Void'
    Check $set.IsPublic 'native position Harmony target remains public'
    $sectorCall = Index $set 'ZDO::SetSector('
    $positionStore = -1
    for ($i=0; $i -lt $set.Body.Instructions.Count; ++$i) {
        $instruction = $set.Body.Instructions[$i]
        if ($instruction.OpCode.Name -eq 'stfld' -and $instruction.Operand.ToString().Contains('ZDO::m_position')) { $positionStore=$i; break }
    }
    Check ($sectorCall -ge 0 -and $positionStore -gt $sectorCall) 'native sector notification precedes position assignment'
    $sector = Method $zdo 'SetSector' 'ZoneSystem/SectorIndex' 'System.Void'
    Check ((Index $sector 'ZDOMan::ZDOSectorInvalidated(') -ge 0) 'SetSector performs native peer notification'
    $getSector = Method $zdo 'GetSectorIndex' '' 'ZoneSystem/SectorIndex'
    Check ($getSector.IsPublic -and (Index $getSector 'ZDO::m_position') -ge 0) 'sector calculation uses final position'
    $position = Method $zdo 'GetPosition' '' 'UnityEngine.Vector3'
    Check $position.IsPublic 'position reader remains public'
    $notify = Method $man 'ZDOSectorInvalidated' 'ZDO' 'System.Void'
    Check ($notify.IsPublic -and (Index $notify 'ZDOMan/ZDOPeer::ZDOSectorInvalidated(') -ge 0) 'notification keeps native per-peer rules'
    $peer = $man.NestedTypes | Where-Object Name -eq 'ZDOPeer'
    $peerNotify = Method $peer 'ZDOSectorInvalidated' 'ZDO' 'System.Void'
    $owner = Index $peerNotify 'ZDO::GetOwner()'; $peerId = Index $peerNotify 'ZNetPeer::m_uid'
    $known = Index $peerNotify 'System.Collections.Generic.Dictionary`2<ZDOID,ZDOMan/ZDOPeer/PeerZDOInfo>::ContainsKey'
    Check ($owner -ge 0 -and $peerId -gt $owner -and $known -gt $peerId) 'native owner-peer exclusion precedes invalidation'
    Check ((Index $peerNotify 'ZDO::GetPosition()') -ge 0 -and (Index $peerNotify 'ZNetScene::InActiveArea(') -ge 0) 'native invalidation uses current position and active-area test'
    Check ((Index $peerNotify 'ZDOMan/ZDOPeer::m_invalidSector') -ge 0) 'notification queues native invalid-sector collection'
    $receive = Method $man 'RPC_ZDOData' 'ZRpc,ZPackage' 'System.Void'
    Check ((Index $receive 'ZDO::InvalidateSector()') -ge 0 -and (Index $receive 'ZDO::InvalidateSector()') -lt (Index $receive 'ZDO::InternalSetPosition(')) 'receiver processes invalidations before full updated records'
    $invalidate = Method $zdo 'InvalidateSector' '' 'System.Void'
    Check ((Index $invalidate 'ZoneSystem::SectorZero') -ge 0 -and (Index $invalidate 'ZDO::SetSector(') -ge 0) 'invalidation only relocates sector index'
    Check ((Index $invalidate 'Destroy') -eq -1) 'invalidation does not destroy ZDO'
    $remove = Method ($game.MainModule.GetType('ZNetScene')) 'RemoveObjects' 'System.Collections.Generic.List`1<ZDO>,System.Collections.Generic.List`1<ZDO>' 'System.Void'
    $persistent = Index $remove 'ZDO::get_Persistent()'; $isOwner = Index $remove 'ZDO::IsOwner()'; $destroy = Index $remove 'ZDOMan::DestroyZDO('
    Check ($persistent -ge 0 -and $isOwner -gt $persistent -and $destroy -gt $isOwner) 'native scene removal keeps persistence and ownership guards'
    Check ($remove.Body.Instructions[$isOwner+1].OpCode.Name.StartsWith('brfalse')) 'non-owning receiver branches around DestroyZDO'
    foreach ($name in @('Player','Humanoid','Character')) {
        $cleanup = Method ($game.MainModule.GetType($name)) 'OnDestroy' '' 'System.Void'
        Check ((Index $cleanup 'Inventory::') -eq -1 -and (Index $cleanup 'DropAll') -eq -1 -and (Index $cleanup 'SavePlayer') -eq -1) ($name + ' view destruction does not drop or save inventory')
    }
    $peers = Method $net 'GetPeers' '' 'System.Collections.Generic.List`1<ZNetPeer>'
    Check ($peers.IsPublic -and (Index $peers 'ZNet::m_peers') -ge 0 -and -not [bool]($peers.Body.Instructions | Where-Object OpCode -Match 'newobj')) 'peer reader returns existing list without allocating'
    $localId = Method $net 'get_LocalPlayerCharacterID' '' 'ZDOID'
    Check $localId.IsPublic 'local current-character identity API'
    $ready = Method ($game.MainModule.GetType('ZNetPeer')) 'IsReady' '' 'System.Boolean'
    Check $ready.IsPublic 'ready-peer identity API'
    $nativeSector = Method ($game.MainModule.GetType('ZoneSystem')) 'GetSectorIndex' 'UnityEngine.Vector3' 'ZoneSystem/SectorIndex'
    Check ($nativeSector.IsPublic -and $nativeSector.IsStatic) 'native sector index helper'

    $patch = $plugin.MainModule.GetType('ValheimModPack.PlayerSectorSync.PlayerPositionPatch')
    $prefix = $patch.Methods | Where-Object Name -eq 'Prefix'; $postfix = $patch.Methods | Where-Object Name -eq 'Postfix'
    Check ($prefix.ReturnType.FullName -eq 'System.Void' -and $postfix.ReturnType.FullName -eq 'System.Void') 'patch never suppresses native position method'
    Check ((Index $postfix 'ZDOMan::ZDOSectorInvalidated(') -ge 0) 'compiled postfix queues native corrected notification'
    $allCalls = @($patch.Methods | Where-Object HasBody | ForEach-Object { $_.Body.Instructions | ForEach-Object { if ($null -ne $_.Operand) { $_.Operand.ToString() } } })
    Check (-not [bool]($allCalls | Where-Object { $_ -match '::(SetPosition|InternalSetPosition|DestroyZDO|ForceSendZDO|InvokeRPC|InvokeRoutedRPC)\(|Inventory::|PlayerProfile::' })) 'compiled patch cannot move, destroy, force-send or alter inventory'
    Check (-not [bool]($patch.Methods | Where-Object HasBody | ForEach-Object { $_.Body.Instructions } | Where-Object { $_.OpCode.Name -eq 'newobj' })) 'per-position patch path has no managed allocations'
    Check ([bool]($prefix.CustomAttributes | Where-Object { $_.AttributeType.Name -eq 'HarmonyPrefix' }) -and [bool]($postfix.CustomAttributes | Where-Object { $_.AttributeType.Name -eq 'HarmonyPostfix' })) 'compiled Harmony prefix/postfix metadata'
    Write-Output "Player Sector Sync: $script:checks native API and receiver contracts passed."
}
finally { if ($plugin) { $plugin.Dispose() }; if ($game) { $game.Dispose() } }
