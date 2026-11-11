using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BigAmbitionsMP;
using Newtonsoft.Json;
using UnityEngine;

namespace BAMPServerBrowser
{
    public class DedicatedConfig
    {
        /// <summary>"auto" = charge la dernière sauvegarde multi si elle existe, sinon nouvelle partie ; "new" ; "load".</summary>
        public string mode = "auto";
        /// <summary>Nom de la partie (Base) à charger ; vide = la plus récente.</summary>
        public string save = "";
        public float startDelaySeconds = 5f;
        public bool autoAccept = true;
        public int autosaveMinutes = 10;
        public int targetFps = 30;
        public int statusLogSeconds = 60;
        /// <summary>Coupe tout le son du jeu en mode serveur.</summary>
        public bool mute = true;

        public static string FilePath =>
            Path.Combine(Application.persistentDataPath, "BAMPServerBrowser", "dedicated.json");

        public static DedicatedConfig Load()
        {
            var c = new DedicatedConfig();
            try
            {
                if (File.Exists(FilePath)) c = JsonConvert.DeserializeObject<DedicatedConfig>(File.ReadAllText(FilePath)) ?? c;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(c, Formatting.Indented));
            }
            catch (Exception ex) { ModEntry.Warn("dedicated config: " + ex.Message); }
            return c;
        }
    }

    /// <summary>
    /// Mode serveur : pilote automatiquement le mod multijoueur (héberger → démarrer →
    /// accepter les joueurs → sauvegarder), sans clic. Activé par "-bamp-dedicated".
    /// Arrêt propre : créer le fichier "stop" à côté de dedicated.json.
    /// </summary>
    public class DedicatedHost : MonoBehaviour
    {
        private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly MethodInfo OnMpHostNewM = typeof(MPCanvasUI).GetMethod("OnMpHostNew", F);
        private static readonly MethodInfo LoadPickedM = typeof(MPCanvasUI).GetMethod("LoadPickedSession", F);
        private static readonly MethodInfo OnLobbyStartM = typeof(MPCanvasUI).GetMethod("OnLobbyStart", F);

        public static bool Requested =>
            Environment.GetCommandLineArgs().Any(a => string.Equals(a, "-bamp-dedicated", StringComparison.OrdinalIgnoreCase));

        private enum Phase { WaitMenu, Hosting, WaitLobby, Starting, InGame, Failed, Stopping }

        private DedicatedConfig _cfg;
        private Phase _phase = Phase.WaitMenu;
        private float _phaseAt;
        private float _menuSeenAt = -1;
        private float _nextSave, _nextStatus, _nextAccept, _nextFpsCheck, _nextMute, _nextCmd;
        private string _cmdFile;
        private int _attempts;
        private string _stopFile;

        private void Awake()
        {
            _cfg = DedicatedConfig.Load();
            _stopFile = Path.Combine(Path.GetDirectoryName(DedicatedConfig.FilePath), "stop");
            _cmdFile = Path.Combine(Path.GetDirectoryName(DedicatedConfig.FilePath), "command.txt");
            try { if (File.Exists(_cmdFile)) File.Delete(_cmdFile); } catch { }
            try { if (File.Exists(_stopFile)) File.Delete(_stopFile); } catch { }
            if (_cfg.targetFps > 0)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = _cfg.targetFps;
            }
            Application.runInBackground = true;
            Log($"server mode active (mode={_cfg.mode}, save='{_cfg.save}', fps={_cfg.targetFps}, autosave={_cfg.autosaveMinutes} min). To stop: create '{_stopFile}'.");
        }

        private static void Log(string s) => ModEntry.Info("[Dedicated] " + s);

        private void SetPhase(Phase p) { _phase = p; _phaseAt = Time.unscaledTime; Log("→ " + p); }

        private void Update()
        {
            float now = Time.unscaledTime;
            if (_phase != Phase.Stopping && File.Exists(_stopFile)) { StopServer("stop file"); return; }
            // le jeu remet sa propre limite d'images en chargeant la ville : on la réimpose
            if (_cfg.targetFps > 0 && now >= _nextFpsCheck)
            {
                _nextFpsCheck = now + 2f;
                if (Application.targetFrameRate != _cfg.targetFps || QualitySettings.vSyncCount != 0)
                {
                    QualitySettings.vSyncCount = 0;
                    Application.targetFrameRate = _cfg.targetFps;
                }
            }
            if (_cfg.mute && now >= _nextMute)
            {
                _nextMute = now + 2f;
                if (AudioListener.volume != 0f) AudioListener.volume = 0f;
                if (!AudioListener.pause) AudioListener.pause = true;
            }
            if (now >= _nextCmd) { _nextCmd = now + 0.5f; PollCommand(); }

            switch (_phase)
            {
                case Phase.WaitMenu:
                    if (!Bridge.InMainMenu) { _menuSeenAt = -1; if (now - _phaseAt > 300) Fail("main menu never reached"); return; }
                    if (_menuSeenAt < 0) _menuSeenAt = now;
                    if (now - _menuSeenAt < 3f) return;
                    // Le serveur doit tourner avec un compte Steam connecté qui possède le jeu.
                    bool steamOk = false;
                    try { steamOk = Steamworks.SteamClient.IsValid && Steamworks.SteamApps.IsSubscribedToApp(1331550); } catch { }
                    if (!steamOk) { Fail("Steam is not running / not signed in with an account that owns Big Ambitions"); return; }
                    Host();
                    break;

                case Phase.WaitLobby:
                    if (MPServer.IsRunning && MPServer.IsInLobby)
                    {
                        if (now - _phaseAt >= _cfg.startDelaySeconds) StartGame();
                    }
                    else if (now - _phaseAt > 30) Retry("the lobby did not open (port already in use?)");
                    break;

                case Phase.Starting:
                    if (!MPServer.IsRunning) { Retry("the server stopped while starting"); return; }
                    TickIntro(now);
                    if (!MPServer.IsInLobby && MPServer.HostSnapshotsReady)
                    {
                        SetPhase(Phase.InGame);
                        _reachedGame = true;
                        _nextSave = now + Math.Max(1, _cfg.autosaveMinutes) * 60f;
                        Log($"game running. Port {MPServer.BoundPort}, Steam {(Steamworks.SteamClient.IsValid ? "yes" : "no")}.");
                    }
                    else if (now - _phaseAt > 600) Fail("the game did not finish loading within 10 min");
                    break;

                case Phase.InGame:
                    if (!MPServer.IsRunning) { Retry("server stopped"); return; }
                    if (_cfg.autoAccept && now >= _nextAccept)
                    {
                        _nextAccept = now + 1f;
                        foreach (var (peer, pid) in MPServer.PendingJoinList)
                        {
                            Log("accepting '" + pid + "'");
                            try { MPServer.AcceptPendingJoin(peer); } catch (Exception ex) { ModEntry.Warn("accept: " + ex.Message); }
                        }
                    }
                    if (_cfg.autosaveMinutes > 0 && now >= _nextSave)
                    {
                        _nextSave = now + _cfg.autosaveMinutes * 60f;
                        try { MPSaveCoordinator.HostSaveNow("dedicated-autosave"); Log("autosave"); }
                        catch (Exception ex) { ModEntry.Warn("autosave: " + ex.Message); }
                    }
                    break;
            }

            if (_cfg.statusLogSeconds > 0 && now >= _nextStatus)
            {
                _nextStatus = now + _cfg.statusLogSeconds;
                int players = 0; try { players = MPServer.IsRunning ? MPServer.ConnectedCount : 0; } catch { }
                Log($"state={_phase} players connected={players} fps={(1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f)):0}");
            }
        }

        // Écran de création du personnage (nouvelle partie) : le mod multijoueur pré-remplit
        // le nom de l'hôte, on valide comme le bouton "Commencer", puis on passe l'intro.
        private float _introSeenAt = -1, _nextIntroCheck, _transitionSeenAt = -1;
        private bool _introDone, _transitionDone;

        private void TickIntro(float now)
        {
            if (now < _nextIntroCheck) return;
            _nextIntroCheck = now + 1f;
            try
            {
                if (!_introDone)
                {
                    var c = UnityEngine.Object.FindObjectOfType<Intro.IntroCharacterCustomizer>();
                    if (c == null || !c.gameObject.activeInHierarchy) { _introSeenAt = -1; return; }
                    if (_introSeenAt < 0) { _introSeenAt = now; Log("character screen detected."); return; }
                    if (now - _introSeenAt < 3f) return;
                    c.StartGame();
                    if (!c.gameObject.activeInHierarchy) { _introDone = true; Log("character confirmed (name: " + MPConfig.PlayerId + ")."); }
                    else ModEntry.Warn("[Dedicated] character confirmation was refused (empty or invalid name?), retrying.");
                    return;
                }
                if (!_transitionDone)
                {
                    var t = UnityEngine.Object.FindObjectOfType<Intro.IntroToGameTransition>();
                    if (t == null || !t.gameObject.activeInHierarchy) return;
                    if (_transitionSeenAt < 0) { _transitionSeenAt = now; return; }
                    if (now - _transitionSeenAt < 5f) return;
                    _transitionDone = true;
                    Log("skipping the intro screen.");
                    t.Continue();
                }
            }
            catch (Exception ex) { ModEntry.Warn("[Dedicated] intro: " + ex.Message); }
        }

        // Commandes envoyées par la console (server.ps1) via command.txt (une par ligne).
        private void PollCommand()
        {
            string[] lines;
            try
            {
                if (!File.Exists(_cmdFile)) return;
                lines = File.ReadAllLines(_cmdFile);
                File.Delete(_cmdFile);
            }
            catch { return; }
            foreach (var raw in lines)
            {
                string line = (raw ?? "").Trim();
                if (line.Length == 0) continue;
                int sp = line.IndexOf(' ');
                string cmd = (sp > 0 ? line.Substring(0, sp) : line).ToLowerInvariant();
                string arg = sp > 0 ? line.Substring(sp + 1).Trim() : "";
                try
                {
                    switch (cmd)
                    {
                        case "stop": StopServer("command"); return;
                        case "save":
                            if (_phase == Phase.InGame) { MPSaveCoordinator.HostSaveNow("console"); Log("save requested."); }
                            else Log("no game running to save.");
                            break;
                        case "status":
                        case "players":
                            string list = MPServer.IsRunning ? ConnectedPlayers() : "";
                            Log($"state={_phase} port={(MPServer.IsRunning ? MPServer.BoundPort : 0)} players={(MPServer.IsRunning ? MPServer.ConnectedCount : 0)}" + (list.Length > 0 ? " : " + list : ""));
                            break;
                        case "say":
                            if (arg.Length > 0 && MPServer.IsRunning) { MPServer.BroadcastChat(MPConfig.PlayerId, arg); Log("message sent: " + arg); }
                            break;
                        case "help":
                            Log("commands: status | players | say <message> | save | stop");
                            break;
                        default:
                            Log("unknown command '" + cmd + "' (try help)");
                            break;
                    }
                }
                catch (Exception ex) { ModEntry.Warn("[Dedicated] command '" + cmd + "': " + (ex.InnerException ?? ex).Message); }
            }
        }

        private static string ConnectedPlayers()
        {
            try
            {
                var m = typeof(MPServer).GetMethod("ConnectedPids", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (m?.Invoke(null, null) is System.Collections.Generic.IEnumerable<string> pids) return string.Join(", ", pids);
            }
            catch { }
            return "";
        }

        private void Host()
        {
            var ui = MPCanvasUI.Instance;
            if (ui == null || OnMpHostNewM == null || LoadPickedM == null || OnLobbyStartM == null)
            {
                Fail("multiplayer mod not found or incompatible version");
                return;
            }
            _attempts++;
            try
            {
                string mode = (_cfg.mode ?? "auto").ToLowerInvariant();
                MPSaveManager.MpVariant pick = null;
                if (mode != "new")
                {
                    var pts = MPSaveManager.ListPlaythroughs() ?? new System.Collections.Generic.List<MPSaveManager.MpPlaythrough>();
                    var pt = string.IsNullOrWhiteSpace(_cfg.save)
                        ? pts.OrderByDescending(p => p.NewestUnix).FirstOrDefault()
                        : pts.FirstOrDefault(p => string.Equals(p.Base, _cfg.save.Trim(), StringComparison.OrdinalIgnoreCase));
                    pick = pt?.Variants?.OrderByDescending(v => v.SavedAtUnix).FirstOrDefault();
                    if (pick == null && mode == "load") { Fail("save '" + _cfg.save + "' not found"); return; }
                }

                if (pick != null)
                {
                    Log($"hosting save '{pick.SessionName}' (day {pick.Day}).");
                    LoadPickedM.Invoke(ui, new object[] { pick.SessionName, pick.PlaythroughId ?? "" });
                }
                else
                {
                    Log("hosting a new game.");
                    OnMpHostNewM.Invoke(ui, null);
                }
                SetPhase(Phase.WaitLobby);
            }
            catch (Exception ex)
            {
                Retry("hosting: " + (ex.InnerException ?? ex).Message);
            }
        }

        private void StartGame()
        {
            try
            {
                OnLobbyStartM.Invoke(MPCanvasUI.Instance, null);
                SetPhase(Phase.Starting);
            }
            catch (Exception ex) { Retry("starting: " + (ex.InnerException ?? ex).Message); }
        }

        private void Retry(string why)
        {
            ModEntry.Warn("[Dedicated] " + why);
            if (_attempts >= 3) { Fail("too many failures"); return; }
            try { if (MPServer.IsRunning) MPServer.Stop(); } catch { }
            _menuSeenAt = -1;
            _introDone = _transitionDone = false; _introSeenAt = _transitionSeenAt = -1;
            SetPhase(Phase.WaitMenu);
        }

        private void Fail(string why)
        {
            ModEntry.Warn("[Dedicated] FAILED: " + why + " - shutting down.");
            SetPhase(Phase.Failed);
            Application.Quit(2);
        }

        private void StopServer(string why)
        {
            Log("stop requested (" + why + "), saving…");
            SetPhase(Phase.Stopping);
            try { File.Delete(_stopFile); } catch { }
            SaveIfPlaying();
            Application.Quit(0);
        }

        private bool _savedOnQuit, _reachedGame;
        private void SaveIfPlaying()
        {
            if (_savedOnQuit) return;
            _savedOnQuit = true;
            try
            {
                if (_reachedGame && MPServer.IsRunning && !MPServer.IsInLobby)
                {
                    MPSaveCoordinator.HostSaveSync("dedicated-stop");
                    Log("saved.");
                }
            }
            catch (Exception ex) { ModEntry.Warn("save on stop: " + ex.Message); }
        }

        private void OnApplicationQuit() => SaveIfPlaying();
    }
}
