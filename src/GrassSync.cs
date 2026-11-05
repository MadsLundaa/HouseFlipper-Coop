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

        public static void DevSend() { _sendAt = Time.realtimeSinceStartup; }

        public static void Tick()
        {
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
