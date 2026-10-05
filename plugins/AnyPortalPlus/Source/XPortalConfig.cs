// AnyPortal+ fork changes, 2026-10-06. Original XPortal by SpikeHimself; GPL-3.0.
using BepInEx.Configuration;
using System;
using UnityEngine;
using XPortal.RPC;

namespace XPortal
{
    internal sealed class XPortalConfig
    {
        ////////////////////////////
        //// Singleton instance ////
        private static readonly Lazy<XPortalConfig> lazy = new Lazy<XPortalConfig>(() => new XPortalConfig());
        public static XPortalConfig Instance { get { return lazy.Value; } }
        ////////////////////////////

        public event Action OnLocalConfigChanged;
        public event Action OnServerConfigChanged;

        private const string Desc_EnforcedByServer = " This setting is enforced (but not overwritten) by the server.";

        private ConfigFile configFile;

        /// <summary>
        /// Container class for all of XPortal's config settings
        /// </summary>
        public class ConfigSettings
        {
            public bool PingMapDisabled;
            public bool DisplayPortalColour;
            public bool DoublePortalCosts;
            public ConfigEntry<Vector3> DefaultPortal;
            public bool HidePortalDistance;
            public ConfigEntry<KeyboardShortcut> PreviousPortalShortcut;
            public ConfigEntry<KeyboardShortcut> NextPortalShortcut;
        }

        /// <summary>
        /// Track local config settings
        /// </summary>
        public ConfigSettings Local { get; set; }

        /// <summary>
        /// Track Server config settings
        /// </summary>
        public ConfigSettings Server { get; set; }

        private XPortalConfig()
        {
            Local = new ConfigSettings();
            Server = new ConfigSettings();
        }

        internal void BeginSession()
        {
            // Awake may have run in the main menu before the host role existed.
            // Guests must not carry another world's server settings into this session.
            Server = Environment.IsServer ? Local : new ConfigSettings();
            OnServerConfigChanged?.Invoke();
        }

        /// <summary>
        /// Load the config file, and track the settings inside it
        /// </summary>
        /// <param name="configFile">The config file being loaded</param>
        public void LoadLocalConfig(ConfigFile configFile)
        {
            this.configFile = configFile;
            ReloadLocalConfig();

            this.configFile.ConfigReloaded += LocalConfigChanged;
            this.configFile.SettingChanged += LocalConfigChanged;

            if (Environment.IsServer)
            {
                Server = Local;
            }
        }

        /// <summary>
        /// Reload the settings inside the config file
        /// </summary>
        private void ReloadLocalConfig()
        {
            // AnyPortal+ is distributed through this pack, not the upstream Nexus release.
            var nexusId = configFile.Bind("General", "NexusID", Mod.Info.NexusId, "AnyPortal+ has no independent Nexus release; leave at zero.");
            if (nexusId.Value != 0) nexusId.Value = 0;

            // Add PingMapDisabled option which disables the Ping Map button
            var cfgPingMapDisabled = configFile.Bind("General", "PingMapDisabled", false, "Disable the Ping Map button completely. For players who wish to play without a map." + Desc_EnforcedByServer);
            Local.PingMapDisabled = cfgPingMapDisabled.Value;

            var cfgDisplayPortalColour = configFile.Bind("General", "DisplayPortalColour", false, "Show a \">>\" tag in the list of portals that has the same colour as the light that the portal emits (integration with \"Advanced Portals\" by RandyKnapp).");
            Local.DisplayPortalColour = cfgDisplayPortalColour.Value;

            var cfgDoublePortalCosts = configFile.Bind("General", "DoublePortalCosts", false, "By using XPortal, you effectively only need half the amount of portals. To compensate for that, we can double the costs of portals." + Desc_EnforcedByServer);
            Local.DoublePortalCosts = cfgDoublePortalCosts.Value;

            Local.DefaultPortal = configFile.Bind("General", "DefaultPortal", Vector3.zero, "The Portal that newly built Portals immediately connect to.");

            var cfgHidePortalDistance = configFile.Bind("General", "HidePortalDistance", false, "In the list of portals, do not show how far away other portals are." + Desc_EnforcedByServer);
            Local.HidePortalDistance = cfgHidePortalDistance.Value;
            Local.PreviousPortalShortcut = configFile.Bind("Controls", "PreviousPortal", new KeyboardShortcut(KeyCode.UpArrow),
                "Select the previous portal while the portal list has keyboard focus. Rebind through Bindrune.");
            Local.NextPortalShortcut = configFile.Bind("Controls", "NextPortal", new KeyboardShortcut(KeyCode.DownArrow),
                "Select the next portal while the portal list has keyboard focus. Rebind through Bindrune.");
        }

        /// <summary>
        /// The config file was reloaded or a setting was changed.
        /// If we are the server, sync the config to clients.
        /// </summary>
        private void LocalConfigChanged(object sender, EventArgs e)
        {
            ReloadLocalConfig();

            if (Environment.IsServer)
            {
                Server = Local;
                Log.Debug("The config was changed, propagating to clients..");
                SendToClient.Config(PackLocalConfig());
                OnServerConfigChanged?.Invoke();
            }

            OnLocalConfigChanged?.Invoke();
        }

        /// <summary>
        /// Wrap the config settings into a package
        /// </summary>
        /// <returns>A ZPackage containing all config settings</returns>
        public ZPackage PackLocalConfig()
        {
            var pkg = new ZPackage();
            pkg.Write(Local.PingMapDisabled);
            pkg.Write(Local.DoublePortalCosts);
            pkg.Write(Local.HidePortalDistance);
            return pkg;
        }

        /// <summary>
        /// Set our config settings based on the package we received from the server
        /// </summary>
        /// <param name="pkg">A ZPackage containing all config settings</param>
        public void ReceiveServerConfig(ZPackage pkg)
        {
            if (pkg == null || pkg.Size() != 3) throw new System.IO.InvalidDataException("Invalid portal config packet.");
            bool pingDisabled = pkg.ReadBool(), doubleCosts = pkg.ReadBool(), hideDistance = pkg.ReadBool();
            Server.PingMapDisabled = pingDisabled;
            Server.DoublePortalCosts = doubleCosts;
            Server.HidePortalDistance = hideDistance;

            Log.Debug($"PingMapDisabled {{ Local: {Local.PingMapDisabled}, Server: {Server.PingMapDisabled} }}");
            Log.Debug($"DoublePortalCosts {{ Local: {Local.DoublePortalCosts}, Server: {Server.DoublePortalCosts} }}");
            Log.Debug($"HidePortalDistance {{ Local: {Local.HidePortalDistance}, Server: {Server.HidePortalDistance} }}");

            OnServerConfigChanged?.Invoke();
        }
    }
}
