using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.WorldCharacters
{
    internal static class GameState
    {
        private static readonly FieldInfo Data = AccessTools.Field(typeof(PlayerProfile), "m_playerData");
        private static readonly MethodInfo WorldData = AccessTools.Method(typeof(PlayerProfile), "GetWorldData", new[] {typeof(long)});
        public static string BuildFingerprint()
        {
            var lines = new List<string> { "WorldCharacters/1;Player33;Inventory109", global::Version.CurrentVersion.ToString() };
            foreach (var pair in Chainloader.PluginInfos.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                string location = pair.Value.Instance.GetType().Assembly.Location;
                lines.Add(pair.Key + ":" + StateCodec.Hash(File.ReadAllBytes(location)));
            }
            return StateCodec.Hash(System.Text.Encoding.UTF8.GetBytes(String.Join("\n", lines.ToArray())));
        }
        public static CharacterState FromProfile(PlayerProfile profile, long world, string owner, string build, bool live, bool includeMap = true)
        {
            if (profile == null) throw new InvalidOperationException("Character profile is unavailable.");
            byte[] payload;
            if (live)
            {
                Player player = Player.m_localPlayer;
                if (!player || player.GetPlayerID() != profile.GetPlayerID()) throw new InvalidOperationException("Local character identity mismatch.");
                // Run the real Player.Save chain, including EAQS, Epic Loot and backpack patches.
                var package = new ZPackage(); player.Save(package); payload = package.GetArray();
                ValidateInventoryBounds(player);
                Data.SetValue(profile, payload);
                if (player.IsDead()) profile.ClearLoguoutPoint(); else profile.SetLogoutPoint(player.transform.position);
            }
            else payload = (byte[])Data.GetValue(profile) ?? new byte[0];
            return new CharacterState { World = world, Owner = owner, Character = profile.GetPlayerID(), Name = profile.GetName(),
                Build = build, Player = (byte[])payload.Clone(), WorldData = CaptureWorld(profile, world, includeMap) };
        }
        public static void Apply(PlayerProfile profile, CharacterState state)
        {
            if (profile.GetPlayerID() != state.Character) throw new InvalidDataException("Selected character does not match server state.");
            Data.SetValue(profile, state.Player.Length == 0 ? null : (byte[])state.Player.Clone());
            ApplyWorld(profile, state.World, state.WorldData);
        }
        public static void VerifyLoaded(Player player, CharacterState state)
        {
            if (player.GetPlayerID() != state.Character) throw new InvalidDataException("Loaded character ID mismatch.");
            ValidateInventoryBounds(player);
            if (state.Player.Length == 0) return; // Explicitly approved fresh character: vanilla starting items.
            var inv = new ZPackage(); player.GetInventory().Save(inv);
            using (var stream = new MemoryStream(inv.GetArray()))
            using (var reader = new BinaryReader(stream))
                NativeInventory.RequireSameItems(NativeInventory.ReadPlayer(state.Player), NativeInventory.ReadInventory(reader));
        }
        public static void RequireSavedPrefabs(CharacterState state)
        {
            foreach (StoredItem item in NativeInventory.IncludingBackpacks(Items(state)))
            {
                GameObject prefab = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(item.Prefab) : null;
                if (!prefab || !prefab.GetComponent<ItemDrop>()) throw new InvalidDataException("Missing item prefab " + item.Prefab + " (including backpack contents). Restore the matching modpack first.");
            }
        }
        private static void ValidateInventoryBounds(Player player)
        {
            Inventory inventory = player.GetInventory();
            var cells = new HashSet<long>();
            foreach (var item in inventory.GetAllItems())
            {
                if (item == null || !item.m_dropPrefab || item.m_stack <= 0 || item.m_gridPos.x < 0 || item.m_gridPos.y < 0
                    || item.m_gridPos.x >= inventory.GetWidth() || item.m_gridPos.y >= inventory.GetHeight()
                    || !cells.Add(((long)item.m_gridPos.y << 32) | (uint)item.m_gridPos.x))
                    throw new InvalidDataException("Inventory has missing prefabs, invalid cells or overlapping slots; save refused.");
            }
        }
        private static object GetWorld(PlayerProfile profile, long world) { return WorldData.Invoke(profile, new object[] {world}); }
        private static byte[] CaptureWorld(PlayerProfile profile, long world, bool includeMap)
        {
            object data = GetWorld(profile, world);
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream))
            {
                w.Write(1);
                foreach (string prefix in new[] {"spawn", "logout", "death", "home"})
                {
                    if (prefix != "home") w.Write((bool)Field(data, Flag(prefix)).GetValue(data));
                    Vector3 point = (Vector3)Field(data, "m_" + prefix + "Point").GetValue(data);
                    w.Write(point.x); w.Write(point.y); w.Write(point.z);
                }
                StateCodec.WriteBytes(w, includeMap ? ((byte[])Field(data, "m_mapData").GetValue(data) ?? new byte[0]) : new byte[0]);
                w.Flush(); return stream.ToArray();
            }
        }
        private static string Flag(string p) { return p == "spawn" ? "m_haveCustomSpawnPoint" : p == "logout" ? "m_haveLogoutPoint" : "m_haveDeathPoint"; }
        private static FieldInfo Field(object target, string name) { return AccessTools.Field(target.GetType(), name); }
        private static void ApplyWorld(PlayerProfile profile, long world, byte[] bytes)
        {
            object data = GetWorld(profile, world);
            if (bytes.Length == 0)
            {
                foreach (string p in new[] {"spawn", "logout", "death"}) Field(data, Flag(p)).SetValue(data, false);
                Field(data, "m_mapData").SetValue(data, null); return;
            }
            using (var stream = new MemoryStream(bytes))
            using (var r = new BinaryReader(stream))
            {
                if (r.ReadInt32() != 1) throw new InvalidDataException("Unsupported world profile.");
                foreach (string p in new[] {"spawn", "logout", "death", "home"})
                {
                    if (p != "home") Field(data, Flag(p)).SetValue(data, r.ReadBoolean());
                    var point = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    if (Single.IsNaN(point.sqrMagnitude) || Single.IsInfinity(point.sqrMagnitude)) throw new InvalidDataException("Invalid spawn position.");
                    Field(data, "m_" + p + "Point").SetValue(data, point);
                }
                byte[] map = StateCodec.ReadBytes(r, 10 * 1024 * 1024);
                Field(data, "m_mapData").SetValue(data, map.Length == 0 ? null : map);
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing world profile data.");
            }
        }
        public static List<StoredItem> Items(CharacterState state) { return state.Player.Length == 0 ? new List<StoredItem>() : NativeInventory.ReadPlayer(state.Player); }
        public static string ItemName(int hash)
        {
            GameObject prefab = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(hash) : null;
            return prefab ? prefab.name : hash.ToString();
        }
    }
}
