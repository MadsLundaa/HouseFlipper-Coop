using System;
using System.Linq;
using System.Reflection;
using FrozenWay.HouseFlipper.Farm.WeatherSystem;
using HarmonyLib;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Farm weather: the next weather (clear, clouds, rain...) is chosen with UnityEngine.Random on every player's own game, so after the first change each player had different weather
    /// (rain for one, sun for the other). The host's choice is sent and the clients use it instead of rolling their own; a client that rolls before the message arrives has its queued
    /// weather replaced as soon as the host's choice reaches it (the queued weather is held for minutes before it starts). The timing of the changes can still differ by the load-time offset.
    /// </summary>
    internal static class WeatherSync
    {
        static MethodInfo _pick;       // FarmWeatherController: () -> WeatherPreset, picks the next weather with Random.Range
        static FieldInfo _presets;     // WeatherPreset[] weatherPresets
        static FieldInfo _next;        // the field the pick is stored in (the weather queued after the current transition)
        static int _lastHost = -1;

        public static void Patch(Harmony h)
        {
            try
            {
                var ct = typeof(FarmWeatherController);
                _presets = ct.GetField("weatherPresets", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (_presets == null) { Plugin.Log.LogWarning("[weather] weatherPresets not found: weather is not synced"); return; }
                var rnd = typeof(UnityEngine.Random).GetMethod("Range", new[] { typeof(float), typeof(float) });
                foreach (var m in ct.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (m.ReturnType != typeof(WeatherPreset) || m.GetParameters().Length != 0 || m.IsSpecialName) continue;
                    var il = m.GetMethodBody() == null ? null : m.GetMethodBody().GetILAsByteArray(); if (il == null || !Calls(il, m.Module, rnd)) continue;
                    _pick = m; break;
                }
                if (_pick == null) { Plugin.Log.LogWarning("[weather] the random weather picker was not found: weather is not synced"); return; }
                // the field that receives the pick: "ldarg.0 ldarg.0 call pick stfld F" in whichever method calls it
                foreach (var m in ct.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    var body = m.GetMethodBody(); if (body == null) continue; var il = body.GetILAsByteArray();
                    for (int i = 0; i + 10 < il.Length; i++)
                        if (il[i] == 0x28 && BitConverter.ToInt32(il, i + 1) == _pick.MetadataToken && il[i + 5] == 0x7D)
                        {
                            try { _next = ct.Module.ResolveField(BitConverter.ToInt32(il, i + 6)); } catch { }
                            if (_next != null) break;
                        }
                    if (_next != null) break;
                }
                h.Patch(_pick, prefix: new HarmonyMethod(typeof(WeatherSync), "Pre"), postfix: new HarmonyMethod(typeof(WeatherSync), "Post"));
                Plugin.Log.LogInfo("[weather] picker " + _pick.Name + ", queue field " + (_next != null ? _next.Name : "not found"));
            }
            catch (Exception e) { Plugin.Log.LogWarning("[weather] patch failed: " + e.Message); }
        }

        // a call in this method's IL to the given method (tokens are per module, so each call token is resolved and compared)
        static bool Calls(byte[] il, Module mod, MethodInfo target)
        {
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;
                try { var mb = mod.ResolveMethod(BitConverter.ToInt32(il, i + 1)); if (mb != null && mb.Name == target.Name && mb.DeclaringType == target.DeclaringType && mb.GetParameters().Length == target.GetParameters().Length) return true; } catch { }
            }
            return false;
        }

        static int IndexOf(object ctl, WeatherPreset p)
        {
            var arr = _presets.GetValue(ctl) as WeatherPreset[]; return arr == null ? -1 : Array.IndexOf(arr, p);
        }

        // client: use the host's last choice instead of rolling
        static bool Pre(object __instance, ref WeatherPreset __result)
        {
            if (!Session.Active || Session.Role != Role.Client || _lastHost < 0) return true;
            var arr = _presets.GetValue(__instance) as WeatherPreset[]; if (arr == null || _lastHost >= arr.Length) return true;
            __result = arr[_lastHost]; return false;
        }

        // host: tell everybody what it picked
        static void Post(object __instance, WeatherPreset __result)
        {
            if (!Session.Active || Session.Role != Role.Host) return;
            int i = IndexOf(__instance, __result); if (i < 0) return;
            _lastHost = i; Events.Send("weather", "pick", i.ToString());
            Plugin.Log.LogInfo("[weather] next weather: " + __result.name);
        }

        /// <summary>Client: the host's pick arrived: queue it (replacing anything this client rolled for itself).</summary>
        public static bool Apply(string key, string json)
        {
            try
            {
                int i; if (!int.TryParse(json, out i)) return false; _lastHost = i;
                var ctl = UnityEngine.Object.FindObjectOfType<FarmWeatherController>(); if (ctl == null) return true;
                var arr = _presets.GetValue(ctl) as WeatherPreset[]; if (arr == null || i < 0 || i >= arr.Length) return true;
                if (_next != null) { _next.SetValue(ctl, arr[i]); Plugin.Log.LogInfo("[weather] next weather from the host: " + arr[i].name); }
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[weather] could not apply: " + e.Message); return false; }
        }

        /// <summary>Host: a client reported its house loaded: give it the weather that is queued.</summary>
        public static void SendTo(Conn c)
        {
            if (Session.Role != Role.Host || _next == null) return;
            try
            {
                var ctl = UnityEngine.Object.FindObjectOfType<FarmWeatherController>(); if (ctl == null) return;
                var nxt = _next.GetValue(ctl) as WeatherPreset; if (nxt == null) return; int i = IndexOf(ctl, nxt); if (i < 0) return;
                c.Send(new W(Msg.Event).S("weather").I(Session.MyId).S("pick").S(i.ToString()).Done());
            }
            catch (Exception e) { Plugin.Log.LogWarning("[weather] could not send the weather: " + e.Message); }
        }

        public static void Reset() { _lastHost = -1; }
    }
}
