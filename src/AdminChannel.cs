using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BigAmbitionsMP;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BAMPServerBrowser
{
    /// <summary>
    /// Petits messages discrets entre l'hôte et les joueurs qui ont ce mod, transportés par
    /// les messages de chat privés du mod multijoueur (marqueur <see cref="Tag"/>, jamais affichés).
    ///  - l'hôte envoie à chaque joueur son rôle (joueur / admin) et, s'il a donné le bon mot de passe,
    ///    le code des sauvegardes partagées (les sauvegardes se synchronisent alors toutes seules) ;
    ///  - un admin peut lire / changer les réglages de la partie et supprimer des sauvegardes partagées :
    ///    c'est toujours l'hôte qui exécute, la clé admin du groupe ne quitte jamais son PC.
    /// Sécurité : le mod multijoueur vérifie l'expéditeur de chaque message de chat (SenderIs),
    /// et on empêche l'hôte de relayer un message marqué qui ne vient pas de lui.
    /// </summary>
    public static class AdminChannel
    {
        public const string Tag = "~bsb1~";
        private static BrowserConfig Cfg => BrowserConfig.Current;
        private static Harmony _h;

        /// <summary>Actions à exécuter sur le thread principal (vidé par ServerBrowser.Update).</summary>
        public static readonly ConcurrentQueue<Action> Main = new ConcurrentQueue<Action>();

        // ------------------------------------------------------------ état hôte
        public enum Role { Player, Admin }
        private class Peer { public Role Role; public bool Verified; public bool AdminRefused; public bool Welcomed; public string SharedWorld = ""; }
        private static readonly Dictionary<string, Peer> Peers = new Dictionary<string, Peer>();
        private static readonly object PeersLock = new object();

        // ------------------------------------------------------------ état client
        public static volatile bool IsAdmin;
        public static volatile bool AdminRefused;
        public static string HostPid = "";
        public static string LastResult = "";
        /// <summary>Nom de l'hôte de la partie rejointe (pour l'afficher sur la relance).</summary>
        public static string JoinedHostName = "";
        public class RemoteSettings { public string name = "", desc = "", lang = ""; public int max; public bool hasPlayerPw, hasAdminPw, publish; public bool loaded; }
        public static RemoteSettings Settings = new RemoteSettings();
        public class RemoteWorldInfo { public string id, name; public int day; public bool deleted; public int daysLeft; }
        public class RemotePlayer { public string id, name; public bool admin, host; }
        public class RemoteBan { public string key, name; }
        public static List<RemotePlayer> Players = new List<RemotePlayer>();
        public static List<RemoteBan> Bans = new List<RemoteBan>();
        public static string PlayersStatus = "";
        private static List<RemotePlayer> _incomingPlayers;
        private static List<RemoteBan> _incomingBans;
        /// <summary>Message affiché au joueur expulsé par un admin.</summary>
        public static string KickedNotice = "";
        public const string KickTag = "BAMP:kicked";

        /// <summary>
        /// Mode local : l'hôte ouvre lui-même la fenêtre admin. Les demandes ne passent pas par le réseau,
        /// elles sont traitées directement sur ce PC avec les mêmes règles.
        /// </summary>
        public const string LocalPid = "\u0001local";
        public static bool LocalMode;

        public static void EnterLocalMode()
        {
            ResetClient();
            LocalMode = true;
            HostPid = LocalPid;
            IsAdmin = true;
        }
        public static List<RemoteWorldInfo> Worlds = new List<RemoteWorldInfo>();
        public static string WorldsStatus = "";
        private static List<RemoteWorldInfo> _incoming;

        // =====================================================================
        public static void Install()
        {
            try
            {
                _h = new Harmony("bampserverbrowser.adminchannel");
                var add = AccessTools.Method(typeof(MPChat), nameof(MPChat.AddMessage));
                var priv = AccessTools.Method(typeof(MPServer), nameof(MPServer.SendChatPrivate));
                var bc = AccessTools.Method(typeof(MPServer), "Broadcast", new[] { typeof(MessageEnvelope) });
                if (add == null || priv == null || bc == null) { ModEntry.Warn("[Admin] multiplayer mod methods not found - admin channel disabled."); return; }
                _h.Patch(add, prefix: new HarmonyMethod(typeof(AdminChannel), nameof(AddMessagePrefix)));
                _h.Patch(priv, prefix: new HarmonyMethod(typeof(AdminChannel), nameof(SendPrivatePrefix)));
                _h.Patch(bc, prefix: new HarmonyMethod(typeof(AdminChannel), nameof(BroadcastPrefix)));
                ModEntry.Info("[Admin] admin channel installed.");
            }
            catch (Exception ex) { ModEntry.Warn("[Admin] install: " + ex.Message); }
        }

        public static void Uninstall() { try { _h?.UnpatchAll(_h.Id); } catch { } }

        private static bool Marked(string text) => text != null && text.StartsWith(Tag, StringComparison.Ordinal);

        // Tout message marqué est intercepté (jamais affiché dans le chat).
        private static bool AddMessagePrefix(string from, string to, string text)
        {
            if (!Marked(text)) return true;
            try
            {
                string me = ""; try { me = MPConfig.PlayerId ?? ""; } catch { }
                if (string.Equals(from, me, StringComparison.Ordinal)) return false; // écho de nos propres envois
                var o = JObject.Parse(text.Substring(Tag.Length));
                bool hosting = false; try { hosting = MPServer.IsRunning; } catch { }
                if (hosting) { string f = from; Main.Enqueue(() => HostReceive(f, o)); }
                else { string f = from; Main.Enqueue(() => ClientReceive(f, o)); }
            }
            catch (Exception ex) { ModEntry.Warn("[Admin] bad message: " + ex.Message); }
            return false;
        }

        // L'hôte ne relaie jamais un message marqué venant d'un autre joueur.
        private static bool SendPrivatePrefix(string fromId, string toId, string text)
        {
            if (!Marked(text)) return true;
            string me = ""; try { me = MPConfig.PlayerId ?? ""; } catch { }
            return string.Equals(fromId, me, StringComparison.Ordinal);
        }

        // ... ni ne diffuse à tout le monde un message marqué.
        private static bool BroadcastPrefix(MessageEnvelope env)
        {
            try { if (env != null && env.Type == MessageType.Chat && (env.Data ?? "").Contains(Tag)) return false; } catch { }
            return true;
        }

        // =====================================================================
        //  Hôte
        // =====================================================================

        /// <summary>Appelé par JoinPassword quand l'hôte accepte un Hello.</summary>
        public static void OnHostAccepted(string pid, bool hasMod, Role role, bool verified, bool adminRefused)
        {
            if (string.IsNullOrEmpty(pid) || !hasMod) return;
            lock (PeersLock) Peers[pid] = new Peer { Role = role, Verified = verified, AdminRefused = adminRefused };
        }

        public static void ResetHost() { lock (PeersLock) Peers.Clear(); }

        private static void SendTo(string pid, object payload)
        {
            if (pid == LocalPid) { try { ClientReceive(LocalPid, JObject.FromObject(payload)); } catch (Exception ex) { ModEntry.Warn("[Admin] local: " + ex.Message); } return; }
            try
            {
                string me = MPConfig.PlayerId ?? "";
                MPServer.SendChatPrivate(me, pid, Tag + JsonConvert.SerializeObject(payload, Formatting.None));
            }
            catch (Exception ex) { ModEntry.Warn("[Admin] send: " + ex.Message); }
        }

        /// <summary>Envoie le message d'accueil aux joueurs connectés qui ne l'ont pas encore reçu (thread principal).</summary>
        public static void TickHost()
        {
            List<(string pid, Peer p)> todo;
            HashSet<string> connected;
            try { connected = new HashSet<string>((MPServer.ConnectedClientPeers() ?? new List<(MPLink, string)>()).Select(x => x.Item2 ?? "")); }
            catch { return; }
            lock (PeersLock)
            {
                foreach (var k in Peers.Keys.Where(k => !connected.Contains(k)).ToList()) Peers.Remove(k);
                todo = Peers.Where(kv => !kv.Value.Welcomed).Select(kv => (kv.Key, kv.Value)).ToList();
                foreach (var t in todo) t.p.Welcomed = true;
            }
            TickRelaunchInfo();
            foreach (var (pid, p) in todo)
            {
                SendTo(pid, new { t = "w", r = p.Role == Role.Admin ? "a" : "p", bad = p.AdminRefused ? 1 : 0 });
                if (p.Role == Role.Admin) SendSettings(pid);
                ModEntry.Info($"[Admin] '{pid}' joined as {(p.Role == Role.Admin ? "admin" : "player")}{(p.Verified ? " (password ok)" : "")}.");
            }
            TickShareWorld();
        }

        /// <summary>
        /// Donne aux joueurs qui connaissent un mot de passe le code de sauvegarde DE CE MONDE seulement
        /// (celui du groupe d'origine si on relance le monde d'un ami), dès que le monde est connu.
        /// </summary>
        private static void TickShareWorld()
        {
            string world = CurrentWorldId();
            if (!WorldSync.ValidId(world)) return;
            string code = "";
            if (Cfg.joinedWorlds != null && Cfg.joinedWorlds.TryGetValue(world, out var jc)) code = jc ?? "";
            else if (WorldSync.Enabled) { try { code = Cfg.ShareCode(MPNet.PublicIp ?? ""); } catch { } }
            if (WorldSync.ParseCode(code) == null) return;
            List<string> send;
            lock (PeersLock)
            {
                send = Peers.Where(kv => kv.Value.Welcomed && (kv.Value.Verified || kv.Value.Role == Role.Admin) && kv.Value.SharedWorld != world).Select(kv => kv.Key).ToList();
                foreach (var pid in send) Peers[pid].SharedWorld = world;
            }
            foreach (var pid in send) SendTo(pid, new { t = "s", c = code, i = world });
        }

        private static bool IsAdminPid(string pid)
        {
            if (pid == LocalPid) return true;
            lock (PeersLock) return Peers.TryGetValue(pid ?? "", out var p) && p.Role == Role.Admin;
        }

        private static void SendSettings(string pid)
        {
            SendTo(pid, new
            {
                t = "g", n = Cfg.serverName ?? "", m = Cfg.maxPlayers, l = Cfg.language ?? "",
                pp = string.IsNullOrEmpty(Cfg.joinPassword) ? 0 : 1, ap = string.IsNullOrEmpty(Cfg.adminPassword) ? 0 : 1, pub = Cfg.publish ? 1 : 0
            });
            SendTo(pid, new { t = "gd", d = Cfg.description ?? "" });
            string world = CurrentWorldId();
            if (pid != LocalPid && world.Length > 0 && !string.IsNullOrEmpty(Cfg.adminPassword))
            {
                SendTo(pid, new { t = "rl", i = world, ah = RelaunchHash(world, Cfg.adminPassword) });
                SendTo(pid, new { t = "rp", p = Cfg.joinPassword ?? "" });
            }
        }

        private static string CurrentWorldId()
        {
            try { return MPSaveCoordinator.ActivePlaythroughId ?? ""; } catch { return ""; }
        }

        private static string _lastWorld = "", _lastAdminPw = "", _lastPlayerPw = "";
        private static float _nextWorldCheck;

        /// <summary>Renvoie les infos de relance aux admins quand le monde ou les mots de passe changent.</summary>
        private static void TickRelaunchInfo()
        {
            if (UnityEngine.Time.unscaledTime < _nextWorldCheck) return;
            _nextWorldCheck = UnityEngine.Time.unscaledTime + 10f;
            string w = CurrentWorldId(), a = Cfg.adminPassword ?? "", p = Cfg.joinPassword ?? "";
            if (w == _lastWorld && a == _lastAdminPw && p == _lastPlayerPw) return;
            _lastWorld = w; _lastAdminPw = a; _lastPlayerPw = p;
            List<string> admins;
            lock (PeersLock) admins = Peers.Where(kv => kv.Value.Welcomed && kv.Value.Role == Role.Admin).Select(kv => kv.Key).ToList();
            foreach (var pid in admins) SendSettings(pid);
        }

        // =====================================================================
        //  Relance d'une partie hors ligne par un admin
        // =====================================================================
        /// <summary>Ce qu'un admin garde sur son PC pour pouvoir relancer la partie quand l'hôte est hors ligne.</summary>
        public class RelaunchInfo
        {
            public string worldId = "", adminHash = "";
            [JsonIgnore] public string playerPassword = "";
            [JsonProperty("playerPassword")] private string PlayerPasswordStored { get => SecretBox.Protect(playerPassword); set => playerPassword = SecretBox.Unprotect(value); }
            public string name = "", desc = "", lang = "", hostName = "";
            public int max = 8;
            public long savedAt;
        }

        private static string RelaunchPath() => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(BrowserConfig.FilePath), "relaunch.json");

        private static Dictionary<string, RelaunchInfo> _rlMem;

        private static Dictionary<string, RelaunchInfo> LoadRelaunchAll()
        {
            if (_rlMem != null) return _rlMem;
            _rlMem = new Dictionary<string, RelaunchInfo>();
            try
            {
                string f = RelaunchPath();
                if (System.IO.File.Exists(f))
                    _rlMem = JsonConvert.DeserializeObject<Dictionary<string, RelaunchInfo>>(System.IO.File.ReadAllText(f)) ?? new Dictionary<string, RelaunchInfo>();
            }
            catch { }
            return _rlMem;
        }

        public static RelaunchInfo GetRelaunch(string worldId)
        {
            if (string.IsNullOrEmpty(worldId)) return null;
            return LoadRelaunchAll().TryGetValue(worldId, out var r) ? r : null;
        }

        private static void SaveRelaunch(RelaunchInfo r)
        {
            try
            {
                var all = LoadRelaunchAll();
                all[r.worldId] = r;
                System.IO.File.WriteAllText(RelaunchPath(), JsonConvert.SerializeObject(all, Formatting.Indented));
            }
            catch (Exception ex) { ModEntry.Warn("[Admin] relaunch info: " + ex.Message); }
        }

        /// <summary>Empreinte lente du mot de passe admin, salée par le monde (vérifiée hors ligne sur le PC de l'admin).</summary>
        public static string RelaunchHash(string worldId, string pw)
        {
            byte[] salt = Encoding.UTF8.GetBytes("bsb-relaunch:" + (worldId ?? ""));
            using (var kdf = new System.Security.Cryptography.Rfc2898DeriveBytes(pw ?? "", salt, 20000))
                return BitConverter.ToString(kdf.GetBytes(32)).Replace("-", "").ToLowerInvariant();
        }

        public static bool CheckRelaunchPassword(RelaunchInfo r, string pw)
        {
            if (r == null || string.IsNullOrEmpty(pw) || string.IsNullOrEmpty(r.adminHash)) return false;
            string h = RelaunchHash(r.worldId, pw);
            if (h.Length != r.adminHash.Length) return false;
            int diff = 0;
            for (int i = 0; i < h.Length; i++) diff |= h[i] ^ r.adminHash[i];
            return diff == 0;
        }

        private static string _pendingWorld = "", _pendingHash = "";

        /// <summary>Appelé quand l'hôte a changé ses réglages (pour que la fenêtre admin se mette à jour).</summary>
        public static Action SettingsChanged;

        private static void HostReceive(string from, JObject o)
        {
            string t = (string)o["t"] ?? "";
            if (!IsAdminPid(from)) { SendTo(from, new { t = "r", ok = 0, m = "You are not admin of this game." }); return; }
            switch (t)
            {
                case "get":
                    SendSettings(from);
                    SendWorldList(from);
                    break;
                case "set":
                {
                    string f = (string)o["f"] ?? "", v = ((string)o["v"] ?? "").Trim();
                    string msg = ApplySetting(f, v);
                    ModEntry.Info($"[Admin] '{from}' changed setting '{f}'.");
                    BrowserConfig.Save();
                    SettingsChanged?.Invoke();
                    SendSettings(from);
                    SendTo(from, new { t = "r", ok = msg == null ? 1 : 0, m = msg ?? "Saved." });
                    break;
                }
                case "pl": SendPlayers(from); break;
                case "kick": Reply(from, HostKick(from, (string)o["i"] ?? "", ban: false)); SendPlayers(from); break;
                case "ban": Reply(from, HostKick(from, (string)o["i"] ?? "", ban: true)); SendPlayers(from); break;
                case "unban":
                {
                    string key = (string)o["k"] ?? "";
                    string res = "";
                    try { res = MPServer.UnbanPlayer(key) ?? ""; } catch (Exception ex) { res = "! " + ex.Message; }
                    ModEntry.Info($"[Admin] '{Who(from)}' unbanned '{key}'.");
                    Reply(from, res.StartsWith("! ") ? res : "Player unbanned.");
                    SendPlayers(from);
                    break;
                }
                case "msg":
                {
                    string text = new string(((string)o["m"] ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
                    if (text.Length == 0) { Reply(from, "! Empty message."); break; }
                    if (text.Length > 150) text = text.Substring(0, 150);
                    try
                    {
                        MPServer.BroadcastChat(MPConfig.PlayerId, (from == LocalPid ? "[Host] " : "[Admin " + Who(from) + "] ") + text);
                        Reply(from, "Message sent to everyone.");
                    }
                    catch (Exception ex) { Reply(from, "! Message not sent: " + ex.Message); }
                    break;
                }
                case "save":
                {
                    bool lobby = true; try { lobby = MPServer.IsInLobby; } catch { }
                    if (lobby) { Reply(from, "! The game has not started yet: nothing to save."); break; }
                    try { MPSaveCoordinator.HostSaveNow("admin"); ModEntry.Info($"[Admin] '{Who(from)}' forced a save."); Reply(from, "Save requested: the game is being saved."); }
                    catch (Exception ex) { Reply(from, "! Save failed: " + ex.Message); }
                    break;
                }
                case "rs":
                {
                    string id = (string)o["i"] ?? "";
                    ModEntry.Info($"[Admin] '{Who(from)}' restores shared save {id}.");
                    Task.Run(() => WorldSync.RestoreRemoteWorld(id)).ContinueWith(task => Main.Enqueue(() =>
                    {
                        int st = task.IsFaulted ? 0 : task.Result;
                        Reply(from, st == 200 ? "Save restored from the trash." : st == 410 ? "! This save is no longer in the trash." : "! Restore refused (" + (st == 0 ? "save server unreachable" : "HTTP " + st) + ").");
                        SendWorldList(from);
                    }));
                    break;
                }
                case "del":
                {
                    string id = (string)o["i"] ?? "";
                    ModEntry.Info($"[Admin] '{from}' deletes shared save {id}.");
                    Task.Run(() => WorldSync.DeleteRemoteWorld(id)).ContinueWith(task => Main.Enqueue(() =>
                    {
                        int st = task.IsFaulted ? 0 : task.Result;
                        SendTo(from, new { t = "r", ok = st == 200 || st == 404 ? 1 : 0, m = st == 200 || st == 404 ? "Save moved to the server trash." : "Deletion refused (" + (st == 0 ? "save server unreachable" : "HTTP " + st) + ")." });
                        SendWorldList(from);
                    }));
                    break;
                }
            }
        }

        private static void Reply(string pid, string msg)
        {
            bool bad = msg.StartsWith("! ");
            SendTo(pid, new { t = "r", ok = bad ? 0 : 1, m = bad ? msg.Substring(2) : msg });
        }

        private static string Who(string pid)
        {
            if (pid == LocalPid) return "host";
            try { return MPServer.DisplayNameFor(pid) ?? pid; } catch { return pid; }
        }

        private static readonly System.Reflection.MethodInfo PeerForPidM = AccessTools.Method(typeof(MPServer), "PeerForPid");

        /// <summary>Expulse (et bannit si demandé) un joueur connecté. Renvoie le message à afficher ("! " = erreur).</summary>
        private static string HostKick(string from, string target, bool ban)
        {
            string me = ""; try { me = MPConfig.PlayerId ?? ""; } catch { }
            if (string.IsNullOrEmpty(target)) return "! No player selected.";
            if (target == me) return "! The host can't be kicked.";
            if (target == from) return "! You can't kick yourself.";
            if (from != LocalPid && IsAdminPid(target)) return "! Only the host can kick another admin.";
            bool connected = false;
            try { connected = (MPServer.ConnectedClientPeers() ?? new List<(MPLink, string)>()).Any(x => x.Item2 == target); } catch { }
            if (!connected && !ban) return "! This player is not connected anymore.";
            string name = Who(target);
            if (ban)
            {
                try { MPServer.BanPlayer(target); } catch (Exception ex) { return "! Ban failed: " + ex.Message; }
            }
            if (connected)
            {
                try
                {
                    bool lobby = true; try { lobby = MPServer.IsInLobby; } catch { }
                    if (lobby) MPServer.KickFromLobby(target);
                    else (PeerForPidM?.Invoke(null, new object[] { target }) as MPLink)?.Disconnect(Encoding.UTF8.GetBytes(KickTag));
                }
                catch (Exception ex) { return "! Kick failed: " + (ex.InnerException ?? ex).Message; }
            }
            ModEntry.Info($"[Admin] '{Who(from)}' {(ban ? "banned" : "kicked")} '{name}'.");
            return (ban ? "Banned: " : "Kicked: ") + name + ".";
        }

        private static void SendPlayers(string pid)
        {
            string me = ""; try { me = MPConfig.PlayerId ?? ""; } catch { }
            SendTo(pid, new { t = "p0" });
            SendTo(pid, new { t = "pi", i = me, n = Short(Who(me), 32), a = 1, h = 1 });
            try
            {
                foreach (var (_, p) in (MPServer.ConnectedClientPeers() ?? new List<(MPLink, string)>()).Take(32))
                    if (!string.IsNullOrEmpty(p)) SendTo(pid, new { t = "pi", i = p, n = Short(Who(p), 32), a = IsAdminPid(p) ? 1 : 0, h = 0 });
            }
            catch { }
            SendTo(pid, new { t = "p1" });
            SendTo(pid, new { t = "b0" });
            try
            {
                foreach (var b in (MPServer.BanList() ?? new List<MPConfig.BanRow>()).Take(30))
                    SendTo(pid, new { t = "bi", k = Short(b.Key ?? b.Name ?? "", 64), n = Short(string.IsNullOrEmpty(b.Name) ? (b.Key ?? "?") : b.Name, 32) });
            }
            catch { }
            SendTo(pid, new { t = "b1" });
        }

        private static string Short(string v, int n) => (v ?? "").Length > n ? v.Substring(0, n) : (v ?? "");

        private static string ApplySetting(string f, string v)
        {
            switch (f)
            {
                case "n": Cfg.serverName = v.Length > 48 ? v.Substring(0, 48) : v; return null;
                case "d": Cfg.description = v.Length > 120 ? v.Substring(0, 120) : v; return null;
                case "m": if (int.TryParse(v, out int m) && m >= 2 && m <= 32) { Cfg.maxPlayers = m; return null; } return "Max players must be between 2 and 32.";
                case "pp": Cfg.joinPassword = v.Length > 64 ? v.Substring(0, 64) : v; return null;
                case "ap":
                    if (v.Length == 0) return "The admin password can't be empty (you would lose admin rights).";
                    Cfg.adminPassword = v.Length > 64 ? v.Substring(0, 64) : v; return null;
                case "pub": Cfg.publish = v == "1"; return null;
                case "l":
                    string l = v.ToUpperInvariant();
                    if (Array.IndexOf(BrowserConfig.Languages, l) < 0) return "Unknown language.";
                    Cfg.language = l; return null;
            }
            return "Unknown setting.";
        }

        private static void SendWorldList(string pid)
        {
            if (!WorldSync.Enabled) { SendTo(pid, new { t = "we", m = "Shared saves are disabled on the host." }); return; }
            Task.Run(() => WorldSync.ListRemoteWorlds()).ContinueWith(task => Main.Enqueue(() =>
            {
                if (task.IsFaulted || task.Result == null) { SendTo(pid, new { t = "we", m = "Save server unreachable from the host." }); return; }
                SendTo(pid, new { t = "w0" });
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (var w in task.Result.Where(w => !w.deleted).OrderByDescending(w => w.savedAt).Take(40))
                    SendTo(pid, new { t = "wi", i = w.id, n = Short(w.name, 40), d = w.worldDay });
                foreach (var w in task.Result.Where(w => w.deleted).OrderByDescending(w => w.deletedAt).Take(20))
                    SendTo(pid, new { t = "wi", i = w.id, n = Short(w.name, 40), d = w.worldDay, x = 1, dl = Math.Max(0, 30 - (int)((now - w.deletedAt) / 86400)) });
                SendTo(pid, new { t = "w1" });
            }));
        }

        // =====================================================================
        //  Client
        // =====================================================================
        public static void ResetClient()
        {
            IsAdmin = false; HostPid = ""; LastResult = ""; Settings = new RemoteSettings(); LocalMode = false;
            Worlds = new List<RemoteWorldInfo>(); WorldsStatus = ""; _incoming = null;
            Players = new List<RemotePlayer>(); Bans = new List<RemoteBan>(); PlayersStatus = ""; _incomingPlayers = null; _incomingBans = null;
        }

        private static void ClientReceive(string from, JObject o)
        {
            string t = (string)o["t"] ?? "";
            if (t == "w")
            {
                HostPid = from ?? "";
                IsAdmin = (string)o["r"] == "a";
                AdminRefused = (int?)o["bad"] == 1;
                ModEntry.Info("[Admin] joined as " + (IsAdmin ? "admin." : "player.") + (AdminRefused ? " Admin password refused." : ""));
                return;
            }
            if (HostPid.Length == 0 || !string.Equals(from, HostPid, StringComparison.Ordinal)) return;
            switch (t)
            {
                case "s": AcceptSharedWorld(from, ((string)o["c"] ?? "").Trim(), ((string)o["i"] ?? "").Trim()); break;
                case "g":
                    Settings.name = (string)o["n"] ?? ""; Settings.max = (int?)o["m"] ?? 0; Settings.lang = (string)o["l"] ?? "";
                    Settings.hasPlayerPw = (int?)o["pp"] == 1; Settings.hasAdminPw = (int?)o["ap"] == 1; Settings.publish = (int?)o["pub"] == 1;
                    Settings.loaded = true;
                    break;
                case "gd": Settings.desc = (string)o["d"] ?? ""; break;
                case "rl": _pendingWorld = (string)o["i"] ?? ""; _pendingHash = (string)o["ah"] ?? ""; break;
                case "rp":
                    if (_pendingWorld.Length > 0 && _pendingHash.Length > 0)
                    {
                        SaveRelaunch(new RelaunchInfo
                        {
                            worldId = _pendingWorld, adminHash = _pendingHash, playerPassword = (string)o["p"] ?? "",
                            name = Settings.name, desc = Settings.desc, lang = Settings.lang, max = Settings.max > 0 ? Settings.max : 8,
                            hostName = JoinedHostName ?? "", savedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });
                        ModEntry.Info("[Admin] relaunch info saved for this world.");
                    }
                    break;
                case "r": LastResult = ((int?)o["ok"] == 1 ? "" : "! ") + ((string)o["m"] ?? ""); break;
                case "w0": _incoming = new List<RemoteWorldInfo>(); WorldsStatus = "Loading..."; break;
                case "wi": _incoming?.Add(new RemoteWorldInfo { id = (string)o["i"] ?? "", name = (string)o["n"] ?? "", day = (int?)o["d"] ?? 0, deleted = (int?)o["x"] == 1, daysLeft = (int?)o["dl"] ?? 0 }); break;
                case "w1": Worlds = _incoming ?? new List<RemoteWorldInfo>(); _incoming = null; WorldsStatus = Worlds.Count == 0 ? "No shared saves." : ""; break;
                case "p0": _incomingPlayers = new List<RemotePlayer>(); break;
                case "pi": _incomingPlayers?.Add(new RemotePlayer { id = (string)o["i"] ?? "", name = (string)o["n"] ?? "", admin = (int?)o["a"] == 1, host = (int?)o["h"] == 1 }); break;
                case "p1": Players = _incomingPlayers ?? new List<RemotePlayer>(); _incomingPlayers = null; PlayersStatus = ""; break;
                case "b0": _incomingBans = new List<RemoteBan>(); break;
                case "bi": _incomingBans?.Add(new RemoteBan { key = (string)o["k"] ?? "", name = (string)o["n"] ?? "" }); break;
                case "b1": Bans = _incomingBans ?? new List<RemoteBan>(); _incomingBans = null; break;
                case "we": WorldsStatus = (string)o["m"] ?? ""; break;
            }
        }

        /// <summary>
        /// L'hôte propose de synchroniser SON monde : seul ce monde est rattaché à son serveur.
        /// Notre propre groupe et nos autres mondes ne changent pas. Refusé si ce monde existe déjà
        /// sur ce PC sans venir de cet hôte (un hôte ne peut pas s'approprier un de nos mondes).
        /// </summary>
        private static void AcceptSharedWorld(string hostPid, string code, string world)
        {
            if (!WorldSync.ValidId(world)) return;
            var parsed = WorldSync.ParseCode(code);
            if (parsed == null) return;
            if (string.Equals(parsed.Value.key, Cfg.GroupKey, StringComparison.Ordinal)) return; // monde de notre propre groupe
            var map = Cfg.joinedWorlds ?? (Cfg.joinedWorlds = new Dictionary<string, string>());
            if (map.TryGetValue(world, out var existing) && existing == code) return;
            var (exists, lastHost) = WorldSync.LocalWorldInfo(world);
            bool fromThisHost = string.Equals(lastHost, hostPid, StringComparison.OrdinalIgnoreCase);
            if ((exists || existing != null) && !fromThisHost)
            {
                ModEntry.Warn($"[Admin] shared save offer for world {world} ignored: this world is already on this PC and was not hosted by '{hostPid}'.");
                return;
            }
            if (existing == null && map.Count >= 50) { ModEntry.Warn("[Admin] too many joined worlds, shared save offer ignored."); return; }
            map[world] = code;
            BrowserConfig.Save();
            ModEntry.Info($"[Admin] shared saves enabled for this world only (host server {code.Substring(code.LastIndexOf('@') + 1)}).");
        }

        private static void SendToHost(object payload)
        {
            if (LocalMode)
            {
                bool hosting = false; try { hosting = MPServer.IsRunning; } catch { }
                if (!hosting) { LastResult = "! You are not hosting anymore."; return; }
                var o = JObject.FromObject(payload);
                Main.Enqueue(() => HostReceive(LocalPid, o));
                return;
            }
            if (HostPid.Length == 0) return;
            try { MPClient.SendChat(Tag + JsonConvert.SerializeObject(payload, Formatting.None), HostPid); }
            catch (Exception ex) { ModEntry.Warn("[Admin] send to host: " + ex.Message); }
        }

        public static void RequestAll() { WorldsStatus = "Loading..."; PlayersStatus = "Loading..."; SendToHost(new { t = "get" }); SendToHost(new { t = "pl" }); }
        public static void RequestPlayers() { PlayersStatus = "Loading..."; SendToHost(new { t = "pl" }); }
        public static void Kick(string pid) { LastResult = "Kicking..."; SendToHost(new { t = "kick", i = pid }); }
        public static void Ban(string pid) { LastResult = "Banning..."; SendToHost(new { t = "ban", i = pid }); }
        public static void Unban(string key) { LastResult = "Unbanning..."; SendToHost(new { t = "unban", k = key }); }
        public static void Message(string text) { LastResult = "Sending..."; SendToHost(new { t = "msg", m = (text ?? "").Length > 150 ? text.Substring(0, 150) : (text ?? "") }); }
        public static void SaveNow() { LastResult = "Saving..."; SendToHost(new { t = "save" }); }
        public static void RestoreWorld(string id) { LastResult = "Restoring..."; SendToHost(new { t = "rs", i = id }); }
        public static void Set(string field, string value) { LastResult = "Sending..."; SendToHost(new { t = "set", f = field, v = value ?? "" }); }
        public static void DeleteWorld(string id) { LastResult = "Deleting..."; SendToHost(new { t = "del", i = id }); }
    }
}
