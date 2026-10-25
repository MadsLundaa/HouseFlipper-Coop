using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HFCoop
{
    /// <summary>Discovery helper: dumps live scene structure to the BepInEx log (F8 aim target, F9 histogram).</summary>
    internal static class Inspector
    {
        public static string Path(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        public static void DumpAim()
        {
            var cam = Camera.main;
            if (cam == null) { Plugin.Log.LogInfo("[insp] no main camera"); return; }
            var ray = new Ray(cam.transform.position, cam.transform.forward);
            if (!Physics.Raycast(ray, out var hit, 20f)) { Plugin.Log.LogInfo("[insp] nothing hit"); return; }
            Plugin.Log.LogInfo("[insp] hit " + Path(hit.collider.transform) + " layer=" + LayerMask.LayerToName(hit.collider.gameObject.layer) + " dist=" + hit.distance + " pos=" + hit.point);
            for (var t = hit.collider.transform; t != null; t = t.parent)
            {
                var comps = t.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().FullName);
                Plugin.Log.LogInfo("[insp]   " + t.name + " id=" + t.GetInstanceID() + " pos=" + t.position + " : " + string.Join(", ", comps.ToArray()));
            }
        }

        /// <summary>Dev test helper: lift the item under the crosshair and fire the game's own "item modified" handler.</summary>
        public static void NudgeAimedItem()
        {
            var cam = Camera.main; if (cam == null) return;
            if (!Physics.Raycast(new Ray(cam.transform.position, cam.transform.forward), out var hit, 20f)) { Plugin.Log.LogInfo("[dev] nothing aimed at"); return; }
            var it = hit.collider.GetComponentInParent<Item>();
            if (it == null) { Plugin.Log.LogInfo("[dev] aimed at non-item " + Path(hit.collider.transform)); return; }
            it.transform.position += Vector3.up * 0.5f;
            var rec = (typeof(HouseSaveManager).GetProperty("Instance").GetValue(null, null) as HouseSaveManager).Recorder;
            typeof(GameplayRecorder).GetMethod("OnBoughtOrModifiedItem", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(rec, new object[] { it });
            Plugin.Log.LogInfo("[dev] nudged " + Path(it.transform) + " -> " + it.transform.position);
        }

        public static void DumpHistogram()
        {
            var counts = new Dictionary<string, int>();
            foreach (var mb in Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (mb == null) continue;
                var n = mb.GetType().FullName;
                int c; counts.TryGetValue(n, out c); counts[n] = c + 1;
            }
            var scenes = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++) scenes.Add(SceneManager.GetSceneAt(i).name);
            Plugin.Log.LogInfo("[insp] scenes: " + string.Join(", ", scenes.ToArray()));
            foreach (var kv in counts.OrderByDescending(k => k.Value))
                Plugin.Log.LogInfo("[insp] " + kv.Value + " x " + kv.Key);
        }

        public static void DumpPlayer()
        {
            var pc = Object.FindObjectOfType<Assets.Code.Player.PlayerController>();
            if (pc == null) { Plugin.Log.LogInfo("[insp] no PlayerController"); return; }
            Plugin.Log.LogInfo("[insp] player " + Path(pc.transform) + " pos=" + pc.transform.position + " rot=" + pc.transform.rotation.eulerAngles);
            foreach (var t in pc.GetComponentsInChildren<Transform>().Take(80))
                Plugin.Log.LogInfo("[insp]   child " + Path(t));
        }
    }
}
