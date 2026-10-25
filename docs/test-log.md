# Test log (2026-10-24/25)

* 23:34 backup of `%USERPROFILE%\AppData\LocalLow\Empyrean\House Flipper Game` → `backups\HFsaves-20261024-2334` (before any test).
* Tests modified, in the real profile: `Profiles\Profile 1\UserSaves\SaveSlot_SmallFirstHouse(.backup)` (moved door mat, cleaned dirt, a duplicated "FrontDoor(Clone)" item, painted wall side) and `PlayerProgress.dat(.backup)` (test balance changes).
* After the last test both game instances were closed, the modified files were copied to `backups\after-testing-modified\` (evidence) and the four files were **restored byte-for-byte from the pre-test backup** (hash-compared: no differences remain in `Profiles` and `Saves`).
* The game was killed (not exited from the menu) between most test runs, which makes the game show "This house did not close properly" – answered with *Continue Game* each time; the restore above removes any residue.
* Config and dev channel were reset to defaults (DevCommands=false, VerboseLog=false, marker file removed) in the installed game before packaging.

## 0.3.0 session (2026-10-25)
* Backups of the modified profile after each test round: `backups\after-testing-modified-5` and `-6`. The original profile/saves were restored byte-for-byte from `backups\HFsaves-20261024-2334` (hash-compared: 0 differences) at the end; `PlayerProgress.before-perks.dat` is the copy taken before 2 perk points per skill were added to the test save.
* Dev marker file, command files, progress/money backups removed; `DevCommands=false`, `VerboseLog=false`.

## Two-machine session (2026-10-25)
* The client machine is a separate clean PC reached through Remote Desktop; files were moved with the RDP clipboard (no SSH, no system settings changed). On it: mod unpacked in `Downloads\hfcoop`, installed into `C:\Program Files (x86)\Steam\steamapps\common\House Flipper`, its profile has no saves of its own.
* The host PC's firewall already allowed House Flipper; the client's join address and longer timeouts were set in its `BepInEx\config\dev.hfcoop.cfg`.

## 0.4.3/0.4.4 session (2026-10-25)
* Two instances on the dev PC, driven with `tools\session.ps1` (now ends in the main house with both connected); the user played both windows and reported what each side showed, logs and `reclists` / `wallsnear` / `probe` / `mounts` dev commands were used to compare peers. Verified: see the 0.4.4 block in `test-checklist.md` and section 12 of `technical-report.md`.
* The laptop was not updated (it was only used for the off-network Steam test); the dev PC runs 0.4.4. Dev marker/command files removed, `VerboseLog=false`, `DevCommands=false`. The profile saves were not restored from backup this time (the host did not save during tests).
