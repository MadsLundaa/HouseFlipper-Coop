using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using Assets.Code.Player;
using UnityEngine;

namespace HFCoop
{
    internal enum Role { None, Host, Client }

    internal sealed class RemotePlayer
    {
        public int Id; public string Name = "?"; public string Scene = "";
        public Vector3 Pos, TargetPos; public float Yaw, TargetYaw, Pitch, TargetPitch;
        public string Tool = ""; public bool Using;
        public double LastState;
        public GameObject Go; public Transform Head;
    }

    /// <summary>Host-authoritative session: owns connections, the player list and message dispatch.</summary>
    internal static class Session
    {
        public const int Protocol = 4; // 4: the player state also carries "is using the tool"
        public static Role Role = Role.None;
        public static string Status = "Offline";
        public static string LastError = "";
        public static int MyId = -1;
        public static readonly Dictionary<int, RemotePlayer> Players = new Dictionary<int, RemotePlayer>();
        public static string HostName = "";

        static readonly Stopwatch Clock = Stopwatch.StartNew();
        public static double Now { get { return Clock.Elapsed.TotalSeconds; } }

        sealed class In { public Conn C; public byte[] D; }
        static readonly Queue<In> Inbox = new Queue<In>();
        static readonly Queue<Conn> Closed = new Queue<Conn>();
        static Listener _listener;
        static readonly List<Conn> _conns = new List<Conn>(); // host: all clients
        static Conn _hostConn;                                  // client: connection to host
        static volatile Conn _connectResult; static volatile string _connectError; static volatile bool _connecting;
        static int _nextId = 1;
        static double _lastSend;

        public static bool Active { get { return Role != Role.None; } }
        public static int PlayerCount { get { return Players.Count + 1; } }

        // -------- lifecycle ---------
        public static void StartHost() { StartHost(Cfg.SteamHost.Value); }

        /// <summary>Host over TCP (always) and, when asked, also over Steam P2P so friends can join without port forwarding.</summary>
        public static void StartHost(bool viaSteam)
        {
            if (Active) return;
            _rc = null;
            try
            {
                _listener = new Listener(); _listener.Start(Cfg.Port.Value);
            }
            catch (Exception e) { Fail("Cannot host on port " + Cfg.Port.Value + ": " + e.Message); return; }
            Role = Role.Host; MyId = 0; _nextId = 1; HostName = Cfg.PlayerName.Value;
            Status = "Hosting on port " + Cfg.Port.Value; LastError = "";
            if (viaSteam)
            {
                SteamTransport.OnMsg = OnRaw; SteamTransport.OnClose = OnClosedRaw;
                string err = SteamTransport.StartListen();
                if (err != null) { LastError = "Steam hosting unavailable: " + err; Plugin.Log.LogWarning("[net] " + LastError); }
                else Status += " + Steam (ID " + SteamTransport.MyId + ")";
            }
            Events.OnSessionStart();
            Plugin.Log.LogInfo("[net] " + Status);
        }

        public static bool SteamHosting { get { return Role == Role.Host && SteamTransport.Listening; } }

        static SteamConn _steamOut; static double _steamDeadline;

        // -------- automatic reconnect (client) ---------
        // A client that loses its connection (network drop, host hiccup) keeps its house open and retries the same address for ReconnectSeconds. A successful
        // retry is a normal join (handshake + fresh snapshot), so everything that was missed during the outage is replaced by the host's current state.
        sealed class Reconnect { public string Addr, Why; public double Until, NextAt; public int Attempts; }
        static Reconnect _rc; static string _lastAddr; static bool _autoAttempt;
        public static bool Reconnecting { get { return _rc != null; } }
        public static string ReconnectText
        {
            get
            {
                if (_rc == null) return "";
                return "Connection lost - reconnecting to the host (attempt " + Math.Max(1, _rc.Attempts) + ", " + Math.Max(0, (int)(_rc.Until - Now)) + " s left)";
            }
        }

        static void BeginReconnect(string addr, string why)
        {
            _rc = new Reconnect { Addr = addr, Why = why, Until = Now + Cfg.ReconnectSeconds.Value, NextAt = Now + 2.0 };
            Plugin.Log.LogInfo("[net] connection lost (" + why + "); will try to reconnect to " + addr + " for " + Cfg.ReconnectSeconds.Value + " s");
        }

