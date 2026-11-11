using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BigAmbitionsMP;
using Steamworks;
using UnityEngine;
using UnityEngine.EventSystems;

namespace BAMPServerBrowser
{
    public class ServerBrowser : MonoBehaviour
    {
        // ---------- état général ----------
        private readonly ConcurrentQueue<Action> _main = new ConcurrentQueue<Action>();
        private BrowserConfig Cfg => BrowserConfig.Current;
        private string _bampVersion = "?";

        // ---------- liste ----------
        private bool _show;
        private bool _autoShown;
        private bool _wasJoinDialog;
        private List<ServerEntry> _servers = new List<ServerEntry>();
        private string _listStatus = "";
        private bool _fetching;
        private float _nextAutoRefresh;
        private string _selectedId = "";
        private string _filter = "";
        private Vector2 _scroll;
        private string _joinMsg = "";

        // ---------- publication (hôte) ----------
        private string _regId, _regToken, _regIp = "";
        private bool _regBusy;
        private float _nextRegTry, _nextBeat;
        private string _hostStatus = "";
        private bool _wasHosting;
        private string _editName, _editDesc;

        // ---------- UI ----------
        private float _scale = 1f;
        private Rect _listRect = new Rect(20, 90, 640, 800);
        private Rect _hostRect = new Rect(20, 230, 440, 400);
        private bool _hostRectPlaced;
        private bool _styles;
        private GUIStyle _win, _row, _rowSel, _small, _title, _bad, _good, _box;
        private Texture2D _dotOn, _dotOff;
        private bool _showShareCode;
        private EventSystem _blockedEs;
        private bool _showIp;
        private WorldSync _sync;
        private string _editKey;
        private string _editPw;
        private string _joinPw = "";
        private string _pwForId = "";

        // Champs texte maison : l'IMGUI ne reçoit pas le clavier dans ce jeu,
        // on lit Input.inputString comme le fait le mod multijoueur.
        private int _focus;              // 0 = aucun, 1 = recherche, 2 = nom, 3 = description
        private string _focusBuf = "";
        private int _focusMax;
        private GUIStyle _field, _fieldFocus;

        private void Awake()
        {
            try
            {
                var f = typeof(MPServer).Assembly.GetType("BigAmbitionsMP.MyPluginInfo")?.GetField("PLUGIN_VERSION", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f != null) _bampVersion = (f.IsLiteral ? f.GetRawConstantValue() : f.GetValue(null)) as string ?? "?";
            }
            catch { }
            _editName = Cfg.serverName;
            _editKey = Cfg.shareKey;
            _editPw = Cfg.joinPassword;
            _sync = new WorldSync(a => _main.Enqueue(a));
            _editDesc = Cfg.description;
        }

        private void Update()
        {
            while (_main.TryDequeue(out var a)) { try { a(); } catch (Exception ex) { ModEntry.Warn(ex.ToString()); } }

            if (Input.GetKeyDown(Cfg.ToggleKeyCode))
            {
                _show = !_show;
                _autoShown = false;
                if (_show) Refresh();
            }

            // ouverture / fermeture auto avec la boîte "Join a Game" du mod multijoueur
            bool jd = Bridge.JoinDialogVisible;
            if (jd && !_wasJoinDialog && !_show) { _show = true; _autoShown = true; _joinMsg = ""; Refresh(); }
            if (!jd && _wasJoinDialog && _autoShown) { _show = false; _autoShown = false; }
            _wasJoinDialog = jd;

            if (_show && !_fetching && Time.unscaledTime >= _nextAutoRefresh) Refresh();

            TickTyping();
            TickPublish();
            try
            {
                bool conn = false;
                try { conn = MPClient.IsConnected || MPClient.IsConnecting; } catch { }
                TickLocalStore();
                _sync.Tick(Bridge.InMainMenu, Hosting, conn);
            }
            catch (Exception ex) { ModEntry.Warn("world sync: " + ex.Message); }
            UpdateClickBlock();
        }

        // =====================================================================
        //  Publication de la partie hébergée
        // =====================================================================
        private bool Hosting { get { try { return MPServer.IsRunning; } catch { return false; } } }

        private HostInfo BuildHostInfo()
        {
            string host = "";
            try { host = MPConfig.PlayerId ?? ""; } catch { }
            string steam = "";
            try { if (SteamClient.IsValid) steam = ((ulong)SteamClient.SteamId).ToString(); } catch { }
            int port = 0, players = 1;
            bool lobby = true;
            try { port = MPServer.BoundPort; players = MPServer.ConnectedCount + 1; lobby = MPServer.IsInLobby; } catch { }
            int day = 0;
            try { day = SaveGameManager.Current?.Day ?? 0; } catch { }
            string name = string.IsNullOrWhiteSpace(Cfg.serverName) ? (host.Length > 0 ? host + "'s game" : "Big Ambitions game") : Cfg.serverName.Trim();
            return new HostInfo
            {
                name = name,
                hostName = host,
                description = Cfg.description ?? "",
                port = port,
                steamId = steam,
                players = players,
                maxPlayers = Cfg.maxPlayers,
                inLobby = lobby,
                modVersion = _bampVersion,
                gameVersion = Application.version,
                hasPassword = JoinPassword.HostHasPassword,
                day = day,
                language = Cfg.language ?? "",
            };
        }

