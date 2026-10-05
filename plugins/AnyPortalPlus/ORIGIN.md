# AnyPortal+ source and license

AnyPortal+ is a modified fork of [XPortal](https://github.com/SpikeHimself/XPortal), originally maintained by SpikeHimself. It implements the requested AnyPortal-style portal selection using the current XPortal code already installed in this modpack. It is not an official upstream release.

- Upstream release: **v1.2.25**.
- Pinned upstream commit: **360eccc528f87c137b07dff9905800159e90d8dc**.
- Original source: [the pinned revision](https://github.com/SpikeHimself/XPortal/tree/360eccc528f87c137b07dff9905800159e90d8dc).
- Fork changes dated **2026-10-06**: portal search, ascending/descending sorting, biome grouping, portal icons, personal tracked map pins, safe map-pin cleanup, input lifecycle fixes and RPC validation.
- License: **GNU General Public License version 3**, reproduced unchanged in `LICENSE`. Existing source notices and attribution are retained.
- Complete corresponding fork source and build scripts: [plugins/AnyPortalPlus in the modpack repository](https://github.com/jullicdev-cyber/valheim-mod-pack/tree/main/plugins/AnyPortalPlus).

The plugin continues to use GUID `yay.spikehimself.xportal`, assembly/namespace `XPortal`, and the original portal ZDO keys so existing world links and Portal Finder integrations remain usable. The displayed name is AnyPortal+. It replaces the upstream XPortal DLL; it must not be installed alongside a second copy of XPortal or the archived AnyPortal plugin.

`Resources/Translations` contains the upstream translations from the pinned revision. `Resources/LICENSE` and `Resources/ORIGIN.md` are included beside the installed DLL. Every distributed resource is listed with a SHA-256 hash in `mods.lock.json`; clean pack builds copy those exact files.

Valheim, Unity, BepInEx, Harmony and Jotunn assemblies are build/runtime dependencies. Their game assemblies and temporary publicized build references are not included in this fork's corresponding source or redistributed as game replacements.

Update AnyPortal+ through this modpack's install/update scripts. Its upstream Nexus update check is disabled to avoid replacing the fork with an upstream DLL that lacks these features.
