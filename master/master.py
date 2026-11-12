#!/usr/bin/env python3
"""BAMP Server Browser - master server (liste des parties ouvertes).

Stdlib only. Stockage en memoire, les entrees expirent sans heartbeat.

  GET    /api/servers               -> {"servers": [...]} parties en ligne
  GET    /api/servers?offline=1     -> idem + parties vues recemment mais hors ligne ("online": false)
  POST   /api/servers               -> enregistre une partie, renvoie {"id", "token", "ttl"}
  POST   /api/servers/<id>          -> heartbeat / mise a jour (header X-Token)
  DELETE /api/servers/<id>          -> retire la partie (header X-Token)
  GET    /health                    -> "ok"
  GET    /api/version               -> {"version", "notes", "download"} lus dans version.json (a cote de master.py)
  GET    /download/BAMPServerBrowser.dll -> le mod a jour, s'il est depose a cote de master.py

Mondes partages (header X-Share-Key = code de groupe, >= 6 caracteres) :
  GET    /api/worlds                -> {"worlds": [...]} du groupe
  GET    /api/worlds/<id>           -> archive du monde (gzip)
  PUT    /api/worlds/<id>           -> envoie une version (headers X-Saved-At, X-World-Day,
                                       X-World-Name, X-Players, X-Uploader) ; 409 si plus ancienne
  Header X-Admin-Key (optionnel) : la premiere cle admin vue par un groupe le "revendique" ;
  ensuite seule cette cle peut supprimer. Les membres (code de groupe seul) envoient/recoivent.
  DELETE /api/worlds/<id>           -> (admin) met le monde a la corbeille (meta "deleted"); un PUT plus
                                       recent que la suppression le restaure ; purge apres 30 jours
  POST   /api/worlds/<id>/restore   -> (admin) sort le monde de la corbeille
"""
import hashlib
import json
import os
import re
import shutil
import secrets
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HOST = os.environ.get("BAMP_MASTER_HOST", "0.0.0.0")
# Dossier de publication du mod : version.json + BAMPServerBrowser.dll (par defaut, celui de master.py)
DIST_DIR = os.environ.get("BAMP_DIST_DIR") or os.path.dirname(os.path.abspath(__file__))
DLL_NAME = "BAMPServerBrowser.dll"
MAX_DLL_BYTES = 10 * 1024 * 1024
_VERSION_RE = re.compile(r"^\d{1,4}(\.\d{1,5}){1,3}$")
PORT = int(os.environ.get("BAMP_MASTER_PORT", "8787"))
TTL = 75            # secondes sans heartbeat avant expiration
MAX_TOTAL = 500
MAX_PER_IP = 5
MAX_BODY = 4096
TRUST_PROXY = os.environ.get("BAMP_TRUST_PROXY", "0") == "1"
GLOBAL_RATE_PER_MIN = 120   # toutes requetes confondues, par IP
MAX_CONCURRENT = 64         # connexions traitees en meme temps
SOCKET_TIMEOUT = 10         # secondes (coupe les connexions lentes / slowloris)
_slots = threading.BoundedSemaphore(MAX_CONCURRENT)
_grate = {}
_grlock = threading.Lock()


def _global_rate_ok(ip):
    now = time.time()
    with _grlock:
        win = [t for t in _grate.get(ip, []) if now - t < 60]
        ok = len(win) < GLOBAL_RATE_PER_MIN
        if ok:
            win.append(now)
        _grate[ip] = win
        if len(_grate) > 10000:
            for k in [k for k, v in _grate.items() if not v or now - v[-1] > 60]:
                del _grate[k]
        return ok

WORLDS_DIR = os.environ.get("BAMP_WORLDS_DIR") or os.path.join(os.environ.get("STATE_DIRECTORY", "/var/lib/bamp-master"), "worlds")
MAX_WORLD_BYTES = 64 * 1024 * 1024
MAX_WORLDS_PER_GROUP = 20
KEEP_VERSIONS = 10
RATE_PER_MIN = 90          # requetes /api/worlds par IP et par minute
TRASH_DAYS = 30
_ID_RE = re.compile(r"^[0-9a-fA-F]{8,64}$")
# BAMP_WORLDS=0 : serveur de liste uniquement, aucun stockage de sauvegardes
WORLDS_ENABLED = os.environ.get("BAMP_WORLDS", "1") != "0"
_wlock = threading.Lock()

