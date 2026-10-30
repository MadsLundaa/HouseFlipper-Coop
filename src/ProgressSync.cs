using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Assets.Code.Player;
using Code.GameManagers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Shared player progression (skills/XP, statistics, unlocked tools, order progress, courses).
    /// The host's PlayerProgress is the reference: on join the client's own values are backed up and replaced by the host's; afterwards every change
    /// is sent as an operation list (numbers: add delta, booleans: OR, primitive lists: union, other: set) so simultaneous gains by two players add up
    /// instead of overwriting each other. On leaving, the client's own progression is restored unless KeepSharedProgress is enabled.
    /// </summary>
    internal static class ProgressSync
    {
        static readonly string[] Members = { "skills", "statistics", "avaialbleTools", "ordersProgress", "coursesProgress", "toolsUsedInUnlockingToolQuest" };
        static readonly JsonSerializer Js = JsonSerializer.Create(new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });
        static readonly Dictionary<string, JToken> Base = new Dictionary<string, JToken>();
        static bool _primed; static double _next;
        static string _ownBackup; // client's own progression before joining

        static string BackupPath { get { return Path.Combine(BepInEx.Paths.BepInExRootPath, "hfcoop-progress-backup.json"); } }

        static PlayerProgress Progress()
        {
            var pc = UnityEngine.Object.FindObjectOfType<PlayerController>();
            return pc != null ? pc.progress : null;
        }

        // ---------- snapshotting ----------
        static JToken Dump(object o)
        {
            var t = JToken.FromObject(o, Js);
            Strip(t);
            return t;
        }

        static void Strip(JToken t)
        {
            if (t is JObject)
            {
                var jo = (JObject)t;
                jo.Remove("playerProgress");
                foreach (var p in new List<JProperty>(jo.Properties())) Strip(p.Value);
            }
            else if (t is JArray) foreach (var c in (JArray)t) Strip(c);
        }

        static JObject DumpAll(PlayerProgress pp)
        {
            var all = new JObject();
            foreach (var m in Members)
            {
                var v = GetMember(pp, m); if (v == null) continue;
                all[m] = Dump(v);
            }
            return all;
        }

        static object GetMember(object o, string name)
        {
            var f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return f == null ? null : f.GetValue(o);
        }

        // ---------- flatten / diff ----------
        static void Flatten(JToken t, string path, Dictionary<string, JToken> into)
        {
            if (t is JObject) { foreach (var p in ((JObject)t).Properties()) Flatten(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name, into); return; }
            if (t is JArray)
            {
                bool prim = true; foreach (var c in (JArray)t) if (c is JObject || c is JArray) { prim = false; break; }
                if (prim) { into[path] = t; return; }
                int i = 0; foreach (var c in (JArray)t) Flatten(c, path + "[" + (i++) + "]", into);
                return;
            }
            into[path] = t;
        }

        static JArray Diff(JToken before, JToken after)
        {
            var b = new Dictionary<string, JToken>(); var a = new Dictionary<string, JToken>();
            Flatten(before, "", b); Flatten(after, "", a);
            var ops = new JArray();
            foreach (var kv in a)
            {
                JToken old; b.TryGetValue(kv.Key, out old);
                var cur = kv.Value;
                if (cur.Type == JTokenType.Integer || cur.Type == JTokenType.Float)
                {
                    double d = cur.Value<double>() - (old == null ? 0 : old.Value<double>());
                    if (Math.Abs(d) > 1e-9) ops.Add(new JObject { { "p", kv.Key }, { "o", "add" }, { "v", d } });
                }
                else if (cur.Type == JTokenType.Boolean)
                {
                    if (cur.Value<bool>() && (old == null || !old.Value<bool>())) ops.Add(new JObject { { "p", kv.Key }, { "o", "or" }, { "v", true } });
                }
                else if (cur is JArray)
                {
                    var added = new JArray();
                    var oldSet = new HashSet<string>(); if (old is JArray) foreach (var e in (JArray)old) oldSet.Add(e.ToString(Formatting.None));
                    foreach (var e in (JArray)cur) if (!oldSet.Contains(e.ToString(Formatting.None))) added.Add(e);
                    if (added.Count > 0) ops.Add(new JObject { { "p", kv.Key }, { "o", "union" }, { "v", added } });
                }
                else if (old == null || !JToken.DeepEquals(old, cur)) ops.Add(new JObject { { "p", kv.Key }, { "o", "set" }, { "v", cur } });
            }
            return ops;
        }

        // ---------- apply ----------

        static List<object> ParsePath(string path)
        {
            var segs = new List<object>();
            foreach (var part in path.Split('.'))
            {
                int br = part.IndexOf('[');
                if (br < 0) { segs.Add(part); continue; }
                if (br > 0) segs.Add(part.Substring(0, br));
                foreach (var idx in part.Substring(br).Split(new[] { '[', ']' }, StringSplitOptions.RemoveEmptyEntries)) segs.Add(int.Parse(idx));
            }
            return segs;
        }

        static object Convert_(JToken t, Type type)
        {
            if (type.IsEnum) return t.Type == JTokenType.String ? Enum.Parse(type, t.Value<string>()) : Enum.ToObject(type, t.Value<long>());
            var u = Nullable.GetUnderlyingType(type) ?? type;
            if (u == typeof(string)) return t.Value<string>();
            return System.Convert.ChangeType(t.ToObject<object>(), u, CultureInfo.InvariantCulture);
        }

        /// <summary>Walks to the container of the last path segment. Returns the object that owns the leaf and the leaf member/index.</summary>
        static bool Walk(object root, List<object> segs, out object owner, out object last)
        {
            owner = root; last = null;
            for (int i = 0; i < segs.Count - 1; i++)
            {
                var s = segs[i];
                if (s is string)
                {
                    var f = owner.GetType().GetField((string)s, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f == null) return false; owner = f.GetValue(owner);
                }
                else { var l = owner as IList; if (l == null || (int)s >= l.Count) return false; owner = l[(int)s]; }
                if (owner == null) return false;
            }
            last = segs[segs.Count - 1];
            return true;
        }

        static void ApplyOp(PlayerProgress pp, JObject op)
        {
            var segs = ParsePath(op.Value<string>("p"));
            object owner, last;
            if (!Walk(pp, segs, out owner, out last)) return;
            string kind = op.Value<string>("o"); var v = op["v"];
            if (last is int)
            {
                var l = owner as IList; int i = (int)last; if (l == null || i >= l.Count) return;
                var et = l.GetType().IsGenericType ? l.GetType().GetGenericArguments()[0] : typeof(object);
                var cur = l[i];
                l[i] = kind == "add" ? System.Convert.ChangeType(System.Convert.ToDouble(cur) + v.Value<double>(), et) : Convert_(v, et);
                return;
            }
            var fi = owner.GetType().GetField((string)last, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fi == null) return;
            var ft = fi.FieldType;
            if (kind == "add")
            {
                double cur = System.Convert.ToDouble(fi.GetValue(owner) ?? 0);
                fi.SetValue(owner, System.Convert.ChangeType(cur + v.Value<double>(), Nullable.GetUnderlyingType(ft) ?? ft));
            }
            else if (kind == "or") { if (v.Value<bool>()) fi.SetValue(owner, true); }
            else if (kind == "union" || (kind == "set" && v is JArray))
            {
                var coll = fi.GetValue(owner); if (coll == null) return;
                var et = ft.IsGenericType ? ft.GetGenericArguments()[0] : typeof(object);
                var list = coll as IList;
                if (list == null) // HashSet<T> (e.g. toolsUsedInUnlockingToolQuest): same operations through reflection
                {
                    var ct = coll.GetType(); var has = ct.GetMethod("Contains"); var add = ct.GetMethod("Add"); var clr = ct.GetMethod("Clear");
                    if (has == null || add == null) return;
                    if (kind == "set" && clr != null) clr.Invoke(coll, null);
                    foreach (var e in (JArray)v) { var item = Convert_(e, et); if (!(bool)has.Invoke(coll, new[] { item })) add.Invoke(coll, new[] { item }); }
                    return;
                }
                if (kind == "set") list.Clear();
                foreach (var e in (JArray)v) { var item = Convert_(e, et); if (!list.Contains(item)) list.Add(item); }
            }
            else fi.SetValue(owner, Convert_(v, ft));
        }

        static void ApplyOps(PlayerProgress pp, JArray ops)
        {
            foreach (var o in ops)
            {
                try { ApplyOp(pp, (JObject)o); }
                catch (Exception e) { Plugin.Log.LogWarning("[prog] op " + o.ToString(Formatting.None) + " failed: " + e.Message); }
            }
        }

        /// <summary>Send our own pending changes now (the normal tick runs once a second).</summary>
        public static void Flush() { _next = 0; Tick(); }
        /// <summary>Make the current local state the baseline, so changes caused by applying remote work are not sent back.</summary>
        public static void Rebase() { var pp = Progress(); if (pp == null) return; foreach (var kv in DumpAll(pp).Properties()) Base[kv.Name] = kv.Value; }

        // ---------- session hooks ----------
        public static void Reset() { Base.Clear(); _primed = false; HostValues.Clear(); }

        /// <summary>Called on the client right after the host's house finished loading.</summary>
        public static void OnHouseReady()
        {
            _primed = false;
            if (Session.Role == Role.Client && _ownBackup == null)
            {
                var pp = Progress();
                if (pp != null) { _ownBackup = DumpAll(pp).ToString(Formatting.None); File.WriteAllText(BackupPath, _ownBackup); Plugin.Log.LogInfo("[prog] backed up own progression (" + _ownBackup.Length + " chars)"); }
            }
        }

        public static void SendFullTo(Conn c)
        {
            var pp = Progress(); if (pp == null) return;
            c.Send(new W(Msg.Event).S("prog").I(Session.MyId).S("full").S(DumpAll(pp).ToString(Formatting.None)).Done());
            var office = OfficeNow(pp);
            if (office != null) c.Send(new W(Msg.Event).S("prog").I(Session.MyId).S("office").S(office).Done());
        }

        // ---------- the office ----------
        // Which house is "the office" (PlayerProgress.currentOfficeHouse) is not part of the dumped members: when the host claimed another house as the office the
        // client kept its own office, so the host's office house still looked like a house on sale to the client. The host sends the office house (as the game saves it)
        // and its creation time; the client swaps in the host's and puts its own back when the session ends.
        static string _officeSent, _pendingOffice, _ownOffice;

        static string OfficeNow(PlayerProgress pp)
        {
            if (pp == null || pp.currentOfficeHouse == null) return null;
            try { return JsonConvert.SerializeObject(pp.currentOfficeHouse) + "\n" + pp.currentOfficeHouseCreatedDateTime.Ticks; }
            catch (Exception e) { Plugin.Log.LogWarning("[prog] office not serialisable: " + e.Message); return null; }
        }

        static void ApplyOffice(PlayerProgress pp, string payload)
        {
            try
            {
                int nl = payload.LastIndexOf('\n'); if (nl < 0) return;
                var info = JsonConvert.DeserializeObject<HouseInfo>(payload.Substring(0, nl));
                long ticks; long.TryParse(payload.Substring(nl + 1), out ticks);
                var expand = typeof(PlayerProgress).GetMethod("ExpandHouseInfo", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (expand != null) info = (HouseInfo)expand.Invoke(null, new object[] { info });
                if (info == null) return;
                pp.SetOfficeSafely(info);
                pp.currentOfficeHouseCreatedDateTime = new DateTime(ticks);
                Plugin.Log.LogInfo("[prog] office house is now the host's");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[prog] could not apply the office: " + (e.InnerException ?? e).Message); }
        }

        /// <summary>The progression before a scene exists (joining from the main menu): through the game manager instead of the player object in the scene.</summary>
        static PlayerProgress ProgressAny()
        {
            var pp = Progress(); if (pp != null) return pp;
            foreach (var f in typeof(GameManager).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (f.FieldType != typeof(GameManager)) continue;
                var gm = f.GetValue(null) as GameManager;
                if (gm != null && gm.player != null) return gm.player.progress;
            }
            return null;
        }

        /// <summary>
        /// Client: take over the host's office before the host's house is loaded. Whether a house is the office is read while it loads (the office PC is registered, the
        /// "house for sale" visitors outside are not spawned); changing the office afterwards made the game put a second office PC into the player's hands.
        /// </summary>
        public static void ApplyOfficeNow()
        {
            if (Session.Role != Role.Client || _pendingOffice == null) return;
            var pp = ProgressAny(); if (pp == null) return;
            if (_ownOffice == null) _ownOffice = OfficeNow(pp);
            var p = _pendingOffice; _pendingOffice = null;
            if (p != OfficeNow(pp)) ApplyOffice(pp, p);
        }

        // A house the host just made its office: the host puts the office PC down, and the client's copy of the house was loaded before that (not an office, no PC), so it kept
        // looking like a house for sale. Once the host has put the PC down the clients fetch the house again, now with the office already switched.
        static bool _refreshArmed, _heldSeen; static double _refreshAt, _freeSince, _pcPlacedAt;
        public static bool RefreshArmed { get { return _refreshArmed; } }
        static double _pcSentAt;
        /// <summary>The office PC was sent to the clients. Claiming the office happens when the PC is put down, so this usually comes just BEFORE the office change is noticed.</summary>
        public static void OfficePcPlaced() { _pcSentAt = Time.realtimeSinceStartup; if (_refreshArmed && _pcPlacedAt == 0) _pcPlacedAt = _pcSentAt; }

        public static void DevArmRefresh() { _refreshArmed = true; _refreshAt = Time.realtimeSinceStartup; _heldSeen = true; _freeSince = 0; }

        static void RefreshAfterClaim()
        {
            if (!_refreshArmed) return;
            double t = Time.realtimeSinceStartup;
            if (Session.PlayerCount < 2 || t - _refreshAt > 600) { _refreshArmed = false; return; }
            if (_pcPlacedAt > 0) { if (t - _pcPlacedAt < 4) return; _heldSeen = true; _freeSince = t - 4; } // the office PC was sent to the clients: it is down
            else if (CarrySync.HeldNow() != null) { _heldSeen = true; _freeSince = 0; return; }
            if (!_heldSeen) return;
            if (_freeSince == 0) { _freeSince = t; return; }
            if (t - _freeSince < 4) return;
            _refreshArmed = false;
            Plugin.Log.LogInfo("[prog] office claimed and the PC is down: clients load the house again");
            ItemSync.AssignAnonymousIds();
            Events.Send("house", "changing", "");
            Events.Send("house", Plugin.SceneName, "");
        }

        static void OfficeTick(PlayerProgress pp)
        {
            if (Session.Role == Role.Host)
            {
                RefreshAfterClaim();
                var now = OfficeNow(pp); if (now == null || now == _officeSent) return;
                bool first = _officeSent == null; _officeSent = now;
                if (!first && Session.PlayerCount > 1) { Events.Send("prog", "office", now); _refreshArmed = true; _refreshAt = Time.realtimeSinceStartup; _heldSeen = false; _freeSince = 0; _pcPlacedAt = Time.realtimeSinceStartup - _pcSentAt < 30 ? _pcSentAt : 0; }
            }
            else if (Session.Role == Role.Client && _pendingOffice != null && Snapshot.CanApplyState)
            {
                if (_ownOffice == null) _ownOffice = OfficeNow(pp);
                var p = _pendingOffice; _pendingOffice = null;
                if (p != OfficeNow(pp)) ApplyOffice(pp, p);
            }
        }

        public static void OnSessionEnd()
        {
            var pp = Progress();
            if (pp != null && _ownBackup != null && !Cfg.KeepSharedProgress.Value) { RestoreFrom(pp, _ownBackup); Plugin.Log.LogInfo("[prog] restored own progression"); }
            _ownBackup = null;
            if (pp != null && _ownOffice != null && !Cfg.KeepSharedProgress.Value) ApplyOffice(pp, _ownOffice);
            _ownOffice = null; _pendingOffice = null; _officeSent = null;
            if (File.Exists(BackupPath)) File.Delete(BackupPath);
            Reset();
        }

        /// <summary>Crash recovery: a client session that died without restoring progression.</summary>
        public static bool RecoverIfNeeded()
        {
            if (Session.Active) return false;
            if (!File.Exists(BackupPath)) return true;
            var pp = Progress(); if (pp == null) return false;
            RestoreFrom(pp, File.ReadAllText(BackupPath)); File.Delete(BackupPath);
            Plugin.Log.LogWarning("[prog] restored your own progression after an unclean co-op session");
            return true;
        }

        static void RestoreFrom(PlayerProgress pp, string json)
        {
            var target = JObject.Parse(json);
            var ops = new JArray();
            var cur = new Dictionary<string, JToken>(); var tgt = new Dictionary<string, JToken>();
            Flatten(DumpAll(pp), "", cur); Flatten(target, "", tgt);
            foreach (var kv in tgt) { JToken now; cur.TryGetValue(kv.Key, out now); if (now == null || !JToken.DeepEquals(now, kv.Value)) ops.Add(new JObject { { "p", kv.Key }, { "o", "set" }, { "v", kv.Value } }); }
            ApplyOps(pp, ops);
        }

        public static void Tick()
        {
            FollowHostPerks();
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 1.0f;
            var pp = Progress(); if (pp == null) return;
            if (_pendingFull != null && Snapshot.CanApplyState) { ApplyFull(pp); return; }
            OfficeTick(pp);
            if (Session.Role == Role.Client && !Snapshot.CanApplyState) return;
            if (!_primed) { Base.Clear(); foreach (var kv in DumpAll(pp).Properties()) Base[kv.Name] = kv.Value; _primed = true; return; }
            var cur = DumpAll(pp);
            var all = new JArray();
            foreach (var kv in cur.Properties())
            {
                JToken before; if (!Base.TryGetValue(kv.Name, out before)) { Base[kv.Name] = kv.Value; continue; }
                var ops = Diff(before, kv.Value);
                if (ops.Count == 0) continue;
                foreach (var o in ops) { ((JObject)o)["p"] = kv.Name + "." + ((JObject)o).Value<string>("p"); all.Add(o); }
                Base[kv.Name] = kv.Value;
            }
            all = HostOnlyFilter(pp, all);
            if (all.Count > 0)
            {
                if (Cfg.VerboseLog.Value) Plugin.Log.LogInfo("[prog] sending " + all.Count + " ops");
                Events.Send("prog", "ops", all.ToString(Formatting.None));
            }
        }

        // ---------- perk points: the host decides ----------
        // The number of perk points and the perk levels follow from the work done, which both peers count themselves, so each peer reached the next point at a slightly
        // different moment (and the synced "+1" could even be counted twice). Only the host computes them: clients never send these values, and keep the host's
        // numbers (the client's own early level-up is put back to the host's value as soon as it happens).
        static readonly Dictionary<string, JToken> HostValues = new Dictionary<string, JToken>();
        static double _nextFollow;

        /// <summary>Path ("skills.cat.field") -> current value for perk points, certificate flag and perk levels of every skill.</summary>
        static Dictionary<string, object> HostOnlyNow(PlayerProgress pp)
        {
            var d = new Dictionary<string, object>();
            if (pp == null || pp.skills == null) return d;
            foreach (var sf in pp.skills.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var cat = sf.GetValue(pp.skills);
                if (cat == null || !typeof(Assets.Code.Player_progress.SkillCategory).IsAssignableFrom(sf.FieldType)) continue;
                string pre = "skills." + sf.Name + ".";
                var np = cat.GetType().GetField("numberOfPerksAvailable", BindingFlags.Instance | BindingFlags.Public); if (np != null) d[pre + np.Name] = np.GetValue(cat);
                var ic = cat.GetType().GetField("isCertificated", BindingFlags.Instance | BindingFlags.Public); if (ic != null) d[pre + ic.Name] = ic.GetValue(cat);
                foreach (var ef in cat.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (ef.FieldType != typeof(Action<int>) || !ef.Name.StartsWith("on") || !ef.Name.EndsWith("ValueChanged")) continue;
                    string core = ef.Name.Substring(2, ef.Name.Length - 2 - "ValueChanged".Length);
                    var vf = cat.GetType().GetField(char.ToLowerInvariant(core[0]) + core.Substring(1), BindingFlags.Instance | BindingFlags.Public);
                    if (vf != null && vf.FieldType == typeof(int)) d[pre + vf.Name] = vf.GetValue(cat);
                }
            }
            return d;
        }

        static bool IsHostOnlyPath(PlayerProgress pp, string path) { return HostOnlyNow(pp).ContainsKey(path); }

        /// <summary>Host: send these values as absolute numbers. Client: never send them.</summary>
        static JArray HostOnlyFilter(PlayerProgress pp, JArray ops)
        {
            if (ops.Count == 0) return ops;
            var hn = HostOnlyNow(pp); var res = new JArray();
            foreach (var o in ops)
            {
                var jo = (JObject)o; object cur;
                if (hn.TryGetValue(jo.Value<string>("p"), out cur))
                {
                    if (Session.Role == Role.Client) continue;
                    res.Add(new JObject { { "p", jo.Value<string>("p") }, { "o", "set" }, { "v", JToken.FromObject(cur) } });
                }
                else res.Add(o);
            }
            return res;
        }

        /// <summary>Client: put back the host's perk numbers when the local game moved them on its own.</summary>
        static void FollowHostPerks()
        {
            if (Session.Role != Role.Client || HostValues.Count == 0 || !Snapshot.CanApplyState) return;
            if (Time.realtimeSinceStartup < _nextFollow) return;
            _nextFollow = Time.realtimeSinceStartup + 0.15f;
            var pp = Progress(); if (pp == null) return;
            var now = HostOnlyNow(pp); JArray fix = null;
            foreach (var kv in HostValues)
            {
                object cur; if (!now.TryGetValue(kv.Key, out cur)) continue;
                if (JToken.DeepEquals(JToken.FromObject(cur), kv.Value)) continue;
                if (fix == null) fix = new JArray();
                fix.Add(new JObject { { "p", kv.Key }, { "o", "set" }, { "v", kv.Value } });
            }
            if (fix == null) return;
            var before = PerkLevels(pp);
            ApplyOps(pp, fix);
            FirePerkEvents(pp, before);
            Plugin.Log.LogInfo("[prog] kept the host's perk numbers (" + fix.Count + " value(s) the local game had moved)");
            try { var pv = UnityEngine.Object.FindObjectOfType<Empyrean.HouseFlipper.Tablet.PerkView.PerksView>(); if (pv != null) pv.RefreshSkillProgress(); } catch { }
        }

        // ---------- perk effects ----------
        // Perk levels are plain int fields (cleaning.fastHands...). The game applies their effect from the skill's "on<Perk>ValueChanged" events, which only fire when
        // a perk is spent locally. Setting the field from a sync leaves the effect off, so after an update the events are fired for every perk whose level changed.
        static Dictionary<string, int> PerkLevels(PlayerProgress pp)
        {
            var d = new Dictionary<string, int>();
            if (pp == null || pp.skills == null) return d;
            foreach (var sf in pp.skills.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var cat = sf.GetValue(pp.skills);
                if (cat == null || !typeof(Assets.Code.Player_progress.SkillCategory).IsAssignableFrom(sf.FieldType)) continue;
                foreach (var ef in cat.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (ef.FieldType != typeof(Action<int>) || !ef.Name.StartsWith("on") || !ef.Name.EndsWith("ValueChanged")) continue;
                    string core = ef.Name.Substring(2, ef.Name.Length - 2 - "ValueChanged".Length);
                    var vf = cat.GetType().GetField(char.ToLowerInvariant(core[0]) + core.Substring(1), BindingFlags.Instance | BindingFlags.Public);
                    if (vf == null || vf.FieldType != typeof(int)) continue;
                    d[sf.Name + "." + ef.Name] = (int)vf.GetValue(cat);
                }
            }
            return d;
        }

        static void FirePerkEvents(PlayerProgress pp, Dictionary<string, int> before)
        {
            var after = PerkLevels(pp);
            foreach (var kv in after)
            {
                int old; if (before.TryGetValue(kv.Key, out old) && old == kv.Value) continue;
                try
                {
                    string[] parts = kv.Key.Split('.');
                    var cat = pp.skills.GetType().GetField(parts[0], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(pp.skills);
                    var ev = cat.GetType().GetField(parts[1], BindingFlags.Instance | BindingFlags.Public).GetValue(cat) as Action<int>;
                    if (ev != null) ev(kv.Value);
                    Plugin.Log.LogInfo("[prog] perk " + kv.Key + " -> " + kv.Value + " (effect event fired)");
                }
                catch (Exception e) { Plugin.Log.LogWarning("[prog] perk event " + kv.Key + " failed: " + (e.InnerException ?? e).Message); }
            }
        }

        static string _pendingFull;

        static void ApplyFull(PlayerProgress pp)
        {
            OnHouseReady(); // own backup first
            var perksBefore = PerkLevels(pp);
            RestoreFrom(pp, _pendingFull);
            FirePerkEvents(pp, perksBefore);
            if (Session.Role == Role.Client) { HostValues.Clear(); foreach (var kv in HostOnlyNow(pp)) HostValues[kv.Key] = JToken.FromObject(kv.Value); }
            _pendingFull = null; _primed = false;
            Plugin.Log.LogInfo("[prog] progression replaced by host's");
        }

        public static bool Apply(string key, string json)
        {
            if (key == "full")
            {
                if (Session.Role != Role.Client) return false;
                _pendingFull = json; // applied as soon as the host's house is loaded (Tick)
                return true;
            }
            if (key == "office") { if (Session.Role != Role.Client) return false; _pendingOffice = json; return true; }
            var pp = Progress(); if (pp == null) return false;
            if (key == "ops")
            {
                var ops = JArray.Parse(json);
                var perksBefore = PerkLevels(pp);
                ApplyOps(pp, ops);
                if (Session.Role == Role.Client) foreach (var o in ops) { var po = (JObject)o; string pth = po.Value<string>("p"); if (pth != null && IsHostOnlyPath(pp, pth) && po.Value<string>("o") == "set") HostValues[pth] = po["v"]; }
                FirePerkEvents(pp, perksBefore);
                // our baseline already includes what we just applied, otherwise we would echo it back
                var cur = DumpAll(pp); foreach (var kv in cur.Properties()) Base[kv.Name] = kv.Value;
                try { var pv = UnityEngine.Object.FindObjectOfType<Empyrean.HouseFlipper.Tablet.PerkView.PerksView>(); if (pv != null) pv.RefreshSkillProgress(); } // an open perks tab only redraws on its own actions
                catch (System.Exception e) { Plugin.Log.LogInfo("[prog] perks view refresh skipped: " + e.Message); }
                return true;
            }
            return false;
        }

        // ---------- dev helpers ----------
        /// <summary>Per synced member: a short hash of its JSON, so two peers can be compared at a glance (equal hash = identical state).</summary>
        public static string DevHashes()
        {
            var pp = Progress(); if (pp == null) return "no progress";
            var d = DumpAll(pp); var sb = new System.Text.StringBuilder();
            foreach (var p in d.Properties()) sb.Append(p.Name).Append('=').Append(p.Value.ToString(Formatting.None).GetHashCode().ToString("x8")).Append(' ');
            var sk = d["skills"] as JObject;
            if (sk != null) foreach (var p in sk.Properties()) { var o = p.Value as JObject; if (o != null && o["numberOfPerksAvailable"] != null) sb.Append("\n  ").Append(p.Name).Append(" perksAvail=").Append(o["numberOfPerksAvailable"]); }
            return sb.ToString();
        }

        public static string DevSummary()
        {
            var pp = Progress(); if (pp == null) return "no progress";
            return "cleaned=" + pp.statistics.cleaned + " painted=" + pp.statistics.painted + " tools=" + (pp.avaialbleTools != null && pp.avaialbleTools.tools != null ? pp.avaialbleTools.tools.Count : -1);
        }

        public static void DevBump()
        {
            var pp = Progress(); if (pp == null) return;
            pp.statistics.cleaned += 5;
            IList list = pp.avaialbleTools.tools; var et = list.GetType().GetGenericArguments()[0];
            foreach (var v in Enum.GetValues(et)) if (!list.Contains(v)) { list.Add(v); Plugin.Log.LogInfo("[dev] unlocked tool " + v); break; }
        }
    }
}
