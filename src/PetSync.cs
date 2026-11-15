using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FrozenWay.HouseFlipper.Pets;
using FrozenWay.HouseFlipper.Pets.Saving;
using Newtonsoft.Json;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Pets (Pets/Farm DLC): a pet spawned on the host (adopted, loaded from a save, bought) did not exist for the other players, and pets walk on each player's own AI. The host sends every pet
    /// as the game's own PetSave when it appears (the client spawns it with PetsManager.LoadPetFromSave), tells when it goes away, and sends the positions twice a second; a client puts a pet
    /// back where the host's is when it has drifted more than 0.75 m (the local AI keeps animating it in between). Feeding, petting and other pet interactions are not shared.
    /// </summary>
    internal static class PetSync
    {
        static readonly Dictionary<string, Pet> Known = new Dictionary<string, Pet>();   // host: pets already announced; client: pets created from the host's announcements
        static readonly JsonSerializerSettings Ser = new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore, Formatting = Formatting.None };
        static double _next, _nextPos; static int _traced;

        public static void Reset() { PetMotion.Reset(); Known.Clear(); Last.Clear(); HostPose.Clear(); PendingAdds.Clear(); HostKeys.Clear(); _next = 0; _settledAt = 0; _leftDone = false; }

        // a pet is told apart by its save guid (the numeric id is 0 for the first pet and was shared by pets spawned together); without a guid the id is used
        static string KeyOfSave(PetSave s) { return s == null ? null : (!string.IsNullOrEmpty(s.guid) ? "g" + s.guid : "i" + s.id.ToString(CultureInfo.InvariantCulture)); }
        static string KeyOfPayload(string json) { try { var s = JsonConvert.DeserializeObject<PetSave>(json.Substring(json.IndexOf('|') + 1)); return KeyOfSave(s); } catch { return null; } }

        static string KeyOf(Pet p)
        {
            try { return KeyOfSave(p.PetToSave()); } catch { return null; }
        }

        // the same pet as the host's: same key and the same animal (pets without a save guid are keyed by their number, and the client's own pet number 1 is not the host's pet number 1)
        static bool SameAnimal(Pet p, PetSave host)
        {
            try { var s = p.PetToSave(); return s != null && KeyOfSave(s) == KeyOfSave(host) && s.type == host.type && s.breedID == host.breedID && s.name == host.name; } catch { return false; }
        }

        // A pet announced while this player's house is still loading was made before the walking mesh existed: the game stopped halfway and left a pet with no animation. Such announcements wait
        // until the house has settled; a creation that throws removes what it left behind and is tried again a few times.
        static readonly List<KeyValuePair<string, int>> PendingAdds = new List<KeyValuePair<string, int>>();
        static double _pendingAt;
        static bool Ready() { return ItemSync.Settled() && UnityEngine.AI.NavMesh.CalculateTriangulation().vertices.Length > 0; }
        static void PendingTick()
        {
            if (PendingAdds.Count == 0 || Time.realtimeSinceStartup < _pendingAt) return;
            _pendingAt = Time.realtimeSinceStartup + 1.0;
            if (!Ready()) return;
            var batch = PendingAdds.ToList(); PendingAdds.Clear();
            foreach (var kv in batch) Create(kv.Key, kv.Value);
        }

        static void Create(string json, int attempt)
        {
            int bar = json.IndexOf('|'); string pk = json.Substring(0, bar);
            var save = JsonConvert.DeserializeObject<PetSave>(json.Substring(bar + 1)); if (save == null) return;
            Pet existing; if (Known.TryGetValue(pk, out existing) && existing != null) return;
            foreach (var p in UnityEngine.Object.FindObjectsOfType<Pet>()) if (p != null && !Known.ContainsValue(p) && SameAnimal(p, save)) { Known[pk] = p; return; } // the game already made it (its own save)
            var pm = UnityEngine.Object.FindObjectOfType<PetsManager>(); if (pm == null) { PendingAdds.Add(new KeyValuePair<string, int>(json, attempt)); return; }
            var before = new HashSet<Pet>(UnityEngine.Object.FindObjectsOfType<Pet>());
            try
            {
                save.usePositionFromSave = true; save.spawnOnLastPosition = true;
                var pet = pm.LoadPetFromSave(save);
                if (pet != null) { Known[pk] = pet; Plugin.Log.LogInfo("[pet] created pet " + pk + " " + save.name + " (" + save.type + ") from the host"); }
                else Plugin.Log.LogWarning("[pet] the game would not create pet " + pk);
            }
            catch (Exception e)
            {
                foreach (var p in UnityEngine.Object.FindObjectsOfType<Pet>()) if (p != null && !before.Contains(p) && !Known.ContainsValue(p)) UnityEngine.Object.Destroy(p.gameObject);
                if (attempt < 5) { PendingAdds.Add(new KeyValuePair<string, int>(json, attempt + 1)); _pendingAt = Time.realtimeSinceStartup + 3.0; }
                Plugin.Log.LogWarning("[pet] creating pet " + pk + " failed (attempt " + (attempt + 1) + "), " + (attempt < 5 ? "trying again" : "giving up") + ": " + (e.InnerException ?? e).Message);
            }
        }
        static readonly Dictionary<Pet, string> HostKeys = new Dictionary<Pet, string>();   // host: the key given to each pet when it was first seen (kept stable, made unique when two pets would share one)
        static string HostKey(Pet p)
        {
            string k;
            if (HostKeys.TryGetValue(p, out k)) return k;
            k = KeyOf(p); if (k == null) return null;
            string b = k; for (int n = 1; HostKeys.Values.Contains(k); n++) k = b + "#" + n;
            HostKeys[p] = k; return k;
        }

        // ---- stats (feeding, watering, petting, resting): a jump in a pet's hunger/thirst/happiness/energy on either side is sent to the other; normal decay is not (both sides run it) ----
        static readonly Dictionary<string, float[]> Last = new Dictionary<string, float[]>();
        static double _nextStat;
        static readonly string[] StatNames = { "hunger", "thirst", "happiness", "energy" };
        static readonly string[] Setters = { "SetHunger", "SetThirst", "SetHappiness", "SetEnergy" };
        static readonly float[] Jump = { 0.15f, 0.15f, 0.08f, 0.15f };

        static float[] ReadStats(Pet p)
        {
            var s = p.PetToSave(); if (s == null) return null; object st = s.petStats; var t = st.GetType(); var r = new float[StatNames.Length];
            for (int i = 0; i < r.Length; i++) { var f = t.GetField(StatNames[i]); if (f == null) return null; r[i] = (float)f.GetValue(st); }
            return r;
        }

        static void StatTick()
        {
            if (!Session.Active || !ItemSync.Settled() || Known.Count == 0 || Time.realtimeSinceStartup < _nextStat) return;
            _nextStat = Time.realtimeSinceStartup + 1.5;
            try
            {
                foreach (var kv in Known.ToList())
                {
                    var p = kv.Value; if (p == null) continue; var cur = ReadStats(p); if (cur == null) continue;
                    float[] last;
                    if (!Last.TryGetValue(kv.Key, out last)) { Last[kv.Key] = cur; continue; }
                    bool jumped = false; for (int i = 0; i < cur.Length; i++) if (Math.Abs(cur[i] - last[i]) > Jump[i]) jumped = true;
                    if (!jumped) { Last[kv.Key] = cur; continue; }
                    Last[kv.Key] = cur;
                    var sb = new StringBuilder().Append(kv.Key);
                    foreach (var v in cur) sb.Append(',').Append(v.ToString("0.####", CultureInfo.InvariantCulture));
                    Events.Send("pet", "stat", sb.ToString());
                    Plugin.Log.LogInfo("[pet] stats of " + kv.Key + " changed here: " + sb);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[pet] stat tick failed: " + e.Message); }
        }

        // Client: pets from this player's own profile that the game loaded into the host's house are not the host's pets. Once the host's list has had time to arrive they are removed
        // (pets that appear later are left alone: they are something this player did in the session).
        static double _settledAt; static bool _leftDone;
        static void LeftoverTick()
        {
            if (Session.Role != Role.Client || _leftDone || !Session.Active || !ItemSync.Settled()) return;
            if (_settledAt == 0) { _settledAt = Time.realtimeSinceStartup; return; }
            if (Time.realtimeSinceStartup - _settledAt < 10.0) return;
            _leftDone = true;
            try
            {
                foreach (var p in UnityEngine.Object.FindObjectsOfType<Pet>())
                {
                    if (p == null || Known.ContainsValue(p)) continue;
                    // only pets from this player's own profile: a house's own animals (the farm's chickens and cows) have no pet id and belong to the house
                    PetSave ps = null; try { ps = p.PetToSave(); } catch { }
                    if (ps == null || ps.id < 0 || !ps.ownedByPlayer) continue;
                    var k = KeyOfSave(ps); if (PendingAdds.Any(x => x.Key.Contains("|") && KeyOfPayload(x.Key) == k)) continue;   // the host's copy of it is still on its way
                    Plugin.Log.LogInfo("[pet] removing " + p.name + ": a pet from this player's own save that the host does not have");
                    UnityEngine.Object.Destroy(p.gameObject);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[pet] leftover cleanup failed: " + e.Message); }
        }

        public static void Tick()
        {
            LeftoverTick();
            if (Session.Active && Session.Role == Role.Client) PendingTick();
            if (Session.Active && ItemSync.Settled())
            {
                PetMotion.FollowTick();
                var shared = new Dictionary<string, Pet>(); foreach (var kv in Known) if (kv.Value != null && kv.Value.gameObject.activeInHierarchy) shared[kv.Key] = kv.Value;
                PetMotion.ClaimTick(shared); PetMotion.SendTick(shared);   // ten times a second, for the pets this game runs
            }
            StatTick();
            if (!Session.Active || Session.Role != Role.Host || !ItemSync.Settled()) return;
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 0.5;
            try
            {
                var live = new Dictionary<string, Pet>();
                foreach (var p in UnityEngine.Object.FindObjectsOfType<Pet>().OrderBy(x => x.GetInstanceID()))
                {
                    if (p == null || !p.gameObject.activeInHierarchy) continue;
                    string id = HostKey(p); if (id == null) continue; live[id] = p;
                    if (!Known.ContainsKey(id)) { Known[id] = p; SendAdd(p, id); }
                }
                foreach (var id in Known.Keys.ToList())
                    if (!live.ContainsKey(id)) { Known.Remove(id); Events.Send("pet", "rem", id); }
                foreach (var gone in HostKeys.Keys.Where(x => x == null).ToList()) HostKeys.Remove(gone);
                if (Time.realtimeSinceStartup >= _nextPos && live.Count > 0)
                {
                    _nextPos = Time.realtimeSinceStartup + 3.0;   // the 10-per-second motion stream (PetMotion) carries the positions; this one is only for pets that do not follow it yet
                    var sb = new StringBuilder();
                    foreach (var kv in live) { var t = kv.Value.transform; sb.Append(kv.Key).Append(',').Append(t.position.x.ToString("0.###", CultureInfo.InvariantCulture)).Append(',').Append(t.position.y.ToString("0.###", CultureInfo.InvariantCulture)).Append(',').Append(t.position.z.ToString("0.###", CultureInfo.InvariantCulture)).Append(',').Append(t.eulerAngles.y.ToString("0", CultureInfo.InvariantCulture)).Append(';'); }
                    Events.Send("pet", "pos", sb.ToString());
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[pet] tick failed: " + e.Message); }
        }

        static void SendAdd(Pet p, string key)
        {
            try
            {
                var save = p.PetToSave(); if (save == null) return;
                Events.Send("pet", "add", key + "|" + JsonConvert.SerializeObject(save, Ser));
                Plugin.Log.LogInfo("[pet] sent pet " + key + " " + save.name + " (" + save.type + ")");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[pet] could not send " + p.name + ": " + e.Message); }
        }

        /// <summary>Host: a client reported its house loaded: tell it about the pets that are here.</summary>
        public static void SendTo(Conn c)
        {
            if (Session.Role != Role.Host) return;
            foreach (var kv in Known.ToList())
            {
                if (kv.Value == null) continue;
                try { var save = kv.Value.PetToSave(); if (save != null) c.Send(new W(Msg.Event).S("pet").I(Session.MyId).S("add").S(kv.Key + "|" + JsonConvert.SerializeObject(save, Ser)).Done()); } catch { }
            }
        }

        /// <summary>The key this pet has in the shared pet list (null when it is not shared).</summary>
        /// <summary>The last position and heading the host reported for each pet (used to hold hens on their nests).</summary>
        public static readonly Dictionary<Pet, KeyValuePair<Vector3, float>> HostPose = new Dictionary<Pet, KeyValuePair<Vector3, float>>();

        public static string KeyForPet(Pet p) { foreach (var kv in Known) if (kv.Value == p) return kv.Key; return null; }
        public static Pet PetByKey(string key) { Pet p; return Known.TryGetValue(key, out p) && p != null ? p : null; }

        public static bool Apply(string key, string json)
        {
            try
            {
                if (key == "add")
                {
                    int bar = json.IndexOf('|'); if (bar < 0) return false; string pk = json.Substring(0, bar);
                    PendingAdds.RemoveAll(x => x.Key.StartsWith(pk + "|"));
                    if (!Ready()) { PendingAdds.Add(new KeyValuePair<string, int>(json, 0)); return true; }   // made once the house has settled
                    Create(json, 0);
                    return true;
                }
                if (key == "rem")
                {
                    string id = json; Pet p; if (Known.TryGetValue(id, out p)) { Known.Remove(id); if (p != null) UnityEngine.Object.Destroy(p.gameObject); }
                    return true;
                }
                if (key == "anim") return PetAnimSync.Apply(json);
                if (key == "mo") return PetMotion.Apply(json);
                if (key == "claim" || key == "release" || key == "own") return PetMotion.Ownership(key, json);
                if (key == "stat")
                {
                    var f = json.Split(','); if (f.Length < 1 + StatNames.Length) return false;
                    string id = f[0]; Pet p; if (!Known.TryGetValue(id, out p) || p == null) return true;
                    var vals = new float[StatNames.Length];
                    for (int i = 0; i < vals.Length; i++)
                    {
                        vals[i] = float.Parse(f[1 + i], CultureInfo.InvariantCulture);
                        var m = typeof(Pet).GetMethod(Setters[i], new[] { typeof(float) }); if (m != null) m.Invoke(p, new object[] { vals[i] });
                    }
                    Last[id] = vals; Plugin.Log.LogInfo("[pet] stats of " + id + " from the other player");
                    return true;
                }
                if (key == "pos")
                {
                    foreach (var rec in json.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var f = rec.Split(','); if (f.Length < 5) continue;
                        Pet p; if (!Known.TryGetValue(f[0], out p) || p == null) continue;
                        var target = new Vector3(float.Parse(f[1], CultureInfo.InvariantCulture), float.Parse(f[2], CultureInfo.InvariantCulture), float.Parse(f[3], CultureInfo.InvariantCulture));
                        HostPose[p] = new KeyValuePair<Vector3, float>(target, float.Parse(f[4], CultureInfo.InvariantCulture));
                        if (PetMotion.IsDriven(p) || PetMotion.OwnedHere(p)) continue;   // follows a motion stream, or this game runs it
                        if ((p.transform.position - target).magnitude < (CoopSync.IsNested(p) ? 0.08f : 0.75f)) continue;   // a hen on a nest is held where the host has it
                        var agent = p.GetComponent<UnityEngine.AI.NavMeshAgent>();
                        if (agent != null && agent.enabled && agent.isOnNavMesh && !CoopSync.IsNested(p)) agent.Warp(target); else p.transform.position = target;   // a nest is above the walking mesh: no warp there
                        p.transform.rotation = Quaternion.Euler(0, float.Parse(f[4], CultureInfo.InvariantCulture), 0);
                    }
                    return true;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[pet] could not apply " + key + ": " + e.Message + (_traced++ < 2 ? " | " + e : "")); return false; }
            return false;
        }
    }
}
