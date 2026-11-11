using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using BigAmbitionsMP;
using Newtonsoft.Json.Linq;

namespace BAMPServerBrowser
{
    /// <summary>Version de ce mod (lue dans l'assembly, voir &lt;Version&gt; du .csproj).</summary>
    public static class BuildInfo
    {
        public static readonly Version Version = typeof(BuildInfo).Assembly.GetName().Version ?? new Version(1, 0, 0);
        public static string Short => Version.ToString(3);
        public static string UserAgent => "BAMPServerBrowser/" + Short;
    }

    /// <summary>Infos publiées dans la liste : réglages de difficulté et noms des joueurs connectés.</summary>
    public static class GameInfo
    {
        private static readonly PropertyInfo HostSettingsP = typeof(MPCanvasUI).GetProperty("_hostSettings", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Réglages de la partie (monde chargé, sinon ceux choisis dans le salon pour une nouvelle partie).</summary>
        private static GameVariablesDto Current()
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi != null && gi.gameVariables != null) return MPServer.DtoFromGameVariables(gi.gameVariables);
            }
            catch { }
            try { if (MPCanvasUI.Instance != null && HostSettingsP != null) return HostSettingsP.GetValue(MPCanvasUI.Instance) as GameVariablesDto; } catch { }
            return null;
        }

        /// <summary>Résumé court de la difficulté, par ex. "Normal · Start $5,000 · Tax 20% · 30 days/year · No energy".</summary>
        public static string RulesSummary()
        {
            var d = Current();
            if (d == null) return "";
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(d.Difficulty)) parts.Add(d.Difficulty);
            parts.Add("Start $" + d.StartingMoney.ToString("N0", Inv));
            if (d.StartingAge > 0) parts.Add("Age " + d.StartingAge);
            parts.Add("Tax " + d.TaxPercentage + "%");
            if (d.DaysPerYear > 0) parts.Add(d.DaysPerYear + " days/year");
            void Mult(string label, float v) { if (Math.Abs(v - 1f) > 0.001f && v > 0f) parts.Add(label + " x" + v.ToString("0.##", Inv)); }
            Mult("Rivals", d.RivalsDifficultyMultiplier);
            Mult("Prices", d.MarketPriceMultiplier);
            Mult("Salaries", d.EmployeeHourlySalaryMultiplier);
            Mult("Customers", d.BaseCustomerPromotionMultiplier);
            Mult("Selling", d.SellingMultiplier);
            Mult("Export", d.ExportMultiplier);
            Mult("Bank interest", d.BankInterestMultiplier);
            void Pct(string label, int v) { if (v > 0 && v != 100) parts.Add(label + " " + v + "%"); }
            Pct("Needs", d.NeedsDrainPercent);
            Pct("Rest speed", d.RestSpeedPercent);
            Pct("Morale", d.MoraleTempoPercent);
            void Flag(string label, bool v) { if (v) parts.Add(label); }
            Flag("No aging", d.DisableAging);
            Flag("No energy", d.DisableEnergy);
            Flag("No happiness", d.DisableHappiness);
            Flag("No vehicle damage", d.DisableVehicleDamage);
            Flag("No fuel", d.DisableVehicleFuel);
            Flag("All courses", d.AllCoursesUnlocked);
            Flag("All contacts", d.AllContactsUnlocked);
            Flag("No import limits", d.DisableWholesaleAndImportLimits);
            Flag("All import products", d.AllProductsAvailableFromImporters);
            string s = string.Join(" · ", parts);
            return s.Length > 300 ? s.Substring(0, 300) : s;
        }

        /// <summary>Noms de l'hôte et des joueurs connectés (16 au plus).</summary>
        public static List<string> PlayerNames()
        {
            var res = new List<string>();
            try { string me = MPConfig.PlayerId ?? ""; if (me.Length > 0) res.Add(Name(me)); } catch { }
            try
            {
                foreach (var (_, pid) in MPServer.ConnectedClientPeers() ?? new List<(MPLink, string)>())
                    if (!string.IsNullOrEmpty(pid) && res.Count < 16) res.Add(Name(pid));
            }
            catch { }
            return res;
        }

        private static string Name(string pid)
        {
            string n = pid;
            try { n = MPServer.DisplayNameFor(pid) ?? pid; } catch { }
            return n.Length > 24 ? n.Substring(0, 24) : n;
        }
    }

    /// <summary>Vérifie une fois par lancement si une version plus récente du mod est publiée sur GitHub.</summary>
    public static class UpdateCheck
    {
        public const string RepoUrl = "https://github.com/ecorepert/BAMP-Server-Browser";
        private const string VersionUrl = "https://github.com/ecorepert/BAMP-Server-Browser/raw/main/version.json";
        public static volatile string NewVersion = "";
        public static volatile string Notes = "";
        private static bool _started;

        public static void Start()
        {
            if (_started || !BrowserConfig.Current.checkUpdates) return;
            _started = true;
            Task.Run(() =>
            {
                try
                {
                    try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                    var r = (HttpWebRequest)WebRequest.Create(VersionUrl);
                    r.Timeout = 8000; r.ReadWriteTimeout = 8000; r.UserAgent = BuildInfo.UserAgent;
                    using (var resp = (HttpWebResponse)r.GetResponse())
                    using (var sr = new System.IO.StreamReader(resp.GetResponseStream()))
                    {
                        string body = sr.ReadToEnd();
                        if (body.Length > 4096) return;
                        var o = JObject.Parse(body);
                        if (Version.TryParse((string)o["version"] ?? "", out var v) && v > BuildInfo.Version)
                        {
                            Notes = ((string)o["notes"] ?? "").Length > 200 ? ((string)o["notes"]).Substring(0, 200) : ((string)o["notes"] ?? "");
                            NewVersion = v.ToString(3);
                            ModEntry.Info($"new version available: {NewVersion} (you have {BuildInfo.Short}).");
                        }
                    }
                }
                catch { /* pas d'internet ou GitHub injoignable : on n'insiste pas */ }
            });
        }
    }
}
