# Test checklist (what was actually verified)

Setup for all "verified" items: two real House Flipper processes on one Windows 11 PC (instance 1 via Steam = host, instance 2 started directly = client, same save/profile), house *SmallFirstHouse*, build 1.26252, base game, loopback TCP. Logs from both instances were read to confirm. **"Dev" = triggered through the plugin's dev command channel, which calls the same game code/events a player action ends in, not a real mouse/keyboard player action.** Nobody else's machine, no real internet link and no more than 2 players were tested.

Legend: ✅ verified · 🟡 verified via dev trigger only · ❌ not working/not implemented · ⬜ not tested

## Milestones
| # | Item | Status | Evidence |
|---|---|---|---|
| 1 | Plugin loads in the original game | ✅ | BepInEx log "House Flipper Co-op loaded", Unity 2019.4.39 |
| 2 | Real network connection between two game instances | ✅ | host log "player <name> (#1) joined", client "connected, my id #1" |
| 3 | Remote player visible in the same house | ✅ | screenshots: host sees blue capsule, client sees red capsule |
| 4 | Movement sync | ✅ (client walked, host/client avatars follow); rotation/pitch included; ⬜ smoothness not measured |
| 5 | One real renovation action both directions | 🟡 | item moved host→client and client→host; dirt cleaned both ways |
| 6 | More systems + shared progression | see below |
| 7 | Join an active session with prior changes | ✅ | host changed doormat + cleaned 3 dirt before the client joined; after join client had doormat at new position and 27/30 dirt; works again on rejoin |
| 8 | Reliable shared save, disconnect recovery, errors | partly, see below |
| 9 | Compatibility/regression/perf | partial, see below |

