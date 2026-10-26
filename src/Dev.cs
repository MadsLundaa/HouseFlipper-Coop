using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Code.GameManagers;
using UnityEngine;

namespace HFCoop
{
    /// <summary>Developer/test command channel: write lines into BepInEx/hfcoop-cmd-&lt;pid&gt;.txt and the plugin executes them. Only active when DevCommands is enabled in config.</summary>
    internal static class Dev
    {
        static System.Collections.Generic.Dictionary<string, string> _recSnap;
        static double _next;
        static string _file;

        public static void Tick()
        {
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 0.4f;
            if (!Cfg.DevCommands.Value && !File.Exists(Path.Combine(BepInEx.Paths.BepInExRootPath, "hfcoop-dev.enabled"))) return;
            if (_file == null) _file = Path.Combine(BepInEx.Paths.BepInExRootPath, "hfcoop-cmd-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".txt");
            if (!File.Exists(_file)) return;
            string[] lines;
            try { lines = File.ReadAllLines(_file); File.Delete(_file); } catch { return; }
            foreach (var l in lines)
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                try { Run(l.Trim()); } catch (Exception e) { Plugin.Log.LogError("[dev] '" + l + "' failed: " + e); }
            }
        }

        static string LabelOf(Component c)
        {
            foreach (var t in c.GetComponentsInChildren<Component>(true))
            {
                if (t == null) continue;
                var p = t.GetType().GetProperty("text");
                if (p != null && p.PropertyType == typeof(string)) { var s = p.GetValue(t, null) as string; if (!string.IsNullOrEmpty(s)) return s; }
            }
            return c.name;
        }

        static Item[] Near(float radius)
        {
            var pc = UnityEngine.Object.FindObjectOfType<Assets.Code.Player.PlayerController>();
            var p = pc != null ? pc.transform.position : Vector3.zero;
            return UnityEngine.Object.FindObjectsOfType<Item>().Where(i => i != null && (i.transform.position - p).magnitude < radius)
                .OrderBy(i => (i.transform.position - p).magnitude).ToArray();
        }

