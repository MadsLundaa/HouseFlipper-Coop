using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace HFCoop
{
    /// <summary>Message type ids. Payload byte 0 of every frame.</summary>
    internal static class Msg
    {
        public const byte Hello = 1;      // c->h  proto, build, plugin, name
        public const byte Welcome = 2;    // h->c  id, hostName, scene, players
        public const byte Reject = 3;     // h->c  reason
        public const byte Ping = 4;
        public const byte Pong = 5;
        public const byte PlayerState = 6; // any   binary
        public const byte PlayerJoined = 7;
        public const byte PlayerLeft = 8;
        public const byte Event = 9;      // renovation event (JSON), relayed through host
        public const byte Snapshot = 10;  // h->c  full house recording JSON (late join)
        public const byte SnapshotReq = 11;
        public const byte Chat = 12;
        public const byte Bye = 13;
        public const byte NotReady = 14;   // h->c  host has no house loaded yet
    }

    /// <summary>A connection to one peer, whatever carries it (TCP or Steam P2P). Messages are whole byte[] frames.</summary>
    internal abstract class Conn
    {
        public const int MaxFrame = 64 * 1024 * 1024;
        public string Remote = "?";
        public int PlayerId = -1;
        public string Name = "?";
        public volatile bool Closed;
        public string CloseReason = "";
        public double LastRecv;
        public double LastPing;
        public bool Handshaken;
        public abstract void Send(byte[] payload);
        public abstract void Close(string reason);
    }

    /// <summary>One TCP connection with a reader and writer thread. Messages are length-prefixed frames.</summary>
    internal sealed class TcpConn : Conn
    {
        public readonly TcpClient Client;
        readonly NetworkStream _s;
        readonly Queue<byte[]> _out = new Queue<byte[]>();
        readonly object _lock = new object();
        readonly Action<Conn, byte[]> _onMsg;
        readonly Action<Conn> _onClose;
        readonly AutoResetEvent _wake = new AutoResetEvent(false);

        public TcpConn(TcpClient c, Action<Conn, byte[]> onMsg, Action<Conn> onClose)
        {
            Client = c; Client.NoDelay = true;
            Remote = c.Client.RemoteEndPoint != null ? c.Client.RemoteEndPoint.ToString() : "?";
            _s = c.GetStream(); _onMsg = onMsg; _onClose = onClose;
            new Thread(ReadLoop) { IsBackground = true, Name = "HFCoop-read" }.Start();
            new Thread(WriteLoop) { IsBackground = true, Name = "HFCoop-write" }.Start();
        }

        public override void Send(byte[] payload)
        {
            if (Closed) return;
            lock (_lock) { _out.Enqueue(payload); }
            _wake.Set();
        }

        public override void Close(string reason)
        {
            if (Closed) return;
            Closed = true; CloseReason = reason;
            try { Client.Close(); } catch { }
            _wake.Set();
            _onClose(this);
        }

        void ReadLoop()
        {
            var hdr = new byte[4];
            try
            {
                while (!Closed)
                {
                    ReadExact(hdr, 4);
                    int len = BitConverter.ToInt32(hdr, 0);
                    if (len <= 0 || len > MaxFrame) throw new IOException("bad frame length " + len);
                    var buf = new byte[len];
                    ReadExact(buf, len);
                    _onMsg(this, buf);
                }
            }
            catch (Exception e) { Close(Closed ? CloseReason : e.Message); }
        }

        void ReadExact(byte[] b, int n)
        {
            int o = 0;
            while (o < n)
            {
                int r = _s.Read(b, o, n - o);
                if (r <= 0) throw new IOException("connection closed by peer");
                o += r;
            }
        }

        void WriteLoop()
        {
            try
            {
                while (!Closed)
                {
                    byte[] m = null;
                    lock (_lock) { if (_out.Count > 0) m = _out.Dequeue(); }
                    if (m == null) { _wake.WaitOne(250); continue; }
                    var len = BitConverter.GetBytes(m.Length);
                    _s.Write(len, 0, 4);
                    _s.Write(m, 0, m.Length);
                }
            }
            catch (Exception e) { Close(e.Message); }
        }
    }

    /// <summary>Little helpers to build/parse frames.</summary>
    internal sealed class W
    {
        readonly MemoryStream _ms = new MemoryStream();
        readonly BinaryWriter _w;
        public W(byte type) { _w = new BinaryWriter(_ms, Encoding.UTF8); _w.Write(type); }
        public W B(byte v) { _w.Write(v); return this; }
        public W I(int v) { _w.Write(v); return this; }
        public W F(float v) { _w.Write(v); return this; }
        public W S(string v) { _w.Write(v ?? ""); return this; }
        public W Bytes(byte[] v) { _w.Write(v.Length); _w.Write(v); return this; }
        public byte[] Done() { _w.Flush(); return _ms.ToArray(); }
    }

    internal sealed class R
    {
        readonly BinaryReader _r;
        public readonly byte Type;
        public R(byte[] data) { _r = new BinaryReader(new MemoryStream(data), Encoding.UTF8); Type = _r.ReadByte(); }
        public byte B() { return _r.ReadByte(); }
        public int I() { return _r.ReadInt32(); }
        public float F() { return _r.ReadSingle(); }
        public string S() { return _r.ReadString(); }
        public byte[] Bytes() { int n = _r.ReadInt32(); return _r.ReadBytes(n); }
    }

    /// <summary>Accepts TCP connections on a background thread.</summary>
    internal sealed class Listener
    {
        TcpListener _l; Thread _t; volatile bool _stop;
        public readonly Queue<TcpClient> Pending = new Queue<TcpClient>();
        public void Start(int port)
        {
            _l = new TcpListener(IPAddress.Any, port);
            _l.Start();
            _t = new Thread(() =>
            {
                try { while (!_stop) { var c = _l.AcceptTcpClient(); lock (Pending) Pending.Enqueue(c); } }
                catch { }
            }) { IsBackground = true, Name = "HFCoop-accept" };
            _t.Start();
        }
        public void Stop() { _stop = true; try { _l.Stop(); } catch { } }
    }
}
