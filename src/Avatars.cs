using UnityEngine;

namespace HFCoop
{
    /// <summary>Placeholder avatars for remote players (the game's player rig is first-person only, no body model exists).</summary>
    internal static class Avatars
    {
        static readonly Color[] Palette = {
            new Color(0.9f, 0.3f, 0.2f), new Color(0.2f, 0.6f, 0.95f), new Color(0.3f, 0.8f, 0.3f), new Color(0.95f, 0.8f, 0.2f), new Color(0.8f, 0.4f, 0.9f)
        };

        static GameObject Make(RemotePlayer p)
        {
            var root = new GameObject("HFCoop_Avatar_" + p.Id);
            var col = Palette[p.Id % Palette.Length];
            var mat = new Material(Shader.Find("Standard") ?? Shader.Find("Diffuse")) { color = col };
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.transform.SetParent(root.transform, false); body.transform.localScale = new Vector3(0.5f, 0.8f, 0.5f);
            body.transform.localPosition = new Vector3(0, -0.1f, 0);
            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.transform.SetParent(root.transform, false); head.transform.localScale = Vector3.one * 0.3f;
            head.transform.localPosition = new Vector3(0, 0.62f, 0);
            var nose = GameObject.CreatePrimitive(PrimitiveType.Cube); // shows facing direction
            nose.transform.SetParent(head.transform, false); nose.transform.localScale = new Vector3(0.25f, 0.25f, 0.6f); nose.transform.localPosition = new Vector3(0, 0, 0.6f);
            // arms and a held-tool block follow the look pitch, so the avatar reads as a person holding something out in front
            var aim = new GameObject("Aim").transform; aim.SetParent(root.transform, false); aim.localPosition = new Vector3(0, 0.3f, 0);
            foreach (float side in new[] { -0.3f, 0.3f })
            {
                var arm = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                arm.transform.SetParent(aim, false); arm.transform.localScale = new Vector3(0.12f, 0.3f, 0.12f);
                arm.transform.localRotation = Quaternion.Euler(90, 0, 0); arm.transform.localPosition = new Vector3(side, -0.1f, 0.3f);
            }
            var tool = GameObject.CreatePrimitive(PrimitiveType.Cube); tool.name = "Tool";
            tool.transform.SetParent(aim, false); tool.transform.localScale = new Vector3(0.1f, 0.1f, 0.4f); tool.transform.localPosition = new Vector3(0.3f, -0.1f, 0.7f);
            foreach (var c in root.GetComponentsInChildren<Collider>()) Object.Destroy(c); // never interfere with the real physics
            foreach (var r in root.GetComponentsInChildren<Renderer>()) { r.sharedMaterial = mat; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; }
            var toolMat = new Material(mat); tool.GetComponent<Renderer>().sharedMaterial = toolMat; // its own material: colour follows the held tool
            p.Head = head.transform;
            return root;
        }

        static Color ToolColor(string tool)
        {
            int h = 17; foreach (char c in tool) h = h * 31 + c;
            return Color.HSVToRGB(Mathf.Abs(h % 360) / 360f, 0.7f, 0.95f); // stable per tool name, so each tool is recognisable at a glance
        }

        public static void Destroy(RemotePlayer p) { if (p.Go != null) Object.Destroy(p.Go); p.Go = null; }

        public static void Update()
        {
            foreach (var p in Session.Players.Values)
            {
                bool show = p.LastState > 0 && p.Scene == Plugin.SceneName && Session.Now - p.LastState < 5.0;
                if (!show) { if (p.Go != null) p.Go.SetActive(false); continue; }
                if (p.Go == null) { p.Go = Make(p); p.Pos = p.TargetPos; p.Yaw = p.TargetYaw; p.Pitch = p.TargetPitch; }
                p.Go.SetActive(true);
                float k = 1f - Mathf.Exp(-14f * Time.deltaTime);
                if ((p.TargetPos - p.Pos).sqrMagnitude > 25f) p.Pos = p.TargetPos; else p.Pos = Vector3.Lerp(p.Pos, p.TargetPos, k);
                p.Yaw = Mathf.LerpAngle(p.Yaw, p.TargetYaw, k); p.Pitch = Mathf.Lerp(p.Pitch, p.TargetPitch, k);
                p.Go.transform.position = p.Pos;
                p.Go.transform.rotation = Quaternion.Euler(0, p.Yaw, 0);
                if (p.Head != null) p.Head.localRotation = Quaternion.Euler(p.Pitch, 0, 0);
                var aim = p.Go.transform.Find("Aim");
                if (aim != null)
                {
                    aim.localRotation = Quaternion.Euler(Mathf.Clamp(p.Pitch, -60f, 60f), 0, 0);
                    var tool = aim.Find("Tool");
                    bool holding = !string.IsNullOrEmpty(p.Tool) && p.Tool != "MoveObjectsTool"; // the default "move objects" hands show no tool
                    if (tool != null) { tool.gameObject.SetActive(holding); if (holding) tool.GetComponent<Renderer>().sharedMaterial.color = ToolColor(p.Tool); }
                }
            }
        }

        static string Label(RemotePlayer p) { return p.Name + " #" + p.Id + (string.IsNullOrEmpty(p.Tool) ? "" : "  [" + p.Tool + "]"); }

        public static void DrawTags()
        {
            if (!Cfg.ShowNameTags.Value) return;
            var cam = Camera.main; if (cam == null) return;
            foreach (var p in Session.Players.Values)
            {
                if (p.Go == null || !p.Go.activeSelf) continue;
                var wp = p.Pos + Vector3.up * 1.2f;
                var sp = cam.WorldToScreenPoint(wp);
                if (sp.z <= 0) continue;
                var rect = new Rect(sp.x - 100, Screen.height - sp.y - 12, 200, 24);
                var st = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 14, fontStyle = FontStyle.Bold };
                st.normal.textColor = Color.black; GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), Label(p), st);
                st.normal.textColor = Color.white; GUI.Label(rect, Label(p), st);
            }
        }
    }
}

