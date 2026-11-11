using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace BAMPServerBrowser
{
    public class ServerEntry
    {
        public string id = "";
        public string name = "";
        public string hostName = "";
        public string description = "";
        public string ip = "";
        public int port;
        public string steamId = "";
        public int players;
        public int maxPlayers;
        public bool inLobby;
        public string modVersion = "";
        public string gameVersion = "";
        public int age;
        public bool hasPassword;
        public int day;
        public string language = "";
        /// <summary>false = partie vue récemment mais hors ligne (anciens masters : toujours true).</summary>
        public bool online = true;
        /// <summary>Secondes depuis la dernière fois que la partie était en ligne (si hors ligne).</summary>
        public int offlineFor;
    }

    public class ServerList
    {
        public List<ServerEntry> servers = new List<ServerEntry>();
    }

    public class RegisterReply
    {
        public string id = "";
        public string token = "";
        public int ttl;
        public string ip = "";
    }

    public class HostInfo
    {
        public string name;
        public string hostName;
        public string description;
        public int port;
        public string steamId;
        public int players;
        public int maxPlayers;
        public bool inLobby;
        public string modVersion;
        public string gameVersion;
        public bool hasPassword;
        public int day;
        public string language;
    }

    /// <summary>Appels HTTP vers le master server (thread de fond, résultat via callback).</summary>
    public static class MasterClient
    {
        static MasterClient()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

        private static string Base => (BrowserConfig.Current.masterUrl ?? "").TrimEnd('/');

        /// <returns>(status HTTP, corps) ; status 0 = erreur réseau.</returns>
        public static (int status, string body) Send(string method, string path, string json, string token = null)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(Base + path);
                req.Method = method;
                req.Timeout = 6000;
                req.ReadWriteTimeout = 6000;
                req.UserAgent = "BAMPServerBrowser/1.0";
                req.Accept = "application/json";
                if (token != null) req.Headers["X-Token"] = token;
                if (json != null)
                {
                    byte[] data = Encoding.UTF8.GetBytes(json);
                    req.ContentType = "application/json";
                    req.ContentLength = data.Length;
                    using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                }
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream()))
                    return ((int)resp.StatusCode, r.ReadToEnd());
            }
            catch (WebException wex) when (wex.Response is HttpWebResponse hr)
            {
                try
                {
                    using (hr)
                    using (var r = new StreamReader(hr.GetResponseStream()))
                        return ((int)hr.StatusCode, r.ReadToEnd());
                }
                catch { return ((int)hr.StatusCode, ""); }
            }
            catch (Exception ex)
            {
                return (0, ex.Message);
            }
        }

        public static Task<(List<ServerEntry> list, string error)> FetchAsync()
        {
            return Task.Run(() =>
            {
                var (st, body) = Send("GET", "/api/servers?offline=1", null);
                if (st != 200) return ((List<ServerEntry>)null, st == 0 ? body : "HTTP " + st);
                try { return (JsonConvert.DeserializeObject<ServerList>(body)?.servers ?? new List<ServerEntry>(), (string)null); }
                catch (Exception ex) { return ((List<ServerEntry>)null, ex.Message); }
            });
        }

        public static Task<(RegisterReply reply, string error)> RegisterAsync(HostInfo info)
        {
            string json = JsonConvert.SerializeObject(info);
            return Task.Run(() =>
            {
                var (st, body) = Send("POST", "/api/servers", json);
                if (st != 201) return ((RegisterReply)null, st == 0 ? body : "HTTP " + st + " " + body);
                try { return (JsonConvert.DeserializeObject<RegisterReply>(body), (string)null); }
                catch (Exception ex) { return ((RegisterReply)null, ex.Message); }
            });
        }

        /// <returns>status HTTP (404 = inconnu, il faut se ré-enregistrer).</returns>
        public static Task<int> HeartbeatAsync(string id, string token, HostInfo info)
        {
            string json = JsonConvert.SerializeObject(info);
            return Task.Run(() => Send("POST", "/api/servers/" + id, json, token).status);
        }

        public static void Unregister(string id, string token)
        {
            Send("DELETE", "/api/servers/" + id, null, token);
        }
    }
}
