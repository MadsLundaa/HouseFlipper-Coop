using System;
using HarmonyLib;
using Moonlit.GrassMowing;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Grass cut by a mower, scythe or flamethrower lives in a mask texture (MowingArea) that the game saves under a texture id in the host's own cache folder, so a
    /// client could never load it and cuts made by another player were invisible. When somebody stops cutting (the recorder's "grass final cut" event) the mask is sent to
    /// the other players and put into their MowingArea, and the lawn progress is recounted from it. A late joiner gets the host's masks when it reports that its house is loaded.
    /// The whole mask is replaced: two players cutting at the same moment can overwrite each other's last strokes.
    /// </summary>
    internal static class GrassSync
    {
        static double _sendAt;

        public static void Patch(Harmony h)
        {
            try
            {
                var m = AccessTools.Method(typeof(GameplayRecorder), "OnGrassFinalCut");
                if (m == null) { Plugin.Log.LogWarning("[grass] OnGrassFinalCut not found: cut grass is not synced"); return; }
                h.Patch(m, postfix: new HarmonyMethod(typeof(GrassSync), "Post"));
                // the flamethrower (and the other cutting tools) end with GrassCutter.StopCutting: send then too, in case the recorder event does not come
                var stop = AccessTools.Method(typeof(GrassCutter), "StopCutting");
                if (stop != null) h.Patch(stop, postfix: new HarmonyMethod(typeof(GrassSync), "Post"));
            }
            catch (Exception e) { Plugin.Log.LogWarning("[grass] patch failed: " + e.Message); }
        }

        static void Post()
        {
            if (!Session.Active || Events.Applying) return;
            _sendAt = Time.realtimeSinceStartup + 0.7; // the mask has settled by then
        }

        /// <summary>Send the lawn shortly (after the hoe dug a tile here: the grass under it is gone).</summary>
        public static void SendSoon() { if (!Session.Active || Events.Applying) return; double at = Time.realtimeSinceStartup + 1.5; if (_sendAt == 0 || _sendAt > at) _sendAt = at; }

        public static void DevSend() { _sendAt = Time.realtimeSinceStartup; }

        // A soil tile rebuilt from another player's record (after they sowed it) is deleted first, and deleting a tile grows the grass back on that spot; the rebuilt tile does not cut it again.
        // The lawn as it was is kept before the delete and put back once the rebuilt tile has loaded.
        static Color32[] _keepStd, _keepSpec; static int _kw, _kh, _ksw, _ksh; static double _restoreAt, _restoreAgainAt;
        public static void KeepLawn()
        {
            try
            {
                if (_keepStd != null || _keepSpec != null) return;   // already holding the lawn from before the first rebuild
                var area = UnityEngine.Object.FindObjectOfType<MowingArea>(); if (area == null) return;
                var t = area.GetTexture() as Texture2D; if (t != null) { _keepStd = t.GetPixels32(); _kw = t.width; _kh = t.height; }
                try { var s = area.GetSpecialTexture() as Texture2D; if (s != null) { _keepSpec = s.GetPixels32(); _ksw = s.width; _ksh = s.height; } } catch { }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[grass] could not keep the lawn: " + e.Message); }
        }
        public static void RestoreLawnSoon() { if (_keepStd == null && _keepSpec == null) return; _restoreAt = Time.realtimeSinceStartup + 1.0; _restoreAgainAt = Time.realtimeSinceStartup + 3.0; }
        static void Put(Color32[] px, int w, int h, bool special, MowingArea area)
        {
            if (px == null) return; var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try { t.SetPixels32(px); t.Apply(); if (special) area.SetSpecialTexture(t); else area.SetTexture(t); }
            finally { UnityEngine.Object.Destroy(t); }
        }
        static void Restore(bool last)
        {
            try
            {
                var area = UnityEngine.Object.FindObjectOfType<MowingArea>();
                if (area != null) { Put(_keepStd, _kw, _kh, false, area); Put(_keepSpec, _ksw, _ksh, true, area); }
                if (last) { _keepStd = null; _keepSpec = null; Plugin.Log.LogInfo("[grass] lawn kept across a rebuilt soil tile"); }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[grass] could not put the lawn back: " + e.Message); _keepStd = null; _keepSpec = null; }
        }

        public static void Tick()
        {
            if (_restoreAt > 0 && Time.realtimeSinceStartup >= _restoreAt) { _restoreAt = 0; Restore(false); }
            if (_restoreAgainAt > 0 && Time.realtimeSinceStartup >= _restoreAgainAt) { _restoreAgainAt = 0; Restore(true); }
            if (_sendAt > 0 && Time.realtimeSinceStartup >= _sendAt) { _sendAt = 0; var msg = Build(); if (msg != null) Events.Send("grass", "mask", msg); }
        }

        static string Encode(Texture2D t)
        {
            if (t == null) return "";
            try { var png = t.EncodeToPNG(); return png == null ? "" : Convert.ToBase64String(png); }
            catch (Exception e) { Plugin.Log.LogWarning("[grass] could not encode a mask: " + e.Message); return ""; }
        }

        /// <summary>"standard|special" as base64 PNGs, null when there is no lawn here.</summary>
        static string Build()
        {
            try
            {
                var area = UnityEngine.Object.FindObjectOfType<MowingArea>(); if (area == null) return null;
                string std = Encode(area.GetTexture());
                string spec = ""; try { spec = Encode(area.GetSpecialTexture()); } catch { }
                if (std.Length == 0 && spec.Length == 0) return null;
                if (std.Length + spec.Length > 6000000) { Plugin.Log.LogWarning("[grass] the lawn masks are too big to send (" + (std.Length + spec.Length) / 1000 + " KB)"); return null; }
                Plugin.Log.LogInfo("[grass] sending the lawn (" + (std.Length + spec.Length) / 1000 + " KB)");
                return std + "|" + spec;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[grass] could not build the lawn message: " + e.Message); return null; }
        }

        /// <summary>Host: a client reported its house loaded: give it the current lawn if any of it was cut.</summary>
        public static void SendTo(Conn c)
        {
            if (Session.Role != Role.Host) return;
            try
            {
                var h = Snapshot.Hsm; var rec = h != null && h.Recorder != null ? h.Recorder.currentRecording : null;
                if (rec == null || rec.mowableState == null) return;
                var msg = Build(); if (msg == null) return;
                c.Send(new W(Msg.Event).S("grass").I(Session.MyId).S("mask").S(msg).Done());
            }
            catch (Exception e) { Plugin.Log.LogWarning("[grass] could not send the lawn to the new player: " + e.Message); }
        }

        static void Load(string b64, bool special, MowingArea area)
        {
            if (string.IsNullOrEmpty(b64)) return;
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!t.LoadImage(Convert.FromBase64String(b64))) { Plugin.Log.LogWarning("[grass] could not read a lawn mask"); return; }
                if (special) area.SetSpecialTexture(t); else area.SetTexture(t);
            }
            finally { UnityEngine.Object.Destroy(t); }
        }

        public static bool Apply(string key, string json)
        {
            try
            {
                var area = UnityEngine.Object.FindObjectOfType<MowingArea>(); if (area == null) return false;
                _keepStd = null; _keepSpec = null; _restoreAt = 0; _restoreAgainAt = 0;   // the other player's lawn is newer than the one kept here
                var parts = json.Split('|');
                Load(parts[0], false, area);
                if (parts.Length > 1) Load(parts[1], true, area);
                var mm = UnityEngine.Object.FindObjectOfType<MowableManager>();
                if (mm != null) mm.CollectProgress(false); // recount the lawn progress from the new mask
                Plugin.Log.LogInfo("[grass] lawn updated by another player");
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[grass] could not apply the lawn: " + e.Message); return false; }
        }
    }
}