## Gameplay sync
| Feature | Status | Notes |
|---|---|---|
| Item move | 🟡 | `OnBoughtOrModifiedItem` fired through the recorder handler |
| Item sell/dispose | 🟡 | `ItemsCoroutineManager.DisposeTrash` (real game code path); item removed on both (183/183 items) |
| Item buy / created items | 🟡 | duplicated an item via the game's prefab lookup + same hook; created on client at same position. Real tablet purchase ⬜ (placement mode reached, not completed) |
| Item damage | ⬜ | hooked, never triggered |
| Dirt cleaning | 🟡 | `Dirt.ForceClean` (raises the game's `OnCleaned`), both directions, counts 30→29→28 on both |
| Window cleaning/dirtying | ⬜ | tracked (`cleanedWindowsGuids`, `extendedDirts`), not triggered |
| Wall paint/wallpaper | 🟡 | `WallSide.FinalizePaintProcess` + recorder; with a registered material guid the client wall changed to the same material. With a non-registered material the game itself falls back to white (game behaviour) |
| Floors / tiles / ceilings / plaster / nests | ⬜ | tracked through the same mechanism, never triggered |
| Demolition / construction | ⬜ experimental | `modifiedWalls/OuterWalls` tracked; architect/demolish never run |
| Doors/windows/drawers/switches | 🟡 | `MovablePart.Interact` → client received and applied; visual change observed on the door; state can diverge if both toggle simultaneously |
| Mission/task/order progress, house appraisal | ❌ | not shared (per-profile) |
| Money (host→client, client→host) | ✅ | +1000 on host → client; +500 on client → host; both show 245,580 etc. (internal units). Real purchase deduction ⬜ |
| Tool state | 🟡 | remote tag shows tool class name; not checked while switching tools |
| Furniture rotation/placement via real hands | ⬜ | |

## Sessions, UI, errors
| Item | Status |
|---|---|
| Host via F5, join via F6 | ✅ |
| Panel (F7): status, player list, Disconnect, Join button | ✅ (clicked Join in the panel; list shows both players) |
| Typing an IP into the panel text field | ⬜ (default 127.0.0.1 used) |
| Clean disconnect (F4) → host removes player | ✅ |
| Rejoin gets new id and fresh snapshot | ✅ |
| Protocol mismatch error | ✅ raw test client: "protocol mismatch (host 1, you 99)" |
| Game build mismatch error | ✅ raw test client |
| Silent peer timeout | ✅ dropped after 12.2 s |
| Session full | ⬜ |
| Host closes while client connected | 🟡 client got "The host ended the session" path only via Bye message code; not observed end-to-end after the last build |
| Client crash (process killed) | ✅ host removed player ("connection closed by peer") |
| Host crash | ⬜ (client times out; not observed in final build) |
| Version check against more than 2 players / third client | ⬜ (second client only via raw bot, joined/timed out) |
| Firewall / real LAN / internet | ⬜ |
| Alt-tab/background run | ⬜ (`runInBackground` forced; not tested while alt-tabbed) |

## Save integrity
| Item | Status |
|---|---|
| Saves backed up before testing | ✅ `backups\HFsaves-*` |
| Host save contains shared changes | ✅ `SaveCurrentGame` on host: file rewritten, 23 cleaned-dirt guids etc. (includes changes made by the client) |
| Client save blocked | ✅ log "[save] blocked house save…", file timestamp unchanged |
| Client balance restored after crash | ✅ host/client restart recovered the pre-session value 244,080.37 from the backup file |
| Client balance restored on normal leave | ⬜ (code path only) |
| Host restart keeps the shared progress | 🟡 relies on the host's normal save; load of the written save was done implicitly at every later session start (state persisted: dirt count stayed reduced, doormat stayed moved) |
| After testing, original saves/profile restored | see final section of `docs/test-log.md` |

## Installer
| Item | Status |
|---|---|
| Install into a fake game folder (with a pre-existing `winhttp.dll` and another mod) | ✅ files added, manifest written, original `winhttp.dll` backed up |
| Uninstall from it | ✅ only own files removed, original `winhttp.dll` restored, other mod untouched, saves untouched |
| Install into the real game | ✅ manually (same files) — plugin in `BepInEx\plugins\HFCoop` |

## Performance / stability
* Two game instances, 720p windowed, Ultra Low: ran stable for the whole final session. At 1440p Ultra the host/client froze for minutes several times (main thread stalled in game code; watchdog `[wd]` entries) — treated as hardware load, not proven.
* Network load: player state ≈ 50 bytes × 15 Hz per player; snapshot 300 KB JSON → 14–15 KB gz.
* Plugin overhead when no session is active: one `Update` that checks hotkeys + MoneySync poll; Harmony hooks only enqueue when a session is active.

## How to re-run
`tools\redeploy.ps1` (kill + copy dll + launch via Steam), `tools\focus.ps1 -ProcId <pid> [-X -Y]`, dev commands in `BepInEx\hfcoop-cmd-<pid>.txt` (marker file `BepInEx\hfcoop-dev.enabled`), `tools\nettest.ps1 -Mode badproto|badbuild|silent`.

---

# Update 0.2.0 (2026-10-25): lobby from the main menu, progression sync, real-input retest

Same harness, but both windows 1280x720 windowed, Ultra Low quality, side by side; no freezes occurred.

| Feature | Status | Evidence |
|---|---|---|
| Join **from the main menu** (client never loaded a house) | ✅ | client at main menu, `join` → handshake → snapshot → menu closed (`GameManager.GetMainMenu().DisableMenu()`) → host's house loaded; both windows show each other's avatar |
| Host still in menu when someone joins | ⬜ | code path (`NotReady` + retry every 3 s) not exercised |
| Balance / progression applied after the house finished loading (menu join) | ✅ | log `[prog] progression replaced by host's`, money synced |
| Skills/XP, statistics, tool unlocks, orders, courses sync (host→client, client→host) | 🟡 | statistics (`cleaned` +5) and a tool unlock bumped on one side appeared on the other, both directions. Skills/XP and orders use the same generic merge but were not changed during the test. |
| Joiner's own progression restored on leave | ✅ | log `[prog] restored own progression`, `[money] restored own balance` |
| Joiner keeps shared progression with `KeepSharedProgress=true` | ⬜ | |
| Host changes house → joiner follows | 🟡 | simulated by the host reloading its own house (`LoadFromRecording`); client received "house" event, requested a new snapshot and reloaded. A real order/office change was not run. |
| Missions (in-house task list) | ✅ by design | derived from the house state, which is synchronised |
| Orders / jobs as separate profile objects | 🟡 | `OrdersProgress` is merged like the rest (booleans OR together); starting an order is covered by "follow host" only when the host actually changes house. Not run. |

## Real input (actual mouse/keyboard in the game, host window, client observing)
| Action | Result |
|---|---|
| Walk, open door (E) | ✅ client saw the door open and the host avatar walk in |
| Tool wheel (hold RMB) → Move Objects / Painting / Demolition | ✅ client name tag changed to `[MoveObjectsTool]`, `[PaintRoller]`, `[HammerTool]` |
| **Pick up furniture** (hold LMB), move, click to place | ✅ dresser moved; `send item` on host, `recv item` on client; both report the same new position (-4.8, 0.0, 2.2) |
| **Tablet purchase** (Tab → product → Space "Buy now" → LMB place) | ✅ `send item c:0:1` + `send money bal`, client received both; item totals equal on both (206). The created item was not located by name on the client (the dev list only prints the 40 nearest items). |
| **Paint roller** | ❌ not completed: the roller says "I should refill the paint roller"; no paint bucket exists in this small house and I did not find one in the shop. Wall-paint sync is therefore only verified through the dev trigger. |
| **Wallpapering / tiling** | ⬜ not attempted (same missing material/paint requirement, tool not equipped) |
| **Demolition** | ❌ not completed: sledgehammer equipped and swung, but the targeted wall was not demolishable in this house; no wall change occurred. Demolition sync remains untested. |
| Cleaning with a real tool | ⬜ |

## Test-state note
All profile/save files were restored from the pre-test backup again after these tests (`backups\after-testing-modified-2` keeps the modified copies).

---

# Update 0.2.1 (2026-10-25): real-input retest of painting, wallpaper, demolition

Same harness (two 720p windows). All actions below were done with the real mouse/keyboard in the game; the other window (client, joined from the main menu) was the observer.

| Action | Result |
|---|---|
| **Paint buckets** | Found: Tablet → first tab (hammer) → *Walls* → *Paint* (≈$19 each). Buy with Space, place on the floor. |
| **Paint roller refill** | Equip roller (hold RMB, point at the roller icon), aim at the tin until it is highlighted ("Hold to load Roller with Paint"), hold LMB ~2 s; roller turns the tin's colour. (My first attempts failed because the crosshair was not exactly on the tin / out of 2.5 m reach.) |
| **Roller painting** (hold LMB over the highlighted wall strip) | ✅ host's recording got a `modifiedWallSides` entry with a real material guid; the client's recording got the identical entry. **First attempt: the client's wall did NOT change visually** (recording matched, mesh not refreshed because the game's load path only swaps the renderer material while the walls are rendered from combined meshes). Fixed in 0.2.1 by finalising the touched wall sides with `WallSide.FinalizePaintProcess` (the same call the roller makes). **After the fix a live strip painted by the host appeared on the client's wall** (a new yellow strip, compared before/after screenshots). |
| **Wallpapering** | Wallpaper packs: Tablet → Walls → Wallpapers (≈$87). Equip *Tiling and paneling* (wheel, left), press-and-hold LMB on the pack (6/6), hold LMB on a highlighted wall strip (pack count drops). ✅ recorded identically on both peers (`modifiedWallSides` 3 = 3) and ✅ visible on the client after joining (snapshot). The *live* wallpaper strip was not re-checked visually after the 0.2.1 fix (the same wall-side path is used). |
| **Demolition** | The previous wall was not demolishable. Demolishable walls are the *interior* walls (`Inside Walls/wall_025`, `DestroyableWall`), here between the bedroom and the bathroom. With the sledgehammer, aim until a blue column highlights, hold LMB ~3-4 s: wall segment removed, room merged ("Multifunctional room 20 m²"). ✅ host and client recordings both have `modifiedWalls` = 2 (first segment came with the join snapshot, the second was applied live). ✅ client sees the opening (tiled shower visible through it). |
| Tool name tag for each tool | ✅ `[PaintRoller]`, `[TilesAndPanelsInstallationT…]`, `[HammerTool]` shown over the remote avatar |
| Late-join snapshot after demolition + paint + wallpaper | ✅ client joined from the main menu and loaded the changed house (walls, paint, wallpaper, items visible) |
| Duplicate snapshot load at join (found while testing) | Fixed in 0.2.1: the retry timer only runs after a "host not ready" reply; a second snapshot while loading is ignored. |

