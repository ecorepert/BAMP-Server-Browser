using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BigAmbitionsMP;
using Newtonsoft.Json;
using UnityEngine;

namespace BAMPServerBrowser
{
    public class RemoteWorld
    {
        public string id = "";
        public long savedAt;
        public int worldDay = -1;
        public string name = "";
        public string players = "";
        public string uploader = "";
        public long size;
        public bool deleted;
        public long deletedAt;
    }

    /// <summary>
    /// Mondes partagés : garde sur un serveur de sauvegardes la version la plus récente de chaque
    /// monde multijoueur (dossier de "playthrough" du mod multijoueur), pour qu'un membre puisse
    /// héberger le monde à jour même si le dernier hôte n'est pas là.
    ///
    /// Chaque monde appartient à UN groupe :
    ///  - les mondes de ce PC vont dans son propre groupe (shareKey) ;
    ///  - un monde reçu en rejoignant la partie d'un ami (joinedWorlds : id -> code de l'ami)
    ///    ne se synchronise qu'avec le serveur de cet ami, et rien d'autre ne lui est envoyé.
    /// </summary>
    public class WorldSync
    {
        private static BrowserConfig Cfg => BrowserConfig.Current;
        private static readonly Regex IdRe = new Regex("^[0-9a-fA-F]{8,64}$");
        private const long MaxDownloadBytes = 80L * 1024 * 1024;     // archive compressée
        private const long MaxUnpackedChars = 400L * 1024 * 1024;    // archive décompressée
        private const int MaxFilesPerWorld = 5000;

        /// <summary>Le propre groupe de ce PC est configuré (code + serveur de sauvegardes).</summary>
        public static bool Enabled => Cfg.GroupKey.Length >= 6 && Cfg.WorldsBaseUrl.Length > 0;
        private static bool AnyTarget => Enabled || (Cfg.joinedWorlds != null && Cfg.joinedWorlds.Count > 0);

        public static bool ValidId(string id) => !string.IsNullOrEmpty(id) && IdRe.IsMatch(id);

        public string Status = "";
        /// <summary>Vrai si la clé admin de ce PC est celle de son propre groupe (seul l'admin peut supprimer).</summary>
        public static volatile bool IsAdmin;
        public string LastUpload = "";
        public bool Busy;

        private readonly Action<Action> _post;
        private float _nextMenuSync, _nextHostCheck;
        private bool _wasHosting;
        // clé "cible|monde" -> savedAt
        private readonly Dictionary<string, long> _uploaded = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, RemoteWorld>> _remote = new Dictionary<string, Dictionary<string, RemoteWorld>>(StringComparer.OrdinalIgnoreCase);

        public WorldSync(Action<Action> post) { _post = post; }

        // ------------------------------------------------------------------ cibles
        /// <summary>Un serveur de sauvegardes + un code de groupe, et les mondes qui y vont.</summary>
        private class Target
        {
            public string Name, BaseUrl, GroupKey, AdminKey;
            public bool Own;
            /// <summary>Mondes de cette cible (null = tous les mondes locaux sauf Exclude).</summary>
            public HashSet<string> Only;
            public HashSet<string> Exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public string Id => (GroupKey + "|" + BaseUrl).ToLowerInvariant();
            public bool Has(string worldId) => Only != null ? Only.Contains(worldId) : !Exclude.Contains(worldId);
        }

        private static Target OwnTarget()
        {
            if (!Enabled) return null;
            var t = new Target { Name = "own", BaseUrl = Cfg.WorldsBaseUrl, GroupKey = Cfg.GroupKey, AdminKey = (Cfg.adminKey ?? "").Trim(), Own = true };
            foreach (var k in (Cfg.joinedWorlds ?? new Dictionary<string, string>()).Keys) t.Exclude.Add(k);
            return t;
        }

