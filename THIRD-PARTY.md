# Third-party components

* **BepInEx 5.4.23.3** (LGPL-2.1, https://github.com/BepInEx/BepInEx) and its bundled HarmonyX / MonoMod: not stored in this repository. The release zip bundles the unmodified BepInEx x64 build so players do not need a separate download; the installer lists exactly which files it adds and `Uninstall.bat` removes them.
* **Game assemblies** (Assembly-CSharp, UnityEngine, Newtonsoft.Json, Steamworks.NET from *House Flipper*): referenced at build time from your own game installation, never copied into this repository or the release.
* This is an unofficial fan mod. *House Flipper* is a trademark of Empyrean; no game code, assets or decompiled sources are distributed here.
