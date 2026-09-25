![BepInEx logo](https://avatars2.githubusercontent.com/u/39589027?s=256)

Due to Thunderstore limitations, if there is information that needs to be live updated or shared about this pack it will be listed on the [Wiki tab](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/wiki/) of the package. If you need to have something listed there, please contact one of the maintainers.

# BepInExPack for Valheim

This is [BepInEx 5.4.23.5](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) pack but pre-configured for Valheim. It is maintained by Azumatt, Vapok, and Margmas in the Valheim community. The specific changes and deviations from upstream BepInEx can be see in [Azumatt's Repo](https://github.com/AzumattDev/BepInEx). If changes to the pack need to be made but BepInEx itself doesn't need to update typically we will increment the version for Thunderstore. Example: `5.4.2202`, this does not mean that BepInEx itself needed to update, likely it's a small change for the game or something needed to change in the description etc.

BepInEx is a general purpose framework for Unity modding.
BepInEx includes tools and libraries to

* load custom code (hereafter *plugins*) into the game on launch;
* patch in-game methods, classes and even entire assemblies without touching original game files;
* configure plugins and log game to desired outputs like console or file;
* manage plugin dependencies.

BepInEx is currently [one of the most popular modding tools for Unity on GitHub](https://github.com/topics/modding?o=desc&s=stars).

### This pack's contents

This pack is preconfigured and usable for Valheim modding.  
In particular, the changes from base BepInEx releases are:

* Added preconfigured `BepInEx.cfg` with console enabled and correct entry point for mods.
* Added scripts necessary to run both game and dedicated server on Linux machines or through r2modman
* Edited BepInEx preloader to help with some mods patching
* Force the Valheim Game class isModded flag to true after the chainloader loads.
* Thunderstore version of the pack is printed to the console since it's separate from the actual BepInEx version if only site changes are needed.
* Plugins can use semver version strings like `1.2.0-beta.1`. Base BepInEx 5 throws those plugins out with an "invalid version" warning.

## Issues, questions, etc.
🔴IMPORTANT❗🔴
**DO NOT ASK FOR HELP in the _Official BepInEx Discord_**

The Valheim community runs a customized version of BepInEx and the official BepInEx folks are unable to help.

If you are having problems with BepInEx in general, mods running on Valheim, or unsure between the two, please seek assistance from ANY of the Discords of the maintainers listed below.

### Current BepInEx for Valheim Maintainers:
* [Azumatt](https://discord.gg/pdHgy6Bsng)

### Installation (game, automated)

This is the recommended way to install BepInEx on the game.

1. Download and install [Gale](https://thunderstore.io/c/valheim/p/Kesomannen/GaleModManager/), [Thunderstore Mod Manager](https://www.overwolf.com/app/Thunderstore-Thunderstore_Mod_Manager) or [r2modman](https://valheim.thunderstore.io/package/ebkr/r2modman/)
2. Click **Install with Mod Manager** button on top of the page
3. Run the game via the mod manager

### Installation (server, automated)

There are some managed dedicated server that support simple and automatic BepInEx installation.  
Here is a list of known ones (list updated as more is known).  
*Note: Some of the links may be affiliate links*

[![Valhost.net](https://valtools.org/resources/images/ValHost_BepInExPack.png)](https://valhost.net/?ref=30893)

[![ValheimServerHosting](https://i.imgur.com/w21YSPf.jpg)](https://valheimserverhosting.com/?via=bepinex)

[![XGamingServer](https://i.imgur.com/sRUEoy0.png)](https://link.xgamingserver.com/BepInEx)


### Installation (manual)

If you are installing this manually, do the following

1. Start with a clean install. Make triple sure your Valheim folder is *clean and unmodified*
2. Click the Manual Download button on the package page above to download the archive, it looks like this: 

    ![Manual Download Button](https://valtools.org/resources/images/manualdownloadbutton.png)
3. Once saved to your computer, extract the archive into a folder. **Do not extract into the game folder.**
4. Move the contents of `BepInExPack_Valheim` folder into `<Steam Location>\steamapps\common\Valheim`.

    ![Moving contents of BepInExPack_Valheim](https://valtools.org/resources/images/DragNDrop_ManualInstall.png)
5. Check that you have installed it correctly. 
    If done correctly, your folder will look as follows

    ![BepInEx, winhttp.dll, and doorstop_config.ini in Valheim folder.](https://valtools.org/resources/images/Highlighted.png)
5. Follow either Windows or Linux game running instructions below:

#### Configuration (Windows)

No need to configure. Simply run the game. If everything is correct, you will see a console pop up.

#### Configuration (Linux, game)

1. Make `start_game_bepinex.sh` executable with `chmod u+x start_game_bepinex.sh`.
2. In Steam, go to game's properties and set game's launch arguments to
    
	```
	./start_game_bepinex.sh %command%
	```
	
	Note to advanced users: You can change `./start_game_bepinex.sh` to full path to the script. This way you can install this pack entirely outside of game folder.
3. Run the game via Steam.

At this moment you will not see any clear indication if BepInEx works.
It is suggested to test by installing a simple plugin such as [MessageCenter from BepInEx.Utility](https://github.com/BepInEx/BepInEx.Utility).

#### Configuration (Linux, server)

This guide assumes you have enough knowledge working with Linux.  
If not, it is suggested you use a server host that supports it out of the box.

1. Make `start_server_bepinex.sh` executable with `chmod u+x start_server_bepinex.sh`.
2. Edit `start_server_bepinex.sh` to change the launch parameter like you would with Valheim's own launch script.
3. Run `start_server_bepinex.sh` to start the server.

If done correctly, you will see BepInEx bootstrap messages in the terminal.

#### Configuration (macOS)

Valheim has a native macOS build, but BepInEx does not run natively on Apple Silicon yet, so the game has to be forced through Rosetta.

1. Make `start_game_bepinex.sh` executable with `chmod u+x start_game_bepinex.sh`.
2. In Steam, go to game's properties and set game's launch arguments to

	```
	/usr/bin/arch -x86_64 /bin/bash ./start_game_bepinex.sh %command%
	```
3. Run the game via Steam.

If you leave off the `arch -x86_64` part on an M-series Mac, the game still launches, it just launches without BepInEx and none of your mods load. Intel Macs don't need it.

This is not something we can fix in the pack. BepInEx 5 leans on MonoMod, and MonoMod has no arm64 support yet. If you want to follow along, it's [BepInEx#899](https://github.com/BepInEx/BepInEx/issues/899).


### Checking BepInEx version and load status

If BepInEx was loaded successfully into the game, you should see a BepInEx console window launched next to the game in the taskbar (Windows):

Stacked:
![BepInEx console window in taskbar](https://valtools.org/resources/images/ConsoleWindowTaskbar.png)

Expanded:
![BepInEx console window in taskbar 2](https://valtools.org/resources/images/ConsoleWindowTaskbar2.png)

Confirm no errors or issues by having Grey/White text in the console only (Info and Message). Red are fatal errors, Dark Red are errors, Yellow are warnings, Dark Gray are debug, and any other color is a custom message.

![BepInEx console window](https://valtools.org/resources/images/ConsoleWindow.png)

### Useful links

* [BepInEx: writing basic plugin walkthrough](https://docs.bepinex.dev/articles/dev_guide/plugin_tutorial/index.html)
* [BepInEx: useful plugins for modding](https://docs.bepinex.dev/articles/dev_guide/dev_tools.html)
* [BepInEx: patching game methods at runtime](https://docs.bepinex.dev/articles/dev_guide/runtime_patching.html)
* [Valheim Modding Discords](https://valtools.org/wiki.php?page=Modding-Discords)
* [Valheim Wiki](https://valtools.org/wiki.php) or [Valheim Modding Wiki source](https://github.com/Valheim-Modding/Wiki/wiki/Modding-Discords)
