# BAMP Server Browser

An add-on for **[Going Public: Multiplayer for Big Ambitions](https://steamcommunity.com/sharedfiles/filedetails/?id=3765662670)**
that adds a **public server list**, **optional player and admin passwords**, **shared world saves** (on the host's PC
or on your own private VPS) and an **automatic headless host mode**.

It is a separate mod: **it does not modify the game or the original multiplayer mod**. It reads the
multiplayer mod's public state, drives its "Join a Game" dialog through reflection, and hooks a few
methods in memory (Harmony, provided by the base mod) for the join password.

---

## Features

| Feature | What it does |
|---|---|
| **Server list** | Every hosted game can be published to a public list. Players pick a game and click *Join* — no IP to type. Joins through the **Steam relay** by default (no port forwarding), or by IP:port. Games seen in the last 7 days stay listed when offline (green dot = online, grey = offline). Filter by language, *Online only*, *Favorites only*, *Hide games with a password*, search by name, and sort by status, name, day, players or language. Selecting a game shows its **difficulty settings**, the **names of the connected players** and the **ping**. |
| **Favorites** | *Add to favorites* on any game; favorites are listed first and you get a notification when one comes online. |
| **Update check** | Once per launch the mod checks `version.json` on GitHub and shows a banner when a newer version exists. |
| **Publish panel** | While hosting: show/hide the game in the list, name, description, language, max players, player password, admin password. The entry shows the language tag (e.g. **[FR]**), *Lobby* / *In game*, the in-game day, player count and mod version. |
| **Player & admin passwords** | Both empty by default (anyone can join, no admin). With a **player password** the game is marked **[password]**, the password is asked right after clicking *Join*, and the host refuses anyone without it; players who give it automatically receive the host's shared saves. Whoever joins with the **admin password** gets a *Manage this game* window: change name, description, language, max players, passwords, public listing, and delete shared saves (the host executes; the group admin key never leaves the host's PC). |
| **Shared world saves** | The latest version of each multiplayer world is kept on the group's save server, so any member can host the up-to-date world. Default: the host's own PC. Option: your own private VPS. |
| **Shared code & admin key** | Created automatically, nothing to type: the host hands its shared-saves code to players who join with a password. Only the host's admin key can delete saves. |
| **Headless host** | Runs the game with no window and no sound, hosts automatically, accepts players, autosaves, and offers a console (`status`, `players`, `say`, `save`, `stop`). |

---

## Requirements

- **Big Ambitions** (Steam), with the official mod loader.
- **Going Public: Multiplayer for Big Ambitions** — subscribe on the Steam Workshop
  (tested with **v0.3.3**). This add-on does not work without it: the game's mod loader loads the
  multiplayer mod first, then this one.
- For the optional private save server: any Linux VPS with **python3** and systemd.

---

## Installation (every player)

1. Subscribe to *Going Public: Multiplayer for Big Ambitions* on the Workshop and launch the game once.
2. Download `BAMPServerBrowser.dll` (from the [`dist/`](dist/) folder or the Releases page).
3. Create this folder and put the DLL in it (it must be the **only** DLL in the folder):

   ```
   %USERPROFILE%\AppData\LocalLow\Hovgaard Games\Big Ambitions\ModsLocal\BAMPServerBrowser\
   ```

4. Start the game. The mod's settings are created in:

   ```
   %USERPROFILE%\AppData\LocalLow\Hovgaard Games\Big Ambitions\BAMPServerBrowser\config.json
   ```

To uninstall, delete the `ModsLocal\BAMPServerBrowser` folder.

---

## How to use

- **Join**: *Multiplayer > Join Game*. The server list opens next to the dialog.
  Double-click a game or use *Join* (*Join by IP* if you prefer IP:port). If the game shows **[password]**,
  the password is asked right after *Join* (player password, or admin password for admin rights). For an
  open game, *Join as admin* asks for the admin password.
- **Admin**: once connected with the admin password, press **F9** → *Manage this game*: players (kick, ban,
  unban), message to everyone, save now, game settings, shared saves and their **trash** (restore within 30 days).
  The host gets the same window from *Publish my game* → *Players & tools*.
- **Host your own offline game again**: select it in the list (marked *(you)*) → *Host this game again*: the
  lobby opens with your latest save of that world.
- **Relaunch an offline game (admin only)**: an admin who joined the game once with the admin password
  can restart it when it is offline: select it in the list → *Relaunch as admin* → admin password. Their PC
  hosts its copy of the world (from the shared saves) with the same name, description, language and passwords.
