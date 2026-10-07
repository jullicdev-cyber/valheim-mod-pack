using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace ValheimModPack.WorldCharacters
{
    internal static class GameMapCapture
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo Own = typeof(Minimap).GetField("m_explored", Fields);
        private static readonly FieldInfo Shared = typeof(Minimap).GetField("m_exploredOthers", Fields);
        private static readonly FieldInfo Pins = typeof(Minimap).GetField("m_pins", Fields);

        // Game-thread read only. This deliberately observes the fields themselves:
        // shared exploration and mods can change pins without native event hooks.
        internal static MapCaptureSnapshot TryRead(Minimap map, ZNet network)
        {
            try
            {
                if (!MapCaptureCompatibility.CanSkipNativeCapture()) return null;
                return TryReadFields(map, network);
            }
            catch (Exception) { return null; }
        }

        internal static MapCaptureSnapshot TryReadFields(Minimap map, ZNet network)
        {
            try
            {
                if (map == null || network == null || Own == null || Shared == null || Pins == null) return null;
                BitArray own = Own.GetValue(map) as BitArray, shared = Shared.GetValue(map) as BitArray;
                List<Minimap.PinData> pins = Pins.GetValue(map) as List<Minimap.PinData>;
                if (own == null || shared == null || pins == null || own.Length != shared.Length) return null;
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    int count = 0;
                    foreach (Minimap.PinData pin in pins) if (pin.m_save) ++count;
                    writer.Write(count);
                    foreach (Minimap.PinData pin in pins)
                    {
                        if (!pin.m_save) continue;
                        writer.Write(pin.m_name);
                        writer.Write(pin.m_pos.x); writer.Write(pin.m_pos.y); writer.Write(pin.m_pos.z);
                        writer.Write((int)pin.m_type); writer.Write(pin.m_checked);
                        writer.Write(pin.m_ownerID); writer.Write(pin.m_author.ToString());
                    }
                    writer.Flush();
                    return new MapCaptureSnapshot(map, map.m_textureSize, own, shared, stream.ToArray(), network.IsReferencePositionPublic());
                }
            }
            catch (Exception)
            {
                // Unknown native layout or an invalid field must never make the
                // optimization skip map data; use the existing native save path.
                return null;
            }
        }
    }
}
