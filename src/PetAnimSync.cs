using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using FrozenWay.HouseFlipper.Pets;
using HarmonyLib;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Pet reactions that a player causes or that show an interaction: being petted, taking a snack, eating or drinking at a bowl, looking at a bowl. Pets walk on each player's own AI and only their
    /// position and stats were shared, so the other players saw the pet just stand there. The pet animation controller's own methods for these reactions are watched; when one runs on a player's game,
    /// the same call (same arguments) is made on the same pet on the others'. Calls made because of a received message are not sent on (no echo). Standing, sitting, lying and sleeping are left to
    /// each game's own AI: they run constantly and sending them would make the pets jitter.
    /// </summary>
    internal static class PetAnimSync
    {
        static readonly string[] Names = { "PlayPettedReaction", "PlayEatSnackAnim", "PlayEatAnimation", "PlayDrinkAnimation", "PlayAttentionNearBowlAction" };
        static Type _ctl;

        public static void Patch(Harmony h)
        {
            try
            {
                _ctl = typeof(Pet).Assembly.GetType("FrozenWay.HouseFlipper.Pets.Feature.PetAnimation.UniversalPetAnimController", false);
                if (_ctl == null) { Plugin.Log.LogWarning("[petanim] animation controller not found: pet reactions are not shared"); return; }
                foreach (var n in Names)
                {
                    // some of the reactions are abstract in the base controller: the patch goes on each concrete controller that implements them (cat, dog, farm animals...)
                    var targets = new List<MethodInfo>();
                    foreach (var t in typeof(Pet).Assembly.GetTypes())
                    {
                        if (!_ctl.IsAssignableFrom(t)) continue;
                        var m = t.GetMethod(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (m != null && !m.IsAbstract) targets.Add(m);
                    }
                    if (targets.Count == 0) { Plugin.Log.LogInfo("[petanim] method not in this game version: " + n); continue; }
                    int ok = 0;
                    foreach (var m in targets)
                    {
                        try { h.Patch(m, postfix: new HarmonyMethod(typeof(PetAnimSync), "Post")); ok++; }
                        catch (Exception e) { Plugin.Log.LogWarning("[petanim] could not watch " + m.DeclaringType.Name + "." + n + ": " + e.Message); }
                    }
                    Plugin.Log.LogInfo("[petanim] watching " + n + " on " + ok + "/" + targets.Count + " controller(s)");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[petanim] patch failed: " + e.Message); }
        }

        static string Enc(object v)
        {
            if (v == null) return "n:";
            if (v is bool) return "b:" + ((bool)v ? "1" : "0");
            if (v is float) return "f:" + ((float)v).ToString("R", CultureInfo.InvariantCulture);
            if (v.GetType().IsEnum) return "e:" + Convert.ToInt32(v, CultureInfo.InvariantCulture);
            if (v is int || v is short || v is byte) return "i:" + Convert.ToInt32(v, CultureInfo.InvariantCulture);
            return "n:";   // objects (a bowl...) cannot travel: the call is made without them
        }

        static object Dec(string s, Type t)
        {
            string v = s.Substring(2);
            if (t == typeof(bool)) return v == "1";
            if (t == typeof(float)) return float.Parse(v, CultureInfo.InvariantCulture);
            if (t.IsEnum) return Enum.ToObject(t, int.Parse(v, CultureInfo.InvariantCulture));
            if (t == typeof(int)) return int.Parse(v, CultureInfo.InvariantCulture);
            return null;
        }

        static void Post(object __instance, MethodBase __originalMethod, object[] __args)
        {
            if (!Session.Active || Events.Applying) return;
            try
            {
                var c = __instance as Component; if (c == null) return;
                var pet = c.GetComponentInParent<Pet>(); if (pet == null) return;
                string key = PetSync.KeyForPet(pet); if (key == null) return;
                var sb = new StringBuilder().Append(key).Append('|').Append(__originalMethod.Name);
                if (__args != null) foreach (var a in __args) sb.Append('|').Append(Enc(a));
                Events.Send("pet", "anim", sb.ToString());
            }
            catch (Exception e) { Plugin.Log.LogWarning("[petanim] could not send: " + e.Message); }
        }

        public static bool Apply(string payload)
        {
            try
            {
                var f = payload.Split('|'); if (f.Length < 2) return false;
                var pet = PetSync.PetByKey(f[0]); if (pet == null) return true;
                var ctl = pet.GetComponentInChildren(_ctl) as Component; if (ctl == null) return true;
                var m = _ctl.GetMethod(f[1], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); if (m == null) return true;
                var ps = m.GetParameters(); var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++) args[i] = i + 2 < f.Length ? Dec(f[i + 2], ps[i].ParameterType) : null;
                m.Invoke(ctl, args);
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[petanim] could not apply: " + (e.InnerException ?? e).Message); return false; }
        }
    }
}
