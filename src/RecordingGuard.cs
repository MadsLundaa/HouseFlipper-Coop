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

        // ---- one failing subscriber must not end the house load ----
        // While a save is applied the game raises a few plain "walls/rooms are ready" events from the load coroutine, and every subscriber runs in one delegate call: an exception in
        // one (a queued door placement whose objects belong to the house that was just left, seen as a NullReferenceException in WallInstallation.InitializePlaced) ended the
        // coroutine and the game sat on the loading screen for ever. These raisers now call each subscriber on its own and log what fails.
        static readonly System.Collections.Generic.Dictionary<MethodBase, FieldInfo> _raisers = new System.Collections.Generic.Dictionary<MethodBase, FieldInfo>();

        public static void PatchLoadRaisers(Harmony h)
        {
            try
            {
                var hub = GameEvents.Hub; if (hub == null) { Plugin.Log.LogWarning("[guard] event hub not found: load raisers are not protected"); return; }
                Type co = null;
                foreach (var nt in typeof(HouseSaveManager).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)) if (nt.Name.Contains("ApplyRecordingToScenesCor")) { co = nt; break; }
                var mn = co != null ? co.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) : null;
                if (mn == null) { Plugin.Log.LogWarning("[guard] load coroutine not found: load raisers are not protected"); return; }
                var il = mn.GetMethodBody().GetILAsByteArray(); int n = 0;
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    if (il[i] != 0x28) continue;
                    MethodBase cb; try { cb = mn.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); } catch { continue; }
                    var mi = cb as MethodInfo;
                    if (mi == null || !mi.IsStatic || mi.DeclaringType != hub || mi.ReturnType != typeof(void) || mi.GetParameters().Length != 0 || _raisers.ContainsKey(mi)) continue;
                    // the raiser loads one static Action field and invokes it
                    FieldInfo fld = null; var ril = mi.GetMethodBody().GetILAsByteArray();
                    for (int k = 0; k + 4 < ril.Length && fld == null; k++) if (ril[k] == 0x7E) { try { var f = mi.Module.ResolveField(BitConverter.ToInt32(ril, k + 1)); if (f != null && f.FieldType == typeof(Action)) fld = f; } catch { } }
                    if (fld == null) continue;
                    _raisers[mi] = fld;
                    h.Patch(mi, prefix: new HarmonyMethod(typeof(RecordingGuard), "SafeRaise")); n++;
                }
                Plugin.Log.LogInfo("[guard] " + n + " load event raiser(s) call their subscribers one by one");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[guard] load raiser patch failed: " + e.Message); }
        }

        static bool SafeRaise(MethodBase __originalMethod)
        {
            FieldInfo fld; if (!_raisers.TryGetValue(__originalMethod, out fld)) return true;
            var d = fld.GetValue(null) as Action; if (d == null) return false;
            foreach (var a in d.GetInvocationList())
            {
                try { ((Action)a)(); }
                catch (Exception e) { if (_qErr++ < 10) Plugin.Log.LogWarning("[guard] a subscriber of a load event failed and was skipped (" + a.Method.DeclaringType.Name + "): " + (e.InnerException ?? e).GetType().Name + " " + (e.InnerException ?? e).Message); }
            }
            return false;
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
