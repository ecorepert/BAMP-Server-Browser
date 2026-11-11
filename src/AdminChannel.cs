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
        private class Peer { public Role Role; public bool Verified; public bool AdminRefused; public bool Welcomed; }
        private static readonly Dictionary<string, Peer> Peers = new Dictionary<string, Peer>();
        private static readonly object PeersLock = new object();

        // ------------------------------------------------------------ état client
        public static volatile bool IsAdmin;
        public static volatile bool AdminRefused;
        public static string HostPid = "";
        public static string LastResult = "";
        public class RemoteSettings { public string name = "", desc = "", lang = ""; public int max; public bool hasPlayerPw, hasAdminPw, publish; public bool loaded; }
        public static RemoteSettings Settings = new RemoteSettings();
        public class RemoteWorldInfo { public string id, name; public int day; }
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
            foreach (var (pid, p) in todo)
            {
                SendTo(pid, new { t = "w", r = p.Role == Role.Admin ? "a" : "p", bad = p.AdminRefused ? 1 : 0 });
                // les sauvegardes partagées ne sont données qu'aux joueurs qui connaissent un mot de passe
                if ((p.Verified || p.Role == Role.Admin) && WorldSync.Enabled)
                {
                    string code = ""; try { code = Cfg.ShareCode(MPNet.PublicIp ?? ""); } catch { }
                    if (code.Length > 0) SendTo(pid, new { t = "s", c = code });
                }
                if (p.Role == Role.Admin) SendSettings(pid);
                ModEntry.Info($"[Admin] '{pid}' joined as {(p.Role == Role.Admin ? "admin" : "player")}{(p.Verified ? " (password ok)" : "")}.");
            }
        }

        private static bool IsAdminPid(string pid)
        {
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
        }

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
                foreach (var w in task.Result.Where(w => !w.deleted).OrderByDescending(w => w.savedAt).Take(40))
                    SendTo(pid, new { t = "wi", i = w.id, n = (w.name ?? "").Length > 40 ? w.name.Substring(0, 40) : (w.name ?? ""), d = w.worldDay });
                SendTo(pid, new { t = "w1" });
            }));
        }

        // =====================================================================
        //  Client
        // =====================================================================
        public static void ResetClient()
        {
            IsAdmin = false; HostPid = ""; LastResult = ""; Settings = new RemoteSettings();
            Worlds = new List<RemoteWorldInfo>(); WorldsStatus = ""; _incoming = null;
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
                case "s":
                {
                    string code = ((string)o["c"] ?? "").Trim();
                    if (code.Length >= 6 && code != Cfg.shareKey)
                    {
                        Cfg.shareKey = code;
                        BrowserConfig.Save();
                        ModEntry.Info("[Admin] shared saves of this game enabled.");
                    }
                    break;
                }
                case "g":
                    Settings.name = (string)o["n"] ?? ""; Settings.max = (int?)o["m"] ?? 0; Settings.lang = (string)o["l"] ?? "";
                    Settings.hasPlayerPw = (int?)o["pp"] == 1; Settings.hasAdminPw = (int?)o["ap"] == 1; Settings.publish = (int?)o["pub"] == 1;
                    Settings.loaded = true;
                    break;
                case "gd": Settings.desc = (string)o["d"] ?? ""; break;
                case "r": LastResult = ((int?)o["ok"] == 1 ? "" : "! ") + ((string)o["m"] ?? ""); break;
                case "w0": _incoming = new List<RemoteWorldInfo>(); WorldsStatus = "Loading..."; break;
                case "wi": _incoming?.Add(new RemoteWorldInfo { id = (string)o["i"] ?? "", name = (string)o["n"] ?? "", day = (int?)o["d"] ?? 0 }); break;
                case "w1": Worlds = _incoming ?? new List<RemoteWorldInfo>(); _incoming = null; WorldsStatus = Worlds.Count == 0 ? "No shared saves." : ""; break;
                case "we": WorldsStatus = (string)o["m"] ?? ""; break;
            }
        }

        private static void SendToHost(object payload)
        {
            if (HostPid.Length == 0) return;
            try { MPClient.SendChat(Tag + JsonConvert.SerializeObject(payload, Formatting.None), HostPid); }
            catch (Exception ex) { ModEntry.Warn("[Admin] send to host: " + ex.Message); }
        }

        public static void RequestAll() { WorldsStatus = "Loading..."; SendToHost(new { t = "get" }); }
        public static void Set(string field, string value) { LastResult = "Sending..."; SendToHost(new { t = "set", f = field, v = value ?? "" }); }
        public static void DeleteWorld(string id) { LastResult = "Deleting..."; SendToHost(new { t = "del", i = id }); }
    }
}
