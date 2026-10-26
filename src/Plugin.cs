using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HFCoop
{
    [BepInPlugin(Guid, "House Flipper Co-op", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "dev.hfcoop";
        public const string Version = "0.4.6";
        internal static ManualLogSource Log;
        internal static string GameBuild = "";

        /// <summary>Name of the house scene currently loaded (what both peers must share).</summary>
        internal static string SceneName
        {
            get
            {
                string best = "";
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var n = SceneManager.GetSceneAt(i).name;
                    if (!n.StartsWith("scn_")) best = n;
                }
                return best;
            }
        }

        static string _tool = ""; static float _toolAt;
        /// <summary>Name of the tool the local player currently holds (first active Tool component under the player rig).</summary>
        internal static string CurrentToolName()
        {
            if (Time.realtimeSinceStartup - _toolAt < 0.4f) return _tool;
            _toolAt = Time.realtimeSinceStartup;
            var pc = Object.FindObjectOfType<Assets.Code.Player.PlayerController>();
            _tool = "";
            if (pc != null)
                foreach (var t in pc.GetComponentsInChildren<Assets.Code.Player.Tools.Tool>(false))
                    if (t != null && t.isActiveAndEnabled) { _tool = t.GetType().Name; break; }
            return _tool;
        }

        /// <summary>True while the local player is working with the held tool (a mouse button held in game, not in a menu): remote avatars animate their tool with it.</summary>
        internal static bool LocalUsingTool()
        {
            try { return Cursor.lockState == CursorLockMode.Locked && (Input.GetMouseButton(0) || Input.GetMouseButton(1)); }
            catch { return false; }
        }

        bool _recovered;
        bool _menu; string _addr; Rect _win = new Rect(20, 60, 360, 200);

        void Awake()
        {
            Log = Logger;
            for (int attempt = 0; ; attempt++)
            {
                try { Cfg.Bind(base.Config); break; }
                catch (System.IO.IOException e) { if (attempt >= 20) throw; Logger.LogWarning("config file busy (" + e.Message + "), retrying"); System.Threading.Thread.Sleep(300); }
            }
            _addr = Cfg.JoinAddress.Value;
            Events.Init(); Watchdog.Start();
            Application.runInBackground = true; // a player who alt-tabs must not stall the session
            GameBuild = Application.version;
            Log.LogInfo("HFCoop " + Version + " loaded. Unity " + Application.unityVersion + ", game " + GameBuild);
        }

        void Update()
        {
            Watchdog.Beat("update");
            if (Input.GetKeyDown(KeyCode.F8) && Input.GetKey(KeyCode.LeftControl)) Inspector.DumpAim();
            if (Input.GetKeyDown(KeyCode.F9) && Input.GetKey(KeyCode.LeftControl)) Inspector.DumpHistogram();
            if (Input.GetKeyDown(KeyCode.F10) && Input.GetKey(KeyCode.LeftControl)) Inspector.DumpPlayer();
            if (Input.GetKeyDown(KeyCode.F2) && Input.GetKey(KeyCode.LeftControl)) Inspector.NudgeAimedItem();
            if (Input.GetKeyDown(KeyCode.F11) && Input.GetKey(KeyCode.LeftControl)) SteamProbe.Toggle();
            if (Input.GetKeyDown(KeyCode.F12) && Input.GetKey(KeyCode.LeftControl)) Session.DebugDrop(); // simulates a network failure (reconnect test)
            SteamProbe.Tick();
            if (Input.GetKeyDown(Cfg.MenuKey.Value)) _menu = !_menu;
            if (Input.GetKeyDown(Cfg.HostKey.Value)) Session.StartHost();
            if (Input.GetKeyDown(Cfg.JoinKey.Value)) Session.StartJoin(Cfg.JoinAddress.Value);
            if (Input.GetKeyDown(Cfg.LeaveKey.Value)) Session.Leave();
            if (!_recovered && Time.realtimeSinceStartup > 5f) _recovered = MoneySync.RecoverIfNeeded() & ProgressSync.RecoverIfNeeded();
            Watchdog.Phase("session.tick");
            Session.Tick();
            if (!Session.Active) Snapshot.Tick(); // clears the "do not save" flag after leaving a session and returning to the menu
            Watchdog.Phase("dev.tick");
            Dev.Tick();
            Watchdog.Phase("update:end");
        }

        void LateUpdate()
        {
            if (_menu) { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; }
        }

        void OnDestroy() { if (Session.Active) Session.Leave("plugin unloaded"); }
        void OnApplicationQuit() { if (Session.Active) Session.Leave("game closing"); }

        void OnGUI()
        {
            Watchdog.Phase("ongui"); Avatars.DrawTags();
            float s = Mathf.Max(1f, Screen.height / 1080f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
            string badge = Session.Reconnecting ? "Co-op: " + Session.ReconnectText : Session.Role == Role.None ? "Co-op: offline  (" + Cfg.MenuKey.Value + ")" :
                (Session.Role == Role.Host ? "HOST" : "CLIENT") + " #" + Session.MyId + " | " + Session.PlayerCount + " players | " + Session.Status;
            var st = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            st.normal.textColor = Color.black; GUI.Label(new Rect(11, 11, 600, 20), badge, st);
            st.normal.textColor = Session.LastError.Length > 0 ? new Color(1f, 0.5f, 0.4f) : Color.white; GUI.Label(new Rect(10, 10, 600, 20), badge, st);
            if (Session.LastError.Length > 0 && Session.Role == Role.None) { st.normal.textColor = new Color(1f, 0.5f, 0.4f); GUI.Label(new Rect(10, 28, 800, 20), Session.LastError, st); }
            if (SteamProbe.Status.Length > 0)
            {
                st.normal.textColor = Color.black; GUI.Label(new Rect(11, 47, 900, 20), "Steam probe: " + SteamProbe.Status, st);
                st.normal.textColor = new Color(0.6f, 1f, 0.6f); GUI.Label(new Rect(10, 46, 900, 20), "Steam probe: " + SteamProbe.Status, st);
            }
            if (_menu) _win = GUILayout.Window(7731, _win, DrawWin, "House Flipper Co-op " + Version);
        }

        System.Collections.Generic.List<SteamTransport.FriendInfo> _friends = new System.Collections.Generic.List<SteamTransport.FriendInfo>();
        float _friendsAt; string _steamId = "";

        /// <summary>Optional Steam P2P section: no IP address or port forwarding, both players must start the game through Steam.</summary>
        void DrawSteam()
        {
            GUILayout.Label("Steam (optional, no port forwarding)");
            if (!SteamTransport.Available) { GUILayout.Label("  not available: " + SteamTransport.LastError); return; }
            GUILayout.BeginHorizontal(); GUILayout.Label("Your Steam ID: " + SteamTransport.MyId);
            if (GUILayout.Button("Copy", GUILayout.Width(50))) GUIUtility.systemCopyBuffer = SteamTransport.MyId.ToString(); GUILayout.EndHorizontal();
            Cfg.SteamHost.Value = GUILayout.Toggle(Cfg.SteamHost.Value, "Also host via Steam (then press Host session)");
            if (Time.realtimeSinceStartup > _friendsAt) { _friendsAt = Time.realtimeSinceStartup + 2f; _friends = SteamTransport.Friends(); }
            int shown = 0;
            foreach (var f in _friends)
            {
                if (!f.Hosting) continue;
                if (GUILayout.Button("Join " + f.Name + " (hosting)")) Session.StartJoinSteam(f.Id);
                shown++;
            }
            if (shown == 0) GUILayout.Label("  no Steam friend is hosting right now");
            GUILayout.BeginHorizontal(); GUILayout.Label("ID", GUILayout.Width(40)); _steamId = GUILayout.TextField(_steamId, 20); GUILayout.EndHorizontal();
            if (GUILayout.Button("Join by Steam ID"))
            {
                ulong sid;
                if (SteamTransport.TryParseTarget(_steamId, out sid)) Session.StartJoinSteam(sid); else Session.LastError = "That is not a Steam ID (17 digits)";
            }
        }

        void DrawWin(int id)
        {
            GUILayout.Label("Status: " + Session.Status);
            if (Session.LastError.Length > 0) GUILayout.Label("<color=#ff8866>" + Session.LastError + "</color>");
            GUILayout.Label("House: " + (SceneName.Length > 0 ? SceneName : "(none - main menu)"));
            if (SceneName.Length == 0 && Session.Role == Role.None) GUILayout.Label("Lobby: host from here and load your save, or join from here and you will be taken into the host's house.");
            if (Session.Reconnecting && GUILayout.Button("Cancel reconnecting")) Session.Leave("Reconnect cancelled");
            if (Session.Role == Role.None)
            {
                GUILayout.BeginHorizontal(); GUILayout.Label("Name", GUILayout.Width(40));
                Cfg.PlayerName.Value = GUILayout.TextField(Cfg.PlayerName.Value, 20); GUILayout.EndHorizontal();
                if (GUILayout.Button("Host session (port " + Cfg.Port.Value + ")")) Session.StartHost();
                GUILayout.BeginHorizontal(); GUILayout.Label("Host", GUILayout.Width(40));
                _addr = GUILayout.TextField(_addr, 40); GUILayout.EndHorizontal();
                if (GUILayout.Button("Join")) { Cfg.JoinAddress.Value = _addr; Session.StartJoin(_addr); }
                GUILayout.Space(6); DrawSteam();
            }
            else
            {
                if (Session.SteamHosting)
                {
                    GUILayout.BeginHorizontal(); GUILayout.Label("Steam: friends join with ID " + SteamTransport.MyId);
                    if (GUILayout.Button("Copy", GUILayout.Width(50))) GUIUtility.systemCopyBuffer = SteamTransport.MyId.ToString(); GUILayout.EndHorizontal();
                }
                GUILayout.Label("Players:");
                GUILayout.Label("  #" + Session.MyId + " " + Cfg.PlayerName.Value + " (you" + (Session.Role == Role.Host ? ", host" : "") + ")");
                foreach (var p in Session.Players.Values)
                    GUILayout.Label("  #" + p.Id + " " + p.Name + (p.Id == 0 ? " (host)" : "") + (p.Scene.Length > 0 && p.Scene != SceneName ? "  [in " + p.Scene + "]" : ""));
                if (GUILayout.Button("Disconnect")) Session.Leave();
            }
            GUI.DragWindow();
        }
    }
}