Still open: live visual check of a wallpaper strip and of a third demolition segment after the 0.2.1 build; floors/tiles/ceilings real-tool tests (those lists use other combined meshes and may need the same refresh treatment).
Saves/profile restored again afterwards (`backups\after-testing-modified-3`).

---

# Update 0.2.2 (2026-10-25): missions / orders

Test: host accepted the "Painting the house" order from the in-house laptop (Mail) while the client was connected (menu-joined). Real input, two windows.

| Finding | Result |
|---|---|
| Client follows host into the order house | ? in 0.2.1 (follow detection relied on `isWaitingForApplyingSave`, which fresh order houses never set). ? after the 0.2.2 fix (detects the host's `GameplayRecorder` changing): log `[follow] host loaded a different house` ? `telling clients to follow into 03B_A` ? client received and loaded the order house snapshot. |
| Client mission HUD | ? client showed the *owned-house* HUD ("Sell the house", buyer portraits); host showed the *job* HUD ("Complete the job", progress bar, Income). Cause: `Recording.houseInfo` is JSON-serialised and loses `HouseInfo`'s `[JsonIgnore]` fields (`isMailOrder`, scene refs), so `GetOrderForHouse` finds no order. 0.2.2 re-expands it through `DataManager` before loading (log `[snap] expanded house info via ...`). **Not yet re-verified on an order house** (the retest stalled when the game stopped accepting key input). |
| Order budget | `HFSceneManager` switches the money source to `OrderBudget` for orders with their own budget (`GameManager.SetupMoneySource`). `MoneySync` now follows the game's current money object instead of the profile balance. Untested. |
| Script/mission state lists | Added to `RecSync`: `decisionsMade`, `plotTwistActivated`, `actionWithTriggerActivated`, `cursesProgress`, `brokenSubitems`. Untested (no such event triggered). |
| Quest/progress counters, progress bar | Untested live. Quest objects are driven by game events; changes applied from a peer through `Apply*` methods may not raise them, so counters probably only refresh after a reload. |
| In-game clock | ? not synced (host 00:33 vs client 20:23). |
| Complete the job / Sleep / buying houses | Not tested. Must stay host-only; the client follows the house change and money via shared money. |
| Menu join after a crash flag | Cosmetic: the client keeps the "house did not close properly" dialog open (press Return). |

Saves/profile restored again afterwards (`backups\after-testing-modified-4`).

---

# Update 0.2.3 (2026-10-25): job round trip retested

Two 720p windows, client joined from the main menu, real input (laptop ? Mail ? "Painting the house" ? Accept).

| Item | Result |
|---|---|
| Host accepts job ? client follows | ? `[follow] host loaded a different house` ? client loaded `03B_A` |
| Client shows the **job HUD** ("Complete the job", progress, income) | ? after the HouseInfo re-expansion (`mail order: True` in the log); previously "Sell the house" |
| Progress bar / recordings during the job | ?? a wall-side change applied on the host showed identically in both recordings; progress bars matched (no real task completed, so counter *movement* is untested) |
| In-game clock | ? `ClockSync`: host broadcasts the map-time counter every 5 s, clock showed 01:06 / 01:07, later 01:27 / 01:27 |
| Client pressing "Complete the job" | ? blocked (`[guard] blocked a house change on the client`, status text shown). Side effect: client HUD briefly lost the "Complete the job" row (cosmetic) |
| Host "Return to office" ? client follows back | ? client loaded `SmallFirstHouse` again |
| Complete a job fully (payout), Sleep | ? not run (Sleep on a client is covered only if it goes through `HFSceneManager.LoadHouse`) |
| "House did not close properly" dialog on menu join | ? not shown any more (popup closed before the load) |
| Config file lock when two instances start together | ? plugin retries; with a 25 s stagger both started cleanly (the simultaneous case was not retested) |

## Retest of the three open items (0.2.3 build)
| Item | Result |
|---|---|
| Quest/progress counters during a job | ? **not verified**. In the job house `questStatistics` only had `Mounted 4/0` and `WindowsCleaned 11/0` (already done), no dirt exists. 12 wall sides painted through the dev trigger appeared identically on both peers (12 = 12) but neither the progress bar nor the quest statistics moved on either side - dev-triggered paints do not count as job work. Real job work (paint all walls, mount air conditioners/radiators) was not done, so counter movement and sync remain untested. |
| Completing a job (payout/sale) | ? not run: with the job unfinished the host's Return key only offers "save progress before returning to the office". |
| Sleep (laptop) | ?? host pressed Sleep: no house change, no desync, clocks stayed equal (both 19:05). Whether the click actually triggered a sleep could not be confirmed. Client Sleep not run. |

## Investigation: "quest counters don't move" (0.2.3 build)
Not a sync bug. In the job house (`03B_A`) the quest manager reports **0 % completion and 6 quests (4 painting, 2 mounting) on the host itself, before and after** painting every wall side the `PaintingQuest`s track (880 sides, with exactly the bucket material the quest asks for, `WallPaint-FAC0D2`) through `WallSide.FinalizePaintProcess` + the recorder event. The client matched the host (0 % / 6 quests; 440 identical `modifiedWallSides` mods in both recordings). So the game's quest tracking ignores programmatic paints: it only advances through the real tool path (roller with the required bucket). Whether a *real* roller stroke moves the counter equally on the client was not testable here (needs the job's specific paint tin and many strokes; progress is 0 % for the first strokes anyway).
Also found: `questStatistics` in the Recording is only refreshed when the game builds a save, so it is not a live counter.
Dev helpers added: `completion` (prints job completion % and quest count), `questpaint` (paints the quests' wall sides with the required material).
Completing the job (payout) and a confirmed Sleep remain untested: completing needs 100 % of real job work.

## Job contents (found with the new `completion` / `questpaint` dev commands)
The "Painting the house" job (`03B_A`) contains 6 quests: 1 `CleaningWindowQuest`, 4 `PaintingQuest` (all require **Pastel Pink**, material `WallPaint-FAC0D2`) and 1 `MountableQuest` (air conditioners/radiators). Completion is 0 % on host and client; tablet shows a *Tasks* tab with the rooms. Tools added: `tools\session.ps1` (starts both instances, joins from the menu, optionally accepts the job), `tools\input.ps1`, dev `tp` fixed (position changes, but the camera did not follow in the job house, so walking is still needed).
Still not done: buy Pastel Pink (tablet ? Walls ? Paint), load the roller, paint real strips and watch `completion` on both peers; mount the AC/radiators; clean the windows; then complete the job. A stray masked window overlay covered part of the screen during these runs, which made tablet navigation unreliable.

---

# Update 0.3.0 (2026-10-25): host-authoritative job/skills/time, real input, two 720p windows

All of the following were done by the user with real input while the assistant compared both windows and the logs (host `LogOutput.log`, client `tools\inst2.log`).

| Item | Result |
|---|---|
| Job progress (number and bar) on the client | ✅ host 62 % / client 61 % (1 % = send interval); bar redraw fixed (client raises `QuestsUpdatedEvent`). Before the fix the client showed 6 % vs the host's 26 %. |
| "Complete the job" button state | ✅ host and client agree (button appears on both once the job passes the minimum). |
| Complete job on the **client** | ✅ blocked, status message, no payout, no skill points (before the guard it paid out locally). |
| Complete job on the **host** | ✅ payout +16,362.50 once, money and progress sent; client followed to the office; same balance on both. |
| Skill numbers | ✅ per-member hashes identical on host and client (`proghash` dev command): skills, statistics, tools, orders, courses, tool-quest set; every skill showed the same perk points. |
| Perks: client spends | ✅ blocked (`[perk] blocked ...`). Host spends: ✅ tablet updates on the client live; ✅ effect events fired on the client (painting fastHands 1→2, handyman plasterer 2). |
| Time of day | ✅ after the host slept to midnight the client's sky and PC clock followed. Client bed sleep ✅ blocked. |
| Buying a house on the client | ✅ blocked at the buy methods (33 entry points); no money change. (The first version only blocked the final load: the house was marked bought and money was taken on both peers.) |
| Doors | ✅ normal door, garage door. Laptop screen is local UI (not replicated, correct). |
| Carry sync (item moving while held) | ✅ works (user: "wonderful"). Loose items have no physics in this game, so nothing else to sync. |
| Avatars | ✅ arms + coloured held-tool block accepted by the user. |
| Tool-unlock quest set | ✅ synced (hash equal); not exercised by a real unlock. |
| Courses | n/a: no code path buys courses in this build. Quests are the tablet's *Tasks* tab. |
| PC "Sleep" button | ❓ does nothing on host or client; handler not found; unexplained. |
| Not tested | farm/DLC, mid-job late join, host leaving mid-job, 3+ players, real network (all tests were localhost, two instances on one PC), the host's own "back to office" menu actions on the client side beyond logs. |

Dev tools: `proghash`, `completion`, `prog`. The dev channel and verbose logging are off in the shipped config.

---

# Update 0.3.1 (2026-10-25): two physical machines on a LAN

| Item | Result |
|---|---|
| Setup | Host: the development PC (Ethernet, 192.168.x.x). Client: a clean Windows 11 laptop (Intel Core i5-5200U, 8 GB RAM, Intel HD Graphics 5500, Wi-Fi, 192.168.x.y), driven through a Remote Desktop session, game window 960×540. Mod installed with the shipped `Install` script from the 0.3.0 zip (BepInEx 5.4.23.3 + plugin); same game build on both. |
| Join from the main menu over the network | ✅ client joined `192.168.x.x:27055`, was handed the host's house snapshot (≈13 KB gz) and loaded it (≈1 min on the slow laptop, ≈3 min from launch to menu) |
| Balance, in-game clock, host avatar | ✅ identical after join (balance $88,463.82; clocks within ~2 s) |
| Job progress, doors, furniture pickup/carry | ✅ user: everything worked as intended, only slow (slow client frame rate delays applying changes by seconds) |
| Host log over the whole session | ✅ no warnings or errors, no timeouts, no watchdog stalls, one clean connection |
| Not done | non-LAN (internet/port-forward) connection, 3+ players, long session on the slow client, farm/DLC. |

Config change after this test: the defaults for `ConnectTimeoutSeconds` (8 → 30) and `TimeoutSeconds` (12 → 40) were raised, the values used successfully on the slow client. An existing config file keeps its old values.

---

# Update: Steam P2P probe (2026-10-25)
Feasibility only, no session code changed (see technical report section 9). ✅ two different Steam accounts on two machines connected through Steam's relay in ≈2 s, 30/30 pings (median 30.7 ms), a 200 KB reliable message echoed intact; ❌ first attempt failed because the client was launched outside Steam (app id mismatch 1471280 vs 613100) - a test-setup error, not a Steam limitation. Not tested: real internet path, 3+ peers, integration into the session transport.

---

# Update 0.4.0 (2026-10-25): Steam P2P transport in real sessions
| Item | Result |
|---|---|
| Host TCP + Steam, client joins by `steam:<SteamID64>` | ✅ two machines, two accounts: connected, snapshot loaded, balance/clock/avatar in sync (`P2P ICE` path) |
| Snapshot split into chunks (host `MaxChunkKB=4`, 13 KB → 4 messages) | ✅ client loaded the house |
| Host process killed | ✅ client: "Lost connection to host ... Timeout" after ~10 s, back to offline, house still open |
| Host presses F4 | ✅ client: "The host ended the session" |
| Panel Steam section (ID + Copy, host checkbox, friends list, join by ID) | ✅ renders; friends-hosting list empty (not friends) |
| TCP default unchanged | ✅ same handshake; TCP verified in the earlier two-machine test; `HostViaSteam` off by default |
| Not tested | hosting friend shown in the list, real internet path, 3+ peers over Steam, frames > 480 KB, gameplay sync over Steam beyond the join (same message layer as TCP) |

---

# Update 0.4.1 (2026-10-25): Steam over the internet, door swing side
* ✅ User: a client on **mobile data** (outside the host's LAN) joined through Steam and everything synced as before (first test over a real internet path).
* 🐞 → fix: a door opened from the other side swung the wrong way on the other peer. Cause: the door picks its swing side (`DoorInteraction`'s private side test) from the **local** player's position, so each peer decided from its own player. Fix: the door "use" event now carries the opener's position and the receiving peer evaluates the side from it. Retest needed with real doors from both sides (host opens from side A, closes, opens from side B; same with the client opening).

---

# Update 0.4.2 (2026-10-25): automatic reconnect (client) - built, NOT yet tested
To test: host + client connected; press **Ctrl+F12** on the host (or on the client) = connections dropped without goodbye. Expected: client shows "Connection lost - reconnecting...", the host removes the player, the client rejoins within seconds (new player id, fresh snapshot), a second drop works too; "Cancel reconnecting" stops the loop; the host pressing F4 / leaving yourself / a full session do **not** trigger a reconnect; with the host really gone the client gives up after `ReconnectSeconds` with a message. Also test over Steam and TCP.

## Result of the 0.4.2 reconnect test (2026-10-25, host PC + laptop over Steam)
| Item | Result |
|---|---|
| Both machines running 0.4.2 (`player Remote joined ... plugin 0.4.2`) | ✅ |
| Host Ctrl+F12 (connections dropped without goodbye) | ✅ host logged `player 1 (Remote) left: connection dropped (test)`; the laptop reconnected on its own within ~25 s (slow laptop included), came back as player #2 and received a fresh snapshot (85 KB json / 7.3 KB gz) |
| Host process killed, never returns | ✅ client showed "Connection lost - reconnecting to the host (attempt 2, 100 s left)" and after the 120 s window "Could not reconnect to the host (Steam connection problem 4001 ...)" and went offline with the house still open |
| Not tested | client-side Ctrl+F12, host coming back during the window, a real network cut (the machines were not disconnected), TCP path, "Cancel reconnecting" button, the door swing fix (needs real door use from both sides) |

## 0.4.4 two-instance tests (2026-10-25, user watched both windows)
Verified: window zoom released when the other player finishes it; mounted radiator syncs both ways without a leak on the other side; door sell/install/hammer-the-wall-inside opens the wall on both sides and the door opens/closes on both; demolition of inner walls from host and client (wall disappears, job counter moves); perk points follow the host; selling an item while another player assembles it releases them; electrical outlet and fuse box repairs sync. NOT verified: dirt %, minimap dirt, laptop on 0.4.4 (laptop was unreachable).

## 0.4.5 two-instance tests (2026-10-26, user watched both windows)
Verified: floor tiles appear for the other player; lamp + switch bought while connected work from host and from client; selling job items updates the other player's task list; buying/placing a job item moves both task counters; following the host into a job house loaded without hanging (harness). NOT reproduced: the reported infinite "Loading host's house" (hardened, not proven fixed).
