using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using FrozenWay.HouseFlipper.Pets;
using HarmonyLib;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Which hen sits on which nest of a chicken coop. Each game's own hen AI picks the nests (ChickenCoop.ChangeStatus), so the players saw different hens in the coop, a hen on a nest on one side and
    /// in the yard on the other, and only the hen that holds a nest can lay there. The host decides: its nest assignment is sent when it changes (and every few seconds for a joiner), the other players
    /// apply it, and their own AI can no longer claim or leave a nest (their ChangeStatus calls are skipped). Hens placed on a nest by the host are also held at the host's position more tightly, so
    /// the hen visibly sits in the same nest. The eggs then come from the host's hens only and travel with the coop's state.
    /// </summary>
    internal static class CoopSync
    {
        const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static Type _coopT, _henT;
        static bool _applying;
        static double _next, _nextFull; static string _last;
        static readonly Dictionary<int, string[]> Wanted = new Dictionary<int, string[]>();   // coop index -> hen key per nest ("" = empty)
        static readonly HashSet<Pet> Nested = new HashSet<Pet>();

        public static void Reset() { Release(new HashSet<Pet>()); Wanted.Clear(); Nested.Clear(); _last = null; }

        // A hen on a nest is above the walking mesh: its navigation agent would pull it back to the ground under the nest, so it stops moving the hen while the host has it on a nest.
        static readonly HashSet<Pet> Held = new HashSet<Pet>();
        static void Release(HashSet<Pet> keep)
        {
            foreach (var p in Held.ToList())
            {
                if (p != null && keep.Contains(p)) continue;
                Held.Remove(p); if (p == null || PetMotion.IsDriven(p)) continue;   // a pet that follows the host's motion keeps its AI paused
                try
                {
                    PetAI.Resume(p);
                }
                catch (Exception e) { Plugin.Log.LogWarning("[coop] could not hand a hen back to its AI: " + e.Message); }
            }
        }
        static void Hold(Pet p)
        {
            if (!Held.Add(p)) return;
            // its own AI would walk it off the nest (it moves the hen from the navigation agent, which stays on the ground under the nest): both pause while the host has the hen on a nest
            PetAI.Pause(p);   // AI, the game's ground-follow helper (the host's game switches it off too while a hen is on a nest) and navigation
        }

        /// <summary>A hen that the host has on a nest (its position is followed closely).</summary>
        public static bool IsNested(Pet p) { return p != null && Nested.Contains(p); }

        // The game puts every pet back on the ground each frame (some script outside the AI and navigation does it), but a nest is above the ground: right before the frame is drawn, a hen that
        // the host has on a nest is put back where the host's hen sits.
        static bool _hooked;
        static void BeforeRender()
        {
            if (Nested.Count == 0 || !Session.Active || Session.Role != Role.Client) return;
            try
            {
                foreach (var p in Nested)
                {
                    KeyValuePair<Vector3, float> pose; if (p == null || !PetSync.HostPose.TryGetValue(p, out pose)) continue;
                    p.transform.position = pose.Key; p.transform.rotation = Quaternion.Euler(0, pose.Value, 0);
                }
            }
            catch { }
        }

        public static void Patch(Harmony h)
        {
            if (!_hooked) { _hooked = true; Application.onBeforeRender += BeforeRender; }
            try
            {
                _coopT = typeof(Pet).Assembly.GetType("Features.Farm.ChickenCoop.ChickenCoop", false);
                _henT = typeof(Pet).Assembly.GetType("FrozenWay.HouseFlipper.Farm.Animals.Chicken", false);
                if (_coopT == null || _henT == null) { Plugin.Log.LogInfo("[coop] chicken coop not in this game version"); return; }
                var m = _coopT.GetMethod("ChangeStatus", F);
                if (m == null) { Plugin.Log.LogWarning("[coop] ChangeStatus not found: nests are not shared"); return; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(CoopSync), "PreChange"));
                Plugin.Log.LogInfo("[coop] watching the chicken coop nests");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[coop] patch failed: " + e.Message); }
        }

        // on the other players the host owns the nests: their own hen AI may not take or leave one
        static bool PreChange() { return _applying || !Session.Active || Session.Role != Role.Client; }

        static Component[] Coops()
        {
            return UnityEngine.Object.FindObjectsOfType(_coopT).Cast<Component>().Where(c => c != null && c.gameObject.activeInHierarchy)
                .OrderBy(c => Mathf.Round(c.transform.position.x * 10)).ThenBy(c => Mathf.Round(c.transform.position.z * 10)).ToArray();
        }

        static Array Places(Component coop) { var f = _coopT.GetField("jumpPlaces", F); return f == null ? null : f.GetValue(coop) as Array; }
        static IDictionary Dict(Component coop) { var f = _coopT.GetField("jumpPlacesDict", F); return f == null ? null : f.GetValue(coop) as IDictionary; }

        static bool Alive(object o) { var u = o as UnityEngine.Object; return o != null && !(o is UnityEngine.Object && u == null); }

        public static void Tick()
        {
            if (!Session.Active || _coopT == null || !ItemSync.Settled()) return;
            double now = Time.realtimeSinceStartup;
            if (now < _next) return; _next = now + (Session.Role == Role.Client ? 0.25 : 0.5);
            try
            {
                if (Session.Role == Role.Client) { Enforce(); return; }
                var coops = Coops(); if (coops.Length == 0) return;
                var sb = new StringBuilder();
                for (int i = 0; i < coops.Length; i++)
                {
                    var places = Places(coops[i]); var d = Dict(coops[i]); if (places == null || d == null) continue;
                    sb.Append(i).Append(':');
                    for (int n = 0; n < places.Length; n++)
                    {
                        var pl = places.GetValue(n); object hen = d.Contains(pl) ? d[pl] : null; string key = "";
                        if (Alive(hen)) { var pet = (hen as Component).GetComponent<Pet>(); key = pet == null ? "" : PetSync.KeyForPet(pet) ?? ""; }
                        if (n > 0) sb.Append(','); sb.Append(key);
                    }
                    sb.Append(';');
                }
                string s = sb.ToString();
                if (s != _last || now >= _nextFull) { _last = s; _nextFull = now + 3.0; Events.Send("coop", "nests", s); }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[coop] tick failed: " + e.Message); }
        }

        public static bool Apply(string key, string json)
        {
            if (key != "nests" || _coopT == null) return false;
            try
            {
                Wanted.Clear();
                foreach (var rec in json.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int c = rec.IndexOf(':'); if (c < 0) continue;
                    Wanted[int.Parse(rec.Substring(0, c))] = rec.Substring(c + 1).Split(',');
                }
                Enforce();
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[coop] could not apply: " + (e.InnerException ?? e).Message); return false; }
        }

        static void Enforce()
        {
            if (Wanted.Count == 0) return;
            var coops = Coops(); var change = _coopT.GetMethod("ChangeStatus", F); Nested.Clear();
            foreach (var kv in Wanted)
            {
                if (kv.Key >= coops.Length) continue;
                var places = Places(coops[kv.Key]); var d = Dict(coops[kv.Key]); if (places == null || d == null) continue;
                for (int n = 0; n < places.Length && n < kv.Value.Length; n++)
                {
                    var pl = places.GetValue(n); object cur = d.Contains(pl) ? d[pl] : null;
                    Component want = null;
                    if (kv.Value[n].Length > 0) { var pet = PetSync.PetByKey(kv.Value[n]); if (pet != null) { want = pet.GetComponent(_henT); Nested.Add(pet); } }
                    bool same = want == null ? !Alive(cur) : (Alive(cur) && (cur as Component) == want);
                    if (same) continue;
                    _applying = true;
                    try { change.Invoke(coops[kv.Key], new object[] { pl, want }); }
                    catch (Exception e) { Plugin.Log.LogWarning("[coop] could not set a nest: " + (e.InnerException ?? e).Message); }
                    finally { _applying = false; }
                }
            }
            foreach (var p in Nested) Hold(p);
            Release(Nested);
        }
    }
}
