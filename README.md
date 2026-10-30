# House Flipper Co-op (prototype 0.5.1)

A BepInEx mod that lets several people renovate **the same house** together in the original **House Flipper (2018, Steam)**.

It is a prototype: the core loop works and was tested with two real game instances, but some systems may be untested or not shared.

## What it does

- You see the other players in the house, with name tags and the tool they hold.
- Furniture, cleaning, painting, floors, demolition, doors/windows and repairs are shared live.
- Money, skills and job progress are shared. The host's values are authoritative.
- Late joiners get the host's current house automatically.
- Only the host saves the shared house. Joiners work on an in-memory copy.

Some actions are host-only (completing/accepting jobs, buying/selling houses, sleeping, spending perk points).

## Install

1. Close House Flipper. Everyone needs the **same game build** and the same mod version.
2. **Back up your saves** (`%USERPROFILE%\AppData\LocalLow\Empyrean\House Flipper Game`).
3. Run `Install.bat`. It finds your Steam copy and installs BepInEx 5.4.23.3 and the plugin.

To uninstall, run `Uninstall.bat`. It removes only what the installer added. Saves are never deleted.

## Play

| Key | Action |
|---|---|
| F5 | Host (after loading your house with *Continue*) |
| F6 | Join using the address in the config |
| F7 | Open the panel (host, join, Steam options) |
| F4 | Leave the session |

**Host:** press F5, then give friends your IP. Over the internet, forward **TCP 27055**, or use a VPN such as Tailscale, ZeroTier or Hamachi.

**Join:** from the main menu press F7, enter the host's IP (`1.2.3.4` or `1.2.3.4:27055`) and press *Join*.

**Steam (optional):** if everyone launches the game through Steam, you can connect by Steam ID without port forwarding.

## Good to know

- Always keep backups. The game was not designed for multiplayer.
- If the host crashes, work since the host's last save is lost. Save before you finish.
- Tested with game build 1.26252 on Windows, base game only. DLCs are not verified.
- Steam Workshop addons are not supported or tested yet. Items from the Workshop may not show up or work for the other players.
- The player limit is 4, including the host. You can raise it with `MaxPlayers` in `BepInEx\config\dev.hfcoop.cfg`, but there is no promise that anything above 4 players works as intended.

## More information

See [`docs/DETAILS.txt`](docs/DETAILS.txt) for the full list of shared features, hosting and Steam details, save safety, troubleshooting and building from source.

Other docs: `docs/technical-report.md`, `docs/test-checklist.md`, `docs/test-log.md`, `CHANGELOG.md`.

## License

MIT (see `LICENSE`). Third-party components: `THIRD-PARTY.md`. Unofficial fan project, not affiliated with the developers of *House Flipper*.

## Reporting problems

Open an issue with the plugin version, TCP or Steam, what you did, and `BepInEx\LogOutput.log` from all players if possible.
