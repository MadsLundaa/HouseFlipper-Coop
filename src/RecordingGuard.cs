using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Duplicate records in a house save make the game build everything twice when it loads: two doors in one frame (press E twice), every window doubled. The mod must never
    /// write such records and must not trust them when it loads a save that already has them: the recording is cleaned just before the game applies it and just before it is saved.
    /// Only records that describe the same object are merged (same scene guid / same guid / the same prefab at the same spot).
    /// </summary>
    internal static class RecordingGuard
    {
        public static void Patch(Harmony h)
        {
            try
            {
                var apply = AccessTools.Method(typeof(HouseSaveManager), "ApplyRecordingToScene");
                if (apply != null) h.Patch(apply, prefix: new HarmonyMethod(typeof(RecordingGuard), "PreApply"));
                else Plugin.Log.LogWarning("[guard] ApplyRecordingToScene not found: loaded saves are not cleaned");
                var make = AccessTools.Method(typeof(HouseSaveManager), "MakeCurrentGameRecording");
                if (make != null) h.Patch(make, postfix: new HarmonyMethod(typeof(RecordingGuard), "PostMake"));
                else Plugin.Log.LogWarning("[guard] MakeCurrentGameRecording not found: saves are not cleaned");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[guard] patch failed: " + e.Message); }
        }

        // ---- a quest that throws while a house loads must not abort the load ----
        // The game tells every quest "the save was loaded" in one loop inside the load coroutine, and the house builder raises a rooms-changed event to the same quests.
        // WallsQuest threw a NullReferenceException there (House.floors of the house being replaced is gone) when a recording with demolished/built walls was loaded
        // while another house was open: the exception ended the coroutine, the game kept waiting for the save for ever and the client sat on "Loading host's house".
        public static void PatchQuests(Harmony h)
        {
            try
            {
                int n = 0;
                foreach (var m in typeof(Empyrean.HouseFlipper.Quests.WallsQuest).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.IsSpecialName || m.ContainsGenericParameters) continue;
                    h.Patch(m, finalizer: new HarmonyMethod(typeof(RecordingGuard), "QuestFinalizer")); n++;
                }
                foreach (var ty in typeof(Quest).Assembly.GetTypes())
                {
                    if (!typeof(Quest).IsAssignableFrom(ty) || ty.ContainsGenericParameters) continue;
                    var m = ty.GetMethod("HandleSaveLoaded", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                    if (m != null && !m.IsAbstract && ty != typeof(Empyrean.HouseFlipper.Quests.WallsQuest)) { h.Patch(m, finalizer: new HarmonyMethod(typeof(RecordingGuard), "QuestFinalizer")); n++; }
                }
                Plugin.Log.LogInfo("[guard] quest load/rooms handlers are protected (" + n + " methods)");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[guard] quest patch failed: " + e.Message); }
        }

        static int _qErr;
        static Exception QuestFinalizer(Exception __exception)
        {
            if (__exception == null || !Session.Active) return __exception;
            if (!(__exception is NullReferenceException)) return __exception;
            if (_qErr++ < 5) Plugin.Log.LogWarning("[guard] a quest threw while the house was loading and was ignored: " + __exception.Message + " " + (__exception.StackTrace ?? "").Split('\n')[0].Trim());
            return null;
        }

        static void PreApply(Recording __0) { Clean(__0, "load"); }
        static void PostMake(Recording __result) { Clean(__result, "save"); }

        static string R(float v) { return Mathf.Round(v * 20f).ToString(); }

        public static int Clean(Recording rec, string when)
        {
            if (rec == null) return 0;
            int removed = 0;
            try
            {
                // door/window installations: one record per scene object, or per prefab and place
                removed += Dedupe(rec.wallInstallations, e => !string.IsNullOrEmpty(e.sceneGuid) ? "s:" + e.sceneGuid : "p:" + e.windowGuid + ":" + R(e.position.x) + ":" + R(e.position.y) + ":" + R(e.position.z), true);
                removed += Dedupe(rec.changedWallModifications, e => e.modifierGuid, true);
                removed += Dedupe(rec.destroyableItemModifications, e => e.guid, true);
                removed += Dedupe(rec.modifiedReplaceableItems, e => e.replaceableGuid, true);
                removed += Dedupe(rec.deviceStateSetterStates, e => e.deviceStateSetterGuid, true);
                removed += Dedupe(rec.prefabSwaps, e => e.swapperGuid, true);
                removed += Dedupe(rec.brokenSubitems, e => e.itemGuid + "#" + e.subitemIndex, true);
                // items: one record per scene guid; a created item exactly on top of another of the same prefab (same spot, same turn) is a double
                removed += Dedupe(rec.modifiedItems, e => !string.IsNullOrEmpty(e.sceneGuid) ? "s:" + e.sceneGuid : (e.isDestroyed ? null : "c:" + e.prefabGuid + ":" + R(e.position.x) + ":" + R(e.position.y) + ":" + R(e.position.z) + ":" + Mathf.Round(e.rotation.y / 2f)), true);
                if (removed > 0) Plugin.Log.LogWarning("[guard] removed " + removed + " duplicate record(s) from the house before " + when + " (doors/windows/items that would have been built twice)");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[guard] clean failed: " + e.Message); }
            return removed;
        }

        /// <summary>Removes entries with the same key from the list. keepLast: the later record is the newer state; otherwise the first one wins.</summary>
        static int Dedupe<T>(List<T> list, Func<T, string> key, bool keepLast)
        {
            if (list == null || list.Count < 2) return 0;
            var seen = new Dictionary<string, int>(); var drop = new HashSet<int>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == null) continue;
                string k; try { k = key(list[i]); } catch { continue; }
                if (string.IsNullOrEmpty(k)) continue;
                int j;
                if (seen.TryGetValue(k, out j)) { if (keepLast) { drop.Add(j); seen[k] = i; } else drop.Add(i); }
                else seen[k] = i;
            }
            if (drop.Count == 0) return 0;
            var keep = new List<T>(list.Count - drop.Count);
            for (int i = 0; i < list.Count; i++) if (!drop.Contains(i)) keep.Add(list[i]);
            list.Clear(); list.AddRange(keep);
            return drop.Count;
        }
    }
}
