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
        static double _next, _nextPos;

        public static void Reset() { Known.Clear(); Last.Clear(); HostKeys.Clear(); _next = 0; _settledAt = 0; _leftDone = false; }

        // a pet is told apart by its save guid (the numeric id is 0 for the first pet and was shared by pets spawned together); without a guid the id is used
        static string KeyOfSave(PetSave s) { return s == null ? null : (!string.IsNullOrEmpty(s.guid) ? "g" + s.guid : "i" + s.id.ToString(CultureInfo.InvariantCulture)); }
        static string KeyOf(Pet p)
        {
            try { return KeyOfSave(p.PetToSave()); } catch { return null; }
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
                    Plugin.Log.LogInfo("[pet] removing " + p.name + ": a pet from this player's own save that the host does not have");
                    UnityEngine.Object.Destroy(p.gameObject);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[pet] leftover cleanup failed: " + e.Message); }
        }

        public static void Tick()
        {
            LeftoverTick();
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
                    _nextPos = Time.realtimeSinceStartup + 0.5;
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

        public static bool Apply(string key, string json)
        {
            try
            {
                if (key == "add")
                {
                    int bar = json.IndexOf('|'); if (bar < 0) return false; string pk = json.Substring(0, bar);
                    var save = JsonConvert.DeserializeObject<PetSave>(json.Substring(bar + 1)); if (save == null) return false;
                    Pet existing; if (Known.TryGetValue(pk, out existing) && existing != null) return true;
                    string own = KeyOfSave(save);
                    foreach (var p in UnityEngine.Object.FindObjectsOfType<Pet>()) if (p != null && !Known.ContainsValue(p) && KeyOf(p) == own) { Known[pk] = p; return true; } // the game already made it (its own save)
                    var pm = UnityEngine.Object.FindObjectOfType<PetsManager>(); if (pm == null) return false;
                    save.usePositionFromSave = true; save.spawnOnLastPosition = true;
                    var pet = pm.LoadPetFromSave(save);
                    if (pet != null) { Known[pk] = pet; Plugin.Log.LogInfo("[pet] created pet " + pk + " " + save.name + " from the host"); }
                    else Plugin.Log.LogWarning("[pet] the game would not create pet " + pk);
                    return true;
                }
                if (key == "rem")
                {
                    string id = json; Pet p; if (Known.TryGetValue(id, out p)) { Known.Remove(id); if (p != null) UnityEngine.Object.Destroy(p.gameObject); }
                    return true;
                }
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
                        if ((p.transform.position - target).magnitude < 0.75f) continue;
                        var agent = p.GetComponent<UnityEngine.AI.NavMeshAgent>();
                        if (agent != null && agent.enabled && agent.isOnNavMesh) agent.Warp(target); else p.transform.position = target;
                        p.transform.rotation = Quaternion.Euler(0, float.Parse(f[4], CultureInfo.InvariantCulture), 0);
                    }
                    return true;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[pet] could not apply " + key + ": " + e.Message); return false; }
            return false;
        }
    }
}
