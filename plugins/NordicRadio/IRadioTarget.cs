using UnityEngine;
namespace ValheimModPack.NordicRadio
{
    // Both world furniture and a carried idol use the same controls and decoder.
    public interface IRadioTarget
    {
        ZDOID Id { get; }
        bool IsReady { get; }
        Vector3 SoundPosition { get; }
        bool HasAccess(Player player);
        string GetHoverName();
        void SetLit(bool on);
    }
}
