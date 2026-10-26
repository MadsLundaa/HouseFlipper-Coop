using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Empyrean.HouseFlipper.Core.Interactions;
using HarmonyLib;
using UnityEngine;

namespace HFCoop
{
    /// <summary>Doors, windows, drawers, light switches: replicate the player's "use" as a toggle on the same object on every peer.</summary>
    internal static class Interactions
    {
        public static void Patch(Harmony h)
        {
            Post(h, "Interact"); Post(h, "SmartInteract");
            PatchDoorSide(h);
        }

        // ---- door swing side ----
        // A door opens towards the side away from the player, decided from the LOCAL player's position. A replicated "use" must be decided from the position of
        // the player who actually opened it, otherwise the other peer swings the door the wrong way (it only worked when both stood on the same side).
        static Vector3? _openerPos;
        static Assets.Code.Player.PlayerController _pc;

        static void PatchDoorSide(Harmony h)
        {
            try
            {
                // the side test is the private parameterless bool method of DoorInteraction that compares the door's yaw with 50/140/230/320 degrees
                byte[] a = BitConverter.GetBytes(140f), b = BitConverter.GetBytes(230f);
                foreach (var m in typeof(DoorInteraction).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (m.ReturnType != typeof(bool) || m.GetParameters().Length != 0) continue;
                    byte[] il; try { var body = m.GetMethodBody(); il = body == null ? null : body.GetILAsByteArray(); } catch { continue; }
                    if (il == null || !Has(il, a) || !Has(il, b)) continue;
                    h.Patch(m, postfix: new HarmonyMethod(typeof(Interactions), "DoorSidePost"));
                    Plugin.Log.LogInfo("[use] door swing side hooked: " + m.Name);
                    return;
                }
                Plugin.Log.LogWarning("[use] door side method not found - remote doors may swing the wrong way");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[use] door side hook failed: " + e.Message); }
        }

        static bool Has(byte[] il, byte[] f)
        {
            for (int i = 0; i + 4 < il.Length; i++)
                if (il[i] == 0x22 && il[i + 1] == f[0] && il[i + 2] == f[1] && il[i + 3] == f[2] && il[i + 4] == f[3]) return true; // ldc.r4
            return false;
        }

        static void DoorSidePost(DoorInteraction __instance, ref bool __result)
        {
            if (!_openerPos.HasValue) return;
            var t = __instance.transform; var p = _openerPos.Value; float y = t.rotation.eulerAngles.y;
            if (y < 50f) __result = t.position.z < p.z;
            else if (y < 140f) __result = t.position.x < p.x;
            else if (y < 230f) __result = t.position.z > p.z;
            else if (y < 320f) __result = t.position.x > p.x;
            else __result = false;
        }

        static bool IsDoor(Interactable i) { return i is DoorInteraction; }

        static string OpenerJson()
        {
            if (_pc == null) _pc = UnityEngine.Object.FindObjectOfType<Assets.Code.Player.PlayerController>();
            if (_pc == null) return "";
            var p = _pc.transform.position;
            return p.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + p.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + p.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        static void Post(Harmony h, string name)
        {
            var m = AccessTools.Method(typeof(Interactable), name);
            if (m == null) { Plugin.Log.LogWarning("[use] Interactable." + name + " not found"); return; }
            h.Patch(m, postfix: new HarmonyMethod(typeof(Interactions), "Used"));
        }

        // Openable / switchable things (matched by class name anywhere in the hierarchy, so subclasses like DoorInteractionWithAnimator, GarageDoor, WindowInteraction count too)
        static readonly HashSet<string> ReplicatedTypes = new HashSet<string> {
            "MovablePart", "HFLightSwitch", "InteractableWithOpenCloseAnimator", "CurtainsInteraction", "FenceGate", "BalconyWindowInteractable",
            "AtticStairs", "OpenableBox", "InteractableWithRotatingPart", "IncorrectDoorInteraction", "TrashbinAnimationController", "DoorInteraction"
        };
        static readonly HashSet<string> SeenSkipped = new HashSet<string>();

        static bool Replicated(Interactable i)
        {
            for (var t = i.GetType(); t != null; t = t.BaseType) if (ReplicatedTypes.Contains(t.Name)) return true;
            return false;
        }

        static void Used(Interactable __instance)
        {
            if (!Session.Active || Events.Applying || __instance == null) return;
            if (!Replicated(__instance))
            {
                if (Cfg.VerboseLog.Value && SeenSkipped.Add(__instance.GetType().Name)) Plugin.Log.LogInfo("[use] not replicated: " + __instance.GetType().Name + " " + Inspector.Path(__instance.transform));
                return;
            }
            string key = KeyOf(__instance);
            if (key == null) { if (Cfg.VerboseLog.Value) Plugin.Log.LogInfo("[use] no key for " + Inspector.Path(__instance.transform)); return; }
            Events.Send("use", key, IsDoor(__instance) ? OpenerJson() : "");
        }

        /// <summary>Stable cross-peer key: the object's scene guid if the game's mapper knows it, else "owning item guid | child index path".</summary>
        public static string KeyOf(Component c)
        {
            string g = ItemSync.GuidOfPublic(c);
            if (!string.IsNullOrEmpty(g)) return "g:" + g;
            var item = c.GetComponentInParent<Item>();
            if (item == null) return null;
            string ig = ItemSync.NetIdPublic(item, true); // scene guid ("s:...") or the net id of an item created during the session ("c:...")
            if (string.IsNullOrEmpty(ig)) return null;
            var path = new List<int>();
            for (var t = c.transform; t != null && t != item.transform; t = t.parent) path.Insert(0, t.GetSiblingIndex());
            return "i:" + ig + "|" + string.Join(",", path.ConvertAll(x => x.ToString()).ToArray()) + "|" + c.GetType().Name;
        }

        public static Component Resolve(string key)
        {
            if (key.StartsWith("g:")) return ItemSync.FromGuidPublic(key.Substring(2));
            if (!key.StartsWith("i:")) return null;
            var parts = key.Substring(2).Split('|');
            var item = (parts[0].StartsWith("s:") || parts[0].StartsWith("c:") || parts[0].StartsWith("w:") ? ItemSync.ByNetId(parts[0]) : ItemSync.FromGuidPublic(parts[0])) as Item; if (item == null) return null;
            var t = item.transform;
            if (parts[1].Length > 0)
                foreach (var s in parts[1].Split(',')) { int i = int.Parse(s); if (i >= t.childCount) return null; t = t.GetChild(i); }
            foreach (var c in t.GetComponents<Interactable>()) if (c.GetType().Name == parts[2]) return c;
            return null;
        }

        public static bool Apply(string key, string json)
        {
            var c = Resolve(key) as Interactable;
            if (c == null) { Plugin.Log.LogWarning("[use] cannot resolve " + key); return false; }
            // lamp states travel through LightSync; a client's copy of the switch has its own wiring (or none), so it must not toggle lamps on its own here
            if (Session.Role == Role.Client) for (var t = c.GetType(); t != null; t = t.BaseType) if (t.Name == "HFLightSwitch") return true;
            Vector3? pos = null;
            if (!string.IsNullOrEmpty(json))
            {
                var s = json.Split(',');
                float x, y, z;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                if (s.Length == 3 && float.TryParse(s[0], System.Globalization.NumberStyles.Float, ci, out x) && float.TryParse(s[1], System.Globalization.NumberStyles.Float, ci, out y) && float.TryParse(s[2], System.Globalization.NumberStyles.Float, ci, out z)) pos = new Vector3(x, y, z);
            }
            _openerPos = pos; // the door decides its swing side from the opener, not from this peer's player
            try { c.Interact(); } finally { _openerPos = null; }
            return true;
        }
    }
}
