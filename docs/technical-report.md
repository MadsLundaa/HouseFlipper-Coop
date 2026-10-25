# House Flipper Co-op — technical report

Date: 2026-10-25. Target: House Flipper (Steam app 613100), build `1.26252 (ab1aa)`, Windows. Everything below was inspected or measured on the author's installed copy; nothing was taken from other people's code.

## 1. The game (facts verified on the install)

| Item | Finding |
|---|---|
| Engine | Unity **2019.4.39f1**, **Mono** scripting backend (`HouseFlipper_Data\Managed\Assembly-CSharp.dll`, 25 MB), x64, `MonoBleedingEdge` runtime present |
| Code | Managed but **obfuscated**: most member names are Arabic-letter identifiers and string literals are scrambled. Class names that Unity serialises (MonoBehaviours, ScriptableObjects, Recording/ItemModification data classes) and many public/private method names that are not obfuscated survive (`GameplayRecorder.On*`, `HouseSaveManager.Apply*`, `Item.CanBeSold()`, `Interactable.Interact()`, `WallSide.FinalizePaintProcess()` …). Obfuscated names change between builds, so the mod only binds to non-obfuscated names and uses reflection with fallbacks. |
| Bundled networking libs | `com.rlabrecque.steamworks.net.dll` (Steamworks.NET), Galaxy, Epic and Discord SDK dlls; no gameplay networking code exists |
| Player | `PlayerController` on `Player(Clone)`: CharacterController + camera + **hands only**, no body mesh → remote players can only be approximated |
| Save format | `Profiles\Profile N\UserSaves\SaveSlot_<House>` = **Newtonsoft JSON of a `Recording`** (186–300 KB). A `Recording` is a *diff* of the house against the baked scene: lists such as `modifiedItems`, `cleanedCleanablesGuids`, `cleanedWindowsGuids`, `modifiedWallSides`, `modifiedFloors`, `modifiedCeilings`, `modifiedCavities`, … keyed by **stable scene GUIDs** (`HouseSaveManager.ComponentToGuid/GuidToComponent`). Profile-level data (money, skills, unlocked tools) lives in `GameProgress.dat`. |
| Recording during play | `GameplayRecorder` subscribes to ~60 events of an obfuscated static event hub and mutates `currentRecording` live (`OnWallPainted`, `OnCleaned`, `OnBoughtOrModifiedItem`, `OnSoldOrDisposedItem`, …). |
| Loading | `HouseSaveManager.LoadFromRecording(Recording)` loads the house scene and applies the recording through private `Apply*` methods (`ApplyItems`, `ApplyCleanables`, `ApplyWallsAndFloors`, …). `ItemModification.TryPostLoad` instantiates/moves/destroys a single item. |
| Money | `PlayerController.progress.moneyProgress` (`Money._money`, double). HUD shows it converted by the selected currency, so values on screen differ from the internal number. |
| Autosave | "Quick save" every 3 min (game setting) writes the house through `HouseSaveManager.SaveRecordedGame`. |

## 2. Mod loader / patching

* **BepInEx 5.4.23.3 (x64, Mono)** + the bundled **HarmonyX**: verified to inject into this build (Doorstop `winhttp.dll`, preloader, chainloader) and to patch private methods of `Assembly-CSharp`. Plugin is compiled against the game's own `mscorlib/System/netstandard/UnityEngine*/Newtonsoft.Json` (net40 profile).
* No other loader was needed; IL2CPP tools do not apply (Mono build).
* Harmony pitfalls found: patch parameters must be addressed positionally (`__0`, `__1`) because original parameter names differ (obfuscated/other names) — a named parameter made one patch fail at startup.
* Object inspection: ILSpy (`ilspycmd`) decompile of `Assembly-CSharp.dll` (4,529 files, `discovery\decomp`, not distributed), plus an in-game dev channel (dump scene histogram, items, interactables, raycast target).

## 3. Existing work

