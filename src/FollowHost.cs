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

        public static void Reset() { _lastRecorder = null; _announce = false; }

        public static void Tick()
        {
            if (Session.Role != Role.Host) return;
            var h = Snapshot.Hsm; if (h == null) return;
            object rec = h.Recorder;
            if (_lastRecorder == null) { _lastRecorder = rec; return; }
            if (!ReferenceEquals(rec, _lastRecorder) && rec != null) { _lastRecorder = rec; _announce = true; _changedAt = Session.Now; Plugin.Log.LogInfo("[follow] host loaded a different house"); }
            if (_announce && !h.isWaitingForApplyingSave && Session.Now - _changedAt > 6.0 && Plugin.SceneName.Length > 0 && Session.PlayerCount > 1)
            {
                _announce = false;
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
