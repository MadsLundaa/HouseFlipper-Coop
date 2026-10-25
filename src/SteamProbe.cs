using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Steamworks;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Feasibility probe for Steam P2P (ISteamNetworkingSockets, through the Steamworks.NET build the game already ships). Independent of the real session code.
    /// One side listens (no target configured), the other connects to the listener's SteamID64. The connecting side measures round trips and a snapshot-sized
    /// message and writes a report to BepInEx/hfcoop-steam-probe.txt. [Steam] ForceRelay disables direct (ICE) paths so Steam's relay network is used even on one LAN.
    /// </summary>
    internal static class SteamProbe
    {
        public static string Status = "";
        static bool _on, _started; static double _t0;
        static HSteamListenSocket _listen = HSteamListenSocket.Invalid; static HSteamNetConnection _conn = HSteamNetConnection.Invalid;
        static bool _connected, _isClient, _done;
        static Callback<SteamNetConnectionStatusChangedCallback_t> _cb;
        static readonly StringBuilder Report = new StringBuilder();
        static readonly List<double> Rtts = new List<double>();
        static int _seq, _stage; static double _bigStart, _stageAt;
        static string ReportPath { get { return Path.Combine(BepInEx.Paths.BepInExRootPath, "hfcoop-steam-probe.txt"); } }

        static void Say(string s)
        {
            Plugin.Log.LogInfo("[steam] " + s); Status = s;
            Report.AppendLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s);
            try { File.WriteAllText(ReportPath, Report.ToString()); } catch { }
        }

        public static void Toggle()
        {
            if (_on) { Stop("stopped"); return; }
            _on = true; _started = false; _done = false; _connected = false; _isClient = false; _seq = 0; _stage = 0; Rtts.Clear(); Report.Length = 0; _t0 = Time.realtimeSinceStartup;
        }

        public static void Stop(string why)
        {
            try
            {
                if (_conn.m_HSteamNetConnection != 0) SteamNetworkingSockets.CloseConnection(_conn, 0, why, false);
                if (_listen.m_HSteamListenSocket != 0) SteamNetworkingSockets.CloseListenSocket(_listen);
            }
            catch { }
            _conn = HSteamNetConnection.Invalid; _listen = HSteamListenSocket.Invalid; _connected = false; _on = false; Say(why);
        }

        public static void Tick()
        {
            if (!_on)
            {
                if (Cfg.SteamProbeAuto.Value && !_started && !_done && Time.realtimeSinceStartup > 20f) { _on = true; _t0 = Time.realtimeSinceStartup; }
                else return;
            }
            try
            {
                SteamAPI.RunCallbacks();
                if (!_started) { if (Time.realtimeSinceStartup - _t0 < 1f) return; Start(); return; }
                if (_conn.m_HSteamNetConnection != 0) Poll();
                if (_isClient && _connected && !_done) Drive();
            }
            catch (Exception e) { Say("probe failed: " + e.GetType().Name + ": " + e.Message); _on = false; _done = true; }
        }

        static void Start()
        {
            _started = true;
            if (!SteamAPI.IsSteamRunning()) { Say("Steam client is not running - Steam P2P impossible"); _on = false; _done = true; return; }
            ulong me; string name;
            try { me = SteamUser.GetSteamID().m_SteamID; name = SteamFriends.GetPersonaName(); }
            catch (Exception e)
            {
                Say("Steamworks not initialised by the game: " + e.Message + " - trying SteamAPI.Init()");
                if (!SteamAPI.Init()) { Say("SteamAPI.Init failed"); _on = false; _done = true; return; }
                me = SteamUser.GetSteamID().m_SteamID; name = SteamFriends.GetPersonaName();
            }
            Say("Steam user '" + name + "' SteamID64=" + me + " appid=" + SteamUtils.GetAppID());
            if (Cfg.SteamForceRelay.Value)
            {
                var p = Marshal.AllocHGlobal(4); Marshal.WriteInt32(p, 0);
                bool ok = SteamNetworkingUtils.SetConfigValue(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, p);
                Marshal.FreeHGlobal(p); Say("ICE (direct paths) disabled, relay only: " + ok);
            }
            SteamNetworkingUtils.InitRelayNetworkAccess();
            SteamRelayNetworkStatus_t rs; var av = SteamNetworkingUtils.GetRelayNetworkStatus(out rs);
            Say("relay network status: " + av + " " + rs.m_debugMsg);
            _cb = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatus);
            string target = Cfg.SteamProbeTarget.Value.Trim();
            if (target.Length == 0)
            {
                _listen = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, null);
                Say("LISTENING on virtual port 0. Give this SteamID64 to the other machine: " + me);
            }
            else
            {
                _isClient = true;
                var id = new SteamNetworkingIdentity(); id.SetSteamID64(ulong.Parse(target));
                _conn = SteamNetworkingSockets.ConnectP2P(ref id, 0, 0, null);
                Say("CONNECTING to " + target + " (conn " + _conn.m_HSteamNetConnection + ")");
            }
        }

        static void OnStatus(SteamNetConnectionStatusChangedCallback_t c)
        {
            var info = c.m_info;
            Say("connection state " + c.m_eOldState + " -> " + info.m_eState + (info.m_eEndReason != 0 ? " reason " + info.m_eEndReason + " '" + info.m_szEndDebug + "'" : ""));
            if (info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting && !_isClient)
            {
                _conn = c.m_hConn; var r = SteamNetworkingSockets.AcceptConnection(c.m_hConn);
                Say("accepted incoming connection from " + info.m_identityRemote.GetSteamID64() + ": " + r);
            }
            else if (info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
            {
                _connected = true; _stage = 0; _stageAt = Time.realtimeSinceStartup + 1.0; Describe();
            }
            else if (info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer || info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
            {
                _connected = false; if (_isClient && !_done) { _done = true; Say("connection ended before the test finished"); }
            }
        }

        static void Describe()
        {
            SteamNetConnectionInfo_t ci; SteamNetworkingSockets.GetConnectionInfo(_conn, out ci);
            string det; SteamNetworkingSockets.GetDetailedConnectionStatus(_conn, out det, 4096);
            Say("CONNECTED: " + ci.m_szConnectionDescription + " | POP remote=" + ci.m_idPOPRemote + " relay=" + ci.m_idPOPRelay);
            if (!string.IsNullOrEmpty(det)) foreach (var l in det.Split('\n')) if (l.Trim().Length > 0) Say("  " + l.Trim());
        }

        static void Send(byte[] data)
        {
            var p = Marshal.AllocHGlobal(data.Length);
            try { Marshal.Copy(data, 0, p, data.Length); long n; SteamNetworkingSockets.SendMessageToConnection(_conn, p, (uint)data.Length, 8 /* reliable */, out n); }
            finally { Marshal.FreeHGlobal(p); }
        }

        static void Poll()
        {
            var msgs = new IntPtr[16];
            int n = SteamNetworkingSockets.ReceiveMessagesOnConnection(_conn, msgs, msgs.Length);
            for (int i = 0; i < n; i++)
            {
                var m = SteamNetworkingMessage_t.FromIntPtr(msgs[i]);
                var buf = new byte[m.m_cbSize]; Marshal.Copy(m.m_pData, buf, 0, m.m_cbSize);
                SteamNetworkingMessage_t.Release(msgs[i]);
                if (!_isClient) { Send(buf); continue; } // the listener echoes everything back
                if (buf.Length >= 13 && buf[0] == (byte)'P') Rtts.Add((DateTime.UtcNow.Ticks - BitConverter.ToInt64(buf, 5)) / 10000.0);
                else if (buf.Length >= 1 && buf[0] == (byte)'B')
                    Say("echo of " + buf.Length + " bytes received after " + ((Time.realtimeSinceStartup - _bigStart) * 1000.0).ToString("0") + " ms round trip");
            }
        }

        static void Drive()
        {
            if (Time.realtimeSinceStartup < _stageAt) return;
            if (_stage == 0)
            {
                // 30 small pings, one every 200 ms
                var b = new byte[64]; b[0] = (byte)'P';
                Array.Copy(BitConverter.GetBytes(_seq++), 0, b, 1, 4); Array.Copy(BitConverter.GetBytes(DateTime.UtcNow.Ticks), 0, b, 5, 8);
                Send(b); _stageAt = Time.realtimeSinceStartup + 0.2;
                if (_seq >= 30) { _stage = 1; _stageAt = Time.realtimeSinceStartup + 2.0; }
            }
            else if (_stage == 1)
            {
                if (Rtts.Count > 0)
                {
                    var s = new List<double>(Rtts); s.Sort();
                    Say("pings: " + Rtts.Count + "/30 answered, RTT ms min " + s[0].ToString("0.0") + " median " + s[s.Count / 2].ToString("0.0") + " max " + s[s.Count - 1].ToString("0.0"));
                }
                else Say("pings: none answered");
                var big = new byte[200000]; new System.Random(1).NextBytes(big); big[0] = (byte)'B';
                _bigStart = Time.realtimeSinceStartup; Send(big); Say("sent a 200000-byte reliable message (about the size of a house snapshot)");
                _stage = 2; _stageAt = Time.realtimeSinceStartup + 8.0;
            }
            else if (_stage == 2)
            {
                var st = default(SteamNetConnectionRealTimeStatus_t); var lane = default(SteamNetConnectionRealTimeLaneStatus_t);
                SteamNetworkingSockets.GetConnectionRealTimeStatus(_conn, ref st, 0, ref lane);
                Say("steam's own status: ping " + st.m_nPing + " ms, local quality " + st.m_flConnectionQualityLocal.ToString("0.00") + ", remote quality " + st.m_flConnectionQualityRemote.ToString("0.00"));
                _done = true; Stop("PROBE FINISHED");
            }
        }
    }
}
