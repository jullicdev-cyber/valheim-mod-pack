// AnyPortal+ additions, 2026-10-06. GPL-3.0, see ../../LICENSE.
using System;
using System.IO;
using UnityEngine;

namespace XPortal.Plus
{
    internal static class PlusPortalMetadata
    {
        internal const string CreatedKey = "AnyPortalPlus_CreatedUtcTicks", IconKey = "AnyPortalPlus_Icon";
        internal static bool ValidIcon(int icon) { return icon == -1 || icon >= 0 && icon <= 3 || icon == 6; }
        internal static void ValidateName(string name)
        {
            if (name == null || name.Length > 256) throw new InvalidDataException("Portal name is too long.");
            for (int i = 0; i < name.Length; ++i)
            {
                char value = name[i];
                if (Char.IsControl(value) || Char.IsLowSurrogate(value)) throw new InvalidDataException("Invalid portal name.");
                if (Char.IsHighSurrogate(value) && (i + 1 == name.Length || !Char.IsLowSurrogate(name[++i])))
                    throw new InvalidDataException("Invalid portal name Unicode.");
            }
        }
        internal static string CleanName(string name)
        {
            if (name == null) return "";
            var result = new System.Text.StringBuilder(256);
            for (int i = 0; i < name.Length && result.Length < 256; ++i)
            {
                char value = name[i];
                if (Char.IsControl(value) || Char.IsLowSurrogate(value)) continue;
                if (Char.IsHighSurrogate(value))
                {
                    if (i + 1 == name.Length || !Char.IsLowSurrogate(name[i + 1]) || result.Length > 254) continue;
                    result.Append(value); result.Append(name[++i]);
                }
                else result.Append(value);
            }
            return result.ToString();
        }
        internal static void Validate(KnownPortal portal)
        {
            if (portal == null || portal.Id.IsNone()) throw new InvalidDataException("Missing portal identity.");
            ValidateName(portal.Name);
            Vector3 point = portal.Location;
            if (!Finite(point.x) || !Finite(point.y) || !Finite(point.z)) throw new InvalidDataException("Invalid portal position.");
            if (portal.CreatedUtcTicks < 0 || portal.CreatedUtcTicks > DateTime.MaxValue.Ticks || !ValidIcon(portal.Icon))
                throw new InvalidDataException("Invalid portal metadata.");
            if (portal.Colour == null || portal.Colour.Length > 128) throw new InvalidDataException("Invalid portal colour.");
        }
        private static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value) && Math.Abs(value) <= 100000; }
        internal static void ReadInto(KnownPortal portal)
        {
            ZDO zdo = ZDOMan.instance == null ? null : ZDOMan.instance.GetZDO(portal.Id);
            if (zdo != null)
            {
                portal.CreatedUtcTicks = zdo.GetLong(CreatedKey, 0);
                if (portal.CreatedUtcTicks < 0 || portal.CreatedUtcTicks > DateTime.MaxValue.Ticks) portal.CreatedUtcTicks = 0;
                portal.Icon = zdo.GetInt(IconKey, -1);
                if (!ValidIcon(portal.Icon)) portal.Icon = -1;
            }
            portal.Biome = WorldGenerator.instance == null ? 0 : (int)WorldGenerator.instance.GetBiome(portal.Location);
        }
        internal static void StampCreation(ZDOID id)
        {
            ZDO zdo = ZDOMan.instance == null ? null : ZDOMan.instance.GetZDO(id);
            if (zdo != null && zdo.GetLong(CreatedKey, 0) == 0) zdo.Set(CreatedKey, DateTime.UtcNow.Ticks);
        }
        internal static void WriteInto(ZDO zdo, KnownPortal portal)
        {
            // The server keeps canonical creation time. Editing an icon or name
            // cannot rewrite it, and migration does not invent old timestamps.
            if (zdo.GetLong(CreatedKey, 0) == 0 && portal.CreatedUtcTicks > 0) zdo.Set(CreatedKey, portal.CreatedUtcTicks);
            zdo.Set(IconKey, portal.Icon);
        }
    }
}
