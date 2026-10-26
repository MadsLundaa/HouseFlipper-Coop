using System.Collections.Generic;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Remote players as a small articulated figure (the game ships no human model: the player rig is first-person arms only). Legs swing with the walking speed, the
    /// arms reach for what the player holds, and the held tool is a copy of the real tool model: whenever the local player holds a tool its meshes are recorded relative
    /// to the camera, and the same shape is shown in the remote player's hands. A tool this machine has not held yet falls back to a coloured block.
    /// </summary>
    internal static class Avatars
    {
        static readonly Color[] Shirts = {
            new Color(0.9f, 0.3f, 0.2f), new Color(0.2f, 0.6f, 0.95f), new Color(0.3f, 0.8f, 0.3f), new Color(0.95f, 0.8f, 0.2f), new Color(0.8f, 0.4f, 0.9f)
        };
        static readonly Color Skin = new Color(0.94f, 0.76f, 0.62f), Pants = new Color(0.2f, 0.23f, 0.32f), Shoes = new Color(0.12f, 0.12f, 0.12f);


        sealed class Rig
        {
            public Transform Body, Head, Aim, ToolRoot, LegL, LegR, ArmL, ArmR;
            public Vector3 Last; public float Speed, Phase, Use; public string ShownTool = ""; public GameObject ToolModel;
        }
        static readonly Dictionary<int, Rig> Rigs = new Dictionary<int, Rig>();

        static Material Mat(Color c) { return new Material(Shader.Find("Standard") ?? Shader.Find("Diffuse")) { color = c }; }

        static Transform Prim(PrimitiveType type, Transform parent, string name, Vector3 pos, Vector3 scale, Material m)
        {
            var g = GameObject.CreatePrimitive(type); g.name = name;
            Object.Destroy(g.GetComponent<Collider>()); // never interfere with the real physics
            g.transform.SetParent(parent, false); g.transform.localPosition = pos; g.transform.localScale = scale;
            var r = g.GetComponent<Renderer>(); r.sharedMaterial = m; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return g.transform;
        }

        static GameObject Make(RemotePlayer p)
        {
            var root = new GameObject("HFCoop_Avatar_" + p.Id);
            var shirt = Mat(Shirts[p.Id % Shirts.Length]); var skin = Mat(Skin); var pants = Mat(Pants); var shoes = Mat(Shoes);
            var rig = new Rig();
            // origin = the player's capsule centre (the feet are 0.8 m below)
            rig.Body = Prim(PrimitiveType.Cube, root.transform, "Torso", new Vector3(0, 0.14f, 0), new Vector3(0.44f, 0.62f, 0.24f), shirt);
            Prim(PrimitiveType.Cube, root.transform, "Hips", new Vector3(0, -0.22f, 0), new Vector3(0.42f, 0.14f, 0.23f), pants);
            rig.Head = Prim(PrimitiveType.Sphere, root.transform, "Head", new Vector3(0, 0.62f, 0), new Vector3(0.27f, 0.3f, 0.27f), skin);
            Prim(PrimitiveType.Cube, rig.Head, "Nose", new Vector3(0, -0.05f, 0.5f), new Vector3(0.2f, 0.2f, 0.25f), skin);   // facing direction
            Prim(PrimitiveType.Sphere, rig.Head, "EyeL", new Vector3(-0.2f, 0.12f, 0.43f), new Vector3(0.14f, 0.14f, 0.1f), shoes);
            Prim(PrimitiveType.Sphere, rig.Head, "EyeR", new Vector3(0.2f, 0.12f, 0.43f), new Vector3(0.14f, 0.14f, 0.1f), shoes);
            Prim(PrimitiveType.Cylinder, rig.Head, "Cap", new Vector3(0, 0.42f, 0.02f), new Vector3(1.05f, 0.12f, 1.05f), shirt); // a cap in the player's colour
            foreach (float side in new[] { -1f, 1f })
            {
                var leg = new GameObject(side < 0 ? "LegL" : "LegR").transform; leg.SetParent(root.transform, false); leg.localPosition = new Vector3(side * 0.11f, -0.26f, 0);
                Prim(PrimitiveType.Capsule, leg, "Thigh", new Vector3(0, -0.26f, 0), new Vector3(0.17f, 0.28f, 0.17f), pants);
                Prim(PrimitiveType.Cube, leg, "Shoe", new Vector3(0, -0.52f, 0.04f), new Vector3(0.16f, 0.08f, 0.26f), shoes);
                if (side < 0) rig.LegL = leg; else rig.LegR = leg;
                var arm = GameObject.CreatePrimitive(PrimitiveType.Capsule); arm.name = side < 0 ? "ArmL" : "ArmR"; Object.Destroy(arm.GetComponent<Collider>());
                arm.transform.SetParent(root.transform, false);
                var ar = arm.GetComponent<Renderer>(); ar.sharedMaterial = shirt; ar.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                if (side < 0) rig.ArmL = arm.transform; else rig.ArmR = arm.transform;
            }
            // Aim = the eye: pitches with the look direction and carries whatever is held, at the same offsets the first-person camera sees it
            rig.Aim = new GameObject("Aim").transform; rig.Aim.SetParent(root.transform, false); rig.Aim.localPosition = new Vector3(0, 0.55f, 0.05f);
            rig.ToolRoot = new GameObject("ToolRoot").transform; rig.ToolRoot.SetParent(rig.Aim, false);
            Rigs[p.Id] = rig; rig.Last = p.Pos;
            p.Head = rig.Head;
            return root;
        }

        static Color ToolColor(string tool)
        {
            int h = 17; foreach (char c in tool) h = h * 31 + c;
            return Color.HSVToRGB(Mathf.Abs(h % 360) / 360f, 0.7f, 0.95f); // stable per tool name, so each tool is recognisable at a glance
        }

        public static void Destroy(RemotePlayer p) { if (p.Go != null) Object.Destroy(p.Go); p.Go = null; Rigs.Remove(p.Id); }

        // ---- blocky tool models: every tool the player can hold is built from a few boxes ----
        static readonly Color Wood = new Color(0.55f, 0.36f, 0.18f), Metal = new Color(0.62f, 0.64f, 0.68f), Dark = new Color(0.18f, 0.18f, 0.2f);

        static Transform Box(Transform parent, Vector3 hand, Vector3 off, Vector3 size, Material m, Vector3 euler = default(Vector3))
        {
            var t = Prim(PrimitiveType.Cube, parent, "b", hand + off, size, m);
            t.localRotation = Quaternion.Euler(euler);
            return t;
        }

        /// <summary>Builds the held item for a tool class name under <paramref name="root"/> (hand grip at (0.2,-0.3,0.42), pointing forward).</summary>
        static void BuildTool(Transform root, string tool, Color accent)
        {
            var wood = Mat(Wood); var metal = Mat(Metal); var dark = Mat(Dark); var acc = Mat(accent);
            var h = Vector3.zero; // the parent already sits at the grip
            string n = tool.ToLowerInvariant();
            if (n.Contains("tile") || n.Contains("panel") || n.Contains("floorandceiling")) // tiling / panel tool: a flat block with a very thin white tile on top, as seen in first person
            {
                Box(root, h, new Vector3(0, -0.02f, 0.2f), new Vector3(0.26f, 0.03f, 0.34f), dark);
                Box(root, h, new Vector3(0, 0.0f, 0.2f), new Vector3(0.23f, 0.008f, 0.3f), Mat(Color.white));
            }
            else if (n.Contains("nailhammer"))
            {
                Box(root, h, new Vector3(0, 0, 0.06f), new Vector3(0.04f, 0.04f, 0.3f), wood); Box(root, h, new Vector3(0, 0.02f, 0.2f), new Vector3(0.05f, 0.07f, 0.14f), metal);
            }
            else if (n.Contains("hammer") || n.Contains("shotgun")) // the wall-smashing sledge / the wall-blasting gun
            {
                if (n.Contains("shotgun")) { Box(root, h, new Vector3(0, 0, 0.12f), new Vector3(0.06f, 0.08f, 0.55f), dark); Box(root, h, new Vector3(0, -0.04f, -0.12f), new Vector3(0.06f, 0.14f, 0.2f), wood); Box(root, h, new Vector3(0, -0.08f, 0.02f), new Vector3(0.05f, 0.1f, 0.05f), metal, new Vector3(20, 0, 0)); }
                else { Box(root, h, new Vector3(0, 0, 0.08f), new Vector3(0.05f, 0.05f, 0.7f), wood); Box(root, h, new Vector3(0, 0.02f, 0.45f), new Vector3(0.28f, 0.12f, 0.12f), metal); }
            }
            else if (n.Contains("axe") || n.Contains("chainsaw") || n.Contains("roof"))
            {
                Box(root, h, new Vector3(0, 0, 0.08f), new Vector3(0.05f, 0.05f, 0.62f), wood);
                if (n.Contains("chainsaw")) { Box(root, h, new Vector3(0, 0, 0.1f), new Vector3(0.14f, 0.16f, 0.26f), acc); Box(root, h, new Vector3(0, 0, 0.45f), new Vector3(0.04f, 0.1f, 0.4f), metal); }
                else Box(root, h, new Vector3(0, 0.0f, 0.42f), new Vector3(0.03f, 0.16f, 0.2f), metal);
            }
            else if (n.Contains("shovel") || n.Contains("hoe") || n.Contains("gravel") || n.Contains("remover") || n.Contains("wallchanger"))
            {
                Box(root, h, new Vector3(0, 0, 0.08f), new Vector3(0.05f, 0.05f, 0.66f), wood); Box(root, h, new Vector3(0, 0, 0.5f), new Vector3(0.2f, 0.03f, 0.22f), metal, new Vector3(15, 0, 0));
            }
            else if (n.Contains("roller")) // paint roller: frame + the roller in the paint colour
            {
                Box(root, h, new Vector3(0, 0, 0.0f), new Vector3(0.04f, 0.04f, 0.4f), metal); Box(root, h, new Vector3(0, 0.0f, 0.22f), new Vector3(0.04f, 0.14f, 0.04f), metal);
                Box(root, h, new Vector3(0, 0.07f, 0.3f), new Vector3(0.3f, 0.09f, 0.09f), acc);
            }
            else if (n.Contains("paint") || n.Contains("brush") && !n.Contains("cutter") || n.Contains("trowel") || n.Contains("comb"))
            {
                Box(root, h, new Vector3(0, 0, 0.05f), new Vector3(0.04f, 0.04f, 0.28f), wood); Box(root, h, new Vector3(0, 0, 0.24f), new Vector3(0.12f, 0.03f, 0.12f), n.Contains("trowel") ? metal : acc);
            }
            else if (n.Contains("clean") || n.Contains("mop") || n.Contains("scrub") || n.Contains("sponge"))
            {
                Box(root, h, new Vector3(0, 0, 0.05f), new Vector3(0.04f, 0.04f, 0.46f), metal); Box(root, h, new Vector3(0, -0.01f, 0.3f), new Vector3(0.2f, 0.05f, 0.12f), acc);
            }
            else if (n.Contains("vacuum"))
            {
                Box(root, h, new Vector3(0, -0.02f, 0.0f), new Vector3(0.14f, 0.12f, 0.22f), acc); Box(root, h, new Vector3(0, 0, 0.3f), new Vector3(0.04f, 0.04f, 0.4f), metal); Box(root, h, new Vector3(0, -0.02f, 0.5f), new Vector3(0.16f, 0.03f, 0.08f), dark);
            }
            else if (n.Contains("washer") || n.Contains("shower") || n.Contains("flame") || n.Contains("blower") || n.Contains("dryer") || n.Contains("cutter") || n.Contains("mower"))
            {
                Box(root, h, new Vector3(0, -0.05f, -0.02f), new Vector3(0.06f, 0.16f, 0.1f), acc, new Vector3(-15, 0, 0)); Box(root, h, new Vector3(0, 0.02f, 0.2f), new Vector3(0.07f, 0.07f, 0.4f), metal);
                Box(root, h, new Vector3(0, 0.02f, 0.45f), new Vector3(0.03f, 0.03f, 0.1f), dark);
            }
            else if (n.Contains("seller") || n.Contains("simple") || n.Contains("interaction")) // the price scanner
            {
                Box(root, h, new Vector3(0, 0, 0.1f), new Vector3(0.12f, 0.06f, 0.24f), dark); Box(root, h, new Vector3(0, 0.035f, 0.14f), new Vector3(0.1f, 0.01f, 0.1f), acc);
            }
            else if (n.Contains("maker") || n.Contains("architect") || n.Contains("modificator") || n.Contains("cabled") || n.Contains("bulb"))
            {
                Box(root, h, new Vector3(-0.02f, 0.02f, 0.14f), new Vector3(0.26f, 0.2f, 0.02f), dark, new Vector3(-20, 0, 0)); Box(root, h, new Vector3(-0.02f, 0.02f, 0.15f), new Vector3(0.22f, 0.16f, 0.025f), acc, new Vector3(-20, 0, 0));
            }
            else // generic hand tool: handle + head
            {
                Box(root, h, new Vector3(0, 0, 0.08f), new Vector3(0.05f, 0.05f, 0.36f), wood); Box(root, h, new Vector3(0, 0, 0.3f), new Vector3(0.14f, 0.1f, 0.12f), acc);
            }
        }

        static void ShowTool(Rig rig, string tool, bool holding)
        {
            if (!holding) { if (rig.ToolModel != null) rig.ToolModel.SetActive(false); rig.ShownTool = ""; return; }
            if (rig.ShownTool != tool)
            {
                rig.ShownTool = tool;
                if (rig.ToolModel != null) Object.Destroy(rig.ToolModel);
                rig.ToolModel = new GameObject("Tool_" + tool); rig.ToolModel.transform.SetParent(rig.ToolRoot, false);
                rig.ToolModel.transform.localPosition = GripPos;
                BuildTool(rig.ToolModel.transform, tool, ToolColor(tool));
            }
            rig.ToolModel.SetActive(true);
        }

        static readonly Vector3 GripPos = new Vector3(0.2f, -0.3f, 0.42f);

        /// <summary>What a tool does while it is being used: swings, strokes, recoil... (u = 0 idle .. 1 in use, t = time with a per-player offset).</summary>
        static void Animate(Transform m, string tool, float u, float t)
        {
            string n = tool.ToLowerInvariant();
            Vector3 pos = Vector3.zero; Vector3 rot = Vector3.zero;
            float s = Mathf.Sin(t * 6f), a = 0.5f + 0.5f * s;
            if (n.Contains("nailhammer")) { a = 0.5f + 0.5f * Mathf.Sin(t * 9f); rot.x = Mathf.Lerp(10f, -35f, a); }
            else if (n.Contains("shotgun")) { float p = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(t * 2.5f)), 8f); pos.z = -0.1f * p; rot.x = -12f * p; }
            else if (n.Contains("hammer")) { a = Mathf.Pow(a, 3f); rot.x = Mathf.Lerp(25f, -70f, a); pos.z = 0.05f * a; }
            else if (n.Contains("chainsaw")) { pos += new Vector3(Mathf.Sin(t * 53f), Mathf.Sin(t * 61f), Mathf.Sin(t * 47f)) * 0.006f; rot.z = Mathf.Sin(t * 40f) * 2f; }
            else if (n.Contains("axe") || n.Contains("roof")) { a = Mathf.Pow(a, 2f); rot.x = Mathf.Lerp(25f, -60f, a); rot.z = 10f * s; }
            else if (n.Contains("shovel") || n.Contains("hoe") || n.Contains("gravel") || n.Contains("remover")) { pos.y = -0.12f * a; pos.z = 0.05f * a; rot.x = Mathf.Lerp(-5f, 35f, a); }
            else if (n.Contains("tile") || n.Contains("panel") || n.Contains("floorandceiling")) { float p = Mathf.Max(0f, Mathf.Sin(t * 3f)); pos.z = 0.07f * p; pos.y = -0.02f * p; }
            else if (n.Contains("roller")) { pos.y = Mathf.Sin(t * 3f) * 0.14f; pos.z = 0.04f; rot.x = Mathf.Sin(t * 3f) * 6f; }
            else if (n.Contains("paint") || n.Contains("brush") && !n.Contains("cutter") || n.Contains("trowel") || n.Contains("comb")) { pos.x = Mathf.Sin(t * 4f) * 0.12f; rot.z = Mathf.Sin(t * 4f) * 12f; }
            else if (n.Contains("clean") || n.Contains("mop") || n.Contains("scrub") || n.Contains("sponge")) { pos.x = Mathf.Sin(t * 5f) * 0.16f; rot.y = Mathf.Sin(t * 5f) * 10f; }
            else if (n.Contains("vacuum")) { pos.z = Mathf.Sin(t * 3f) * 0.1f; pos.y = Mathf.Sin(t * 22f) * 0.003f; }
            else if (n.Contains("washer") || n.Contains("shower") || n.Contains("flame") || n.Contains("blower") || n.Contains("dryer") || n.Contains("cutter") || n.Contains("mower"))
            { pos += new Vector3(Mathf.Sin(t * 37f), Mathf.Sin(t * 43f), 0f) * 0.005f; pos.z = -0.03f; rot.x = -3f; }
            else if (n.Contains("seller") || n.Contains("simple") || n.Contains("interaction")) { rot.y = Mathf.Sin(t * 2f) * 12f; }
            else { rot.x = Mathf.Sin(t * 5f) * 15f; }
            pos.y += Mathf.Sin(t * 1.7f) * 0.004f; rot.z += Mathf.Sin(t * 1.3f) * 1f; // idle breathing
            m.localPosition = GripPos + pos * u + new Vector3(0, Mathf.Sin(t * 1.7f) * 0.004f * (1f - u), 0);
            m.localRotation = Quaternion.Euler(rot * u);
        }

        static void Limb(Transform limb, Vector3 from, Vector3 to, float thick)
        {
            var d = to - from; float len = d.magnitude; if (len < 0.01f) return;
            limb.position = (from + to) * 0.5f; limb.rotation = Quaternion.FromToRotation(Vector3.up, d); limb.localScale = new Vector3(thick, len * 0.5f, thick);
        }

        /// <summary>Dev: show this tool class name on every remote avatar (empty = real tool).</summary>
        public static string Preview = ""; public static bool PreviewUse;

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
                Rig rig; if (!Rigs.TryGetValue(p.Id, out rig)) continue;
                rig.Head.localRotation = Quaternion.Euler(Mathf.Clamp(p.Pitch, -70f, 70f), 0, 0);
                rig.Aim.localRotation = Quaternion.Euler(Mathf.Clamp(p.Pitch, -60f, 60f), 0, 0);
                // walking: speed from the movement since the last frame
                var moved = p.Pos - rig.Last; moved.y = 0; rig.Last = p.Pos;
                float v = Time.deltaTime > 0 ? moved.magnitude / Time.deltaTime : 0f; if (v > 12f) v = 0f; // a teleport is not a sprint
                rig.Speed = Mathf.Lerp(rig.Speed, v, 1f - Mathf.Exp(-10f * Time.deltaTime));
                rig.Phase += rig.Speed * Time.deltaTime * 4.2f;
                float amp = Mathf.Clamp01(rig.Speed / 1.4f) * 38f, sw = Mathf.Sin(rig.Phase);
                rig.LegL.localRotation = Quaternion.Euler(sw * amp, 0, 0); rig.LegR.localRotation = Quaternion.Euler(-sw * amp, 0, 0);
                string held = Preview.Length > 0 ? Preview : p.Tool;
                bool holding = !string.IsNullOrEmpty(held) && held != "MoveObjectsTool"; // the default "move objects" hands hold nothing visible
                ShowTool(rig, held, holding);
                rig.Use = Mathf.Lerp(rig.Use, (p.Using || PreviewUse) && holding ? 1f : 0f, 1f - Mathf.Exp(-9f * Time.deltaTime));
                if (holding) Animate(rig.ToolModel.transform, held, rig.Use, Time.time + p.Id * 1.7f);
                var rt = p.Go.transform; Vector3 shL = rt.TransformPoint(-0.28f, 0.36f, 0), shR = rt.TransformPoint(0.28f, 0.36f, 0);
                Vector3 hL, hR;
                if (holding) { var tm = rig.ToolModel.transform; hR = tm.TransformPoint(0f, 0f, 0f); hL = tm.TransformPoint(-0.38f, -0.04f, -0.04f); }
                else
                {
                    float a = Mathf.Clamp01(rig.Speed / 1.4f) * 0.35f * sw;
                    hL = shL + rt.TransformDirection(new Vector3(-0.02f, -0.46f * Mathf.Cos(a), 0.46f * Mathf.Sin(a)));
                    hR = shR + rt.TransformDirection(new Vector3(0.02f, -0.46f * Mathf.Cos(a), -0.46f * Mathf.Sin(a)));
                }
                Limb(rig.ArmL, shL, hL, 0.12f); Limb(rig.ArmR, shR, hR, 0.12f);
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
