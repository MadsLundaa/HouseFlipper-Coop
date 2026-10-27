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
        static MethodInfo _place, _sold, _soldArgs, _wallAdded; static bool _looked;

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
