using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
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
    /// Mondes partagés : garde sur le master server la version la plus récente de chaque
    /// monde multijoueur (dossier de "playthrough" du mod multijoueur), pour qu'un membre du
    /// groupe puisse héberger le monde à jour même si le dernier hôte n'est pas là.
    ///  - envoi : quand on héberge, après chaque sauvegarde ; au menu, tout monde local plus récent ;
    ///  - réception : au menu principal, tout monde distant plus récent que la copie locale.
    /// Le groupe est défini par un code partagé (config shareKey, au moins 6 caractères).
    /// </summary>
    public class WorldSync
    {
        private static BrowserConfig Cfg => BrowserConfig.Current;
        public static bool Enabled => Cfg.GroupKey.Length >= 6 && Cfg.WorldsBaseUrl.Length > 0;

        public string Status = "";
        /// <summary>Vrai si la clé admin de ce PC est celle du groupe (seul l'admin peut supprimer).</summary>
        public static volatile bool IsAdmin;
        public string LastUpload = "";
        public bool Busy;

        private readonly Action<Action> _post;
        private float _nextMenuSync, _nextHostCheck;
        private bool _wasHosting;
        private readonly Dictionary<string, long> _uploaded = new Dictionary<string, long>();
        private Dictionary<string, RemoteWorld> _remote = new Dictionary<string, RemoteWorld>();

        public WorldSync(Action<Action> post) { _post = post; }

        // Mondes déjà synchronisés sur ce PC (id -> savedAt). Un monde connu qui disparaît
        // localement a été supprimé par le joueur : on le supprime aussi sur le serveur
        // au lieu de le retélécharger.
        private static string KnownPath()
        {
            string key = Cfg.GroupKey + "|" + Cfg.WorldsBaseUrl.ToLowerInvariant();
            string h;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                h = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").Substring(0, 12).ToLowerInvariant();
            return Path.Combine(Path.GetDirectoryName(BrowserConfig.FilePath), "worldsync-" + h + ".json");
        }

        private static Dictionary<string, long> LoadKnown()
        {
            try
            {
                string f = KnownPath();
                if (File.Exists(f))
                    return new Dictionary<string, long>(JsonConvert.DeserializeObject<Dictionary<string, long>>(File.ReadAllText(f)) ?? new Dictionary<string, long>(), StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly object KnownLock = new object();

        private static void MarkKnown(string id, long savedAt, bool remove = false)
        {
            lock (KnownLock)
            {
                var k = LoadKnown();
                if (remove) k.Remove(id); else k[id] = savedAt;
                try { File.WriteAllText(KnownPath(), JsonConvert.SerializeObject(k, Formatting.Indented)); } catch { }
            }
        }

        private static int DeleteRemote(string id)
        {
            var r = Req("DELETE", "/api/worlds/" + id);
            r.Headers["X-Uploader"] = Ascii(SafePlayer());
            try { using (var resp = (HttpWebResponse)r.GetResponse()) return (int)resp.StatusCode; }
            catch (WebException ex) when (ex.Response is HttpWebResponse hr) { using (hr) return (int)hr.StatusCode; }
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
            if (!Enabled) { Status = Cfg.WorldsBaseUrl.Length == 0 ? "Saves on this PC only (no save server configured)." : "Shared worlds disabled (group code too short)."; return; }
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
                if (v == null || string.IsNullOrEmpty(v.PlaythroughId)) continue;
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
        private static HttpWebRequest Req(string method, string path)
        {
            var r = (HttpWebRequest)WebRequest.Create(Cfg.WorldsBaseUrl + path);
            r.Method = method;
            r.Timeout = 60000;
            r.ReadWriteTimeout = 60000;
            r.UserAgent = "BAMPServerBrowser/1.1";
            r.Headers["X-Share-Key"] = Cfg.GroupKey;
            if (!string.IsNullOrWhiteSpace(Cfg.adminKey)) r.Headers["X-Admin-Key"] = Cfg.adminKey.Trim();
            return r;
        }

        private static Dictionary<string, RemoteWorld> FetchRemote()
        {
            var r = Req("GET", "/api/worlds");
            using (var resp = (HttpWebResponse)r.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream()))
            {
                var obj = JsonConvert.DeserializeAnonymousType(sr.ReadToEnd(), new { worlds = new List<RemoteWorld>(), isAdmin = false });
                IsAdmin = obj != null && obj.isAdmin;
                return (obj?.worlds ?? new List<RemoteWorld>()).Where(w => !string.IsNullOrEmpty(w.id)).ToDictionary(w => w.id, StringComparer.OrdinalIgnoreCase);
            }
        }

        private static int Upload(Local w)
        {
            byte[] data = Pack(w.Dir);
            var r = Req("PUT", "/api/worlds/" + w.Id);
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

        private static byte[] Download(string id)
        {
            var r = Req("GET", "/api/worlds/" + id);
            using (var resp = (HttpWebResponse)r.GetResponse())
            using (var ms = new MemoryStream())
            {
                resp.GetResponseStream().CopyTo(ms);
                return ms.ToArray();
            }
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

        // ------------------------------------------------------------------ actions
        private void UploadNewer(long onlyRecentSeconds)
        {
            List<Local> locals;
            try { locals = ScanLocal(); } catch (Exception ex) { Status = "Reading saves: " + ex.Message; return; }
            long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var todo = locals.Where(l => (onlyRecentSeconds <= 0 || nowUnix - l.SavedAt < onlyRecentSeconds)
                                         && (!_uploaded.TryGetValue(l.Id, out var u) || u < l.SavedAt)
                                         && (!_remote.TryGetValue(l.Id, out var rw) || rw.savedAt < l.SavedAt)
                                         && QuietFor(l.Dir, 5)).ToList();
            if (todo.Count == 0) return;
            Busy = true;
            Task.Run(() =>
            {
                var msgs = new List<string>();
                foreach (var w in todo)
                {
                    try
                    {
                        int st = Upload(w);
                        if (st == 200 || st == 409) { MarkKnown(w.Id, w.SavedAt); _post(() => { _uploaded[w.Id] = w.SavedAt; }); }
                        msgs.Add(st == 200 ? $"'{w.Name}' uploaded (day {w.Day})" : st == 409 ? $"'{w.Name}' already up to date" : $"'{w.Name}' refused (HTTP {st})");
                    }
                    catch (Exception ex) { msgs.Add($"'{w.Name}' : {ex.Message}"); }
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

        private void FullSync()
        {
            List<Local> locals;
            try { locals = ScanLocal(); } catch (Exception ex) { Status = "Reading saves: " + ex.Message; return; }
            string root = "";
            try { root = MPSaveManager.MpVersionFolder(); } catch { }
            Busy = true;
            Status = "Syncing worlds…";
            Task.Run(() =>
            {
                var msgs = new List<string>();
                Dictionary<string, RemoteWorld> remote;
                try { remote = FetchRemote(); }
                catch (Exception ex) { _post(() => { Busy = false; Status = "Save server unreachable: " + ex.Message; }); return; }

                var byId = locals.ToDictionary(l => l.Id, StringComparer.OrdinalIgnoreCase);
                var known = LoadKnown();
                int down = 0, up = 0, del = 0;
                foreach (var rw in remote.Values)
                {
                    byId.TryGetValue(rw.id, out var loc);
                    try
                    {
                        if (rw.deleted)
                        {
                            // supprimé par un membre : on ne le renvoie que s'il a été rejoué depuis
                            if (loc != null && loc.SavedAt > rw.deletedAt && QuietFor(loc.Dir, 5))
                            {
                                int st0 = Upload(loc);
                                if (st0 == 200) { up++; MarkKnown(loc.Id, loc.SavedAt); msgs.Add($"'{loc.Name}' restored on the server (played again after deletion)"); }
                            }
                            else MarkKnown(rw.id, 0, remove: true);
                            continue;
                        }
                        if (loc == null)
                        {
                            if (known.TryGetValue(rw.id, out long kv))
                            {
                                if (kv < 0)
                                {
                                    // retiré de ce PC par un membre : on ne le reprend que s'il a progressé depuis
                                    if (rw.savedAt <= -kv) continue;
                                }
                                else if (IsAdmin)
                                {
                                    int st1 = DeleteRemote(rw.id);
                                    if (st1 == 200 || st1 == 404) { del++; MarkKnown(rw.id, 0, remove: true); msgs.Add($"'{rw.name}' moved to the server trash (deleted on this PC, admin)"); }
                                    else msgs.Add($"'{rw.name}': deletion refused (HTTP {st1})");
                                    continue;
                                }
                                else
                                {
                                    // membre sans clé admin : suppression locale seulement, le monde reste pour le groupe
                                    MarkKnown(rw.id, -Math.Max(1, rw.savedAt));
                                    msgs.Add($"'{rw.name}' removed from this PC only (only the admin can delete it from the server)");
                                    continue;
                                }
                            }
                            string dir = root.Length > 0 ? Path.Combine(root, rw.id) : null;
                            if (dir == null) { msgs.Add("save folder not found"); continue; }
                            Unpack(Download(rw.id), dir);
                            down++; MarkKnown(rw.id, rw.savedAt);
                            msgs.Add($"'{rw.name}' downloaded (day {rw.worldDay}, by {rw.uploader})");
                        }
                        else if (loc.SavedAt < rw.savedAt)
                        {
                            Unpack(Download(rw.id), loc.Dir);
                            down++; MarkKnown(rw.id, rw.savedAt);
                            msgs.Add($"'{rw.name}' updated (day {rw.worldDay}, by {rw.uploader})");
                        }
                        else if (loc.SavedAt > rw.savedAt)
                        {
                            if (!QuietFor(loc.Dir, 5)) continue;
                            int st2 = Upload(loc);
                            if (st2 == 200) { up++; MarkKnown(loc.Id, loc.SavedAt); msgs.Add($"'{loc.Name}' uploaded (day {loc.Day})"); }
                            else if (st2 != 409) msgs.Add($"'{loc.Name}' refused (HTTP {st2})");
                        }
                        else MarkKnown(loc.Id, loc.SavedAt);
                    }
                    catch (Exception ex) { msgs.Add($"'{rw.name}' : {ex.Message}"); }
                }
                // mondes locaux absents du serveur
                foreach (var l in locals)
                {
                    if (remote.ContainsKey(l.Id) || !QuietFor(l.Dir, 5)) continue;
                    try
                    {
                        int st = Upload(l);
                        if (st == 200) { up++; MarkKnown(l.Id, l.SavedAt); msgs.Add($"'{l.Name}' uploaded (day {l.Day})"); }
                        else if (st != 409) msgs.Add($"'{l.Name}' refused (HTTP {st})");
                    }
                    catch (Exception ex) { msgs.Add($"'{l.Name}' : {ex.Message}"); }
                }
                Dictionary<string, RemoteWorld> after = remote;
                try { after = FetchRemote(); } catch { }
                _post(() =>
                {
                    Busy = false;
                    _remote = after;
                    foreach (var l in locals) if (after.TryGetValue(l.Id, out var r2) && !r2.deleted && r2.savedAt >= l.SavedAt) _uploaded[l.Id] = l.SavedAt;
                    Status = (IsAdmin ? "[admin] " : "") + $"Shared worlds up to date ({after.Values.Count(w => !w.deleted)} on the server" + (down > 0 ? $", {down} downloaded" : "") + (up > 0 ? $", {up} uploaded" : "") + (del > 0 ? $", {del} deleted" : "") + ").";
                    if (msgs.Count > 0) ModEntry.Info("[WorldSync] " + string.Join(" | ", msgs));
                });
            });
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

        private static void Unpack(byte[] blob, string dir)
        {
            Bundle b;
            using (var ms = new MemoryStream(blob))
            using (var gz = new GZipStream(ms, CompressionMode.Decompress))
            using (var sr = new StreamReader(gz, Encoding.UTF8))
                b = JsonConvert.DeserializeObject<Bundle>(sr.ReadToEnd());
            if (b?.files == null || b.format != "bamp-world-1" || b.files.Count == 0) throw new InvalidDataException("invalid world archive");
            if (!b.files.Any(f => f.p.EndsWith("manifest.bamp.json", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("archive without manifest");

            string target = Path.GetFullPath(dir).TrimEnd('\\', '/');
            string staging = target + ".sync-tmp";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            foreach (var f in b.files)
            {
                string rel = (f.p ?? "").Replace('/', Path.DirectorySeparatorChar);
                string path = Path.GetFullPath(Path.Combine(staging, rel));
                if (!path.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("forbidden path in archive: " + f.p);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, Convert.FromBase64String(f.d ?? ""));
                if (f.t > 0) File.SetLastWriteTimeUtc(path, new DateTime(f.t, DateTimeKind.Utc));
            }
            // l'ancienne copie locale est gardée en sauvegarde, hors du dossier lu par le jeu
            if (Directory.Exists(target))
            {
                string backupRoot = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(target)), "_BAMPServerBrowser_backups");
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