        private void TickPublish()
        {
            bool hosting = Hosting;
            if (hosting && !_wasHosting) { _nextRegTry = Time.unscaledTime + 1.5f; _hostStatus = "Hosting detected…"; }
            _wasHosting = hosting;

            bool want = hosting && Cfg.publish;
            if (!want)
            {
                if (_regId != null) UnregisterAsync();
                if (!hosting) _hostStatus = "";
                else _hostStatus = "Not listed (publishing disabled).";
                return;
            }
            if (_regBusy) return;
            float now = Time.unscaledTime;

            if (_regId == null)
            {
                if (now < _nextRegTry) return;
                _regBusy = true;
                _hostStatus = "Registering on the list…";
                var info = BuildHostInfo();
                MasterClient.RegisterAsync(info).ContinueWith(t => _main.Enqueue(() =>
                {
                    _regBusy = false;
                    var (rep, err) = t.Result;
                    if (rep == null || string.IsNullOrEmpty(rep.id))
                    {
                        _hostStatus = "Registration failed: " + err + " (retrying in 20 s)";
                        _nextRegTry = Time.unscaledTime + 20f;
                        ModEntry.Warn("register failed: " + err);
                        return;
                    }
                    if (!Hosting || !Cfg.publish) { var id = rep.id; var tk = rep.token; Task.Run(() => MasterClient.Unregister(id, tk)); return; }
                    _regId = rep.id; _regToken = rep.token; _regIp = rep.ip ?? "";
                    _nextBeat = Time.unscaledTime + 20f;
                    _hostStatus = "Game listed publicly.";
                    ModEntry.Info($"registered '{info.name}' id={rep.id} ip={rep.ip} port={info.port} steam={(info.steamId.Length > 0 ? "yes" : "no")}");
                }));
                return;
            }

            if (now >= _nextBeat)
            {
                _regBusy = true;
                _nextBeat = now + 20f;
                string id = _regId, tok = _regToken;
                MasterClient.HeartbeatAsync(id, tok, BuildHostInfo()).ContinueWith(t => _main.Enqueue(() =>
                {
                    _regBusy = false;
                    int st = t.Result;
                    if (st == 200) { _hostStatus = "Game listed publicly."; return; }
                    if (st == 404) { _regId = null; _nextRegTry = 0; _hostStatus = "Entry expired, registering again…"; return; }
                    _hostStatus = "List unreachable (" + (st == 0 ? "network" : "HTTP " + st) + "), retrying…";
                    _nextBeat = Time.unscaledTime + 10f;
                }));
            }
        }

        private void PushUpdateNow() { if (_regId != null) _nextBeat = 0; }

        private void UnregisterAsync()
        {
            string id = _regId, tok = _regToken;
            _regId = null; _regToken = null;
            if (id != null) Task.Run(() => MasterClient.Unregister(id, tok));
        }

        public void UnregisterNow()
        {
            string id = _regId, tok = _regToken;
            _regId = null; _regToken = null;
            if (id != null) { try { MasterClient.Unregister(id, tok); } catch { } }
        }

        private void OnApplicationQuit() => UnregisterNow();
        private void OnDestroy() { UnregisterNow(); SetBlock(false); LocalWorldStore.Stop(); }

        // Serveur de sauvegarde intégré : actif quand ce PC est le serveur du groupe (mode par défaut).
        private float _nextStoreCheck;
        private bool _ipAsked;
        private void TickLocalStore()
        {
            if (Time.unscaledTime < _nextStoreCheck) return;
            _nextStoreCheck = Time.unscaledTime + 5f;
            if (Cfg.IsLocalStorageOwner)
            {
                if (!LocalWorldStore.Running || LocalWorldStore.Port != Cfg.localStoragePort)
                {
                    LocalWorldStore.Start(Cfg.localStoragePort);
                    if (!LocalWorldStore.Running) _nextStoreCheck = Time.unscaledTime + 60f;
                }
                if (!_ipAsked) { _ipAsked = true; try { MPNet.FetchPublicIpAsync(); } catch { } }
            }
            else if (LocalWorldStore.Running) LocalWorldStore.Stop();
        }

        private static string PublicIp() { try { return MPNet.PublicIp ?? ""; } catch { return ""; } }

        // =====================================================================
        //  Liste des parties
        // =====================================================================
        private void Refresh()
        {
            if (_fetching) return;
            _fetching = true;
            _listStatus = "Loading…";
            MasterClient.FetchAsync().ContinueWith(t => _main.Enqueue(() =>
            {
                _fetching = false;
                _nextAutoRefresh = Time.unscaledTime + 15f;
                var (list, err) = t.Result;
                if (list == null) { _listStatus = "List unreachable: " + err; return; }
                _servers = list;
                _listStatus = "";
            }));
        }

