# Changelog

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
