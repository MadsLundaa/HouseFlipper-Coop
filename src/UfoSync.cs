using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Farm UFO event and gravity machinery: inserting the fuse into the portal panel (which reveals the circle puzzle), turning a puzzle circle, pulling the gravity lever and using the gravity lift only
    /// changed the player's own game. The interaction itself (OnInteractSuccess) is sent to the others, who run it on the same object (found by type, name and position). The puzzle's solution and the
    /// rescue sequence then follow from identical circle rotations on both sides.
    /// </summary>
    internal static class UfoSync
    {
        // type -> the method whose call is sent to the others (an interaction, or the puzzle's completion sequence)
        static readonly string[][] Targets =
        {
            new[] { "Features.Farm.UfoEvent.UfoEventPortalPanel", "OnInteractSuccess" }, new[] { "Features.Farm.UfoEvent.InteractablePuzzleCircle", "OnInteractSuccess" },
            new[] { "Features.Farm.UfoEvent.InteractableGravityLift", "OnInteractSuccess" }, new[] { "Features.Farm.GravityReversal.GravityLever", "OnInteractSuccess" },
            new[] { "Features.Farm.UfoEvent.CirclePuzzle", "DoCompletionSequence" }
        };
        static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>();
        static readonly Dictionary<string, string> Methods = new Dictionary<string, string>();

        public static void Patch(Harmony h)
        {
            foreach (var tg in Targets)
            {
                string n = tg[0];
                try
                {
                    Type t = null; foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { try { t = a.GetType(n, false); } catch { } if (t != null) break; }
                    if (t == null) { Plugin.Log.LogInfo("[ufo] type not in this game version: " + n); continue; }
                    var m = t.GetMethod(tg[1], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (m == null) { Plugin.Log.LogInfo("[ufo] no " + tg[1] + " on " + n); continue; }
                    Types[t.Name] = t; Methods[t.Name] = tg[1];
                    h.Patch(m, postfix: new HarmonyMethod(typeof(UfoSync), "Post"));
                }
                catch (Exception e) { Plugin.Log.LogWarning("[ufo] patch " + n + " failed: " + e.Message); }
            }
        }

        static string Id(Component c) { var p = c.transform.position; return c.name + "@" + Mathf.Round(p.x * 10f) + "," + Mathf.Round(p.y * 10f) + "," + Mathf.Round(p.z * 10f); }

        static void Post(object __instance)
        {
            if (!Session.Active || Events.Applying) return;
            var c = __instance as Component; if (c == null) return;
            Events.Send("ufo", __instance.GetType().Name, Id(c));
            Plugin.Log.LogInfo("[ufo] " + __instance.GetType().Name + " " + Id(c) + " used here");
        }

        public static bool Apply(string key, string id)
        {
            try
            {
                Type t; if (!Types.TryGetValue(key, out t)) return true;
                foreach (var o in UnityEngine.Object.FindObjectsOfType(t))
                {
                    var c = o as Component; if (c == null || Id(c) != id) continue;
                    t.GetMethod(Methods[key], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Invoke(c, null);
                    Plugin.Log.LogInfo("[ufo] " + key + " " + id + " used by the other player");
                    return true;
                }
                Plugin.Log.LogWarning("[ufo] " + key + " " + id + " not found here");
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[ufo] could not apply " + key + ": " + (e.InnerException ?? e).Message); return false; }
        }
    }
}
