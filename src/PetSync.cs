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
        static readonly Dictionary<int, Pet> Known = new Dictionary<int, Pet>();   // host: pets already announced; client: pets created from the host's announcements
        static readonly JsonSerializerSettings Ser = new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore, Formatting = Formatting.None };
        static double _next, _nextPos;

        public static void Reset() { Known.Clear(); _next = 0; }

        static int IdOf(Pet p)
        {
            try { var s = p.PetToSave(); return s == null ? 0 : s.id; } catch { return 0; }
        }

        public static void Tick()
        {
            if (!Session.Active || Session.Role != Role.Host || !ItemSync.Settled()) return;
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 0.5;
            try
            {
                var live = new Dictionary<int, Pet>();
                foreach (var p in UnityEngine.Object.FindObjectsOfType<Pet>())
                {
                    if (p == null || !p.gameObject.activeInHierarchy) continue;
                    int id = IdOf(p); if (id == 0) continue; live[id] = p;
                    if (!Known.ContainsKey(id)) { Known[id] = p; SendAdd(p); }
                }
                foreach (var id in Known.Keys.ToList())
                    if (!live.ContainsKey(id)) { Known.Remove(id); Events.Send("pet", "rem", id.ToString(CultureInfo.InvariantCulture)); }
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

        static void SendAdd(Pet p)
        {
            try
            {
                var save = p.PetToSave(); if (save == null) return;
                Events.Send("pet", "add", JsonConvert.SerializeObject(save, Ser));
                Plugin.Log.LogInfo("[pet] sent pet #" + save.id + " " + save.name + " (" + save.type + ")");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[pet] could not send " + p.name + ": " + e.Message); }
        }

        /// <summary>Host: a client reported its house loaded: tell it about the pets that are here.</summary>
        public static void SendTo(Conn c)
        {
            if (Session.Role != Role.Host) return;
            foreach (var p in Known.Values.ToList())
            {
                if (p == null) continue;
                try { var save = p.PetToSave(); if (save != null) c.Send(new W(Msg.Event).S("pet").I(Session.MyId).S("add").S(JsonConvert.SerializeObject(save, Ser)).Done()); } catch { }
            }
        }

        public static bool Apply(string key, string json)
        {
            try
            {
                if (key == "add")
                {
                    var save = JsonConvert.DeserializeObject<PetSave>(json); if (save == null) return false;
                    Pet existing; if (Known.TryGetValue(save.id, out existing) && existing != null) return true;
                    foreach (var p in UnityEngine.Object.FindObjectsOfType<Pet>()) if (p != null && IdOf(p) == save.id) { Known[save.id] = p; return true; } // the game already made it (its own save)
                    var pm = UnityEngine.Object.FindObjectOfType<PetsManager>(); if (pm == null) return false;
                    save.usePositionFromSave = true; save.spawnOnLastPosition = true;
                    var pet = pm.LoadPetFromSave(save);
                    if (pet != null) { Known[save.id] = pet; Plugin.Log.LogInfo("[pet] created pet #" + save.id + " " + save.name + " from the host"); }
                    else Plugin.Log.LogWarning("[pet] the game would not create pet #" + save.id);
                    return true;
                }
                if (key == "rem")
                {
                    int id; if (!int.TryParse(json, NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) return false;
                    Pet p; if (Known.TryGetValue(id, out p)) { Known.Remove(id); if (p != null) UnityEngine.Object.Destroy(p.gameObject); }
                    return true;
                }
                if (key == "pos")
                {
                    foreach (var rec in json.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var f = rec.Split(','); if (f.Length < 5) continue;
                        int id; Pet p; if (!int.TryParse(f[0], out id) || !Known.TryGetValue(id, out p) || p == null) continue;
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
