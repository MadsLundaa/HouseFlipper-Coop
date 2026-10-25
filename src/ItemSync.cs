using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Replicates item changes (move, buy, sell, damage). We reuse the game's own save pipeline:
    /// an ItemModification (the exact record the game writes into its save file) is built with PreSave from the live item,
    /// shipped as JSON, and applied on the other side with TryPostLoad (instantiate / move / destroy).
    /// Scene items are identified by the game's scene guid; items created at runtime get a "c:owner:n" id.
    /// </summary>
    internal static class ItemSync
    {
        static readonly JsonSerializerSettings Ser = new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore, Formatting = Formatting.None };
        static readonly Dictionary<Item, string> CreatedId = new Dictionary<Item, string>();
        static readonly Dictionary<string, Item> CreatedById = new Dictionary<string, Item>();
        static readonly Dictionary<string, string> LastJson = new Dictionary<string, string>();
        static readonly HashSet<Item> Pending = new HashSet<Item>();
        static readonly List<string> PendingDeleted = new List<string>();
        static int _counter;

        static MethodInfo _mPreSave = typeof(ItemModification).GetMethod("PreSave");
        static MethodInfo _mPostLoad = typeof(ItemModification).GetMethod("TryPostLoad", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static MethodInfo _mLoadState = typeof(ItemModification).GetMethod("TryToLoadItemState", BindingFlags.Instance | BindingFlags.NonPublic);

        static object Hsm { get { var p = typeof(HouseSaveManager).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public); return p == null ? null : p.GetValue(null, null); } }

        static string GuidOf(Component c)
        {
            var h = Hsm; if (h == null) return null;
            var m = h.GetType().GetMethod("ComponentToGuid");
            return m == null ? null : m.Invoke(h, new object[] { c }) as string;
        }

        static Component FromGuid(string g)
        {
            var h = Hsm; if (h == null) return null;
            var m = h.GetType().GetMethod("GuidToComponent");
            return m == null ? null : m.Invoke(h, new object[] { g }) as Component;
        }

        public static string GuidOfPublic(Component c) { return GuidOf(c); }
        public static Component FromGuidPublic(string g) { return FromGuid(g); }
        public static string IdOf(Item it) { return NetId(it, false); }
        public static string NetIdPublic(Item it, bool create) { return NetId(it, create); }
        public static void RegisterCreated(string id, Item it) { CreatedById[id] = it; CreatedId[it] = id; }
        public static void QueuePublic(Item it) { Queue(it); }
        /// <summary>Drop a runtime item created by this sync (recorded as sold, forgotten, destroyed) without telling the other peers.</summary>
        public static void Discard(string id)
        {
            Item it; if (!CreatedById.TryGetValue(id, out it)) return;
            if (it != null) { RecordSale(it, null); CreatedId.Remove(it); UnityEngine.Object.Destroy(it.gameObject); }
            CreatedById.Remove(id); LastJson.Remove(id);
        }
        public static Item ByNetId(string netId)
        {
            if (netId.StartsWith("s:")) return FromGuid(netId.Substring(2)) as Item;
            Item i; CreatedById.TryGetValue(netId, out i); return i;
        }

        static string NetId(Item it, bool create)
        {
            string id;
            if (CreatedId.TryGetValue(it, out id)) return id;
            string g = GuidOf(it);
            if (!string.IsNullOrEmpty(g)) return "s:" + g;
            if (!create) return null;
            id = "c:" + Session.MyId + ":" + (++_counter);
            CreatedId[it] = id; CreatedById[id] = it;
            return id;
        }

        // ---- late-join id mapping: runtime-created items have no scene guid, so host and client agree on ids via the recording's item order ----
        public static void AssignLoadedIds(Recording rec)
        {
            foreach (var mod in rec.modifiedItems)
            {
                if (mod == null || mod.instance == null || !mod.IsCreated) continue;
                string id; if (CreatedId.TryGetValue(mod.instance, out id)) continue;
                id = "c:" + Session.MyId + ":" + (++_counter); CreatedId[mod.instance] = id; CreatedById[id] = mod.instance;
            }
            // doors/windows installed in earlier sessions are not in modifiedItems: they get the id "w:<index in wallInstallations>" on both peers
            if (rec.wallInstallations != null)
                for (int i = 0; i < rec.wallInstallations.Count; i++)
                {
                    var wi = rec.wallInstallations[i]; var it = wi != null ? InstSync.ItemOf(wi) : null;
                    if (it == null || CreatedId.ContainsKey(it) || !string.IsNullOrEmpty(GuidOf(it))) continue;
                    string id = "w:" + i; CreatedId[it] = id; CreatedById[id] = it;
                }
        }

        public static string ExportIdMap(Recording rec)
        {
            var map = new Dictionary<string, string>();
            for (int i = 0; i < rec.modifiedItems.Count; i++)
            {
                var mod = rec.modifiedItems[i]; string id;
                if (mod != null && mod.instance != null && mod.IsCreated && CreatedId.TryGetValue(mod.instance, out id)) map[i.ToString()] = id;
            }
            if (rec.wallInstallations != null)
                for (int i = 0; i < rec.wallInstallations.Count; i++)
                {
                    var it = rec.wallInstallations[i] != null ? InstSync.ItemOf(rec.wallInstallations[i]) : null; string id;
                    if (it != null && CreatedId.TryGetValue(it, out id) && id.StartsWith("w:")) map["w" + i] = id;
                }
            return JsonConvert.SerializeObject(map);
        }

        public static void ImportIdMap(Recording rec, string json)
        {
            if (rec == null || string.IsNullOrEmpty(json)) return;
            var map = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            int n = 0;
            foreach (var kv in map)
            {
                int i;
                if (kv.Key.StartsWith("w"))
                {
                    if (!int.TryParse(kv.Key.Substring(1), out i) || rec.wallInstallations == null || i >= rec.wallInstallations.Count) continue;
                    var wit = InstSync.ItemOf(rec.wallInstallations[i]); if (wit == null) continue;
                    CreatedId[wit] = kv.Value; CreatedById[kv.Value] = wit; n++; continue;
                }
                if (!int.TryParse(kv.Key, out i) || i >= rec.modifiedItems.Count) continue;
                var it = rec.modifiedItems[i].instance; if (it == null) continue;
                CreatedId[it] = kv.Value; CreatedById[kv.Value] = it; n++;
            }
            Plugin.Log.LogInfo("[item] imported " + n + " created-item ids");
        }

        public static void Reset() { CreatedId.Clear(); CreatedById.Clear(); LastJson.Clear(); Pending.Clear(); PendingDeleted.Clear(); }

        // ---------- hooks ----------
        public static void Patch(Harmony h)
        {
            var rec = typeof(GameplayRecorder);
            Post(h, rec, "OnBoughtOrModifiedItem", "PostModified");
            Post(h, rec, "OnDamageItem", "PostDamaged");
            Post(h, rec, "OnOfficeRelocated", "PostModified");
            var sold = AccessTools.Method(rec, "OnSoldOrDisposedItem");
            if (sold != null) h.Patch(sold, prefix: new HarmonyMethod(typeof(ItemSync), "PreSold"));
            else Plugin.Log.LogWarning("[item] OnSoldOrDisposedItem not found (game update?)");
        }

        static void Post(Harmony h, Type t, string name, string mine)
        {
            var m = AccessTools.Method(t, name);
            if (m == null) { Plugin.Log.LogWarning("[item] hook target missing: " + name); return; }
            h.Patch(m, postfix: new HarmonyMethod(typeof(ItemSync), mine));
        }

        static void PostModified(Item __0) { Queue(__0); }
        static void PostDamaged(float __0, Item __1) { Queue(__1); }

        static void Queue(Item item)
        {
            if (!Session.Active || Events.Applying || item == null) return;
            Pending.Add(item);
        }

        static FieldInfo _soldListField;
        static void PreSold(object __0)
        {
            if (!Session.Active || Events.Applying || __0 == null) return;
            if (_soldListField == null)
                foreach (var f in __0.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (typeof(IEnumerable<Item>).IsAssignableFrom(f.FieldType)) { _soldListField = f; break; }
            if (_soldListField == null) { Plugin.Log.LogWarning("[item] cannot read sold item list"); return; }
            var list = _soldListField.GetValue(__0) as IEnumerable<Item>; if (list == null) return;
            foreach (var it in list)
            {
                if (it == null) continue;
                var id = NetId(it, false);
                if (id != null) PendingDeleted.Add(id);
                Pending.Remove(it);
                if (id != null && id.StartsWith("c:")) { CreatedById.Remove(id); CreatedId.Remove(it); }
            }
        }

        // ---------- send ----------
        public static void Tick()
        {
            if (!Session.Active) { Pending.Clear(); PendingDeleted.Clear(); return; }
            foreach (var id in PendingDeleted)
            {
                LastJson.Remove(id);
                Events.Send("item", id, "{\"isDestroyed\":true}");
            }
            PendingDeleted.Clear();
            if (Pending.Count == 0) return;
            var list = new List<Item>(Pending); Pending.Clear();
            foreach (var it in list)
            {
                if (it == null || it.itemData == null) continue;
                try
                {
                    var id = NetId(it, true);
                    var mod = new ItemModification { instance = it, sceneGuid = id.StartsWith("s:") ? id.Substring(2) : null, prefabGuid = it.itemData.prefabGuid, currentItemState = it.currentItemState };
                    _mPreSave.Invoke(mod, new object[] { Hsm });
                    string json = JsonConvert.SerializeObject(mod, Ser);
                    string last;
                    if (LastJson.TryGetValue(id, out last) && last == json) continue;
                    LastJson[id] = json;
                    Events.Send("item", id, json);
                }
                catch (Exception e) { Plugin.Log.LogError("[item] send failed: " + e); }
            }
        }

        // ---------- apply ----------
        public static bool Apply(int origin, string netId, string json)
        {
            var mod = JsonConvert.DeserializeObject<ItemModification>(json);
            if (mod == null) return false;
            bool scene = netId.StartsWith("s:");
            Item inst = null;
            if (scene) { inst = FromGuid(netId.Substring(2)) as Item; mod.sceneGuid = netId.Substring(2); }
            else { CreatedById.TryGetValue(netId, out inst); if (inst == null) mod.sceneGuid = null; }
            if (inst == null && !scene && mod.isDestroyed) return true; // already gone
            if (inst == null && scene)
            {
                // scene item that no longer exists locally (already sold) - nothing to do
                if (!mod.isDestroyed) Plugin.Log.LogWarning("[item] unknown scene item " + netId);
                return false;
            }
            mod.instance = inst;
            if (mod.isDestroyed && inst != null) ReleaseZoom(inst);
            if (mod.isDestroyed && inst != null) RecordSale(inst, scene ? netId.Substring(2) : null);
            if (inst != null && !scene && !mod.isDestroyed)
            {
                // runtime-created item that already exists here: only update its state, do not instantiate again
                _mLoadState.Invoke(mod, new object[] { Hsm });
            }
            else
            {
                _mPostLoad.Invoke(mod, new object[] { Hsm });
                if (!scene && mod.instance != null) { CreatedById[netId] = mod.instance; CreatedId[mod.instance] = netId; MountSync.OnRemoteItem(mod.instance); }
            }
            if (mod.isDestroyed) { CreatedById.Remove(netId); LastJson.Remove(netId); }
            else LastJson[netId] = json;
            RecordInLocalSave(mod, netId);
            return true;
        }

        /// <summary>Keep this peer's own Recording (what the host saves) in sync with the applied change.</summary>
        /// <summary>Mark an item sold in this peer's Recording (same call the game's recorder makes) before the object is destroyed.</summary>
        /// <summary>The item was sold by someone else while the local player is assembling/cleaning it (camera locked on it): leave the zoom view first or the player stays stuck.</summary>
        static void ReleaseZoom(Item item)
        {
            try
            {
                foreach (var z in item.GetComponentsInChildren<HFCameras.CameraZoomable>(true))
                    if (RecSync.IsZoomed(z)) { z.LaunchTweenBackToFppCameraCoroutine(); Plugin.Log.LogInfo("[item] " + item.name + " was sold by another player while you were working on it: leaving the zoom view"); }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[item] zoom release failed: " + e.Message); }
        }

        static void RecordSale(Item item, string sceneGuid)
        {
            try
            {
                var h = Hsm as HouseSaveManager; if (h == null || h.Recorder == null || h.Recorder.currentRecording == null) return;
                var m = typeof(Recording).GetMethod("SellItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (m != null) m.Invoke(h.Recorder.currentRecording, new object[] { item, sceneGuid });
            }
            catch (Exception e) { Plugin.Log.LogWarning("[item] could not record sale locally: " + e.Message); }
        }

        static void RecordInLocalSave(ItemModification mod, string netId)
        {
            try
            {
                var h = Hsm as HouseSaveManager; if (h == null) return;
                var rec = h.Recorder; if (rec == null || rec.currentRecording == null) return;
                var item = mod.instance;
                if (mod.isDestroyed || item == null) return;
                var m = typeof(GameplayRecorder).GetMethod("OnBoughtOrModifiedItem", BindingFlags.Instance | BindingFlags.NonPublic);
                if (m != null) m.Invoke(rec, new object[] { item });
            }
            catch (Exception e) { Plugin.Log.LogWarning("[item] could not mirror into local recording: " + e.Message); }
        }
    }
}
