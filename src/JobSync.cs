using System;
using System.Reflection;
using Assets.Code.Player_progress;
using Code.GameManagers;
using HarmonyLib;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Job progress: the quest trackers only count actions done through the real tool path, so remotely applied work (mounted devices, paint) never moves a
    /// client's counter. The host broadcasts its quest manager's completion % and clients report that number instead of their own (host-authoritative).
    /// </summary>
    internal static class JobSync
    {
        static object _qm; static MethodInfo _get; static bool _patched; static double _next;
        static int _hostPct = -1, _hostCan = -1;

        static bool Find()
        {
            try
            {
                var gmT = typeof(GameManager); object gm = null;
                foreach (var f in gmT.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) if (f.FieldType == gmT) { gm = f.GetValue(null); if (gm != null) break; }
                foreach (var f in gmT.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var v = f.GetValue(null); if (v == null || v == gm) continue;
                    var m = v.GetType().GetMethod("GetCompletion", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (m == null || m.GetParameters().Length != 0 || m.ReturnType != typeof(int)) continue;
                    _qm = v; _get = m; // the quest manager is recreated per house, so it is looked up every time
                    if (!_patched)
                    {
                        _patched = true;
                        new Harmony(Plugin.Guid + ".job").Patch(m, postfix: new HarmonyMethod(typeof(JobSync), "Post"));
                        Plugin.Log.LogInfo("[job] hooked " + v.GetType().Name + ".GetCompletion");
                        var can = typeof(QuestTracker).GetMethod("CanCompleteJob");
                        if (can != null) new Harmony(Plugin.Guid + ".jobcan").Patch(can, postfix: new HarmonyMethod(typeof(JobSync), "PostCan"));
                    }
                    return true;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[job] lookup failed: " + e.Message); }
            return false;
        }

        static void Post(ref int __result)
        {
            if (Session.Role == Role.Client && Session.Active && _hostPct >= 0) __result = _hostPct;
        }

        public static void Reset() { _qm = null; _hostPct = -1; _hostCan = -1; }

        static void PostCan(ref bool __result)
        {
            if (Session.Role == Role.Client && Session.Active && _hostCan >= 0) __result = _hostCan == 1;
        }

        /// <summary>
        /// The "complete the job" payout (money, skill points, unlocks, archiving the order) is a static method of an obfuscated class. It is found by shape:
        /// the static parameterless method whose IL calls HouseSaveManager.MoveOrderSaveToArchival. Clients never run it (the host completes, everyone follows).
        /// </summary>
        public static void PatchComplete(Harmony h)
        {
            try
            {
                var target = typeof(HouseSaveManager).GetMethod("MoveOrderSaveToArchival", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (target == null) { Plugin.Log.LogWarning("[job] MoveOrderSaveToArchival not found - client job completion is NOT blocked"); return; }
                byte[] tok = BitConverter.GetBytes(target.MetadataToken);
                int n = 0;
                foreach (var t in typeof(HouseSaveManager).Assembly.GetTypes())
                    foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.GetParameters().Length != 0 || m.ReturnType != typeof(void)) continue;
                        byte[] il; try { var b = m.GetMethodBody(); il = b == null ? null : b.GetILAsByteArray(); } catch { continue; }
                        if (il == null) continue;
                        for (int i = 0; i + 4 < il.Length; i++)
                            if ((il[i] == 0x28 || il[i] == 0x6F) && il[i + 1] == tok[0] && il[i + 2] == tok[1] && il[i + 3] == tok[2] && il[i + 4] == tok[3])
                            {
                                h.Patch(m, prefix: new HarmonyMethod(typeof(JobSync), "BlockComplete")); n++;
                                Plugin.Log.LogInfo("[job] guarded completion method " + t.Name + "." + m.Name);
                                break;
                            }
                    }
                if (n == 0) Plugin.Log.LogWarning("[job] completion method not found - client job completion is NOT blocked");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[job] completion guard failed: " + e.Message); }
        }

        /// <summary>
        /// Spending a perk point (SkillCategory: decrements numberOfPerksAvailable, applies the perk) is host-only: clients can open and read the skills tab
        /// but the progression belongs to the host, and ProgressSync replicates the result. Found by shape: the public instance void method taking the perk
        /// position enum that decrements numberOfPerksAvailable.
        /// </summary>
        public static void PatchPerks(Harmony h)
        {
            try
            {
                var fld = typeof(SkillCategory).GetField("numberOfPerksAvailable");
                if (fld == null) { Plugin.Log.LogWarning("[perk] numberOfPerksAvailable not found - clients can spend perk points"); return; }
                byte[] tok = BitConverter.GetBytes(fld.MetadataToken);
                int n = 0;
                foreach (var m in typeof(SkillCategory).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    var ps = m.GetParameters();
                    if (m.ReturnType != typeof(void) || ps.Length != 1 || !ps[0].ParameterType.IsEnum || m.IsAbstract) continue;
                    byte[] il; try { var b = m.GetMethodBody(); il = b == null ? null : b.GetILAsByteArray(); } catch { continue; }
                    if (il == null) continue;
                    bool refs = false, sub = Array.IndexOf(il, (byte)0x59) >= 0;
                    for (int i = 0; i + 4 < il.Length && !refs; i++)
                        if ((il[i] == 0x7B || il[i] == 0x7D) && il[i + 1] == tok[0] && il[i + 2] == tok[1] && il[i + 3] == tok[2] && il[i + 4] == tok[3]) refs = true;
                    if (!refs || !sub) continue;
                    h.Patch(m, prefix: new HarmonyMethod(typeof(JobSync), "BlockPerk")); n++;
                    Plugin.Log.LogInfo("[perk] guarded perk spend " + m.Name);
                }
                if (n == 0) Plugin.Log.LogWarning("[perk] perk spend method not found - clients can spend perk points");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[perk] guard failed: " + e.Message); }
        }

        static bool BlockPerk()
        {
            if (Session.Role != Role.Client || !Session.Active || Events.Applying) return true;
            Plugin.Log.LogInfo("[perk] blocked perk spend on the client - only the host spends perk points");
            Session.Status = "Only the host can spend perk points";
            return false;
        }

        static bool BlockComplete()
        {
            if (Session.Role != Role.Client || !Session.Active) return true;
            Plugin.Log.LogInfo("[job] blocked job completion on the client - only the host completes jobs");
            Session.Status = "Only the host can complete the job";
            return false;
        }

        public static void Tick()
        {
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 2f;
            if (!Find()) return;
            if (Session.Role == Role.Host && Session.PlayerCount > 1)
            {
                int c = (int)_get.Invoke(_qm, null);
                var qt = UnityEngine.Object.FindObjectOfType<QuestTracker>();
                Events.Send("job", "pct", c + "|" + (qt != null && qt.CanCompleteJob() ? 1 : 0));
            }
        }

        public static bool Apply(string json)
        {
            if (Session.Role != Role.Client) return false;
            var parts = json.Split('|');
            int c; if (!int.TryParse(parts[0], out c)) return false;
            int can = parts.Length > 1 && parts[1] == "1" ? 1 : 0;
            if (c != _hostPct) Plugin.Log.LogInfo("[job] host completion " + c + "%");
            bool changed = c != _hostPct || can != _hostCan;
            _hostPct = c; _hostCan = can; Find();
            if (changed) RefreshHud();
            return true;
        }

        /// <summary>The job HUD (progress bar, ready state) only redraws on the quest manager's static update events, which a synced change never raises on a client.</summary>
        static void RefreshHud()
        {
            if (_qm == null) return;
            try
            {
                var t = _qm.GetType();
                var ev = t.GetField("QuestsUpdatedEvent", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var a = ev == null ? null : ev.GetValue(null) as Action;
                if (a != null) a();
                var gd = t.GetMethod("GetCompletionData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (gd == null) return;
                var data = gd.Invoke(_qm, null);
                foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!typeof(Delegate).IsAssignableFrom(f.FieldType) || f.FieldType.IsGenericType == false) continue;
                    var ga = f.FieldType.GetGenericArguments();
                    if (ga.Length != 1 || data == null || ga[0] != data.GetType()) continue;
                    var d = f.GetValue(null) as Delegate; if (d != null) d.DynamicInvoke(data);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[job] HUD refresh failed: " + (e.InnerException ?? e).Message); }
        }
    }
}
