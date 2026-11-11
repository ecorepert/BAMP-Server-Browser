using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace BAMPServerBrowser
{
    public class BrowserConfig
    {
        public string masterUrl = "http://2.24.57.221:8787";
        public bool publish = true;
        public string serverName = "";
        public string description = "";
        public int maxPlayers = 8;
        public string toggleKey = "F9";
        public bool preferSteam = true;
        /// <summary>Code de groupe des mondes partagés (>= 6 caractères, identique pour tous les amis). Vide = désactivé.</summary>
        [JsonIgnore] public string shareKey = "";
        [JsonProperty("shareKey")] private string ShareKeyStored { get => SecretBox.Protect(shareKey); set => shareKey = SecretBox.Unprotect(value); }
        /// <summary>Mot de passe pour rejoindre la partie que l'on héberge. Vide (défaut) = partie ouverte.</summary>
        [JsonIgnore] public string joinPassword = "";
        [JsonProperty("joinPassword")] private string JoinPasswordStored { get => SecretBox.Protect(joinPassword); set => joinPassword = SecretBox.Unprotect(value); }
        /// <summary>Mot de passe admin : qui rejoint avec peut changer les réglages de la partie et supprimer des sauvegardes partagées.</summary>
        [JsonIgnore] public string adminPassword = "";
        [JsonProperty("adminPassword")] private string AdminPasswordStored { get => SecretBox.Protect(adminPassword); set => adminPassword = SecretBox.Unprotect(value); }
        /// <summary>Clé admin du groupe (uniquement sur le PC de l'admin) : seule elle peut supprimer des mondes du serveur.</summary>
        [JsonIgnore] public string adminKey = "";
        [JsonProperty("adminKey")] private string AdminKeyStored { get => SecretBox.Protect(adminKey); set => adminKey = SecretBox.Unprotect(value); }
        /// <summary>
        /// Serveur de sauvegarde externe (VPS) choisi par l'admin, ex. "http://1.2.3.4:8787".
        /// Vide (défaut) = le PC de l'hôte sert lui-même les sauvegardes (port localStoragePort),
        /// joignable par les amis avec son IP publique.
        /// </summary>
        public string worldsUrl = "";
        /// <summary>Port du serveur de sauvegarde intégré au jeu (mode local). À ouvrir (TCP) sur la box.</summary>
        public int localStoragePort = 8788;
        /// <summary>
        /// Mondes reçus en rejoignant la partie d'un ami (id du monde -> "code@hote:port" de cet ami).
        /// Ils ne se synchronisent qu'avec le serveur de cet ami ; tous les autres mondes restent dans notre groupe.
        /// </summary>
        [JsonIgnore] public System.Collections.Generic.Dictionary<string, string> joinedWorlds = new System.Collections.Generic.Dictionary<string, string>();
        [JsonProperty("joinedWorlds", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        private System.Collections.Generic.Dictionary<string, string> JoinedWorldsStored
        {
            get
            {
                var d = new System.Collections.Generic.Dictionary<string, string>();
                foreach (var kv in joinedWorlds ?? new System.Collections.Generic.Dictionary<string, string>()) d[kv.Key] = SecretBox.Protect(kv.Value);
                return d;
            }
            set
            {
                joinedWorlds = new System.Collections.Generic.Dictionary<string, string>();
                foreach (var kv in value ?? new System.Collections.Generic.Dictionary<string, string>()) joinedWorlds[kv.Key] = SecretBox.Unprotect(kv.Value);
            }
        }
        /// <summary>Parties favorites (clé "steam:id" ou "ip:adresse:port").</summary>
        public System.Collections.Generic.List<string> favorites = new System.Collections.Generic.List<string>();
        /// <summary>Notification quand une partie favorite passe en ligne.</summary>
        public bool notifyFavorites = true;
        /// <summary>Masquer les parties protégées par mot de passe dans la liste.</summary>
        public bool listHidePassword;
        /// <summary>N'afficher que les favoris.</summary>
        public bool listFavoritesOnly;
        /// <summary>Demander où ranger les sauvegardes partagées à chaque "Host New Game".</summary>
        public bool askSaveServerOnNewGame = true;
        /// <summary>Serveurs de sauvegarde déjà utilisés (le plus récent d'abord), pour pré-remplir la question.</summary>
        public System.Collections.Generic.List<string> saveServerHistory = new System.Collections.Generic.List<string>();
        /// <summary>Vérifier sur GitHub si une nouvelle version du mod existe.</summary>
        public bool checkUpdates = true;
        /// <summary>Langue parlée sur la partie hébergée (code court, ex. "FR"). Vide = déduite de la langue du système.</summary>
        public string language = "";
        /// <summary>Préférences de la liste des parties.</summary>
        public bool listOnlineOnly;
        public int listSort;
        public bool listSortDesc = true;
        public string listLanguage = "";

        /// <summary>Langues proposées dans le panneau d'hébergement.</summary>
        [JsonIgnore]
        public static readonly string[] Languages =
            { "EN", "FR", "DE", "ES", "IT", "PT", "NL", "PL", "RU", "UK", "TR", "CS", "SV", "DA", "NO", "FI", "HU", "RO", "ZH", "JA", "KO", "AR" };

        private static string SystemLanguageCode()
        {
            switch (Application.systemLanguage)
            {
                case SystemLanguage.French: return "FR";
                case SystemLanguage.German: return "DE";
                case SystemLanguage.Spanish: return "ES";
                case SystemLanguage.Italian: return "IT";
                case SystemLanguage.Portuguese: return "PT";
                case SystemLanguage.Dutch: return "NL";
                case SystemLanguage.Polish: return "PL";
                case SystemLanguage.Russian: return "RU";
                case SystemLanguage.Ukrainian: return "UK";
                case SystemLanguage.Turkish: return "TR";
                case SystemLanguage.Czech: return "CS";
                case SystemLanguage.Swedish: return "SV";
                case SystemLanguage.Danish: return "DA";
                case SystemLanguage.Norwegian: return "NO";
                case SystemLanguage.Finnish: return "FI";
                case SystemLanguage.Hungarian: return "HU";
                case SystemLanguage.Romanian: return "RO";
                case SystemLanguage.Chinese:
                case SystemLanguage.ChineseSimplified:
                case SystemLanguage.ChineseTraditional: return "ZH";
                case SystemLanguage.Japanese: return "JA";
                case SystemLanguage.Korean: return "KO";
                case SystemLanguage.Arabic: return "AR";
                default: return "EN";
            }
        }

        /// <summary>Partie "code" du code de groupe (avant un éventuel "@hote:port").</summary>
        [JsonIgnore]
        public string GroupKey
        {
            get
            {
                string k = (shareKey ?? "").Trim();
                int at = k.LastIndexOf('@');
                return at > 0 ? k.Substring(0, at) : k;
            }
        }

        /// <summary>Adresse contenue dans le code de groupe ("code@hote:port"), si c'est le code d'un ami.</summary>
        [JsonIgnore]
        public string CodeHost
        {
            get
            {
                string k = (shareKey ?? "").Trim();
                int at = k.LastIndexOf('@');
                return at > 0 && at < k.Length - 1 ? k.Substring(at + 1) : "";
            }
        }

        /// <summary>Vrai si ce PC est le serveur de sauvegarde du groupe (pas de VPS, pas de code d'un ami).</summary>
        [JsonIgnore]
        public bool IsLocalStorageOwner => (worldsUrl ?? "").Trim().Length == 0 && CodeHost.Length == 0 && GroupKey.Length >= 6;

        /// <summary>Serveur de sauvegarde effectivement utilisé par ce PC.</summary>
        [JsonIgnore]
        public string WorldsBaseUrl
        {
            get
            {
                if (IsLocalStorageOwner) return "http://127.0.0.1:" + localStoragePort;
                string u = (worldsUrl ?? "").Trim();
                if (u.Length == 0) u = CodeHost;
                return NormalizeUrl(u);
            }
        }

        /// <summary>Code à donner aux amis (contient l'adresse du serveur de sauvegarde).</summary>
        public string ShareCode(string publicIp)
        {
            string key = GroupKey;
            if (key.Length == 0) return "";
            if (IsLocalStorageOwner)
                return string.IsNullOrEmpty(publicIp) ? "" : key + "@" + publicIp + ":" + localStoragePort;
            string w = WorldsBaseUrl;
            if (w.Length == 0) return key;
            string host = w.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? w.Substring(7) : w;
            return key + "@" + host;
        }

        /// <summary>"1.2.3.4:8787" ou "http(s)://hote:port/" -> "http(s)://hote:port" ; "" si vide ou invalide.</summary>
        public static string NormalizeUrl(string u)
        {
            u = (u ?? "").Trim();
            if (u.Length == 0) return "";
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) u = "http://" + u;
            u = u.TrimEnd('/');
            if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.AbsolutePath.Trim('/').Length > 0 || uri.Query.Length > 0 || u.Contains(" ")) return "";
            return u;
        }

        /// <summary>Nouveau code de groupe (pour repartir avec son propre groupe).</summary>
        public static string NewGroupKey() => "groupe-" + RandomHex(8);

        private static string RandomHex(int bytes)
        {
            var b = new byte[bytes];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }
        public float hostPanelX = -1, hostPanelY = -1;
        public float listPanelX = -1, listPanelY = -1;

        [JsonIgnore] public static BrowserConfig Current = new BrowserConfig();

        public static string FilePath =>
            Path.Combine(Application.persistentDataPath, "BAMPServerBrowser", "config.json");

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    Current = JsonConvert.DeserializeObject<BrowserConfig>(File.ReadAllText(FilePath)) ?? new BrowserConfig();
                }
                // Première utilisation : code de groupe et clé admin générés automatiquement.
                if (string.IsNullOrWhiteSpace(Current.shareKey)) Current.shareKey = "groupe-" + RandomHex(8);
                if (string.IsNullOrWhiteSpace(Current.adminKey)) Current.adminKey = "admin-" + RandomHex(12);
                if (Array.IndexOf(Languages, (Current.language ?? "").Trim().ToUpperInvariant()) < 0) Current.language = SystemLanguageCode();
                else Current.language = Current.language.Trim().ToUpperInvariant();
                Current.listLanguage = (Current.listLanguage ?? "").Trim().ToUpperInvariant();
                if (Current.joinedWorlds == null) Current.joinedWorlds = new System.Collections.Generic.Dictionary<string, string>();
                if (Current.favorites == null) Current.favorites = new System.Collections.Generic.List<string>();
                if (Current.saveServerHistory == null) Current.saveServerHistory = new System.Collections.Generic.List<string>();
                // l'ancien serveur configuré à la main fait partie de l'historique
                string ws = (Current.worldsUrl ?? "").Trim();
                if (ws.Length > 0 && !Current.saveServerHistory.Contains(ws)) Current.saveServerHistory.Insert(0, ws);
                if (Current.localStoragePort < 1024 || Current.localStoragePort > 65535) Current.localStoragePort = 8788;
                Save();
            }
            catch (Exception ex)
            {
                ModEntry.Warn("config load: " + ex.Message);
                Current = new BrowserConfig();
            }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(Current, Formatting.Indented));
            }
            catch (Exception ex)
            {
                ModEntry.Warn("config save: " + ex.Message);
            }
        }

        [JsonIgnore]
        public KeyCode ToggleKeyCode
        {
            get
            {
                try { return (KeyCode)Enum.Parse(typeof(KeyCode), toggleKey, true); }
                catch { return KeyCode.F9; }
            }
        }
    }
}
