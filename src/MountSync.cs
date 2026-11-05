using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Mountable devices (radiators, air conditioners...) are fitted part by part while the player is zoomed on them; the game records the device only when it is
    /// FULLY mounted (GameplayRecorder.OnDeviceFullyMountedForSavingSystem). The item sync moves the device onto the wall, but nothing carried "the fittings are on",
    /// so the other peer kept an unmounted device (open pipes, leaking, the job task never completing). On "fully mounted" we send the device's item id and the
    /// other peer mounts the same device instantly through the game's own MountInstantly (which also records it for the host's save).
    /// </summary>
    internal static class MountSync
    {
        static readonly List<MountableDevice> Pending = new List<MountableDevice>();

        public static void Patch(Harmony h)
        {
            var m = AccessTools.Method(typeof(GameplayRecorder), "OnDeviceFullyMountedForSavingSystem");
            if (m == null) { Plugin.Log.LogWarning("[mount] OnDeviceFullyMountedForSavingSystem not found - mounted devices are not synced"); return; }
            h.Patch(m, postfix: new HarmonyMethod(typeof(MountSync), "Post"));
        }

        static void Post(MountableDevice __0)
        {
            if (!Session.Active || Events.Applying || __0 == null) return;
            if (!Pending.Contains(__0)) Pending.Add(__0);
        }

        public static void Reset() { Pending.Clear(); Quiet.Clear(); _retry.Clear(); }

        // ---- water from a device we did not place ----
        // An unmounted device leaks: its MontageControl re-creates a cleanable puddle every 12 s once it is initialised. The player who places a device never
        // sees that (it is only initialised when they start mounting), but the copy created from another peer is initialised while it is built, so it leaked
        // straight away and left water only that peer could see. For a device created by the item sync that is not mounted we stop that loop and remove the puddle.
        sealed class QuietEntry { public MontageControl Mc; public double Until, Next; }
        static readonly List<QuietEntry> Quiet = new List<QuietEntry>();
        static System.Reflection.FieldInfo _flawField; static bool _flawLooked;

        public static void OnRemoteItem(Item item)
        {
            try
            {
                if (item == null) return;
                var md = item.GetComponentInChildren<MountableDevice>(); var mc = item.GetComponentInChildren<MontageControl>();
                if (md == null || mc == null || md.IsFullyMounted()) return;
                Quiet.Add(new QuietEntry { Mc = mc, Until = Time.realtimeSinceStartup + 25.0, Next = 0 });
            }
            catch (Exception e) { Plugin.Log.LogWarning("[mount] quiet setup failed: " + e.Message); }
        }

        static void QuietTick()
        {
            if (Quiet.Count == 0) return;
            double now = Time.realtimeSinceStartup;
            for (int i = Quiet.Count - 1; i >= 0; i--)
            {
                var q = Quiet[i];
                if (q.Mc == null || now > q.Until) { Quiet.RemoveAt(i); continue; }
                if (now < q.Next) continue;
                q.Next = now + (now - q.Until + 25.0 < 5.0 ? 0.0 : 1.0); // every frame while the device is new
                try
                {
                    q.Mc.StopAllCoroutines(); // the 12-second "create the leak again" loop
                    if (!_flawLooked)
                    {
                        _flawLooked = true;
                        foreach (var f in typeof(MontageControl).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
                            if (f.FieldType == typeof(GameObject)) { _flawField = f; break; } // the one private GameObject: the live puddle
                        if (_flawField == null) Plugin.Log.LogWarning("[mount] puddle field of MontageControl not found");
                    }
                    if (_flawField != null)
                    {
                        var flaw = _flawField.GetValue(q.Mc) as GameObject;
                        if (flaw != null) { UnityEngine.Object.Destroy(flaw); _flawField.SetValue(q.Mc, null); Plugin.Log.LogInfo("[mount] removed the leak puddle of a device placed by another player"); }
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning("[mount] quiet failed: " + e.Message); Quiet.RemoveAt(i); }
            }
        }

        /// <summary>Called right after ItemSync.Tick so that a device item created a moment ago reaches the other peer before its "mounted" message.</summary>
        public static void Tick()
        {
            QuietTick(); RetryTick();
            if (Pending.Count == 0) return;
            var list = new List<MountableDevice>(Pending); Pending.Clear();
            foreach (var d in list)
            {
                try
                {
                    if (d == null) continue;
                    var item = d.GetComponentInParent<Item>();
                    if (item == null)
                    {
                        // wall devices (electrical outlets, switches) are scene objects without an Item: identified by their unique scene path
                        string pid = "p:" + Inspector.Path(d.transform);
                        Events.Send("mount", pid, "");
                        if (Cfg.VerboseLog.Value) Plugin.Log.LogInfo("[mount] sent fully-mounted wall device " + pid);
                        continue;
                    }
                    string id = ItemSync.NetIdPublic(item, true); if (id == null) continue;
                    ItemSync.QueuePublic(item);
                    Events.Send("mount", id, "");
                    if (Cfg.VerboseLog.Value) Plugin.Log.LogInfo("[mount] sent fully-mounted device " + id);
                }
                catch (Exception e) { Plugin.Log.LogWarning("[mount] send failed: " + e.Message); }
            }
        }

        static MountableDevice ByPath(string path)
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<MountableDevice>())
                if (d.gameObject.scene.IsValid() && Inspector.Path(d.transform) == path) return d;
            return null;
        }

        static void MountDevice(MountableDevice md, string label)
        {
            if (md.IsFullyMounted()) return;
            if (RecSync.IsZoomed(md)) md.LaunchTweenBackToFppCameraCoroutine(); // somebody else finished it while the local player was zoomed on it
            md.EnsurePartsInitialized();
            md.MountInstantly(true);
            Plugin.Log.LogInfo("[mount] device " + label + " mounted by another player");
        }

        // a device that is not here yet (its item still being created) is retried for a while instead of the message being lost
        static readonly List<KeyValuePair<string, double>> _retry = new List<KeyValuePair<string, double>>();
        static double _nextRetry;

        static void RetryTick()
        {
            if (_retry.Count == 0 || Time.realtimeSinceStartup < _nextRetry) return;
            _nextRetry = Time.realtimeSinceStartup + 1.0;
            for (int i = _retry.Count - 1; i >= 0; i--)
            {
                var r = _retry[i];
                if (Time.realtimeSinceStartup > r.Value) { _retry.RemoveAt(i); Plugin.Log.LogWarning("[mount] gave up mounting " + r.Key); continue; }
                bool was = Events.Applying; Events.Applying = true;
                try { if (Apply(r.Key, true)) _retry.RemoveAt(i); } catch (Exception e) { Plugin.Log.LogWarning("[mount] retry failed: " + e.Message); _retry.RemoveAt(i); } finally { Events.Applying = was; }
            }
        }

        public static bool Apply(string id) { return Apply(id, false); }

        static bool Apply(string id, bool retrying)
        {
            if (id.StartsWith("p:"))
            {
                var wd = ByPath(id.Substring(2));
                if (wd == null) { if (!retrying) { Plugin.Log.LogInfo("[mount] wall device " + id + " is not here yet: trying again"); _retry.Add(new KeyValuePair<string, double>(id, Time.realtimeSinceStartup + 30.0)); } return false; }
                MountDevice(wd, id); return true;
            }
            var item = ItemSync.ByNetId(id);
            if (item == null) { if (!retrying) { Plugin.Log.LogInfo("[mount] device " + id + " is not here yet: trying again"); _retry.Add(new KeyValuePair<string, double>(id, Time.realtimeSinceStartup + 30.0)); } return false; }
            var md = item.GetComponentInChildren<MountableDevice>();
            if (md == null) { Plugin.Log.LogWarning("[mount] item " + id + " has no MountableDevice"); return false; }
            if (md.IsFullyMounted()) return true;
            // somebody else finished this device while the local player was zoomed on it: do not leave the player locked in the zoom camera
            if (RecSync.IsZoomed(md)) md.LaunchTweenBackToFppCameraCoroutine();
            md.EnsurePartsInitialized();
            md.MountInstantly(true);
            Plugin.Log.LogInfo("[mount] device " + id + " mounted by another player");
            return true;
        }
    }
}

