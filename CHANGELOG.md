# Changelog

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
