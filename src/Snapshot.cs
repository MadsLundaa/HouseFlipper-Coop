using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HFCoop
{
    /// <summary>
    /// Late join: the host serialises its current house Recording (exactly what it would write to a save file) and the
    /// client loads it with the game's own HouseSaveManager.LoadFromRecording, i.e. joining == loading the host's save in memory.
    /// The client never writes it to disk.
    /// </summary>
    internal static class Snapshot
    {
        public static bool Loading;
        static bool _suspended; static double _suspendAt;
        /// <summary>Client: the host is changing house; events that arrive now describe the old house and are dropped until the new snapshot arrives.</summary>
        public static bool EventsSuspended { get { return _suspended; } }
        public static void SuspendEvents() { _suspended = true; _suspendAt = Session.Now; }
        /// <summary>True only while our own snapshot load runs; any other house change on a client is blocked (the host owns house changes).</summary>
        public static bool AllowLoad;
        /// <summary>Client: the host's house has been loaded (state that needs the house, e.g. money/progress, can be applied).</summary>
        public static bool Ready;
        public static bool CanApplyState { get { return Session.Role != Role.Client || (Ready && !Loading); } }
        /// <summary>True from receiving the host house until the player returns to the menu: such a house must never be saved over the player's own save.</summary>
        public static bool Tainted;
        static double _loadStart; static bool _sawLoadStart;
        static string _mapJson, _loadScene;
        static byte[] _queuedSnapshot; // a newer snapshot that arrived while one was still loading (the host moved on again)

        /// <summary>The game's HouseSaveManager. Its static Instance is only set once a house scene is loaded; from the main menu we reach it through GameManager.houseSaveManager.</summary>
        public static HouseSaveManager Hsm
        {
            get
            {
                var h = typeof(HouseSaveManager).GetProperty("Instance").GetValue(null, null) as HouseSaveManager;
                if (h != null) return h;
                var gmType = typeof(Code.GameManagers.GameManager);
                object gm = null;
                foreach (var f in gmType.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)) if (f.FieldType == gmType) { gm = f.GetValue(null); if (gm != null) break; }
                if (gm == null) foreach (var p in gmType.GetProperties(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)) if (p.PropertyType == gmType) { gm = p.GetValue(null, null); if (gm != null) break; }
                return gm == null ? null : ((Code.GameManagers.GameManager)gm).houseSaveManager as HouseSaveManager;
            }
        }

        public static byte[] Gzip(string s)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress)) { var b = Encoding.UTF8.GetBytes(s); gz.Write(b, 0, b.Length); }
                return ms.ToArray();
            }
        }

        public static string Gunzip(byte[] d)
        {
            using (var gz = new GZipStream(new MemoryStream(d), CompressionMode.Decompress))
            using (var o = new MemoryStream())
            {
                var buf = new byte[65536]; int n;
                while ((n = gz.Read(buf, 0, buf.Length)) > 0) o.Write(buf, 0, n);
                return Encoding.UTF8.GetString(o.ToArray());
            }
        }

        // ---- host ----
        public static void SendTo(Conn c)
        {
            Watchdog.Phase("snapshot.send"); var h = Hsm;
            if (h == null || Plugin.SceneName.Length == 0 || h.Recorder == null || h.Recorder.currentRecording == null) { Plugin.Log.LogInfo("[snap] host has no house loaded yet, telling #" + c.PlayerId + " to wait"); c.Send(new W(Msg.NotReady).Done()); return; }
            Recording rec = h.MakeCurrentGameRecording();
            if (rec == null) { Plugin.Log.LogWarning("[snap] host has no recording"); return; }
            ItemSync.AssignLoadedIds(rec); ItemSync.AssignAnonymousIds();
            string json = rec.ToJsonString();
            string map = ItemSync.ExportIdMap(rec);
            var gz = Gzip(json);
            c.Send(new W(Msg.Snapshot).S(Plugin.SceneName).S(map).Bytes(gz).Done());
            Plugin.Log.LogInfo("[snap] sent snapshot to #" + c.PlayerId + ": " + json.Length + " chars json, " + gz.Length + " bytes gz");
        }

        // ---- client ----
        static bool _awaiting, _notReady; static double _retryAt;
        public static void Request() { _awaiting = true; _notReady = false; _retryAt = Session.Now + 3.0; Session.SendAll(new W(Msg.SnapshotReq).Done()); Plugin.Log.LogInfo("[snap] requested snapshot"); }
        public static void HostNotReady() { _notReady = true; Session.Status = "Waiting for the host to load a house..."; }

        public static void Receive(R r)
        {
            _awaiting = false; _suspended = false;
            if (Loading)
            {
                // the host changed house again while we are still loading: remember the newest one and apply it as soon as this load ends
                string qs = r.S(); string qm = r.S(); var qgz = r.Bytes();
                _queuedSnapshot = new W(Msg.Snapshot).S(qs).S(qm).Bytes(qgz).Done();
                Plugin.Log.LogInfo("[snap] a newer snapshot arrived while loading; it will be loaded next");
                return;
            }
            string scene = r.S(); _mapJson = r.S(); var gz = r.Bytes(); _loadScene = scene;
            string json = Gunzip(gz);
            Plugin.Log.LogInfo("[snap] received snapshot for " + scene + " (" + json.Length + " chars)");
            Recording rec;
            try { rec = JsonConvert.DeserializeObject<Recording>(json); }
            catch (Exception e) { Session.LastError = "Could not read host's house data: " + e.Message; Plugin.Log.LogError("[snap] " + e); return; }
            HouseInfoExpander.Expand(rec);
            var h = Hsm; if (h == null) { Session.LastError = "Game is not ready to load a house yet (still starting?). Try again."; Session.Leave(Session.LastError); return; }
            Ready = false; Events.Applying = true; Loading = true; Tainted = true; _sawLoadStart = false; _loadStart = Session.Now;
            Session.Status = "Loading host's house...";
            ItemSync.Reset(); RecSync.Reset();
            if (Plugin.SceneName.Length == 0) LeaveMainMenu(); // joined from the main menu: do what "Continue Game" does before loading
            AllowLoad = true;
            ProgressSync.ApplyOfficeNow(); // the office decides "is this the office" while the house loads (PC registration, buyers outside): it must be the host's by then
            try { h.LoadFromRecording(rec); } finally { AllowLoad = false; }
        }

        /// <summary>Same preparation as the menu's Continue button: mark the game as started and hide the main menu.</summary>
        static void LeaveMainMenu()
        {
            try
            {
                var gmType = typeof(Code.GameManagers.GameManager);
                object gm = null;
                foreach (var f in gmType.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)) if (f.FieldType == gmType) { gm = f.GetValue(null); if (gm != null) break; }
                var g = gm as Code.GameManagers.GameManager; if (g == null) return;
                g.wasGameStarted = true;
                try
                {
                    var pm = g.popupManager; var mi = pm.GetType().GetMethod("CloseCurrentPopupWithType");
                    var et = mi.GetParameters()[0].ParameterType;
                    mi.Invoke(pm, new object[] { System.Enum.Parse(et, "StandardInfo") }); // the "house did not close properly" dialog
                }
                catch (Exception pe) { Plugin.Log.LogInfo("[snap] no popup to close: " + pe.Message); }
                var menu = g.GetMainMenu(); if (menu != null) menu.DisableMenu();
                Plugin.Log.LogInfo("[snap] left main menu for host's house");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[snap] could not close main menu: " + e.Message); }
        }

        public static void Tick()
        {
            if (_awaiting && _notReady && !Loading && Session.Role == Role.Client && Session.Now > _retryAt) Request();
            if (!Session.Active) { _awaiting = false; _suspended = false; }
            if (_suspended && Session.Now - _suspendAt > 60.0) _suspended = false; // the snapshot never came: do not stay deaf
            if (Tainted && !Loading && Plugin.SceneName.Length == 0) { Tainted = false; Plugin.Log.LogInfo("[snap] back in menu: saving allowed again"); }
            if (!Loading) return;
            var h = Hsm;
            if (h != null && h.isWaitingForApplyingSave) _sawLoadStart = true;
            double age = Session.Now - _loadStart;
            // fresh houses (orders) never raise isWaitingForApplyingSave: the right scene with a recorder also counts as loaded
            bool sceneUp = h != null && h.Recorder != null && h.Recorder.currentRecording != null && Plugin.SceneName.Length > 0 && Plugin.SceneName == _loadScene;
            bool done = h != null && !h.isWaitingForApplyingSave && ((_sawLoadStart && age > 3) || (sceneUp && age > 6) || age > 45);
            if (!done && age > 20)
            {
                // never leave the player on "Loading host's house...": give up waiting, say why in the log, and fetch the house again
                Plugin.Log.LogError("[snap] load did not finish after 20 s (waiting=" + (h != null && h.isWaitingForApplyingSave) + ", scene=" + Plugin.SceneName + ", expected=" + _loadScene + ", hsm=" + (h != null) + "): asking the host again");
                Loading = false; Events.Applying = false; Ready = false; Request();
                return;
            }
            if (!done) return;
            Loading = false; Events.Applying = false; Ready = true;
            if (h != null && h.Recorder != null) { ItemSync.ImportIdMap(h.Recorder.currentRecording, _mapJson); ItemSync.AssignAnonymousIds(); }
            RecSync.Prime(); InstSync.Prime(); ProgressSync.OnHouseReady();
            Session.Status = "Connected to " + Session.HostName;
            Plugin.Log.LogInfo("[snap] house loaded from host snapshot");
            Events.Send("house", "ready", ""); // the host now accepts our events again
            if (_queuedSnapshot != null) { var q = _queuedSnapshot; _queuedSnapshot = null; var rr = new R(q); Plugin.Log.LogInfo("[snap] loading the newer snapshot"); Receive(rr); }
        }
    }
}

