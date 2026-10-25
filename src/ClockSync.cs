using System;
using System.Globalization;
using System.Reflection;
using Code.GameManagers;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Keeps the in-game clock identical: the host broadcasts the game's "time played on this map" counter (the time of day is derived from it);
    /// clients overwrite their counter when it drifts. The counter class is obfuscated, so it is found by shape: the static GameManager property whose
    /// type has exactly two private floats, the second being the map-time counter (checked against its public getter).
    /// </summary>
    internal static class ClockSync
    {
        static object _clock; static FieldInfo _mapField; static PropertyInfo _mapGetter; static bool _looked; static double _next;

        static bool Find()
        {
            if (_clock != null) return true;
            if (_looked && Time.realtimeSinceStartup < _next) return false;
            _looked = true; _next = Time.realtimeSinceStartup + 5f;
            try
            {
                foreach (var p in typeof(GameManager).GetProperties(BindingFlags.Static | BindingFlags.Public))
                {
                    var t = p.PropertyType; if (t.IsPrimitive || t == typeof(string)) continue;
                    var floats = Array.FindAll(t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic), f => f.FieldType == typeof(float));
                    if (floats.Length != 2) continue;
                    object inst; try { inst = p.GetValue(null, null); } catch { continue; }
                    if (inst == null) continue;
                    foreach (var g in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                        if (g.PropertyType == typeof(float) && Math.Abs((float)g.GetValue(inst, null) - (float)floats[1].GetValue(inst)) < 0.001f) { _mapGetter = g; break; }
                    if (_mapGetter == null) continue;
                    _clock = inst; _mapField = floats[1];
                    Plugin.Log.LogInfo("[clock] found map-time counter " + t.Name + "." + _mapField.Name);
                    return true;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[clock] lookup failed: " + e.Message); }
            return false;
        }

        public static void Reset() { _clock = null; _mapGetter = null; _looked = false; }

        public static void Tick()
        {
            if (Time.realtimeSinceStartup < _next) return;
            if (!Find()) return;
            _next = Time.realtimeSinceStartup + 5f;
            if (Session.Role == Role.Host && Session.PlayerCount > 1)
                Events.Send("clock", "t", ((float)_mapField.GetValue(_clock)).ToString("R", CultureInfo.InvariantCulture));
        }

        public static bool Apply(string json)
        {
            if (Session.Role != Role.Client || !Find()) return false;
            float t = float.Parse(json, CultureInfo.InvariantCulture);
            float mine = (float)_mapField.GetValue(_clock);
            if (Math.Abs(t - mine) > 2f) _mapField.SetValue(_clock, t);
            return true;
        }
    }
}

namespace HFCoop
{
    /// <summary>
    /// Time of day (the sun and the PC clock) is DayNightController.currentTime, a different counter from the map timer above. The host broadcasts it and
    /// clients adopt it when it drifts (e.g. after the host sleeps). Bed sleeping on a client is blocked: it would only change the client's own sky.
    /// </summary>
    internal static class DaySync
    {
        static bool _warned; static Type _dnc; static FieldInfo _cur; static MethodInfo _override; static double _next;
        static ConstructorInfo _ctor; static FieldInfo _h, _m, _s;

        static object Controller()
        {
            try
            {
                if (_dnc == null)
                {
                    _dnc = typeof(Assets.Code.DayNightController);
                    _cur = _dnc.GetField("currentTime", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    _override = _dnc.GetMethod("OverrideCurrentTimeWith", new[] { typeof(int), typeof(int), typeof(int), typeof(int) });
                    var dt = typeof(Assets.Code.DayTime);
                    _h = dt.GetField("hour"); _m = dt.GetField("min"); _s = dt.GetField("sec");
                    _ctor = dt.GetConstructor(new[] { typeof(int), typeof(int), typeof(int), typeof(int) });
                }
                if (_cur == null) return null;
                foreach (var o in Resources.FindObjectsOfTypeAll(_dnc)) { var mb = o as Component; if (mb != null && mb.gameObject.scene.IsValid()) return o; }
                return null;
            }
            catch { return null; }
        }

        /// <summary>ManlySingleton&lt;DayNightController&gt;.Instance (its property name is obfuscated: take the static property of the singleton's own type).</summary>
        static object Instance()
        {
            for (var t = _dnc; t != null; t = t.BaseType)
                foreach (var p in t.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    if (p.PropertyType == _dnc) { try { var v = p.GetValue(null, null); if (v != null) return v; } catch { } }
            foreach (var f in _dnc.BaseType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                if (f.FieldType == _dnc) { var v = f.GetValue(null); if (v != null) return v; }
            return null;
        }

        static int Secs(object dt) { return (int)_h.GetValue(dt) * 3600 + (int)_m.GetValue(dt) * 60 + (int)_s.GetValue(dt); }

        public static void Tick()
        {
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 3f;
            if (Session.Role != Role.Host || Session.PlayerCount < 2) return;
            var c = Controller(); if (c == null) { if (!_warned) { _warned = true; Plugin.Log.LogWarning("[clock] day/night controller not found (field " + (_cur != null) + ", override " + (_override != null) + ")"); } return; }
            var dt = _cur.GetValue(c); if (dt == null) return;
            Events.Send("clock", "day", Secs(dt).ToString());
        }

        public static bool Apply(string json)
        {
            if (Session.Role != Role.Client) return false;
            var c = Controller(); if (c == null || _override == null) return false;
            int host = int.Parse(json), dt = _cur.GetValue(c) == null ? -1 : Secs(_cur.GetValue(c));
            int d = Math.Abs(host - dt); d = Math.Min(d, 86400 - d);
            if (dt >= 0 && d < 90) return true; // game minutes pass at the same speed everywhere: only fix real drift or sleep jumps
            _override.Invoke(c, new object[] { host / 3600, host % 3600 / 60, host % 60, 0 });
            Plugin.Log.LogInfo("[clock] time of day set to host's " + (host / 3600) + ":" + (host % 3600 / 60).ToString("00"));
            return true;
        }

        public static void PatchBed(HarmonyLib.Harmony h)
        {
            try
            {
                var dt = typeof(Assets.Code.DayTime);
                MethodInfo setter = null;
                foreach (var p in typeof(Assets.Code.DayNightController).GetProperties(BindingFlags.Instance | BindingFlags.Public))
                    if (p.PropertyType == dt && p.GetSetMethod() != null && p.GetGetMethod() != null) { setter = p.GetSetMethod(); break; }
                if (setter == null) { Plugin.Log.LogWarning("[sleep] time setter not found - client bed sleep is NOT blocked"); return; }
                byte[] tok = BitConverter.GetBytes(setter.MetadataToken);
                int n = 0;
                foreach (var m in typeof(Features.Farm.BedSleeping.SleepableBed).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    byte[] il; try { var b = m.GetMethodBody(); il = b == null ? null : b.GetILAsByteArray(); } catch { continue; }
                    if (il == null) continue;
                    for (int i = 0; i + 4 < il.Length; i++)
                        if ((il[i] == 0x28 || il[i] == 0x6F) && il[i + 1] == tok[0] && il[i + 2] == tok[1] && il[i + 3] == tok[2] && il[i + 4] == tok[3])
                        {
                            if (m.GetParameters().Length == 0 && m.ReturnType == typeof(void)) { h.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(DaySync), "BlockSleep")); n++; Plugin.Log.LogInfo("[sleep] guarded " + m.Name); }
                            break;
                        }
                }
                if (n == 0) Plugin.Log.LogWarning("[sleep] bed sleep method not found - client bed sleep is NOT blocked");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[sleep] guard failed: " + e.Message); }
        }

        static bool BlockSleep()
        {
            if (Session.Role != Role.Client || !Session.Active) return true;
            Plugin.Log.LogInfo("[sleep] blocked sleeping on the client - only the host sleeps");
            Session.Status = "Only the host can sleep";
            return false;
        }
    }
}