_lock = threading.Lock()
_servers = {}       # id -> dict
# Parties hors ligne recemment vues (cle stable steamId ou ip:port), conservees OFFLINE_DAYS jours
# et sauvegardees sur disque pour survivre a un redemarrage du service.
OFFLINE_DAYS = 7
MAX_OFFLINE = 300
OFFLINE_FILE = os.environ.get("BAMP_OFFLINE_FILE") or os.path.join(os.environ.get("STATE_DIRECTORY", "/var/lib/bamp-master"), "offline.json")
_offline = {}       # cle -> dict public (sans token / ip)
_PUBLIC_KEYS = ("name", "hostName", "description", "port", "steamId", "players", "maxPlayers", "inLobby",
                "modVersion", "gameVersion", "hasPassword", "day", "language", "worldId", "rules", "bsbVersion")


def _server_key(s):
    return ("steam:" + s["steamId"]) if s.get("steamId") else ("ip:%s:%s" % (s.get("ip", ""), s.get("port", 0)))


def _load_offline():
    try:
        with open(OFFLINE_FILE, "r", encoding="utf-8") as fh:
            data = json.load(fh)
        if isinstance(data, dict):
            for k, v in data.items():
                if isinstance(v, dict) and isinstance(v.get("lastSeen"), (int, float)):
                    _offline[str(k)[:64]] = {kk: v.get(kk) for kk in _PUBLIC_KEYS + ("lastSeen",)}
    except (OSError, ValueError):
        pass


def _save_offline():
    try:
        os.makedirs(os.path.dirname(OFFLINE_FILE), exist_ok=True)
        tmp = OFFLINE_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as fh:
            json.dump(_offline, fh)
        os.replace(tmp, OFFLINE_FILE)
    except OSError:
        pass


def _go_offline(s, now):
    """Passe une partie en ligne dans la liste hors ligne (appele sous _lock)."""
    snap = {k: s.get(k) for k in _PUBLIC_KEYS}
    snap["players"] = 0
    snap["lastSeen"] = s.get("lastSeen", now)
    _offline[_server_key(s)] = snap
    if len(_offline) > MAX_OFFLINE:
        for k in sorted(_offline, key=lambda k: _offline[k]["lastSeen"])[:len(_offline) - MAX_OFFLINE]:
            del _offline[k]


def _clean_str(v, n):
    if not isinstance(v, str):
        return ""
    v = "".join(c for c in v if c.isprintable() and c not in "<>\"'`\\")
    return v.strip()[:n]


def _clean_int(v, lo, hi, default=0):
    try:
        v = int(v)
    except (TypeError, ValueError):
        return default
    return max(lo, min(hi, v))


def _version_info():
    """Version publiee du mod (version.json a cote de master.py), ou None si rien n'est publie."""
    try:
        with open(os.path.join(DIST_DIR, "version.json"), "r", encoding="utf-8-sig") as fh:
            data = json.load(fh)
    except (OSError, ValueError):
        return None
    if not isinstance(data, dict):
        return None
    v = str(data.get("version", "")).strip()
    if not _VERSION_RE.match(v):
        return None
    dll = os.path.join(DIST_DIR, DLL_NAME)
    has_dll = os.path.isfile(dll) and 0 < os.path.getsize(dll) <= MAX_DLL_BYTES
    return {"version": v, "notes": _clean_str(data.get("notes"), 200), "download": ("/download/" + DLL_NAME) if has_dll else ""}


def _clean_names(v):
    if not isinstance(v, list):
        return []
    out = []
    for n in v[:64]:
        n = _clean_str(n, 24)
        if n:
            out.append(n)
            if len(out) >= 16:
                break
    return out


def _fields(body):
    steam_id = _clean_str(body.get("steamId"), 20)
    if steam_id and not (steam_id.isdigit() and len(steam_id) == 17):
        steam_id = ""
    return {
        "name": _clean_str(body.get("name"), 48) or "Partie Big Ambitions",
        "hostName": _clean_str(body.get("hostName"), 32),
        "description": _clean_str(body.get("description"), 120),
        "port": _clean_int(body.get("port"), 0, 65535),
        "steamId": steam_id,
        "players": _clean_int(body.get("players"), 0, 64, 1),
        "maxPlayers": _clean_int(body.get("maxPlayers"), 0, 64, 0),
        "inLobby": bool(body.get("inLobby", True)),
        "modVersion": _clean_str(body.get("modVersion"), 24),
        "gameVersion": _clean_str(body.get("gameVersion"), 32),
        "hasPassword": bool(body.get("hasPassword", False)),
        "day": _clean_int(body.get("day"), 0, 1000000, 0),
        "language": "".join(c for c in _clean_str(body.get("language"), 8).upper() if c.isalpha()),
        "worldId": "".join(c for c in _clean_str(body.get("worldId"), 64) if c.isalnum()),
        "rules": _clean_str(body.get("rules"), 300),
        "bsbVersion": _clean_str(body.get("bsbVersion"), 24),
        "playerNames": _clean_names(body.get("playerNames")),
    }


