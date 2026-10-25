using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Steamworks;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Optional transport: Steam P2P through ISteamNetworkingSockets (Steam Datagram Relay, no port forwarding). The session code only sees <see cref="Conn"/>s, so
    /// everything above the transport (handshake, events, snapshots) is identical to TCP. Messages are reliable and ordered; frames above ~480 KB are split.
    /// Both players must start the game through Steam (same app id) and be online.
    /// </summary>
    internal static class SteamTransport
    {
        public static Action<Conn, byte[]> OnMsg;
        public static Action<Conn> OnClose;
        public static readonly Queue<SteamConn> Incoming = new Queue<SteamConn>();
        internal static readonly Dictionary<uint, SteamConn> Conns = new Dictionary<uint, SteamConn>();

        static bool _avail, _init; static ulong _me; static double _nextCheck;
        static HSteamListenSocket _listen = HSteamListenSocket.Invalid;
        static Callback<SteamNetConnectionStatusChangedCallback_t> _cb;
        public static string LastError = "";

        /// <summary>True when the Steam client is running and the game initialised Steamworks (rechecked every few seconds until it is).</summary>
        public static bool Available
        {
            get
            {
                if (_avail) return true;
                if (Time.realtimeSinceStartup < _nextCheck) return false;
                _nextCheck = Time.realtimeSinceStartup + 5f;
                try
                {
                    if (!SteamAPI.IsSteamRunning()) { LastError = "Steam is not running"; return false; }
                    var id = SteamUser.GetSteamID();
                    if (!id.IsValid()) { LastError = "not logged in to Steam"; return false; }
                    _me = id.m_SteamID; _avail = true; LastError = "";
                }
                catch (Exception e) { LastError = "Steamworks not initialised: " + e.Message; return false; }
                return _avail;
            }
        }

        public static ulong MyId { get { return Available ? _me : 0UL; } }
        public static bool Listening { get { return _listen.m_HSteamListenSocket != 0; } }
        public static bool InUse { get { return _init && (Listening || Conns.Count > 0); } }

        static void EnsureInit()
        {
            if (_init) return;
            SteamNetworkingUtils.InitRelayNetworkAccess();
            _cb = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatus);
            _init = true;
        }

        // ---- host ----
        public static string StartListen()
        {
            if (!Available) return LastError;
            try
            {
                EnsureInit();
                if (Listening) return null;
                _listen = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, null);
                if (!Listening) return "could not open the Steam listen socket";
                SetPresence(true);
                Plugin.Log.LogInfo("[steam] listening for Steam connections as " + _me);
                return null;
            }
            catch (Exception e) { return e.Message; }
        }

        public static void StopListen()
        {
            try { if (Listening) SteamNetworkingSockets.CloseListenSocket(_listen); } catch { }
            _listen = HSteamListenSocket.Invalid;
            SetPresence(false);
        }

        // ---- client ----
        public static SteamConn Connect(ulong steamId)
        {
            if (!Available) return null;
            EnsureInit();
            var ident = new SteamNetworkingIdentity(); ident.SetSteamID64(steamId);
            var h = SteamNetworkingSockets.ConnectP2P(ref ident, 0, 0, null);
            if (h.m_HSteamNetConnection == 0) return null;
            var sc = new SteamConn(h, false) { Remote = "steam:" + steamId };
            lock (Conns) Conns[h.m_HSteamNetConnection] = sc;
            Plugin.Log.LogInfo("[steam] connecting to " + steamId);
            return sc;
        }

        // ---- per-frame ----
        public static void Tick()
        {
            if (!_init) return;
            try { SteamAPI.RunCallbacks(); } catch { }
            List<SteamConn> all; lock (Conns) all = new List<SteamConn>(Conns.Values);
            foreach (var c in all) c.Poll();
        }

        static void OnStatus(SteamNetConnectionStatusChangedCallback_t c)
        {
            var info = c.m_info; var st = info.m_eState; uint key = c.m_hConn.m_HSteamNetConnection;
            SteamConn sc; lock (Conns) Conns.TryGetValue(key, out sc);
            if (st == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting && sc == null
                && info.m_hListenSocket.m_HSteamListenSocket != 0 && info.m_hListenSocket.m_HSteamListenSocket == _listen.m_HSteamListenSocket)
            {
                if (SteamNetworkingSockets.AcceptConnection(c.m_hConn) == EResult.k_EResultOK)
                {
                    sc = new SteamConn(c.m_hConn, true) { Remote = "steam:" + info.m_identityRemote.GetSteamID64() };
                    lock (Conns) Conns[key] = sc;
                }
                else SteamNetworkingSockets.CloseConnection(c.m_hConn, 0, "accept failed", false);
            }
            else if (st == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected && sc != null)
            {
                sc.State = 1; Plugin.Log.LogInfo("[steam] connected: " + info.m_szConnectionDescription);
                if (sc.IsIncoming) lock (Incoming) Incoming.Enqueue(sc);
            }
            else if ((st == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer || st == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally))
            {
                string why = st == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer ? "closed by peer" : Describe(info.m_eEndReason, info.m_szEndDebug);
                if (sc != null) sc.Failed(why);
                else try { SteamNetworkingSockets.CloseConnection(c.m_hConn, 0, "", false); } catch { }
            }
        }

        static string Describe(int reason, string debug)
        {
            if (reason == 5003) return "Steam could not reach the host (timed out) - is the host online and running the game through Steam?";
            return "Steam connection problem " + reason + (string.IsNullOrEmpty(debug) ? "" : " (" + debug + ")");
        }

        internal static void Forget(SteamConn c) { lock (Conns) Conns.Remove(c.H.m_HSteamNetConnection); }

        // ---- friends / presence ----
        public sealed class FriendInfo { public ulong Id; public string Name; public bool Hosting; public bool InGame; }

        public static void SetPresence(bool hosting)
        {
            try { if (hosting) SteamFriends.SetRichPresence("hfcoop", "host"); else SteamFriends.SetRichPresence("hfcoop", null); }
            catch { }
        }

        /// <summary>Steam friends that are online, hosting ones first. Hosting = their rich presence says so.</summary>
        public static List<FriendInfo> Friends()
        {
            var list = new List<FriendInfo>();
            if (!Available) return list;
            try
            {
                int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
                for (int i = 0; i < n; i++)
                {
                    var id = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                    if (SteamFriends.GetFriendPersonaState(id) == EPersonaState.k_EPersonaStateOffline) continue;
                    var f = new FriendInfo { Id = id.m_SteamID, Name = SteamFriends.GetFriendPersonaName(id) };
                    f.Hosting = SteamFriends.GetFriendRichPresence(id, "hfcoop") == "host";
                    FriendGameInfo_t gi; f.InGame = SteamFriends.GetFriendGamePlayed(id, out gi) && gi.m_gameID.AppID().m_AppId == SteamUtils.GetAppID().m_AppId;
                    list.Add(f);
                }
                list.Sort((a, b) => a.Hosting != b.Hosting ? (a.Hosting ? -1 : 1) : (a.InGame != b.InGame ? (a.InGame ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception e) { LastError = e.Message; }
            return list;
        }

        /// <summary>Accepts "steam:7656..." or a bare 17-digit SteamID64.</summary>
        public static bool TryParseTarget(string s, out ulong id)
        {
            id = 0; if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim(); if (s.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) s = s.Substring(6).Trim();
            return s.Length >= 16 && s.Length <= 20 && ulong.TryParse(s, out id) && id > 76561197960265728UL;
        }
    }

    /// <summary>One Steam P2P connection (reliable, ordered). Reassembles split frames and delivers whole frames to the session.</summary>
    internal sealed class SteamConn : Conn
    {
        // Steam's limit is 512 KB per message; [Steam] MaxChunkKB can lower it (testing the split/reassembly path)
        static int Chunk { get { return Math.Max(2, Math.Min(480, Cfg.SteamChunkKB.Value)) * 1024; } }
        internal readonly HSteamNetConnection H;
        public readonly bool IsIncoming;
        public volatile int State;             // 0 connecting, 1 connected, 2 failed before it connected
        public string FailReason = "";
        MemoryStream _asm;

        public SteamConn(HSteamNetConnection h, bool incoming) { H = h; IsIncoming = incoming; }

        public override void Send(byte[] payload)
        {
            if (Closed) return;
            try
            {
                if (payload.Length + 1 <= Chunk) { Put(0, payload, 0, payload.Length); return; }
                for (int off = 0; off < payload.Length; off += Chunk - 1)
                {
                    int n = Math.Min(Chunk - 1, payload.Length - off);
                    Put((byte)(off + n >= payload.Length ? 2 : 1), payload, off, n);
                }
            }
            catch (Exception e) { Close("send failed: " + e.Message); }
        }

        void Put(byte kind, byte[] data, int off, int n)
        {
            var p = Marshal.AllocHGlobal(n + 1);
            try
            {
                Marshal.WriteByte(p, kind); Marshal.Copy(data, off, p + 1, n);
                long num; var r = SteamNetworkingSockets.SendMessageToConnection(H, p, (uint)(n + 1), 9 /* reliable + no nagle */, out num);
                if (r != EResult.k_EResultOK && r != EResult.k_EResultLimitExceeded) throw new IOException("Steam send result " + r);
                if (r == EResult.k_EResultLimitExceeded) throw new IOException("Steam send buffer full");
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        internal void Poll()
        {
            if (Closed || State != 1) return;
            var msgs = new IntPtr[32];
            int n = SteamNetworkingSockets.ReceiveMessagesOnConnection(H, msgs, msgs.Length);
            for (int i = 0; i < n; i++)
            {
                var m = SteamNetworkingMessage_t.FromIntPtr(msgs[i]);
                var buf = new byte[m.m_cbSize]; Marshal.Copy(m.m_pData, buf, 0, m.m_cbSize);
                SteamNetworkingMessage_t.Release(msgs[i]);
                if (buf.Length < 1) continue;
                byte kind = buf[0];
                if (kind == 0) { Deliver(buf, 1, buf.Length - 1); continue; }
                if (_asm == null) _asm = new MemoryStream();
                _asm.Write(buf, 1, buf.Length - 1);
                if (_asm.Length > MaxFrame) { Close("frame too large"); return; }
                if (kind == 2) { var all = _asm.ToArray(); _asm = null; Deliver(all, 0, all.Length); }
            }
        }

        void Deliver(byte[] b, int off, int n)
        {
            var d = new byte[n]; Buffer.BlockCopy(b, off, d, 0, n);
            if (SteamTransport.OnMsg != null) SteamTransport.OnMsg(this, d);
        }

        /// <summary>Steam told us the connection is gone.</summary>
        internal void Failed(string why)
        {
            if (State == 0) { State = 2; FailReason = why; }
            Release();
            if (!Closed) { Closed = true; CloseReason = why; if (SteamTransport.OnClose != null) SteamTransport.OnClose(this); }
        }

        void Release()
        {
            SteamTransport.Forget(this);
            try { SteamNetworkingSockets.CloseConnection(H, 0, "", false); } catch { }
        }

        public override void Close(string reason)
        {
            if (Closed) return;
            Closed = true; CloseReason = reason;
            SteamTransport.Forget(this);
            try { SteamNetworkingSockets.CloseConnection(H, 0, reason, true); } catch { } // linger: a queued Bye still goes out
            if (SteamTransport.OnClose != null) SteamTransport.OnClose(this);
        }
    }
}
