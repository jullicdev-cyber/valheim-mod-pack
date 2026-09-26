# Automatic Fuel

### Updated for Valheim 1.0

# Some notes for 1.0  There was a rather large change in the code base for 1.0  As a result you will notice some additional logging.  I will turn that off later but it is will help if there are issues reported.

This has been running stable on my build but please let me know if you encounter any errors.


Fuels torches, campfires, windmills, spinning wheels, hearths, kilns and smelters
- Fuels from the ground
- Fuels from chests and custom chests
- Stack Smelters - Default is True:  Removes most smoke and isblocked check
- Remove the ServerSync Version and Client checks.  Default is True and does check.  Can be turned off in the config.
- Blast Furnace can take all Fuel - NEW - 

I'll do my best to help with any issues.  

[Reach Me on Discord](https://discord.gg/z5NrSZqP)
### About the Mod:
__________________


#### now with ServerSync

#### Mistlands Compatible

This mod is a combination of my own code and scaled down version of Aedenthorn's AutoFuel mod.  

Credit goes to Aedenthorn for the idea and original mod.

--------------------

Fuels torches, campfires, windmills, spinning wheels, hearths, kilns and smelters from items in chests or off the ground

`A config file BepInEx/config/tastychickenlegs.automaticfuel.cfg is created after running the game once with this mod)`

![ScreenShot](https://i.ibb.co/CBRDPKZ/spin.png)

You can adjust the config values by editing this file using a text editor or in-game using the Config Manager﻿.

|Config Option|Definition
|---|---|
|verifyClient| This turns on and off the need to verify other clients.  Users can now play without the mod on a server with the mod.|
|fireplaceRange| The maximum range to pull fuel from containers for fireplaces|
|smelterOreRange| The maximum range to pull fuel from containers for smelters|
|smelterFuelRange| The maximum range to pull ore from containers for smelters|
|fuelDisallowTypes| Types of item to disallow as fuel (i.e. anything that is consumed), comma-separated.|
|oreDisallowTypes| Types of item to disallow as ore (i.e. anything that is transformed), comma-separated).|
|refuelStandingTorches| Refuel standing torches|
|refuelWallTorches| Refuel wall torches|
|refuelFirePits| Refuel fire pits|
|restrictKilnOutput| Restrict kiln output|
|restrictKilnOutputAmount| Amount of coal to shut off kiln fueling|
|distributedFilling| If true, refilling will occur one piece of fuel or ore at a time, making filling take longer but be better distributed between objects|
|leaveLastItem| Don't use last of item in chest|
|StackSmelters| Allow the ability to stack smelters and kilns.  Turns off the smoke and prohibits the blocked smoke check.|
|RestrictKiln| Turn off the Kiln|
|RefuelHotTub| Turn on and off refueling of hottub.. bathtub whatever|
|RefuelHearth| Turn on and off the refueling of the hearth|

Custom Toggle key to turn on and off mod in-game. 

You can adjust the ranges for containers and ground pulling (default 10 meters each).

Reset the config by opening the in-game console `(F5)` and typing autofuel reset and pressing Enter.

___________________________
#### Installation: (manual)  

Extract DLL from zip file into `"<GameDirectory>\Bepinex\plugins"`  
Start the game.
___________________________
#### Installation (Automatic)
Use the R2Modmanager on Thunderstore.  Search for the mod and install
___________________________

#### Server Configuration
``````
For Servers that want to control ther configuration, install on the server and clients.  
The server config will push down.

For Servers that cannot use mods, simply install this on the clients only and it will work just fine.  Clients will all need
the same settings in their config files for optimal results.

For people that run a server and don't want to verify clients turn the "verifyclients" setting off.
``````
### Version Information
___________________________

1.5.1

- updated serversync libraries for multiplayer support


1.5.0

- updated for Valheim 1.0  
- There is some additional logging turned on and will be turned off in the next build.
- recoded methods for adding fuel.  


1.4.8

- no changes made other than turning off some console logging


1.4.7

option to disable blast furnace and smelter default is on
Added ability to turn off using floor drops for fuel

1.4.6

Updated for newest patch and added shield generators

1.4.5

Updated server sync

1.4.4

Updated for The Bog Witch version 


1.4.3

Updated for newest version
Added Iron Ore to the list of items to be made in Blast Furnace
-- Thanks EiraValkyrie for reporting the problem

1.4.2

removed for errors in build


1.4.1 

Updated for final release of Ashlands.



1.4.0

updated for Valheim 0.218.12  Ashlands PTB  

THIS VERSION WILL NOT WORK IN ANYTHING BELOW 0.218
Use the older version for non Ashalands Versions.



1.3.9

- updated for Valheim 0.271.22


1.3.8

- Updated to the newest Haldor's Quest Version

1.3.7

- Updated to newest Test Branch of Valheim 
- Increased amount of selectable coal before turning off kilns
- fixed a few spelling errors



1.3.6

- Updated to newest BepInEx and Valheim Patch 0.214.300



1.3.5

- updated to the newest version of Valheiml 0.214.2
- added back the toggle to turn off and on Automatic Fueling.

1.3.4 - fixed several RPC bugs and bumped version for incorrect README.MD


1.3.0

- add ability to turn off Spinning Wheel and Windmills


1.2.0

- Added ability to turn off Hearth and BathTub.  This allows the mod to coexists with other mods like BetterWards 

1.1.9

- Fixed bug in turning off Kilns.  When kilns were turned off all "Smelters" stopped working.


1.1.8

- Option to turn off Kilns
- Ability to pull from carts
- Added ownership checks to containers for multiplayer duping


1.1.7

- Added option for Blast Furnace to take all fuel.  Default is on.
- Lots of code cleanup.

1.16

- Fix for smelter oject not set to an instance error.  Widmills and Spinning Wheels are smelters and they were causing errors when checking their smoke.


1.15

- Enabled the ability to turn off the verification of clients.  This keeps the server from kicking players that do not have the mod.
- Added the ability to stack smelters and kilns.  

1.1.4

- Added ServerSync - Install on the Clients and Server and the Server will control the config.
- fine tuned the container code to include custom chests and mods
- confirmed working with Drawers Mod.

1.1.3

- Fixed the black iron chests and custom chest.  Can now pull from all containers
- Confirmed working with Eitr Refinery

1.1.2

- Fixed the toggle key

1.1.0

- added the options for torches, campfires and hearths

1.0.1

- initial release

##	Now for the shameless plug

> ### My Other Mods:
>>* [No Smoke Stay Lit](https://valheim.thunderstore.io/package/TastyChickenLeg/NoSmokeStayLit/)
>>* [No Smoke Simplified](https://valheim.thunderstore.io/package/TastyChickenLegs/NoSmokeSimplified/)
>>* [Honey Please](https://valheim.thunderstore.io/package/TastyChickenLegs/HoneyPlease/)
>>* [Automatic Fuel](https://valheim.thunderstore.io/package/TastyChickenLeg/AutomaticFuel/)
>>* [Forsaken Powers Plus](https://valheim.thunderstore.io/package/TastyChickenLeg/ForsakenPowersPlus/)
>>* [Recycle Plus](https://valheim.thunderstore.io/package/TastyChickenLeg/RecyclePlus/)
>>* [Blast Furnace Takes All](https://valheim.thunderstore.io/package/TastyChickenLeg/BlastFurnaceTakesAll/)
>>* [Timed Torches Stay Lit](https://valheim.thunderstore.io/package/TastyChickenLeg/TimedTorchesStayLit/)
>>* [Drop More Loot](https://valheim.thunderstore.io/package/TastyChickenLegs/DropMoreLoot/)