def _purge(now):
    changed = False
    dead = [k for k, s in _servers.items() if now - s["lastSeen"] > TTL]
    for k in dead:
        _go_offline(_servers.pop(k), now)
        changed = True
    old = [k for k, s in _offline.items() if now - s["lastSeen"] > OFFLINE_DAYS * 86400]
    for k in old:
        del _offline[k]
        changed = True
    if changed:
        _save_offline()


def _public(s, now):
    out = {k: v for k, v in s.items() if k not in ("token", "lastSeen", "created")}
    out["ip"] = s["ip"] if s["port"] > 0 else ""
    out["age"] = int(now - s["created"])
    out["online"] = True
    return out


def _public_offline(s, now):
    out = {k: s.get(k) for k in _PUBLIC_KEYS}
    out.update(id="", ip="", age=0, online=False, offlineFor=int(now - s["lastSeen"]), playerNames=[])
    return out


def _group_dir(key):
    if not key or len(key) < 6 or len(key) > 128:
        return None
    return os.path.join(WORLDS_DIR, hashlib.sha256(key.encode("utf-8")).hexdigest()[:32])


_rate = {}
_rlock = threading.Lock()


def _rate_ok(ip):
    now = time.time()
    with _rlock:
        win = [t for t in _rate.get(ip, []) if now - t < 60]
        if len(win) >= RATE_PER_MIN:
            _rate[ip] = win
            return False
        win.append(now)
        _rate[ip] = win
        if len(_rate) > 5000:
            for k in [k for k, v in _rate.items() if not v or now - v[-1] > 60]:
                del _rate[k]
        return True


def _hash(v):
    return hashlib.sha256(("bamp-admin:" + v).encode("utf-8")).hexdigest()


def _admin_state(g, admin_key):
    """(has_admin, is_admin). Revendique le groupe si aucune cle admin n'est encore enregistree."""
    f = os.path.join(g, "group.json")
    try:
        with open(f, encoding="utf-8") as fh:
            stored = json.load(fh).get("adminHash", "")
    except Exception:
        stored = ""
    if not stored:
        if admin_key and 8 <= len(admin_key) <= 128:
            with _wlock:
                os.makedirs(g, exist_ok=True)
                if not os.path.isfile(f):
                    with open(f, "w", encoding="utf-8") as fh:
                        json.dump({"adminHash": _hash(admin_key), "claimedAt": int(time.time())}, fh)
            return True, True
        return False, False
    return True, bool(admin_key) and secrets.compare_digest(stored, _hash(admin_key))


