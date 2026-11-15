using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FrozenWay.HouseFlipper.Pets;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Pet movement and animation are shared. Every game ran its own pet AI, so a dog that sat on one screen walked around on the others. Each pet has one owner whose game runs its AI and sends
    /// its position, heading, animator values and animation state ten times a second; on the other games the pet's AI and navigation are paused, the pet glides to the owner's pose, takes the
    /// owner's animator values and is cross-faded into the owner's animation state when its own differs. The owner is the host, except while another player stands next to the pet: that player's
    /// game asks for it (so picking it up, petting and feeding run on the game of the player doing them, with the pet's real AI and navigation) and hands it back once they walk away.
    /// Reactions (petting, snacks, bowls) still travel as calls (PetAnimSync) and play on top.
    /// </summary>
    internal static class PetMotion
    {
        class Target { public Vector3 Pos; public float Yaw; public float[] Vals; public int[] States; public double Seen; public double StateWrongSince; }
        static readonly Dictionary<Pet, Target> Targets = new Dictionary<Pet, Target>();
        static readonly HashSet<Pet> Driven = new HashSet<Pet>();
        static readonly Dictionary<string, int> Owner = new Dictionary<string, int>();      // pet key -> player id that runs it (absent = the host)
        static readonly Dictionary<string, double> FarSince = new Dictionary<string, double>();
        static readonly Dictionary<string, double> Asked = new Dictionary<string, double>();
        static double _next, _nextClaim, _rateAt; static long _bytes;
        static readonly Dictionary<string, string> LastSent = new Dictionary<string, string>();
        static readonly Dictionary<string, double> SentAt = new Dictionary<string, double>();
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        const float ClaimRange = 3.5f, ReleaseRange = 6f;   // taken over before the player is close enough to interact (the AI needs a moment to be set up again)

        /// <summary>A pet whose AI is paused because it follows another player's game.</summary>
        public static bool IsDriven(Pet p) { return p != null && Driven.Contains(p); }

        /// <summary>A pet this game runs although it is not the host (the local player stands next to it).</summary>
        public static bool OwnedHere(Pet p) { if (p == null || Session.Role == Role.Host) return false; var k = PetSync.KeyForPet(p); int o; return k != null && Owner.TryGetValue(k, out o) && o == Session.MyId; }

        static bool MineToRun(string key) { int o; bool has = Owner.TryGetValue(key, out o); return Session.Role == Role.Host ? !has : (has && o == Session.MyId); }

        public static void Reset() { foreach (var p in Driven.ToList()) Release(p); Driven.Clear(); Targets.Clear(); Owner.Clear(); FarSince.Clear(); Asked.Clear(); LastSent.Clear(); SentAt.Clear(); }

        /// <summary>Host: a player left: the pets they ran come back to the host.</summary>
        public static void OnPlayerLeft(int id)
        {
            if (Session.Role != Role.Host) return;
            foreach (var k in Owner.Where(x => x.Value == id).Select(x => x.Key).ToList()) { Owner.Remove(k); Events.Send("pet", "own", k + "|-1"); TakeBack(k); }
        }

        static string F(float v) { return v.ToString("0.###", C); }

        // the local player (Camera.main is not set in this game: the camera is not tagged MainCamera)
        static Assets.Code.Player.PlayerController _pc; static double _pcAt;
        static bool MyPos(out Vector3 pos)
        {
            if (_pc == null && Time.realtimeSinceStartup >= _pcAt) { _pcAt = Time.realtimeSinceStartup + 2.0; _pc = UnityEngine.Object.FindObjectOfType<Assets.Code.Player.PlayerController>(); }
            pos = _pc != null ? _pc.transform.position : Vector3.zero; return _pc != null;
        }
        static float Flat(Vector3 a, Vector3 b) { a.y = 0; b.y = 0; return Vector3.Distance(a, b); }

        // ---------------- the game that runs a pet: send it ----------------
        public static void SendTick(Dictionary<string, Pet> pets)
        {
            if (Time.realtimeSinceStartup < _next || pets.Count == 0) return; _next = Time.realtimeSinceStartup + 0.1;
            try
            {
                var sb = new StringBuilder(); double now = Time.realtimeSinceStartup;
                foreach (var kv in pets)
                {
                    var p = kv.Value; if (p == null || !MineToRun(kv.Key)) continue; var t = p.transform;
                    int start = sb.Length;
                    sb.Append(kv.Key).Append(',').Append(F(t.position.x)).Append(',').Append(F(t.position.y)).Append(',').Append(F(t.position.z)).Append(',').Append(F(t.eulerAngles.y));
                    var an = p.GetComponentInChildren<Animator>();
                    if (an != null && an.isActiveAndEnabled && an.runtimeAnimatorController != null)
                    {
                        sb.Append('/');
                        foreach (var par in an.parameters)
                        {
                            if (par.type == AnimatorControllerParameterType.Trigger) continue;
                            float v = par.type == AnimatorControllerParameterType.Float ? an.GetFloat(par.nameHash) : par.type == AnimatorControllerParameterType.Int ? an.GetInteger(par.nameHash) : (an.GetBool(par.nameHash) ? 1 : 0);
                            sb.Append(F(v)).Append(' ');
                        }
                        sb.Append('/');
                        for (int l = 0; l < an.layerCount; l++) { var st = an.IsInTransition(l) ? an.GetNextAnimatorStateInfo(l) : an.GetCurrentAnimatorStateInfo(l); sb.Append(st.fullPathHash).Append(' '); }
                    }
                    // an animal that has not changed is sent once a second only (the followers drop a pet after 3 s without updates): idle animals cost almost nothing
                    string rec = sb.ToString(start, sb.Length - start); string last; double at;
                    if (LastSent.TryGetValue(kv.Key, out last) && last == rec && SentAt.TryGetValue(kv.Key, out at) && now - at < 1.0) { sb.Length = start; continue; }
                    LastSent[kv.Key] = rec; SentAt[kv.Key] = now;
                    sb.Append(';');
                }
                if (sb.Length > 0) { Events.Send("pet", "mo", sb.ToString()); _bytes += sb.Length; }
                if (now >= _rateAt) { if (_rateAt > 0 && Cfg.VerboseLog.Value) Plugin.Log.LogInfo("[petmo] motion sent: " + (_bytes / 10) + " bytes/s"); _bytes = 0; _rateAt = now + 10.0; }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[petmo] send failed: " + e.Message); }
        }

        // ---------------- other players: ask for a pet next to the local player, hand it back when far ----------------
        public static void ClaimTick(Dictionary<string, Pet> pets)
        {
            if (Session.Role == Role.Host || Time.realtimeSinceStartup < _nextClaim) return; _nextClaim = Time.realtimeSinceStartup + 0.25;
            Vector3 me; if (!MyPos(out me)) return; double now = Time.realtimeSinceStartup;
            foreach (var kv in pets)
            {
                var p = kv.Value; if (p == null || CoopSync.IsNested(p)) continue;
                float d = Flat(me, p.transform.position); bool mine = MineToRun(kv.Key);
                if (!mine && d < ClaimRange)
                {
                    int o; if (Owner.TryGetValue(kv.Key, out o) && o != Session.MyId) continue;   // another player is at it
                    double a; if (Asked.TryGetValue(kv.Key, out a) && now - a < 1.0) continue; Asked[kv.Key] = now;
                    Events.Send("pet", "claim", kv.Key + "|" + Session.MyId);
                }
                else if (mine)
                {
                    if (d < ReleaseRange) { FarSince.Remove(kv.Key); continue; }
                    double f; if (!FarSince.TryGetValue(kv.Key, out f)) { FarSince[kv.Key] = now; continue; }
                    if (now - f > 2.0) { FarSince.Remove(kv.Key); Events.Send("pet", "release", kv.Key + "|" + Session.MyId); }
                }
            }
        }

        /// <summary>claim / release (to the host) and own (from the host): who runs a pet.</summary>
        public static bool Ownership(string key, string json)
        {
            var f = json.Split('|'); if (f.Length < 2) return false; string pk = f[0]; int id = int.Parse(f[1], C);
            if (key == "claim")
            {
                if (Session.Role != Role.Host) return true;
                int o; if (Owner.TryGetValue(pk, out o) && o != id) return true;
                // the host player is at that pet too: the host keeps it (it may be in the middle of petting or carrying it)
                var hp = PetSync.PetByKey(pk); Vector3 me;
                if (hp != null && MyPos(out me) && Flat(me, hp.transform.position) < ClaimRange + 0.5f) { Events.Send("pet", "own", pk + "|-1"); return true; }
                Owner[pk] = id; Events.Send("pet", "own", pk + "|" + id); OnOwner(pk, id); return true;
            }
            if (key == "release")
            {
                if (Session.Role != Role.Host) return true;
                int o; if (!Owner.TryGetValue(pk, out o) || o != id) return true;
                Owner.Remove(pk); Events.Send("pet", "own", pk + "|-1"); TakeBack(pk); return true;
            }
            if (key == "own")
            {
                if (id < 0) Owner.Remove(pk); else Owner[pk] = id;
                OnOwner(pk, id); return true;
            }
            return false;
        }

        static void OnOwner(string pk, int id)
        {
            var p = PetSync.PetByKey(pk); if (p == null) return;
            if (MineToRun(pk)) { Targets.Remove(p); if (Driven.Remove(p)) Release(p); Plugin.Log.LogInfo("[petmo] " + p.name + " is run by this game now"); }
        }

        // the host gets a pet back: its AI runs it again from where the other player left it
        static void TakeBack(string pk) { var p = PetSync.PetByKey(pk); if (p == null) return; Targets.Remove(p); if (Driven.Remove(p)) Release(p); }

        // ---------------- the games that follow a pet ----------------
        public static bool Apply(string json)
        {
            try
            {
                double now = Time.realtimeSinceStartup;
                foreach (var rec in json.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = rec.Split('/'); var f = parts[0].Split(','); if (f.Length < 5) continue;
                    if (MineToRun(f[0])) continue;   // this game runs that pet (a late update from its previous owner)
                    var p = PetSync.PetByKey(f[0]); if (p == null) continue;
                    Target t; if (!Targets.TryGetValue(p, out t)) { t = new Target(); Targets[p] = t; }
                    t.Pos = new Vector3(float.Parse(f[1], C), float.Parse(f[2], C), float.Parse(f[3], C)); t.Yaw = float.Parse(f[4], C); t.Seen = now;
                    t.Vals = parts.Length > 1 ? parts[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, C)).ToArray() : null;
                    t.States = parts.Length > 2 ? parts[2].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x, C)).ToArray() : null;
                    PetSync.HostPose[p] = new KeyValuePair<Vector3, float>(t.Pos, t.Yaw);
                    if (!Driven.Contains(p)) Drive(p);
                    SetParams(p, t);
                }
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[petmo] could not apply: " + e.Message); return false; }
        }

        static void Drive(Pet p)
        {
            try
            {
                PetAI.Pause(p);
                var an = p.GetComponentInChildren<Animator>(); if (an != null) an.applyRootMotion = false;
                Driven.Add(p);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[petmo] could not take over " + p.name + ": " + e.Message); }
        }

        static void Release(Pet p)
        {
            if (p == null || CoopSync.IsNested(p)) return;   // a hen on a nest stays held by the coop sync
            try { PetAI.Resume(p); } catch { }
        }

        static void SetParams(Pet p, Target t)
        {
            var an = p.GetComponentInChildren<Animator>(); if (an == null || t.Vals == null || an.runtimeAnimatorController == null) return;
            int i = 0;
            foreach (var par in an.parameters)
            {
                if (par.type == AnimatorControllerParameterType.Trigger) continue; if (i >= t.Vals.Length) break; float v = t.Vals[i++];
                if (par.type == AnimatorControllerParameterType.Float) an.SetFloat(par.nameHash, v);
                else if (par.type == AnimatorControllerParameterType.Int) an.SetInteger(par.nameHash, (int)v);
                else an.SetBool(par.nameHash, v > 0.5f);
            }
        }

        /// <summary>Every frame: glide followed pets to their owner's pose, and fix the animation state when it stays different.</summary>
        static double _keepAt;
        public static void FollowTick()
        {
            if (Targets.Count == 0) return;
            double now = Time.realtimeSinceStartup; float k = 1f - Mathf.Exp(-12f * Time.deltaTime);
            if (now >= _keepAt) { _keepAt = now + 0.5; foreach (var dp in Driven) if (dp != null && !CoopSync.IsNested(dp)) PetAI.KeepPaused(dp); }
            foreach (var kv in Targets.ToList())
            {
                var p = kv.Key; var t = kv.Value;
                if (p == null) { Targets.Remove(p); Driven.Remove(p); continue; }
                if (now - t.Seen > 3.0) { Targets.Remove(p); if (Driven.Remove(p)) Release(p); continue; }   // no updates any more: its own AI takes over again
                var tr = p.transform;
                if ((tr.position - t.Pos).sqrMagnitude > 9f) tr.position = t.Pos; else tr.position = Vector3.Lerp(tr.position, t.Pos, k);
                tr.rotation = Quaternion.Slerp(tr.rotation, Quaternion.Euler(0, t.Yaw, 0), k);
                var an = p.GetComponentInChildren<Animator>(); if (an == null || t.States == null || !an.isActiveAndEnabled) continue;
                bool wrong = false;
                for (int l = 0; l < an.layerCount && l < t.States.Length; l++)
                {
                    var cur = an.IsInTransition(l) ? an.GetNextAnimatorStateInfo(l) : an.GetCurrentAnimatorStateInfo(l);
                    if (cur.fullPathHash == t.States[l] || t.States[l] == 0) continue;
                    wrong = true;
                    if (t.StateWrongSince > 0 && now - t.StateWrongSince > 0.25 && an.HasState(l, t.States[l])) an.CrossFadeInFixedTime(t.States[l], 0.15f, l);
                }
                if (!wrong) t.StateWrongSince = 0; else if (t.StateWrongSince == 0) t.StateWrongSince = now;
            }
        }
    }
}
