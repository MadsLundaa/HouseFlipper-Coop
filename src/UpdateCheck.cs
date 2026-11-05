using System;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

namespace HFCoop
{
    /// <summary>
    /// Tells the player when a newer version of the mod has been released. One read-only request to GitHub's "latest release" endpoint a short while after the game started
    /// (nothing is sent but the request itself); the result only switches a plain "outdated" notice on. Nothing is downloaded or installed, no link is shown.
    /// Config: Update / CheckForUpdates.
    /// </summary>
    internal static class UpdateCheck
    {
        const string Url = "https://api.github.com/repos/MadsLundaa/HouseFlipper-Coop/releases/latest";
        static bool _started;
        public static volatile bool Outdated;

        /// <summary>Call from Update: starts the check once, a while after the game started.</summary>
        public static void Tick()
        {
            if (_started || !Cfg.CheckForUpdates.Value || UnityEngine.Time.realtimeSinceStartup < 20f) return;
            Run(Plugin.Version);
        }

        public static void Run(string current)
        {
            _started = true;
            new Thread(() =>
            {
                try
                {
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
                    var req = (HttpWebRequest)WebRequest.Create(Url);
                    req.UserAgent = "HFCoop-update-check"; req.Accept = "application/vnd.github+json"; req.Timeout = 10000;
                    string body;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var rd = new StreamReader(resp.GetResponseStream())) body = rd.ReadToEnd();
                    var m = Regex.Match(body, @"""tag_name""\s*:\s*""v?([0-9][0-9.]*)""");
                    Version latest, mine;
                    if (!m.Success || !Version.TryParse(m.Groups[1].Value, out latest) || !Version.TryParse(current, out mine)) { Plugin.Log.LogInfo("[update] could not read the latest version"); return; }
                    Outdated = latest > mine;
                    Plugin.Log.LogInfo("[update] this version " + mine + ", latest release " + latest + (Outdated ? ": outdated" : ": up to date"));
                }
                catch (Exception e) { Plugin.Log.LogInfo("[update] check skipped: " + e.Message); } // offline, blocked, rate limited: no notice, no fuss
            }) { IsBackground = true, Name = "HFCoop-update-check" }.Start();
        }
    }
}