- **Host**: *Host New Game* or *Host Saved Game*. *Host New Game* first asks **where to store the shared
  saves**: *This PC* (default, with its port) or *My own server* (address pre-filled from your config and the
  servers you used before, with a *Test* button, and the ready-to-paste command that **sends the save server
  files from your PC and installs them on your VPS** (scp + ssh) with a *Copy* button). The **Publish my game** panel appears in the lobby; its
  *Change* button opens the same choice later. The headless server is never asked.
- **F9**: show/hide the list at any time (joining needs the main menu).
- Panels can be dragged by their title bar.

---

## Saves: where they live

The public list server **stores no saves**; it only lists games. Each group chooses where its saves go.

### Default — on the host's PC (nothing to install)

- While the game runs, the host's PC serves the group's saves on **TCP port 8788**.
- Friends who join with the **player password** (or the admin password) automatically receive the shared-saves
  code **of that world only** (`groupe-…@HOST_PUBLIC_IP:8788`, stored in `joinedWorlds`). Only that world then
  syncs with the host's PC; the friend's own worlds stay in the friend's own group and are never sent to the
  host. A host can't claim a world that is already on the friend's PC unless it hosted it. Games without a
  player password never hand out the code.
- The save server on the host's PC only accepts the host's own group code and stores at most 2 GB in total.
- Downloaded worlds are checked: invalid world ids, paths leaving the save folder, executable files and
  oversized archives are refused.
- One-time setup on the host's router: **forward TCP 8788** to the host's PC, and allow Big Ambitions in
  the Windows firewall when asked.
- When the host's PC is off, friends keep the copy they already have but cannot get a newer one.

### Option — on your own private VPS (the host's PC can be off)

Easiest: in the game, *Host New Game* → *My own server* → type the VPS address → **Copy** the command and
paste it in PowerShell on your PC (Debian/Ubuntu or any systemd Linux with python3). The server files
(`master.py`, `install.sh`) are **embedded in the mod**: the command sends them from your PC with `scp`, then
runs the installer with `ssh`. Nothing is downloaded from the internet. It looks like:

```bash
scp "%LOCALAPPDATA%Low\Hovgaard Games\Big Ambitions\BAMPServerBrowserps-install\master.py" "…\install.sh" root@YOUR_VPS:/tmp/ ; ssh -t root@YOUR_VPS "cd /tmp && sudo sh install.sh 8787"
```

Or by hand, on the VPS, in a folder that contains `master.py` and `install.sh`:

```bash
sudo sh install.sh              # port 8787
sudo sh install.sh 9000         # or another port
```

The script installs a hardened systemd service (`bamp-master`), opens the port in `ufw` if it is
active, and prints the exact line to use. Then, **game closed**, edit the admin's `config.json`:

```json
"worldsUrl": "http://YOUR_VPS_IP:8787"
```

Start the game: the code to give your friends becomes `groupe-xxxx@YOUR_VPS_IP:8787`. Friends only paste
that code — no file to edit. The admin's local worlds are uploaded on the next sync, and the admin key is
registered by the VPS on first connection.

If your hosting provider has its own firewall, open the port there as well. Check the service with
`systemctl status bamp-master` and `journalctl -u bamp-master -f`.

### How syncing works

1. While hosting, the world is uploaded after every save (checked every 30 s) and once more back in the menu.
2. In the main menu (at launch, every 90 s, or *Sync now*), newer worlds are downloaded and newer local
   worlds are uploaded.
3. To play the shared world: *Multiplayer > Host Saved Game*.

### Safety rules

- No group code → no access. Seeing a game in the public list gives no access to its saves.
- Only the **admin key** can delete a world (it goes to the server trash for 30 days). A member who deletes
  a world locally only removes it from their own PC.
