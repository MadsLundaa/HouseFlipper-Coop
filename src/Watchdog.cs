using System.Diagnostics;
using System.Threading;

namespace HFCoop
{
    /// <summary>Background thread that reports when the Unity main thread stops ticking and in which plugin phase it last was (diagnostics for hangs).</summary>
    internal static class Watchdog
    {
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static long _lastBeat;
        static volatile string _phase = "init";
        static bool _started;

        public static void Beat(string phase) { _phase = phase; Interlocked.Exchange(ref _lastBeat, Clock.ElapsedMilliseconds); }
        public static void Phase(string phase) { _phase = phase; }

        static long _lastStall = -1000000;
        /// <summary>True when the main thread has not been blocked for 4 s or more during the last <paramref name="seconds"/> seconds (a PC busy loading a house blocks it repeatedly; a load that is merely hung does not).</summary>
        public static bool QuietFor(double seconds) { return Clock.ElapsedMilliseconds - Interlocked.Read(ref _lastStall) > seconds * 1000.0; }

        public static void Start()
        {
            if (_started) return; _started = true;
            Beat("start");
            new Thread(() =>
            {
                bool warned = false;
                while (true)
                {
                    Thread.Sleep(1000);
                    long age = Clock.ElapsedMilliseconds - Interlocked.Read(ref _lastBeat);
                    if (age > 4000) { Interlocked.Exchange(ref _lastStall, Clock.ElapsedMilliseconds); if (!warned || age % 5000 < 1000) Plugin.Log.LogWarning("[wd] main thread has not ticked for " + age / 1000 + "s, last plugin phase: " + _phase); warned = true; }
                    else warned = false;
                }
            }) { IsBackground = true, Name = "HFCoop-watchdog" }.Start();
        }
    }
}
