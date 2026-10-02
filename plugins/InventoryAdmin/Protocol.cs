using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace ValheimModPack.InventoryAdmin
{
    public enum InventoryOperation { Delete = 1, Take = 2 }

    public sealed class PlayerInfo
    {
        public long PeerId, CharacterId;
        public string OwnerId = "", Name = "";
        // Role data is returned only to authorized inventory administrators.
        public bool IsAdmin, InventoryAvailable;
    }

    public sealed class ItemInfo
    {
        public string SlotId = "", Fingerprint = "", Prefab = "", Name = "", Container = "";
        public int Stack, Quality, Variant, X, Y;
        public float Durability;
        public bool Equipped;
    }

    public sealed class InventoryView
    {
        public string RequestId = "", TargetOwner = "";
        public long TargetPeerId, TargetCharacter, Revision;
        public List<ItemInfo> Items = new List<ItemInfo>();
    }

    public sealed class MutationRequest
    {
        public string RequestId = "", ViewRequestId = "", SlotId = "", Fingerprint = "";
        public long TargetPeerId, TargetCharacter, Revision;
        public int Count;
        public InventoryOperation Operation;
    }

    // Wire data is a narrow versioned value format, never BinaryFormatter,
    // reflection-based type deserialization, or a recipe for recreating ItemData.
    public static class InventoryCodec
    {
        public const int MaximumPlayers = 128, MaximumItems = 2048, MaximumViewBytes = 2 * 1024 * 1024;
        public const int MaximumItemBytes = 4 * 1024 * 1024;
        private const int Magic = 0x49415731, Version = 1;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static byte[] EncodePlayers(IList<PlayerInfo> players)
        {
            if (players == null || players.Count > MaximumPlayers) throw new InvalidDataException("Too many players.");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8))
            {
                Header(writer, 1); writer.Write(players.Count);
                var ids = new HashSet<long>();
                foreach (PlayerInfo player in players)
                {
                    ValidatePlayer(player); if (!ids.Add(player.PeerId)) throw new InvalidDataException("Duplicate peer identity.");
                    writer.Write(player.PeerId); writer.Write(player.CharacterId);
                    PolicyBinary.WriteString(writer, player.OwnerId, 40); PolicyBinary.WriteString(writer, player.Name, 512);
                    writer.Write(player.IsAdmin); writer.Write(player.InventoryAvailable);
                }
                writer.Flush(); return Finish(stream, 128 * 1024);
            }
        }

        public static List<PlayerInfo> DecodePlayers(byte[] bytes)
        {
            using (var stream = Open(bytes, 128 * 1024))
            using (var reader = new BinaryReader(stream, Utf8))
            {
                Header(reader, 1); int count = Count(reader, MaximumPlayers); var result = new List<PlayerInfo>(count); var ids = new HashSet<long>();
                for (int i = 0; i < count; ++i)
                {
                    var player = new PlayerInfo { PeerId = reader.ReadInt64(), CharacterId = reader.ReadInt64(),
                        OwnerId = PolicyBinary.ReadString(reader, 40), Name = PolicyBinary.ReadString(reader, 512),
                        IsAdmin = Flag(reader), InventoryAvailable = Flag(reader) };
                    ValidatePlayer(player); if (!ids.Add(player.PeerId)) throw new InvalidDataException("Duplicate peer identity."); result.Add(player);
                }
                End(stream); return result;
            }
        }

        public static byte[] EncodeInventoryView(InventoryView view)
        {
            ValidateView(view);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8))
            {
                Header(writer, 2); PolicyBinary.WriteString(writer, view.RequestId, 32); PolicyBinary.WriteString(writer, view.TargetOwner, 40);
                writer.Write(view.TargetPeerId); writer.Write(view.TargetCharacter); writer.Write(view.Revision); writer.Write(view.Items.Count);
                foreach (ItemInfo item in view.Items) WriteItem(writer, item);
                writer.Flush(); return Finish(stream, MaximumViewBytes);
            }
        }

        public static InventoryView DecodeInventoryView(byte[] bytes)
        {
            using (var stream = Open(bytes, MaximumViewBytes))
            using (var reader = new BinaryReader(stream, Utf8))
            {
                Header(reader, 2);
                var view = new InventoryView { RequestId = PolicyBinary.ReadString(reader, 32), TargetOwner = PolicyBinary.ReadString(reader, 40),
                    TargetPeerId = reader.ReadInt64(), TargetCharacter = reader.ReadInt64(), Revision = reader.ReadInt64() };
                int count = Count(reader, MaximumItems);
                for (int i = 0; i < count; ++i) view.Items.Add(ReadItem(reader));
                End(stream); ValidateView(view); return view;
            }
        }

        public static byte[] EncodeMutationRequest(MutationRequest request)
        {
            ValidateMutation(request);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8))
            {
                Header(writer, 3);
                PolicyBinary.WriteString(writer, request.RequestId, 32); PolicyBinary.WriteString(writer, request.ViewRequestId, 32);
                PolicyBinary.WriteString(writer, request.SlotId, 32); PolicyBinary.WriteString(writer, request.Fingerprint, 64);
                writer.Write(request.TargetPeerId); writer.Write(request.TargetCharacter); writer.Write(request.Revision);
                writer.Write(request.Count); writer.Write((int)request.Operation); writer.Flush(); return Finish(stream, 512);
            }
        }

        public static MutationRequest DecodeMutationRequest(byte[] bytes)
        {
            using (var stream = Open(bytes, 512))
            using (var reader = new BinaryReader(stream, Utf8))
            {
                Header(reader, 3);
                var request = new MutationRequest { RequestId = PolicyBinary.ReadString(reader, 32), ViewRequestId = PolicyBinary.ReadString(reader, 32),
                    SlotId = PolicyBinary.ReadString(reader, 32), Fingerprint = PolicyBinary.ReadString(reader, 64),
                    TargetPeerId = reader.ReadInt64(), TargetCharacter = reader.ReadInt64(), Revision = reader.ReadInt64(),
                    Count = reader.ReadInt32(), Operation = (InventoryOperation)reader.ReadInt32() };
                End(stream); ValidateMutation(request); return request;
            }
        }

        public static void ValidateMutation(MutationRequest request)
        {
            if (request == null) throw new InvalidDataException("Missing inventory mutation.");
            PolicyBinary.RequireToken(request.RequestId); PolicyBinary.RequireToken(request.ViewRequestId); PolicyBinary.RequireToken(request.SlotId);
            PolicyBinary.RequireHash(request.Fingerprint);
            if (request.TargetCharacter == 0 || request.Revision < 0 || request.Count < 1 || request.Count > 65535
                || (request.Operation != InventoryOperation.Delete && request.Operation != InventoryOperation.Take))
                throw new InvalidDataException("Invalid inventory mutation fields.");
        }

        // Native adapter calls this on the Unity thread immediately
        // before touching ItemData. A displayed view is not a mutation authority.
        public static ItemInfo RequireCurrentSelection(InventoryView view, MutationRequest request)
        {
            ValidateView(view); ValidateMutation(request);
            if (request.ViewRequestId != view.RequestId || request.TargetPeerId != view.TargetPeerId
                || request.TargetCharacter != view.TargetCharacter || request.Revision != view.Revision)
                throw new InvalidOperationException("Inventory view is stale; refresh before changing items.");
            foreach (ItemInfo item in view.Items)
            {
                if (item.SlotId != request.SlotId) continue;
                if (item.Fingerprint != request.Fingerprint || request.Count > item.Stack)
                    throw new InvalidOperationException("The selected item changed; refresh before changing items.");
                return item;
            }
            throw new InvalidOperationException("The selected item is no longer present.");
        }

        public static string Fingerprint(byte[] completeNativeItemData)
        {
            if (completeNativeItemData == null || completeNativeItemData.Length == 0 || completeNativeItemData.Length > MaximumItemBytes)
                throw new InvalidDataException("Invalid native item data size.");
            return PolicyBinary.Hash(completeNativeItemData);
        }

        private static void ValidatePlayer(PlayerInfo player)
        {
            if (player == null || (player.InventoryAvailable && player.CharacterId == 0)) throw new InvalidDataException("Invalid player identity.");
            Owner(player.OwnerId); PolicyBinary.RequireText(player.Name, 512);
        }
        private static void ValidateView(InventoryView view)
        {
            if (view == null || view.TargetCharacter == 0 || view.Revision < 0 || view.Items == null || view.Items.Count > MaximumItems)
                throw new InvalidDataException("Invalid inventory view.");
            PolicyBinary.RequireToken(view.RequestId); Owner(view.TargetOwner);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (ItemInfo item in view.Items) { ValidateItem(item); if (!ids.Add(item.SlotId)) throw new InvalidDataException("Duplicate inventory item ID."); }
        }
        private static void ValidateItem(ItemInfo item)
        {
            if (item == null) throw new InvalidDataException("Missing inventory item.");
            PolicyBinary.RequireToken(item.SlotId); PolicyBinary.RequireHash(item.Fingerprint);
            PolicyBinary.RequireText(item.Prefab, 256); PolicyBinary.RequireText(item.Name, 512); PolicyBinary.RequireText(item.Container, 128);
            if (item.Prefab.Length == 0 || item.Stack < 1 || item.Stack > 65535 || item.Quality < 1 || item.Quality > 65535
                || item.Variant < 0 || item.X < 0 || item.X > 255 || item.Y < 0 || item.Y > 255
                || Single.IsNaN(item.Durability) || Single.IsInfinity(item.Durability) || item.Durability < 0 || item.Durability > 1000000000f)
                throw new InvalidDataException("Invalid inventory item fields.");
        }
        private static void WriteItem(BinaryWriter writer, ItemInfo item)
        {
            PolicyBinary.WriteString(writer, item.SlotId, 32); PolicyBinary.WriteString(writer, item.Fingerprint, 64);
            PolicyBinary.WriteString(writer, item.Prefab, 256); PolicyBinary.WriteString(writer, item.Name, 512); PolicyBinary.WriteString(writer, item.Container, 128);
            writer.Write(item.Stack); writer.Write(item.Quality); writer.Write(item.Variant); writer.Write(item.X); writer.Write(item.Y);
            writer.Write(item.Durability); writer.Write(item.Equipped);
        }
        private static ItemInfo ReadItem(BinaryReader reader)
        {
            return new ItemInfo { SlotId = PolicyBinary.ReadString(reader, 32), Fingerprint = PolicyBinary.ReadString(reader, 64),
                Prefab = PolicyBinary.ReadString(reader, 256), Name = PolicyBinary.ReadString(reader, 512), Container = PolicyBinary.ReadString(reader, 128),
                Stack = reader.ReadInt32(), Quality = reader.ReadInt32(), Variant = reader.ReadInt32(), X = reader.ReadInt32(), Y = reader.ReadInt32(),
                Durability = reader.ReadSingle(), Equipped = Flag(reader) };
        }
        private static void Owner(string owner) { if (owner != "local-host") PermissionPolicy.RequireSteamOwner(owner); }
        private static void Header(BinaryWriter writer, int kind) { writer.Write(Magic); writer.Write(Version); writer.Write(kind); }
        private static void Header(BinaryReader reader, int kind)
        { if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version || reader.ReadInt32() != kind) throw new InvalidDataException("Unknown inventory wire format."); }
        private static int Count(BinaryReader reader, int maximum)
        { int count = reader.ReadInt32(); if (count < 0 || count > maximum) throw new InvalidDataException("Invalid collection size."); return count; }
        private static bool Flag(BinaryReader reader)
        { byte flag = reader.ReadByte(); if (flag > 1) throw new InvalidDataException("Invalid boolean flag."); return flag != 0; }
        private static MemoryStream Open(byte[] bytes, int maximum)
        { if (bytes == null || bytes.Length < 12 || bytes.Length > maximum) throw new InvalidDataException("Invalid packet size."); return new MemoryStream(bytes, false); }
        private static void End(MemoryStream stream) { if (stream.Position != stream.Length) throw new InvalidDataException("Trailing inventory packet data."); }
        private static byte[] Finish(MemoryStream stream, int maximum)
        { if (stream.Length > maximum) throw new InvalidDataException("Inventory packet exceeds maximum size."); return stream.ToArray(); }
    }
}
