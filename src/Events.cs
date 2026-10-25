using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace HFCoop
{
    /// <summary>Renovation-event plumbing: Harmony hooks on the game's own recorder, event relay through the host, apply on receipt.</summary>
    internal static class Events
    {
        static Harmony _h;
        /// <summary>True while applying a remote change, so the recorder hooks don't echo it back.</summary>
        public static bool Applying;

        public static void Init()
        {
            _h = new Harmony(Plugin.Guid);
            ItemSync.Patch(_h);
            Interactions.Patch(_h); WallSidePatch.Patch(_h); HouseChangeGuard.Patch(_h); JobSync.PatchComplete(_h); MountSync.Patch(_h); JobSync.PatchPerks(_h); DaySync.PatchBed(_h); HouseActions.Patch(_h); RecSync.Patch(_h);
            var save = AccessTools.Method(typeof(HouseSaveManager), "SaveRecordedGame");
            if (save != null) _h.Patch(save, prefix: new HarmonyMethod(typeof(Events), "BlockClientSave"));
            else Plugin.Log.LogWarning("[save] SaveRecordedGame not found - client saves are NOT blocked");
        }

        static readonly List<KeyValuePair<Conn, byte[]>> Held = new List<KeyValuePair<Conn, byte[]>>();

        public static void Tick()
        {
            Snapshot.Tick();
            if (Snapshot.Loading) return;
            if (Held.Count > 0)
            {
                var h = Held.ToArray(); Held.Clear();
                foreach (var kv in h) { var rr = new R(kv.Value); Handle(kv.Key, rr.Type, rr, kv.Value); }
            }
            Watchdog.Phase("evt.item"); ItemSync.Tick(); InstSync.Tick(); MountSync.Tick(); Watchdog.Phase("evt.rec"); RecSync.Tick(); Watchdog.Phase("evt.money"); MoneySync.Tick(); ProgressSync.Tick(); FollowHost.Tick(); ClockSync.Tick(); DaySync.Tick(); JobSync.Tick(); CarrySync.Tick();
        }

        public static void OnSessionStart() { MoneySync.OnSessionStart(); }

        /// <summary>Clients never write the shared house to disk: only the host's save is authoritative.</summary>
        static bool BlockClientSave(object __1)
        {
            if (Session.Role != Role.Client && !Snapshot.Tainted) return true;
            Plugin.Log.LogInfo("[save] blocked house save: this house copy came from the host (host owns the shared save)");
            var cb = __1 as UnityEngine.Events.UnityAction<bool>; if (cb != null) cb(true);
            return false;
        }

        public static void OnSessionEnd() { ItemSync.Reset(); RecSync.Reset(); Snapshot.Loading = false; Snapshot.Ready = false; Applying = false; MoneySync.OnSessionEnd(); ProgressSync.OnSessionEnd(); FollowHost.Reset(); ClockSync.Reset(); JobSync.Reset(); CarrySync.Reset(); MountSync.Reset(); InstSync.Reset(); }
        public static void OnPlayerJoined(Conn c) { MoneySync.SendBalanceTo(c); ProgressSync.SendFullTo(c); }
        public static void OnPlayerLeft(int id) { }
        /// <summary>Client got Welcome: ask for the host's current house state.</summary>
        public static void OnWelcome(string hostScene) { Snapshot.Request(); }

        /// <summary>Send an event to the rest of the session (host relays client events).</summary>
        public static void Send(string kind, string key, string json)
        {
            if (!Session.Active) return;
            if (Cfg.VerboseLog.Value) Plugin.Log.LogInfo("[evt] send " + kind + " " + key);
            Session.SendAll(new W(Msg.Event).S(kind).I(Session.MyId).S(key).S(json ?? "").Done());
        }

        public static void Handle(Conn from, byte type, R r, byte[] raw)
        {
            if (type == Msg.NotReady) { Snapshot.HostNotReady(); return; }
            if (type == Msg.SnapshotReq) { if (Session.Role == Role.Host) Snapshot.SendTo(from); return; }
            if (type == Msg.Snapshot) { if (Session.Role == Role.Client) Snapshot.Receive(r); return; }
            if (type != Msg.Event) return;
            if (Snapshot.Loading) { Held.Add(new KeyValuePair<Conn, byte[]>(from, raw)); return; } // replay once the house finished loading
            string kind = r.S(); int origin = r.I(); string key = r.S(); string json = r.S();
            if (Cfg.VerboseLog.Value) Plugin.Log.LogInfo("[evt] recv " + kind + " from #" + origin + " " + key);
            bool ok = false;
            Applying = true;
            try
            {
                switch (kind)
                {
                    case "item": ok = ItemSync.Apply(origin, key, json); break;
                    case "rec": ok = RecSync.Apply(key, json); break;
                    case "use": ok = Interactions.Apply(key, json); break;
                    case "money": ok = MoneySync.Apply(key, json); break;
                    case "prog": ok = ProgressSync.Apply(key, json); break;
                    case "house": ok = FollowHost.Apply(); break;
                    case "clock": ok = key == "day" ? DaySync.Apply(json) : ClockSync.Apply(json); break;
                    case "job": ok = JobSync.Apply(json); break;
                    case "carry": ok = CarrySync.Apply(key, json); break;
                    case "mount": ok = MountSync.Apply(key); break;
                    case "inst": ok = InstSync.Apply(key, json); break;
                    default: Plugin.Log.LogWarning("[evt] unknown event kind " + kind); break;
                }
            }
            catch (Exception e) { Plugin.Log.LogError("[evt] apply failed for " + kind + " " + key + ": " + e); }
            finally { Applying = false; }
            // host relays to everyone else so all peers converge on the host's ordering
            if (Session.Role == Role.Host) Session.BroadcastExcept(from, raw);
        }
    }
}

namespace HFCoop
{
    /// <summary>Only the host decides which house everyone is in (accept order, complete job, sleep/office, buy house): a client's own LoadHouse is refused.</summary>
    internal static class HouseChangeGuard
    {
        public static void Patch(HarmonyLib.Harmony h)
        {
            var m = HarmonyLib.AccessTools.Method(typeof(Code.GameManagers.HFSceneManager), "LoadHouse");
            if (m == null) { Plugin.Log.LogWarning("[guard] HFSceneManager.LoadHouse not found"); return; }
            h.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(HouseChangeGuard), "Pre"));
        }

        static bool Pre()
        {
            if (Session.Role != Role.Client || Snapshot.AllowLoad) return true;
            Plugin.Log.LogInfo("[guard] blocked a house change on the client - only the host can accept jobs, complete them, sleep or buy houses");
            Session.Status = "Only the host can change house (job / sleep / buy)";
            return false;
        }
    }
}
