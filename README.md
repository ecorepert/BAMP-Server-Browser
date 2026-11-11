# BAMP Server Browser

An add-on for **[Going Public: Multiplayer for Big Ambitions](https://steamcommunity.com/sharedfiles/filedetails/?id=3765662670)**
that adds a **public server list**, **optional join passwords**, **shared world saves** (on the host's PC
or on your own private VPS) and an **automatic headless host mode**.

It is a separate mod: **it does not modify the game or the original multiplayer mod**. It reads the
multiplayer mod's public state, drives its "Join a Game" dialog through reflection, and hooks a few
methods in memory (Harmony, provided by the base mod) for the join password.

---

## Features

| Feature | What it does |
|---|---|
| **Server list** | Every hosted game can be published to a public list. Players pick a game and click *Join* — no IP to type. Joins through the **Steam relay** by default (no port forwarding), or by IP:port. |
| **Publish panel** | While hosting: show/hide the game in the list, name, description, max players, password. The entry shows *Lobby* / *In game*, player count and mod version. |
| **Join password** | Empty by default (anyone can join). When the host sets one, the game is marked **[password]** and the host refuses anyone without it. |
| **Shared world saves** | The latest version of each multiplayer world is kept on the group's save server, so any member can host the up-to-date world. Default: the host's own PC. Option: your own private VPS. |
| **Group code & admin key** | Created automatically. Only players with the group code can see the group's worlds; only the admin key can delete them. |
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
  Double-click a game or use *Join* (*Join by IP* if you prefer IP:port). Enter the password if the
  game shows **[password]**.
- **Host**: *Host New Game* or *Host Saved Game*. The **Publish my game** panel appears in the lobby.
- **F9**: show/hide the list at any time (joining needs the main menu).
- Panels can be dragged by their title bar.

---

## Saves: where they live

The public list server **stores no saves**; it only lists games. Each group chooses where its saves go.

### Default — on the host's PC (nothing to install)

- While the game runs, the host's PC serves the group's saves on **TCP port 8788**.
- The list window shows the **code to give your friends**, e.g. `groupe-1a2b3c4d5e6f7a8b@HOST_PUBLIC_IP:8788`
  (*Copy* button). Friends paste it in **Group code** → *Save*.
- One-time setup on the host's router: **forward TCP 8788** to the host's PC, and allow Big Ambitions in
  the Windows firewall when asked.
- When the host's PC is off, friends keep the copy they already have but cannot get a newer one.

### Option — on your own private VPS (the host's PC can be off)

On the VPS (Debian/Ubuntu or any systemd Linux with python3):

```bash
# copy master/master.py and master/install.sh to the VPS, then in that folder:
sudo sh install.sh          # port 8787
sudo sh install.sh 9000     # or another port
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
| `joinPassword` | `""` | Password to join your hosted game (empty = open) |
| `maxPlayers` | `8` | Shown in the list |
| `toggleKey` | `F9` | Show/hide the list |
| `preferSteam` | `true` | Join through the Steam relay when possible |
| `shareKey` | generated | Group code (`code` or `code@host:port` from a friend) |
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
| GET | `/api/servers` | Public game list |
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