        /// <summary>Test helper (Ctrl+F12): drop every connection without saying goodbye, like a network failure would.</summary>
        public static void DebugDrop()
        {
            if (Role == Role.Host) foreach (var c in _conns.ToArray()) c.Close("connection dropped (test)");
            else if (Role == Role.Client && _hostConn != null) _hostConn.Close("connection dropped (test)");
        }

        public static void StartJoinSteam(ulong steamId)
        {
            if (Active || _connecting) return;
            if (!_autoAttempt) _rc = null; _lastAddr = "steam:" + steamId;
            LastError = "";
            SteamTransport.OnMsg = OnRaw; SteamTransport.OnClose = OnClosedRaw;
            if (!SteamTransport.Available) { Fail("Steam is not available: " + SteamTransport.LastError); return; }
            _steamOut = SteamTransport.Connect(steamId);
            if (_steamOut == null) { Fail("Could not start the Steam connection"); return; }
            _connecting = true; _connectError = null; _connectResult = null; _steamDeadline = Now + Cfg.ConnectTimeout.Value;
            Status = "Connecting via Steam to " + steamId + "...";
        }

        public static void StartJoin(string addr)
        {
            if (Active || _connecting) return;
            if (!_autoAttempt) _rc = null; _lastAddr = addr;
            ulong sid; if (SteamTransport.TryParseTarget(addr, out sid)) { StartJoinSteam(sid); return; }
            string host = addr; int port = Cfg.Port.Value;
            int c = addr.LastIndexOf(':');
            if (c > 0) { int p; if (int.TryParse(addr.Substring(c + 1), out p)) { port = p; host = addr.Substring(0, c); } }
            _connecting = true; _connectError = null; _connectResult = null; LastError = "";
            Status = "Connecting to " + host + ":" + port + "...";
            new Thread(() =>
            {
                try
                {
                    var tc = new TcpClient();
                    var ar = tc.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(Cfg.ConnectTimeout.Value * 1000)) { tc.Close(); throw new Exception("connection timed out"); }
                    tc.EndConnect(ar);
                    _connectResult = new TcpConn(tc, OnRaw, OnClosedRaw);
                }
                catch (Exception e) { _connectError = e.Message; }
            }) { IsBackground = true }.Start();
        }

        public static void Leave(string reason = "Disconnected")
        {
            _rc = null; // leaving on purpose (or the session ended for good): no reconnect
            try
            {
                if (Role == Role.Client && _hostConn != null) { _hostConn.Send(new W(Msg.Bye).Done()); Thread.Sleep(50); _hostConn.Close("left"); }
                if (Role == Role.Host) { foreach (var c in _conns.ToArray()) { c.Send(new W(Msg.Bye).Done()); } Thread.Sleep(50); foreach (var c in _conns.ToArray()) c.Close("host closed"); }
            }
            catch { }
            if (_listener != null) { _listener.Stop(); _listener = null; }
            if (_steamOut != null) { try { _steamOut.Close("left"); } catch { } _steamOut = null; }
            try { if (SteamTransport.Listening) SteamTransport.StopListen(); } catch { }
            _conns.Clear(); _hostConn = null; _connecting = false;
            foreach (var p in Players.Values) Avatars.Destroy(p);
            Players.Clear(); lock (Inbox) Inbox.Clear();
            Role = Role.None; MyId = -1; Status = reason;
            Plugin.Log.LogInfo("[net] session ended: " + reason);
            Events.OnSessionEnd();
        }

        static void Fail(string msg) { LastError = msg; Status = "Error"; Plugin.Log.LogWarning("[net] " + msg); }

        // -------- reader-thread callbacks ---------
        static void OnRaw(Conn c, byte[] d) { c.LastRecv = Now; lock (Inbox) Inbox.Enqueue(new In { C = c, D = d }); }
        static void OnClosedRaw(Conn c) { lock (Closed) Closed.Enqueue(c); }

