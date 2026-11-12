# BAMP Server Browser

An add-on for **[Going Public: Multiplayer for Big Ambitions](https://steamcommunity.com/sharedfiles/filedetails/?id=3765662670)**
(the "base mod" below). It adds a **public game list**, **player and admin passwords**, **remote admin tools**,
**shared world saves** and an **automatic headless host**.

It is a separate mod: **it does not modify the game or the base mod's files**. It uses the base mod's public API,
a few reflection calls on its UI, and a handful of in-memory hooks (Harmony, shipped with the base mod). The full
list is in [For the Going Public developer](#for-the-going-public-developer).

- Mod version: **1.3.5** (C#, .NET Framework 4.8, `src/`)
- Tested with: **Going Public v0.3.3** on Big Ambitions (Steam, Windows)
- Server side: `master/master.py`, Python 3 standard library only (list server, optional save storage)

---

## Features

| Feature | What it does |
|---|---|
| **Game list** | Hosted games are published to a public list. Pick a game and click *Join*: no IP to type. Joins through the **Steam relay** by default (no port forwarding) or by IP:port. |
| **Offline games** | Games seen in the last 7 days stay listed when offline (green dot = online, grey = offline). |
| **Filters & sorting** | Search by name; filter by language, *Online only*, *Favorites only*, *Hide games with a password*; sort by status, name, in-game day, players or language. |
| **Game details** | Difficulty settings (difficulty, starting money, tax, days per year, multipliers, disabled needs…), connected player names, ping, host's mod version. |
| **Favorites** | Favorites are listed first; a notification appears when one comes online. |
| **Passwords** | Optional **player password** (asked right after *Join*) and **admin password**. Players who give a password automatically receive the shared save of that world. |
| **Admin tools** | Joining with the admin password opens *Manage this game*: player list, kick, ban/unban (base mod's ban list), message to everyone, save now, game settings (name, description, language, max players, passwords, public listing), shared saves and their trash (restore within 30 days). The host gets the same window from *Publish my game → Players & tools*. |
| **Relaunch offline games** | The host can *Host this game again* from the list. An admin who joined once can *Relaunch as admin* (admin password checked) with the same name and passwords. |
| **Shared world saves** | Each world syncs with the save server of its host: the host's PC (default) or the host's own VPS. Chosen when clicking *Host New Game*. |
| **Update check** | At launch, the mod asks the game list server (`/api/version`) and offers a *Download* button served by that same server. |
| **Headless host** | Runs the game with no window and no sound, hosts automatically, accepts players, autosaves; console with `status`, `players`, `say`, `save`, `stop`. |

---

## Requirements

- **Big Ambitions** (Steam) with its official mod loader.
- **Going Public: Multiplayer for Big Ambitions** (Steam Workshop). The game loads it first, then this add-on.
- Optional, for a save server that is always on: a Linux VPS with **python3** and systemd.

## Installation (every player)

1. Subscribe to *Going Public* on the Workshop and launch the game once.
2. Get `BAMPServerBrowser.dll`: the game list server publishes it at `/download/BAMPServerBrowser.dll`
   (the mod's *Download* button), or build it from `src/` (see [Building](#building)).
3. Put it, game closed, as the **only** DLL in:

   ```
   %USERPROFILE%\AppData\LocalLow\Hovgaard Games\Big Ambitions\ModsLocal\BAMPServerBrowser\
   ```

4. Start the game. Settings are created in `…\Big Ambitions\BAMPServerBrowser\config.json`.

To uninstall, delete the `ModsLocal\BAMPServerBrowser` folder.

---

## How to use

- **Join**: *Multiplayer → Join Game* (the list opens next to the dialog), or **F9** at any time. Select a game,
  then *Join* (or double-click). If it shows **[password]**, the password is asked right after *Join*; give the
  admin password to get admin rights. For an open game, *Join as admin* asks for the admin password.
- **Host**: *Host New Game* first asks **where to store the shared saves** (see below), then the base mod's lobby
  opens with the **Publish my game** panel: public listing, name, description, language, max players, player and
  admin passwords, save server (*Change*), *Players & tools*.
- **Admin**: connected with the admin password, press **F9 → Manage this game**.
- **Offline games**: select one → *Host this game again* (your own game) or *Relaunch as admin*.
- Panels can be dragged by their title bar.

---

## Shared saves

The game list server only lists games. Saves are stored by **the host's save server**:

- **This PC** (default): while the game runs, the host's PC serves its saves on **TCP 8788** (forward this port on
  the router for friends). When the PC is off, friends keep their last copy.
- **My own server**: a VPS running `master.py` with storage on. In *Host New Game → My own server*, type the VPS
  address and click **Copy**: the copied PowerShell line sends `master.py` and `install.sh` **from your PC**
  (they are embedded in the mod) with `scp`, then runs the installer with `ssh`. Nothing is downloaded from the
  internet. Then click *Test*.

  ```
  scp "<…>\BAMPServerBrowser\vps-install\master.py" "<…>\vps-install\install.sh" root@VPS:/tmp/ ; ssh -t root@VPS "cd /tmp && sudo sh install.sh 8787"
  ```

How it works:

1. When a player joins with a password, the host sends the save code of **that world only**
   (`group-key@host:port`). Only that world then syncs with the host's server; the player's own worlds stay in
   the player's own group and are never sent. A host can't claim a world already on the player's PC unless it
   hosted it.
2. While hosting, changed saves are uploaded every 30 s; in the main menu everything syncs every 90 s.
   The most recent save wins. Before a local copy is replaced, the old one goes to
   `SaveGames\_BAMP_MP\_BAMPServerBrowser_backups\` (3 kept).
3. Only the host's admin key can delete a world (server trash for 30 days, restorable). The save server keeps
   the 10 previous versions of each world.

Safety: the built-in save server only accepts its owner's group code and stores at most 2 GB. Downloaded worlds
are checked (hex world ids only, no path leaving the save folder, no `.dll/.exe/scripts`, size limits).

---

## Headless host (optional)

```
server\start-server.bat
```

- Console commands: `status`, `players`, `say <message>`, `save`, `stop` (Ctrl+C = `stop`); `server\stop-server.bat`
  also stops it cleanly. Remote admins use *Manage this game* as with any host.
- Uses `%USERPROFILE%\BigAmbitionsServer` (executable copy + junctions) so the server has its own identity.
- Settings: `BAMPServerBrowser\dedicated.json` (`mode` auto/new/load, `save`, `startDelaySeconds`, `autoAccept`,
  `autosaveMinutes`, `targetFps`, `statusLogSeconds`, `mute`).
- Steam must run with an account that owns the game (one running game per account).

---

## Settings (`config.json`)

| Key | Default | Purpose |
|---|---|---|
| `masterUrl` | public list server | Game list server address (also used for the update check) |
| `publish` | `true` | Publish hosted games in the list |
| `serverName`, `description`, `language`, `maxPlayers` | `""`, `""`, system language, `8` | Shown in the list |
| `joinPassword`, `adminPassword` | `""` | Player / admin passwords of your hosted games |
| `shareKey`, `adminKey` | generated | Your save group code and its admin key |
| `worldsUrl` | `""` | Your own save server; empty = this PC |
| `localStoragePort` | `8788` | Port of the save server built into the game |
| `joinedWorlds` | `{}` | Worlds received from other hosts (world id → their save code) |
| `askSaveServerOnNewGame`, `saveServerHistory`, `sshUser` | `true`, `[]`, `root` | *Host New Game* save server question |
| `favorites`, `notifyFavorites` | `[]`, `true` | Favorite games and their notification |
| `listOnlineOnly`, `listFavoritesOnly`, `listHidePassword`, `listLanguage`, `listSort`, `listSortDesc` | | List filters and sorting |
| `checkUpdates` | `true` | Ask the list server for a newer version at launch |
| `toggleKey`, `preferSteam` | `F9`, `true` | List hotkey; join through the Steam relay when possible |

Passwords, keys and save codes are **encrypted with Windows DPAPI** in `config.json` and `relaunch.json` (only this
Windows account on this PC can read them; old plain-text values are still read, then encrypted).

---

## For the Going Public developer

This add-on depends entirely on the base mod. Below is everything it touches, so that a base mod update does
not break it silently, and a short wishlist of APIs that would let it drop its hooks.

### Harmony prefixes

| Target | Why |
|---|---|
| `MPServer.HandleHello(MPLink, MessageEnvelope)` | Check the player/admin password hash in the Hello; refuse with `peer.Disconnect("BAMP:password")`; record the player's role. |
| `MPClient.Send(MessageEnvelope)` | Add `bsbPw` (password hash) and `bsbV` (add-on present) to the outgoing Hello. |
| `MPClient.OnDisconnected(string, byte[])` | Detect `BAMP:password` (wrong password) and `BAMP:kicked` (kicked by an admin). |
| `MPChat.AddMessage(string, string, string)` | Swallow the add-on's hidden messages (private chat lines starting with `~bsb1~` + JSON). |
| `MPServer.SendChatPrivate(string, string, string)` | Never relay a tagged message that does not come from the host. |
| `MPServer.Broadcast(MessageEnvelope)` | Never broadcast a tagged chat message. |
| `MPCanvasUI.OnMpHostNew()` | Ask where to store shared saves before the lobby opens (skipped by the headless host). |

### Reflection (non-public members)

`MPCanvasUI`: `_joinDialog`, `_joinIp`, `_joinPort`, `OnMpJoin`, `OnJoinConnect` (fill and submit *Join a Game*),
`_mpButton`, `_mpSubmenu` (menu state), `OnMpHostNew`, `LoadPickedSession(session, pid)`, `OnLobbyStart`
(headless host, relaunch), `_hostSettings` (difficulty while in the lobby).
`MPServer.PeerForPid` (kick an in-game player), `MPServer.ConnectedPids`, `MyPluginInfo.PLUGIN_VERSION`.

### Public API used

`MPServer`: `IsRunning`, `IsInLobby`, `BoundPort`, `ConnectedCount`, `ConnectedClientPeers`, `DisplayNameFor`,
`BanPlayer`, `UnbanPlayer`, `BanList`, `KickFromLobby`, `BroadcastChat`, `SendChatPrivate`, `DtoFromGameVariables`,
`PendingJoinList`, `AcceptPendingJoin`, `HostSnapshotsReady`, `Stop`. `MPClient`: `IsConnected`, `IsConnecting`,
`SendChat`. `MPConfig.PlayerId`, `MPChat.AddMessage`, `MPNet.PublicIp` / `FetchPublicIpAsync`,
`MPSaveManager.ListPlaythroughs` / `MpVersionFolder`, `MPSaveCoordinator.ActivePlaythroughId` / `HostSaveNow` /
`HostSaveSync`, `MPCanvasUI.Instance`.

### Hidden message channel

Host ↔ player messages ride on private chat (`SendChatPrivate` / `SendChat`, sender checked by the base mod's
`SenderIs`), text `~bsb1~{json}` (≤ 200 chars, the base mod's chat limit).
Host → player: `w` role, `s` save code of the current world, `g`/`gd` settings, `rl`/`rp` relaunch info, `r` result,
`w0/wi/w1/we` saves and trash, `p0/pi/p1` players, `b0/bi/b1` bans.
Player → host (admins only): `get`, `set`, `del`, `rs` (restore), `pl`, `kick`, `ban`, `unban`, `msg`, `save`.

### Wishlist (would remove most hooks)

1. A Hello extension point: extra fields from client mods, and a host callback that can accept or refuse with a
   reason shown to the player (replaces the 3 password hooks).
2. A mod-to-mod message channel (custom type + payload) instead of tagged private chat.
3. Public calls for *Host New Game*, *Host Saved Game (session, playthrough id)*, *Start lobby* and
   *Join (address, port)* instead of reflection on `MPCanvasUI`.
4. An in-game kick (`KickFromLobby` only covers the lobby).
5. Events: player joined/left, world loaded (playthrough id), current game settings.

### Observed issue (base mod v0.3.3)

Sometimes the host stays on *"Waiting for all players to finish loading… Auto-start in 3:00"* while alone. Log:
`[TimeSync] Startup release received BEFORE the hold began — remembered…`, then
`[TimeSync] Startup hold SKIPPED — the release for this load already arrived`, and the
`[FreezeGate] host soft-hold ENGAGED` is never followed by `RELEASED` (normally `soft-hold RELEASED (startup hold released)`).
The skipped hold seems to leave the soft-hold engaged until the 180 s fallback.

---

## Repository layout

| Path | Content |
|---|---|
| `src/` | Mod source (C#). `ServerBrowser.cs` UI, `AdminChannel.cs` admin/messages, `JoinPassword.cs` passwords, `WorldSync.cs` + `LocalWorldStore.cs` shared saves, `SaveServerPrompt.cs` save server choice, `DedicatedHost.cs` headless host, `SecretBox.cs` DPAPI, `GameInfo.cs` difficulty/players/update check |
| `master/master.py` | Game list server + optional save storage + mod publication |
| `master/install.sh` | Installs `master.py` as a hardened systemd service (embedded in the mod too) |
| `master/bamp-master.service` | systemd unit for a **list-only** server (`BAMP_WORLDS=0`) |
| `master/version.json` | Version published by the list server (`/api/version`) |
| `server/` | Headless host console scripts |

### Building

```bash
dotnet build src/BAMPServerBrowser.csproj -c Release
```

References the game's `Managed` DLLs and the base mod's DLLs from their default Steam paths (override with
`-p:GameDir=...` and `-p:BampDir=...`); they are not redistributed. Output: `src/bin/Release/net48/BAMPServerBrowser.dll`.

### Server (`master.py`)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/servers` | Online games |
| GET | `/api/servers?offline=1` | Same + games seen in the last 7 days (`online: false`, `offlineFor`, no IP; one entry per world) |
| POST | `/api/servers` | Register a game → `{id, token}` |
| POST / DELETE | `/api/servers/<id>` | Heartbeat / unregister (`X-Token`) |
| GET | `/api/worlds` | Worlds of a group (`X-Share-Key`) |
| GET / PUT | `/api/worlds/<id>` | Download / upload a world |
| DELETE | `/api/worlds/<id>` | Move to trash (`X-Admin-Key`) |
| POST | `/api/worlds/<id>/restore` | Restore from trash (`X-Admin-Key`) |
| GET | `/api/version` | Published mod version (`version.json` next to `master.py`) |
| GET | `/download/BAMPServerBrowser.dll` | Published mod file (next to `master.py`) |

Environment: `BAMP_MASTER_HOST`, `BAMP_MASTER_PORT` (8787), `BAMP_WORLDS=0` (list only), `BAMP_WORLDS_DIR`,
`BAMP_OFFLINE_FILE`, `BAMP_DIST_DIR`, `BAMP_TRUST_PROXY=1` (behind a reverse proxy). Limits: 120 requests/min per
IP, 5 listed games per IP, 500 games, 75 s heartbeat timeout, 64 MB per world, 20 worlds per group.

**Publishing an update**: copy the new `BAMPServerBrowser.dll` and `master/version.json` (new number) next to
`master.py` on the list server, then restart it; every player sees the update at the next launch.

---

## Credits

- *Going Public: Multiplayer for Big Ambitions* and its authors: this add-on depends on it entirely.
- *Big Ambitions* by Hovgaard Games. Not affiliated with Hovgaard Games or the base mod's authors.
- MIT License (see `LICENSE`).
