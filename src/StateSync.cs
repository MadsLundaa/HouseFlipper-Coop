using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Items whose saved state changes without the game telling the recorder: a DineOut tablecloth, a Luxury furniture that was renovated, pet bowls and containers (filled/emptied by pets and players),
    /// chicken coops, animal bedding, leaf piles. The item sync only sends an item when the recorder reports a purchase/move/damage, so these edits stayed on the player who made them.
    /// Every second the items that carry such a component are re-serialised with the game's own save record (ItemModification) and handed to the item sync, which sends the ones that differ from what
    /// was last sent or received. The first look at an item only records its state (both players start from the same house).
    /// </summary>
    internal static class StateSync
    {
        static readonly string[] TypeNames =
        {
            "HF.DineOut.Tableware.TableclothController", "RenovationFurnitureInteractable", "FrozenWay.HouseFlipper.Farm.Animals.SimpleMovablePart.PetBowlWithMovableParts",
            "Features.Pets.Items.AbstractPetContainerItem", "Features.Farm.ChickenCoop.ChickenCoop", "FrozenWay.HouseFlipper.Farm.AnimalStrawBed", "Features.Farm.LeafBlowerTool.LeafPile",
            "Features.Farm.InteractableAnimationCycler.InteractableAnimationCycler", "Features.Farm.CustomItemStacker.CustomItemStacker"
        };
        static List<Type> _types;
        static double _next; static int _lastCount = -1;
        static readonly HashSet<Item> Known = new HashSet<Item>();
        static readonly Dictionary<string, int> Counts = new Dictionary<string, int>();

        static readonly Dictionary<Item, double> Hold = new Dictionary<Item, double>();   // items whose state just arrived: their own re-serialisation is taken as the baseline once it has settled

        /// <summary>Host: a client reported its house loaded: send it the current state of every watched item.</summary>
        public static void SendTo(Conn c)
        {
            if (Session.Role != Role.Host) return;
            foreach (var it in Known.ToList()) if (it != null) ItemSync.SendStateTo(it, c);
            Plugin.Log.LogInfo("[state] sent the state of " + Known.Count + " watched item(s) to #" + c.PlayerId);
        }

        public static void Reset() { Known.Clear(); Hold.Clear(); _next = 0; }

        /// <summary>A state for this item arrived from another player and was applied (textures and meshes may finish loading a moment later).</summary>
        public static void OnApplied(Item it) { if (it != null && Known.Contains(it)) Hold[it] = Time.realtimeSinceStartup + 3.0; }

        // Every player's hens lay eggs in their own game and a coop state that arrives from another player spawns its eggs again (the game's load routine never looks at what is already on the nest) and never
        // removes an egg that the sender picked up. So the eggs of every coop are reconciled against the nests: no egg object on a nest that holds no egg, and one egg object at most on a nest that does.
        static readonly Dictionary<Component, double> EggMissingSince = new Dictionary<Component, double>();
        static void DedupeEggs(Component coop)
        {
            try
            {
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var ef = coop.GetType().GetField("eggs", F); var d = ef == null ? null : ef.GetValue(coop) as System.Collections.IDictionary; if (d == null) return;
                var pf = coop.GetType().GetField("jumpPlaces", F); var places = pf == null ? null : pf.GetValue(coop) as Array; if (places == null) return;
                var drop = new List<object>(); var kept = new HashSet<object>(); int dups = 0, ghosts = 0;
                foreach (System.Collections.DictionaryEntry e in d)
                {
                    var c = e.Key as Component;
                    if (c == null) { drop.Add(e.Key); continue; }   // picked up here: the entry stays in the game's dictionary
                    var hf = e.Value == null ? null : e.Value.GetType().GetField("hasEgg", F); bool has = hf != null && (bool)hf.GetValue(e.Value);
                    if (!has) { drop.Add(e.Key); ghosts++; continue; }      // the nest is empty (the egg was picked up on the other side)
                    if (!kept.Add(e.Value)) { drop.Add(e.Key); dups++; }    // second egg on a nest
                }
                foreach (var k in drop) { d.Remove(k); var c = k as Component; if (c != null) UnityEngine.Object.Destroy(c.gameObject); }
                // a nest marked as holding an egg with no egg object on it (the house was loaded before the eggs could be spawned): nobody can pick it up and no hen lays there again
                int missing = 0;
                foreach (var pl in places) { var hf = pl.GetType().GetField("hasEgg", F); if (hf != null && (bool)hf.GetValue(pl) && !kept.Contains(pl)) missing++; }
                double now = Time.realtimeSinceStartup;
                if (missing == 0) EggMissingSince.Remove(coop);
                else if (!EggMissingSince.ContainsKey(coop)) EggMissingSince[coop] = now;
                else if (now - EggMissingSince[coop] > 6.0)
                {
                    EggMissingSince[coop] = now + 30.0;   // one repair, then wait
                    var save = coop.GetType().GetMethod("GetSaveData", F); var load = coop.GetType().GetMethods(F).FirstOrDefault(m => m.Name == "LoadEggs");
                    if (save != null && load != null) { load.Invoke(coop, new object[] { save.Invoke(coop, null) }); Plugin.Log.LogInfo("[state] coop eggs: " + missing + " nest(s) held an egg with nothing on them: eggs spawned again"); }
                }
                if (dups + ghosts > 0) Plugin.Log.LogInfo("[state] coop eggs: removed " + dups + " duplicate(s) and " + ghosts + " egg(s) already picked up elsewhere");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[state] egg check failed: " + e.Message); }
        }

        static void Resolve()
        {
            _types = new List<Type>();
            foreach (var n in TypeNames)
            {
                Type t = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { try { t = a.GetType(n, false); } catch { } if (t != null) break; }
                if (t != null && typeof(Component).IsAssignableFrom(t)) _types.Add(t); else Plugin.Log.LogInfo("[state] type not in this game version: " + n);
            }
        }

        public static void Tick()
        {
            if (!Session.Active || !ItemSync.Settled()) return;
            if (Time.realtimeSinceStartup < _next) return; _next = Time.realtimeSinceStartup + 1.0;
            try
            {
                if (_types == null) Resolve();
                var items = new HashSet<Item>();
                foreach (var t in _types)
                    foreach (var o in UnityEngine.Object.FindObjectsOfType(t))
                    {
                        var c = o as Component; if (c == null || !c.gameObject.activeInHierarchy) continue;
                        if (t.Name == "ChickenCoop") DedupeEggs(c);
                        var it = c.GetComponentInParent<Item>(); if (it != null) items.Add(it);
                    }
                if (items.Count != _lastCount) { _lastCount = items.Count; Plugin.Log.LogInfo("[state] watching " + items.Count + " stateful item(s)"); }
                foreach (var it in items)
                {
                    if (Known.Contains(it) == false && !ItemSync.EnsureLoadedId(it)) continue;
                    if (Known.Add(it)) { ItemSync.Baseline(it); continue; }
                    double until;
                    if (Hold.TryGetValue(it, out until)) { if (Time.realtimeSinceStartup < until) continue; Hold.Remove(it); ItemSync.Baseline(it, true); continue; }
                    ItemSync.QueuePublic(it);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[state] sweep failed: " + e.Message); }
        }
    }
}