        private static string OwnSteamId()
        {
            try { return SteamClient.IsValid ? ((ulong)SteamClient.SteamId).ToString() : ""; } catch { return ""; }
        }

        private void Join(ServerEntry s, bool forceIp)
        {
            string own = OwnSteamId();
            bool steamOk = !string.IsNullOrEmpty(s.steamId) && s.steamId != own && own.Length > 0;
            bool ipOk = !string.IsNullOrEmpty(s.ip) && s.port > 0;
            string addr, port = s.port > 0 ? s.port.ToString() : "7777";
            if (forceIp && ipOk) addr = s.ip;
            else if (steamOk && (Cfg.preferSteam || !ipOk)) addr = "steam:" + s.steamId;
            else if (ipOk) addr = s.ip;
            else { _joinMsg = "This game can't be reached through Steam or IP."; return; }

            if (!s.online) { _joinMsg = "This game is offline."; return; }
            if (Hosting) { _joinMsg = "You are already hosting a game."; return; }
            try { if (MPClient.IsConnected || MPClient.IsConnecting) { _joinMsg = "Already connected / connecting."; return; } } catch { }

            JoinPassword.Pending = s.hasPassword ? (_pwForId == Key(s) ? _joinPw : "") : "";
            if (s.hasPassword && string.IsNullOrEmpty(JoinPassword.Pending)) { _joinMsg = "This game is password protected: enter the password."; return; }
            string err = Bridge.Join(addr, port);
            if (err != null) { _joinMsg = err; return; }
            ModEntry.Info("join '" + s.name + "' via " + addr + (addr.StartsWith("steam:") ? "" : ":" + port));
            _joinMsg = "";
            _show = false;
            _autoShown = false;
        }

        // =====================================================================
        //  IMGUI
        // =====================================================================
        private void InitStyles()
        {
            if (_styles) return;
            _styles = true;
            var bg = Tex(new Color(0.07f, 0.09f, 0.13f, 0.97f));
            _win = new GUIStyle(GUI.skin.window) { padding = new RectOffset(12, 12, 30, 12), fontSize = 15, fontStyle = FontStyle.Bold };
            _win.normal.background = bg; _win.onNormal.background = bg;
            _win.normal.textColor = _win.onNormal.textColor = Color.white;
            _row = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, richText = true, fontSize = 13, wordWrap = false, padding = new RectOffset(8, 8, 4, 4) };
            _row.normal.background = Tex(new Color(0.13f, 0.16f, 0.22f, 1f));
            _row.hover.background = Tex(new Color(0.18f, 0.22f, 0.30f, 1f));
            _row.normal.textColor = _row.hover.textColor = Color.white;
            _rowSel = new GUIStyle(_row);
            _rowSel.normal.background = _rowSel.hover.background = Tex(new Color(0.22f, 0.36f, 0.58f, 1f));
            _small = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true, richText = true };
            _small.normal.textColor = new Color(0.75f, 0.8f, 0.88f);
            _title = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
            _title.normal.textColor = Color.white;
            _field = new GUIStyle(GUI.skin.textField) { fontSize = 13, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            _field.normal.textColor = _field.hover.textColor = _field.active.textColor = Color.white;
            _fieldFocus = new GUIStyle(_field);
            _fieldFocus.normal.background = _fieldFocus.hover.background = _fieldFocus.active.background = Tex(new Color(0.16f, 0.22f, 0.34f, 1f));
            _bad = new GUIStyle(_small); _bad.normal.textColor = new Color(1f, 0.5f, 0.45f);
            _good = new GUIStyle(_small); _good.normal.textColor = new Color(0.5f, 0.9f, 0.55f);
            _title.richText = true;
            _row.padding = new RectOffset(28, 8, 4, 4);
            _rowSel.padding = new RectOffset(28, 8, 4, 4);
            _box = new GUIStyle(GUI.skin.box) { padding = new RectOffset(10, 10, 8, 8), margin = new RectOffset(0, 0, 6, 0) };
            _box.normal.background = Tex(new Color(0.11f, 0.14f, 0.20f, 1f));
            _dotOn = Dot(new Color(0.30f, 0.85f, 0.39f));
            _dotOff = Dot(new Color(0.42f, 0.45f, 0.50f));
        }