A search (2026-10) found **no multiplayer mod for the original House Flipper**: a gamepressure.com guide states there is none and that the developers said co-op would need extensive rewrites ([source](https://www.gamepressure.com/S013-amp.asp?ID=20080)). Co-op exists officially only in *House Flipper 2* (separate game). Search hits for "House Flipper trainer/BepInEx" were single-player trainers for HF2 or other games. We did not find or use any repository for HF1 co-op; claims of existing HF1 multiplayer mods should be treated as unverified. BepInEx/HarmonyX docs used: [BepInEx docs](https://docs.bepinex.dev), [HarmonyX](https://github.com/BepInEx/HarmonyX).

## 4. Networking decision

| Option | Assessment |
|---|---|
| **Direct TCP (chosen)** | Works with plain .NET sockets already in the game's Mono, no dependency, no paid service. Needs host port-forward (or a free overlay VPN). Fully testable on one PC with two game instances. |
| Steam P2P (Steamworks.NET present in game) | **Implemented as an optional transport (0.4.0), tested on two machines and two Steam accounts (sections 9-10).** TCP remains the default; the host can additionally listen on Steam. |
| Unity UNET (`UnityEngine.UNETModule.dll` present) | Deprecated/removed API surface; rejected. |

Protocol: length-prefixed frames on TCP, one reader + one writer thread per connection, main-thread dispatch (`ConcurrentQueue`-style with locks). Message types: Hello/Welcome/Reject (version & build check, player id, max players), Ping/Pong (2 s, timeout 12 s), PlayerState (15 Hz), PlayerJoined/Left, Event (JSON), Snapshot(Req).

## 5. Architecture

Host-authoritative star topology. Every change is *applied locally by the game* on the player who did it, detected through the game's own recorder, then sent to the host, which applies it, mirrors it into its own `Recording` (so the host's save contains it) and relays it to the other clients. Late joiners get a snapshot.

```
local action ─► game events ─► GameplayRecorder.On*  ─(Harmony postfix / list diff)─► Events.Send
                                                                                         │ TCP
 remote peers ◄─ host relay ◄─ host applies (+ mirrors into currentRecording) ◄──────────┘
```

| Subsystem | Source | Mechanism |
|---|---|---|
| Players | `Session.cs`, `Avatars.cs` | position/yaw/pitch/tool name @15 Hz; capsule+head avatar, interpolated, no colliders, name tag |
| Items (move, buy, sell/dispose, damage) | `ItemSync.cs` | Harmony postfix on `OnBoughtOrModifiedItem`, `OnDamageItem`, `OnOfficeRelocated`, prefix on `OnSoldOrDisposedItem`; builds an `ItemModification` with the game's `PreSave`, ships JSON, receiver applies with `TryPostLoad` (or `TryToLoadItemState` for existing runtime-created items). IDs: scene GUID (`s:`) or `c:<owner>:<n>` for runtime-created. Dedupe by last JSON per id. |
| List state (dirt, windows, wall sides/paint, floors, ceilings, plaster, cockroach nests, pet dirt, standalone wall sides; experimental: walls/outer walls) | `RecSync.cs` | every 0.5 s serialise the tracked `Recording` lists, ship **new/changed elements**, receiver builds a partial `Recording`, calls `RestoreHardRefs()` (guid→Material) and the game's own `Apply*` method, then merges into its own recording. |
| Doors/windows/drawers/switches | `Interactions.cs` | postfix on `Interactable.Interact/SmartInteract` for `MovablePart`/`HFLightSwitch`; key = scene guid or "item guid + child path"; receiver calls `Interact()` (toggle). |
| Money | `MoneySync.cs` | host authoritative absolute balance; clients send signed deltas; joiner's own balance backed up to disk and restored on leave/crash recovery. |
| Progression | `ProgressSync.cs` | skills, statistics, unlocked tools, orders, courses of `PlayerProgress` are serialised to JSON, diffed and shipped as operations (number: add delta, bool: OR, primitive list: union, other: set) and applied by reflection; host replaces the joiner's values at join, joiner backed up to disk and restored on leave. |
| Follow host | `FollowHost.cs` | when the host's `isWaitingForApplyingSave` goes true→false (a house finished loading) the host announces it, clients fetch a new snapshot |
| Main-menu join | `Snapshot.cs` | `HouseSaveManager` is reached via `GameManager.houseSaveManager` when its static Instance does not exist yet; before `LoadFromRecording` the client does what the menu's Continue does (`wasGameStarted = true`, `GetMainMenu().DisableMenu()`) |
| Late join | `Snapshot.cs` | host: `MakeCurrentGameRecording()` → JSON → gzip (≈14 KB for a 300 KB recording); client: `LoadFromRecording`; ids of runtime-created items are exchanged by recording index; events arriving during the load are queued and replayed. |
| Save integrity | `Events.cs` | Harmony prefix on `HouseSaveManager.SaveRecordedGame`: blocked on clients and on any "tainted" (host-derived) house until the player returns to the menu. Host saves normally. |
| Diagnostics | `Watchdog.cs`, `Dev.cs` | main-thread stall watchdog; file-based dev command channel (disabled unless `hfcoop-dev.enabled` exists / config). |

Conflict handling: the host relays events in arrival order, so all peers converge on the host's order; last writer wins per object. Echo suppression: `Events.Applying` flag + `Known` sets + `LastJson` per item. Duplicated or replayed identical events are no-ops. Two players using the same door simultaneously both toggle twice and end up closed on both sides (consistent, not necessarily intended). Item destroyed while another player edits it: edit is ignored (unknown id).

## 6. Obstacles and uncertainties

1. **Obfuscation**: only readable names are used; a game update that renames them breaks hooks (the plugin logs missing targets instead of crashing). Re-verify after every game patch.
2. **No avatar model**: capsules with arms, a tool-coloured block and a name tag with the held tool's class name. No animation (0.3.0).
3. **Per-profile progression** (skills, tool unlocks, statistics, orders) is shared since 0.2.0 via `ProgressSync` (host reference, merge of deltas); achievements, pets, farm state and the joiner's other profile data are not. See section 8 for the host-only actions.
4. **Real mouse-driven actions** (paint roller, wallpapering, demolition, tablet purchase, furniture pickup, window cleaning, mounting devices) were retested with real input in 0.2.1–0.3.0; see the checklist.
5. **Wall demolition/construction/architect tool/gardening/pets/farm/DLC**: not implemented or only experimentally tracked; see checklist.
6. The joiner's profile is modified while connected (house loaded into the session, balance replaced). Protection exists (no house save, balance backup) but a hard crash of the joiner's PC while connected relies on the recovery code at next start.
7. Two instances on one PC at *Ultra* quality repeatedly froze one instance (main thread stalled in game code for minutes). After switching both to Ultra Low / 1280×720 windowed no freeze occurred in the final sessions. Cause not proven (suspected GPU/driver load); the watchdog logs `[wd]` if it recurs.
8. `Application.runInBackground = true` is forced so an alt-tabbed player does not time out.

## 7. Source map

```
src/Plugin.cs      entry, hotkeys, IMGUI panel          src/Net.cs        framing, connections
src/Session.cs     roles, handshake, players, tick      src/Avatars.cs    remote players
src/Events.cs      hooks, relay, save blocking          src/ItemSync.cs   item replication
src/RecSync.cs     list replication                     src/Interactions.cs doors etc.
src/MoneySync.cs   money                                src/Snapshot.cs   late join
src/Config.cs      BepInEx config                       src/Watchdog.cs, Dev.cs, Inspector.cs  diagnostics
src/JobSync.cs     job progress, perk/complete guards   src/ClockSync.cs  map timer + time of day (DaySync)
src/HouseActions.cs host-only house deals               src/CarrySync.cs  items carried live
src/ProgressSync.cs progression + perk effects          src/FollowHost.cs follow the host between houses
installer/         install.ps1 / uninstall.ps1 (+ .bat)  tools/  test harness (see checklist)
```

## 8. Host-authoritative features added in 0.3.0 (how they work)

The game's code is obfuscated, so most of these hooks are found **by shape** at startup (a reflection scan of IL for a call to a readable method) and log `guarded ...` or a warning if the target is missing.

| Feature | File | Mechanism |
|---|---|---|
| Job progress | `JobSync.cs` | Quest trackers only count actions done through the real tool path, so a synced mount or paint never moved a client's counter. The host sends `GetCompletion()` and `QuestTracker.CanCompleteJob()` every 2 s; a Harmony postfix on both makes clients report the host's values, and the client raises `QuestsManager.QuestsUpdatedEvent` so the HUD redraws. The quest manager is re-looked-up each time (it is recreated per house). |
| Job completion on a client | `JobSync.cs` | The payout/unlock/archive method is the static method whose IL calls `HouseSaveManager.MoveOrderSaveToArchival`. It is blocked on clients (a client previously paid out locally and sent the money to the host before the house change was refused). |
| Perk spending | `JobSync.cs` | `SkillCategory` method that decrements `numberOfPerksAvailable` is blocked on clients. Perk levels sync as plain ints; `ProgressSync` then fires the skill's `on<Perk>ValueChanged` events so the effect is active on every peer. |
| Progress state | `ProgressSync.cs` | Also syncs `toolsUsedInUnlockingToolQuest` (a `HashSet`, via reflection `Add/Contains`). Sets and lists use union semantics. |
| Time of day | `ClockSync.cs` (`DaySync`) | `DayNightController.currentTime` is separate from the map timer. Host broadcasts seconds-of-day every 3 s; clients call the `OverrideCurrentTimeWith(h,m,s,ms)` overload when drift exceeds 90 s. Bed sleeping (methods of `SleepableBed` that call the time setter) is blocked on clients. |
| House deals | `HouseActions.cs` | Buy/sell/rent/accept/relocate/back-to-office: every method that calls `HFSceneManager.LoadHouse` or `HousesProgress.MarkAsBought/SellHouse/RentHouse/EndHouseRenting` (outside the snapshot loader), plus the main-menu button dispatcher for those buttons, is blocked on clients (33 entry points). The money is taken in the same method as the house change, so blocking only `LoadHouse` was not enough. |
| Carrying items | `CarrySync.cs` | The held item is the only plain `Item` field of `MoveObjectsTool`. Pose sent ~12 Hz; receivers glide the same item to it; the placement still uses `ItemSync` for the exact state. |
| Doors etc. | `Interactions.cs` | (0.4.1: a door decides which way to swing from the player's position, so the "use" event of a door carries the opener's position and the receiving peer decides the side from it, via a postfix on the door's private side test, found by its 50/140/230/320 degree constants.) Replicated interactable types matched by class name through the hierarchy (movable parts, animator open/close incl. garage doors and blinds, curtains, gates, attic stairs, openable boxes, light switches). Skipped types are logged once in verbose mode. |
| Avatars | `Avatars.cs` | Capsule body + head + arms; a block whose colour is derived from the held tool's name, tilted with the look pitch. |

Findings: the real "PC Sleep" button and courses. Courses (`CoursesProgress.BuyCourse`) have no callers in this build, i.e. the feature does not exist. The PC Sleep button did nothing on either peer in tests and its handler was not found, so it is unexplained.

## 9. Steam P2P feasibility test (2026-10-25)

**Question:** can the mod connect players through Steam (no IP, no port forwarding) the way many Steam games do?

**What the game provides.** `HouseFlipper_Data/Managed/com.rlabrecque.steamworks.net.dll` (Steamworks.NET with the 1.5x API surface: `SteamNetworkingSockets.CreateListenSocketP2P`, `ConnectP2P`, `AcceptConnection`, `SendMessageToConnection`, `ReceiveMessagesOnConnection`, `GetConnectionInfo`, `SteamNetworkingUtils.InitRelayNetworkAccess`, lobbies/matchmaking, the older `SendP2PPacket`) and `steam_api64.dll` 6.91. The game itself initialises Steam, so the mod only calls into it.

**Probe (`SteamProbe.cs`, off by default, `[Steam] ProbeAutoStart` / Ctrl+F11).** One side listens on virtual port 0 and prints its SteamID64 and echoes every message; the other connects to that SteamID64, sends 30 pings of 64 bytes (200 ms apart) and a 200,000-byte reliable message (about the size of a house snapshot), and writes `BepInEx/hfcoop-steam-probe.txt`.

**Setup.** Host PC: Steam account A, Ethernet, 192.168.x.x. Client: clean Windows 11 laptop (i5-5200U, HD 5500), a *different* account "Mastia", Wi-Fi 192.168.x.y. Both on the same LAN. Both ran the game launched **through Steam** (app id 613100).

| Result | Value |
|---|---|
| Relay network | `Current OK`, 25 relays valid on both machines |
| Connection | `P2P SDR` (Steam Datagram Relay), data center `sto2` (Stockholm), listener accepted in 0.07 s, route found + connected ≈ 2.1 s after `ConnectP2P` |
| Pings | 30/30 answered; RTT min 15.1 / median 30.7 / max 92.6 ms (Wi-Fi laptop through the relay) |
| 200 KB reliable message | echoed back intact after 1.65 s round trip (400 KB total) |
| Loss / reordering | none reported by Steam |
| NAT / firewall | nothing opened or forwarded; Steam chose the relay path even though both peers were on one LAN |

**Pitfall found.** The first attempt timed out (`reason 5003 'Timed out attempting to connect'`) because the client had been started by running `HouseFlipper.exe` directly: it then reports the app id from `steam_appid.txt` (1471280) while the host ran as 613100, and Steam only connects peers of the same app id. Both players must start the game from Steam (the normal way), or set the same id.

**Implications for a real transport.**
* The reliable-message model maps onto our framing (one message per frame; the largest we send, the gzip'd snapshot, was 13 KB of 205 KB JSON; Steam's limit is 512 KB per message, chunk above that).
* Join flow options: type a SteamID64 (works today), Steam friends "Join Game"/rich presence, or a lobby (`SteamMatchmaking`, invite code). A friend-list UI is the main new work.
* Latency is higher than LAN/TCP (≈15-30 ms over the relay) but fine for this mod's event rates.
* The API is polled from the main thread (`ReceiveMessagesOnConnection`) and the connection callback needs `SteamAPI.RunCallbacks()` (the probe calls it itself).
* Both players need Steam online and the base game on their accounts; a game started without Steam cannot use it.
* Not tested: a real internet path (both machines shared a LAN, the relay path was used anyway), 3+ peers, a long session, host migration.

## 10. Steam P2P as an optional transport (0.4.0)

**Design.** `Conn` (Net.cs) is now an abstract base (`Send`, `Close`, plus peer id/name/handshake state). `TcpConn` is the old TCP implementation (reader/writer threads, length-prefixed frames), `SteamConn` (SteamNet.cs) carries the same whole frames over `ISteamNetworkingSockets`. The session code (handshake, events, snapshot, timeouts, relay through the host) is unchanged; a host can hold `TcpConn`s and `SteamConn`s at the same time.

| Piece | Behaviour |
|---|---|
| Host | TCP listener always; `[Steam] HostViaSteam` (or the panel checkbox) also opens `CreateListenSocketP2P(0)`, accepts incoming connections in the status callback, queues them once `Connected`, and sets Steam rich presence `hfcoop=host` so friends can see that the player is hosting. |
| Join | `Session.StartJoin(addr)`: `steam:<id>` or a bare 17-digit SteamID64 goes through `SteamTransport.Connect` (`ConnectP2P`); anything else is TCP as before. The panel offers hosting friends (from rich presence) and a join-by-Steam-ID field. Timeout = `ConnectTimeoutSeconds`. |
| Framing | Reliable + no-nagle messages, ordered. First byte: 0 = whole frame, 1 = chunk, 2 = last chunk; frames above `MaxChunkKB` (480, Steam's per-message limit is 512 KB) are split and reassembled. |
| Receiving | polled on the main thread each tick (`ReceiveMessagesOnConnection`), with `SteamAPI.RunCallbacks()` called by the session as well as by the game. |
| Failure handling | Steam end reasons are turned into messages ("could not reach the host ... through Steam" for 5003); a host that disappears is noticed in about 10 s ("Lost connection to host: ... Timeout"); a normal leave sends `Bye` and uses linger-close so it arrives first. |
| Isolation | all Steamworks calls live in `SteamNet.cs`/`SteamProbe.cs`; with `HostViaSteam=false` and a TCP address nothing Steam-related runs. `SteamTransport.Available` checks that Steam is running and the game initialised it. |

**Test (2026-10-25, host PC (Steam account A) + laptop (Steam account B), same LAN, game started through Steam).**
* Host with TCP+Steam, laptop joined with `JoinAddress = steam:<host Steam ID>`: connected, handshake, snapshot (13 KB gz), house loaded, balance, clock and avatar in sync. Path chosen by Steam this time: `P2P ICE` (direct) rather than the relay seen in the probe.
* Chunk path: host `MaxChunkKB=4` (the 13 KB snapshot sent as 4 messages) → laptop loaded the house normally after rejoining.
* Host killed abruptly → client reported "Lost connection to host: Steam connection problem 4001 (Timeout...)" after about 10 s and went offline with the house still open. Host pressed F4 → client showed "The host ended the session".
* Panel: Steam section shows the user's Steam ID (Copy), the host checkbox, hosting friends (none here) and join-by-ID.
* Not tested: a hosting friend appearing in the friends list (the two accounts were not friends, joined by ID), a real internet path, 3+ peers over Steam, frames above 480 KB (only the split logic with a small chunk size).

**Harness note.** When driving the laptop through Remote Desktop, never use `open_application` on the already-open RDP window (it spawns extra connect dialogs that take the keyboard focus); raise the session window with a click or `focus.ps1`. The slow laptop needs mouse clicks held ~1.5 s and keys held ~3 s.

## 11. Automatic reconnect (0.4.2)

`Session` keeps the last join address (`steam:<id>` or `host:port`). When the connection to the host closes for any reason other than "left" (we left), "remote said bye" (the host ended the session) or a host rejection, and `AutoReconnect` is on, the client runs the normal `Leave` cleanup (players, sync state, its own money/progress restored) but keeps the loaded house, then `BeginReconnect`: `Tick` calls `StartJoin` again every 2+2n s (capped at 10 s) until `ReconnectSeconds` (120) run out. Attempts that fail to connect or are cut during the handshake are silent and the loop continues; a Welcome ends it ("reconnected after n attempt(s)"). Because the retry is an ordinary join, the host sends a new snapshot and everything missed during the outage is replaced by the host's current state (nothing is merged). Host side: on a Hello from the same name and the same machine (IP or Steam id) while the old connection has been silent for more than 1.5 s, the old one is closed ("replaced by reconnect") so the player does not appear twice. `Session.DebugDrop()` (Ctrl+F12) drops all connections without a Bye, for testing.
Detection time: Steam notices a dead peer in about 10 s (observed), TCP through `TimeoutSeconds` (40 s) of silence or an immediate reset.

## 12. Renovation work that needed more than list diffs (0.4.3 / 0.4.4)

Found while two players worked for an hour; all verified with two instances on one PC and the user watching both windows.

* **Camera locks.** Windows (cleaning) and mountable devices (assembling) put the player in a zoom view (`HFCameras.CameraZoomable`, first bool = locked). If the other peer finishes or sells the object, the zoom is released first (`LaunchTweenBackToFppCameraCoroutine`; the tween is driven by a static camera event so it survives the object being destroyed).
* **Mounted devices** (`MountSync`): `GameplayRecorder.OnDeviceFullyMountedForSavingSystem` is postfixed; the other peer calls `MountInstantly`. Devices without an `Item` (electrical outlets under `[Wall Devices]`) are identified by `p:<scene path>`. Fuse boxes work through the same event. A device created from another peer's item event leaked a puddle on that peer only (`MontageControl` re-creates it every 12 s): the coroutines are stopped and the puddle removed for the first 25 s.
* **Doors/windows** (`InstSync`): installations live in `Recording.wallInstallations` (state Placed/ReadyToDestroyWall/ReadyToInstall/Installed), not in the item list. Every change is sent with the item's net id; the receiver rebuilds the installation through `HouseSaveManager.ApplyNewWallModifications`. A later stage destroys the old installation object first (it is a separate object next to the item; leaving it left a second frame and the wall plug) and any `DestroyableWallPlug` past stage 1. Doors from earlier sessions get ids `w:<index>` from the snapshot id map so they open/close on both sides.
* **Demolition** (`RecSync`): the game's loader only deletes the `Wall` object. The live hammer path also raises the static "wall destroyed" hub event (the combined wall mesh `WallsMeshCombiner`, the job quest counters/markers and culling react to it) - without it the wall stayed visible on the peer and the job counter did not move. We call `Wall.DestroyAndNotify` for `Removed`/`FracturedWall` entries and raise the hub event (found by shape: the only static `Action<Wall>` event's raiser) after the local recording lists the walls. Demolished walls also left empty entries in `WallsMapBuilder.wallsList`, which made the game's own `ApplyWallsAndFloors` throw; they are purged first. After door installs the combined meshes are rebuilt (`BuildAll`, debounced).
* **Repairs:** more recording lists are replicated (`modifiedReplaceableItems`, `deviceStateSetterStates`, `repairedCockpits`, `destroyableItemModifications`, `changedWallModifications`). `prefabSwaps` is deliberately not (swapping twice).
* **Perk points are host-authoritative:** points and perk levels follow from locally counted work, so peers reached them at different moments (and a synced "+1" could be counted twice). Clients never send them; the host sends absolute values and the client puts the host's numbers back whenever its own game moves them (checked every 150 ms).
* **Selling an item another player is working on** releases the local zoom before the item is destroyed.

Known gaps: removals from recording lists (other than via the paths above), runtime dirt / minimap phantom dirt and dirt %, leaking puddles created at runtime are not replicated, job-house items without scene guid and not in `modifiedItems` get no net id.
