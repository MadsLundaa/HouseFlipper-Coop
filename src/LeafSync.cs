using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Leaf blower. Blowing spawns a force-field object at the tool tip that pushes the leaf particles; leaves are particles on each player's own game, so the others saw the pile lie still while
    /// someone blew it (only the leaf count was shared). While a player is blowing (the game's own "blowing started / stopped" broadcast), the position of that force field is sent ten times a
    /// second; the other players spawn a copy of the same force field (made from the prefab of their own leaf blower tool) and move it there, and remove it when the updates stop.
    /// </summary>
    internal static class LeafSync
    {
        static Type _fsT, _toolT;
        static bool _blowing; static double _next;
        static readonly Dictionary<int, Component> Mine = new Dictionary<int, Component>();   // force fields made here for a remote player (key = the sender's instance id)
        static readonly Dictionary<int, double> Seen = new Dictionary<int, double>();

        public static void Reset() { RemoveAll(); _blowing = false; }

        // The piles do not look at objects in the scene: each pile has an "external forces" list built from the leaf pile manager's own blower system (its ForceFieldList). A copy of the system that is
        // only standing in the scene pushes nothing, so the fields of every copy are added to that list (and taken out again), and the piles rebuild their influence lists when the set changes.
        static IList FieldList()
        {
            var mt = _fsT.Assembly.GetType("Features.Farm.LeafBlowerTool.LeafPileManager", false); if (mt == null) return null;
            var mgr = UnityEngine.Object.FindObjectOfType(mt); if (mgr == null) return null;
            var g = mt.GetMethod("get_LeafBlowerForceField", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); if (g == null) return null;
            var sys = g.Invoke(g.IsStatic ? null : mgr, null); if (sys == null) return null;
            var lm = _fsT.GetMethod("get_ForceFieldList", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); if (lm == null) return null;
            return lm.Invoke(lm.IsStatic ? null : sys, null) as IList;
        }

        static Component _adopted;   // a player who never used the leaf blower has no blower system in the manager: the first copy made for a remote player becomes it

        static PropertyInfo ManagerSystemProp(Type mt)
        {
            foreach (var p in mt.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) if (p.PropertyType == _fsT && p.CanWrite && p.CanRead) return p;
            return null;
        }

        static void Adopt(Component copy, bool on)
        {
            try
            {
                var mt = _fsT.Assembly.GetType("Features.Farm.LeafBlowerTool.LeafPileManager", false); if (mt == null) return;
                var mgr = UnityEngine.Object.FindObjectOfType(mt); if (mgr == null) return;
                var p = ManagerSystemProp(mt); if (p == null) return;
                if (on) { if (p.GetValue(mgr, null) == null) { p.SetValue(mgr, copy, null); _adopted = copy; } }
                else if (_adopted == copy) { if (p.GetValue(mgr, null) as Component == copy) p.SetValue(mgr, null, null); _adopted = null; }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[leaf] could not use the copy as the blower system: " + (e.InnerException ?? e).Message); }
        }

        static void RefreshPiles()
        {
            try
            {
                var pt = _fsT.Assembly.GetType("Features.Farm.LeafBlowerTool.LeafPile", false); if (pt == null) return;
                var m = pt.GetMethod("RefreshExternalForceInfluences", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (m == null) return;
                var empty = Activator.CreateInstance(m.GetParameters()[0].ParameterType);
                foreach (var pile in UnityEngine.Object.FindObjectsOfType(pt)) { try { m.Invoke(pile, new object[] { empty }); } catch { } }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[leaf] could not refresh the piles: " + (e.InnerException ?? e).Message); }
        }

        static void Register(Component copy, bool add)
        {
            try
            {
                if (copy == null) return;
                var list = FieldList();
                if (list == null) { if (add) Adopt(copy, true); return; }   // no blower system here yet: the copy itself becomes it (its own fields are already in its list)
                if (copy == _adopted) return;
                foreach (var f in copy.GetComponentsInChildren<ParticleSystemForceField>(true)) { if (add) { if (!list.Contains(f)) list.Add(f); } else list.Remove(f); }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[leaf] could not register the force fields: " + (e.InnerException ?? e).Message); }
        }

        static void Drop(int id)
        {
            Component c; if (!Mine.TryGetValue(id, out c)) return;
            if (c != null) { Adopt(c, false); Register(c, false); UnityEngine.Object.Destroy(c.gameObject); }
            Mine.Remove(id); Seen.Remove(id);
        }

        static void RemoveAll() { foreach (var id in Mine.Keys.ToList()) Drop(id); if (_fsT != null) RefreshPiles(); }

        public static void Patch(Harmony h)
        {
            try
            {
                _fsT = typeof(FrozenWay.HouseFlipper.Pets.Pet).Assembly.GetType("Features.Farm.LeafBlowerTool.LeafBlowerForceFieldSystem", false);
                _toolT = typeof(FrozenWay.HouseFlipper.Pets.Pet).Assembly.GetType("Features.Farm.LeafBlowerTool.LeafBlowerTool", false);
                if (_fsT == null || _toolT == null) { Plugin.Log.LogWarning("[leaf] leaf blower types not found: blowing is not shared"); return; }
                var st = _fsT.GetMethod("BroadcastBlowingStarted", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var sp = _fsT.GetMethod("BroadcastBlowingStopped", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (st != null) h.Patch(st, postfix: new HarmonyMethod(typeof(LeafSync), "Started"));
                if (sp != null) h.Patch(sp, postfix: new HarmonyMethod(typeof(LeafSync), "Stopped"));
                Plugin.Log.LogInfo("[leaf] watching the leaf blower (" + (st != null) + "/" + (sp != null) + ")");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[leaf] patch failed: " + e.Message); }
        }

        static void Started() { if (Session.Active && !Events.Applying) { _blowing = true; _next = 0; if (Cfg.VerboseLog.Value) Plugin.Log.LogInfo("[leaf] blowing started here: " + new System.Diagnostics.StackTrace(1, false).ToString().Replace("\r", "").Replace("\n", " | ")); } }
        static void Stopped() { if (Session.Active && !Events.Applying && _blowing) { _blowing = false; Events.Send("leaf", "stop", "1"); } }

        static string F(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        public static void Tick()
        {
            if (!Session.Active || _fsT == null) return;
            double now = Time.realtimeSinceStartup;
            // remote force fields that stopped getting updates
            if (Mine.Count > 0)
            {
                bool dropped = false;
                foreach (var id in Mine.Keys.ToList())
                    if (Mine[id] == null || now - Seen[id] > 0.8) { Drop(id); dropped = true; }
                if (dropped) RefreshPiles();
            }
            if (!_blowing || now < _next) return; _next = now + 0.1;
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var o in UnityEngine.Object.FindObjectsOfType(_fsT))
                {
                    var c = o as Component; if (c == null || Mine.ContainsValue(c)) continue; var t = c.transform;
                    sb.Append(c.GetInstanceID()).Append(',').Append(F(t.position.x)).Append(',').Append(F(t.position.y)).Append(',').Append(F(t.position.z)).Append(',')
                      .Append(F(t.rotation.x)).Append(',').Append(F(t.rotation.y)).Append(',').Append(F(t.rotation.z)).Append(',').Append(F(t.rotation.w)).Append(';');
                }
                if (sb.Length > 0) { if (Cfg.VerboseLog.Value) Plugin.Log.LogInfo("[leaf] sending " + sb); Events.Send("leaf", "ff", sb.ToString()); }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[leaf] tick failed: " + e.Message); }
        }

        static Component Prefab()
        {
            foreach (var t in Resources.FindObjectsOfTypeAll(_toolT))
            {
                var f = _toolT.GetField("forceFieldSystemToSpawn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var p = f == null ? null : f.GetValue(t) as Component; if (p != null) return p;
            }
            return null;
        }

        public static bool Apply(string key, string json)
        {
            try
            {
                if (_fsT == null) return true;
                if (key == "stop") { RemoveAll(); return true; }
                if (key != "ff") return false;
                var prefab = Prefab(); if (prefab == null) return true;   // this player has no leaf blower tool object yet: nothing to copy
                foreach (var rec in json.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var f = rec.Split(','); if (f.Length < 8) continue; int id = int.Parse(f[0], CultureInfo.InvariantCulture);
                    var pos = new Vector3(float.Parse(f[1], CultureInfo.InvariantCulture), float.Parse(f[2], CultureInfo.InvariantCulture), float.Parse(f[3], CultureInfo.InvariantCulture));
                    var rot = new Quaternion(float.Parse(f[4], CultureInfo.InvariantCulture), float.Parse(f[5], CultureInfo.InvariantCulture), float.Parse(f[6], CultureInfo.InvariantCulture), float.Parse(f[7], CultureInfo.InvariantCulture));
                    Component c; bool made = false;
                    if (!Mine.TryGetValue(id, out c) || c == null) { c = UnityEngine.Object.Instantiate(prefab, pos, rot) as Component; Mine[id] = c; made = true; }
                    if (c != null) { c.transform.position = pos; c.transform.rotation = rot; if (made) { c.gameObject.SetActive(true); Register(c, true); RefreshPiles(); } }
                    Seen[id] = Time.realtimeSinceStartup;
                }
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[leaf] could not apply: " + (e.InnerException ?? e).Message); return false; }
        }
    }
}
