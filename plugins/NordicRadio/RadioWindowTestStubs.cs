// Only compiled by Test-UI.ps1. Game and Jotunn types remain real references;
// these stubs isolate RadioWindow from the other new plugin components.
using System.Collections.Generic;
using UnityEngine;

namespace ValheimModPack.NordicRadio
{
    public sealed class Plugin
    {
        public RadioService Service;
        public float PersonalVolume;
    }
    public interface IRadioTarget
    {
        ZDOID Id { get; }
        bool IsReady { get; }
        Vector3 SoundPosition { get; }
        bool HasAccess(Player player);
        string GetHoverName();
        void SetLit(bool on);
    }
    public sealed class RadioPiece : MonoBehaviour, IRadioTarget
    {
        public ZDOID Id { get; set; }
        public bool IsReady { get; set; }
        public Vector3 SoundPosition { get { return transform.position; } }
        public bool HasAccess(Player player) { return IsReady && player != null; }
        public string GetHoverName() { return "Skald's Horn"; }
        public void SetLit(bool on) { }
    }
    public sealed class TrackInfo
    {
        public string Id;
        public string Title;
        public long Size;
        public double Duration;
    }
    public sealed class RadioSnapshot
    {
        public string TrackId;
        public bool Playing;
        public double StartedAt;
        public float Offset;
        public float Volume;
        public bool Repeat;
        public bool Shuffle;
        public int Revision;
    }
    public sealed class RadioService
    {
        public IList<TrackInfo> Tracks;
        public string LibraryStatus;
        public RadioSnapshot GetState(ZDOID id) { return null; }
        public void Command(ZDOID id, string command, string trackId, float value) { }
        public void RefreshLibrary() { }
        public string Status(string trackId) { return ""; }
    }
}
