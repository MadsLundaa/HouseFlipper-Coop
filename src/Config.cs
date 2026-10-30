using BepInEx.Configuration;

namespace HFCoop
{
    internal static class Cfg
    {
        public static ConfigEntry<int> Port, SendRate, Timeout, ConnectTimeout, MaxPlayers, ReconnectSeconds;
        public static ConfigEntry<bool> AutoReconnect;
        public static ConfigEntry<string> PlayerName, JoinAddress;
        public static ConfigEntry<bool> DevCommands, KeepSharedProgress, TemporaryWorkshop; public static ConfigEntry<int> LoadRetrySeconds;
        public static ConfigEntry<bool> AlwaysLitLamps, DiscordPresence, VerboseLog, ShowNameTags, AutoSyncMoney, SteamProbeAuto, SteamForceRelay, SteamHost;
        public static ConfigEntry<string> SteamProbeTarget;
        public static ConfigEntry<int> SteamChunkKB;
        public static ConfigEntry<UnityEngine.KeyCode> MenuKey, HostKey, JoinKey, LeaveKey;

        public static void Bind(ConfigFile c)
        {
            Port = c.Bind("Network", "Port", 27055, "TCP port the host listens on (forward/allow it in the firewall).");
            JoinAddress = c.Bind("Network", "JoinAddress", "127.0.0.1", "Host address to join (ip or ip:port).");
            PlayerName = c.Bind("Network", "PlayerName", System.Environment.UserName, "Name shown to other players.");
            SendRate = c.Bind("Network", "PlayerSendRateHz", 15, "Player state updates per second.");
            Timeout = c.Bind("Network", "TimeoutSeconds", 40, "Drop a peer after this many seconds of silence (a slow PC can stall for many seconds while loading a house).");
            ConnectTimeout = c.Bind("Network", "ConnectTimeoutSeconds", 30, "Give up joining after this many seconds.");
            AutoReconnect = c.Bind("Network", "AutoReconnect", true, "Client: when the connection to the host is lost (not when the host ends the session or you leave), keep the house open and retry the same address.");
            ReconnectSeconds = c.Bind("Network", "ReconnectSeconds", 120, "How long a client keeps trying to reconnect before giving up.");
            MaxPlayers = c.Bind("Network", "MaxPlayers", 4, "Maximum players including the host.");
            VerboseLog = c.Bind("Debug", "VerboseLog", false, "Log every replicated event.");
            KeepSharedProgress = c.Bind("Progress", "KeepSharedProgress", false, "Joiner: keep the shared skills/tools/statistics after leaving (default: restore your own).");
            TemporaryWorkshop = c.Bind("Workshop", "TemporaryDownloads", true, "Joiner: when the host uses Steam Workshop items you do not have, subscribe to them for the length of the session (Steam downloads them) and unsubscribe again when you disconnect. Items you were already subscribed to are never touched. Off: those items are simply missing for you.");
            LoadRetrySeconds = c.Bind("Network", "LoadRetrySeconds", 40, "Joiner: if the host's house has not finished loading after this many seconds, fetch it again (a safety net for a load that hangs; a slower PC can need more than 20 s for a healthy load).");
            DevCommands = c.Bind("Debug", "DevCommands", false, "Execute commands written to BepInEx/hfcoop-cmd-<pid>.txt (testing only).");
            AlwaysLitLamps = c.Bind("UI", "AlwaysLitLamps", true, "Lamps that are switched on stay lit when you leave their room (the game normally darkens lamps outside the room you are in).");
            DiscordPresence = c.Bind("UI", "DiscordPresence", true, "Show a Discord Rich Presence (game + co-op player count). The game's own native Discord SDK is always disabled: it can crash the game.");
            ShowNameTags =c.Bind("UI", "ShowNameTags", true, "Draw name tags above remote players.");
            SteamHost = c.Bind("Steam", "HostViaSteam", false, "When hosting, also accept Steam P2P connections (friends join by Steam ID or from the friends list, no port forwarding). TCP stays on regardless.");
            SteamChunkKB = c.Bind("Steam", "MaxChunkKB", 480, "Advanced: largest single Steam message in KB (frames above it are split and reassembled). Lower it only for testing.");
            SteamProbeAuto = c.Bind("Steam", "ProbeAutoStart", false, "Experimental: run the Steam P2P connectivity probe 20 s after start (also Ctrl+F11).");
            SteamProbeTarget = c.Bind("Steam", "ProbeTarget", "", "Probe: SteamID64 to connect to. Empty = listen and report this machine's SteamID64.");
            SteamForceRelay = c.Bind("Steam", "ForceRelay", false, "Probe: disable direct paths so Steam's relay network is used.");
            MenuKey = c.Bind("Keys", "ToggleMenu", UnityEngine.KeyCode.F7, "Open/close the co-op panel.");
            HostKey = c.Bind("Keys", "HostSession", UnityEngine.KeyCode.F5, "Quick: start hosting.");
            JoinKey = c.Bind("Keys", "JoinSession", UnityEngine.KeyCode.F6, "Quick: join JoinAddress.");
            LeaveKey = c.Bind("Keys", "LeaveSession", UnityEngine.KeyCode.F4, "Quick: disconnect.");
        }
    }
}

