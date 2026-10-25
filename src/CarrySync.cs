using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Assets.Code.Player.Tools;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Shows an item moving while a player carries it (the normal item sync only sends the final placed state). The sender reports the held item's pose ~12 times a second;
    /// receivers glide the same item to it. The placement itself still goes through ItemSync, which sets the exact final state.
    /// </summary>
    internal static class CarrySync
    {
        static MoveObjectsTool _tool; static FieldInfo _held; static bool _looked; static double _nextSend, _nextLook;
        static string _lastId; static Vector3 _lastPos; static Quaternion _lastRot;

        class Target { public Item Item; public Vector3 Pos; public Quaternion Rot; public float Seen; }
        static readonly Dictionary<string, Target> Targets = new Dictionary<string, Target>();

        public static void Reset() { _tool = null; _lastId = null; Targets.Clear(); }

        static Item Held()
        {
            if (_tool == null)
            {
                if (Time.realtimeSinceStartup < _nextLook) return null;
                _nextLook = Time.realtimeSinceStartup + 2f;
                _tool = UnityEngine.Object.FindObjectOfType<MoveObjectsTool>();
                if (_tool == null) return null;
            }
            if (_held == null && !_looked)
            {
                _looked = true;
                foreach (var f in typeof(MoveObjectsTool).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    if (f.FieldType == typeof(Item)) { _held = f; break; } // the item currently picked up (the tool's only plain Item field)
                if (_held == null) Plugin.Log.LogWarning("[carry] held-item field not found - carried items are only synced when placed");
            }
            if (_held == null) return null;
            try { return _held.GetValue(_tool) as Item; } catch { return null; }
        }

        public static void Tick()
        {
            if (!Session.Active) { if (Targets.Count > 0) Targets.Clear(); return; }
            // receive side: glide followed items toward the last reported pose; stop following shortly after the reports stop
            if (Targets.Count > 0)
            {
                var done = new List<string>();
                float k = 1f - Mathf.Exp(-16f * Time.deltaTime);
                foreach (var kv in Targets)
                {
                    var t = kv.Value;
                    if (t.Item == null || Time.realtimeSinceStartup - t.Seen > 0.6f) { done.Add(kv.Key); continue; }
                    t.Item.transform.position = Vector3.Lerp(t.Item.transform.position, t.Pos, k);
                    t.Item.transform.rotation = Quaternion.Slerp(t.Item.transform.rotation, t.Rot, k);
                }
                foreach (var d in done) Targets.Remove(d);
            }
            // send side
            if (Time.realtimeSinceStartup < _nextSend) return;
            _nextSend = Time.realtimeSinceStartup + 0.08f;
            var it = Held();
            if (it == null) { _lastId = null; return; }
            var id = ItemSync.IdOf(it); if (id == null) return;
            var p = it.transform.position; var r = it.transform.rotation;
            if (id == _lastId && (p - _lastPos).sqrMagnitude < 1e-6f && Quaternion.Angle(r, _lastRot) < 0.2f) return;
            _lastId = id; _lastPos = p; _lastRot = r;
            Func<float, string> f = v => v.ToString("0.###", CultureInfo.InvariantCulture);
            Events.Send("carry", id, f(p.x) + "," + f(p.y) + "," + f(p.z) + "," + f(r.x) + "," + f(r.y) + "," + f(r.z) + "," + f(r.w));
        }

        public static bool Apply(string id, string json)
        {
            var it = ItemSync.ByNetId(id); if (it == null) return false;
            if (_tool != null && _held != null && ReferenceEquals(Held(), it)) return true; // we are carrying it ourselves: ours wins
            var s = json.Split(',');
            if (s.Length < 7) return false;
            Func<int, float> g = i => float.Parse(s[i], CultureInfo.InvariantCulture);
            Target t; if (!Targets.TryGetValue(id, out t)) { t = new Target { Item = it }; Targets[id] = t; }
            t.Item = it; t.Pos = new Vector3(g(0), g(1), g(2)); t.Rot = new Quaternion(g(3), g(4), g(5), g(6)); t.Seen = Time.realtimeSinceStartup;
            return true;
        }
    }
}