        // -------- main-thread tick ---------
        public static void Tick()
        {
            if (_rc != null && !Active && !_connecting && Now >= _rc.NextAt)
            {
                if (Now > _rc.Until)
                {
                    LastError = "Could not reconnect to the host (" + _rc.Why + "). Use Join to try again."; Status = "Offline";
                    Plugin.Log.LogWarning("[net] gave up reconnecting after " + _rc.Attempts + " attempts"); _rc = null;
                }
                else
                {
                    _rc.Attempts++; _rc.NextAt = Now + Math.Min(10.0, 2.0 + _rc.Attempts * 2.0);
                    _autoAttempt = true; var keep = _rc;
                    try { StartJoin(_rc.Addr); } finally { _autoAttempt = false; }
                    _rc = keep; Status = ReconnectText;
                    Plugin.Log.LogInfo("[net] reconnect attempt " + keep.Attempts);
                }
            }
            if (_connecting && _steamOut != null)
            {
                SteamTransport.Tick(); // Steam callbacks drive the connection forward
                if (_steamOut.State == 1) { _connectResult = _steamOut; _steamOut = null; }
                else if (_steamOut.State == 2) { _connectError = _steamOut.FailReason; _steamOut = null; }
                else if (Now > _steamDeadline) { _connectError = "Steam connection timed out"; var t = _steamOut; _steamOut = null; t.Close("timed out"); }
            }
            if (_connecting)
            {
                if (_connectError != null)
                {
                    _connecting = false;
                    if (_rc != null) { Plugin.Log.LogInfo("[net] reconnect attempt failed: " + _connectError); Status = ReconnectText; }
                    else { Fail("Join failed: " + _connectError); Status = "Offline"; }
                }
                else if (_connectResult != null)
                {
                    _hostConn = _connectResult; _connectResult = null; _connecting = false;
                    _hostConn.LastRecv = Now; Role = Role.Client; Status = "Handshaking..."; Events.OnSessionStart();
                    _hostConn.Send(new W(Msg.Hello).I(Protocol).S(Plugin.GameBuild).S(Plugin.Version).S(Cfg.PlayerName.Value).Done());
                }
            }
            if (!Active) return;

            if (SteamTransport.InUse) SteamTransport.Tick(); // receive on Steam connections, run Steam callbacks
            if (Role == Role.Host && SteamTransport.Listening)
            {
                lock (SteamTransport.Incoming)
                    while (SteamTransport.Incoming.Count > 0)
                    {
                        var sc = SteamTransport.Incoming.Dequeue(); sc.LastRecv = Now; _conns.Add(sc);
                        Plugin.Log.LogInfo("[net] incoming Steam connection from " + sc.Remote);
                    }
            }

            if (Role == Role.Host && _listener != null)
            {
                lock (_listener.Pending)
                    while (_listener.Pending.Count > 0)
                    {
                        var tc = _listener.Pending.Dequeue();
                        var c = new TcpConn(tc, OnRaw, OnClosedRaw) { LastRecv = Now };
                        _conns.Add(c);
                        Plugin.Log.LogInfo("[net] incoming connection from " + c.Remote);
                    }
            }

            while (true)
            {
                In m; lock (Inbox) { if (Inbox.Count == 0) break; m = Inbox.Dequeue(); }
                try { Handle(m.C, m.D); }
                catch (Exception e) { Plugin.Log.LogError("[net] error handling message: " + e); }
            }

            while (true)
            {
                Conn c; lock (Closed) { if (Closed.Count == 0) break; c = Closed.Dequeue(); }
                OnClosed(c);
                if (!Active) return;
            }

            double now = Now;
            foreach (var c in (Role == Role.Host ? _conns.ToArray() : (_hostConn != null ? new[] { _hostConn } : new Conn[0])))
            {
                if (c.Closed) continue;
                if (now - c.LastRecv > Cfg.Timeout.Value) { c.Close("timed out"); continue; }
                if (now - c.LastPing > 2.0) { c.LastPing = now; c.Send(new W(Msg.Ping).Done()); }
            }

            if (Players.Count > 0 || Role == Role.Client) SendLocalState(now);
            Avatars.Update();
            Events.Tick();
        }

