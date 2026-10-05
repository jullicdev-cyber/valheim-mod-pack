// AnyPortal+ fork changes, 2026-10-06. Original XPortal by SpikeHimself; GPL-3.0.
using UnityEngine;
using XPortal.Extension;

namespace XPortal
{
    public class KnownPortal
    {
        public ZDOID Id { get; set; }
        public string Name { get; set; }
        public ZDOID PreviousId { get; set; }
        public ZDOID Target { get; set; }
        public Vector3 Location { get; set; }
        public string Colour { get; set; }
        public long CreatedUtcTicks { get; set; }
        public int Biome { get; set; }
        public int Icon { get; set; } = -1;

        public bool IsDefaultPortal
        {
            get
            {
                return Location.Round().Equals(XPortalConfig.Instance.Local.DefaultPortal.Value.Round());
            }
        }

        public KnownPortal(ZDOID id)
        {
            Id = id;
            Name = string.Empty;
            Location = Vector3.zero;
            PreviousId = ZDOID.None;
            Target = KnownPortalsManager.Instance.FindDefaultPortal();
            Colour = PortalColour.GetPortalColour(id);
            Plus.PlusPortalMetadata.ReadInto(this);
        }

        public KnownPortal(ZDOID id, Vector3 location) : this(id)
        {
            Location = location;
            Plus.PlusPortalMetadata.ReadInto(this);
        }

        public KnownPortal(ZPackage pkg)
        {
            if (pkg == null || pkg.Size() > 4096) throw new System.IO.InvalidDataException("Portal data exceeds its size limit.");
            Id = pkg.ReadZDOID();
            Name = pkg.ReadString();
            Location = pkg.ReadVector3();
            PreviousId = pkg.ReadZDOID();
            Target = pkg.ReadZDOID();
            Colour = pkg.ReadString();
            if (pkg.ReadInt() != 1) throw new System.IO.InvalidDataException("AnyPortal+ metadata protocol mismatch.");
            CreatedUtcTicks = pkg.ReadLong(); Biome = pkg.ReadInt(); Icon = pkg.ReadInt();
            Plus.PlusPortalMetadata.Validate(this);
            if (pkg.GetPos() != pkg.Size()) throw new System.IO.InvalidDataException("Trailing portal data.");
        }

        public string GetFriendlyName()
        {
            var portalName = Name;
            if (string.IsNullOrEmpty(portalName))
            {
                return Localization.instance.Localize("$piece_portal_tag_none");  // "(No Name)"
            }
            else
            {
                return portalName;
            }
        }

        public string GetFriendlyTargetName()
        {
            if (!HasTarget())
            {
                return Localization.instance.Localize("$piece_portal_target_none");   // "(None)"
            }

            if (!KnownPortalsManager.Instance.ContainsId(Target))
            {
                return $"{Target} (invalid)";
            }

            return KnownPortalsManager.Instance.GetKnownPortalById(Target).GetFriendlyName();
        }

        public bool HasTarget()
        {
            return Target != null && Target != ZDOID.None && !Target.IsNone();
        }

        public ZPackage Pack()
        {
            var pkg = new ZPackage();
            pkg.Write(Id);
            pkg.Write(Name);
            pkg.Write(Location);
            pkg.Write(PreviousId);
            pkg.Write(Target);
            pkg.Write(Colour);
            Plus.PlusPortalMetadata.Validate(this);
            pkg.Write(1); pkg.Write(CreatedUtcTicks); pkg.Write(Biome); pkg.Write(Icon);
            return pkg;
        }

        public bool Targets(ZDOID target)
        {
            return Target == target;
        }

        public override string ToString()
        {
            return $"{{ Id: `{Id}`, Name; `{GetFriendlyName()}`, Location: `{Location}`, Target: `{Target}` (`{GetFriendlyTargetName()}`), Colour: `{Colour}` }}";
        }
    }
}
