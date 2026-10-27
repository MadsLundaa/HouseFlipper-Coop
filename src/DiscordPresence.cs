using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// The game's own Discord integration uses the native discord_game_sdk.dll (Rust). When Discord is closed, restarted or in an odd state that library panics
    /// inside a callback and takes the whole game down (seen when a co-op session ended: ActivityManager.ClearActivity -> RaiseException). The plugin therefore always
    /// switches that SDK off (its constructor is made to fail, which the game already handles by skipping Discord) and shows the presence itself, through Discord's
    /// documented local IPC pipe, written in managed code on a background thread: a broken Discord can only make a connect fail, never crash the game.
    /// </summary>
    internal static class DiscordPresence
    {
        const string ClientId = "502580882834587653"; // the game's own Discord application: shows "House Flipper" with its artwork
        static Thread _t; static volatile bool _stop;
        static readonly object L = new object();
        static string _details = "Renovating houses", _state = "Playing solo", _image = "", _text = "";
        static int _size, _max; static string _party = "";
        static readonly long Start = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        static double _nextTick;

        // ---- the native SDK must never run ----
        public static void Patch(Harmony h)
        {
            try
            {
                var d = AccessTools.TypeByName("Discord.Discord");
                if (d != null) foreach (var c in d.GetConstructors()) h.Patch(c, prefix: new HarmonyMethod(typeof(DiscordPresence), "BlockSdk"));
                var am = AccessTools.TypeByName("Discord.ActivityManager");
                if (am != null)
                    foreach (var name in new[] { "ClearActivity", "UpdateActivity" })
                        foreach (var m in am.GetMethods()) if (m.Name == name) h.Patch(m, prefix: new HarmonyMethod(typeof(DiscordPresence), "BlockCall"));
                Plugin.Log.LogInfo("[discord] the game's native Discord SDK is disabled (crash risk); the plugin shows the presence itself");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[discord] could not disable the game's Discord SDK: " + e.Message); }
        }

        static bool BlockSdk() { throw new InvalidOperationException("Discord SDK disabled by the co-op plugin"); } // the game's Init catches this and skips Discord
        static bool BlockCall() { return false; }

        // ---- our own presence ----
        public static void StartThread()
        {
            if (!Cfg.DiscordPresence.Value || _t != null) return;
            _t = new Thread(Run) { IsBackground = true, Name = "HFCoop-discord" }; _t.Start();
        }

        public static void Stop() { _stop = true; if (_t != null) _t.Join(1500); }

        /// <summary>Main thread: work out what to show (cheap, twice a second at most).</summary>
        public static void Tick()
        {
            if (Time.realtimeSinceStartup < _nextTick) return; _nextTick = Time.realtimeSinceStartup + 2f;
            string scene = Plugin.SceneName ?? "";
            string details, state; int size = 0, max = 0; string party = "";
            if (Session.Active)
            {
                details = "Co-op renovation";
                state = (Session.Role == Role.Host ? "Hosting" : "Joined a session") + " - " + Session.PlayerCount + (Session.PlayerCount == 1 ? " player" : " players");
                size = Session.PlayerCount; max = Mathf.Max(size, Cfg.MaxPlayers.Value); party = "hfcoop-" + Process.GetCurrentProcess().Id;
            }
            else { details = "Renovating houses"; state = "Playing solo"; }
            lock (L) { _details = details; _state = state; _image = scene.ToLowerInvariant(); _text = scene; _size = size; _max = max; _party = party; }
        }

        static string Esc(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "") { if (c == '"' || c == '\\') sb.Append('\\').Append(c); else if (c < 32) sb.Append(' '); else sb.Append(c); }
            return sb.ToString();
        }

        static string Build(bool clear)
        {
            string act = "";
            if (!clear)
                lock (L)
                {
                    act = ",\"activity\":{\"details\":\"" + Esc(_details) + "\",\"state\":\"" + Esc(_state) + "\",\"timestamps\":{\"start\":" + Start + "}" +
                        (_image.Length > 0 ? ",\"assets\":{\"large_image\":\"" + Esc(_image) + "\",\"large_text\":\"" + Esc(_text) + "\"}" : "") +
                        (_party.Length > 0 && _max >= _size && _size > 0 ? ",\"party\":{\"id\":\"" + Esc(_party) + "\",\"size\":[" + _size + "," + _max + "]}" : "") + "}";
                }
            return "{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":" + Process.GetCurrentProcess().Id + act + "},\"nonce\":\"" + Guid.NewGuid().ToString("N") + "\"}";
        }

        static void Send(Stream s, int op, string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var buf = new byte[8 + body.Length];
            BitConverter.GetBytes(op).CopyTo(buf, 0); BitConverter.GetBytes(body.Length).CopyTo(buf, 4); body.CopyTo(buf, 8);
            s.Write(buf, 0, buf.Length); s.Flush();
        }

        static bool ReadExactly(Stream s, byte[] buf, int n, int timeoutMs)
        {
            int got = 0; var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (got < n)
            {
                var ar = s.BeginRead(buf, got, n - got, null, null);
                int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0 || !ar.AsyncWaitHandle.WaitOne(left)) return false;
                int r = s.EndRead(ar); if (r <= 0) return false; got += r;
            }
            return true;
        }

        /// <summary>Reads one reply frame (and answers a ping); false = the pipe is dead or silent.</summary>
        static bool ReadFrame(Stream s, int timeoutMs, out int op, out string json)
        {
            op = -1; json = "";
            var head = new byte[8]; if (!ReadExactly(s, head, 8, timeoutMs)) return false;
            op = BitConverter.ToInt32(head, 0); int len = BitConverter.ToInt32(head, 4);
            if (len < 0 || len > 1 << 20) return false;
            var body = new byte[len]; if (len > 0 && !ReadExactly(s, body, len, timeoutMs)) return false;
            json = Encoding.UTF8.GetString(body);
            if (op == 3) Send(s, 4, json); // ping -> pong
            return op != 2; // 2 = Discord closed the connection
        }

        static NamedPipeClientStream Connect()
        {
            for (int i = 0; i < 10; i++)
            {
                NamedPipeClientStream p = null;
                try
                {
                    p = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                    p.Connect(150);
                    Send(p, 0, "{\"v\":1,\"client_id\":\"" + ClientId + "\"}");
                    int op; string json;
                    if (ReadFrame(p, 3000, out op, out json) && json.Contains("READY")) { Plugin.Log.LogInfo("[discord] presence connected (pipe " + i + ")"); return p; }
                }
                catch (Exception) { }
                try { if (p != null) p.Dispose(); } catch (Exception) { }
            }
            return null;
        }

        static void Run()
        {
            NamedPipeClientStream pipe = null; string last = ""; var lastSent = DateTime.MinValue;
            try
            {
                while (!_stop)
                {
                    try
                    {
                        if (pipe == null)
                        {
                            pipe = Connect();
                            if (pipe == null) { for (int i = 0; i < 60 && !_stop; i++) Thread.Sleep(250); continue; } // Discord not running: try again in 15 s
                            last = ""; lastSent = DateTime.MinValue;
                        }
                        string now; lock (L) { now = _details + "|" + _state + "|" + _image + "|" + _size + "/" + _max; }
                        if ((now != last && (DateTime.UtcNow - lastSent).TotalSeconds >= 4) || (DateTime.UtcNow - lastSent).TotalSeconds >= 120)
                        {
                            Send(pipe, 1, Build(false)); last = now; lastSent = DateTime.UtcNow;
                            int op; string json; if (!ReadFrame(pipe, 3000, out op, out json)) throw new IOException("no answer from Discord");
                        }
                    }
                    catch (Exception)
                    {
                        try { if (pipe != null) pipe.Dispose(); } catch (Exception) { }
                        pipe = null; for (int i = 0; i < 20 && !_stop; i++) Thread.Sleep(250);
                    }
                    Thread.Sleep(250);
                }
                if (pipe != null) { try { Send(pipe, 1, Build(true)); int op; string json; ReadFrame(pipe, 500, out op, out json); } catch (Exception) { } }
            }
            catch (Exception) { }
            finally { try { if (pipe != null) pipe.Dispose(); } catch (Exception) { } }
        }
    }
}
