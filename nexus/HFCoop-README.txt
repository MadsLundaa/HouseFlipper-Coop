House Flipper Co-op - manual install (Nexus package)

1. Close House Flipper and back up your saves:
   %USERPROFILE%\AppData\LocalLow\Empyrean\House Flipper Game
2. Extract everything in this zip into the game folder, the one that contains HouseFlipper.exe
   (Steam: right-click House Flipper > Manage > Browse local files).
   You should end up with winhttp.dll and a BepInEx folder next to HouseFlipper.exe.
   If you already have BepInEx 5 installed, you only need BepInEx\plugins\HFCoop\HFCoop.dll.
3. Start the game. Keys: F5 host, F6 join, F7 co-op panel, F4 leave.

Uninstall: delete BepInEx\plugins\HFCoop. To remove BepInEx too, also delete winhttp.dll,
doorstop_config.ini, .doorstop_version, changelog.txt and the BepInEx folder. Saves are never touched.

Everyone in a session needs the same game build and the same mod version.
Source, full readme and an installer version: https://github.com/MadsLundaa/HouseFlipper-Coop