namespace HFCoop
{
    internal static class HouseInfoExpander
    {
        /// <summary>
        /// A Recording that was serialised to JSON lost HouseInfo's [JsonIgnore] fields (isMailOrder, scene references...). The game's own loader re-expands the info
        /// through DataManager; without it a joiner loads an order house as a plain owned house (no job HUD, no order budget).
        /// </summary>
        public static void Expand(Recording rec)
        {
            try
            {
                if (rec == null || rec.houseInfo == null) return;
                var h = Snapshot.Hsm; if (h == null) return;
                var gmType = typeof(Code.GameManagers.GameManager); object gm = null;
                foreach (var f in gmType.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)) if (f.FieldType == gmType) { gm = f.GetValue(null); if (gm != null) break; }
                var dm = (gm as Code.GameManagers.GameManager).dataManager;
                foreach (var m in typeof(DataManager).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    var ps = m.GetParameters();
                    if (ps.Length != 1 || ps[0].ParameterType != typeof(HouseInfo) || m.ReturnType != typeof(HouseInfo)) continue;
                    var res = m.Invoke(dm, new object[] { rec.houseInfo }) as HouseInfo;
                    if (res != null) { Plugin.Log.LogInfo("[snap] expanded house info via " + m.Name + " (mail order: " + res.isMailOrder + ")"); rec.houseInfo = res; return; }
                }
                Plugin.Log.LogWarning("[snap] could not expand house info");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[snap] house info expand failed: " + e.Message); }
        }
    }
}
