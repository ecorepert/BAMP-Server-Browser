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
        private Rect _listRect = new Rect(20, 90, 700, 840);
        private Rect _hostRect = new Rect(20, 230, 480, 540);
        private bool _hostRectPlaced;
        private bool _styles;
        private GUIStyle _win, _row, _rowSel, _small, _title, _bad, _good, _box, _radio;
        private Texture2D _dotOn, _dotOff;

        private EventSystem _blockedEs;
        private bool _showIp;
        private WorldSync _sync;
        private string _editPw;
        private string _joinPw = "";
        private string _pwPromptKey = "";
        private bool _pwPromptAdmin, _pwPromptIp, _pwPromptRelaunch;
        private string _editApw;

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
            _editApw = Cfg.adminPassword;
            SaveServerPrompt.OnAsk = ui => OpenSaveServerDialog(ui);
            AdminChannel.SettingsChanged = () =>
            {
                _editName = Cfg.serverName; _editDesc = Cfg.description; _editPw = Cfg.joinPassword; _editApw = Cfg.adminPassword;
                if (Cfg.publish) _nextRegTry = 0;
                PushUpdateNow();
            };
            _editPw = Cfg.joinPassword;
            _sync = new WorldSync(a => _main.Enqueue(a));
            _editDesc = Cfg.description;
        }

        private float _nextAdminTick;
        private bool _updateToasted;
        private bool _wasHostingAdmin;

        private void Update()
        {
            while (_main.TryDequeue(out var a)) { try { a(); } catch (Exception ex) { ModEntry.Warn(ex.ToString()); } }
            while (AdminChannel.Main.TryDequeue(out var a2)) { try { a2(); } catch (Exception ex) { ModEntry.Warn("[Admin] " + ex); } }
            if (JoinPassword.LastRefused && _pwPromptKey.Length == 0 && _selectedId.Length > 0)
            {
                var refused = _servers.FirstOrDefault(x => Key(x) == _selectedId);
                if (refused != null) { OpenPasswordPrompt(refused, admin: false, ip: false); JoinPassword.LastRefused = true; _show = true; }
            }
            UpdateCheck.Start();
            if (UpdateCheck.NewVersion.Length > 0 && !_updateToasted && Bridge.InMainMenu)
            {
                _updateToasted = true;
                Toast($"New version {UpdateCheck.NewVersion} of BAMP Server Browser is available (you have {BuildInfo.Short}).", "Download page", () => Application.OpenURL(UpdateCheck.RepoUrl));
            }
            if (Cfg.notifyFavorites && Cfg.favorites.Count > 0 && !_fetching && !_favPolling && Time.unscaledTime >= _nextFavPoll)
            {
                _nextFavPoll = Time.unscaledTime + 60f;
                _favPolling = true;
                MasterClient.FetchAsync().ContinueWith(t => _main.Enqueue(() =>
                {
                    _favPolling = false;
                    var (fl, _) = t.Result;
                    if (fl != null) CheckFavorites(fl);
                }));
            }
            _toasts.RemoveAll(t => Time.unscaledTime > t.Until);
            TickPing();
            bool hostingNow = Hosting;
            if (hostingNow && Time.unscaledTime >= _nextAdminTick) { _nextAdminTick = Time.unscaledTime + 1f; AdminChannel.TickHost(); }
            if (!hostingNow && _wasHostingAdmin) AdminChannel.ResetHost();
            _wasHostingAdmin = hostingNow;
            if (_showAdmin)
            {
                if (AdminChannel.LocalMode) { if (!hostingNow) { _showAdmin = false; AdminChannel.ResetClient(); } }
                else { bool c = false; try { c = MPClient.IsConnected; } catch { } if (!c || !AdminChannel.IsAdmin) _showAdmin = false; }
            }

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
            string world = "";
            try { world = MPSaveCoordinator.ActivePlaythroughId ?? ""; } catch { }
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
                worldId = world,
                rules = GameInfo.RulesSummary(),
                bsbVersion = BuildInfo.Short,
                playerNames = GameInfo.PlayerNames(),
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
                if (!LocalWorldStore.Running || LocalWorldStore.Port != Cfg.localStoragePort || LocalWorldStore.GroupKeyInUse != Cfg.GroupKey)
                {
                    LocalWorldStore.Start(Cfg.localStoragePort, Cfg.GroupKey);
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
                CheckFavorites(list);
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

            JoinPassword.Pending = _pwPromptKey == Key(s) ? (_joinPw ?? "").Trim() : "";
            if (s.hasPassword && string.IsNullOrEmpty(JoinPassword.Pending)) { OpenPasswordPrompt(s, admin: false, ip: forceIp); return; }
            JoinPassword.LastRefused = false;
            string err = Bridge.Join(addr, port);
            if (err != null) { _joinMsg = err; return; }
            AdminChannel.JoinedHostName = s.hostName ?? "";
            ModEntry.Info("join '" + s.name + "' via " + addr + (addr.StartsWith("steam:") ? "" : ":" + port));
            _joinMsg = "";
            _pwPromptKey = "";
            _joinPw = "";
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
            _radio = new GUIStyle(GUI.skin.toggle) { richText = true, fontSize = 13 };
            _radio.normal.textColor = _radio.onNormal.textColor = _radio.hover.textColor = _radio.onHover.textColor = Color.white;
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
            if (!_show && !ShowHostPanel && !_showAdmin && _toasts.Count == 0 && !_showSaveDlg) return;
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
            if (_showAdmin) _adminRect = Clamp(GUI.Window(0x42A3, _adminRect, DrawAdmin, AdminChannel.LocalMode ? "Players & tools (host)" : "Manage this game (admin)", _win), vw, vh);
            if (_showSaveDlg)
            {
                if (!_saveDlgPlaced) { _saveDlgPlaced = true; _saveDlgRect.x = (vw - _saveDlgRect.width) / 2; _saveDlgRect.y = (vh - _saveDlgRect.height) / 2; }
                _saveDlgRect = Clamp(GUI.Window(0x42A5, _saveDlgRect, DrawSaveDlg, "Where should your shared saves be stored?", _win), vw, vh);
                GUI.FocusWindow(0x42A5);
            }
            if (_toasts.Count > 0) DrawToasts(vw);

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

        // ---------- favoris / notifications / ping ----------
        private static string FavKey(ServerEntry s) => !string.IsNullOrEmpty(s.steamId) ? "steam:" + s.steamId : "ip:" + (s.ip ?? "") + ":" + s.port;
        private bool IsFav(ServerEntry s) => Cfg.favorites.Contains(FavKey(s));
        private void ToggleFav(ServerEntry s)
        {
            string k = FavKey(s);
            if (!Cfg.favorites.Remove(k)) Cfg.favorites.Add(k);
            BrowserConfig.Save();
        }

        private HashSet<string> _favOnline;
        private float _nextFavPoll = 20f;
        private bool _favPolling;

        /// <summary>Compare avec le dernier état connu et prévient quand une partie favorite passe en ligne.</summary>
        private void CheckFavorites(List<ServerEntry> list)
        {
            string own = OwnSteamId();
            var onlineNow = new HashSet<string>(list.Where(x => x.online && IsFav(x)).Select(FavKey));
            if (_favOnline != null && Cfg.notifyFavorites)
                foreach (var x in list.Where(x => x.online && IsFav(x) && !_favOnline.Contains(FavKey(x)) && (own.Length == 0 || x.steamId != own)))
                {
                    var target = x;
                    Toast($"Favorite game online: {target.name} ({target.hostName})", "Show", () => { _show = true; _selectedId = Key(target); Refresh(); });
                }
            _favOnline = onlineNow;
        }

        private class ToastMsg { public string Text, Action; public Action OnAction; public float Until; }
        private readonly List<ToastMsg> _toasts = new List<ToastMsg>();
        private void Toast(string text, string action = null, Action onAction = null)
        {
            _toasts.Add(new ToastMsg { Text = text, Action = action, OnAction = onAction, Until = Time.unscaledTime + 12f });
            while (_toasts.Count > 3) _toasts.RemoveAt(0);
        }

        private Rect ToastRect(int i, float vw) => new Rect(vw - 400, 20 + i * 84, 380, 76);

        private void DrawToasts(float vw)
        {
            for (int i = 0; i < _toasts.Count; i++)
            {
                var t = _toasts[i];
                int idx = i;
                GUI.Window(0x42B0 + i, ToastRect(i, vw), _ =>
                {
                    GUILayout.Label(Esc(t.Text), _small);
                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    if (t.Action != null && GUILayout.Button(t.Action, GUILayout.Width(150))) { try { t.OnAction?.Invoke(); } catch { } t.Until = 0; }
                    if (GUILayout.Button("Close", GUILayout.Width(70))) t.Until = 0;
                    GUILayout.EndHorizontal();
                }, "BAMP Server Browser", _win);
            }
        }

        // ping : clé de la partie -> (ms, moment de la mesure) ; ms : -1 pas de réponse, -2 en cours, -3 impossible
        private readonly Dictionary<string, (int ms, float at)> _ping = new Dictionary<string, (int, float)>();
        private UnityEngine.Ping _pingObj;
        private string _pingKey = "";
        private float _pingStarted;

        /// <summary>Mesure le ping de la partie sélectionnée (toutes les 10 s, une mesure à la fois).</summary>
        private void TickPing()
        {
            float now = Time.unscaledTime;
            if (_pingObj != null)
            {
                bool done = false; int ms = -1;
                try { done = _pingObj.isDone; if (done) ms = _pingObj.time; } catch { done = true; ms = -3; }
                if (!done && now - _pingStarted > 2f) { done = true; ms = -1; }
                if (!done) return;
                _ping[_pingKey] = (ms < 0 ? (ms == -3 ? -3 : -1) : ms, now);
                try { _pingObj.DestroyPing(); } catch { }
                _pingObj = null;
            }
            if (!_show) return;
            var cur = _servers.FirstOrDefault(x => Key(x) == _selectedId);
            if (cur == null || !cur.online || string.IsNullOrEmpty(cur.ip) || !System.Net.IPAddress.TryParse(cur.ip, out var addr) || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return;
            string k = Key(cur);
            if (_ping.TryGetValue(k, out var v) && now - v.at < 10f) return;
            try { _pingObj = new UnityEngine.Ping(cur.ip); _pingKey = k; _pingStarted = now; if (!_ping.ContainsKey(k)) _ping[k] = (-2, now); }
            catch { _ping[k] = (-3, now); _pingObj = null; }
        }

        private string PingText(ServerEntry s)
        {
            if (string.IsNullOrEmpty(s.ip)) return "Ping: not measured (Steam connection)";
            if (!_ping.TryGetValue(Key(s), out var v) || v.ms == -2) return "Ping: measuring...";
            if (v.ms == -1) return "Ping: no answer (the host's router blocks ping)";
            if (v.ms == -3) return "Ping: not available";
            string col = v.ms < 60 ? "#4cd964" : v.ms < 150 ? "#ffd24a" : "#ff7a70";
            return $"Ping: <color={col}>{v.ms} ms</color>";
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
            if (Cfg.listHidePassword) rows = rows.Where(s => !s.hasPassword);
            if (Cfg.listFavoritesOnly) rows = rows.Where(s => IsFav(s));
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
                    o = rows.OrderBy(s => s.online ? 0 : 1).ThenBy(s => IsFav(s) ? 0 : 1);
                    o = desc ? o.ThenByDescending(s => s.players) : o.ThenBy(s => s.players);
                    o = o.ThenBy(s => s.offlineFor);
                    break;
            }
            return o.ThenBy(s => n(s.name));
        }

        private void DrawList(int id)
        {
            if (UpdateCheck.NewVersion.Length > 0)
            {
                GUILayout.BeginHorizontal(_box);
                GUILayout.Label($"<color=#ffd24a>New version {UpdateCheck.NewVersion} available</color> (you have {BuildInfo.Short})." + (UpdateCheck.Notes.Length > 0 ? " " + Esc(UpdateCheck.Notes) : ""), _small);
                if (GUILayout.Button("Download page", GUILayout.Width(130))) Application.OpenURL(UpdateCheck.RepoUrl);
                GUILayout.EndHorizontal();
            }
            if ((AdminChannel.KickedNotice ?? "").Length > 0)
            {
                GUILayout.BeginHorizontal(_box);
                GUILayout.Label(AdminChannel.KickedNotice, _bad);
                if (GUILayout.Button("OK", GUILayout.Width(50))) AdminChannel.KickedNotice = "";
                GUILayout.EndHorizontal();
            }
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

            // ---- ligne 3 : favoris / mot de passe
            GUILayout.BeginHorizontal();
            bool fo = GUILayout.Toggle(Cfg.listFavoritesOnly, " Favorites only");
            if (fo != Cfg.listFavoritesOnly) { Cfg.listFavoritesOnly = fo; BrowserConfig.Save(); }
            GUILayout.Space(14);
            bool hp = GUILayout.Toggle(Cfg.listHidePassword, " Hide games with a password");
            if (hp != Cfg.listHidePassword) { Cfg.listHidePassword = hp; BrowserConfig.Save(); }
            GUILayout.Space(14);
            bool nf = GUILayout.Toggle(Cfg.notifyFavorites, " Notify me when a favorite comes online");
            if (nf != Cfg.notifyFavorites) { Cfg.notifyFavorites = nf; BrowserConfig.Save(); }
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
                else line2 = $"<color=#9aa3ad>{Esc(s.hostName)}  ·  Offline, last seen {Ago(s.offlineFor)}{dayTxt}</color>"
                             + (AdminChannel.GetRelaunch(s.worldId) != null ? "  ·  <color=#8fc3ff>you can relaunch it (admin)</color>" : "");
                string name = s.online ? $"<b>{Esc(s.name)}</b>" : $"<b><color=#b8c0cc>{Esc(s.name)}</color></b>";
                string favTag = IsFav(s) ? "<color=#ffd24a>FAV</color> " : "";
                string label = $"{favTag}{lang}{name}{(s.hasPassword ? "  <color=#ffd24a>[password]</color>" : "")}{(mine ? "  <color=#9ab>(you)</color>" : "")}\n<size=11>{line2}</size>";
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
                GUILayout.BeginHorizontal();
                GUILayout.Label((cur.online ? "<color=#4cd964>Online</color>" : "<color=#9aa3ad>Offline</color>") + "  " + Esc(cur.name) +
                                (string.IsNullOrEmpty(cur.language) ? "" : "  ·  " + Esc(cur.language)) + (cur.day > 0 ? "  ·  Day " + cur.day : ""), _title);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(IsFav(cur) ? "Remove favorite" : "Add to favorites", GUILayout.Width(140))) ToggleFav(cur);
                GUILayout.EndHorizontal();
                if (!string.IsNullOrEmpty(cur.description)) GUILayout.Label(Esc(cur.description), _small);
                if (!string.IsNullOrEmpty(cur.rules)) GUILayout.Label("<b>Settings:</b> " + Esc(cur.rules), _small);
                if (cur.online)
                {
                    var names = cur.playerNames ?? new List<string>();
                    GUILayout.Label("<b>Players:</b> " + (names.Count > 0 ? Esc(string.Join(", ", names)) : cur.players + " connected") + "    " + PingText(cur), _small);
                    if (string.IsNullOrEmpty(cur.bsbVersion))
                        GUILayout.Label("<size=11>The host has an older version of BAMP Server Browser: shared saves and admin tools may not work.</size>", _bad);
                    else if (cur.bsbVersion != BuildInfo.Short)
                        GUILayout.Label($"<size=11>The host uses BAMP Server Browser v{Esc(cur.bsbVersion)}, you have v{BuildInfo.Short}. Use the same version to avoid problems.</size>", _bad);
                }
                if (!cur.online)
                {
                    string who = string.IsNullOrEmpty(cur.hostName) ? "the host" : Esc(cur.hostName);
                    var rl = AdminChannel.GetRelaunch(cur.worldId);
                    bool mineOff = own.Length > 0 && cur.steamId == own;
                    if (mineOff)
                    {
                        GUILayout.Label($"Your game is offline (last seen {Ago(cur.offlineFor)}). Host it again with the latest save of this world on your PC:", _small);
                        GUI.enabled = !string.IsNullOrEmpty(cur.worldId);
                        if (GUILayout.Button("Host this game again", GUILayout.Height(34))) HostAgain(cur);
                        GUI.enabled = true;
                        if (string.IsNullOrEmpty(cur.worldId))
                            GUILayout.Label("<size=10>This entry was listed before the update: host it once from Multiplayer > Host Saved Game.</size>", _small);
                    }
                    else if (rl == null)
                    {
                        GUILayout.Label($"This game is offline (last seen {Ago(cur.offlineFor)}). Only {who} or an admin of this game can start it again.", _small);
                        GUILayout.Label("<size=10>Admins: join this game once with the admin password while it is online, then you can relaunch it from here when it is offline.</size>", _small);
                    }
                    else if (_pwPromptKey == Key(cur) && _pwPromptRelaunch)
                    {
                        GUILayout.Label("Enter the admin password to relaunch this game on your PC. You become its host, with the same name and passwords:", _small);
                        GUILayout.BeginHorizontal();
                        GUILayout.Label("Admin password:", _small, GUILayout.Width(120));
                        _joinPw = Field(6, _joinPw, 64, mask: true);
                        GUILayout.EndHorizontal();
                        GUILayout.BeginHorizontal();
                        GUI.enabled = (_joinPw ?? "").Trim().Length > 0;
                        if (GUILayout.Button("Relaunch the game", GUILayout.Height(34))) Relaunch(cur, rl);
                        GUI.enabled = true;
                        if (GUILayout.Button("Cancel", GUILayout.Height(34), GUILayout.Width(110))) { _pwPromptKey = ""; _joinPw = ""; _focus = 0; _joinMsg = ""; }
                        GUILayout.EndHorizontal();
                    }
                    else
                    {
                        GUILayout.Label($"This game is offline (last seen {Ago(cur.offlineFor)}). As an admin, you can start it again on your PC with your copy of the world.", _small);
                        if (GUILayout.Button("Relaunch as admin", GUILayout.Height(34))) { OpenPasswordPrompt(cur, admin: true, ip: false); _pwPromptRelaunch = true; }
                    }
                }
                else
                {
                    bool canSteam = cur.steamId.Length > 0 && cur.steamId != own;
                    bool canIp = cur.ip.Length > 0 && cur.port > 0;
                    if (cur.modVersion != _bampVersion)
                        GUILayout.Label($"Different mod version (host v{Esc(cur.modVersion)}, you v{_bampVersion}): the connection may be refused.", _bad);
                    if (_pwPromptKey == Key(cur))
                    {
                        // ---- mot de passe demandé après le clic sur Join ----
                        GUILayout.Label(_pwPromptAdmin
                            ? "Admin password (gives the right to change the game settings and delete shared saves):"
                            : "This game needs a password. Enter the player password, or the admin password to get admin rights:", _small);
                        GUILayout.BeginHorizontal();
                        GUILayout.Label("Password:", _small, GUILayout.Width(95));
                        _joinPw = Field(6, _joinPw, 64, mask: true);
                        GUILayout.EndHorizontal();
                        if (JoinPassword.LastRefused) GUILayout.Label("Wrong password: the host refused the connection.", _bad);
                        GUILayout.BeginHorizontal();
                        GUI.enabled = (canSteam || canIp) && (_joinPw ?? "").Trim().Length > 0;
                        if (GUILayout.Button("Join", GUILayout.Height(34))) Join(cur, _pwPromptIp);
                        GUI.enabled = true;
                        if (GUILayout.Button("Cancel", GUILayout.Height(34), GUILayout.Width(110))) { _pwPromptKey = ""; _joinPw = ""; _focus = 0; JoinPassword.LastRefused = false; }
                        GUILayout.EndHorizontal();
                    }
                    else
                    {
                        GUILayout.BeginHorizontal();
                        GUI.enabled = canSteam || canIp;
                        if (GUILayout.Button(cur.hasPassword ? "Join (password)" : "Join", GUILayout.Height(34))) Join(cur, false);
                        GUI.enabled = canIp && canSteam;
                        if (GUILayout.Button(canIp ? $"Join by IP ({cur.ip}:{cur.port})" : "IP not available", GUILayout.Height(34))) Join(cur, true);
                        GUI.enabled = canSteam || canIp;
                        if (!cur.hasPassword && GUILayout.Button("Join as admin", GUILayout.Height(34), GUILayout.Width(120))) OpenPasswordPrompt(cur, admin: true, ip: false);
                        GUI.enabled = true;
                        GUILayout.EndHorizontal();
                    }
                }
            }
            if (_joinMsg.Length > 0) GUILayout.Label(_joinMsg, _bad);
            GUILayout.EndVertical();

            bool connected = false; try { connected = MPClient.IsConnected; } catch { }
            if (connected && AdminChannel.IsAdmin)
            {
                GUILayout.BeginHorizontal(_box);
                GUILayout.Label("<color=#4cd964>You are admin of the game you are in.</color>", _small);
                if (GUILayout.Button("Manage this game", GUILayout.Width(170))) OpenAdmin();
                GUILayout.EndHorizontal();
            }
            else if (connected && AdminChannel.AdminRefused)
                GUILayout.Label("Wrong admin password: you joined as a normal player.", _bad);

            GUILayout.Label($"<size=10>{Cfg.toggleKey}: show/hide · Double-click to join · Green dot = online</size>", _small);
            GUI.DragWindow(new Rect(0, 0, 10000, 28));
        }

        private static readonly MethodInfo LoadPickedSessionM = typeof(MPCanvasUI).GetMethod("LoadPickedSession", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        /// <summary>Un admin relance sur son PC une partie hors ligne, avec sa copie du monde et les réglages de l'hôte.</summary>
        private void Relaunch(ServerEntry s, AdminChannel.RelaunchInfo rl)
        {
            string pw = (_joinPw ?? "").Trim();
            if (Hosting) { _joinMsg = "You are already hosting a game."; return; }
            try { if (MPClient.IsConnected || MPClient.IsConnecting) { _joinMsg = "Leave the game you are in first."; return; } } catch { }
            if (!Bridge.InMainMenu) { _joinMsg = "Go back to the main menu first."; return; }
            if (!AdminChannel.CheckRelaunchPassword(rl, pw)) { _joinMsg = "Wrong admin password."; return; }

            var pick = FindLocalWorld(rl.worldId);
            if (pick == null)
            {
                _joinMsg = "This world is not on your PC yet. It arrives with the shared saves after you have played it (go back to the main menu once after playing so it can sync).";
                return;
            }
            if (MPCanvasUI.Instance == null || LoadPickedSessionM == null) { _joinMsg = "Multiplayer mod not found or incompatible version."; return; }

            // réglages de la partie d'origine (mêmes nom et mots de passe)
            if (!string.IsNullOrWhiteSpace(rl.name)) Cfg.serverName = rl.name;
            Cfg.description = rl.desc ?? "";
            if (Array.IndexOf(BrowserConfig.Languages, rl.lang) >= 0) Cfg.language = rl.lang;
            if (rl.max >= 2 && rl.max <= 32) Cfg.maxPlayers = rl.max;
            Cfg.joinPassword = rl.playerPassword ?? "";
            Cfg.adminPassword = pw;
            Cfg.publish = true;
            BrowserConfig.Save();
            _editName = Cfg.serverName; _editDesc = Cfg.description; _editPw = Cfg.joinPassword; _editApw = Cfg.adminPassword;
            if (StartHosting(pick)) ModEntry.Info($"[Admin] relaunching '{s.name}' (world {rl.worldId}, local copy day {pick.Day}).");
        }

        private static MPSaveManager.MpVariant FindLocalWorld(string worldId)
        {
            if (string.IsNullOrEmpty(worldId)) return null;
            try
            {
                return (MPSaveManager.ListPlaythroughs() ?? new List<MPSaveManager.MpPlaythrough>())
                    .SelectMany(p => p.Variants ?? new List<MPSaveManager.MpVariant>())
                    .Where(v => string.Equals(v.PlaythroughId, worldId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(v.SessionName))
                    .OrderByDescending(v => v.SavedAtUnix).FirstOrDefault();
            }
            catch (Exception ex) { ModEntry.Warn("local world: " + ex.Message); return null; }
        }

        /// <summary>Ouvre le salon du mod multijoueur avec cette sauvegarde (comme "Host Saved Game").</summary>
        private bool StartHosting(MPSaveManager.MpVariant pick)
        {
            try
            {
                LoadPickedSessionM.Invoke(MPCanvasUI.Instance, new object[] { pick.SessionName, pick.PlaythroughId ?? "" });
                _joinMsg = "";
                _pwPromptKey = ""; _joinPw = ""; _focus = 0;
                _show = false; _autoShown = false;
                return true;
            }
            catch (Exception ex) { _joinMsg = "Could not start the game: " + (ex.InnerException ?? ex).Message; return false; }
        }

        /// <summary>L'hôte relance sa propre partie hors ligne (sa dernière sauvegarde de ce monde).</summary>
        private void HostAgain(ServerEntry s)
        {
            if (Hosting) { _joinMsg = "You are already hosting a game."; return; }
            try { if (MPClient.IsConnected || MPClient.IsConnecting) { _joinMsg = "Leave the game you are in first."; return; } } catch { }
            if (!Bridge.InMainMenu) { _joinMsg = "Go back to the main menu first."; return; }
            var pick = FindLocalWorld(s.worldId);
            if (pick == null) { _joinMsg = "The save of this world was not found on this PC."; return; }
            if (MPCanvasUI.Instance == null || LoadPickedSessionM == null) { _joinMsg = "Multiplayer mod not found or incompatible version."; return; }
            if (!Cfg.publish) { Cfg.publish = true; BrowserConfig.Save(); }
            if (StartHosting(pick)) ModEntry.Info($"hosting again '{s.name}' (world {s.worldId}, day {pick.Day}).");
        }

        private void OpenPasswordPrompt(ServerEntry s, bool admin, bool ip)
        {
            _pwPromptKey = Key(s);
            _pwPromptAdmin = admin;
            _pwPromptRelaunch = false;
            _pwPromptIp = ip;
            _joinPw = "";
            _joinMsg = "";
            JoinPassword.LastRefused = false;
            _focus = 6; _focusBuf = ""; _focusMax = 64;
        }

        // =====================================================================
        //  Fenêtre admin (joueur connecté avec le mot de passe admin)
        // =====================================================================
        private bool _showAdmin;
        private Rect _adminRect = new Rect(680, 90, 500, 780);
        private string _aName = "", _aDesc = "", _aPw = "", _aApw = "", _aLang = "";
        private int _aMax = 8;
        private bool _aPub = true, _aSynced;
        private string _aConfirmDel = "";

        private void OpenAdmin()
        {
            _showAdmin = true;
            _aSynced = false;
            AdminChannel.Settings.loaded = false;
            AdminChannel.RequestAll();
        }

        private string _aMsg = "";
        private string _aConfirmBan = "";
        private Vector2 _aOuter;

        private void DrawAdmin(int id)
        {
            var st = AdminChannel.Settings;
            GUILayout.BeginHorizontal();
            GUILayout.Label(AdminChannel.LocalMode ? "Your game: changes apply right away." : "Changes are applied by the host right away.", _small);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Reload", GUILayout.Width(80))) OpenAdmin();
            if (GUILayout.Button("X", GUILayout.Width(28))) { _showAdmin = false; if (AdminChannel.LocalMode) AdminChannel.ResetClient(); }
            GUILayout.EndHorizontal();
            string res = AdminChannel.LastResult ?? "";
            if (res.Length > 0) GUILayout.Label(res.StartsWith("! ") ? res.Substring(2) : res, res.StartsWith("! ") ? _bad : _good);

            _aOuter = GUILayout.BeginScrollView(_aOuter, GUILayout.ExpandHeight(true));

            // ---------------- joueurs
            GUILayout.Label("Players", _title);
            if ((AdminChannel.PlayersStatus ?? "").Length > 0) GUILayout.Label(AdminChannel.PlayersStatus, _small);
            foreach (var p in AdminChannel.Players)
            {
                GUILayout.BeginHorizontal(_box);
                GUILayout.Label(Esc(p.name) + (p.host ? "  <color=#8fc3ff>[host]</color>" : p.admin ? "  <color=#4cd964>[admin]</color>" : ""), _small);
                GUILayout.FlexibleSpace();
                bool canAct = !p.host && (AdminChannel.LocalMode || !p.admin);
                GUI.enabled = canAct;
                if (GUILayout.Button("Kick", GUILayout.Width(70))) AdminChannel.Kick(p.id);
                bool confirm = _aConfirmBan == p.id;
                if (GUILayout.Button(confirm ? "Really ban?" : "Ban", GUILayout.Width(100)))
                {
                    if (confirm) { AdminChannel.Ban(p.id); _aConfirmBan = ""; } else _aConfirmBan = p.id;
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            if (AdminChannel.Bans.Count > 0)
            {
                GUILayout.Label("<size=11>Banned players</size>", _small);
                foreach (var b in AdminChannel.Bans)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("  " + Esc(b.name), _small);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Unban", GUILayout.Width(80))) AdminChannel.Unban(b.key);
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Message:", _small, GUILayout.Width(120));
            _aMsg = Field(12, _aMsg, 150);
            GUI.enabled = (_aMsg ?? "").Trim().Length > 0;
            if (GUILayout.Button("Send to all", GUILayout.Width(100))) { AdminChannel.Message(_aMsg.Trim()); _aMsg = ""; _focus = 0; }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Save the game now", GUILayout.Height(28))) AdminChannel.SaveNow();

            // ---------------- réglages
            GUILayout.Space(8);
            if (!st.loaded && !_aSynced) { GUILayout.Label("Loading the game settings...", _small); }
            else
            {
                if (st.loaded && !_aSynced)
                {
                    _aSynced = true;
                    _aName = st.name; _aDesc = st.desc; _aMax = st.max; _aPub = st.publish; _aLang = st.lang;
                    _aPw = ""; _aApw = "";
                }
                GUILayout.Label("Game settings", _title);
                GUILayout.BeginHorizontal(); GUILayout.Label("Name:", _small, GUILayout.Width(120)); _aName = Field(8, _aName, 48); GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal(); GUILayout.Label("Description:", _small, GUILayout.Width(120)); _aDesc = Field(9, _aDesc, 120); GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Language:", _small, GUILayout.Width(120));
                {
                    var langs = BrowserConfig.Languages;
                    int li = Mathf.Max(0, Array.IndexOf(langs, _aLang));
                    if (GUILayout.Button("<", GUILayout.Width(26))) _aLang = langs[(li + langs.Length - 1) % langs.Length];
                    GUILayout.Label(langs[Mathf.Max(0, Array.IndexOf(langs, _aLang))], _title, GUILayout.Width(36));
                    if (GUILayout.Button(">", GUILayout.Width(26))) _aLang = langs[(li + 1) % langs.Length];
                }
                GUILayout.Space(20);
                GUILayout.Label("Max players:", _small, GUILayout.Width(90));
                if (GUILayout.Button("-", GUILayout.Width(26)) && _aMax > 2) _aMax--;
                GUILayout.Label(_aMax.ToString(), _title, GUILayout.Width(30));
                if (GUILayout.Button("+", GUILayout.Width(26)) && _aMax < 32) _aMax++;
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                _aPub = GUILayout.Toggle(_aPub, " Show the game in the public list");

                GUILayout.BeginHorizontal();
                GUILayout.Label("Player password:", _small, GUILayout.Width(120));
                _aPw = Field(10, _aPw, 64, mask: _focus != 10);
                if (st.hasPlayerPw && GUILayout.Button("Remove", GUILayout.Width(80))) { AdminChannel.Set("pp", ""); _aSynced = false; st.loaded = false; }
                GUILayout.EndHorizontal();
                GUILayout.Label("<size=10>" + (st.hasPlayerPw ? "A player password is set. Type a new one to change it, or Remove it to open the game to everyone." : "No player password: anyone can join. Type one to protect the game.") + "</size>", _small);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Admin password:", _small, GUILayout.Width(120));
                _aApw = Field(11, _aApw, 64, mask: _focus != 11);
                GUILayout.EndHorizontal();
                GUILayout.Label("<size=10>Leave empty to keep the current admin password.</size>", _small);

                bool dirty = _aName != st.name || _aDesc != st.desc || _aMax != st.max || _aPub != st.publish || _aLang != st.lang
                             || (_aPw ?? "").Trim().Length > 0 || (_aApw ?? "").Trim().Length > 0;
                GUI.enabled = dirty;
                if (GUILayout.Button("Apply changes", GUILayout.Height(30)))
                {
                    if (_aName != st.name) AdminChannel.Set("n", _aName);
                    if (_aDesc != st.desc) AdminChannel.Set("d", _aDesc);
                    if (_aMax != st.max) AdminChannel.Set("m", _aMax.ToString());
                    if (_aPub != st.publish) AdminChannel.Set("pub", _aPub ? "1" : "0");
                    if (_aLang != st.lang) AdminChannel.Set("l", _aLang);
                    if ((_aPw ?? "").Trim().Length > 0) AdminChannel.Set("pp", _aPw.Trim());
                    if ((_aApw ?? "").Trim().Length > 0) AdminChannel.Set("ap", _aApw.Trim());
                    _aSynced = false; st.loaded = false;
                }
                GUI.enabled = true;
            }

            // ---------------- sauvegardes partagées + corbeille
            GUILayout.Space(8);
            GUILayout.Label("Shared saves of this game", _title);
            if ((AdminChannel.WorldsStatus ?? "").Length > 0) GUILayout.Label(AdminChannel.WorldsStatus, _small);
            foreach (var w in AdminChannel.Worlds.Where(x => !x.deleted))
            {
                GUILayout.BeginHorizontal(_box);
                GUILayout.Label(Esc(w.name) + (w.day > 0 ? "  ·  Day " + w.day : ""), _small);
                GUILayout.FlexibleSpace();
                bool confirm = _aConfirmDel == w.id;
                if (GUILayout.Button(confirm ? "Really delete?" : "Delete", GUILayout.Width(120)))
                {
                    if (confirm) { AdminChannel.DeleteWorld(w.id); _aConfirmDel = ""; }
                    else _aConfirmDel = w.id;
                }
                GUILayout.EndHorizontal();
            }
            var trash = AdminChannel.Worlds.Where(x => x.deleted).ToList();
            if (trash.Count > 0)
            {
                GUILayout.Label("Trash", _title);
                foreach (var w in trash)
                {
                    GUILayout.BeginHorizontal(_box);
                    GUILayout.Label("<color=#9aa3ad>" + Esc(w.name) + (w.day > 0 ? "  ·  Day " + w.day : "") + "  ·  " + w.daysLeft + " day(s) left</color>", _small);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Restore", GUILayout.Width(120))) AdminChannel.RestoreWorld(w.id);
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.Label("<size=10>Deleted saves stay in the trash for 30 days, then they are erased for good.</size>", _small);
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, 10000, 28));
        }

        // =====================================================================
        //  Question "où ranger les sauvegardes partagées" (Host New Game, ou bouton Change)
        // =====================================================================
        private bool _showSaveDlg, _saveDlgPlaced;
        private MPCanvasUI _saveDlgUi;            // non nul : lancer "Host New Game" après le choix
        private Rect _saveDlgRect = new Rect(0, 0, 680, 640);
        private int _ssMode;                      // 0 = ce PC, 1 = mon serveur, 2 = garder le groupe d'un ami
        private string _ssUrl = "", _ssPort = "", _ssTest = "";
        private bool _ssTestOk, _ssTesting, _ssDontAsk;
        private string _ssUser = "";
        private float _ssCopiedAt = -100f;
        private const string InstallScriptUrl = "https://github.com/ecorepert/BAMP-Server-Browser/raw/main/master/install.sh";

        /// <summary>
        /// Commande à coller dans un terminal du PC (PowerShell) : se connecte au VPS en SSH, télécharge
        /// install.sh depuis GitHub (qui récupère lui-même le dernier master.py) et installe le serveur
        /// de sauvegardes sur le port de l'adresse.
        /// </summary>
        private string VpsInstallCommand(out bool hasHost, out bool portGiven, out int port)
        {
            string u = BrowserConfig.NormalizeUrl(_ssUrl);
            string host = "YOUR_VPS_IP";
            port = 8787; hasHost = false; portGiven = false;
            if (u.Length > 0 && Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
            {
                host = uri.Host; hasHost = true;
                if (!uri.IsDefaultPort) { port = uri.Port; portGiven = true; }
            }
            string user = new string((_ssUser ?? "").Trim().Where(c => char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-').ToArray());
            if (user.Length == 0) user = "root";
            return $"ssh -t {user}@{host} \"curl -fsSL {InstallScriptUrl} -o /tmp/bamp-install.sh && sudo sh /tmp/bamp-install.sh {port} latest\"";
        }

        private void OpenSaveServerDialog(MPCanvasUI hostNewUi)
        {
            _saveDlgUi = hostNewUi;
            string cur = (Cfg.worldsUrl ?? "").Trim();
            _ssMode = cur.Length > 0 ? 1 : Cfg.CodeHost.Length > 0 ? 2 : 0;
            // pré-remplissage : serveur actuel, sinon le dernier serveur utilisé
            _ssUrl = cur.Length > 0 ? cur : (Cfg.saveServerHistory.FirstOrDefault() ?? "");
            _ssPort = Cfg.localStoragePort.ToString();
            _ssTest = ""; _ssTestOk = false; _ssTesting = false;
            _ssDontAsk = !Cfg.askSaveServerOnNewGame;
            _ssUser = string.IsNullOrWhiteSpace(Cfg.sshUser) ? "root" : Cfg.sshUser;
            _saveDlgPlaced = false;
            _showSaveDlg = true;
        }

        private string SaveServerSummary()
        {
            if ((Cfg.worldsUrl ?? "").Trim().Length > 0) return "your server " + Cfg.worldsUrl.Trim();
            if (Cfg.CodeHost.Length > 0) return "the group of " + Cfg.CodeHost + " (code from a friend)";
            return "this PC (port " + Cfg.localStoragePort + ")";
        }

        private void TestSaveServer()
        {
            _ssTesting = true; _ssTest = "Testing..."; _ssTestOk = false;
            string u = _ssUrl;
            SaveServerPrompt.TestAsync(u).ContinueWith(t => _main.Enqueue(() =>
            {
                _ssTesting = false;
                if (u != _ssUrl) return; // adresse modifiée entre-temps
                var (ok, msg) = t.IsFaulted ? (false, "Test failed.") : t.Result;
                _ssTestOk = ok; _ssTest = msg;
            }));
        }

        private void ApplySaveServer()
        {
            if (_ssMode == 0)
            {
                if (!int.TryParse((_ssPort ?? "").Trim(), out int port) || port < 1024 || port > 65535) { _ssTest = "The port must be a number between 1024 and 65535."; _ssTestOk = false; return; }
                if (Cfg.CodeHost.Length > 0) Cfg.shareKey = BrowserConfig.NewGroupKey(); // notre propre groupe, rangé sur ce PC
                Cfg.worldsUrl = "";
                Cfg.localStoragePort = port;
            }
            else if (_ssMode == 1)
            {
                string raw = (_ssUrl ?? "").Trim().TrimEnd('/');
                if (BrowserConfig.NormalizeUrl(raw).Length == 0) { _ssTest = "Invalid address. Example: 203.0.113.5:8787"; _ssTestOk = false; return; }
                if (Cfg.CodeHost.Length > 0) Cfg.shareKey = BrowserConfig.NewGroupKey();
                Cfg.worldsUrl = raw;
                if (!string.IsNullOrWhiteSpace(_ssUser)) Cfg.sshUser = _ssUser.Trim();
                Cfg.saveServerHistory.RemoveAll(x => string.Equals(x, raw, StringComparison.OrdinalIgnoreCase));
                Cfg.saveServerHistory.Insert(0, raw);
                while (Cfg.saveServerHistory.Count > 5) Cfg.saveServerHistory.RemoveAt(Cfg.saveServerHistory.Count - 1);
            }
            Cfg.askSaveServerOnNewGame = !_ssDontAsk;
            BrowserConfig.Save();
            _sync.SyncNow();
            ModEntry.Info("[SaveServer] shared saves stored on " + SaveServerSummary() + ".");
            _showSaveDlg = false;
            _focus = 0;
            var ui = _saveDlgUi;
            _saveDlgUi = null;
            if (ui != null) SaveServerPrompt.ContinueHostNew(ui);
        }

        private void DrawSaveDlg(int id)
        {
            GUILayout.Label("Your friends who join with your player password receive the shared save of your world, and an admin can relaunch it when you are offline. This choice applies to all the games you host.", _small);
            GUILayout.Space(6);

            // ---- ce PC
            if (GUILayout.Toggle(_ssMode == 0, " <b>This PC</b> (default)", _radio) && _ssMode != 0) { _ssMode = 0; _ssTest = ""; }
            if (_ssMode == 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(24);
                GUILayout.Label("Port:", _small, GUILayout.Width(50));
                _ssPort = Field(13, _ssPort, 5);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Label("<size=11>      Saves stay on this PC. Friends need this TCP port open on your router to get them. When your PC is off, they keep their last copy.</size>", _small);
            }

            // ---- mon serveur
            if (GUILayout.Toggle(_ssMode == 1, " <b>My own server</b> (always on, e.g. a VPS)", _radio) && _ssMode != 1) { _ssMode = 1; _ssTest = ""; }
            if (_ssMode == 1)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(24);
                GUILayout.Label("Address:", _small, GUILayout.Width(70));
                string before = _ssUrl;
                _ssUrl = Field(14, _ssUrl, 120);
                if (_ssUrl != before) { _ssTest = ""; _ssTestOk = false; }
                GUI.enabled = !_ssTesting && (_ssUrl ?? "").Trim().Length > 0;
                if (GUILayout.Button(_ssTesting ? "..." : "Test", GUILayout.Width(70))) TestSaveServer();
                GUI.enabled = true;
                GUILayout.EndHorizontal();
                var others = Cfg.saveServerHistory.Where(x => !string.Equals(x, (_ssUrl ?? "").Trim(), StringComparison.OrdinalIgnoreCase)).Take(4).ToList();
                if (others.Count > 0)
                {
                    GUILayout.Label("<size=11>      Servers you used before:</size>", _small);
                    foreach (var h in others)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Space(24);
                        if (GUILayout.Button(Esc(h), GUILayout.Width(360))) { _ssUrl = h; _ssTest = ""; _ssTestOk = false; }
                        GUILayout.EndHorizontal();
                    }
                }
                // ---- installation sur le VPS
                GUILayout.Space(4);
                GUILayout.BeginVertical(_box);
                GUILayout.Label("<b>Not installed yet?</b> Install the save server on your VPS (Linux with python3 and curl):", _small);
                GUILayout.BeginHorizontal();
                GUILayout.Label("SSH user:", _small, GUILayout.Width(80));
                _ssUser = Field(15, _ssUser, 32);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                string cmd = VpsInstallCommand(out bool hasHost, out bool portGiven, out int port);
                GUILayout.Label("<size=11>1. Paste this command in a terminal on your PC (PowerShell), then type your VPS password when asked:</size>", _small);
                GUILayout.BeginHorizontal();
                GUILayout.Label("<size=10><color=#cfe3ff>" + Esc(cmd) + "</color></size>", _small);
                GUI.enabled = hasHost;
                if (GUILayout.Button(Time.unscaledTime - _ssCopiedAt < 2.5f ? "Copied!" : "Copy", GUILayout.Width(80), GUILayout.Height(30)))
                {
                    try { GUIUtility.systemCopyBuffer = cmd; _ssCopiedAt = Time.unscaledTime; } catch { }
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
                if (!hasHost) GUILayout.Label("<size=11>Type the address of your VPS above first (for example 203.0.113.5:8787).</size>", _bad);
                else if (!portGiven) GUILayout.Label($"<size=11>No port in the address: the server is installed on port {port}. Use the address <b>{Esc(new Uri(BrowserConfig.NormalizeUrl(_ssUrl)).Host)}:{port}</b>.</size>", _small);
                GUILayout.Label("<size=11>2. When it says the server is running, click <b>Test</b>.</size>", _small);
                GUILayout.Label("<size=10>With an SSH key, add  -i C:\\path\\to\\key  after ssh. If this VPS already runs the game list on this port, the same service will also store the saves.</size>", _small);
                GUILayout.EndVertical();
            }

            // ---- groupe d'un ami (ancienne configuration)
            if (Cfg.CodeHost.Length > 0)
            {
                if (GUILayout.Toggle(_ssMode == 2, " Keep using the group of " + Esc(Cfg.CodeHost) + " (code from a friend)", _radio) && _ssMode != 2) { _ssMode = 2; _ssTest = ""; }
            }

            if (_ssTest.Length > 0) GUILayout.Label(_ssTest, _ssTestOk ? _good : _bad);
            GUILayout.FlexibleSpace();
            _ssDontAsk = GUILayout.Toggle(_ssDontAsk, " Don't ask again when I host a new game (change it in \"Publish my game\")");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_saveDlgUi != null ? "Continue and host" : "Save", GUILayout.Height(34))) ApplySaveServer();
            if (GUILayout.Button("Cancel", GUILayout.Height(34), GUILayout.Width(120))) { _showSaveDlg = false; _saveDlgUi = null; _focus = 0; }
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0, 0, 10000, 28));
        }

        private void DrawHost(int id)
        {
            bool pub = GUILayout.Toggle(Cfg.publish, " Show my game in the public list");
            if (pub != Cfg.publish) { Cfg.publish = pub; BrowserConfig.Save(); if (pub) _nextRegTry = 0; }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Name:", _small, GUILayout.Width(120));
            _editName = Field(2, _editName, 48);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Description:", _small, GUILayout.Width(120));
            _editDesc = Field(3, _editDesc, 120);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Language:", _small, GUILayout.Width(120));
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
            GUILayout.Label("Player password:", _small, GUILayout.Width(120));
            _editPw = Field(5, _editPw, 64, mask: _focus != 5);
            GUILayout.EndHorizontal();
            GUILayout.Label("<size=10>" + (string.IsNullOrEmpty(Cfg.joinPassword) ? "Empty: anyone can join." : "Only players who know it can join. They also get your shared saves.") + "</size>", _small);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Admin password:", _small, GUILayout.Width(120));
            _editApw = Field(7, _editApw, 64, mask: _focus != 7);
            GUILayout.EndHorizontal();
            GUILayout.Label("<size=10>" + (string.IsNullOrEmpty(Cfg.adminPassword) ? "Empty: no admin. Set one to let a friend manage this game." : "Whoever joins with it can change these settings and delete shared saves.") + "</size>", _small);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Max players:", _small, GUILayout.Width(120));
            if (GUILayout.Button("-", GUILayout.Width(26)) && Cfg.maxPlayers > 2) { Cfg.maxPlayers--; BrowserConfig.Save(); PushUpdateNow(); }
            GUILayout.Label(Cfg.maxPlayers.ToString(), _title, GUILayout.Width(30));
            if (GUILayout.Button("+", GUILayout.Width(26)) && Cfg.maxPlayers < 32) { Cfg.maxPlayers++; BrowserConfig.Save(); PushUpdateNow(); }
            GUILayout.FlexibleSpace();
            bool dirty = _editName != Cfg.serverName || _editDesc != Cfg.description || (_editPw ?? "") != (Cfg.joinPassword ?? "") || (_editApw ?? "") != (Cfg.adminPassword ?? "");
            GUI.enabled = dirty;
            if (GUILayout.Button("Apply", GUILayout.Width(90)))
            {
                Cfg.serverName = (_editName ?? "").Trim();
                Cfg.description = (_editDesc ?? "").Trim();
                Cfg.joinPassword = (_editPw ?? "").Trim();
                Cfg.adminPassword = (_editApw ?? "").Trim();
                _editPw = Cfg.joinPassword;
                _editApw = Cfg.adminPassword;
                BrowserConfig.Save();
                PushUpdateNow();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("<size=11>Shared saves stored on " + Esc(SaveServerSummary()) + "</size>", _small);
            if (GUILayout.Button("Change", GUILayout.Width(80))) OpenSaveServerDialog(null);
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Players & tools (kick, ban, message, save, trash)", GUILayout.Height(28)))
            {
                AdminChannel.EnterLocalMode();
                OpenAdmin();
            }
            bool ok = _regId != null;
            GUILayout.Label(_hostStatus, ok ? _good : _small);
            if (WorldSync.Enabled)
            {
                GUILayout.Label("<size=11>Shared saves: " + Esc(_sync.Status) + "</size>", _small);
                if (Cfg.IsLocalStorageOwner && !LocalWorldStore.Running && LocalWorldStore.LastError.Length > 0)
                    GUILayout.Label("<size=10>Save server unavailable on port " + Cfg.localStoragePort + ": " + Esc(LocalWorldStore.LastError) + "</size>", _bad);
                else if (Cfg.IsLocalStorageOwner)
                    GUILayout.Label("<size=10>Saves are stored on this PC: friends need TCP port " + Cfg.localStoragePort + " open on your router to get them.</size>", _small);
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
            bool visible = ((_focus == 1 || _focus == 6) && _show) || ((_focus == 2 || _focus == 3 || _focus == 5 || _focus == 7) && ShowHostPanel)
                           || (_focus >= 8 && _focus <= 12 && _showAdmin) || ((_focus == 13 || _focus == 14 || _focus == 15) && _showSaveDlg);
            if (!visible) { _focus = 0; return; }
            if (Input.GetMouseButtonDown(0))
            {
                // un clic hors du champ retire le focus (le clic sur un autre champ le redonne dans OnGUI)
                var m = Input.mousePosition;
                var p = new Vector2(m.x / _scale, (Screen.height - m.y) / _scale);
                if (!((_show && _listRect.Contains(p)) || (ShowHostPanel && _hostRect.Contains(p)) || (_showAdmin && _adminRect.Contains(p)) || (_showSaveDlg && _saveDlgRect.Contains(p)))) { _focus = 0; return; }
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
            if (_show || ShowHostPanel || _showAdmin || _toasts.Count > 0 || _showSaveDlg)
            {
                var m = Input.mousePosition;
                var p = new Vector2(m.x / _scale, (Screen.height - m.y) / _scale);
                over = (_show && _listRect.Contains(p)) || (ShowHostPanel && _hostRect.Contains(p)) || (_showAdmin && _adminRect.Contains(p)) || (_showSaveDlg && _saveDlgRect.Contains(p));
                float vwc = Screen.width / _scale;
                for (int i = 0; i < _toasts.Count && !over; i++) over = ToastRect(i, vwc).Contains(p);
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
