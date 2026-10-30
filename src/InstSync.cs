using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Doors and windows are installed into walls (the wall is swapped for one with a hole / the opening is closed again). The game records them in
    /// Recording.wallInstallations, not as normal item changes, so nothing reached the other peer: a door installed by one player never appeared on the other
    /// side and the wall kept standing there. We watch that list, send each new or changed installation with the door item's network id, and the other peer
    /// installs it through the game's own HouseSaveManager.ApplyNewWallModifications (the code path of loading a saved door, which also swaps the walls).
    /// The item gets the same network id on both peers, so selling it later is replicated by the normal item sync (which already restores the wall).
    /// </summary>
    internal static class InstSync
    {
        static readonly Dictionary<InstallationsModification, string> Known = new Dictionary<InstallationsModification, string>();
        static readonly Dictionary<InstallationsModification, string> KnownId = new Dictionary<InstallationsModification, string>(); // the door/window item id of each installation we know
        static readonly JsonSerializerSettings Ser = new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore, Formatting = Formatting.None };
        static readonly HashSet<InstallationsModification> _alive = new HashSet<InstallationsModification>();
        static readonly HashSet<InstallationsModification> _warned = new HashSet<InstallationsModification>();
        static bool _primed; static double _next;
        static MethodInfo _apply;

        static Recording Cur()
        {
            var h = typeof(HouseSaveManager).GetProperty("Instance").GetValue(null, null) as HouseSaveManager;
            return h != null && h.Recorder != null ? h.Recorder.currentRecording : null;
        }

        static PropertyInfo _itemProp; static bool _itemLooked;
        /// <summary>The door/window Item behind an installation record (the WallInstallation keeps it in its one Item-typed property; it is not always a parent object).</summary>
        public static Item ItemOf(InstallationsModification e)
        {
            if (e.item != null) return e.item;
            if (e.instance == null) return null;
            if (!_itemLooked)
            {
                _itemLooked = true;
                foreach (var p in typeof(Empyrean.HouseFlipper.WallModifiers.WallInstallation).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (p.PropertyType == typeof(Item) && p.CanRead) { _itemProp = p; break; }
            }
            Item it = _itemProp != null ? _itemProp.GetValue(e.instance, null) as Item : null;
            return it != null ? it : e.instance.GetComponentInParent<Item>();
        }

        static string Json(InstallationsModification e) { return JsonConvert.SerializeObject(e, Ser); }

        public static void Reset() { Known.Clear(); KnownId.Clear(); _alive.Clear(); _primed = false; _dupAt = 0; _dupRuns = 0; }

        // ---- doors/windows built twice in one frame ----
        // A save can contain the same door or window twice (two installation records for one place, or a scene door plus a copy). The records are cleaned when the save is
        // applied, but a pair that survived still stands in the house as two objects. Every peer looks for such pairs in its own house after it loaded and removes the extra one
        // locally: the other peers may not have the same doubles, so nothing is sent.
        static double _dupAt, _primedAt; static int _dupRuns;

        /// <summary>The door/window Item of a wall installation object (its Item-typed property, else the item it sits under).</summary>
        static Item ItemOfInstallation(Empyrean.HouseFlipper.WallModifiers.WallInstallation wi)
        {
            if (!_itemLooked)
            {
                _itemLooked = true;
                foreach (var p in typeof(Empyrean.HouseFlipper.WallModifiers.WallInstallation).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (p.PropertyType == typeof(Item) && p.CanRead) { _itemProp = p; break; }
            }
            Item it = _itemProp != null ? _itemProp.GetValue(wi, null) as Item : null;
            return it != null ? it : wi.GetComponentInParent<Item>();
        }

        static void DedupeLive(Recording rec)
        {
            try
            {
                var list = rec.wallInstallations; var drop = new List<InstallationsModification>();
                for (int i = 0; i < list.Count; i++)
                {
                    var a = list[i]; if (a == null || drop.Contains(a)) continue;
                    var ia = ItemOfLoaded(a); if (ia == null) continue;
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        var b = list[j]; if (b == null || drop.Contains(b) || b.windowGuid != a.windowGuid) continue;
                        var ib = ItemOfLoaded(b); if (ib == null || ib == ia) continue;
                        if (Vector3.Distance(ia.transform.position, ib.transform.position) > 0.25f) continue;
                        if (Mathf.Abs(Mathf.DeltaAngle(ia.transform.eulerAngles.y, ib.transform.eulerAngles.y)) > 15f) continue;
                        // keep the original (scene guid) over a copy; between equals the first
                        bool aScene = !string.IsNullOrEmpty(a.sceneGuid), bScene = !string.IsNullOrEmpty(b.sceneGuid);
                        if (!aScene && bScene) { drop.Add(a); break; }
                        drop.Add(b);
                    }
                }
                foreach (var d in drop)
                {
                    var it = ItemOf(d);
                    Plugin.Log.LogWarning("[inst] duplicate " + (it != null ? it.name : d.windowGuid) + " at the same spot: removing the extra one on this peer");
                    if (d.instance != null) UnityEngine.Object.Destroy(d.instance.gameObject);
                    list.Remove(d); Known.Remove(d); KnownId.Remove(d); _alive.Remove(d);
                    if (it != null) ItemSync.DiscardLocal(it);
                }
                if (drop.Count > 0) RecSync.RequestMeshRebuild();
                // leftovers: after the house was loaded a second time in one run (main menu and back, or the host's house changing) the previous load's door/window objects stayed in the
                // scene next to the new ones and no record points at them. They are the same door twice, at the same spot as a recorded one.
                var live = new HashSet<Empyrean.HouseFlipper.WallModifiers.WallInstallation>();
                foreach (var e in list) if (e != null && e.instance != null) live.Add(e.instance);
                var recItems = new List<Item>(); foreach (var e in list) { var ri = e != null ? ItemOfLoaded(e) : null; if (ri != null) recItems.Add(ri); }
                int orphans = 0;
                var prefabs = new HashSet<string>(); foreach (var e in list) if (e != null && !string.IsNullOrEmpty(e.windowGuid)) prefabs.Add(e.windowGuid);
                foreach (var oi in Resources.FindObjectsOfTypeAll<Item>())
                {
                    if (oi == null || !oi.gameObject.scene.IsValid() || !oi.gameObject.activeInHierarchy || oi.itemData == null || !prefabs.Contains(oi.itemData.prefabGuid) || recItems.Contains(oi)) continue;
                    bool same = false;
                    foreach (var ri in recItems)
                        if (ri.itemData != null && ri.itemData.prefabGuid == oi.itemData.prefabGuid && Vector3.Distance(ri.transform.position, oi.transform.position) < 0.25f && Mathf.Abs(Mathf.DeltaAngle(ri.transform.eulerAngles.y, oi.transform.eulerAngles.y)) < 15f) { same = true; break; }
                    if (!same) continue;
                    Plugin.Log.LogWarning("[inst] leftover " + oi.name + " from an earlier load stands at the same spot as the current one: removing it on this peer");
                    var owner = oi.GetComponentInParent<Empyrean.HouseFlipper.WallModifiers.WallInstallation>();
                    if (owner != null && !live.Contains(owner)) UnityEngine.Object.Destroy(owner.gameObject);
                    ItemSync.DiscardLocal(oi); orphans++;
                }                if (orphans > 0) RecSync.RequestMeshRebuild();
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[inst] duplicate check failed: " + ex.Message); }
        }

        public static void Prime()
        {
            var rec = Cur(); if (rec == null || rec.wallInstallations == null) return;
            Known.Clear();
            foreach (var e in rec.wallInstallations) { try { Known[e] = Json(e); var pit = ItemOfLoaded(e); string pid = pit != null ? ItemSync.NetIdPublic(pit, false) : null; if (pid != null) KnownId[e] = pid; } catch { } }
            _primed = true; _primedAt = Time.realtimeSinceStartup; _dupAt = Time.realtimeSinceStartup + 2f; _dupRuns = 0;
        }

        public static void Tick()
        {
            if (!Session.Active || Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 0.5f;
            var rec = Cur(); if (rec == null || rec.wallInstallations == null) return;
            if (!_primed) { Prime(); return; }
            if (_dupRuns < 12 && Time.realtimeSinceStartup > _dupAt)
            {
                var hsm = typeof(HouseSaveManager).GetProperty("Instance").GetValue(null, null) as HouseSaveManager;
                if (hsm != null && !hsm.isWaitingForApplyingSave && !Snapshot.Loading) { _dupRuns++; _dupAt = Time.realtimeSinceStartup + 4f; DedupeLive(rec); }
            }
            foreach (var e in new List<InstallationsModification>(rec.wallInstallations))
            {
                string js; try { js = Json(e); } catch (Exception ex) { Plugin.Log.LogWarning("[inst] serialize: " + ex.Message); continue; }
                string old; if (Known.TryGetValue(e, out old) && old == js) continue;
                var item = ItemOf(e);
                if (item == null) { if (_warned.Add(e)) Plugin.Log.LogWarning("[inst] installation without item yet: instance=" + (e.instance != null) + " guid=" + e.windowGuid); continue; } // retried on the next poll
                // a door that came with the house gets the id both peers compute from its place; "c:" ids are for things made during the session (the other peer cannot find those)
                if (Time.realtimeSinceStartup < _primedAt + 90f && ItemSync.NetIdPublic(item, false) == null) { if (!ItemSync.Settled()) continue; ItemSync.AssignAnonymousIds(); }
                string id = ItemSync.NetIdPublic(item, true); if (id == null) { if (_warned.Add(e)) Plugin.Log.LogWarning("[inst] no net id for " + item.name); continue; }
                Known[e] = js; KnownId[e] = id;
                ItemSync.QueuePublic(item); // the door must exist on the other peer before its installation arrives
                Events.Send("inst", id, js);
                if (Cfg.VerboseLog.Value) Plugin.Log.LogInfo("[inst] sent installation " + id + " (" + e.windowGuid + ")");
            }
            // an installation whose door/window was removed by a sale applied from another peer: the game only drops the record for local sales
            for (int i = rec.wallInstallations.Count - 1; i >= 0; i--)
            {
                var w = rec.wallInstallations[i];
                if (w.instance != null) _alive.Add(w);
                else if (_alive.Contains(w)) { rec.wallInstallations.RemoveAt(i); Known.Remove(w); _alive.Remove(w); KnownId.Remove(w); }
            }
            var gone = new List<InstallationsModification>();
            foreach (var k in Known.Keys) if (!rec.wallInstallations.Contains(k)) gone.Add(k);
            foreach (var k in gone)
            {
                Known.Remove(k);
                // the game removed the installation record: the door/window was sold. Say so explicitly; relying on the generic sale event missed doors built during the session.
                string gid; if (KnownId.TryGetValue(k, out gid)) { KnownId.Remove(k); ItemSync.AnnounceGone(gid); Plugin.Log.LogInfo("[inst] installation of " + gid + " was removed (sold): telling the others"); }
            }
        }

        /// <summary>
        /// An item that carries a wall installation (door/window) was sold by someone else. The installation (frame, wall plug, the record in our recording) is its own object next
        /// to the item, so destroying the item alone left it standing on this peer: remove it together with the item.
        /// </summary>
        public static void OnItemSold(Item item)
        {
            try
            {
                var rec = Cur(); if (rec == null || rec.wallInstallations == null || item == null) return;
                var e = rec.wallInstallations.Find(x => ItemOf(x) == item);
                if (e == null) return;
                if (e.instance != null) UnityEngine.Object.Destroy(e.instance.gameObject);
                rec.wallInstallations.Remove(e); Known.Remove(e); KnownId.Remove(e); _alive.Remove(e);
                Plugin.Log.LogInfo("[inst] removed the installation of a door/window sold by another player");
                RecSync.RequestMeshRebuild();
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[inst] could not remove a sold installation: " + ex.Message); }
        }

        /// <summary>ItemOf, and for a freshly loaded installation whose Item is not set yet: the item at its place (remembered in the record).</summary>
        public static Item ItemOfLoaded(InstallationsModification e)
        {
            var it = ItemOf(e); if (it != null) return it;
            it = FindNear(e, true); if (it != null) e.item = it;
            return it;
        }

        static Item FindNear(InstallationsModification mod) { return FindNear(mod, false); }
        static Item FindNear(InstallationsModification mod, bool allowKnown)
        {
            Item best = null; float bd = 0.6f;
            foreach (var it in Resources.FindObjectsOfTypeAll<Item>())
            {
                if (it == null || !it.gameObject.scene.IsValid() || !it.gameObject.activeInHierarchy || it.itemData == null || it.itemData.prefabGuid != mod.windowGuid) continue;
                if (!allowKnown && ItemSync.NetIdPublic(it, false) != null) continue; // already known: not the one just created
                float d = Vector3.Distance(it.transform.position, mod.position); if (d < bd) { bd = d; best = it; }
            }
            return best;
        }

        public static bool Apply(string id, string json)
        {
            var mod = JsonConvert.DeserializeObject<InstallationsModification>(json);
            if (mod == null) return false;
            bool scene = id.StartsWith("s:");
            var rec0 = Cur();
            if (!scene)
            {
                var old = ItemSync.ByNetId(id);
                if (old != null)
                {
                    // the plain copy made by the item sync is not installed in a wall: replace it by a real installation (swaps the wall like loading a saved door)
                    var have = rec0 != null && rec0.wallInstallations != null ? rec0.wallInstallations.Find(x => ItemOf(x) == old) : null;
                    if (have != null)
                    {
                        if (have.state == mod.state) return true; // nothing new
                        rec0.wallInstallations.Remove(have); Known.Remove(have); KnownId.Remove(have); // a later stage of the install (wall torn down, installed): rebuild it in that state
                        // the installation (door frame, the wall plug the hammer smashes) is its own object next to the item: destroying the item leaves it behind
                        if (have.instance != null) UnityEngine.Object.Destroy(have.instance.gameObject);
                    }
                    ItemSync.Discard(id);
                }
            }
            if (scene)
            {
                mod.sceneGuid = id.Substring(2);
                // the item already has an installation (frame, plug...) from the load: applying again builds a second one next to it, the door/window exists twice
                var prev = rec0 != null && rec0.wallInstallations != null ? rec0.wallInstallations.Find(x => x != null && x.sceneGuid == mod.sceneGuid) : null;
                if (prev != null)
                {
                    if (prev.instance != null) UnityEngine.Object.Destroy(prev.instance.gameObject);
                    rec0.wallInstallations.Remove(prev); Known.Remove(prev); KnownId.Remove(prev); _alive.Remove(prev);
                }
            }
            if (_apply == null) _apply = typeof(HouseSaveManager).GetMethod("ApplyNewWallModifications", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (_apply == null) { Plugin.Log.LogWarning("[inst] ApplyNewWallModifications not found"); return false; }
            var hsm = typeof(HouseSaveManager).GetProperty("Instance").GetValue(null, null);
            _apply.Invoke(hsm, new object[] { new List<InstallationsModification> { mod } });
            var rec = Cur();
            // the game finishes initialising a freshly placed installation a moment later (its Item is not known yet): find the door/window item by prefab and position
            var it = ItemOf(mod) ?? FindNear(mod);
            if (it != null) { mod.item = it; if (!scene) ItemSync.RegisterCreated(id, it); }
            else Plugin.Log.LogWarning("[inst] could not find the door/window item for " + id + ": it will not sync");
            if (rec != null && rec.wallInstallations != null)
            {
                var local = mod.instance != null ? rec.wallInstallations.Find(x => x.instance == mod.instance) : (it != null ? rec.wallInstallations.Find(x => x.item == it) : null);
                if (local == null) rec.wallInstallations.Add(mod); else { local.position = mod.position; local.rotation = mod.rotation; local.variants = mod.variants; local.state = mod.state; local.item = it != null ? it : local.item; mod = local; }
            }
            try { Known[mod] = Json(mod); KnownId[mod] = id; } catch { }
            Plugin.Log.LogInfo("[inst] installation " + id + " applied (" + mod.windowGuid + ", state " + mod.state + ")");
            // past the "smash the wall" stage the plug is gone for good
            if (mod.instance != null && (int)mod.state >= 1)
                foreach (var plug in mod.instance.GetComponentsInChildren<Empyrean.HouseFlipper.WallModifiers.DestroyableWallPlug>(true)) UnityEngine.Object.Destroy(plug.gameObject);
            RecSync.RequestMeshRebuild();
            return true;
        }
    }
}
