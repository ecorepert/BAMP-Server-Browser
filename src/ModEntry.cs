using System.Threading.Tasks;
using BAModAPI;
using UnityEngine;

[assembly: RegisterModClass(typeof(BAMPServerBrowser.ModEntry))]

namespace BAMPServerBrowser
{
    /// <summary>
    /// Mod compagnon de "Going Public: Multiplayer for Big Ambitions".
    /// N'altère ni le jeu ni le mod original : il lit son état public et
    /// pilote sa boîte "Join a Game" par réflexion.
    /// </summary>
    [ModEntryOnInitializationLoad]
    public class ModEntry : ModBigAmbitionsBase
    {
        internal static IModLogger Log;
        private GameObject _host;

        public override Task OnLoadAsync(ModContext context)
        {
            Log = context.Logger;
            BrowserConfig.Load();
            _host = new GameObject("BAMPServerBrowser");
            Object.DontDestroyOnLoad(_host);
            _host.AddComponent<ServerBrowser>();
            JoinPassword.Install();
            AdminChannel.Install();
            SaveServerPrompt.Install();
            if (DedicatedHost.Requested) _host.AddComponent<DedicatedHost>();
            Info("loaded v" + BuildInfo.Short + " - master " + BrowserConfig.Current.masterUrl);
            return Task.CompletedTask;
        }

        public override Task OnUnloadAsync()
        {
            JoinPassword.Uninstall();
            AdminChannel.Uninstall();
            SaveServerPrompt.Uninstall();
            if (_host != null)
            {
                var b = _host.GetComponent<ServerBrowser>();
                if (b != null) b.UnregisterNow();
                Object.Destroy(_host);
            }
            return Task.CompletedTask;
        }

        internal static void Info(string s)
        {
            try { if (Log != null) Log.Info("[ServerBrowser] " + s); else Debug.Log("[ServerBrowser] " + s); } catch { }
        }

        internal static void Warn(string s)
        {
            try { if (Log != null) Log.Warn("[ServerBrowser] " + s); else Debug.LogWarning("[ServerBrowser] " + s); } catch { }
        }
    }
}