        static void Run(string line)
        {
            var a = line.Split(' ');
            Plugin.Log.LogInfo("[dev] > " + line);
            switch (a[0])
            {
                case "pos":
                    {
                        var pc = UnityEngine.Object.FindObjectOfType<Assets.Code.Player.PlayerController>();
                        Plugin.Log.LogInfo("[dev] local pos " + (pc != null ? pc.transform.position.ToString() : "none") + " scene " + Plugin.SceneName + " id " + Session.MyId);
                        foreach (var r in Session.Players.Values) Plugin.Log.LogInfo("[dev] remote #" + r.Id + " " + r.Name + " scene=" + r.Scene + " pos=" + r.TargetPos + " avatar=" + (r.Go != null && r.Go.activeSelf));
                        break;
                    }
                case "items":
                    {
                        var list = Near(a.Length > 1 ? float.Parse(a[1]) : 10f);
                        int n = 0;
                        foreach (var i in list.Take(40))
                            Plugin.Log.LogInfo("[dev] #" + (n++) + " " + Inspector.Path(i.transform) + " pos=" + i.transform.position + " trash=" + i.IsTrash() + " sell=" + i.CanBeSold() + " irremovable=" + i.IsIrremovable() + " weed=" + i.IsWeed());
                        Plugin.Log.LogInfo("[dev] total " + list.Length);
                        break;
                    }
                case "nudge":
                    {
                        var list = Near(30f).Where(i => !i.IsWeed() && !i.IsIrremovable()).ToArray();
                        int idx = a.Length > 1 && a[1] != "nearest" ? int.Parse(a[1]) : 0;
                        if (idx >= list.Length) { Plugin.Log.LogInfo("[dev] no such item"); break; }
                        var it = list[idx];
                        it.transform.position += Vector3.up * 0.5f;
                        var rec = (typeof(HouseSaveManager).GetProperty("Instance").GetValue(null, null) as HouseSaveManager).Recorder;
                        typeof(GameplayRecorder).GetMethod("OnBoughtOrModifiedItem", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(rec, new object[] { it });
                        Plugin.Log.LogInfo("[dev] nudged " + Inspector.Path(it.transform) + " -> " + it.transform.position);
                        break;
                    }
                case "ui":
                    foreach (var b in UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Selectable>())
                        if (b != null && b.isActiveAndEnabled) Plugin.Log.LogInfo("[dev] ui " + b.GetType().Name + " '" + LabelOf(b) + "' " + Inspector.Path(b.transform));
                    break;
                case "click":
                    {
                        string want = line.Substring(6).Trim().ToLowerInvariant();
                        foreach (var b in UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Button>())
                            if (b != null && b.isActiveAndEnabled && LabelOf(b).ToLowerInvariant().Contains(want)) { Plugin.Log.LogInfo("[dev] clicking " + LabelOf(b)); b.onClick.Invoke(); break; }
                        break;
                    }
                case "cleandirt":
                    {
                        var pc = UnityEngine.Object.FindObjectOfType<Assets.Code.Player.PlayerController>();
                        var p = pc != null ? pc.transform.position : Vector3.zero;
                        var d = UnityEngine.Object.FindObjectsOfType<Dirt>().Where(x => x != null && x.isActiveAndEnabled).OrderBy(x => (x.transform.position - p).magnitude).ToArray();
                        Plugin.Log.LogInfo("[dev] dirt objects: " + d.Length);
                        int idx = a.Length > 1 ? int.Parse(a[1]) : 0;
                        if (idx < d.Length) { Plugin.Log.LogInfo("[dev] ForceClean " + Inspector.Path(d[idx].transform) + " " + d[idx].GetType().Name); d[idx].ForceClean(); }
                        break;
                    }
                case "money":
                    if (a.Length > 1) MoneySync.DevAdd(double.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture));
                    Plugin.Log.LogInfo("[dev] balance " + MoneySync.Balance());
                    break;
                case "usekey":
                    {
                        // list interactables near the player with their cross-peer keys
                        var pc = UnityEngine.Object.FindObjectOfType<Assets.Code.Player.PlayerController>();
                        var p = pc != null ? pc.transform.position : Vector3.zero;
                        foreach (var i in UnityEngine.Object.FindObjectsOfType<Empyrean.HouseFlipper.Core.Interactions.Interactable>().Where(x => x != null && (x.transform.position - p).magnitude < 12f).OrderBy(x => (x.transform.position - p).magnitude).Take(15))
                            Plugin.Log.LogInfo("[dev] interactable " + i.GetType().Name + " " + Inspector.Path(i.transform) + " key=" + Interactions.KeyOf(i));
                        break;
                    }
                case "interact":
                    {
                        var pc = UnityEngine.Object.FindObjectOfType<Assets.Code.Player.PlayerController>();
                        var p = pc != null ? pc.transform.position : Vector3.zero;
                        var list = UnityEngine.Object.FindObjectsOfType<MovablePart>().Where(x => x != null).OrderBy(x => (x.transform.position - p).magnitude).ToArray();
                        int idx = a.Length > 1 ? int.Parse(a[1]) : 0;
                        if (idx < list.Length) { Plugin.Log.LogInfo("[dev] interacting " + list[idx].GetType().Name + " " + Inspector.Path(list[idx].transform)); list[idx].Interact(); }
                        break;
                    }
                case "savegame":
                    {
                        var h = typeof(HouseSaveManager).GetProperty("Instance").GetValue(null, null) as HouseSaveManager;
                        // SaveCurrentGame(saveType default) - same call the game's save interaction makes
                        typeof(HouseSaveManager).GetMethod("SaveCurrentGame").Invoke(h, new object[] { Type.Missing });
                        Plugin.Log.LogInfo("[dev] SaveCurrentGame invoked");
                        break;
                    }
                case "dispose":
                    {
                        var list = Near(30f).Where(i => i.CanBeSold() && !i.IsWeed() && !i.IsIrremovable()).ToArray();
                        int idx = a.Length > 1 ? int.Parse(a[1]) : 0;
                        if (idx >= list.Length) break;
                        var pc = UnityEngine.Object.FindObjectOfType<Assets.Code.Player.PlayerController>();
                        var f = typeof(Assets.Code.Player.PlayerController).GetField("itemsCoroutineManager", BindingFlags.Instance | BindingFlags.NonPublic);
                        var mgr = f.GetValue(pc) as Assets.Code.Player.ItemsCoroutineManager;
                        Plugin.Log.LogInfo("[dev] DisposeTrash " + Inspector.Path(list[idx].transform));
                        mgr.DisposeTrash(list[idx]);
                        break;
                    }
                case "paintwall":
                    {
                        var pc = UnityEngine.Object.FindObjectOfType<Assets.Code.Player.PlayerController>();
                        var p = pc != null ? pc.transform.position : Vector3.zero;
                        var sides = UnityEngine.Object.FindObjectsOfType<Code.HouseBuildingBlocks.WallSide>().Where(s => s != null && s.GetComponentInParent<Wall>() != null).OrderBy(s => (s.transform.position - p).magnitude).ToArray();
                        int idx = a.Length > 1 ? int.Parse(a[1]) : 0;
                        var ws = sides[idx];
                        var cur = ws.GetComponent<Renderer>().sharedMaterial;
                        var other = sides.Select(s => s.GetComponent<Renderer>()).Where(r => r != null && r.sharedMaterial != cur).Select(r => r.sharedMaterial).FirstOrDefault();
                        if (other == null) { Plugin.Log.LogInfo("[dev] no alternative material found"); break; }
                        ws.FinalizePaintProcess(other);
                        var rec = (typeof(HouseSaveManager).GetProperty("Instance").GetValue(null, null) as HouseSaveManager).Recorder;
                        typeof(GameplayRecorder).GetMethod("OnWallPainted", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(rec, new object[] { ws.GetComponentInParent<Wall>(), ws });
                        Plugin.Log.LogInfo("[dev] painted " + Inspector.Path(ws.transform) + " " + cur.name + " -> " + other.name);
                        break;
                    }
                case "wallmat":
                    {
                        var pc = UnityEngine.Object.FindObjectOfType<Assets.Code.Player.PlayerController>();
                        var p = pc != null ? pc.transform.position : Vector3.zero;
                        var sides = UnityEngine.Object.FindObjectsOfType<Code.HouseBuildingBlocks.WallSide>().Where(s => s != null && s.GetComponentInParent<Wall>() != null).OrderBy(s => (s.transform.position - p).magnitude).Take(4);
                        foreach (var s in sides) Plugin.Log.LogInfo("[dev] wallside " + Inspector.Path(s.transform) + " mat=" + s.GetComponent<Renderer>().sharedMaterial.name);
                        break;
                    }
                case "spawn":
                    {
                        // duplicate the nth nearby item as a new runtime-created item (same instantiate path as loading a bought item from a save)
                        var list = Near(30f).Where(i => i.CanBeSold() && !i.IsWeed() && !i.IsIrremovable() && !string.IsNullOrEmpty(i.itemData.prefabGuid)).ToArray();
                        int idx = a.Length > 1 ? int.Parse(a[1]) : 0;
                        var src = list[idx];
                        var gmType = typeof(GameManager);
                        object gm = null;
                        foreach (var f in gmType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) if (f.FieldType == gmType) { gm = f.GetValue(null); if (gm != null) break; }
                        if (gm == null) foreach (var p in gmType.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) if (p.PropertyType == gmType) { gm = p.GetValue(null, null); if (gm != null) break; }
                        var dm = ((GameManager)gm).dataManager;
                        Item prefab = null;
                        foreach (var m in typeof(DataManager).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                        {
                            if (m.ReturnType != typeof(Item)) continue; var ps = m.GetParameters(); if (ps.Length != 1 || ps[0].ParameterType != typeof(string)) continue;
                            prefab = m.Invoke(dm, new object[] { src.itemData.prefabGuid }) as Item; if (prefab != null) break;
                        }
                        if (prefab == null) { Plugin.Log.LogInfo("[dev] prefab lookup failed"); break; }
                        var pc = UnityEngine.Object.FindObjectOfType<Assets.Code.Player.PlayerController>();
                        var inst = UnityEngine.Object.Instantiate(prefab);
                        inst.transform.position = pc.transform.position + pc.transform.forward * 2f + Vector3.up * 0.2f;
                        var rec = (typeof(HouseSaveManager).GetProperty("Instance").GetValue(null, null) as HouseSaveManager).Recorder;
                        typeof(GameplayRecorder).GetMethod("OnBoughtOrModifiedItem", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(rec, new object[] { inst });
                        Plugin.Log.LogInfo("[dev] spawned " + Inspector.Path(inst.transform) + " prefab " + src.itemData.prefabGuid + " at " + inst.transform.position);
                        break;
                    }
                case "setwallguid":
                    {
                        var rec = (typeof(HouseSaveManager).GetProperty("Instance").GetValue(null, null) as HouseSaveManager).Recorder;
                        var mods = rec.currentRecording.modifiedWallSides;
                        if (mods.Count == 0) break;
                        var m = mods[mods.Count - 1];
                        m.GetType().GetField("materialGuid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(m, a[1]);
                        Plugin.Log.LogInfo("[dev] last wall side mod material guid set to " + a[1]);
                        break;
                    }
                case "buckets":
                    foreach (var b in UnityEngine.Object.FindObjectsOfType<PaintBucket>()) Plugin.Log.LogInfo("[dev] bucket " + Inspector.Path(b.transform) + " mat=" + b.material.name + " pos=" + b.transform.position + " name=" + b.GetPaintBucketNameTranslation());
                    break;
                case "windows":
                    {
                        // lists the windows (ExtendedDirt) with their guid, clean state and distance to the player
                        var pc = UnityEngine.Object.FindObjectOfType<Assets.Code.Player.PlayerController>();
                        int i = 0;
                        foreach (var ed in UnityEngine.Object.FindObjectsOfType<Empyrean.HouseFlipper.ExtendedDirt.ExtendedDirt>())
                        {
                            string g = ItemSync.GuidOfPublic(ed);
                            float d = pc != null ? Vector3.Distance(pc.transform.position, ed.transform.position) : -1f;
                            Plugin.Log.LogInfo("[dev] window #" + (i++) + " guid=" + g + " clean=" + ed.isClean + " dist=" + d.ToString("0.0") + " path=" + Inspector.Path(ed.transform));
                        }
                        break;
                    }
                case "devstates": // per mountable device: mounted?, setter start state, flaws, puddles near it
                    {
                        int i = 0;
                        var puds = new System.Collections.Generic.List<Puddle>();
                        foreach (var p in Resources.FindObjectsOfTypeAll<Puddle>()) if (p.gameObject.scene.IsValid() && p.gameObject.activeInHierarchy) puds.Add(p);
                        foreach (var md in UnityEngine.Object.FindObjectsOfType<MountableDevice>())
                        {
                            var it = md.GetComponentInParent<Item>();
                            var dss = md.GetComponentInParent<DeviceStateSetter>() ?? md.GetDeviceStateSetter();
                            var ssf = dss != null ? dss.GetType().GetField("startState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) : null;
                            var mc = md.GetComponentInChildren<MontageControl>();
                            int near = 0; foreach (var p in puds) if (Vector3.Distance(p.transform.position, md.transform.position) < 3f) near++;
                            Plugin.Log.LogInfo("[dev] devstate #" + (i++) + " id=" + (it != null ? ItemSync.NetIdPublic(it, false) : "-") + " mounted=" + md.IsFullyMounted() + " pct=" + md.MountedPercentage() + " setterStart=" + (ssf != null ? ssf.GetValue(dss) : "-") + " montage=" + (mc != null) + " puddlesNear=" + near + " item=" + (it != null ? it.name : "-"));
                        }
                        break;
                    }
                case "puddles": // every Puddle (water spill) in the scene: active?, where, and what it hangs under
                    {
                        int i = 0;
                        foreach (var p in Resources.FindObjectsOfTypeAll<Puddle>())
                        {
                            if (!p.gameObject.scene.IsValid()) continue;
                            var it = p.GetComponentInParent<Item>();
                            Plugin.Log.LogInfo("[dev] puddle #" + (i++) + " active=" + p.gameObject.activeInHierarchy + " scale=" + p.transform.localScale.x.ToString("0.00") + " pos=" + p.transform.position + " item=" + (it != null ? it.name + "/" + ItemSync.NetIdPublic(it, false) : "-") + " path=" + Inspector.Path(p.transform));
                        }
                        Plugin.Log.LogInfo("[dev] puddles total " + i);
                        break;
                    }
                case "reclists": // sizes of the wall/installation Recording lists; "reclists <field>" also dumps that list
                    {
                        var hsm = typeof(HouseSaveManager).GetProperty("Instance").GetValue(null, null) as HouseSaveManager;
                        var rec = hsm != null && hsm.Recorder != null ? hsm.Recorder.currentRecording : null;
                        if (rec == null) { Plugin.Log.LogInfo("[dev] no recording"); break; }
                        var sb = new System.Text.StringBuilder();
                        foreach (var n in new[] { "modifiedWalls", "modifiedOuterWalls", "modifiedWallsToRebuild", "modifiedWallSides", "wallInstallations", "legacyWallInstallations", "changedWallModifications", "destroyableItemModifications", "destroyedWalls", "architectWalls", "modifiedCavities" })
                        {
                            var f = typeof(Recording).GetField(n);
                            var l = f != null ? f.GetValue(rec) as System.Collections.IList : null;
                            sb.Append(n + "=" + (l != null ? l.Count.ToString() : "?") + " ");
                        }
                        Plugin.Log.LogInfo("[dev] reclists " + sb);
                        if (a.Length > 1)
                        {
                            var f2 = typeof(Recording).GetField(a[1]); var l2 = f2 != null ? f2.GetValue(rec) as System.Collections.IList : null;
                            if (l2 != null) foreach (var e in l2) Plugin.Log.LogInfo("[dev]   " + a[1] + ": " + Newtonsoft.Json.JsonConvert.SerializeObject(e, new Newtonsoft.Json.JsonSerializerSettings { ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore }));
                        }
                        break;
                    }
                case "wallsnear": // wallsnear <x> <z> [radius]: every Wall near a point (type, active, enabled colliders, position)
                    {
                        float wx = float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), wz = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                        float rad = a.Length > 3 ? float.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture) : 2f; int n = 0;
                        foreach (var w in Resources.FindObjectsOfTypeAll<Wall>())
                        {
                            if (!w.gameObject.scene.IsValid()) continue;
                            var p = w.transform.position; if (Mathf.Abs(p.x - wx) > rad || Mathf.Abs(p.z - wz) > rad) continue;
                            int cols = 0; foreach (var c in w.GetComponentsInChildren<Collider>()) if (c.enabled) cols++;
                            Plugin.Log.LogInfo("[dev] wall #" + (n++) + " " + w.name + " type=" + w.wallType + " active=" + w.gameObject.activeInHierarchy + " colliders=" + cols + " pos=" + p);
                        }
                        Plugin.Log.LogInfo("[dev] walls total " + n);
                        break;
                    }
                case "noid": // items that have neither a scene guid nor a net id (they cannot be synced)
                    {
                        int n = 0, all = 0;
                        foreach (var it in Resources.FindObjectsOfTypeAll<Item>())
                        {
                            if (!it.gameObject.scene.IsValid() || !it.gameObject.activeInHierarchy) continue;
                            all++;
                            if (ItemSync.NetIdPublic(it, false) != null) continue;
                            n++; if (n <= 25) Plugin.Log.LogInfo("[dev] noid #" + n + " " + it.name + " pos=" + it.transform.position + " path=" + Inspector.Path(it.transform));
                        }
                        Plugin.Log.LogInfo("[dev] noid total " + n + " of " + all + " items");
                        break;
                    }
                case "smash": // smash <n>: demolish the n-th (by position) inner wall like the hammer does (DestroyAndNotify)
                    {
                        int want = a.Length > 1 ? int.Parse(a[1]) : 0; var list = new System.Collections.Generic.List<Wall>();
                        foreach (var w in Resources.FindObjectsOfTypeAll<Wall>()) if (w.gameObject.scene.IsValid() && w.gameObject.activeInHierarchy && w.wallType.ToString() == "InnerFull") list.Add(w);
                        list.Sort((p, q) => { int c = p.transform.position.x.CompareTo(q.transform.position.x); return c != 0 ? c : p.transform.position.z.CompareTo(q.transform.position.z); });
                        if (want >= list.Count) { Plugin.Log.LogInfo("[dev] smash: only " + list.Count + " inner walls"); break; }
                        var t = list[want]; Plugin.Log.LogInfo("[dev] smash " + t.name + " at " + t.transform.position); t.DestroyAndNotify(false);
                        break;
                    }
                case "look": // what is in front of the camera: collider, owner components, renderer (compare host and client at the same spot)
                    {
                        var cam = Camera.main; if (cam == null) { Plugin.Log.LogInfo("[dev] look: no camera"); break; }
                        RaycastHit hit;
                        if (!Physics.Raycast(cam.transform.position, cam.transform.forward, out hit, 30f, ~0, QueryTriggerInteraction.Ignore)) { Plugin.Log.LogInfo("[dev] look: nothing hit"); break; }
                        var sb = new System.Text.StringBuilder();
                        for (var tr = hit.collider.transform; tr != null; tr = tr.parent) { sb.Append(tr.name).Append('{'); foreach (var c in tr.GetComponents<Component>()) if (c != null && !(c is Transform)) sb.Append(c.GetType().Name).Append(','); sb.Append("} < "); if (sb.Length > 700) break; }
                        var rend = hit.collider.GetComponentInChildren<Renderer>();
                        Plugin.Log.LogInfo("[dev] look: hit " + hit.collider.name + " dist=" + hit.distance.ToString("0.00") + " at " + hit.point + " chain=" + sb + " mat=" + (rend != null && rend.sharedMaterial != null ? rend.sharedMaterial.name : "-"));
                        break;
                    }
                case "probe": // probe <x> <y> <z>: colliders and enabled renderers around a world point (what is physically/visually there)
                    {
                        var P = new Vector3(float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture), float.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture));
                        int n = 0;
                        foreach (var c in Physics.OverlapSphere(P, 0.25f, ~0, QueryTriggerInteraction.Ignore)) { if (n++ > 12) break; Plugin.Log.LogInfo("[dev] probe collider " + c.name + " path=" + Inspector.Path(c.transform)); }
                        n = 0;
                        foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
                        {
                            if (!r.gameObject.scene.IsValid() || !r.enabled || !r.gameObject.activeInHierarchy || !r.bounds.Contains(P)) continue;
                            if (n++ > 15) break;
                            Plugin.Log.LogInfo("[dev] probe renderer " + r.name + " " + r.GetType().Name + " size=" + r.bounds.size + " mat=" + (r.sharedMaterial != null ? r.sharedMaterial.name : "-") + " path=" + Inspector.Path(r.transform));
                        }
                        Plugin.Log.LogInfo("[dev] probe done at " + P);
                        break;
                    }
                case "modpos": // world position of the recorded demolished walls
                    {
                        var hsm = typeof(HouseSaveManager).GetProperty("Instance").GetValue(null, null) as HouseSaveManager; var rec = hsm.Recorder.currentRecording;
                        foreach (var m in rec.modifiedWalls) Plugin.Log.LogInfo("[dev] modpos " + m.description + " -> " + m.GetWorldPosition());
                        break;
                    }
                case "floormat": // floormat <x> <z>: material of the floor tiles within 0.3 m of a point (compare host and client)
                    {
                        var P = new Vector3(float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), 0f, float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture));
                        int n = 0;
                        foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
                        {
                            if (!r.gameObject.scene.IsValid() || !r.gameObject.activeInHierarchy) continue;
                            var p = r.transform.position; if (Mathf.Abs(p.x - P.x) > 0.3f || Mathf.Abs(p.z - P.z) > 0.3f || p.y > 0.3f) continue;
                            if (n++ > 20) break;
                            Plugin.Log.LogInfo("[dev] floormat " + r.name + " en=" + r.enabled + " mat=" + (r.sharedMaterial != null ? r.sharedMaterial.name : "-") + " pos=" + p + " path=" + Inspector.Path(r.transform));
                        }
                        Plugin.Log.LogInfo("[dev] floormat done " + n);
                        break;
                    }
                case "recsnap": // remember every Recording field (JSON hash) ...
                case "recdiff": // ... and list which fields changed since (find out where the game records an action)
                    {
                        var hsm = typeof(HouseSaveManager).GetProperty("Instance").GetValue(null, null) as HouseSaveManager; var rec = hsm.Recorder.currentRecording;
                        var cur = new System.Collections.Generic.Dictionary<string, string>();
                        var ser = new Newtonsoft.Json.JsonSerializerSettings { ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore };
                        foreach (var f in typeof(Recording).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                        {
                            try { var v = f.GetValue(rec); var js = Newtonsoft.Json.JsonConvert.SerializeObject(v, ser); var l = v as System.Collections.IList; cur[f.Name] = (l != null ? l.Count + ":" : "") + js.GetHashCode(); }
                            catch (Exception) { cur[f.Name] = "?"; }
                        }
                        if (a[0] == "recsnap") { _recSnap = cur; Plugin.Log.LogInfo("[dev] recsnap stored " + cur.Count + " fields"); break; }
                        int n = 0;
                        foreach (var kv in cur) { string old; if (_recSnap == null || !_recSnap.TryGetValue(kv.Key, out old) || old != kv.Value) { Plugin.Log.LogInfo("[dev] recdiff changed: " + kv.Key + " " + (_recSnap != null && _recSnap.ContainsKey(kv.Key) ? _recSnap[kv.Key] : "-") + " -> " + kv.Value); n++; } }
                        Plugin.Log.LogInfo("[dev] recdiff " + n + " field(s) changed");
                        break;
                    }
                case "dirts": // census of Dirt objects by type: total, with scene guid, without (runtime-created)
                    {
                        var cnt = new System.Collections.Generic.Dictionary<string, int[]>();
                        foreach (var d in UnityEngine.Object.FindObjectsOfType<Dirt>())
                        {
                            string n = d.GetType().Name; int[] c; if (!cnt.TryGetValue(n, out c)) cnt[n] = c = new int[2];
                            c[string.IsNullOrEmpty(ItemSync.GuidOfPublic(d)) ? 1 : 0]++;
                        }
                        foreach (var kv in cnt) Plugin.Log.LogInfo("[dev] dirt " + kv.Key + ": with guid " + kv.Value[0] + ", runtime (no guid) " + kv.Value[1]);
                        Plugin.Log.LogInfo("[dev] dirt census done");
                        break;
                    }
                case "mounts": // lists every mountable device: item id, mounted?, parts
                    {
                        int i = 0;
                        foreach (var md in UnityEngine.Object.FindObjectsOfType<MountableDevice>())
                        {
                            var it = md.GetComponentInParent<Item>();
                            Plugin.Log.LogInfo("[dev] mount #" + (i++) + " id=" + (it != null ? ItemSync.NetIdPublic(it, false) : "-") + " mounted=" + md.IsFullyMounted() + " pct=" + md.MountedPercentage() + " item=" + (it != null ? it.name : "-") + " path=" + Inspector.Path(md.transform));
                        }
                        break;
                    }
                case "mountall": // mountall [index]: fully mount the unmounted devices the way the game does when the player finishes (fires the recorder hook)
                    {
                        int i = 0, only = a.Length > 1 ? int.Parse(a[1]) : -1;
                        foreach (var md in UnityEngine.Object.FindObjectsOfType<MountableDevice>())
                        {
                            if ((only < 0 || only == i) && !md.IsFullyMounted()) { md.EnsurePartsInitialized(); md.MountInstantly(true); Plugin.Log.LogInfo("[dev] mounted #" + i); }
                            i++;
                        }
                        break;
                    }
                case "zoomfix": RecSync.ZoomFix = a.Length < 2 || a[1] != "off"; Plugin.Log.LogInfo("[dev] zoomfix " + RecSync.ZoomFix); break;
                case "winzoom": // winzoom <index>: start cleaning that window (enters its zoom camera) like pressing E at it
                    {
                        var eds = UnityEngine.Object.FindObjectsOfType<Empyrean.HouseFlipper.ExtendedDirt.ExtendedDirt>();
                        int n = a.Length > 1 ? int.Parse(a[1]) : 0;
                        if (n >= eds.Length) { Plugin.Log.LogInfo("[dev] no window #" + n); break; }
                        eds[n].GoToDefaultPosition(false); // enters the fixed zoom camera on this window without the side/tool checks
                        Plugin.Log.LogInfo("[dev] winzoom " + n + " " + ItemSync.GuidOfPublic(eds[n]));
                        break;
                    }
                case "winstate": // zoom flag of every window (is the local camera locked on it?)
                    {
                        int i = 0;
                        foreach (var ed in UnityEngine.Object.FindObjectsOfType<Empyrean.HouseFlipper.ExtendedDirt.ExtendedDirt>())
                            Plugin.Log.LogInfo("[dev] winstate #" + (i++) + " zoomed=" + RecSync.IsZoomed(ed) + " clean=" + ed.isClean + " canInteract=" + ed.canInteract);
                        break;
                    }
                case "recapply": // recapply <field> <json array>: apply a remote-style list event locally
                    {
                        string field = a[1]; string json = string.Join(" ", a, 2, a.Length - 2);
                        Events.Applying = true;
                        try { Plugin.Log.LogInfo("[dev] recapply " + field + " -> " + RecSync.Apply(field, json)); } finally { Events.Applying = false; }
                        break;
                    }
                case "completion":
                    {
                        var gmT = typeof(GameManager); object gm = null;
                        foreach (var f in gmT.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) if (f.FieldType == gmT) { gm = f.GetValue(null); if (gm != null) break; }
                        foreach (var f in gmT.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            var v = f.GetValue(null); if (v == null || v == gm) continue;
                            var m = v.GetType().GetMethod("GetCompletion", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (m == null || m.GetParameters().Length != 0) continue;
                            var ready = v.GetType().GetMethod("IsReadyToComplete");
                            int c = (int)m.Invoke(v, null);
                            Plugin.Log.LogInfo("[dev] job completion=" + c + "% ready=" + (ready != null ? ready.Invoke(v, new object[] { c }) : "?"));
                            var qs = v.GetType().GetMethod("GetQuests"); if (qs != null) { var arr = (System.Collections.IEnumerable)qs.Invoke(v, null); int n = 0; foreach (var q in arr) { n++; try { var qt = q.GetType(); var d = qt.GetMethod("GetAllDoneQuestsNumber"); var t = qt.GetMethod("GetAllQuestsNumber"); Plugin.Log.LogInfo("[dev]   quest " + qt.Name + " done=" + (d != null ? d.Invoke(q, null) : "?") + "/" + (t != null ? t.Invoke(q, null) : "?")); } catch (System.Exception qe) { Plugin.Log.LogInfo("[dev]   quest " + q.GetType().Name + " (" + qe.Message + ")"); } } Plugin.Log.LogInfo("[dev] quests=" + n); }
                            break;
                        }
                        break;
                    }
                case "questpaint":
                    {
                        // paint wall sides with the exact bucket material the job's PaintingQuest asks for (what a real roller would do with the right tin)
                        int n = a.Length > 1 ? int.Parse(a[1]) : 4;
                        var quests = UnityEngine.Object.FindObjectsOfType<Empyrean.HouseFlipper.Quests.PaintingQuest>();
                        Plugin.Log.LogInfo("[dev] PaintingQuests in scene: " + quests.Length);
                        Material mat = null;
                        foreach (var q in quests) { if (q.paintBuckets != null && q.paintBuckets.Length > 0 && q.paintBuckets[0] != null) { mat = q.paintBuckets[0].material; Plugin.Log.LogInfo("[dev] quest " + q.name + " wants " + mat.name + " (" + q.paintBuckets[0].GetPaintBucketNameTranslation() + ")"); break; } }
                        if (mat == null) break;
                        var rec = (typeof(HouseSaveManager).GetProperty("Instance").GetValue(null, null) as HouseSaveManager).Recorder;
                        var hook = typeof(GameplayRecorder).GetMethod("OnWallPainted", BindingFlags.Instance | BindingFlags.NonPublic);
                        int total = 0;
                        foreach (var q in quests)
                        {
                            if (q.paintBuckets == null || q.paintBuckets.Length == 0 || q.paintBuckets[0] == null) continue;
                            var qm = q.paintBuckets[0].material;
                            foreach (var f in typeof(Empyrean.HouseFlipper.Quests.WallChangerQuest).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                            {
                                if (!typeof(System.Collections.IEnumerable).IsAssignableFrom(f.FieldType) || !f.FieldType.IsGenericType || f.FieldType.GetGenericArguments()[0] != typeof(Code.HouseBuildingBlocks.WallSide)) continue;
                                var list = f.GetValue(q) as System.Collections.IEnumerable; if (list == null) continue;
                                foreach (Code.HouseBuildingBlocks.WallSide ws in list) { if (ws == null) continue; ws.FinalizePaintProcess(qm); var w = ws.GetComponentInParent<Wall>(); if (w != null) hook.Invoke(rec, new object[] { w, ws }); total++; }
                            }
                        }
                        n = total;
                        Plugin.Log.LogInfo("[dev] painted " + n + " sides with " + mat.name);
                        break;
                    }
                case "wsmat":
                    {
                        var rec = Snapshot.Hsm.Recorder.currentRecording;
                        var walls = UnityEngine.Object.FindObjectsOfType<Wall>();
                        foreach (var mod in rec.modifiedWallSides)
                        {
                            bool found = false;
                            foreach (var w in walls)
                            {
                                foreach (var m in typeof(Wall).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                                {
                                    var ps = m.GetParameters();
                                    if (ps.Length != 1 || ps[0].ParameterType != mod.GetType() || m.ReturnType != typeof(bool)) continue;
                                    bool ok; try { ok = (bool)m.Invoke(w, new object[] { mod }); } catch { continue; }
                                    if (!ok) continue;
                                    if (mod.wallSideIndex < w.transform.childCount)
                                    {
                                        var r = w.transform.GetChild(mod.wallSideIndex).GetComponent<Renderer>();
                                        Plugin.Log.LogInfo("[dev] wsmat mod(" + mod.x + "," + mod.z + "," + mod.wallSideIndex + ") -> " + Inspector.Path(w.transform.GetChild(mod.wallSideIndex)) + " mat=" + (r != null ? r.sharedMaterial.name : "none"));
                                        found = true;
                                    }
                                }
                            }
                            if (!found) Plugin.Log.LogInfo("[dev] wsmat mod(" + mod.x + "," + mod.z + "," + mod.wallSideIndex + ") no wall found");
                        }
                        break;
                    }
                case "walls":
                    {
                        var pc = UnityEngine.Object.FindObjectOfType<Assets.Code.Player.PlayerController>();
                        var p = pc.transform.position;
                        foreach (var w in UnityEngine.Object.FindObjectsOfType<Wall>().OrderBy(x => (x.transform.position - p).magnitude).Take(25))
                            Plugin.Log.LogInfo("[dev] wall " + w.GetType().Name + " " + Inspector.Path(w.transform) + " pos=" + w.transform.position + " destroyable=" + (w.GetComponent<DestroyableWall>() != null) + " inner=" + w.IsInner());
                        break;
                    }
                case "tp":
                    {
                        var pc = UnityEngine.Object.FindObjectOfType<Assets.Code.Player.PlayerController>();
                        var cc = pc.GetComponent<CharacterController>(); var fps = pc.fpsController;
                        if (cc != null) cc.enabled = false; if (fps != null) fps.enabled = false;
                        var dest = new Vector3(float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture), float.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture));
                        pc.transform.position = dest; if (pc.playerRigidbody != null) pc.playerRigidbody.position = dest; Physics.SyncTransforms();
                        if (cc != null) cc.enabled = true; if (fps != null) fps.enabled = true;
                        Plugin.Log.LogInfo("[dev] teleported to " + pc.transform.position);
                        break;
                    }
                case "prog": Plugin.Log.LogInfo("[dev] progress " + ProgressSync.DevSummary()); break;
                case "proghash": Plugin.Log.LogInfo("[dev] hashes " + ProgressSync.DevHashes()); break;
                case "progbump": ProgressSync.DevBump(); Plugin.Log.LogInfo("[dev] progress " + ProgressSync.DevSummary()); break;
                case "reloadhouse":
                    {
                        var h = Snapshot.Hsm; var rec = h.MakeCurrentGameRecording();
                        Plugin.Log.LogInfo("[dev] reloading own house from its current recording (simulates the host changing house)");
                        h.LoadFromRecording(rec);
                        break;
                    }
                case "host": Session.StartHost(); break;
                case "join": Session.StartJoin(a.Length > 1 ? a[1] : Cfg.JoinAddress.Value); break;
                case "leave": Session.Leave(); break;
                case "dumprec":
                    {
                        var rec = (typeof(HouseSaveManager).GetProperty("Instance").GetValue(null, null) as HouseSaveManager).Recorder;
                        var path = Path.Combine(BepInEx.Paths.BepInExRootPath, "recording-dump-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".json");
                        File.WriteAllText(path, rec.currentRecording.ToJsonString());
                        Plugin.Log.LogInfo("[dev] wrote " + path);
                        break;
                    }
                default: Plugin.Log.LogInfo("[dev] unknown command"); break;
            }
        }
    }
}

