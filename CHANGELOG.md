# Changelog

## 0.5.0
* **The office is taken over before the host's house loads** (no second office PC in the client's hands, no "for sale" buyers outside). **The office the host claims is shared**: the host sends which house is its office (and when it was created) when you join and whenever it changes; the client swaps it in, so the host's office no longer looks like a house on sale, and puts its own office back when the session ends.
* **Doors/windows left over from an earlier load are removed**: loading a house a second time in one run (host to the main menu and back, or a house change) left the first load's doors/windows standing next to the new ones. The check now also removes those leftovers (any door/window item of an installed kind that no record points to and that stands at the same spot as a recorded one); it runs a few times in the first minute after a load. Verified on the host with a reloaded house.
* **Doubled doors and windows already in a save are removed**: after a house has loaded, every peer checks its own house for two doors or windows of the same kind at the same spot and removes the extra one locally (nothing is sent, so a peer that does not have the double is not affected).
* **A quest that fails while a house loads no longer blocks the load** (the client got stuck on "Loading host's house"); the load retry now fires after 20 s instead of 90 s.
* **Doubled doors/windows after a job and a client seeing office doors inside a job**: when the host changed house (job -> office and back) the mod compared the new house with what it had sent for the old one, so every door/window/record looked "new" (and every old one "gone") and was sent to a client that was still in the old house. The mod now resets what it remembers the moment the host changes house, tells the clients first ("changing"), ignores events about the old house on both sides, and the client reports "ready" once it is in the new house.
* **Saves with duplicate records are cleaned** (when the host saves and when any save is loaded): duplicate door/window installation records, replaced-window records, item records and other doubles are merged, so doors/windows are not built twice when the office is loaded. A save that already has doubled windows is repaired when it is loaded.
* **Re-applying an installation to a door/window that already has one** (frame and plug) no longer builds a second one next to it, and loaded installations always get an id both players share.
* **Job progress now follows paint, tile and panel work done by the other player**: the painting/tiling quests recount on events that only the paint and tile tools raise, so work applied from the other player never updated them. The same events are now raised after a remote wall/floor/ceiling change (found from the quests' own subscriptions). Not verified in a job yet.
* **Snowdrifts (and rubbish heaps) cleared by the other player** now disappear for you and count toward the job.
* **Replaced items (stairs, windows...)**: the replacement quests are pointed at the replaced item and the game's "replacement confirmed" event is raised, so the job counts them (verified for stairs replaced by the host).

## About 0.4.7 and 0.4.8
These two versions were internal builds and were never published: they did not feel finished enough to put in public, so the first public version after 0.4.6 is 0.4.9. Everything from 0.4.7 and 0.4.8 is included in 0.4.9 and listed below.

## 0.4.9
* **Doors and windows built during the session now work for the other player**: opening/closing syncs and selling removes them (frame included). The cause was that the game finishes setting up a freshly placed installation a moment after it is applied, so the mod could not tell which item was the door; it is now found by prefab and position. A sold door/window is also announced when its installation record disappears, and the other peer removes the installation together with the item.
* **Doors and windows audit**: every door/window/opener class in the game was checked against what the mod syncs.
* **Smart doors now sync** (smart-home doors only react to a different call than normal doors; the mod always replayed the normal one). A use that did not really happen is no longer replicated either.
* **Safe doors, scissor-lift doors, animated openers and doorbells** are replicated now.
* **Items that come with a house** (job furniture, windows/doors made by the replace-window/door quests) get an id that both players agree on, so their doors, windows and cupboards sync and moving them no longer creates duplicates. A brand-new item is sent to the other player before its first use.

## 0.4.8
* **Lamps stay lit**: a lamp that is switched on no longer goes dark when you leave its room (the game only lit lamps in the room you were in). Config `AlwaysLitLamps` (default on) turns this off.
* **Perk popups**: a client no longer levels up by itself, which made the "new perk" popup repeat every time the tool was used. The host still decides perk points; the client just follows.
* **Tiles and paint on newly built walls** now sync (walls built by the other player are registered in the floor's wall list, as the wall tool does).
* **Items that disappear without a sale event** (for example doors installed during the session and then sold) are now noticed and removed for everyone; this covers doors, windows, appliances and lights alike.
* Discord (from 0.4.7): the game's native Discord SDK stays disabled and the plugin shows its own presence.

## 0.4.7
* **Fixes a game crash**: the game's native Discord SDK (`discord_game_sdk.dll`) can panic when Discord is closed or in an odd state (seen as a crash right after a co-op session ended, in `ActivityManager.ClearActivity`). The plugin now always switches that SDK off.
* **Own Discord Rich Presence** written in managed code (Discord's local IPC pipe, background thread, reconnects by itself): it shows House Flipper with the current house, and for co-op "Hosting / Joined a session - N players" with a party size. No player names are sent. Turn it off with `DiscordPresence = false` (`[UI]` in the config); the native SDK stays off either way.

## 0.4.6
* **New player avatars**: a small articulated figure per player (cap and shirt in the player's colour, walking legs, arms that reach for what they hold) instead of a capsule.
* **Held tools are drawn as blocky models** (sledgehammer, nail hammer, axe, chainsaw, shovel, paint roller, brush, trowel, cleaners, vacuum, pressure washer and sprayers, shotgun, price scanner, wall/floor tools) and the tiling/panel tool is a flat block with a thin white tile.
* **Tool animations**: while a player works with a tool (a mouse button held in game) the others see it swing, stroke, scrub, recoil or press; idle tools breathe slightly.
* Network protocol 4: the player state now carries "is using the tool", so **everyone must update to 0.4.6** (older versions refuse to connect with a clear message).

## 0.4.5
* Floor tiles now show up for the other player (the floor's combined mesh is rebuilt after a remote change).
* Light switches and lamps bought while connected work for both players (lamp on/off states are synced directly).
* Buying and placing job items, and selling them, update the other player's job task list the way a local drop/sale does.
* Joining a job house is more robust: a newer house arriving during a load is queued, a load that never finishes is retried after 90 s instead of leaving "Loading host's house...".

## 0.4.4
* Demolition (hammer) of inner walls syncs, including the job counters and the visible wall mesh.
* Doors and windows: every install stage, selling, and open/close (also doors placed in earlier sessions) sync; the wall inside a new door opens on both sides.
* Repairs sync: electrical outlets, fuse boxes, replaced sockets, mounted devices (no more one-sided leaks).
* Perk points and perk levels are host-authoritative.
* Selling an item while another player is working on it no longer leaves them stuck in the zoom camera (same fix for windows finished by someone else).

## 0.4.2 - 0.4.3
* Automatic reconnect for clients; door-swing fix; window/device zoom release; mounted-device sync.

## 0.4.0
* Optional Steam P2P transport next to the default TCP transport.

## 0.3.x / 0.2.x
* Host-authoritative job progress, perks, time of day; late join with a house snapshot; shared money and progression; first public prototype (see `docs/`).
