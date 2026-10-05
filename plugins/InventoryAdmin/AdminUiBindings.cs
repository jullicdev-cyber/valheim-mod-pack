using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimModPack.InventoryAdmin
{
    // Presentation data is deliberately detached from live ItemData and Inventory.
    // Every inventory action returns the snapshot and item token to the service.
    public sealed class AdminPlayerView
    {
        public long PeerId;
        public string Name;
        public bool IsAdmin;
    }
    public sealed class AdminItemView
    {
        public string ItemToken, Prefab, Name, Group;
        public int Count, Quality, SlotX, SlotY;
        public float Durability, MaxDurability;
        public bool Equipped;
        public Sprite Icon;
    }
    public sealed class AdminInventoryView
    {
        public long PeerId;
        public string Name, SnapshotToken;
        public readonly List<AdminItemView> Items = new List<AdminItemView>();
    }
    // Only the private administrator snapshot reaches these controls. Peer IDs
    // are session-local; persistent account identifiers never enter the UI.
    public sealed class GroupRadiusAdminView
    {
        public bool Enabled, ReadOnly;
        public float Radius;
        public long LeaderPeerId, Revision;
        public string LeaderName, Notice;
        public HashSet<long> ExemptPeers = new HashSet<long>();
    }
    public sealed class AdminUiBindings
    {
        public Func<bool> CanUse, IsHost;
        public Func<long> LocalPeerId;
        public Func<string> ShortcutLabel;
        public Func<bool> IsTrackingPlayers;
        public Func<string> TrackingShortcutLabel;
        public Action<bool> SetTrackingPlayers;
        public Func<long, bool> CanFindPlayerOnMap;
        public Action<long> FindPlayerOnMap;
        public Func<GroupRadiusAdminView> GetGroupRadius;
        public Func<string> GroupRadiusShortcutLabel;
        public Action<long, bool, float> UpdateGroupRadius;
        public Action<long, long> SetGroupRadiusLeader;
        public Action<long, long, bool> SetGroupRadiusExemption;
        public Func<string, string, string> Translate;
        public Action RequestPlayers;
        public Action<long> RequestInventory;
        public Action<string, string, int> Delete, Take;
        public Action<long, bool> SetAdmin;
        public Action<Exception> Error;
        public Action OnClosed;
    }
}
