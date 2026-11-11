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
        public string shareKey = "";
        /// <summary>Mot de passe pour rejoindre la partie que l'on héberge. Vide (défaut) = partie ouverte.</summary>
        public string joinPassword = "";
        /// <summary>Clé admin du groupe (uniquement sur le PC de l'admin) : seule elle peut supprimer des mondes du serveur.</summary>
        public string adminKey = "";
        /// <summary>
        /// Serveur de sauvegarde externe (VPS) choisi par l'admin, ex. "http://1.2.3.4:8787".
        /// Vide (défaut) = le PC de l'hôte sert lui-même les sauvegardes (port localStoragePort),
        /// joignable par les amis avec son IP publique.
        /// </summary>
        public string worldsUrl = "";
        /// <summary>Port du serveur de sauvegarde intégré au jeu (mode local). À ouvrir (TCP) sur la box.</summary>
        public int localStoragePort = 8788;

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
                if (u.Length == 0) return "";
                if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) u = "http://" + u;
                return u.TrimEnd('/');
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