        static void OnClosed(Conn c)
        {
            if (Role == Role.Client && c == _hostConn)
            {
                string why = c.CloseReason;
                if (why == "left") return;
                bool rejected = !string.IsNullOrEmpty(LastError) || why.StartsWith("rejected");
                bool hostEnded = why == "remote said bye";
                string msg = !string.IsNullOrEmpty(LastError) ? LastError : (hostEnded ? "The host ended the session" : "Lost connection to host: " + why);
                var prev = _rc; string addr = _lastAddr; // a failed handshake during a reconnect keeps the reconnect going
                Leave(msg); LastError = msg;
                if (!rejected && !hostEnded && Cfg.AutoReconnect.Value && !string.IsNullOrEmpty(addr))
                {
                    if (prev != null) { _rc = prev; _rc.Why = why; Status = ReconnectText; LastError = ""; Plugin.Log.LogInfo("[net] reconnect handshake interrupted (" + why + "), still trying"); }
                    else { BeginReconnect(addr, why); Status = ReconnectText; LastError = ""; }
                }
                return;
            }
            if (Role == Role.Host && _conns.Remove(c) && c.Handshaken)
            {
                Plugin.Log.LogInfo("[net] player " + c.PlayerId + " (" + c.Name + ") left: " + c.CloseReason);
                RemotePlayer rp;
                if (Players.TryGetValue(c.PlayerId, out rp)) { Avatars.Destroy(rp); Players.Remove(c.PlayerId); }
                BroadcastExcept(null, new W(Msg.PlayerLeft).I(c.PlayerId).Done());
                Events.OnPlayerLeft(c.PlayerId);
            }
        }

        // -------- sending ---------
        public static void BroadcastExcept(Conn except, byte[] data)
        {
            foreach (var c in _conns) if (c != except && c.Handshaken && !c.Closed) c.Send(data);
        }

        /// <summary>Send to everyone else: host -> all clients; client -> host (who relays).</summary>
        public static void SendAll(byte[] data)
        {
            if (Role == Role.Host) BroadcastExcept(null, data);
            else if (Role == Role.Client && _hostConn != null && _hostConn.Handshaken) _hostConn.Send(data);
        }

        public static Conn ConnOf(int id) { foreach (var c in _conns) if (c.PlayerId == id) return c; return null; }

        static PlayerController _pc; static double _pcLook;
        static void SendLocalState(double now)
        {
            if (now - _lastSend < 1.0 / Cfg.SendRate.Value) return;
            _lastSend = now;
            if (_pc == null && now - _pcLook > 1.0) { _pcLook = now; _pc = UnityEngine.Object.FindObjectOfType<PlayerController>(); }
            if (_pc == null) return;
            if (Role == Role.Client && (_hostConn == null || !_hostConn.Handshaken)) return;
            var t = _pc.transform;
            float pitch = 0f;
            var cam = Camera.main;
            if (cam != null) { pitch = cam.transform.eulerAngles.x; if (pitch > 180) pitch -= 360; }
            var data = new W(Msg.PlayerState).I(MyId).S(Plugin.SceneName).F(t.position.x).F(t.position.y).F(t.position.z)
                .F(cam != null ? cam.transform.eulerAngles.y : t.eulerAngles.y).F(pitch).S(Plugin.CurrentToolName()).B(Plugin.LocalUsingTool() ? (byte)1 : (byte)0).Done();
            SendAll(data);
        }

        // -------- receiving ---------
        static void Handle(Conn c, byte[] d)
        {
            var r = new R(d);
            switch (r.Type)
            {
                case Msg.Hello: if (Role == Role.Host) OnHello(c, r); break;
                case Msg.Welcome: if (Role == Role.Client) OnWelcome(c, r); break;
                case Msg.Reject: LastError = "Host rejected connection: " + r.S(); Plugin.Log.LogWarning("[net] " + LastError); break; // Leave happens on close
                case Msg.Ping: c.Send(new W(Msg.Pong).Done()); break;
                case Msg.Pong: break;
                case Msg.Bye: c.Close("remote said bye"); break;
                case Msg.PlayerState: OnState(c, d, r); break;
                case Msg.PlayerJoined:
                    {
                        int id = r.I(); string n = r.S();
                        if (id != MyId && !Players.ContainsKey(id)) Players[id] = new RemotePlayer { Id = id, Name = n };
                        Plugin.Log.LogInfo("[net] player joined: " + n + " (#" + id + ")");
                        break;
                    }
                case Msg.PlayerLeft:
                    {
                        int id = r.I(); RemotePlayer rp;
                        if (Players.TryGetValue(id, out rp)) { Plugin.Log.LogInfo("[net] player left: " + rp.Name); Avatars.Destroy(rp); Players.Remove(id); }
                        break;
                    }
                default: Events.Handle(c, r.Type, r, d); break;
            }
        }

