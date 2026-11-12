using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using BigAmbitionsMP;
using HarmonyLib;

namespace BAMPServerBrowser
{
    /// <summary>
    /// "Host New Game" du mod multijoueur : avant d'ouvrir le salon, on demande où ranger les
    /// sauvegardes partagées (ce PC par défaut, ou un serveur à soi). Le serveur dédié n'est jamais interrompu.
    /// </summary>
    public static class SaveServerPrompt
    {
        private static Harmony _h;
        private static bool _bypass;
        private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly MethodInfo OnMpHostNewM = typeof(MPCanvasUI).GetMethod("OnMpHostNew", F);

        /// <summary>Demandé par le clic sur "Host New Game" (traité par ServerBrowser qui affiche la fenêtre).</summary>
        public static Action<MPCanvasUI> OnAsk;

        public static void Install()
        {
            try
            {
                if (OnMpHostNewM == null) { ModEntry.Warn("[SaveServer] 'Host New Game' not found - no save server question."); return; }
                _h = new Harmony("bampserverbrowser.saveserverprompt");
                _h.Patch(OnMpHostNewM, prefix: new HarmonyMethod(typeof(SaveServerPrompt), nameof(HostNewPrefix)));
                ModEntry.Info("[SaveServer] save server question installed on 'Host New Game'.");
            }
            catch (Exception ex) { ModEntry.Warn("[SaveServer] install: " + ex.Message); }
        }

        public static void Uninstall() { try { _h?.UnpatchAll(_h.Id); } catch { } }

        private static bool HostNewPrefix(MPCanvasUI __instance)
        {
            if (_bypass || DedicatedHost.Requested || !BrowserConfig.Current.askSaveServerOnNewGame || OnAsk == null) return true;
            try { OnAsk(__instance); return false; }
            catch (Exception ex) { ModEntry.Warn("[SaveServer] " + ex.Message); return true; }
        }

        /// <summary>Lance vraiment "Host New Game" une fois le choix fait.</summary>
        public static void ContinueHostNew(MPCanvasUI ui)
        {
            if (ui == null || OnMpHostNewM == null) return;
            _bypass = true;
            try { OnMpHostNewM.Invoke(ui, null); }
            catch (Exception ex) { ModEntry.Warn("[SaveServer] host new game: " + (ex.InnerException ?? ex).Message); }
            finally { _bypass = false; }
        }

        /// <summary>
        /// Écrit master.py et install.sh (embarqués dans le mod) dans un dossier de ce PC, en fins de ligne Linux,
        /// pour que la commande d'installation les envoie au VPS avec scp. Renvoie le dossier ("" en cas d'échec).
        /// À appeler sur le thread principal (dossier de données du jeu).
        /// </summary>
        public static string ExtractVpsFiles()
        {
            try
            {
                string dir = Path.Combine(UnityEngine.Application.persistentDataPath, "BAMPServerBrowser", "vps-install");
                Directory.CreateDirectory(dir);
                var asm = typeof(SaveServerPrompt).Assembly;
                foreach (var name in new[] { "master.py", "install.sh" })
                {
                    using (var st = asm.GetManifestResourceStream("vps/" + name))
                    {
                        if (st == null) { ModEntry.Warn("[SaveServer] embedded file missing: " + name); return ""; }
                        string text;
                        using (var sr = new StreamReader(st, System.Text.Encoding.UTF8)) text = sr.ReadToEnd();
                        text = text.Replace("\r\n", "\n"); // le shell Linux refuse les fins de ligne Windows
                        File.WriteAllBytes(Path.Combine(dir, name), new System.Text.UTF8Encoding(false).GetBytes(text));
                    }
                }
                return Path.GetFullPath(dir);
            }
            catch (Exception ex) { ModEntry.Warn("[SaveServer] cannot prepare the VPS files: " + ex.Message); return ""; }
        }

        /// <summary>
        /// Vérifie qu'une adresse est un serveur de sauvegardes BAMP utilisable avec notre code de groupe.
        /// Renvoie (ok, message à afficher).
        /// </summary>
        public static Task<(bool ok, string msg)> TestAsync(string address)
        {
            string url = BrowserConfig.NormalizeUrl(address);
            string key = BrowserConfig.Current.GroupKey;
            return Task.Run(() =>
            {
                if (url.Length == 0) return (false, "Enter the address of your server, for example 203.0.113.5:8787.");
                try
                {
                    try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                    var r = (HttpWebRequest)WebRequest.Create(url + "/api/worlds");
                    r.Timeout = 6000; r.ReadWriteTimeout = 6000; r.UserAgent = BuildInfo.UserAgent; r.AllowAutoRedirect = false;
                    r.Headers["X-Share-Key"] = key;
                    try
                    {
                        using (var resp = (HttpWebResponse)r.GetResponse())
                        using (var sr = new StreamReader(resp.GetResponseStream()))
                        {
                            string body = sr.ReadToEnd();
                            return body.Contains("\"worlds\"") ? (true, "OK: this server stores saves.") : (false, "This address answers, but it is not a BAMP save server.");
                        }
                    }
                    catch (WebException ex) when (ex.Response is HttpWebResponse hr)
                    {
                        using (hr)
                        {
                            string body = "";
                            try { using (var sr = new StreamReader(hr.GetResponseStream())) body = sr.ReadToEnd(); } catch { }
                            if ((int)hr.StatusCode == 404 && body.Contains("disabled")) return (false, "This server only lists games: save storage is turned off on it (BAMP_WORLDS=0).");
                            if ((int)hr.StatusCode == 403) return (false, "The server refused the group code.");
                            return (false, "The server answered HTTP " + (int)hr.StatusCode + ".");
                        }
                    }
                }
                catch (Exception ex) { return (false, "Server unreachable: " + ex.Message); }
            });
        }
    }
}