        /// <summary>"code@hote:port" -> (code, http://hote:port) ; null si pas d'adresse.</summary>
        public static (string key, string url)? ParseCode(string code)
        {
            code = (code ?? "").Trim();
            int at = code.LastIndexOf('@');
            if (at < 6 || at >= code.Length - 1) return null;
            string key = code.Substring(0, at), host = code.Substring(at + 1);
            if (key.Length > 128 || host.Length > 255 || host.Any(c => char.IsWhiteSpace(c) || c == '/' || c == '\\' || c == '?' || c == '#' || c == '@')) return null;
            return (key, "http://" + host);
        }

        private static List<Target> Targets()
        {
            var res = new List<Target>();
            var own = OwnTarget();
            if (own != null) res.Add(own);
            // mondes reçus d'autres hôtes, regroupés par serveur
            foreach (var g in (Cfg.joinedWorlds ?? new Dictionary<string, string>()).Where(kv => ValidId(kv.Key)).GroupBy(kv => kv.Value ?? ""))
            {
                var p = ParseCode(g.Key);
                if (p == null) continue;
                if (own != null && string.Equals(p.Value.key, own.GroupKey, StringComparison.Ordinal)) continue; // notre propre groupe
                res.Add(new Target
                {
                    Name = g.Key.Substring(g.Key.LastIndexOf('@') + 1), BaseUrl = p.Value.url, GroupKey = p.Value.key, AdminKey = "",
                    Only = new HashSet<string>(g.Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase)
                });
            }
            return res;
        }

