using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Farm soil and crops (hoeing, sowing, harvesting). The game writes the soil into the recording only when it saves, so the recording-list sync never saw a tile being hoed or sown. Every second each
    /// player compares the soil grid (CropManager.SoilDictionary) with what was last agreed; a tile that appeared, changed (level, seeded, crop type) or vanished is sent as the game's own PlacedSoil
    /// record (SoilBehaviour.SoilModificationToSave) and the other player rebuilds that tile with the game's own load path (CropManager.BeginLoadSoilFieldFromSave), replacing its old tile.
    /// Growth itself is not sent: a crop carries its planting time and both games age it the same way.
    /// </summary>
    internal static class CropSync
    {
        static Type _cmT, _soilT, _psT, _cropT;
        static FieldInfo _dictF; static MethodInfo _toSave, _begin, _delete;
        static Component _cm; static double _next, _findAt;
        static readonly Dictionary<string, string> Sig = new Dictionary<string, string>();   // tile "x:z" -> signature last agreed with the others
        static readonly List<string> PendingLoad = new List<string>();                        // received records waiting for the old tile to be gone
        static readonly Dictionary<string, double> Hold = new Dictionary<string, double>();   // tiles just rebuilt from the other player's record: their crop loads a moment later, so they are not compared before then
        static bool _init; static double _dbgAt; static int _dbgCount = -1, _dbgNoCm, _dbgErr;
        static readonly Dictionary<string, string> Json = new Dictionary<string, string>(); // tile -> its record, for a player who joins later
        static readonly JsonSerializerSettings Ser = new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore, Formatting = Formatting.None };

        public static void Reset() { _reconnectAt = 0; Rebuilds.Clear(); NoCropSince.Clear(); Retried.Clear(); Hold.Clear(); Json.Clear(); Sig.Clear(); PendingLoad.Clear(); _init = false; _cm = null; _next = 0; }

        static Type T(string n) { foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { Type t = null; try { t = a.GetType(n, false); } catch { } if (t != null) return t; } return null; }

        static bool Resolve()
        {
            if (_cmT != null) return true;
            _cmT = T("Features.Farm.HoeTool.CropManager"); _soilT = T("Features.Farm.HoeTool.SoilBehaviour"); _psT = T("PlacedSoil");
            if (_cmT == null || _soilT == null || _psT == null) { _cmT = null; return false; }
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            _dictF = _cmT.GetField("SoilDictionary", all); _toSave = _soilT.GetMethod("SoilModificationToSave", all, null, Type.EmptyTypes, null); _delete = _soilT.GetMethod("DeleteSoil", all, null, Type.EmptyTypes, null);
            _begin = _cmT.GetMethod("BeginLoadSoilFieldFromSave", all);
            if (_dictF == null || _toSave == null || _delete == null || _begin == null) { Plugin.Log.LogWarning("[crop] soil API not found (dict " + (_dictF != null) + ", save " + (_toSave != null) + ", delete " + (_delete != null) + ", load " + (_begin != null) + "): crops are not synced"); _cmT = null; return false; }
            return true;
        }

        static object F(object o, string n) { var f = o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); return f == null ? null : f.GetValue(o); }

        static string KeyOf(object ps) { return F(ps, "soilPositionX") + ":" + F(ps, "soilPositionZ"); }

        static string SigOf(object ps)
        {
            var crop = F(ps, "crop"); string ct = crop == null ? "-" : "" + F(crop, "cropType");
            return F(ps, "soilLevel") + "|" + F(ps, "isSeeded") + "|" + ct;
        }

        static IDictionary Soils()
        {
            if (_cm == null && Time.realtimeSinceStartup >= _findAt) { _findAt = Time.realtimeSinceStartup + 4.0; _cm = UnityEngine.Object.FindObjectOfType(_cmT) as Component; }
            return _cm == null ? null : _dictF.GetValue(_dictF.IsStatic ? null : _cm) as IDictionary;
        }

        static MethodInfo _gridOf;
        /// <summary>Grid cell of a tile from its transform (the tile's own save record leaves the position at 0 until the game saves).</summary>
        static bool GridOf(Component soil, out int x, out int z)
        {
            x = z = 0;
            if (_cm == null) return false;
            if (_gridOf == null) _gridOf = _cmT.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(m => m.Name == "GetSoilGridPosition" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Transform));
            if (_gridOf == null) return false;
            var v = _gridOf.Invoke(_gridOf.IsStatic ? null : _cm, new object[] { soil.transform }); var vt = v.GetType(); x = (int)vt.GetProperty("x").GetValue(v, null); z = (int)vt.GetProperty("y").GetValue(v, null); return true;
        }

        static object SaveRecord(Component soil, out string key)
        {
            key = null; int x, z; if (!GridOf(soil, out x, out z)) return null;
            var ps = _toSave.Invoke(soil, null); if (ps == null) return null;
            _psT.GetField("soilPositionX").SetValue(ps, x); _psT.GetField("soilPositionZ").SetValue(ps, z);
            key = x + ":" + z; return ps;
        }

        static bool Seeded(Component soil) { var v = F(soil, "currentlySeeded"); return v is bool && (bool)v; }
        static bool HasCrop(Component soil) { var v = F(soil, "currentCrop") as UnityEngine.Object; return v != null; }
        // which of two tiles on one cell survives: the one that carries a crop object, then the one marked seeded
        static int Quality(Component soil) { return (HasCrop(soil) ? 2 : 0) + (Seeded(soil) ? 1 : 0); }

        // two tiles on one grid cell (a tile from the saved recording and one rebuilt from a live record, or a double placement): the weaker one is removed once the pair has been seen for a while
        // (a rebuilt tile needs a few seconds to grow its crop object, so a young pair is left alone)
        static readonly Dictionary<int, double> DupSeen = new Dictionary<int, double>();
        static readonly List<Component> _extras = new List<Component>();
        static void Extra(Component c) { _extras.Add(c); }
        static void RemoveDuplicates()
        {
            var now = Time.realtimeSinceStartup; var seen = new HashSet<int>();
            foreach (var c in _extras)
            {
                if (c == null) continue; int id = c.GetInstanceID(); seen.Add(id); double first; if (!DupSeen.TryGetValue(id, out first)) { DupSeen[id] = now; continue; }
                if (now - first < 15.0 || HasCrop(c)) continue;
                try { GrassSync.KeepLawn(); _delete.Invoke(c, null); GrassSync.RestoreLawnSoon(); Reconnect(); Plugin.Log.LogInfo("[crop] removed a duplicate soil tile"); } catch (Exception e) { Plugin.Log.LogWarning("[crop] could not remove a duplicate tile: " + (e.InnerException ?? e).Message); }
            }
            foreach (var id in DupSeen.Keys.ToList()) if (!seen.Contains(id)) DupSeen.Remove(id);
        }

        static Dictionary<string, Component> _scan; static double _scanAt;
        /// <summary>Every live soil tile by grid cell (from its transform: a tile that cannot be saved yet still occupies its cell). The game's SoilDictionary does not list every tile, so the objects themselves are scanned.</summary>
        static Dictionary<string, Component> Scan(bool fresh)
        {
            if (!fresh && _scan != null && Time.realtimeSinceStartup < _scanAt) return _scan;
            var r = new Dictionary<string, Component>(); if (fresh) _extras.Clear();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(_soilT))
            {
                var c = o as Component; if (c == null || !c.gameObject.activeInHierarchy) continue;
                try
                {
                    int x, z; if (!GridOf(c, out x, out z)) continue; string k = x + ":" + z; Component other;
                    if (r.TryGetValue(k, out other)) { if (Quality(c) > Quality(other)) { Extra(other); r[k] = c; } else Extra(c); } else r[k] = c;
                }
                catch (Exception e) { if (_dbgErr++ < 3) Plugin.Log.LogWarning("[crop] scan: " + (e.InnerException ?? e).Message); }
            }
            _scan = r; _scanAt = Time.realtimeSinceStartup + 0.4; return r;
        }

        // A rebuilt tile that is marked seeded but never grew its crop object (seen when this player has a seed item of that crop in the scene): after a while the game's own sowing step
        // (SoilBehaviour.Sow with the seed data of the crop type in the record) is run on it.
        /// <summary>The game's seed data for a crop type (SeedAdder.SeedDictionary; a crop is listed once this player's game has seen its seed bag). If missing, a loaded seed bag of that crop adds it.</summary>
        static object SeedData(int want)
        {
            var sat = T("Features.Farm.Seeding.Code.SeedAdder"); if (sat == null) return null;
            var fd = sat.GetField("SeedDictionary", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); var dict = fd == null ? null : fd.GetValue(null) as IDictionary; if (dict == null) return null;
            foreach (DictionaryEntry e in dict) if (Convert.ToInt32(e.Key) == want) return e.Value;
            try
            {
                var add = sat.GetMethod("AddSeedToDictionary", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); var tf = sat.GetField("farmCollectibleType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (add != null && tf != null)
                    foreach (var o in UnityEngine.Resources.FindObjectsOfTypeAll(sat)) if (Convert.ToInt32(tf.GetValue(o)) == want) { add.Invoke(o, null); break; }
            }
            catch { }
            foreach (DictionaryEntry e in dict) if (Convert.ToInt32(e.Key) == want) return e.Value;
            return null;
        }

        /// <summary>The other player sowed a tile that exists here: sow ours with the game's own step instead of rebuilding it (a rebuild grows the grass back for a moment and loses the tile's shape).</summary>
        static bool SowInPlace(Component local, object ps)
        {
            try
            {
                if (Seeded(local) || HasCrop(local)) return false;
                var crop = F(ps, "crop"); var seeded = F(ps, "isSeeded"); if (crop == null || !(seeded is bool) || !(bool)seeded) return false;
                if (Convert.ToInt32(F(ps, "soilLevel")) != Convert.ToInt32(F(local, "currentSoilLevel"))) return false;
                var data = SeedData(Convert.ToInt32(F(crop, "cropType"))); if (data == null) return false;
                var sow = _soilT.GetMethod("Sow", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (sow == null) return false;
                ((MonoBehaviour)local).StartCoroutine((IEnumerator)sow.Invoke(local, new object[] { 0.1f, data }));
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[crop] could not sow in place: " + (e.InnerException ?? e).Message); return false; }
        }

        static readonly Dictionary<int, double> NoCropSince = new Dictionary<int, double>();
        static readonly HashSet<int> Retried = new HashSet<int>();
        static readonly Dictionary<string, int> Rebuilds = new Dictionary<string, int>();

        // Tiles built from another player's records (or a neighbour removed) do not reshape the tiles around them, so a row hoed on one side showed single plots with stone edges on the other.
        // After a rebuild every tile looks at its neighbours again and re-aligns (the game's own LookForNeighbours and Align, what placing a tile with the hoe does).
        static double _reconnectAt; static bool _reconnectAgain;
        static void Reconnect() { _reconnectAt = Time.realtimeSinceStartup + 1.0; _reconnectAgain = true; }
        static MethodInfo _look, _align, _appear;
        static void ConnectAll()
        {
            try
            {
                if (_look == null) _look = _soilT.GetMethod("LookForNeighbours", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_align == null) _align = _soilT.GetMethod("Align", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_appear == null) _appear = _soilT.GetMethod("SetAppearance", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_look == null || _align == null) return;
                var tiles = Scan(true); var grid = Soils(); int fixedGrid = 0;
                if (grid != null)
                {
                    // the neighbour lookup reads the game's soil grid; tiles built from records were not always in it, so their neighbours took them for empty ground
                    var keyT = _dictF.FieldType.IsGenericType ? _dictF.FieldType.GetGenericArguments()[0] : null;
                    foreach (var k in grid.Keys.Cast<object>().ToList()) { var v = grid[k] as UnityEngine.Object; if (v == null) { grid.Remove(k); fixedGrid++; } }
                    if (keyT != null)
                        foreach (var kv in tiles)
                        {
                            var xz = kv.Key.Split(':'); var gk = Activator.CreateInstance(keyT, int.Parse(xz[0]), int.Parse(xz[1]));
                            if (!grid.Contains(gk) || !ReferenceEquals(grid[gk], kv.Value)) { grid[gk] = kv.Value; fixedGrid++; }
                        }
                }
                int n = 0;
                foreach (var kv in tiles) { try { _look.Invoke(kv.Value, null); n++; } catch { } }
                foreach (var kv in tiles) { try { _align.Invoke(kv.Value, null); if (_appear != null) _appear.Invoke(kv.Value, null); } catch { } }
                if (fixedGrid > 0) Plugin.Log.LogInfo("[crop] soil grid: " + fixedGrid + " entr(ies) fixed");
                if (n > 0) Plugin.Log.LogInfo("[crop] " + n + " soil tile(s) joined to their neighbours again");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[crop] could not join the tiles: " + (e.InnerException ?? e).Message); }
        }
        static void EnsureCrops(Dictionary<string, Component> tiles)
        {
            var now = Time.realtimeSinceStartup; var seen = new HashSet<int>();
            foreach (var kv in tiles)
            {
                var c = kv.Value; if (c == null || !Seeded(c) || HasCrop(c)) continue; int id = c.GetInstanceID(); seen.Add(id);
                double first; if (!NoCropSince.TryGetValue(id, out first)) { NoCropSince[id] = now; continue; }
                if (now - first < 12.0 || !Retried.Add(id)) continue;
                try
                {
                    string js; if (!Json.TryGetValue(kv.Key, out js)) continue;
                    var jo = Newtonsoft.Json.Linq.JObject.Parse(js); var cropTok = jo["crop"]; if (cropTok == null || cropTok.Type == Newtonsoft.Json.Linq.JTokenType.Null) continue;
                    int want = (int)cropTok["cropType"];
                    object data = SeedData(want);
                    if (data == null)
                    {
                        // the game only knows a crop's seed data after this player has held that seed: rebuild the tile from the record instead (its load path brings the crop)
                        int n; Rebuilds.TryGetValue(kv.Key, out n); if (n >= 3) continue; Rebuilds[kv.Key] = n + 1;
                        GrassSync.KeepLawn(); _delete.Invoke(c, null); PendingLoad.Add(js); Hold[kv.Key] = now + 10.0;
                        Plugin.Log.LogInfo("[crop] tile " + kv.Key + " has no crop (no seed data for crop " + want + " here): rebuilt from its record");
                        continue;
                    }
                    var sow = _soilT.GetMethod("Sow", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    ((MonoBehaviour)c).StartCoroutine((IEnumerator)sow.Invoke(c, new object[] { 0.1f, data }));
                    Plugin.Log.LogInfo("[crop] grew the missing crop on tile " + kv.Key + " with the game's own sowing step");
                }
                catch (Exception e) { Plugin.Log.LogWarning("[crop] could not grow the missing crop: " + (e.InnerException ?? e).Message); }
            }
            foreach (var id in NoCropSince.Keys.ToList()) if (!seen.Contains(id)) NoCropSince.Remove(id);
        }

        public static void Tick()
        {
            if (!Session.Active || !ItemSync.Settled() || Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 1.0;
            try
            {
                if (Time.realtimeSinceStartup > _dbgAt) { _dbgAt = Time.realtimeSinceStartup + 15; Plugin.Log.LogInfo("[crop] tick: init=" + _init + " cm=" + (_cm != null) + " soilObjects=" + (_soilT == null ? -1 : UnityEngine.Object.FindObjectsOfType(_soilT).Length) + " known=" + Sig.Count); }
                if (!Resolve()) return;
                Soils(); if (_cm == null) { if (_dbgNoCm++ == 0) Plugin.Log.LogInfo("[crop] no CropManager here"); return; }
                if (PendingLoad.Count > 0) { var batch = PendingLoad.ToList(); PendingLoad.Clear(); Load(batch); GrassSync.RestoreLawnSoon(); Reconnect(); return; }
                if (_reconnectAt > 0 && Time.realtimeSinceStartup >= _reconnectAt) { _reconnectAt = _reconnectAgain ? Time.realtimeSinceStartup + 2.0 : 0; _reconnectAgain = false; ConnectAll(); }
                var cur = new Dictionary<string, KeyValuePair<string, object>>();
                var present = new HashSet<string>();
                foreach (var kv in Scan(true))
                {
                    present.Add(kv.Key);   // a tile whose record cannot be made yet (its crop is still loading) is still there: it is neither changed nor gone
                    object ps; string k; try { ps = SaveRecord(kv.Value, out k); } catch { continue; }
                    if (ps != null) cur[kv.Key] = new KeyValuePair<string, object>(SigOf(ps), ps);
                }
                if (cur.Count != _dbgCount) { _dbgCount = cur.Count; Plugin.Log.LogInfo("[crop] watching " + cur.Count + " soil tile(s)"); }
                RemoveDuplicates(); EnsureCrops(Scan(false));
                if (!_init) { foreach (var kv in cur) { Sig[kv.Key] = kv.Value.Key; Json[kv.Key] = JsonConvert.SerializeObject(kv.Value.Value, Ser); } _init = true; return; }
                foreach (var kv in cur)
                {
                    double until; if (Hold.TryGetValue(kv.Key, out until)) { if (Time.realtimeSinceStartup < until) continue; Hold.Remove(kv.Key); Sig[kv.Key] = kv.Value.Key; continue; }
                    string old; if (Sig.TryGetValue(kv.Key, out old) && old == kv.Value.Key) continue;
                    if (old == null) GrassSync.SendSoon();   // a tile dug here: the hoe also cut the grass under it, and the lawn only travels when it is sent
                    Sig[kv.Key] = kv.Value.Key;
                    string js = JsonConvert.SerializeObject(kv.Value.Value, Ser); Json[kv.Key] = js;
                    Events.Send("soil", kv.Key, js);
                }
                foreach (var key in Sig.Keys.ToList())
                    if (!present.Contains(key) && !Hold.ContainsKey(key)) { Sig.Remove(key); Json.Remove(key); Events.Send("soil", key, "-"); }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[crop] tick failed: " + (e.InnerException ?? e).Message); }
        }

        /// <summary>Host: a client reported its house loaded: give it the soil tiles that exist now (the recording only has them after a save).</summary>
        public static void SendTo(Conn c)
        {
            if (Session.Role != Role.Host) return;
            foreach (var kv in Json.ToList()) { try { c.Send(new W(Msg.Event).S("soil").I(Session.MyId).S(kv.Key).S(kv.Value).Done()); } catch { } }
        }

        // one call for the whole batch: the game's loader works through its queue over several frames and a second call while it runs does not add to it
        static void Load(List<string> jsons)
        {
            try
            {
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_psT));
                foreach (var j in jsons) { var ps = JsonConvert.DeserializeObject(j, _psT); if (ps != null) list.Add(ps); }
                if (list.Count > 0) _begin.Invoke(_cm, new object[] { list });
            }
            catch (Exception e) { Plugin.Log.LogWarning("[crop] could not build soil tiles: " + (e.InnerException ?? e).Message); }
        }

        /// <summary>A tile changed on another player's farm: replace ours (json = the PlacedSoil record, "-" = the tile is gone).</summary>
        public static bool Apply(string key, string json)
        {
            try
            {
                if (!Resolve()) return true; Soils(); if (_cm == null) return true;
                var parts = key.Split(':'); if (parts.Length != 2) return false;
                int x = int.Parse(parts[0], CultureInfo.InvariantCulture), z = int.Parse(parts[1], CultureInfo.InvariantCulture);
                Component local; Scan(false).TryGetValue(x + ":" + z, out local);
                if (json == "-") { Sig.Remove(key); Json.Remove(key); if (local != null) { _delete.Invoke(local, null); Reconnect(); } return true; }
                var ps = JsonConvert.DeserializeObject(json, _psT); if (ps == null) return false;
                Sig[key] = SigOf(ps); Json[key] = json; Hold[key] = Time.realtimeSinceStartup + 10.0;
                if (local != null)
                {
                    object cur; try { string ck; cur = SaveRecord(local, out ck); } catch { cur = null; }
                    if (cur != null && SigOf(cur) == Sig[key]) return true;
                    if (SowInPlace(local, ps)) { Plugin.Log.LogInfo("[crop] tile " + key + " sown in place like the other player's"); return true; }
                    GrassSync.KeepLawn(); _delete.Invoke(local, null); PendingLoad.Add(json);  // rebuild once the old tile is gone (the lawn is put back afterwards)
                }
                else PendingLoad.Add(json);
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[crop] could not apply " + key + ": " + (e.InnerException ?? e).Message); return false; }
        }
    }
}
