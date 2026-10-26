using System;
using System.Collections.Generic;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Lamps are switched on and off by the room's light switch, which only reaches the lamps its own room registered. A lamp or switch bought while connected
    /// is created on the other peer without that wiring (it only appears after a reconnect, which loads the house the normal way), so the switch did nothing there
    /// and the lamp state differed. Lamp states are replicated directly: each peer reports the lamps whose on/off state changed and the other peer sets the same state.
    /// </summary>
    internal static class LightSync
    {
        static readonly Dictionary<string, bool> Known = new Dictionary<string, bool>();
        static double _next; static bool _primed;

        public static void Reset() { Known.Clear(); _primed = false; }

        public static void Tick()
        {
            if (!Session.Active || !Snapshot.CanApplyState) return;
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 0.4f;
            try
            {
                foreach (var it in UnityEngine.Object.FindObjectsOfType<Item>())
                {
                    var li = it.lightInteractable; if (li == null) continue;
                    string id = ItemSync.NetIdPublic(it, false); if (id == null) continue;
                    bool on = li.IsOn(), old;
                    if (!Known.TryGetValue(id, out old))
                    {
                        // first time we see this lamp: only a lamp WE created tells the others its state (the copy made from someone else's lamp starts in its default state and must not overwrite the original)
                        Known[id] = on;
                        if (_primed && id.StartsWith("c:" + Session.MyId + ":")) Events.Send("light", id, on ? "1" : "0");
                        continue;
                    }
                    if (old == on) continue;
                    Known[id] = on;
                    Events.Send("light", id, on ? "1" : "0");
                }
                _primed = true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[light] scan failed: " + e.Message); }
        }

        public static bool Apply(string id, string json)
        {
            var it = ItemSync.ByNetId(id);
            if (it == null || it.lightInteractable == null) return false;
            bool on = json == "1";
            Known[id] = on;
            if (it.lightInteractable.IsOn() == on) return true;
            if (on) it.lightInteractable.TurnOn(); else it.lightInteractable.TurnOff();
            Plugin.Log.LogInfo("[light] " + it.name + " " + (on ? "on" : "off") + " (from another player)");
            return true;
        }
    }
}
