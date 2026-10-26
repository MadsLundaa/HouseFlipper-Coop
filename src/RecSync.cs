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
    /// Replicates "list-style" renovation changes (dirt cleaned, windows cleaned, wall sides painted, floors/ceilings/tiles, plaster, ...).
    /// The game's GameplayRecorder already writes each change into Recording.&lt;list&gt;. We watch those lists, ship new elements as JSON,
    /// and apply them on the other side by calling the game's own HouseSaveManager.Apply* method with a partial Recording that contains only the new elements.
    /// </summary>
    internal static class RecSync
    {
        sealed class Track
        {
            public string Field; public string[] Apply; public FieldInfo Fi; public Type Elem;
            public readonly HashSet<string> Known = new HashSet<string>();
            public bool Primed;
        }

        static readonly JsonSerializerSettings Ser = new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore, Formatting = Formatting.None };
        static readonly List<Track> Tracks = new List<Track>();
        static double _next;
        static bool _built;

        static readonly string[][] Table =
        {
            new[] { "cleanedCleanablesGuids", "ApplyCleanables" },
            new[] { "cleanedWindowsGuids", "ApplyExtendedDirts" },
            new[] { "extendedDirts", "ApplyExtendedDirts" },
            new[] { "modifiedWallSides", "ApplyWallsAndFloors" },
            new[] { "modifiedFloors", "ApplyWallsAndFloors" },
            new[] { "modifiedCeilings", "ApplyWallsAndFloors" },
            new[] { "modifiedStandaloneWallSides", "ApplyStandaloneWallSides" },
            new[] { "modifiedWalls", "ApplyWallsAndFloors" },       // experimental: demolition / construction
            new[] { "modifiedOuterWalls", "ApplyWallsAndFloors" },  // experimental
            new[] { "modifiedCavities", "ApplyCavities" },
            new[] { "destroyedCockroachesNests", "ApplyDestroyedCockroachesNest" },
            new[] { "spawnedPetDirts", "ApplySpawnedDirts" },
            // mission/script state
            new[] { "decisionsMade", "ApplyDecision" },
            new[] { "plotTwistActivated", "ApplyPlotTwists" },
            new[] { "actionWithTriggerActivated", "ApplyActions" },
            new[] { "cursesProgress", "ApplyCurseState" },
            new[] { "brokenSubitems", "ApplyBrokenSubitems" },
            // repairs: replaced sockets/switches, device state, swapped prefabs, cockpits, smashed items, changed windows
            new[] { "modifiedReplaceableItems", "ApplyRenewableItems" },
            new[] { "deviceStateSetterStates", "ApplyDeviceStateSetters" },
            new[] { "repairedCockpits", "ApplyRepairedCockpits" },
            new[] { "destroyableItemModifications", "ApplyDestroyableItems" },
            new[] { "changedWallModifications", "ApplyChangedWindows" },
        };

        static void Build()
        {
            _built = true;
            foreach (var row in Table)
            {
                var fi = typeof(Recording).GetField(row[0], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi == null || !fi.FieldType.IsGenericType) { Plugin.Log.LogWarning("[rec] field not found: " + row[0]); continue; }
                Tracks.Add(new Track { Field = row[0], Apply = new[] { row[1] }, Fi = fi, Elem = fi.FieldType.GetGenericArguments()[0] });
            }
        }

        static Recording Current()
        {
            var h = typeof(HouseSaveManager).GetProperty("Instance").GetValue(null, null) as HouseSaveManager;
            return h != null && h.Recorder != null ? h.Recorder.currentRecording : null;
        }

        public static void Reset() { foreach (var t in Tracks) { t.Known.Clear(); t.Primed = false; } }

        /// <summary>Mark everything currently in the recording as already synchronised (called after joining / when a session starts).</summary>
        public static void Prime()
        {
            if (!_built) Build();
            var rec = Current(); if (rec == null) return;
            foreach (var t in Tracks)
            {
                t.Known.Clear();
                var list = t.Fi.GetValue(rec) as IList; if (list == null) continue;
                foreach (var e in list) t.Known.Add(JsonConvert.SerializeObject(e, Ser));
                t.Primed = true;
            }
        }

        static double _meshAt;
        /// <summary>Rebuild the combined wall meshes shortly (after walls were swapped/removed by a remote change; the game does it from its own tool code).</summary>
        public static void RequestMeshRebuild() { _meshAt = Time.realtimeSinceStartup + 0.5; }

        static void MeshTick()
        {
            if (_meshAt <= 0 || Time.realtimeSinceStartup < _meshAt) return;
            _meshAt = 0;
            try
            {
                int n = 0;
                foreach (var c in UnityEngine.Object.FindObjectsOfType<WallsMeshCombiner>()) { c.BuildAll(); n++; }
                int fl = 0;
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Code.HouseBuildingBlocks.FloorTilesCombiner>()) { c.Rebuild(); fl++; }
                Plugin.Log.LogInfo("[rec] rebuilt " + n + " combined wall mesh(es) and " + fl + " floor tile mesh(es)");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[rec] wall mesh rebuild failed: " + (e.InnerException ?? e).Message); }
        }

        public static void Tick()
        {
            MeshTick();
            if (!Session.Active) { return; }
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 0.5f;
            if (!_built) Build();
            var rec = Current(); if (rec == null) return;
            foreach (var t in Tracks)
            {
                var list = t.Fi.GetValue(rec) as IList; if (list == null) continue;
                if (!t.Primed) { Prime(); return; }
                List<string> fresh = null;
                foreach (var e in list)
                {
                    string js;
                    try { js = JsonConvert.SerializeObject(e, Ser); } catch (Exception ex) { Plugin.Log.LogWarning("[rec] serialize " + t.Field + ": " + ex.Message); continue; }
                    if (t.Known.Add(js)) { if (fresh == null) fresh = new List<string>(); fresh.Add(js); }
                }
                if (fresh != null) Events.Send("rec", t.Field, "[" + string.Join(",", fresh.ToArray()) + "]");
            }
        }

        // ---- a window another player just finished while the local player is zoomed on it ----
        // Cleaning a window locks the camera onto it (zoom mode). Marking the window clean from the other peer disables it underneath that player, who
        // would stay locked in zoom mode with the window gone ("stuck until the map is restarted"). Before the window changes we release the zoom.
        public static bool ZoomFix = true;
        static FieldInfo _zoomFlag; static bool _zoomLooked;

        /// <summary>True while this zoomable (window, mountable device...) holds the local camera.</summary>
        public static bool IsZoomed(object zoomable)
        {
            if (!_zoomLooked)
            {
                _zoomLooked = true;
                foreach (var f in typeof(HFCameras.CameraZoomable).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    if (f.FieldType == typeof(bool)) { _zoomFlag = f; break; } // the first bool of CameraZoomable: "this zoomable currently holds the camera"
                if (_zoomFlag == null) Plugin.Log.LogWarning("[rec] zoom flag of CameraZoomable not found: a window cleaned by someone else can leave you stuck in zoom");
            }
            return _zoomFlag != null && (bool)_zoomFlag.GetValue(zoomable);
        }

        static void ReleaseWindowZoom(string field, Newtonsoft.Json.Linq.JArray arr)
        {
            try
            {
                foreach (var tok in arr)
                {
                    string guid = field == "cleanedWindowsGuids" ? tok.ToString() : (string)tok["guid"];
                    var ed = ItemSync.FromGuidPublic(guid) as Empyrean.HouseFlipper.ExtendedDirt.ExtendedDirt;
                    if (ed == null) continue;
                    if (!IsZoomed(ed)) continue;
                    // the game's own "cancel" refuses once the window is clean, and TryTween... refuses outside the fixed camera mode: go back to first person directly
                    bool tried = ed.TryTweenFromFixedToFppCamera();
                    if (!tried) ed.LaunchTweenBackToFppCameraCoroutine();
                    Plugin.Log.LogInfo("[rec] window " + guid + " finished by another player while you were cleaning it: leaving the zoom view (" + (tried ? "normal exit" : "forced exit") + ")");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[rec] zoom release failed: " + e.Message); }
        }

        /// <summary>Walls the game demolished leave empty entries in the floor's wall list; its own wall loader then fails on them (NullReferenceException) and nothing is applied.</summary>
        static void PurgeWallLists()
        {
            try
            {
                foreach (var sb in Resources.FindObjectsOfTypeAll<Empyrean.HouseFlipper.HouseManagement.StoreyBuilder>())
                {
                    if (!sb.gameObject.scene.IsValid() || sb.wallsMapBuilder == null) continue;
                    var list = sb.wallsMapBuilder.wallsList as IList; if (list == null) continue;
                    int removed = 0;
                    for (int i = list.Count - 1; i >= 0; i--) { var o = list[i] as UnityEngine.Object; if (o == null) { list.RemoveAt(i); removed++; } }
                    if (removed > 0) Plugin.Log.LogInfo("[rec] removed " + removed + " demolished wall entries from " + sb.name);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[rec] wall list cleanup failed: " + e.Message); }
        }

        // ---- walls demolished by another player ----
        // The game's loader removes walls through DestroyOnLoadAndNotify, which skips the "wall destroyed" event the live demolition raises: job counters
        // ("Destroy the walls 4/34") and the combined wall mesh never heard of it. While applying a remote change we raise the live event as well.
        public static void Patch(HarmonyLib.Harmony h)
        {
            var m = AccessTools.Method(typeof(Wall), "DestroyOnLoadAndNotify");
            if (m == null) { Plugin.Log.LogWarning("[rec] Wall.DestroyOnLoadAndNotify not found: remote demolition is not counted"); return; }
            h.Patch(m, prefix: new HarmonyMethod(typeof(RecSync), "PreWallDestroy"));
        }

        static void PreWallDestroy(Wall __instance)
        {
            if (!Events.Applying || __instance == null) return;
            try { __instance.NotifyDestroyedEvent(); } catch (Exception e) { Plugin.Log.LogWarning("[rec] wall destroyed event failed: " + e.Message); }
        }

        /// <summary>
        /// Demolished inner walls: destroy them through the same call the hammer ends with (Wall.DestroyAndNotify). The game's loader only deletes the wall object, so
        /// the floor's wall list, the combined wall mesh and the job counters never learn about it and the wall stays visible on the peer.
        /// </summary>
        static MethodInfo _hubRaise; static bool _hubLooked;
        static MethodInfo HubRaise()
        {
            if (_hubLooked) return _hubRaise; _hubLooked = true;
            foreach (var ty in typeof(Wall).Assembly.GetTypes())
            {
                bool ev = false;
                foreach (var fi in ty.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) if (fi.FieldType == typeof(Action<Wall>)) { ev = true; break; }
                if (!ev) continue;
                foreach (var m in ty.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(Wall) && m.ReturnType == typeof(void)) { _hubRaise = m; Plugin.Log.LogInfo("[rec] wall-destroyed hub event: " + ty.Name + "." + m.Name); return m; }
                }
            }
            Plugin.Log.LogWarning("[rec] wall-destroyed hub event not found: combined wall meshes of remote demolition are not refreshed");
            return null;
        }

        /// <summary>The live hammer ends by raising the game's static "wall destroyed" event; the combined wall mesh, the job quest (counter, markers) and room culling react to it.</summary>
        static void RaiseWallDestroyed(List<Wall> walls)
        {
            var m = HubRaise(); if (m == null) return;
            foreach (var w in walls) { try { if (w != null) m.Invoke(null, new object[] { w }); } catch (Exception e) { Plugin.Log.LogWarning("[rec] wall event failed: " + (e.InnerException ?? e).Message); } }
        }

        static List<Wall> SmashRemoteWalls(IList mods)
        {
            var smashed = new List<Wall>();
            try
            {
                Wall[] all = null; int n = 0;
                foreach (var o in mods)
                {
                    var mod = o as WallModification; if (mod == null) continue;
                    if (mod.type != WallModification.Type.Removed && mod.type != WallModification.Type.FracturedWall && mod.type != WallModification.Type.FracturedDoorWall) continue;
                    if (all == null) all = Resources.FindObjectsOfTypeAll<Wall>();
                    var gc = mod.GetGridCoords(); bool hit = false; int same = 0;
                    foreach (var w in all)
                    {
                        if (w == null || !w.gameObject.scene.IsValid()) continue;
                        if (!object.Equals(w.GetGridCoords(), gc)) continue;
                        same++;
                        if (!w.gameObject.activeInHierarchy) continue;
                        smashed.Add(w); w.DestroyAndNotify(false); n++; hit = true; break;
                    }
                    Plugin.Log.LogInfo("[rec] wall " + mod.description + " -> " + (hit ? "destroyed" : same > 0 ? "found but inactive" : "no wall at those coordinates"));
                }
                if (n > 0) Plugin.Log.LogInfo("[rec] demolished " + n + " wall(s) from another player");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[rec] remote demolition failed: " + e); }
            return smashed;
        }

        public static bool Apply(string field, string jsonArray)
        {
            if (!_built) Build();
            Track t = Tracks.Find(x => x.Field == field);
            if (t == null) { Plugin.Log.LogWarning("[rec] unknown field " + field); return false; }
            var rec = Current(); if (rec == null) return false;
            var listType = t.Fi.FieldType;
            var parsed = JsonConvert.DeserializeObject(jsonArray, listType) as IList;
            if (parsed == null) return false;
            // record each element as known so we never echo it back
            var arr = Newtonsoft.Json.Linq.JArray.Parse(jsonArray);
            foreach (var tok in arr) t.Known.Add(tok.ToString(Formatting.None));
            if (ZoomFix && (field == "cleanedWindowsGuids" || field == "extendedDirts")) ReleaseWindowZoom(field, arr);
            // partial recording containing only the new elements
            var partial = new Recording();
            t.Fi.SetValue(partial, parsed);
            try { partial.RestoreHardRefs(); } catch (Exception e) { Plugin.Log.LogWarning("[rec] RestoreHardRefs: " + e.Message); } // resolves material guids -> Material objects
            var hsm = typeof(HouseSaveManager).GetProperty("Instance").GetValue(null, null);
            bool isWallField = field == "modifiedWalls" || field == "modifiedOuterWalls";
            if (isWallField || field == "modifiedWallSides" || field == "modifiedFloors" || field == "modifiedCeilings") PurgeWallLists();
            List<Wall> smashedWalls = field == "modifiedWalls" ? SmashRemoteWalls(parsed) : null;
            if (isWallField) ProgressSync.Flush(); // our own pending statistics go out first, so what the demolition counts here is not sent back as if we had done it
            WallSidePatch.Touched = new List<KeyValuePair<Code.HouseBuildingBlocks.WallSide, Material>>();
            foreach (var name in t.Apply)
            {
                var m = hsm.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m == null) { Plugin.Log.LogWarning("[rec] apply method missing: " + name); continue; }
                try { m.Invoke(hsm, new object[] { partial }); }
                catch (Exception e) { Plugin.Log.LogError("[rec] " + name + " failed: " + (e.InnerException ?? e)); }
            }
            if (isWallField) ProgressSync.Rebase();
            if (field == "modifiedFloors" || field == "modifiedCeilings") RequestMeshRebuild(); // the floors are drawn by combined meshes: the loader only swaps the tile material
            foreach (var kv in WallSidePatch.Touched) { try { if (kv.Key != null) kv.Key.FinalizePaintProcess(kv.Value); } catch (Exception e) { Plugin.Log.LogWarning("[rec] FinalizePaintProcess: " + e.Message); } }
            WallSidePatch.Touched = null;
            // keep our own recording (what the host saves) consistent
            var local = t.Fi.GetValue(rec) as IList;
            if (local != null)
                for (int i = 0; i < parsed.Count; i++)
                {
                    string js = JsonConvert.SerializeObject(parsed[i], Ser);
                    bool have = false;
                    foreach (var e in local) { if (JsonConvert.SerializeObject(e, Ser) == js) { have = true; break; } }
                    if (!have) local.Add(parsed[i]);
                }
            if (smashedWalls != null && smashedWalls.Count > 0) RaiseWallDestroyed(smashedWalls); // after our recording lists the walls, the quest reads them
            return true;
        }
    }
}

namespace HFCoop
{
    /// <summary>
    /// The load path (ApplyWallSideSave) only swaps the renderer material; live painting goes through WallSide.FinalizePaintProcess, which also
    /// notifies the parent wall so the combined wall mesh is refreshed. We collect the wall sides touched by an applied delta and finalise them the live way.
    /// </summary>
    internal static class WallSidePatch
    {
        public static System.Collections.Generic.List<KeyValuePair<Code.HouseBuildingBlocks.WallSide, Material>> Touched;

        public static void Patch(HarmonyLib.Harmony h)
        {
            var m = HarmonyLib.AccessTools.Method(typeof(HouseSaveManager), "ApplyWallSideSave");
            if (m == null) { Plugin.Log.LogWarning("[rec] ApplyWallSideSave not found: live wall paint will not refresh meshes on peers"); return; }
            h.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(WallSidePatch), "Post"));
        }

        static void Post(Code.HouseBuildingBlocks.WallSide __0, object __1)
        {
            if (Touched == null || __0 == null) return;
            var f = __1.GetType().GetField("materialInstance");
            var mat = f != null ? f.GetValue(__1) as Material : null;
            if (mat != null) Touched.Add(new KeyValuePair<Code.HouseBuildingBlocks.WallSide, Material>(__0, mat));
        }
    }
}

