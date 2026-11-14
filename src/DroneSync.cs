using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Farm drone: a drone one player flies did not exist for the other player. The player whose drone is active sends its position five times a second; the other player keeps one extra drone
    /// (made with the game's own DroneManager.SpawnDrone, held still by the physics being switched off) and moves it there. When the updates stop for two seconds that drone is removed.
    /// </summary>
    internal static class DroneSync
    {
        static Type _droneT, _mgrT; static FieldInfo _active;
        static double _next; static Component _puppet; static double _lastRecv;
        static bool _resolved;

        public static void Reset() { if (_puppet != null) UnityEngine.Object.Destroy(_puppet.gameObject); _puppet = null; _next = 0; }

        static void Resolve()
        {
            if (_resolved) return; _resolved = true;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (_droneT == null) _droneT = a.GetType("FrozenWay.HouseFlipper.Farm.Drone.Drone", false); if (_mgrT == null) _mgrT = a.GetType("FrozenWay.HouseFlipper.Farm.Drone.DroneManager", false); } catch { }
            }
            if (_droneT != null) _active = _droneT.GetField("isActive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        static bool IsActive(Component d) { try { return _active != null && (bool)_active.GetValue(d); } catch { return false; } }

        public static void Tick()
        {
            if (!Session.Active || !ItemSync.Settled()) return;
            if (_puppet != null && Time.realtimeSinceStartup - _lastRecv > 2.0) { UnityEngine.Object.Destroy(_puppet.gameObject); _puppet = null; }
            if (Time.realtimeSinceStartup < _next) return; _next = Time.realtimeSinceStartup + 0.2;
            try
            {
                Resolve(); if (_droneT == null) return;
                foreach (var o in UnityEngine.Object.FindObjectsOfType(_droneT))
                {
                    var d = o as Component; if (d == null || d == _puppet || !IsActive(d)) continue;
                    var t = d.transform;
                    Events.Send("drone", "pos", t.position.x.ToString("0.##", CultureInfo.InvariantCulture) + "," + t.position.y.ToString("0.##", CultureInfo.InvariantCulture) + "," + t.position.z.ToString("0.##", CultureInfo.InvariantCulture) + "," + t.eulerAngles.y.ToString("0", CultureInfo.InvariantCulture));
                    return;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[drone] tick failed: " + e.Message); }
        }

        public static bool Apply(string key, string json)
        {
            try
            {
                Resolve(); if (_droneT == null || _mgrT == null) return true;
                var f = json.Split(','); if (f.Length < 4) return false;
                var pos = new Vector3(float.Parse(f[0], CultureInfo.InvariantCulture), float.Parse(f[1], CultureInfo.InvariantCulture), float.Parse(f[2], CultureInfo.InvariantCulture));
                _lastRecv = Time.realtimeSinceStartup;
                if (_puppet == null)
                {
                    var mgr = UnityEngine.Object.FindObjectOfType(_mgrT); if (mgr == null) return true;
                    var spawn = _mgrT.GetMethod("SpawnDrone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _puppet = spawn.Invoke(mgr, null) as Component; if (_puppet == null) return true;
                    var rb = _puppet.GetComponent<Rigidbody>(); if (rb != null) { rb.isKinematic = true; rb.useGravity = false; }
                    Plugin.Log.LogInfo("[drone] showing the other player's drone");
                }
                _puppet.transform.position = pos; _puppet.transform.rotation = Quaternion.Euler(0, float.Parse(f[3], CultureInfo.InvariantCulture), 0);
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[drone] could not apply: " + (e.InnerException ?? e).Message); return false; }
        }
    }
}