- The save server keeps the **10 previous versions** of each world. Before a local copy is replaced, the old
  one is moved to `SaveGames\_BAMP_MP\_BAMPServerBrowser_backups\`.
- Conflict rule: the most recent save wins.
- Never share `adminKey` or your whole `config.json`; share only the group code.

---

## Settings (`config.json`)

| Key | Default | Purpose |
|---|---|---|
| `masterUrl` | public list server | Server list address |
| `publish` | `true` | Publish your hosted games in the list |
| `serverName`, `description` | `""` | Shown in the list |
| `joinPassword` | `""` | Player password to join your hosted game (empty = open) |
| `adminPassword` | `""` | Admin password: joining with it gives the *Manage this game* window (empty = no remote admin) |
| `favorites` | `[]` | Favorite games (`steam:<id>` or `ip:<address>:<port>`) |
| `notifyFavorites` | `true` | Notification when a favorite comes online |
| `listHidePassword` | `false` | Hide games with a password in the list |
| `checkUpdates` | `true` | Check GitHub for a newer version at launch |
| `askSaveServerOnNewGame` | `true` | Ask where to store the shared saves on *Host New Game* |
| `saveServerHistory` | `[]` | Save servers used before (pre-filled in that question) |
| `sshUser` | `root` | SSH user used in the VPS install command |

Passwords, the admin key and shared-saves codes are **encrypted with Windows (DPAPI)** in `config.json` and
`relaunch.json`: only your Windows account on this PC can read them. Old plain-text values are still read and
are encrypted on the next save. A `config.json` copied to another PC loses these values (they are recreated).
| `language` | system language | Language shown for your hosted game in the list (`EN`, `FR`, `DE`…) |
| `maxPlayers` | `8` | Shown in the list |
| `toggleKey` | `F9` | Show/hide the list |
| `preferSteam` | `true` | Join through the Steam relay when possible |
| `shareKey` | generated | Shared-saves code (`code` or `code@host:port` from a friend) |
| `adminKey` | generated | Admin key for deleting worlds — keep it private |
| `worldsUrl` | `""` | Private VPS save server; empty = the host's PC |
| `localStoragePort` | `8788` | Port of the save server built into the game |

Deleting `config.json` recreates it with defaults (and a **new** group code) on the next launch.

---

## Headless host (optional)

Runs Big Ambitions as an automatic host with no window and no sound, from a console.

```
server\start-server.bat
```

- Commands: `status`, `players`, `say <message>`, `save`, `stop` (Ctrl+C = `stop`). `server\stop-server.bat` also stops it cleanly.
- It uses a separate folder (`%USERPROFILE%\BigAmbitionsServer`: executable copy + junctions, a few MB) so the
  server has its own multiplayer identity.
- Settings in `BAMPServerBrowser\dedicated.json`: `mode` (`auto`/`new`/`load`), `save`, `startDelaySeconds`,
  `autoAccept`, `autosaveMinutes`, `targetFps`, `statusLogSeconds`, `mute`.
- **Steam must be running and signed in with an account that owns the game.** Steam allows one running game
  per account, so to play at the same time the server needs **another machine and another Steam account
  that owns the game**. Needs ~4 GB RAM, no graphics card. Windows only (Linux untested via Proton).

---

## Repository layout

| Path | Content |
|---|---|
| `src/` | Mod source (C#, .NET Framework 4.8) |
| `dist/BAMPServerBrowser.dll` | Ready-to-use build |
| `master/master.py` | List + save server (Python 3, standard library only) |
| `master/install.sh` | Installs the save server on a private VPS |
| `master/bamp-master.service` | systemd unit used for a **list-only** server (`BAMP_WORLDS=0`) |
| `server/` | Headless host console scripts |

### Building

```bash
dotnet build src/BAMPServerBrowser.csproj -c Release
```

The project references the game's `Managed` DLLs and the multiplayer mod's DLLs from their default Steam
paths (override with `-p:GameDir=...` and `-p:BampDir=...`). They are used as references only and are
not redistributed.

### Server API (`master.py`)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/servers` | Public game list (online games only) |
| POST | `/api/worlds/<id>/restore` | (admin key) Take a world out of the trash |
| GET | `/api/servers?offline=1` | Same, plus games seen in the last 7 days that are offline (`online: false`, `offlineFor` in seconds, no IP). A world relaunched by another host (same `worldId`) is listed once. |
| POST | `/api/servers` | Register a game → `{id, token}` (IP taken from the connection) |
| POST / DELETE | `/api/servers/<id>` | Heartbeat / remove (`X-Token`) |
| GET | `/api/worlds` | Group worlds (`X-Share-Key`) |
| GET / PUT | `/api/worlds/<id>` | Download / upload a world version |
| DELETE | `/api/worlds/<id>` | Move to trash (`X-Admin-Key`) |

`BAMP_WORLDS=0` makes it a list-only server. Built-in limits: 120 requests/min per IP, 5 listed games per IP,
4 KB list entries, 64 MB per world, 20 worlds per group.

---

## Credits

- *Going Public: Multiplayer for Big Ambitions* and its authors — this add-on depends on it entirely.
- *Big Ambitions* by Hovgaard Games.
- Not affiliated with Hovgaard Games or the multiplayer mod authors.
