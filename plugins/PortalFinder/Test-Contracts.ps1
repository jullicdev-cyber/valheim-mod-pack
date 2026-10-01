[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GameDirectory,
    [string]$PluginFile
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $PluginFile) { $PluginFile = Join-Path $root 'local-plugins/PortalFinder.dll' }
# Cecil reads metadata and IL; none of the game or plugin assemblies is executed.
Add-Type -Path (Join-Path $root 'Game/BepInEx/core/Mono.Cecil.dll')
$script:checks = 0
function Check([bool]$Passed, [string]$Message) {
    $script:checks++
    if (-not $Passed) { throw "Portal Finder API contract changed: $Message" }
}
function Method($Type, [string]$Name, [string]$Parameters, [string]$Returns) {
    if ($null -eq $Type) { throw "Portal Finder API type unavailable for $Name" }
    $found = @($Type.Methods | Where-Object {
        $_.Name -eq $Name -and (($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ',') -eq $Parameters
    })
    Check ($found.Count -eq 1) ($Type.FullName + '::' + $Name + '(' + $Parameters + ')')
    Check ($found[0].ReturnType.FullName -eq $Returns) ($Name + ' return type')
    return $found[0]
}
function Calls($Method, [string]$Part) {
    return [bool]($Method.Body.Instructions | Where-Object { $_.Operand -and $_.Operand.ToString().Contains($Part) })
}
$assemblies = @()
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$reader = [Mono.Cecil.ReaderParameters]::new()
$reader.AssemblyResolver = $resolver
try {
    $managed = Join-Path $GameDirectory 'valheim_Data/Managed'
    foreach ($directory in @($managed, (Join-Path $root 'Game/BepInEx/core'), (Join-Path $root 'Game/BepInEx/plugins'), (Split-Path $PluginFile -Parent))) {
        $resolver.AddSearchDirectory($directory)
    }
    $game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $managed 'assembly_valheim.dll'), $reader); $assemblies += $game
    $utils = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $managed 'assembly_utils.dll'), $reader); $assemblies += $utils
    $xportal = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $root 'Game/BepInEx/plugins/XPortal/XPortal.dll'), $reader); $assemblies += $xportal
    $jotunn = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $root 'Game/BepInEx/plugins/Jotunn.dll'), $reader); $assemblies += $jotunn
    $plugin = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($PluginFile, $reader); $assemblies += $plugin

    $map = $game.MainModule.GetType('Minimap')
    $convert = Method $map 'ScreenToWorldPoint' 'UnityEngine.Vector3' 'UnityEngine.Vector3'
    Check $convert.IsPrivate 'ScreenToWorldPoint remains private and reflected'
    Check (Calls $convert 'Minimap::MapPointToWorld(') 'screen conversion uses native map offset and zoom'
    $show = Method $map 'ShowPointOnMap' 'UnityEngine.Vector3' 'System.Void'
    Check $show.IsPublic 'ShowPointOnMap public API'
    foreach ($name in 'OnMapLeftClick','OnMapDblClick') {
        $click = Method $map $name '' 'System.Void'
        Check $click.IsPublic ($name + ' public Harmony target')
    }
    $add = Method $map 'AddPin' 'UnityEngine.Vector3,Minimap/PinType,System.String,System.Boolean,System.Boolean,System.Int64,Splatform.PlatformUserID' 'Minimap/PinData'
    Check ($add.IsPublic -and $add.Parameters[5].IsOptional -and $add.Parameters[6].IsOptional) 'temporary map marker overload and optional owner/author'
    $remove = Method $map 'RemovePin' 'Minimap/PinData' 'System.Void'
    Check $remove.IsPublic 'remove only the owned temporary marker'
    $pinType = $map.NestedTypes | Where-Object Name -eq 'PinType'
    Check ([bool]($pinType.Fields | Where-Object { $_.Name -eq 'Icon4' -and $_.Constant -eq 6 })) 'portal map icon is native Icon4 = 6'
    $pin = $map.NestedTypes | Where-Object Name -eq 'PinData'
    Check ([bool]($pin.Fields | Where-Object { $_.Name -eq 'm_name' -and $_.IsPublic -and $_.FieldType.FullName -eq 'System.String' })) 'marker label field'

    $zdos = $game.MainModule.GetType('ZDOMan')
    $portalList = Method $zdos 'GetPortalList' '' 'System.Collections.Generic.List`1<ZDO>'
    Check ($portalList.IsPublic -and (Calls $portalList 'ZDOMan::m_portalObjects')) 'complete persistent portal registry'
    $position = Method ($game.MainModule.GetType('ZDO')) 'GetPosition' '' 'UnityEngine.Vector3'
    Check $position.IsPublic 'persistent portal position getter'
    $zinput = $utils.MainModule.GetType('ZInput')
    foreach ($name in 'GetButton','GetButtonDown') {
        $input = Method $zinput $name 'System.String' 'System.Boolean'
        Check ($input.IsPublic -and $input.IsStatic) ($name + ' native input Harmony target')
        Check (@($zinput.Methods | Where-Object Name -eq $name).Count -eq 1) ($name + ' target has no ambiguous overload')
    }
    $pointer = Method $zinput 'get_pointerPosition' '' 'UnityEngine.Vector3'
    Check ($pointer.IsPublic -and $pointer.IsStatic) 'native mouse/controller pointer API'

    $manager = $xportal.MainModule.GetType('XPortal.KnownPortalsManager')
    $instance = Method $manager 'get_Instance' '' 'XPortal.KnownPortalsManager'
    Check ($instance.IsPublic -and $instance.IsStatic) 'XPortal reflected singleton getter'
    $list = Method $manager 'GetList' '' 'System.Collections.Generic.List`1<XPortal.KnownPortal>'
    Check ($list.IsPublic -and (Calls $list 'System.Linq.Enumerable::ToList')) 'XPortal supplies a copied registry list'
    $sync = Method $manager 'UpdateFromResyncPackage' 'ZPackage' 'System.Void'
    Check (Calls $sync 'XPortal.KnownPortalsManager::UpdateFromList') 'full resync reconciles the client registry'
    $reset = Method $manager 'Reset' '' 'System.Void'
    Check (Calls $reset '::Clear()') 'XPortal reset clears old world records'
    $known = $xportal.MainModule.GetType('XPortal.KnownPortal')
    foreach ($entry in @(@('Id','ZDOID'), @('Name','System.String'), @('Location','UnityEngine.Vector3'))) {
        $getter = Method $known ('get_' + $entry[0]) '' $entry[1]
        Check $getter.IsPublic ('XPortal ' + $entry[0] + ' readable property')
    }
    $portalPlugin = $xportal.MainModule.GetType('XPortal.XPortal')
    $all = Method $portalPlugin 'GetAllPortalZDOs' '' 'System.Collections.Generic.List`1<ZDO>'
    Check (Calls $all 'ZDOMan::GetPortalList()') 'XPortal server source includes unloaded world portals'
    $joined = Method $portalPlugin 'MinimapManager_OnVanillaMapDataLoaded' '' 'System.Void'
    Check (Calls $joined 'XPortal.RPC.SendToServer::SyncRequest') 'XPortal automatically requests full registry at join'

    $gui = $jotunn.MainModule.GetType('Jotunn.Managers.GUIManager')
    foreach ($entry in @(@('CreateButton',7), @('CreateText',13), @('get_CustomGUIFront',0))) {
        Check ([bool]($gui.Methods | Where-Object { $_.Name -eq $entry[0] -and $_.Parameters.Count -eq $entry[1] -and $_.IsPublic })) ('Jotunn UI ' + $entry[0])
    }
    $finder = $plugin.MainModule.GetType('ValheimModPack.PortalFinder.Plugin')
    Check ($null -ne $finder) 'compiled Portal Finder plugin definition'
    Check ([bool]($finder.CustomAttributes | Where-Object {
        $_.AttributeType.FullName -eq 'BepInEx.BepInDependency' -and $_.ConstructorArguments.Count -eq 2 -and
        $_.ConstructorArguments[0].Value -eq 'yay.spikehimself.xportal' -and $_.ConstructorArguments[1].Value -eq 2
    })) 'XPortal is a soft load-order dependency'
    foreach ($entry in @(@('PointClickPatch','Minimap','OnMapLeftClick','Minimap'), @('DoubleClickPatch','Minimap','OnMapDblClick','Minimap'), @('DownPatch','ZInput','GetButtonDown','System.Boolean&'), @('HeldPatch','ZInput','GetButton','System.Boolean&'))) {
        $patch = $finder.NestedTypes | Where-Object Name -eq $entry[0]
        Check ([bool]($patch.CustomAttributes | Where-Object {
            $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch' -and $_.ConstructorArguments.Count -eq 2 -and
            $_.ConstructorArguments[0].Value.FullName -eq $entry[1] -and $_.ConstructorArguments[1].Value -eq $entry[2]
        })) ($entry[0] + ' maps to an installed native target')
        $prefix = Method $patch 'Prefix' $entry[3] 'System.Boolean'
        Check $prefix.IsStatic ($entry[0] + ' static Harmony prefix')
    }
    $registry = $plugin.MainModule.GetType('ValheimModPack.PortalFinder.PortalRegistry')
    foreach ($name in 'AfterResync','AfterReset') {
        $postfix = Method $registry $name '' 'System.Void'
        Check $postfix.IsStatic ($name + ' dynamic Harmony postfix')
    }
    $read = Method $registry 'TryRead' 'System.Collections.Generic.List`1<ValheimModPack.PortalFinder.PortalRecord>&,System.String&' 'System.Boolean'
    Check (Calls $read 'ZDOMan::GetPortalList()') 'compiled registry reads native persistent records'
    Write-Output "PASS: $script:checks installed Valheim/XPortal/Jotunn and compiled Portal Finder API contracts; no Unity execution."
} finally {
    foreach ($assembly in $assemblies) { $assembly.Dispose() }
    $resolver.Dispose()
}
