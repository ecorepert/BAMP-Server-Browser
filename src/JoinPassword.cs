using System;
using System.Security.Cryptography;
using System.Text;
using BigAmbitionsMP;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace BAMPServerBrowser
{
    /// <summary>
    /// Mot de passe de connexion optionnel (vide par défaut = partie ouverte).
    /// Les fichiers du mod multijoueur ne sont pas modifiés : on s'accroche en mémoire (Harmony)
    ///  - côté client, à l'envoi du message Hello, pour y ajouter l'empreinte du mot de passe ;
    ///  - côté hôte, à la réception du Hello, pour refuser la connexion si elle ne correspond pas.
    /// </summary>
    public static class JoinPassword
    {
        public const string RefuseTag = "BAMP:password";
        private const string Field = "bsbPw";
        private const string ModField = "bsbV";

        /// <summary>Mot de passe à envoyer lors de la prochaine connexion (saisi dans la liste).</summary>
        public static volatile string Pending = "";
        /// <summary>Vrai si l'hôte vient de refuser notre connexion pour mot de passe incorrect.</summary>
        public static volatile bool LastRefused;

        private static Harmony _h;

        public static bool HostHasPassword => !string.IsNullOrEmpty(BrowserConfig.Current.joinPassword);

        public static string Hash(string pw)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("bsb-join:" + (pw ?? "")))).Replace("-", "").ToLowerInvariant();
        }

        public static void Install()
        {
            try
            {
                _h = new Harmony("bampserverbrowser.joinpassword");
                var hello = AccessTools.Method(typeof(MPServer), "HandleHello");
                var send = AccessTools.Method(typeof(MPClient), "Send", new[] { typeof(MessageEnvelope) });
                var disc = AccessTools.Method(typeof(MPClient), "OnDisconnected");
                if (hello == null || send == null || disc == null)
                {
                    ModEntry.Warn("[Password] incompatible multiplayer mod (methods not found) - join password disabled.");
                    return;
                }
                _h.Patch(hello, prefix: new HarmonyMethod(typeof(JoinPassword), nameof(HelloPrefix)));
                _h.Patch(send, prefix: new HarmonyMethod(typeof(JoinPassword), nameof(SendPrefix)));
                _h.Patch(disc, prefix: new HarmonyMethod(typeof(JoinPassword), nameof(DisconnectPrefix)));
                ModEntry.Info("[Password] join password check installed.");
            }
            catch (Exception ex) { ModEntry.Warn("[Password] install: " + ex.Message); }
        }

        public static void Uninstall()
        {
            try { _h?.UnpatchAll(_h.Id); } catch { }
        }

        // ---------------------------------------------------------------- hôte
        private static bool HelloPrefix(MPLink peer, MessageEnvelope env)
        {
            try
            {
                if (env == null) return true;
                JObject o;
                try { o = JObject.Parse(env.Data ?? "{}"); } catch { o = new JObject(); }
                string sent = (string)o[Field] ?? "";
                string who = (string)o["PlayerId"] ?? "";
                bool hasMod = o[ModField] != null || sent.Length > 0;
                string pw = BrowserConfig.Current.joinPassword ?? "";
                string apw = BrowserConfig.Current.adminPassword ?? "";

                if (apw.Length > 0 && sent.Length > 0 && string.Equals(sent, Hash(apw), StringComparison.Ordinal))
                {
                    AdminChannel.OnHostAccepted(who, hasMod, AdminChannel.Role.Admin, verified: true, adminRefused: false);
                    return true;
                }
                if (pw.Length == 0)
                {
                    // partie ouverte : un mot de passe faux (tentative admin) laisse entrer comme simple joueur
                    AdminChannel.OnHostAccepted(who, hasMod, AdminChannel.Role.Player, verified: false, adminRefused: sent.Length > 0);
                    return true;
                }
                if (sent.Length > 0 && string.Equals(sent, Hash(pw), StringComparison.Ordinal))
                {
                    AdminChannel.OnHostAccepted(who, hasMod, AdminChannel.Role.Player, verified: true, adminRefused: false);
                    return true;
                }

                ModEntry.Info($"[Password] connection from '{(who.Length > 0 ? who : "?")}' refused: password {(sent.Length == 0 ? "missing" : "wrong")}.");
                try { peer.Disconnect(Encoding.UTF8.GetBytes(RefuseTag)); } catch { }
                return false; // le Hello n'est pas traité par le mod multijoueur
            }
            catch (Exception ex)
            {
                ModEntry.Warn("[Password] check: " + ex.Message);
                return true;
            }
        }

        // ---------------------------------------------------------------- client
        private static void SendPrefix(MessageEnvelope env)
        {
            try
            {
                if (env == null || env.Type != MessageType.Hello) return;
                AdminChannel.ResetClient();
                string pw = Pending;
                var o = JObject.Parse(env.Data ?? "{}");
                o[ModField] = 1; // signale à l'hôte que ce joueur a ce mod (messages admin / sauvegardes)
                if (!string.IsNullOrEmpty(pw)) o[Field] = Hash(pw);
                env.Data = o.ToString(Newtonsoft.Json.Formatting.None);
                LastRefused = false;
            }
            catch (Exception ex) { ModEntry.Warn("[Password] send: " + ex.Message); }
        }

        private static void DisconnectPrefix(string reason, byte[] extra)
        {
            try
            {
                AdminChannel.ResetClient();
                if (extra != null && extra.Length > 0 && Encoding.UTF8.GetString(extra) == RefuseTag)
                {
                    LastRefused = true;
                    ModEntry.Info("[Password] the host refused the connection: wrong password.");
                }
            }
            catch { }
        }
    }
}