        // Mondes déjà synchronisés sur ce PC, par cible (id -> savedAt). Un monde connu qui disparaît
        // localement a été supprimé par le joueur : on le supprime aussi sur le serveur (admin)
        // au lieu de le retélécharger.
        private static string KnownPath(Target t)
        {
            string h;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                h = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(t.GroupKey + "|" + t.BaseUrl.ToLowerInvariant()))).Replace("-", "").Substring(0, 12).ToLowerInvariant();
            return Path.Combine(Path.GetDirectoryName(BrowserConfig.FilePath), "worldsync-" + h + ".json");
        }

        private static Dictionary<string, long> LoadKnown(Target t)
        {
            try
            {
                string f = KnownPath(t);
                if (File.Exists(f))
                    return new Dictionary<string, long>(JsonConvert.DeserializeObject<Dictionary<string, long>>(File.ReadAllText(f)) ?? new Dictionary<string, long>(), StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly object KnownLock = new object();

        private static void MarkKnown(Target t, string id, long savedAt, bool remove = false)
        {
            lock (KnownLock)
            {
                var k = LoadKnown(t);
                if (remove) k.Remove(id); else k[id] = savedAt;
                try { File.WriteAllText(KnownPath(t), JsonConvert.SerializeObject(k, Formatting.Indented)); } catch { }
            }
        }

        // ------------------------------------------------------------------ API pour l'hôte (fenêtre admin)
        /// <summary>Liste des mondes du propre groupe de ce PC (appel réseau bloquant ; null si injoignable).</summary>
        public static List<RemoteWorld> ListRemoteWorlds()
        {
            var t = OwnTarget();
            if (t == null) return null;
            try { return FetchRemote(t).Values.ToList(); } catch { return null; }
        }

        /// <summary>Met un monde du propre groupe à la corbeille du serveur (clé admin de ce PC ; appel bloquant).</summary>
        public static int DeleteRemoteWorld(string id)
        {
            var t = OwnTarget();
            if (t == null) return 0;
            if (!ValidId(id)) return 400;
            try { return DeleteRemote(t, id); } catch { return 0; }
        }

        /// <summary>Le monde existe-t-il sur ce PC, et qui l'a hébergé en dernier ?</summary>
        public static (bool exists, string lastHost) LocalWorldInfo(string worldId)
        {
            try
            {
                var l = ScanLocal().FirstOrDefault(x => string.Equals(x.Id, worldId, StringComparison.OrdinalIgnoreCase));
                return l == null ? (false, "") : (true, l.Host ?? "");
            }
            catch { return (false, ""); }
        }

        private class Local
        {
            public string Id, Dir, Name, Players, Host;
            public long SavedAt;
            public int Day;
        }

        // ------------------------------------------------------------------ boucle
        public void Tick(bool inMenu, bool hosting, bool connected)
        {
            if (!AnyTarget) { Status = Cfg.WorldsBaseUrl.Length == 0 ? "Saves on this PC only (no save server configured)." : "Shared worlds disabled (group code too short)."; return; }
            float now = Time.unscaledTime;

            if (hosting)
            {
                if (!_wasHosting) { _wasHosting = true; _nextHostCheck = now + 30f; }
                if (!Busy && now >= _nextHostCheck) { _nextHostCheck = now + 30f; UploadNewer(onlyRecentSeconds: 6 * 3600); }
                return;
            }
            if (_wasHosting)
            {
                // fin d'hébergement : dernier envoi (la sauvegarde de sortie vient d'être écrite)
                _wasHosting = false;
                _nextMenuSync = now + 8f;
            }
            if (connected || !inMenu) return;
            if (!Busy && now >= _nextMenuSync) { _nextMenuSync = now + 90f; FullSync(); }
        }

        public void SyncNow() { _nextMenuSync = 0; _nextHostCheck = 0; }

        // ------------------------------------------------------------------ local
        private static List<Local> ScanLocal()
        {
            var res = new List<Local>();
            foreach (var pt in MPSaveManager.ListPlaythroughs() ?? new List<MPSaveManager.MpPlaythrough>())
            {
                var v = pt.Variants?.Where(x => !string.IsNullOrEmpty(x.SessionDir)).OrderByDescending(x => x.SavedAtUnix).FirstOrDefault();
                if (v == null || !ValidId(v.PlaythroughId)) continue;
                string dir = Path.GetDirectoryName(v.SessionDir.TrimEnd('/', '\\'));
                if (string.IsNullOrEmpty(dir) || !string.Equals(Path.GetFileName(dir), v.PlaythroughId, StringComparison.OrdinalIgnoreCase)) continue;
                res.Add(new Local
                {
                    Id = v.PlaythroughId, Dir = dir, Name = pt.Base, SavedAt = pt.NewestUnix > 0 ? pt.NewestUnix : v.SavedAtUnix,
                    Day = pt.NewestDay, Players = pt.Players ?? "", Host = pt.LastHostName ?? ""
                });
            }
            return res;
        }

        // ------------------------------------------------------------------ réseau
        private static HttpWebRequest Req(Target t, string method, string path)
        {
            var r = (HttpWebRequest)WebRequest.Create(t.BaseUrl + path);
            r.Method = method;
            r.Timeout = 60000;
            r.ReadWriteTimeout = 60000;
            r.AllowAutoRedirect = false;
            r.UserAgent = "BAMPServerBrowser/1.2";
            r.Headers["X-Share-Key"] = t.GroupKey;
            if (!string.IsNullOrEmpty(t.AdminKey)) r.Headers["X-Admin-Key"] = t.AdminKey;
            return r;
        }

        private static Dictionary<string, RemoteWorld> FetchRemote(Target t)
        {
            var r = Req(t, "GET", "/api/worlds");
            using (var resp = (HttpWebResponse)r.GetResponse())
            using (var sr = new StreamReader(LimitedCopy(resp.GetResponseStream(), 4 * 1024 * 1024)))
            {
                var obj = JsonConvert.DeserializeAnonymousType(sr.ReadToEnd(), new { worlds = new List<RemoteWorld>(), isAdmin = false });
                if (t.Own) IsAdmin = obj != null && obj.isAdmin;
                // les identifiants servent de noms de dossier : tout ce qui n'est pas un identifiant valide est ignoré
                var res = new Dictionary<string, RemoteWorld>(StringComparer.OrdinalIgnoreCase);
                foreach (var w in obj?.worlds ?? new List<RemoteWorld>())
                    if (w != null && ValidId(w.id) && t.Has(w.id)) res[w.id] = w;
                return res;
            }
        }

        private static int Upload(Target t, Local w)
        {
            byte[] data = Pack(w.Dir);
            var r = Req(t, "PUT", "/api/worlds/" + w.Id);
            r.ContentType = "application/octet-stream";
            r.ContentLength = data.Length;
            r.Headers["X-Saved-At"] = w.SavedAt.ToString();
            r.Headers["X-World-Day"] = w.Day.ToString();
            r.Headers["X-World-Name"] = Ascii(w.Name);
            r.Headers["X-Players"] = Ascii(w.Players);
            r.Headers["X-Uploader"] = Ascii(SafePlayer());
            using (var s = r.GetRequestStream()) s.Write(data, 0, data.Length);
            try { using (var resp = (HttpWebResponse)r.GetResponse()) return (int)resp.StatusCode; }
            catch (WebException ex) when (ex.Response is HttpWebResponse hr) { using (hr) return (int)hr.StatusCode; }
        }

        private static byte[] Download(Target t, string id)
        {
            var r = Req(t, "GET", "/api/worlds/" + id);
            using (var resp = (HttpWebResponse)r.GetResponse())
            {
                if (resp.ContentLength > MaxDownloadBytes) throw new InvalidDataException("world archive too large");
                return LimitedCopy(resp.GetResponseStream(), MaxDownloadBytes).ToArray();
            }
        }

        private static int DeleteRemote(Target t, string id)
        {
            var r = Req(t, "DELETE", "/api/worlds/" + id);
            r.Headers["X-Uploader"] = Ascii(SafePlayer());
            try { using (var resp = (HttpWebResponse)r.GetResponse()) return (int)resp.StatusCode; }
            catch (WebException ex) when (ex.Response is HttpWebResponse hr) { using (hr) return (int)hr.StatusCode; }
        }

        /// <summary>Copie un flux en mémoire en refusant au-delà de max octets.</summary>
        private static MemoryStream LimitedCopy(Stream src, long max)
        {
            var ms = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                if (ms.Length + n > max) throw new InvalidDataException("response too large");
                ms.Write(buf, 0, n);
            }
            ms.Position = 0;
            return ms;
        }

        private static string SafePlayer() { try { return MPConfig.PlayerId ?? ""; } catch { return ""; } }

        // Les en-têtes HTTP doivent rester en ASCII.
        private static string Ascii(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in (s ?? "").Normalize(NormalizationForm.FormD))
                if (c >= 32 && c < 127) sb.Append(c);
            return sb.ToString();
        }

        private static string Short(Target t, string msg) => t.Own ? msg : msg + " [" + t.Name + "]";

        // ------------------------------------------------------------------ actions
        /// <summary>En hébergeant : envoie chaque monde modifié récemment vers SON groupe.</summary>
        private void UploadNewer(long onlyRecentSeconds)
        {
            List<Local> locals;
            try { locals = ScanLocal(); } catch (Exception ex) { Status = "Reading saves: " + ex.Message; return; }
            long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var todo = new List<(Target t, Local w)>();
            foreach (var t in Targets())
            {
                _remote.TryGetValue(t.Id, out var rem);
                foreach (var l in locals)
                {
                    if (!t.Has(l.Id)) continue;
                    if (onlyRecentSeconds > 0 && nowUnix - l.SavedAt >= onlyRecentSeconds) continue;
                    if (_uploaded.TryGetValue(t.Id + "|" + l.Id, out var u) && u >= l.SavedAt) continue;
                    if (rem != null && rem.TryGetValue(l.Id, out var rw) && rw.savedAt >= l.SavedAt) continue;
                    if (!QuietFor(l.Dir, 5)) continue;
                    todo.Add((t, l));
                }
            }
            if (todo.Count == 0) return;
            Busy = true;
            Task.Run(() =>
            {
                var msgs = new List<string>();
                foreach (var (t, w) in todo)
                {
                    try
                    {
                        int st = Upload(t, w);
                        if (st == 200 || st == 409) { MarkKnown(t, w.Id, w.SavedAt); string k = t.Id + "|" + w.Id; long sa = w.SavedAt; _post(() => { _uploaded[k] = sa; }); }
                        msgs.Add(Short(t, st == 200 ? $"'{w.Name}' uploaded (day {w.Day})" : st == 409 ? $"'{w.Name}' already up to date" : $"'{w.Name}' refused (HTTP {st})"));
                    }
                    catch (Exception ex) { msgs.Add(Short(t, $"'{w.Name}' : {ex.Message}")); }
                }
                _post(() =>
                {
                    Busy = false;
                    LastUpload = DateTime.Now.ToString("HH:mm") + " - " + string.Join(", ", msgs);
                    Status = "Upload: " + string.Join(", ", msgs);
                    ModEntry.Info("[WorldSync] " + Status);
                });
            });
        }

        /// <summary>Au menu principal : synchronise chaque groupe avec ses propres mondes uniquement.</summary>
        private void FullSync()
        {
            List<Local> locals;
            try { locals = ScanLocal(); } catch (Exception ex) { Status = "Reading saves: " + ex.Message; return; }
            string root = "";
            try { root = MPSaveManager.MpVersionFolder(); } catch { }
            var targets = Targets();
            Busy = true;
            Status = "Syncing worlds…";
            Task.Run(() =>
            {
                var msgs = new List<string>();
                var results = new List<(Target t, Dictionary<string, RemoteWorld> after)>();
                int down = 0, up = 0, del = 0, total = 0, unreachable = 0;
                foreach (var t in targets)
                {
                    var r = SyncTarget(t, locals.Where(l => t.Has(l.Id)).ToList(), root, msgs, ref down, ref up, ref del);
                    if (r == null) { unreachable++; continue; }
                    results.Add((t, r));
                    total += r.Values.Count(w => !w.deleted);
                }
                _post(() =>
                {
                    Busy = false;
                    foreach (var (t, after) in results)
                    {
                        _remote[t.Id] = after;
                        foreach (var l in locals)
                            if (t.Has(l.Id) && after.TryGetValue(l.Id, out var r2) && !r2.deleted && r2.savedAt >= l.SavedAt) _uploaded[t.Id + "|" + l.Id] = l.SavedAt;
                    }
                    Status = (IsAdmin ? "[admin] " : "") + $"Shared worlds up to date ({total} on the server(s)" + (down > 0 ? $", {down} downloaded" : "") + (up > 0 ? $", {up} uploaded" : "")
                             + (del > 0 ? $", {del} deleted" : "") + (unreachable > 0 ? $", {unreachable} server(s) unreachable" : "") + ").";
                    if (msgs.Count > 0) ModEntry.Info("[WorldSync] " + string.Join(" | ", msgs));
                });
            });
        }

        /// <summary>Synchronise une cible (thread de fond). Renvoie l'état distant après coup, ou null si injoignable.</summary>
        private static Dictionary<string, RemoteWorld> SyncTarget(Target t, List<Local> locals, string root, List<string> msgs, ref int down, ref int up, ref int del)
        {
            Dictionary<string, RemoteWorld> remote;
            try { remote = FetchRemote(t); }
            catch (Exception ex) { msgs.Add(Short(t, "save server unreachable: " + ex.Message)); return null; }

            var byId = locals.ToDictionary(l => l.Id, StringComparer.OrdinalIgnoreCase);
            var known = LoadKnown(t);
            bool admin = t.Own && IsAdmin;
            foreach (var rw in remote.Values)
            {
                byId.TryGetValue(rw.id, out var loc);
                try
                {
                    if (rw.deleted)
                    {
                        // supprimé par l'admin : on ne le renvoie que s'il a été rejoué depuis
                        if (loc != null && loc.SavedAt > rw.deletedAt && QuietFor(loc.Dir, 5))
                        {
                            int st0 = Upload(t, loc);
                            if (st0 == 200) { up++; MarkKnown(t, loc.Id, loc.SavedAt); msgs.Add(Short(t, $"'{loc.Name}' restored on the server (played again after deletion)")); }
                        }
                        else MarkKnown(t, rw.id, 0, remove: true);
                        continue;
                    }
                    if (loc == null)
                    {
                        if (known.TryGetValue(rw.id, out long kv))
                        {
                            if (kv < 0)
                            {
                                // retiré de ce PC : on ne le reprend que s'il a progressé depuis
                                if (rw.savedAt <= -kv) continue;
                            }
                            else if (admin)
                            {
                                int st1 = DeleteRemote(t, rw.id);
                                if (st1 == 200 || st1 == 404) { del++; MarkKnown(t, rw.id, 0, remove: true); msgs.Add($"'{rw.name}' moved to the server trash (deleted on this PC, admin)"); }
                                else msgs.Add($"'{rw.name}': deletion refused (HTTP {st1})");
                                continue;
                            }
                            else
                            {
                                // sans clé admin : suppression locale seulement, le monde reste pour le groupe
                                MarkKnown(t, rw.id, -Math.Max(1, rw.savedAt));
                                msgs.Add(Short(t, $"'{rw.name}' removed from this PC only (only the admin can delete it from the server)"));
                                continue;
                            }
                        }
                        if (root.Length == 0) { msgs.Add("save folder not found"); continue; }
                        Unpack(Download(t, rw.id), Path.Combine(root, rw.id), root);
                        down++; MarkKnown(t, rw.id, rw.savedAt);
                        msgs.Add(Short(t, $"'{rw.name}' downloaded (day {rw.worldDay}, by {rw.uploader})"));
                    }
                    else if (loc.SavedAt < rw.savedAt)
                    {
                        Unpack(Download(t, rw.id), loc.Dir, Path.GetDirectoryName(loc.Dir));
                        down++; MarkKnown(t, rw.id, rw.savedAt);
                        msgs.Add(Short(t, $"'{rw.name}' updated (day {rw.worldDay}, by {rw.uploader})"));
                    }
                    else if (loc.SavedAt > rw.savedAt)
                    {
                        if (!QuietFor(loc.Dir, 5)) continue;
                        int st2 = Upload(t, loc);
                        if (st2 == 200) { up++; MarkKnown(t, loc.Id, loc.SavedAt); msgs.Add(Short(t, $"'{loc.Name}' uploaded (day {loc.Day})")); }
                        else if (st2 != 409) msgs.Add(Short(t, $"'{loc.Name}' refused (HTTP {st2})"));
                    }
                    else MarkKnown(t, loc.Id, loc.SavedAt);
                }
                catch (Exception ex) { msgs.Add(Short(t, $"'{rw.name}' : {ex.Message}")); }
            }
            // mondes locaux de cette cible absents du serveur
            foreach (var l in locals)
            {
                if (remote.ContainsKey(l.Id) || !QuietFor(l.Dir, 5)) continue;
                try
                {
                    int st = Upload(t, l);
                    if (st == 200) { up++; MarkKnown(t, l.Id, l.SavedAt); msgs.Add(Short(t, $"'{l.Name}' uploaded (day {l.Day})")); }
                    else if (st != 409) msgs.Add(Short(t, $"'{l.Name}' refused (HTTP {st})"));
                }
                catch (Exception ex) { msgs.Add(Short(t, $"'{l.Name}' : {ex.Message}")); }
            }
            try { return FetchRemote(t); } catch { return remote; }
        }

        // ------------------------------------------------------------------ fichiers
        private static bool QuietFor(string dir, int seconds)
        {
            try
            {
                var newest = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => File.GetLastWriteTimeUtc(f)).DefaultIfEmpty(DateTime.MinValue).Max();
                return (DateTime.UtcNow - newest).TotalSeconds >= seconds;
            }
            catch { return false; }
        }

        private class Bundle { public string format = "bamp-world-1"; public List<BFile> files = new List<BFile>(); }
        private class BFile { public string p; public string d; public long t; }

        private static byte[] Pack(string dir)
        {
            var b = new Bundle();
            string full = Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetFullPath(f).Substring(full.Length).Replace('\\', '/');
                b.files.Add(new BFile { p = rel, d = Convert.ToBase64String(File.ReadAllBytes(f)), t = File.GetLastWriteTimeUtc(f).Ticks });
            }
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress))
                using (var sw = new StreamWriter(gz, new UTF8Encoding(false)))
                    sw.Write(JsonConvert.SerializeObject(b));
                return ms.ToArray();
            }
        }

        /// <summary>Décompresse une archive de monde dans dir, qui doit être un sous-dossier direct de root.</summary>
        private static void Unpack(byte[] blob, string dir, string root)
        {
            string target = Path.GetFullPath(dir).TrimEnd('\\', '/');
            string rootFull = Path.GetFullPath(root).TrimEnd('\\', '/');
            if (!string.Equals(Path.GetDirectoryName(target), rootFull, StringComparison.OrdinalIgnoreCase) || !ValidId(Path.GetFileName(target)))
                throw new InvalidDataException("refused: destination outside the multiplayer save folder");

            Bundle b;
            using (var ms = new MemoryStream(blob))
            using (var gz = new GZipStream(ms, CompressionMode.Decompress))
            using (var sr = new StreamReader(gz, Encoding.UTF8))
            {
                // lecture bornée : une archive piégée ne peut pas saturer la mémoire
                var sb = new StringBuilder();
                var buf = new char[65536];
                int n;
                while ((n = sr.Read(buf, 0, buf.Length)) > 0)
                {
                    if (sb.Length + n > MaxUnpackedChars) throw new InvalidDataException("world archive too large once unpacked");
                    sb.Append(buf, 0, n);
                }
                b = JsonConvert.DeserializeObject<Bundle>(sb.ToString());
            }
            if (b?.files == null || b.format != "bamp-world-1" || b.files.Count == 0 || b.files.Count > MaxFilesPerWorld) throw new InvalidDataException("invalid world archive");
            if (!b.files.Any(f => (f.p ?? "").EndsWith("manifest.bamp.json", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("archive without manifest");

            string staging = target + ".sync-tmp";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            try
            {
                foreach (var f in b.files)
                {
                    string rel = (f.p ?? "").Replace('/', Path.DirectorySeparatorChar);
                    string path = Path.GetFullPath(Path.Combine(staging, rel));
                    if (!path.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("forbidden path in archive: " + f.p);
                    string ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext == ".dll" || ext == ".exe" || ext == ".bat" || ext == ".cmd" || ext == ".ps1") throw new InvalidDataException("forbidden file type in archive: " + f.p);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, Convert.FromBase64String(f.d ?? ""));
                    if (f.t > 0 && f.t < DateTime.MaxValue.Ticks) File.SetLastWriteTimeUtc(path, new DateTime(f.t, DateTimeKind.Utc));
                }
            }
            catch
            {
                try { Directory.Delete(staging, true); } catch { }
                throw;
            }
            // l'ancienne copie locale est gardée en sauvegarde, hors du dossier lu par le jeu
            if (Directory.Exists(target))
            {
                string backupRoot = Path.Combine(Path.GetDirectoryName(rootFull), "_BAMPServerBrowser_backups");
                Directory.CreateDirectory(backupRoot);
                string backup = Path.Combine(backupRoot, Path.GetFileName(target) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.Move(target, backup);
                PruneBackups(backupRoot, Path.GetFileName(target), 3);
            }
            Directory.Move(staging, target);
        }

        private static void PruneBackups(string root, string id, int keep)
        {
            try
            {
                foreach (var d in Directory.GetDirectories(root, id + "-*").OrderByDescending(x => x).Skip(keep))
                    Directory.Delete(d, true);
            }
            catch { }
        }
    }
}
