using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// The game announces "item placed" / "item sold" through a static event hub that job quests, room item lists and the HUD listen to. Work that arrives from another
    /// peer is applied below that level (loading code), so none of those listeners heard about it: a bought and placed item counted only for the player who placed it,
    /// sold items kept their entry in the job list. The raisers are obfuscated, so they are found by shape.
    /// </summary>
    internal static class GameEvents
    {
        static MethodInfo _place, _sold, _soldArgs, _wallAdded, _replaced; static Type _hub; static bool _looked;

        static void Look()
        {
            if (_looked) return; _looked = true;
            try
            {
                var asm = typeof(Item).Assembly;
                foreach (var ty in asm.GetTypes())
                {
                    if (!ty.IsAbstract || !ty.IsSealed) continue; // static class
                    // the placement helper: public static void M(Item, bool) twice (placed / loaded variants); the first declared one raises the "placed" event
                    var ms = ty.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.ReturnType == typeof(void)).ToList();
                    var p = ms.Where(m => { var ps = m.GetParameters(); return ps.Length == 2 && ps[0].ParameterType == typeof(Item) && ps[1].ParameterType == typeof(bool); }).OrderBy(m => m.MetadataToken).ToList();
                    var one = ms.Where(m => { var ps = m.GetParameters(); return ps.Length == 1 && ps[0].ParameterType == typeof(Item); }).ToList();
                    if (p.Count == 2 && ty.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Length <= 4) { _place = p[0]; Plugin.Log.LogInfo("[events] placement helper " + ty.Name + "." + p[0].Name); break; }
                }
                // the hub: the static class that declares static Action<Wall> and Action<X> events where X holds a set of items (the "sold" args)
                foreach (var ty in asm.GetTypes())
                {
                    if (!ty.IsAbstract || !ty.IsSealed) continue;
                    if (!ty.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Any(f => f.FieldType == typeof(Action<Wall>))) continue;
                    _hub = ty;
                    // "replacement confirmed": the static void(Replaceable) raiser
                    _replaced = ty.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => { var ps = m.GetParameters(); return m.ReturnType == typeof(void) && ps.Length == 1 && ps[0].ParameterType == typeof(HF.Replaceable.Replaceable); }).FirstOrDefault();
                    // "a wall was built": the first static void (Wall, float, bool) raiser; the floor's wall list, the combined wall mesh and the quests listen to it
                    _wallAdded = ty.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => { var ps = m.GetParameters(); return m.ReturnType == typeof(void) && ps.Length == 3 && ps[0].ParameterType == typeof(Wall) && ps[1].ParameterType == typeof(float) && ps[2].ParameterType == typeof(bool); }).OrderBy(m => m.MetadataToken).FirstOrDefault();
                    if (_wallAdded != null) Plugin.Log.LogInfo("[events] wall-built raiser " + ty.Name + "." + _wallAdded.Name);
                    var cand = ty.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m =>
                    {
                        var ps = m.GetParameters(); if (m.ReturnType != typeof(void) || ps.Length != 1) return false;
                        var t = ps[0].ParameterType; if (t.IsPrimitive || t == typeof(Item) || t == typeof(Wall) || t.IsGenericType) return false;
                        return t.GetFields(BindingFlags.Public | BindingFlags.Instance).Any(f => f.FieldType == typeof(HashSet<Item>));
                    }).OrderBy(m => m.MetadataToken).ToList();
                    if (cand.Count > 0)
                    {
                        _sold = cand[0]; var at = _sold.GetParameters()[0].ParameterType;
                        _soldArgs = at.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.ReturnType == at && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(Item));
                        Plugin.Log.LogInfo("[events] sold event raiser " + ty.Name + "." + _sold.Name + " args " + (_soldArgs != null ? _soldArgs.Name : "none"));
                        break;
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[events] lookup failed: " + e.Message); }
        }

        /// <summary>A wall another player built was created by the loader only: announce it like the wall tool does.</summary>
        public static bool RaiseWallAdded(Wall wall)
        {
            Look();
            if (_wallAdded == null || wall == null) return false;
            _wallAdded.Invoke(null, new object[] { wall, 0f, false });
            return true;
        }

        /// <summary>A pile (snowdrift...) cleared by another peer: raise the hub event of that pile type (generic nested class, found by shape).</summary>
        public static bool RaisePile(Empyrean.HouseFlipper.Pile pile)
        {
            Look();
            if (_hub == null || pile == null) return false;
            foreach (var nt in _hub.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!nt.IsGenericTypeDefinition || nt.GetGenericArguments().Length != 1) continue;
                var cons = nt.GetGenericArguments()[0].GetGenericParameterConstraints(); bool ok = false; foreach (var c in cons) if (c == typeof(Empyrean.HouseFlipper.Pile)) ok = true;
                if (!ok) continue;
                var closed = nt.MakeGenericType(pile.GetType());
                foreach (var m in closed.GetMethods(BindingFlags.Public | BindingFlags.Static))
                { var ps = m.GetParameters(); if (m.ReturnType == typeof(void) && ps.Length == 1 && ps[0].ParameterType == pile.GetType()) { m.Invoke(null, new object[] { pile }); return true; } }
            }
            return false;
        }

        // ---- "a wall side changed" events that the wall quests (painting, tiling, panels) recompute their progress on ----
        // The tools raise them (PaintTool, the tiles tool); a change applied from another peer never went through a tool, so the quests of the other player kept the old count.
        // The events are the ones WallChangerQuest.InitQuest subscribes to: read from its IL (the hub's names are obfuscated).
        static List<FieldInfo> _sideEvents, _plainEvents;

        static void LookSideEvents()
        {
            if (_sideEvents != null) return;
            _sideEvents = new List<FieldInfo>(); _plainEvents = new List<FieldInfo>();
            try
            {
                var init = typeof(Empyrean.HouseFlipper.Quests.WallChangerQuest).GetMethod("InitQuest", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (init == null) { Plugin.Log.LogWarning("[events] WallChangerQuest.InitQuest not found"); return; }
                var il = init.GetMethodBody().GetILAsByteArray();
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    if (il[i] != 0x28 && il[i] != 0x6F) continue;
                    MethodBase mb; try { mb = init.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); } catch { continue; }
                    if (mb == null || !mb.IsStatic || !mb.DeclaringType.IsAbstract || !mb.DeclaringType.IsSealed) continue;
                    var mps = mb.GetParameters(); if (mps.Length != 1 || (mps[0].ParameterType != typeof(Action) && mps[0].ParameterType != typeof(Action<Code.HouseBuildingBlocks.WallSide>))) continue; // an event accessor (renamed)
                    FieldInfo f = null;
                    if (f == null)
                    {
                        // the obfuscator can rename the backing field apart from the event: the accessor loads it (ldsfld)
                        var ail = mb.GetMethodBody().GetILAsByteArray();
                        for (int k = 0; k + 4 < ail.Length && f == null; k++)
                        {
                            if (ail[k] != 0x7E) continue;
                            try { f = mb.Module.ResolveField(BitConverter.ToInt32(ail, k + 1)); } catch { f = null; }
                        }
                    }
                    if (f == null) continue;
                    if (f.FieldType == typeof(Action<Code.HouseBuildingBlocks.WallSide>)) { if (!_sideEvents.Contains(f)) _sideEvents.Add(f); }
                    else if (f.FieldType == typeof(Action)) { if (!_plainEvents.Contains(f)) _plainEvents.Add(f); }
                }
                Plugin.Log.LogInfo("[events] wall quest events: " + _sideEvents.Count + " with a wall side, " + _plainEvents.Count + " plain");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[events] wall quest events lookup failed: " + e.Message); }
        }

        /// <summary>Tell the wall quests that wall sides changed (paint, tiles, panels): they recount all their sides on any of these events.</summary>
        public static void RaiseWallSidesChanged(IList<Code.HouseBuildingBlocks.WallSide> sides)
        {
            LookSideEvents();
            foreach (var f in _plainEvents) { try { var d = f.GetValue(null) as Action; if (d != null) d(); } catch (Exception e) { Plugin.Log.LogWarning("[events] wall quest refresh failed: " + (e.InnerException ?? e).Message); } }
            if (sides == null) return;
            int n = 0;
            foreach (var s in sides)
            {
                if (s == null) continue;
                foreach (var f in _sideEvents) { try { var d = f.GetValue(null) as Action<Code.HouseBuildingBlocks.WallSide>; if (d != null) d(s); } catch (Exception e) { Plugin.Log.LogWarning("[events] wall quest refresh failed: " + (e.InnerException ?? e).Message); } }
                if (++n >= 3) break; // the quests recount everything on each event; a few are enough
            }
        }

        /// <summary>
        /// The replacement quests work out which stairs/window was replaced from "the item the player last aimed at", which the replace tool sets when the player looks at it. For a
        /// replacement made by another peer nobody aimed at it here, so the quest found no entry and the progress stayed. Point those quests at the replaced item first.
        /// </summary>
        static void AimQuestsAt(HF.Replaceable.Replaceable rep)
        {
            try
            {
                var af = typeof(HF.Replaceable.Replaceable).GetField("assignedItemToReplace", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var item = af != null ? af.GetValue(rep) as Item : null; if (item == null) return;
                foreach (var q in UnityEngine.Object.FindObjectsOfType<Code.Quests.ItemReplacementQuest>())
                    for (var t = q.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                            if (f.FieldType == typeof(Item)) f.SetValue(q, item);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[events] could not aim the replacement quests: " + e.Message); }
        }

        public static bool RaiseReplaced(HF.Replaceable.Replaceable rep)
        {
            Look();
            if (_replaced == null || rep == null) return false;
            AimQuestsAt(rep);
            _replaced.Invoke(null, new object[] { rep });
            return true;
        }

        /// <summary>An item from another peer was put down: do what the game does when a player drops it (room, quests, culling...).</summary>
        public static bool RaisePlaced(Item item)
        {
            Look();
            if (_place == null) return false;
            _place.Invoke(null, new object[] { item, true });
            return true;
        }

        /// <summary>An item was sold by another peer (call before the object is destroyed).</summary>
        public static bool RaiseSold(Item item)
        {
            Look();
            if (_sold == null || _soldArgs == null) return false;
            var ps = _soldArgs.GetParameters(); var args = new object[ps.Length]; args[0] = item;
            for (int i = 1; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
            var ev = _soldArgs.Invoke(null, args);
            _sold.Invoke(null, new object[] { ev });
            return true;
        }
    }
}