        private static Texture2D Dot(Color c)
        {
            const int n = 22;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            float r = n / 2f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r));
                    t.SetPixel(x, y, new Color(c.r, c.g, c.b, Mathf.Clamp01(r - d)));
                }
            t.Apply();
            return t;
        }

        private static Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c); t.Apply();
            return t;
        }

        private bool ShowHostPanel => Hosting && (SafeInLobby() || _show);
        private static bool SafeInLobby() { try { return MPServer.IsInLobby; } catch { return false; } }

        private void OnGUI()
        {
            if (!_show && !ShowHostPanel) return;
            InitStyles();
            _scale = Mathf.Max(0.5f, Screen.height / 1080f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(_scale, _scale, 1f));
            GUI.depth = -1000;
            if (!_hostRectPlaced)
            {
                _hostRectPlaced = true;
                if (Cfg.hostPanelX >= 0) { _hostRect.x = Cfg.hostPanelX; _hostRect.y = Cfg.hostPanelY; }
                if (Cfg.listPanelX >= 0) { _listRect.x = Cfg.listPanelX; _listRect.y = Cfg.listPanelY; }
            }
            float vw = Screen.width / _scale, vh = Screen.height / _scale;

            if (_show) _listRect = Clamp(GUI.Window(0x42A1, _listRect, DrawList, "Open multiplayer games", _win), vw, vh);
            if (ShowHostPanel) _hostRect = Clamp(GUI.Window(0x42A2, _hostRect, DrawHost, "Publish my game", _win), vw, vh);

            if (Event.current.type == EventType.MouseUp &&
                (_hostRect.x != Cfg.hostPanelX || _hostRect.y != Cfg.hostPanelY || _listRect.x != Cfg.listPanelX || _listRect.y != Cfg.listPanelY))
            {
                Cfg.hostPanelX = _hostRect.x; Cfg.hostPanelY = _hostRect.y;
                Cfg.listPanelX = _listRect.x; Cfg.listPanelY = _listRect.y;
                BrowserConfig.Save();
            }
        }

        private static Rect Clamp(Rect r, float vw, float vh)
        {
            r.x = Mathf.Clamp(r.x, 0, Mathf.Max(0, vw - r.width));
            r.y = Mathf.Clamp(r.y, 0, Mathf.Max(0, vh - r.height));
            return r;
        }

        // ---------- filtres / tri de la liste ----------
        private static readonly string[] SortNames = { "Online first", "Name", "Day", "Players", "Language" };

        private static string Key(ServerEntry s) =>
            s.online && !string.IsNullOrEmpty(s.id) ? s.id : "off:" + (s.steamId ?? "") + ":" + (s.ip ?? "") + ":" + s.port + ":" + (s.name ?? "");

        private static string Ago(int sec)
        {
            if (sec < 90) return "just now";
            if (sec < 3600) return (sec / 60) + " min ago";
            if (sec < 86400) return (sec / 3600) + " h ago";
            int d = sec / 86400;
            return d + (d == 1 ? " day ago" : " days ago");
        }

        private string SortDirLabel()
        {
            switch (Cfg.listSort)
            {
                case 1: case 4: return Cfg.listSortDesc ? "Z-A" : "A-Z";
                case 2: case 3: return Cfg.listSortDesc ? "Highest first" : "Lowest first";
                default: return Cfg.listSortDesc ? "Most players" : "Fewest players";
            }
        }

        private IEnumerable<ServerEntry> FilteredSorted()
        {
            IEnumerable<ServerEntry> rows = _servers;
            if (Cfg.listOnlineOnly) rows = rows.Where(s => s.online);
            if (!string.IsNullOrEmpty(Cfg.listLanguage)) rows = rows.Where(s => string.Equals(s.language, Cfg.listLanguage, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(_filter))
            {
                string f = _filter.Trim().ToLowerInvariant();
                rows = rows.Where(s => ((s.name ?? "") + " " + (s.hostName ?? "") + " " + (s.description ?? "")).ToLowerInvariant().Contains(f));
            }
            bool desc = Cfg.listSortDesc;
            Func<string, string> n = x => (x ?? "").ToLowerInvariant();
            IOrderedEnumerable<ServerEntry> o;
            switch (Cfg.listSort)
            {
                case 1: o = desc ? rows.OrderByDescending(s => n(s.name)) : rows.OrderBy(s => n(s.name)); break;
                case 2: o = desc ? rows.OrderByDescending(s => s.day) : rows.OrderBy(s => s.day); break;
                case 3: o = desc ? rows.OrderByDescending(s => s.players) : rows.OrderBy(s => s.players); break;
                case 4: o = desc ? rows.OrderByDescending(s => n(s.language)) : rows.OrderBy(s => n(s.language)); break;
                default:
                    o = rows.OrderBy(s => s.online ? 0 : 1);
                    o = desc ? o.ThenByDescending(s => s.players) : o.ThenBy(s => s.players);
                    o = o.ThenBy(s => s.offlineFor);
                    break;
            }
            return o.ThenBy(s => n(s.name));
        }

        private void DrawList(int id)
        {
            // ---- ligne 1 : recherche ----
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search name:", _small, GUILayout.Width(95));
            _filter = Field(1, _filter, 40);
            if (GUILayout.Button(_fetching ? "..." : "Refresh", GUILayout.Width(90))) Refresh();
            if (GUILayout.Button("X", GUILayout.Width(28))) { _show = false; _autoShown = false; }
            GUILayout.EndHorizontal();

            // ---- ligne 2 : filtres et tri ----
            GUILayout.BeginHorizontal();
            var langs = _servers.Select(s => (s.language ?? "").ToUpperInvariant()).Where(l => l.Length > 0).Distinct().OrderBy(l => l).ToList();
            if (Cfg.listLanguage.Length > 0 && !langs.Contains(Cfg.listLanguage)) langs.Insert(0, Cfg.listLanguage);
            if (GUILayout.Button("Language: " + (Cfg.listLanguage.Length > 0 ? Cfg.listLanguage : "All"), GUILayout.Width(130)))
            {
                int i = langs.IndexOf(Cfg.listLanguage);
                Cfg.listLanguage = i + 1 < langs.Count ? langs[i + 1] : "";
                BrowserConfig.Save();
            }
            if (GUILayout.Button("Sort: " + SortNames[Mathf.Clamp(Cfg.listSort, 0, SortNames.Length - 1)], GUILayout.Width(150)))
            {
                Cfg.listSort = (Mathf.Clamp(Cfg.listSort, 0, SortNames.Length - 1) + 1) % SortNames.Length;
                Cfg.listSortDesc = Cfg.listSort != 1 && Cfg.listSort != 4;
                BrowserConfig.Save();
            }
            if (GUILayout.Button(SortDirLabel(), GUILayout.Width(120))) { Cfg.listSortDesc = !Cfg.listSortDesc; BrowserConfig.Save(); }
            bool oo = GUILayout.Toggle(Cfg.listOnlineOnly, " Online only");
            if (oo != Cfg.listOnlineOnly) { Cfg.listOnlineOnly = oo; BrowserConfig.Save(); }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            var rows = FilteredSorted().ToList();
            int on = _servers.Count(s => s.online), off = _servers.Count - on;
            string status = _listStatus.Length > 0 ? _listStatus
                : _servers.Count == 0 ? "No games yet."
                : $"{on} online · {off} offline (seen in the last 7 days)" + (rows.Count != _servers.Count ? $" · {rows.Count} shown" : "");
            GUILayout.Label(status, _small);

            // ---- liste ----
            string own = OwnSteamId();
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            if (rows.Count == 0 && _servers.Count > 0) GUILayout.Label("No game matches these filters.", _small);
            foreach (var s in rows)
            {
                string key = Key(s);
                bool sel = key == _selectedId;
                bool mine = own.Length > 0 && s.steamId == own;
                string max = s.maxPlayers > 0 ? "/" + s.maxPlayers : "";
                string lang = string.IsNullOrEmpty(s.language) ? "" : $"<color=#8fc3ff>[{Esc(s.language)}]</color> ";
                string dayTxt = s.day > 0 ? $"  ·  Day {s.day}" : "";
                string line2;
                if (s.online)
                {
                    string state = s.inLobby ? "<color=#7fe08a>Lobby</color>" : "<color=#ffd24a>In game</color>";
                    string ver = s.modVersion == _bampVersion ? "" : $"  <color=#ff7a70>mod v{Esc(s.modVersion)}</color>";
                    line2 = $"{Esc(s.hostName)}  ·  {s.players}{max} player(s)  ·  {state}{dayTxt}{ver}";
                }
                else line2 = $"<color=#9aa3ad>{Esc(s.hostName)}  ·  Offline, last seen {Ago(s.offlineFor)}{dayTxt}</color>";
                string name = s.online ? $"<b>{Esc(s.name)}</b>" : $"<b><color=#b8c0cc>{Esc(s.name)}</color></b>";
                string label = $"{lang}{name}{(s.hasPassword ? "  <color=#ffd24a>[password]</color>" : "")}{(mine ? "  <color=#9ab>(you)</color>" : "")}\n<size=11>{line2}</size>";
                if (GUILayout.Button(label, sel ? _rowSel : _row, GUILayout.Height(44)))
                {
                    if (sel && Event.current.clickCount >= 2 && s.online) Join(s, false);
                    _selectedId = key;
                    _joinMsg = "";
                }
                if (Event.current.type == EventType.Repaint)
                {
                    var r = GUILayoutUtility.GetLastRect();
                    GUI.DrawTexture(new Rect(r.x + 9, r.y + 9, 11, 11), s.online ? _dotOn : _dotOff);
                }
            }
            GUILayout.EndScrollView();

            // ---- rejoindre la partie sélectionnée ----
            var cur = _servers.FirstOrDefault(s => Key(s) == _selectedId);
            GUILayout.BeginVertical(_box);
            if (cur == null) GUILayout.Label("Select a game in the list to join it.", _small);
            else
            {
                GUILayout.Label((cur.online ? "<color=#4cd964>Online</color>" : "<color=#9aa3ad>Offline</color>") + "  " + Esc(cur.name) +
                                (string.IsNullOrEmpty(cur.language) ? "" : "  ·  " + Esc(cur.language)) + (cur.day > 0 ? "  ·  Day " + cur.day : ""), _title);
                if (!string.IsNullOrEmpty(cur.description)) GUILayout.Label(Esc(cur.description), _small);
                if (!cur.online)
                {
                    string who = string.IsNullOrEmpty(cur.hostName) ? "the host" : Esc(cur.hostName);
                    GUILayout.Label($"This game is offline (last seen {Ago(cur.offlineFor)}). You can join it once {who} starts hosting again.", _small);
                }
                else
                {
                    if (cur.hasPassword)
                    {
                        if (_pwForId != Key(cur)) { _pwForId = Key(cur); _joinPw = ""; }
                        GUILayout.BeginHorizontal();
                        GUILayout.Label("Password:", _small, GUILayout.Width(95));
                        _joinPw = Field(6, _joinPw, 64, mask: true);
                        GUILayout.EndHorizontal();
                    }
                    if (JoinPassword.LastRefused) GUILayout.Label("Connection refused by the host: wrong password.", _bad);
                    if (cur.modVersion != _bampVersion)
                        GUILayout.Label($"Different mod version (host v{Esc(cur.modVersion)}, you v{_bampVersion}): the connection may be refused.", _bad);
                    GUILayout.BeginHorizontal();
                    bool canSteam = cur.steamId.Length > 0 && cur.steamId != own;
                    bool canIp = cur.ip.Length > 0 && cur.port > 0;
                    GUI.enabled = canSteam || canIp;
                    if (GUILayout.Button("Join", GUILayout.Height(34))) Join(cur, false);
                    GUI.enabled = canIp && canSteam;
                    if (GUILayout.Button(canIp ? $"Join by IP ({cur.ip}:{cur.port})" : "IP not available", GUILayout.Height(34))) Join(cur, true);
                    GUI.enabled = true;
                    GUILayout.EndHorizontal();
                }
            }
            if (_joinMsg.Length > 0) GUILayout.Label(_joinMsg, _bad);
            GUILayout.EndVertical();

            // ---- sauvegardes partagées ----
            GUILayout.Space(6);
            GUILayout.Label("Shared saves with friends", _title);
            GUILayout.Label("<size=11>Everyone who enters the same code shares the same multiplayer worlds. It is not a game password and not the admin key.</size>", _small);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Shared code:", _small, GUILayout.Width(95));
            _editKey = Field(4, _editKey, 64, mask: _focus != 4);
            bool keyDirty = (_editKey ?? "").Trim() != (Cfg.shareKey ?? "");
            GUI.enabled = keyDirty;
            if (GUILayout.Button("Save", GUILayout.Width(80)))
            {
                string k = (_editKey ?? "").Trim();
                int at = k.LastIndexOf('@');
                string kk = at > 0 ? k.Substring(0, at) : k;
                if (k.Length > 0 && kk.Length < 6) _joinMsg = "The shared code must be at least 6 characters long.";
                else { Cfg.shareKey = k; BrowserConfig.Save(); _sync.SyncNow(); _joinMsg = ""; }
            }
            GUI.enabled = WorldSync.Enabled && !_sync.Busy;
            if (GUILayout.Button(_sync.Busy ? "..." : "Sync now", GUILayout.Width(95))) _sync.SyncNow();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("<size=11>" + Esc(_sync.Status) + "</size>", _small);
            GUILayout.Label($"<size=10>{Cfg.toggleKey}: show/hide · Double-click to join · Green dot = online · The code for friends is in \"Publish my game\" while you host.</size>", _small);
            GUI.DragWindow(new Rect(0, 0, 10000, 28));
        }

        private void DrawHost(int id)
        {
            bool pub = GUILayout.Toggle(Cfg.publish, " Show my game in the public list");
            if (pub != Cfg.publish) { Cfg.publish = pub; BrowserConfig.Save(); if (pub) _nextRegTry = 0; }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Name:", _small, GUILayout.Width(80));
            _editName = Field(2, _editName, 48);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Description:", _small, GUILayout.Width(80));
            _editDesc = Field(3, _editDesc, 120);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Language:", _small, GUILayout.Width(80));
            {
                var langs = BrowserConfig.Languages;
                int li = Mathf.Max(0, System.Array.IndexOf(langs, Cfg.language));
                int ni = li;
                if (GUILayout.Button("<", GUILayout.Width(26))) ni = (li + langs.Length - 1) % langs.Length;
                GUILayout.Label(langs[li], _title, GUILayout.Width(36));
                if (GUILayout.Button(">", GUILayout.Width(26))) ni = (li + 1) % langs.Length;
                if (ni != li) { Cfg.language = langs[ni]; BrowserConfig.Save(); PushUpdateNow(); }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Password:", _small, GUILayout.Width(80));
            _editPw = Field(5, _editPw, 64, mask: _focus != 5);
            GUILayout.EndHorizontal();
            GUILayout.Label("<size=10>" + (string.IsNullOrEmpty(Cfg.joinPassword) ? "No password: anyone can join." : "Password set: only players who know it can join.") + "</size>", _small);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Max players:", _small, GUILayout.Width(80));
            if (GUILayout.Button("-", GUILayout.Width(26)) && Cfg.maxPlayers > 2) { Cfg.maxPlayers--; BrowserConfig.Save(); PushUpdateNow(); }
            GUILayout.Label(Cfg.maxPlayers.ToString(), _title, GUILayout.Width(30));
            if (GUILayout.Button("+", GUILayout.Width(26)) && Cfg.maxPlayers < 32) { Cfg.maxPlayers++; BrowserConfig.Save(); PushUpdateNow(); }
            GUILayout.FlexibleSpace();
            bool dirty = _editName != Cfg.serverName || _editDesc != Cfg.description || (_editPw ?? "") != (Cfg.joinPassword ?? "");
            GUI.enabled = dirty;
            if (GUILayout.Button("Apply", GUILayout.Width(90)))
            {
                Cfg.serverName = (_editName ?? "").Trim();
                Cfg.description = (_editDesc ?? "").Trim();
                Cfg.joinPassword = (_editPw ?? "").Trim();
                _editPw = Cfg.joinPassword;
                BrowserConfig.Save();
                PushUpdateNow();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            bool ok = _regId != null;
            GUILayout.Label(_hostStatus, ok ? _good : _small);
            if (WorldSync.Enabled)
            {
                string code = Cfg.ShareCode(PublicIp());
                GUILayout.BeginHorizontal();
                string shown = code.Length == 0 ? "detecting public IP..." : _showShareCode ? code : "*********";
                GUILayout.Label("<size=11>Shared-saves code for friends: <b>" + Esc(shown) + "</b></size>", _small);
                GUI.enabled = code.Length > 0;
                if (GUILayout.Button(_showShareCode ? "Hide" : "Show", GUILayout.Width(60))) _showShareCode = !_showShareCode;
                if (GUILayout.Button("Copy", GUILayout.Width(60))) { try { GUIUtility.systemCopyBuffer = code; } catch { } }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
                if (Cfg.IsLocalStorageOwner)
                    GUILayout.Label("<size=10>" + (LocalWorldStore.Running
                        ? "Saves are stored on this PC: friends need TCP port " + Cfg.localStoragePort + " open on your router."
                        : "Save server unavailable on port " + Cfg.localStoragePort + ": " + Esc(LocalWorldStore.LastError)) + "</size>",
                        LocalWorldStore.LastError.Length > 0 ? _bad : _small);
            }
            if (WorldSync.Enabled)
                GUILayout.Label("<size=11>Shared world: " + Esc(_sync.LastUpload.Length > 0 ? "last upload " + _sync.LastUpload : "uploaded after the next save") + "</size>", _small);
            if (ok)
            {
                int port = 0; try { port = MPServer.BoundPort; } catch { }
                string ipTxt = _showIp ? _regIp : "•••••••••";
                string via = (OwnSteamId().Length > 0 ? "Steam" : "") + (port > 0 ? $" / IP {ipTxt}:{port}" : "");
                if (port > 0 && GUILayout.Button(_showIp ? "Hide IP" : "Show IP", GUILayout.Width(110))) _showIp = !_showIp;
                GUILayout.Label("<size=11>Reachable via " + via + (port > 0 ? " (IP: forward the port on your router if UPnP is off)" : "") + "</size>", _small);
            }
            GUI.DragWindow(new Rect(0, 0, 10000, 28));
        }

        private string Field(int id, string value, int max, bool mask = false)
        {
            value = value ?? "";
            bool f = _focus == id;
            if (f) value = _focusBuf;
            bool caret = f && Mathf.FloorToInt(Time.unscaledTime * 2f) % 2 == 0;
            string shown = mask ? new string('•', value.Length) : value;
            if (GUILayout.Button(shown + (caret ? "|" : ""), f ? _fieldFocus : _field, GUILayout.Height(24)))
            {
                if (!f) { _focus = id; _focusBuf = value; _focusMax = max; }
            }
            return value;
        }

        private void TickTyping()
        {
            if (_focus == 0) return;
            bool visible = ((_focus == 1 || _focus == 4 || _focus == 6) && _show) || ((_focus == 2 || _focus == 3 || _focus == 5) && ShowHostPanel);
            if (!visible) { _focus = 0; return; }
            if (Input.GetMouseButtonDown(0))
            {
                // un clic hors du champ retire le focus (le clic sur un autre champ le redonne dans OnGUI)
                var m = Input.mousePosition;
                var p = new Vector2(m.x / _scale, (Screen.height - m.y) / _scale);
                if (!((_show && _listRect.Contains(p)) || (ShowHostPanel && _hostRect.Contains(p)))) { _focus = 0; return; }
            }
            if (Input.GetKeyDown(KeyCode.Escape)) { _focus = 0; return; }
            string buf = _focusBuf;
            if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKeyDown(KeyCode.V))
            {
                try { foreach (char c in GUIUtility.systemCopyBuffer ?? "") if (!char.IsControl(c)) buf += c; } catch { }
            }
            foreach (char c in Input.inputString)
            {
                if (c == (char)8) { if (buf.Length > 0) buf = buf.Substring(0, buf.Length - 1); }
                else if (c == (char)10 || c == (char)13) { _focus = 0; break; }
                else if (!char.IsControl(c)) buf += c;
            }
            if (buf.Length > _focusMax) buf = buf.Substring(0, _focusMax);
            _focusBuf = buf;
        }

        private static string Esc(string s) => (s ?? "").Replace("<", "‹").Replace(">", "›");

        // Empêche les clics sur nos fenêtres de traverser vers les boutons du jeu (uGUI).
        private void UpdateClickBlock()
        {
            bool over = false;
            if (_show || ShowHostPanel)
            {
                var m = Input.mousePosition;
                var p = new Vector2(m.x / _scale, (Screen.height - m.y) / _scale);
                over = (_show && _listRect.Contains(p)) || (ShowHostPanel && _hostRect.Contains(p));
            }
            SetBlock(over);
        }

        private void SetBlock(bool on)
        {
            try
            {
                if (on)
                {
                    if (_blockedEs == null && EventSystem.current != null && EventSystem.current.enabled)
                    {
                        _blockedEs = EventSystem.current;
                        _blockedEs.enabled = false;
                    }
                }
                else if (_blockedEs != null)
                {
                    _blockedEs.enabled = true;
                    _blockedEs = null;
                }
            }
            catch { _blockedEs = null; }
        }
    }

    /// <summary>Accès (lecture seule + boîte Join) à l'interface du mod multijoueur original.</summary>
    internal static class Bridge
    {
        private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static MPCanvasUI _ui;
        private static float _nextFind;
        private static readonly FieldInfo JoinDialogF = typeof(MPCanvasUI).GetField("_joinDialog", F);
        private static readonly FieldInfo JoinIpF = typeof(MPCanvasUI).GetField("_joinIp", F);
        private static readonly FieldInfo JoinPortF = typeof(MPCanvasUI).GetField("_joinPort", F);
        private static readonly MethodInfo OnMpJoinM = typeof(MPCanvasUI).GetMethod("OnMpJoin", F);
        private static readonly MethodInfo OnJoinConnectM = typeof(MPCanvasUI).GetMethod("OnJoinConnect", F);

        private static MPCanvasUI UI
        {
            get
            {
                if (_ui == null && Time.unscaledTime >= _nextFind)
                {
                    _nextFind = Time.unscaledTime + 1f;
                    _ui = UnityEngine.Object.FindObjectOfType<MPCanvasUI>();
                }
                return _ui;
            }
        }

        public static bool JoinDialogVisible
        {
            get
            {
                try
                {
                    var ui = UI;
                    if (ui == null || JoinDialogF == null) return false;
                    var go = JoinDialogF.GetValue(ui) as GameObject;
                    return go != null && go.activeInHierarchy;
                }
                catch { return false; }
            }
        }

        private static readonly FieldInfo MpButtonF = typeof(MPCanvasUI).GetField("_mpButton", F);
        private static readonly FieldInfo MpSubmenuF = typeof(MPCanvasUI).GetField("_mpSubmenu", F);

        /// <summary>Vrai quand le bouton "Multiplayer" (ou son sous-menu) du mod est visible = menu principal.</summary>
        public static bool InMainMenu
        {
            get
            {
                try
                {
                    var ui = UI;
                    if (ui == null) return false;
                    if (MpButtonF?.GetValue(ui) is GameObject b && b != null && b.activeInHierarchy) return true;
                    if (MpSubmenuF?.GetValue(ui) is List<GameObject> l && l.Exists(g => g != null && g.activeInHierarchy)) return true;
                }
                catch { }
                return false;
            }
        }

        /// <returns>null si OK, sinon un message d'erreur.</returns>
        public static string Join(string address, string port)
        {
            var ui = UI;
            if (ui == null) return "Multiplayer mod not found.";
            if (JoinIpF == null || JoinPortF == null || OnJoinConnectM == null || OnMpJoinM == null)
                return "This multiplayer mod version is not compatible with the server list (fields not found).";
            try
            {
                if (!JoinDialogVisible)
                {
                    if (!InMainMenu) return "Go back to the main menu to join a game.";
                    OnMpJoinM.Invoke(ui, null);
                    if (!JoinDialogVisible) return "Open Multiplayer > Join Game from the main menu, then try again.";
                }
                JoinIpF.SetValue(ui, address);
                JoinPortF.SetValue(ui, port);
                OnJoinConnectM.Invoke(ui, null);
                return null;
            }
            catch (Exception ex)
            {
                ModEntry.Warn("join: " + ex);
                return "Error: " + (ex.InnerException ?? ex).Message;
            }
        }
    }
}
