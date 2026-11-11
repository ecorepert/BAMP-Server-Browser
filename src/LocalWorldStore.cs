using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace BAMPServerBrowser
{
    /// <summary>
    /// Petit serveur de sauvegardes intégré au jeu, sur le PC de celui qui héberge.
    /// Même API que la partie "mondes" de master.py :
    ///   GET /api/worlds, GET|PUT|DELETE /api/worlds/&lt;id&gt; (header X-Share-Key ; X-Admin-Key pour supprimer).
    /// Ne tourne que pendant que le jeu est lancé. Les données sont dans
    /// %LocalLow%\Hovgaard Games\Big Ambitions\BAMPServerBrowser\store\.
    /// </summary>
    public static class LocalWorldStore
    {
        private const int MaxWorldBytes = 64 * 1024 * 1024;
        private const int MaxWorldsPerGroup = 20;
        private const int KeepVersions = 10;
        private const int RatePerMin = 90;
        private const int MaxConcurrent = 16;
        /// <summary>Place disque maximale de toutes les sauvegardes stockées sur ce PC.</summary>
        private const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;
        private static readonly Regex IdRe = new Regex("^[0-9a-fA-F]{8,64}$");

        private static TcpListener _listener;
        private static Thread _thread;
        private static volatile bool _running;
        private static int _port;
        private static readonly object FileLock = new object();
        private static readonly Dictionary<string, List<DateTime>> Rate = new Dictionary<string, List<DateTime>>();
        private static int _active;

        public static bool Running => _running;
        public static int Port => _port;
        public static string LastError = "";

        private static string _root = "";
        /// <summary>Seul groupe accepté : celui du propriétaire de ce PC (tout autre code est refusé).</summary>
        private static volatile string _allowedGroupDir = "";
        public static string GroupKeyInUse = "";
        private static string Root => _root;

        public static void Start(int port, string groupKey)
        {
            if (_running && _port == port && GroupKeyInUse == (groupKey ?? "")) return;
            Stop();
            try
            {
                _root = Path.Combine(Application.persistentDataPath, "BAMPServerBrowser", "store"); // thread principal
                GroupKeyInUse = groupKey ?? "";
                _allowedGroupDir = GroupDir(GroupKeyInUse) ?? "";
                if (_allowedGroupDir.Length == 0) throw new InvalidOperationException("group code too short");
                Directory.CreateDirectory(Root);
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start(32);
                _port = port;
                _running = true;
                LastError = "";
                _thread = new Thread(Loop) { IsBackground = true, Name = "BAMP-WorldStore" };
                _thread.Start();
                ModEntry.Info($"[Store] local save server running on port {port}.");
            }
            catch (Exception ex)
            {
                _running = false;
                LastError = ex.Message;
                ModEntry.Warn($"[Store] cannot open port {port}: {ex.Message}");
            }
        }

        public static void Stop()
        {
            if (!_running && _listener == null) return;
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
            ModEntry.Info("[Store] local save server stopped.");
        }

        private static void Loop()
        {
            while (_running)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch { if (!_running) return; Thread.Sleep(100); continue; }
                if (Interlocked.Increment(ref _active) > MaxConcurrent)
                {
                    Interlocked.Decrement(ref _active);
                    try { c.Close(); } catch { }
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Handle(c); }
                    catch { }
                    finally
                    {
                        Interlocked.Decrement(ref _active);
                        try { c.Close(); } catch { }
                    }
                });
            }
        }

        // ------------------------------------------------------------------ HTTP minimal
        private class Req
        {
            public string Method, Path, Ip;
            public Dictionary<string, string> H = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public byte[] Body = new byte[0];
            public string Hd(string k) => H.TryGetValue(k, out var v) ? v.Trim() : "";
        }

        private static void Handle(TcpClient c)
        {
            c.ReceiveTimeout = 10000;
            c.SendTimeout = 10000;
            var s = c.GetStream();
            string ip = ((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString();
            var head = ReadHead(s);
            if (head == null) return;
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length < 3) { Send(s, 400, "{\"error\":\"bad request\"}"); return; }
            var r = new Req { Method = first[0].ToUpperInvariant(), Path = first[1], Ip = ip };
            if (r.Path.Length > 256) { Send(s, 414, "{\"error\":\"uri too long\"}"); return; }
            foreach (var l in lines.Skip(1))
            {
                int i = l.IndexOf(':');
                if (i > 0) r.H[l.Substring(0, i).Trim()] = l.Substring(i + 1).Trim();
            }
            if (!RateOk(ip)) { Send(s, 429, "{\"error\":\"too many requests\"}"); return; }
            if (int.TryParse(r.Hd("Content-Length"), out int len) && len > 0)
            {
                if (len > MaxWorldBytes) { Send(s, 413, "{\"error\":\"bad size\"}"); return; }
                r.Body = ReadExact(s, len);
                if (r.Body == null) return;
            }
            try { Route(s, r); }
            catch (Exception ex) { ModEntry.Warn("[Store] " + ex.Message); Send(s, 500, "{\"error\":\"server error\"}"); }
        }

        private static string ReadHead(NetworkStream s)
        {
            var buf = new List<byte>(1024);
            int b, n = 0;
            while ((b = s.ReadByte()) >= 0)
            {
                buf.Add((byte)b);
                if (++n > 8192) return null;
                int c = buf.Count;
                if (c >= 4 && buf[c - 4] == 13 && buf[c - 3] == 10 && buf[c - 2] == 13 && buf[c - 1] == 10)
                    return Encoding.ASCII.GetString(buf.ToArray(), 0, c - 4);
            }
            return null;
        }

        private static byte[] ReadExact(NetworkStream s, int len)
        {
            var data = new byte[len];
            int off = 0;
            while (off < len)
            {
                int k = s.Read(data, off, len - off);
                if (k <= 0) return null;
                off += k;
            }
            return data;
        }

        private static void Send(NetworkStream s, int code, string json, byte[] raw = null, string extraHeaders = "")
        {
            byte[] body = raw ?? Encoding.UTF8.GetBytes(json ?? "");
            string ct = raw != null ? "application/octet-stream" : "application/json; charset=utf-8";
            string h = $"HTTP/1.1 {code} {(code < 400 ? "OK" : "Error")}\r\nServer: BAMPStore\r\nContent-Type: {ct}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\n{extraHeaders}Connection: close\r\n\r\n";
            var hb = Encoding.ASCII.GetBytes(h);
            s.Write(hb, 0, hb.Length);
            s.Write(body, 0, body.Length);
        }

        private static bool RateOk(string ip)
        {
            if (ip == "127.0.0.1" || ip == "::1") return true;
            lock (Rate)
            {
                var now = DateTime.UtcNow;
                if (!Rate.TryGetValue(ip, out var l)) Rate[ip] = l = new List<DateTime>();
                l.RemoveAll(t => (now - t).TotalSeconds > 60);
                if (l.Count >= RatePerMin) return false;
                l.Add(now);
                if (Rate.Count > 2000) foreach (var k in Rate.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) Rate.Remove(k);
                return true;
            }
        }

        // ------------------------------------------------------------------ logique (identique à master.py)
        private static string Sha(string v)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(v))).Replace("-", "").ToLowerInvariant();
        }

        private static long StoreBytes()
        {
            try { return new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
            catch { return 0; }
        }

        private static string GroupDir(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length < 6 || key.Length > 128) return null;
            return Path.Combine(Root, Sha(key).Substring(0, 32));
        }

        private static JObject ReadMeta(string dir)
        {
            try { var f = Path.Combine(dir, "meta.json"); return File.Exists(f) ? JObject.Parse(File.ReadAllText(f)) : null; }
            catch { return null; }
        }

        private static (bool has, bool isAdmin) AdminState(string g, string adminKey)
        {
            string f = Path.Combine(g, "group.json");
            string stored = "";
            try { if (File.Exists(f)) stored = (string)JObject.Parse(File.ReadAllText(f))["adminHash"] ?? ""; } catch { }
            if (stored.Length == 0)
            {
                if (!string.IsNullOrEmpty(adminKey) && adminKey.Length >= 8 && adminKey.Length <= 128)
                {
                    lock (FileLock)
                    {
                        Directory.CreateDirectory(g);
                        if (!File.Exists(f))
                            File.WriteAllText(f, new JObject { ["adminHash"] = Sha("bamp-admin:" + adminKey), ["claimedAt"] = Now() }.ToString());
                    }
                    return (true, true);
                }
                return (false, false);
            }
            return (true, !string.IsNullOrEmpty(adminKey) && stored == Sha("bamp-admin:" + adminKey));
        }

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private static string Clean(string v, int n)
        {
            var sb = new StringBuilder();
            foreach (char ch in v ?? "") if (!char.IsControl(ch) && "<>\"'`\\".IndexOf(ch) < 0) sb.Append(ch);
            var r = sb.ToString().Trim();
            return r.Length > n ? r.Substring(0, n) : r;
        }

        private static void Route(NetworkStream s, Req r)
        {
            string path = r.Path.Split('?')[0].TrimEnd('/');
            if (path == "/health") { Send(s, 200, "{\"ok\":true}"); return; }
            var parts = path.Trim('/').Split('/');
            if (parts.Length < 2 || parts[0] != "api" || parts[1] != "worlds" || parts.Length > 3) { Send(s, 404, "{\"error\":\"not found\"}"); return; }
            string wid = parts.Length == 3 ? parts[2] : "";
            string g = GroupDir(r.Hd("X-Share-Key"));
            if (g == null) { Send(s, 403, "{\"error\":\"share key required (>= 6 chars)\"}"); return; }
            // ce PC ne stocke que les sauvegardes de son propre groupe
            string allowed = _allowedGroupDir;
            if (allowed.Length == 0 || !string.Equals(g, allowed, StringComparison.OrdinalIgnoreCase)) { Send(s, 403, "{\"error\":\"unknown group\"}"); return; }
            if (wid.Length > 0 && !IdRe.IsMatch(wid)) { Send(s, 400, "{\"error\":\"bad id\"}"); return; }
            string d = wid.Length > 0 ? Path.Combine(g, wid) : null;

            if (r.Method == "GET" && wid.Length == 0)
            {
                var (has, isAdmin) = AdminState(g, r.Hd("X-Admin-Key"));
                var list = new JArray();
                if (Directory.Exists(g))
                    foreach (var dir in Directory.GetDirectories(g))
                    {
                        var m = ReadMeta(dir);
                        if (m == null) continue;
                        if ((bool?)m["deleted"] == true && Now() - ((long?)m["deletedAt"] ?? Now()) > 30 * 86400)
                        { lock (FileLock) { try { Directory.Delete(dir, true); } catch { } } continue; }
                        list.Add(m);
                    }
                var sorted = new JArray(list.OrderByDescending(m => (long?)m["savedAt"] ?? 0));
                Send(s, 200, new JObject { ["worlds"] = sorted, ["hasAdmin"] = has, ["isAdmin"] = isAdmin }.ToString(Formatting.None));
                return;
            }
            if (wid.Length == 0) { Send(s, 405, "{\"error\":\"method not allowed\"}"); return; }

            if (r.Method == "GET")
            {
                var m = ReadMeta(d);
                if (m != null && (bool?)m["deleted"] == true) { Send(s, 410, "{\"error\":\"world deleted\"}"); return; }
                string f = Path.Combine(d, "current.bin");
                if (!File.Exists(f)) { Send(s, 404, "{\"error\":\"unknown world\"}"); return; }
                Send(s, 200, null, File.ReadAllBytes(f), $"X-Saved-At: {(long?)m?["savedAt"] ?? 0}\r\n");
                return;
            }
            if (r.Method == "PUT")
            {
                long.TryParse(r.Hd("X-Saved-At"), out long savedAt);
                if (r.Body.Length < 2 || r.Body[0] != 0x1f || r.Body[1] != 0x8b) { Send(s, 400, "{\"error\":\"expected gzip body\"}"); return; }
                lock (FileLock)
                {
                    Directory.CreateDirectory(g);
                    var old = ReadMeta(d);
                    bool oldDeleted = old != null && (bool?)old["deleted"] == true;
                    if (oldDeleted && savedAt <= ((long?)old["deletedAt"] ?? 0)) { Send(s, 410, "{\"error\":\"world deleted\"}"); return; }
                    if (old != null && !oldDeleted && ((long?)old["savedAt"] ?? 0) >= savedAt) { Send(s, 409, "{\"error\":\"older or same version\"}"); return; }
                    if (old == null && Directory.GetDirectories(g).Length >= MaxWorldsPerGroup) { Send(s, 507, "{\"error\":\"too many worlds in this group\"}"); return; }
                    if (StoreBytes() + r.Body.Length > MaxTotalBytes) { Send(s, 507, "{\"error\":\"storage full on this PC\"}"); return; }
                    Directory.CreateDirectory(d);
                    string cur = Path.Combine(d, "current.bin");
                    if (File.Exists(cur) && old != null)
                    {
                        string v = Path.Combine(d, "v" + ((long?)old["savedAt"] ?? 0) + ".bin");
                        if (File.Exists(v)) File.Delete(v);
                        File.Move(cur, v);
                        foreach (var x in Directory.GetFiles(d, "v*.bin").OrderBy(x => x).Reverse().Skip(KeepVersions)) File.Delete(x);
                    }
                    File.WriteAllBytes(cur + ".tmp", r.Body);
                    if (File.Exists(cur)) File.Delete(cur);
                    File.Move(cur + ".tmp", cur);
                    int.TryParse(r.Hd("X-World-Day"), out int day);
                    var meta = new JObject
                    {
                        ["id"] = wid, ["savedAt"] = savedAt, ["worldDay"] = day,
                        ["name"] = Clean(r.Hd("X-World-Name"), 64), ["players"] = Clean(r.Hd("X-Players"), 200),
                        ["uploader"] = Clean(r.Hd("X-Uploader"), 32), ["size"] = r.Body.Length, ["uploadedAt"] = Now()
                    };
                    File.WriteAllText(Path.Combine(d, "meta.json"), meta.ToString(Formatting.None));
                }
                Send(s, 200, "{\"ok\":true,\"savedAt\":" + savedAt + "}");
                return;
            }
            if (r.Method == "DELETE")
            {
                var (_, isAdmin) = AdminState(g, r.Hd("X-Admin-Key"));
                if (!isAdmin) { Send(s, 403, "{\"error\":\"admin key required to delete\"}"); return; }
                lock (FileLock)
                {
                    var m = ReadMeta(d);
                    if (m == null) { Send(s, 404, "{\"error\":\"unknown world\"}"); return; }
                    if ((bool?)m["deleted"] != true)
                    {
                        string cur = Path.Combine(d, "current.bin");
                        if (File.Exists(cur)) { string v = Path.Combine(d, "v" + ((long?)m["savedAt"] ?? 0) + ".bin"); if (File.Exists(v)) File.Delete(v); File.Move(cur, v); }
                        m["deleted"] = true; m["deletedAt"] = Now(); m["deletedBy"] = Clean(r.Hd("X-Uploader"), 32);
                        File.WriteAllText(Path.Combine(d, "meta.json"), m.ToString(Formatting.None));
                    }
                }
                Send(s, 200, "{\"ok\":true}");
                return;
            }
            Send(s, 405, "{\"error\":\"method not allowed\"}");
        }
    }
}