        static void OnHello(Conn c, R r)
        {
            int proto = r.I(); string build = r.S(), ver = r.S(), name = r.S();
            if (proto != Protocol) { Reject(c, "protocol mismatch (host " + Protocol + ", you " + proto + ") - update the mod"); return; }
            if (build != Plugin.GameBuild) { Reject(c, "game version mismatch (host " + Plugin.GameBuild + ", you " + build + ")"); return; }
            if (_conns.FindAll(x => x.Handshaken).Count >= Cfg.MaxPlayers.Value - 1) { Reject(c, "session is full"); return; }
            // the same player coming back after a network drop: the old connection is dead but may not have timed out yet - replace it right away
            string rk = RemoteKey(c.Remote);
            foreach (var o in _conns.ToArray())
                if (o != c && o.Handshaken && !o.Closed && o.Name == name && RemoteKey(o.Remote) == rk && Now - o.LastRecv > 1.5)
                { Plugin.Log.LogInfo("[net] " + o.Name + " reconnected: replacing the stale connection #" + o.PlayerId); o.Close("replaced by reconnect"); }
            c.Name = string.IsNullOrEmpty(name) ? "Player" : name; c.PlayerId = _nextId++; c.Handshaken = true;
            var w = new W(Msg.Welcome).I(c.PlayerId).S(Cfg.PlayerName.Value).S(Plugin.SceneName).I(Players.Count + 1);
            w.I(0).S(Cfg.PlayerName.Value);
            foreach (var p in Players.Values) if (p.Id != c.PlayerId) w.I(p.Id).S(p.Name);
            c.Send(w.Done());
            var rp = new RemotePlayer { Id = c.PlayerId, Name = c.Name };
            Players[c.PlayerId] = rp;
            BroadcastExcept(c, new W(Msg.PlayerJoined).I(c.PlayerId).S(c.Name).Done());
            Plugin.Log.LogInfo("[net] player " + c.Name + " (#" + c.PlayerId + ") joined from " + c.Remote + " plugin " + ver);
            Events.OnPlayerJoined(c);
        }

        /// <summary>Machine part of a connection's remote address: the IP for TCP ("1.2.3.4:5555"), the whole "steam:id" for Steam.</summary>
        static string RemoteKey(string remote)
        {
            if (remote.StartsWith("steam:")) return remote;
            int i = remote.LastIndexOf(':'); return i > 0 ? remote.Substring(0, i) : remote;
        }

        static void Reject(Conn c, string why)
        {
            Plugin.Log.LogWarning("[net] rejecting " + c.Remote + ": " + why);
            c.Send(new W(Msg.Reject).S(why).Done());
            var cc = c; new Thread(() => { Thread.Sleep(300); cc.Close("rejected: " + why); }) { IsBackground = true }.Start();
        }

        static void OnWelcome(Conn c, R r)
        {
            MyId = r.I(); HostName = r.S(); string scene = r.S(); int n = r.I();
            c.Handshaken = true; c.PlayerId = 0;
            if (_rc != null) { Plugin.Log.LogInfo("[net] reconnected after " + _rc.Attempts + " attempt(s)"); _rc = null; }
            for (int i = 0; i < n; i++) { int id = r.I(); string nm = r.S(); if (id != MyId) Players[id] = new RemotePlayer { Id = id, Name = nm }; }
            Status = "Connected to " + HostName;
            Plugin.Log.LogInfo("[net] connected, my id #" + MyId + ", host scene " + scene);
            Events.OnWelcome(scene);
        }

        static void OnState(Conn from, byte[] raw, R r)
        {
            int id = r.I(); if (id == MyId) return;
            string scene = r.S(); var pos = new Vector3(r.F(), r.F(), r.F()); float yaw = r.F(), pitch = r.F(); string tool = r.S(); bool usingTool = r.B() == 1;
            RemotePlayer p;
            if (!Players.TryGetValue(id, out p)) return;
            if (Role == Role.Host && from.PlayerId != id) return; // only accept own state from a client
            p.Scene = scene; p.TargetPos = pos; p.TargetYaw = yaw; p.TargetPitch = pitch; p.Tool = tool; p.Using = usingTool; p.LastState = Now;
            if (p.Go == null) p.Pos = pos;
            if (Role == Role.Host) BroadcastExcept(from, raw);
        }
    }
}

