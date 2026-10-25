using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Code.GameManagers;
using HarmonyLib;

namespace HFCoop
{
    /// <summary>
    /// House-changing actions (sell, buy, relocate the office, back to the office, accept an order from the map...) belong to the host. The house change itself is already
    /// refused on clients (HouseChangeGuard), but money, unlocks and archiving can run before it, as the job completion showed. This blocks the entry points on clients:
    /// the main-menu button dispatcher for those buttons, and every method that calls HFSceneManager.LoadHouse directly. All are found by shape (obfuscated names).
    /// </summary>
    internal static class HouseActions
    {
        static readonly HashSet<string> HostOnlyButtons = new HashSet<string> { "BackToOffice", "CompleteOrder", "SellHouse", "RelocateOffice" };

        public static void Patch(Harmony h)
        {
            int n = 0;
            try
            {
                var asm = typeof(HouseSaveManager).Assembly;
                // calls that mark a house deal as done: a method that makes one of them also pays / is paid, so the whole method is host-only
                var tokens = new List<byte[]>();
                var load = typeof(HFSceneManager).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == "LoadHouse");
                if (load != null) tokens.Add(BitConverter.GetBytes(load.MetadataToken));
                foreach (var name in new[] { "MarkAsBought", "SellHouse", "RentHouse", "EndHouseRenting" })
                {
                    foreach (var hm in typeof(HousesProgress).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (hm.Name == name) tokens.Add(BitConverter.GetBytes(hm.MetadataToken)); // every overload
                }
                byte[] tok = tokens.Count == 0 ? null : tokens[0];
                foreach (var t in asm.GetTypes())
                {
                    bool menu = t.Name == "MainMenu";
                    bool skip = t == typeof(HFSceneManager) || t == typeof(HouseSaveManager) || t == typeof(HousesProgress);
                    foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.IsAbstract || m.ContainsGenericParameters) continue;
                        var ps = m.GetParameters();
                        if (menu && m.ReturnType == typeof(void) && ps.Length == 1 && ps[0].ParameterType.IsEnum && Array.IndexOf(Enum.GetNames(ps[0].ParameterType), "SellHouse") >= 0)
                        {
                            h.Patch(m, prefix: new HarmonyMethod(typeof(HouseActions), "BlockButton")); n++;
                            Plugin.Log.LogInfo("[house] guarded menu button dispatcher " + t.Name + "." + m.Name);
                            continue;
                        }
                        if (skip || tok == null) continue;
                        byte[] il; try { var b = m.GetMethodBody(); il = b == null ? null : b.GetILAsByteArray(); } catch { continue; }
                        if (il == null) continue;
                        for (int i = 0; i + 4 < il.Length; i++)
                            if ((il[i] == 0x28 || il[i] == 0x6F) && tokens.Exists(k => il[i + 1] == k[0] && il[i + 2] == k[1] && il[i + 3] == k[2] && il[i + 4] == k[3]))
                            {
                                if (m.ReturnType != typeof(void) && m.ReturnType != typeof(bool)) break; // keep it safe: skip exotic return types
                                h.Patch(m, prefix: new HarmonyMethod(typeof(HouseActions), "BlockAction")); n++;
                                Plugin.Log.LogInfo("[house] guarded house action " + t.Name + "." + m.Name);
                                break;
                            }
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[house] guard scan failed: " + e.Message); }
            Plugin.Log.LogInfo("[house] " + n + " house-action entry points guarded");
        }

        static bool BlockButton(object __0)
        {
            if (Session.Role != Role.Client || !Session.Active || Snapshot.AllowLoad) return true;
            if (__0 == null || !HostOnlyButtons.Contains(__0.ToString())) return true;
            Plugin.Log.LogInfo("[house] blocked menu action " + __0 + " on the client - only the host changes houses");
            Session.Status = "Only the host can change house (job / sell / relocate)";
            return false;
        }

        static bool BlockAction()
        {
            if (Session.Role != Role.Client || !Session.Active || Snapshot.AllowLoad) return true;
            Plugin.Log.LogInfo("[house] blocked a house action on the client - only the host changes houses");
            Session.Status = "Only the host can change house (job / buy / sell)";
            return false;
        }
    }
}
