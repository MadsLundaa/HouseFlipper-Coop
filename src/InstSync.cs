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

        public static void Reset() { Known.Clear(); KnownId.Clear(); _alive.Clear(); _primed = false; }

        public static void Prime()
        {
            var rec = Cur(); if (rec == null || rec.wallInstallations == null) return;
            Known.Clear();
            foreach (var e in rec.wallInstallations) { try { Known[e] = Json(e); var pit = ItemOf(e); string pid = pit != null ? ItemSync.NetIdPublic(pit, false) : null; if (pid != null) KnownId[e] = pid; } catch { } }
            _primed = true;
        }

        public static void Tick()
        {
            if (!Session.Active || Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 0.5f;
            var rec = Cur(); if (rec == null || rec.wallInstallations == null) return;
            if (!_primed) { Prime(); return; }
            foreach (var e in new List<InstallationsModification>(rec.wallInstallations))
            {
                string js; try { js = Json(e); } catch (Exception ex) { Plugin.Log.LogWarning("[inst] serialize: " + ex.Message); continue; }
                string old; if (Known.TryGetValue(e, out old) && old == js) continue;
                var item = ItemOf(e);
                if (item == null) { if (_warned.Add(e)) Plugin.Log.LogWarning("[inst] installation without item yet: instance=" + (e.instance != null) + " guid=" + e.windowGuid); continue; } // retried on the next poll
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

        static Item FindNear(InstallationsModification mod)
        {
            Item best = null; float bd = 0.6f;
            foreach (var it in Resources.FindObjectsOfTypeAll<Item>())
            {
                if (it == null || !it.gameObject.scene.IsValid() || !it.gameObject.activeInHierarchy || it.itemData == null || it.itemData.prefabGuid != mod.windowGuid) continue;
                if (ItemSync.NetIdPublic(it, false) != null) continue; // already known: not the one just created
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
            if (scene) mod.sceneGuid = id.Substring(2);
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