def _read_meta(d):
    try:
        with open(os.path.join(d, "meta.json"), encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return None


class Handler(BaseHTTPRequestHandler):
    server_version = "BAMPMaster"
    sys_version = ""                 # ne pas annoncer la version de Python
    protocol_version = "HTTP/1.1"
    timeout = SOCKET_TIMEOUT

    def handle_one_request(self):
        # limite globale de debit par IP + nombre de connexions simultanees
        if not _slots.acquire(blocking=False):
            self.close_connection = True
            return
        try:
            super().handle_one_request()
        finally:
            _slots.release()

    def parse_request(self):
        if not super().parse_request():
            return False
        if self.path != "/health" and not _global_rate_ok(self._ip()):
            self._send(429, {"error": "too many requests"})
            return False
        if len(self.path) > 256:
            self._send(414, {"error": "uri too long"})
            return False
        return True

    def send_error(self, code, message=None, explain=None):
        # reponses d'erreur courtes, sans page HTML ni details internes
        try:
            self._send(code, {"error": "method not allowed" if code == 501 else ("bad request" if code < 500 else "server error")})
        except Exception:
            pass

    def log_message(self, fmt, *args):
        print("%s %s" % (self._ip(), fmt % args), flush=True)

    def _ip(self):
        if TRUST_PROXY:
            fwd = self.headers.get("X-Forwarded-For", "")
            if fwd:
                return fwd.split(",")[0].strip()
        return self.client_address[0]

    def _send(self, code, obj):
        data = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def _send_file(self, path, name):
        with open(path, "rb") as fh:
            data = fh.read(MAX_DLL_BYTES + 1)
        if len(data) > MAX_DLL_BYTES:
            return self._send(404, {"error": "not found"})
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Disposition", 'attachment; filename="%s"' % name)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def _body(self):
        n = int(self.headers.get("Content-Length") or 0)
        if n <= 0 or n > MAX_BODY:
            return None
        try:
            obj = json.loads(self.rfile.read(n).decode("utf-8"))
            return obj if isinstance(obj, dict) else None
        except Exception:
            return None

    def _id_from_path(self):
        parts = self.path.split("?")[0].strip("/").split("/")
        if len(parts) == 3 and parts[0] == "api" and parts[1] == "servers":
            return parts[2]
        return None

    def _world_route(self):
        parts = self.path.split("?")[0].strip("/").split("/")
        if len(parts) >= 2 and parts[0] == "api" and parts[1] == "worlds":
            if not WORLDS_ENABLED:
                self._send(404, {"error": "world storage disabled on this server"})
                return False
            return parts[2] if len(parts) == 3 else ""
        return None

    def do_GET(self):
        path = self.path.split("?")[0].rstrip("/")
        if path == "/health":
            return self._send(200, {"ok": True})
        if path == "/api/version":
            info = _version_info()
            return self._send(200, info) if info else self._send(404, {"error": "no version published"})
        if path == "/download/" + DLL_NAME:
            dll = os.path.join(DIST_DIR, DLL_NAME)
            if not os.path.isfile(dll):
                return self._send(404, {"error": "not found"})
            return self._send_file(dll, DLL_NAME)
        wid = self._world_route()
        if wid is False:
            return
        if wid is not None:
            if not _rate_ok(self._ip()):
                return self._send(429, {"error": "too many requests"})
            g = _group_dir(self.headers.get("X-Share-Key", ""))
            if not g:
                return self._send(403, {"error": "share key required (>= 6 chars)"})
            if wid == "":
                has_admin, is_admin = _admin_state(g, self.headers.get("X-Admin-Key", ""))
                out = []
                if os.path.isdir(g):
                    now = time.time()
                    for name in os.listdir(g):
                        wd = os.path.join(g, name)
                        m = _read_meta(wd)
                        if not m:
                            continue
                        if m.get("deleted") and now - m.get("deletedAt", now) > TRASH_DAYS * 86400:
                            with _wlock:
                                shutil.rmtree(wd, ignore_errors=True)
                            continue
                        out.append(m)
                out.sort(key=lambda m: -m.get("savedAt", 0))
                return self._send(200, {"worlds": out, "hasAdmin": has_admin, "isAdmin": is_admin})
            if not _ID_RE.match(wid):
                return self._send(400, {"error": "bad id"})
            f = os.path.join(g, wid, "current.bin")
            if (_read_meta(os.path.join(g, wid)) or {}).get("deleted"):
                return self._send(410, {"error": "world deleted"})
            if not os.path.isfile(f):
                return self._send(404, {"error": "unknown world"})
            with open(f, "rb") as fh:
                data = fh.read()
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("X-Saved-At", str((_read_meta(os.path.join(g, wid)) or {}).get("savedAt", 0)))
            self.end_headers()
            self.wfile.write(data)
            return
        if path == "/api/servers":
            now = time.time()
            want_offline = "offline=1" in (self.path.split("?", 1) + [""])[1]
            with _lock:
                _purge(now)
                lst = [_public(s, now) for s in _servers.values()]
                if want_offline:
                    online_keys = {_server_key(s) for s in _servers.values()}
                    online_worlds = {s.get("worldId") for s in _servers.values() if s.get("worldId")}
                    # un meme monde relance par un autre hote : on ne garde que l'entree la plus recente
                    newest = {}
                    for k, s in _offline.items():
                        if k in online_keys:
                            continue
                        w = s.get("worldId") or ""
                        if w and w in online_worlds:
                            continue
                        dk = w or ("key:" + k)
                        if dk not in newest or s["lastSeen"] > newest[dk]["lastSeen"]:
                            newest[dk] = s
                    lst += [_public_offline(s, now) for s in newest.values()]
            lst.sort(key=lambda s: (not s["online"], -s["players"], (s["name"] or "").lower()))
            return self._send(200, {"servers": lst, "ttl": TTL})
        self._send(404, {"error": "not found"})

    def do_POST(self):
        body = self._body()
        if body is None:
            return self._send(400, {"error": "bad body"})
        path = self.path.split("?")[0].rstrip("/")
        ip = self._ip()
        now = time.time()
        parts = path.strip("/").split("/")
        if len(parts) == 4 and parts[0] == "api" and parts[1] == "worlds" and parts[3] == "restore":
            return self._restore_world(parts[2])
        if path == "/api/servers":
            f = _fields(body)
            if f["port"] == 0 and not f["steamId"]:
                return self._send(400, {"error": "port or steamId required"})
            with _lock:
                _purge(now)
                # meme hote qui se re-enregistre : on remplace l'ancienne entree
                for k, s in list(_servers.items()):
                    if s["ip"] == ip and ((f["steamId"] and s["steamId"] == f["steamId"]) or (f["port"] and s["port"] == f["port"])):
                        del _servers[k]
                if len(_servers) >= MAX_TOTAL:
                    return self._send(503, {"error": "list full"})
                if sum(1 for s in _servers.values() if s["ip"] == ip) >= MAX_PER_IP:
                    return self._send(429, {"error": "too many servers for this ip"})
                sid = secrets.token_hex(8)
                token = secrets.token_hex(16)
                _servers[sid] = dict(f, id=sid, ip=ip, token=token, lastSeen=now, created=now)
                if _offline.pop(_server_key(_servers[sid]), None) is not None:
                    _save_offline()
            return self._send(201, {"id": sid, "token": token, "ttl": TTL, "ip": ip})
        sid = self._id_from_path()
        if sid:
            with _lock:
                s = _servers.get(sid)
                if not s or not secrets.compare_digest(s["token"], self.headers.get("X-Token", "")):
                    return self._send(404, {"error": "unknown server"})
                f = _fields(body)
                s.update(f)
                s["lastSeen"] = now
            return self._send(200, {"ok": True, "ttl": TTL})
        self._send(404, {"error": "not found"})

    def do_PUT(self):
        wid = self._world_route()
        if wid is False:
            return
        if not wid or not _ID_RE.match(wid):
            return self._send(404, {"error": "not found"})
        if not _rate_ok(self._ip()):
            return self._send(429, {"error": "too many requests"})
        g = _group_dir(self.headers.get("X-Share-Key", ""))
        if not g:
            return self._send(403, {"error": "share key required (>= 6 chars)"})
        n = int(self.headers.get("Content-Length") or 0)
        if n <= 0 or n > MAX_WORLD_BYTES:
            return self._send(413, {"error": "bad size"})
        try:
            saved_at = int(self.headers.get("X-Saved-At", "0"))
        except ValueError:
            saved_at = 0
        data = self.rfile.read(n)
        if len(data) != n or data[:2] != b"\x1f\x8b":
            return self._send(400, {"error": "expected gzip body"})
        d = os.path.join(g, wid)
        with _wlock:
            os.makedirs(g, exist_ok=True)
            old = _read_meta(d)
            if old and old.get("deleted") and saved_at <= old.get("deletedAt", 0):
                return self._send(410, {"error": "world deleted", "deletedAt": old.get("deletedAt", 0)})
            if old and not old.get("deleted") and old.get("savedAt", 0) >= saved_at:
                return self._send(409, {"error": "older or same version", "savedAt": old.get("savedAt", 0)})
            if not old and len([x for x in os.listdir(g) if os.path.isdir(os.path.join(g, x))]) >= MAX_WORLDS_PER_GROUP:
                return self._send(507, {"error": "too many worlds in this group"})
            os.makedirs(d, exist_ok=True)
            cur = os.path.join(d, "current.bin")
            if os.path.isfile(cur) and old:
                shutil.move(cur, os.path.join(d, "v%d.bin" % old.get("savedAt", 0)))
                vs = sorted(x for x in os.listdir(d) if x.startswith("v") and x.endswith(".bin"))
                for x in vs[:-KEEP_VERSIONS]:
                    os.remove(os.path.join(d, x))
            tmp = cur + ".tmp"
            with open(tmp, "wb") as fh:
                fh.write(data)
            os.replace(tmp, cur)
            meta = {
                "id": wid,
                "savedAt": saved_at,
                "worldDay": _clean_int(self.headers.get("X-World-Day"), -1, 100000, -1),
                "name": _clean_str(self.headers.get("X-World-Name", ""), 64),
                "players": _clean_str(self.headers.get("X-Players", ""), 200),
                "uploader": _clean_str(self.headers.get("X-Uploader", ""), 32),
                "size": n,
                "uploadedAt": int(time.time()),
            }
            with open(os.path.join(d, "meta.json"), "w", encoding="utf-8") as fh:
                json.dump(meta, fh)
        return self._send(200, {"ok": True, "savedAt": saved_at})

    def do_HEAD(self):
        self._send(405, {"error": "method not allowed"})

    def do_OPTIONS(self):
        self._send(405, {"error": "method not allowed"})

    def _restore_world(self, wid):
        if not WORLDS_ENABLED:
            return self._send(404, {"error": "world storage disabled on this server"})
        if not wid or not _ID_RE.match(wid):
            return self._send(404, {"error": "not found"})
        if not _rate_ok(self._ip()):
            return self._send(429, {"error": "too many requests"})
        g = _group_dir(self.headers.get("X-Share-Key", ""))
        if not g:
            return self._send(403, {"error": "share key required (>= 6 chars)"})
        has_admin, is_admin = _admin_state(g, self.headers.get("X-Admin-Key", ""))
        if not is_admin:
            return self._send(403, {"error": "admin key required to restore"})
        d = os.path.join(g, wid)
        with _wlock:
            m = _read_meta(d)
            if not m:
                return self._send(404, {"error": "unknown world"})
            if m.get("deleted"):
                v = os.path.join(d, "v%d.bin" % m.get("savedAt", 0))
                if not os.path.isfile(v):
                    return self._send(410, {"error": "backup no longer available"})
                shutil.move(v, os.path.join(d, "current.bin"))
                m["deleted"] = False
                m.pop("deletedAt", None)
                m.pop("deletedBy", None)
                with open(os.path.join(d, "meta.json"), "w", encoding="utf-8") as fh:
                    json.dump(m, fh)
        return self._send(200, {"ok": True})

    def do_DELETE(self):
        wid = self._world_route()
        if wid is False:
            return
        if wid is not None:
            if not wid or not _ID_RE.match(wid):
                return self._send(404, {"error": "not found"})
            if not _rate_ok(self._ip()):
                return self._send(429, {"error": "too many requests"})
            g = _group_dir(self.headers.get("X-Share-Key", ""))
            if not g:
                return self._send(403, {"error": "share key required (>= 6 chars)"})
            has_admin, is_admin = _admin_state(g, self.headers.get("X-Admin-Key", ""))
            if not is_admin:
                return self._send(403, {"error": "admin key required to delete"})
            d = os.path.join(g, wid)
            with _wlock:
                m = _read_meta(d)
                if not m:
                    return self._send(404, {"error": "unknown world"})
                if not m.get("deleted"):
                    cur = os.path.join(d, "current.bin")
                    if os.path.isfile(cur):
                        shutil.move(cur, os.path.join(d, "v%d.bin" % m.get("savedAt", 0)))
                    m["deleted"] = True
                    m["deletedAt"] = int(time.time())
                    m["deletedBy"] = _clean_str(self.headers.get("X-Uploader", ""), 32)
                    with open(os.path.join(d, "meta.json"), "w", encoding="utf-8") as fh:
                        json.dump(m, fh)
            return self._send(200, {"ok": True, "deletedAt": m.get("deletedAt")})
        sid = self._id_from_path()
        if not sid:
            return self._send(404, {"error": "not found"})
        with _lock:
            s = _servers.get(sid)
            if s and secrets.compare_digest(s["token"], self.headers.get("X-Token", "")):
                _go_offline(_servers.pop(sid), time.time())
                _save_offline()
                return self._send(200, {"ok": True})
        self._send(404, {"error": "unknown server"})


if __name__ == "__main__":
    _load_offline()
    srv = ThreadingHTTPServer((HOST, PORT), Handler)
    srv.daemon_threads = True
    print("BAMP master listening on %s:%d" % (HOST, PORT), flush=True)
    srv.serve_forever()
