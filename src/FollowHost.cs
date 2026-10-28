using System.Collections.Generic;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// When the host moves to another house (accepts an order, completes a job, goes back to the office, buys a house...), clients follow.
    /// A house change is detected by the host's GameplayRecorder instance changing (a new house scene creates a new recorder); fresh houses
    /// (orders) never raise isWaitingForApplyingSave, so that flag alone is not enough. After the house settled the host announces it and clients fetch a snapshot.
    /// </summary>
    internal static class FollowHost
    {
        static object _lastRecorder; static double _changedAt; static bool _announce;

        public static void Reset() { _lastRecorder = null; _announce = false; _pending.Clear(); }

        // clients that have not yet reported "ready" in the new house: their events still describe the old one
        static readonly HashSet<int> _pending = new HashSet<int>(); static double _pendingUntil;
        public static bool IsPending(int id) { return _pending.Count > 0 && Session.Now < _pendingUntil && _pending.Contains(id); }
        public static void ClientReady(int id) { if (_pending.Remove(id)) Plugin.Log.LogInfo("[follow] #" + id + " is in the new house"); }

        /// <summary>Host, every frame before the trackers: notice the house changed and reset everything that remembers the old house.</summary>
        public static void Pre()
        {
            if (Session.Role != Role.Host || !Session.Active) return;
            var h = Snapshot.Hsm; if (h == null) return;
            object rec = h.Recorder;
            if (_lastRecorder == null) { _lastRecorder = rec; return; }
            if (rec == null || ReferenceEquals(rec, _lastRecorder)) return;
            _lastRecorder = rec; _announce = true; _changedAt = Session.Now;
            Plugin.Log.LogInfo("[follow] host loaded a different house");
            // the trackers compare the current state with what they sent before: against the new house everything would look new (and everything old gone) and be sent to
            // clients that are still in the old house (office doors appearing inside a job, doors doubled, items "sold")
            RecSync.Prime(); InstSync.Reset(); InstSync.Prime(); LightSync.Reset(); ItemSync.OnHouseChanged();
            _pending.Clear(); foreach (var id in new List<int>(Session.Players.Keys)) _pending.Add(id); _pendingUntil = Session.Now + 120.0;
            if (Session.PlayerCount > 1) Events.Send("house", "changing", "");
        }

        public static void Tick()
        {
            if (Session.Role != Role.Host) return;
            var h = Snapshot.Hsm; if (h == null) return;
            object rec = h.Recorder;
            if (_lastRecorder == null) { _lastRecorder = rec; return; }
            if (_announce && !h.isWaitingForApplyingSave && Session.Now - _changedAt > 6.0 && Plugin.SceneName.Length > 0 && Session.PlayerCount > 1)
            {
                _announce = false; ItemSync.AssignAnonymousIds();
                Plugin.Log.LogInfo("[follow] telling clients to follow into " + Plugin.SceneName);
                Events.Send("house", Plugin.SceneName, "");
            }
        }

        /// <summary>Client: the host loaded another house; fetch it.</summary>
        public static bool Apply()
        {
            if (Session.Role != Role.Client) return false;
            Plugin.Log.LogInfo("[follow] host changed house, requesting snapshot");
            Snapshot.Request();
            return true;
        }
    }
}
