using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Steam Workshop addons are custom items ("CustomItem_&lt;Steam id&gt;") that the game builds from the player's subscribed Workshop folder. A joiner who is not subscribed to
    /// what the host placed would not get those items, and the game even refunds the sale price of every missing one (which the shared money would pass on to the host).
    /// A client therefore subscribes to the missing items for the length of the session (the game downloads them), waits for the download before it loads the host's
    /// house, and unsubscribes again when the session ends. Only items the mod subscribed to itself are ever unsubscribed; the list is kept in a file so a crash
    /// does not leave them behind. Config: Workshop / TemporaryDownloads.
    /// </summary>
    internal static class Workshop
    {
        const string Prefix = "CustomItem_";
        static readonly HashSet<ulong> Temp = new HashSet<ulong>();
        static string BackupPath { get { return Path.Combine(BepInEx.Paths.BepInExRootPath, "hfcoop-workshop-temp.txt"); } }

        class Wait { public HashSet<ulong> Ids; public int Total; public Action Done; public double Deadline; }
        class Late { public ulong Id; public int Origin; public string NetId, Json; public double Deadline; }
        static Wait _hold;
        static readonly Dictionary<string, Late> _late = new Dictionary<string, Late>();
        static double _next;

        static SteamWorkshop W { get { return SteamWorkshop.instance; } }

        public static void Patch(Harmony h)
        {
            try
            {
                var m = AccessTools.Method(typeof(ItemModification), "SellAndSaveMissingItem");
                if (m != null) h.Patch(m, prefix: new HarmonyMethod(typeof(Workshop), "PreSell"));
                else Plugin.Log.LogWarning("[workshop] SellAndSaveMissingItem not found: missing addons would be refunded on a client");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[workshop] patch failed: " + e.Message); }
        }

        /// <summary>The game "sells" a Workshop item it cannot build and pays the price out. For a client that money would be shared with the host: skip it.</summary>
        static bool PreSell(ulong __0)
        {
            if (!(Session.Role == Role.Client && Session.Active)) return true;
            Plugin.Log.LogWarning("[workshop] item " + __0 + " is not available here: not refunded and not listed as deleted");
            return false;
        }

        static bool TryId(string prefabGuid, out ulong id)
        {
            id = 0;
            return !string.IsNullOrEmpty(prefabGuid) && prefabGuid.StartsWith(Prefix) && ulong.TryParse(prefabGuid.Substring(Prefix.Length), out id);
        }

        static void RefreshSubs()
        {
            try
            {
                if (W == null) return;
                uint n = SteamUGC.GetNumSubscribedItems();
                var a = new PublishedFileId_t[n];
                SteamUGC.GetSubscribedItems(a, n);
                W.subscribedItemArray = a;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[workshop] could not read the subscriptions: " + e.Message); }
        }

        static bool Subscribed(ulong id) { return W != null && W.subscribedItemArray != null && W.ItemSubscribed(id); }

        static bool Ready(ulong id)
        {
            try
            {
                uint s = SteamUGC.GetItemState(new PublishedFileId_t(id));
                uint busy = (uint)EItemState.k_EItemStateNeedsUpdate | (uint)EItemState.k_EItemStateDownloading | (uint)EItemState.k_EItemStateDownloadPending;
                return (s & (uint)EItemState.k_EItemStateInstalled) != 0 && (s & busy) == 0;
            }
            catch { return false; }
        }

        static bool OnDisk(ulong id)
        {
            try
            {
                var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../workshop/content/613100/" + id));
                return Directory.Exists(dir) && Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length > 0;
            }
            catch { return true; } // cannot tell: assume it is the player's
        }

        static bool Usable(ulong id) { return Subscribed(id) && Ready(id); }

        /// <summary>Workshop items of the host's house that this player cannot use yet.</summary>
        public static List<ulong> MissingIn(Recording rec)
        {
            var list = new List<ulong>();
            try
            {
                if (rec == null || rec.modifiedItems == null || W == null) return list;
                RefreshSubs();
                foreach (var m in rec.modifiedItems)
                {
                    ulong id; if (m == null || m.isDestroyed || !TryId(m.prefabGuid, out id) || list.Contains(id)) continue; // sold/removed items need no download
                    if (!Usable(id)) list.Add(id);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[workshop] check failed: " + e.Message); }
            return list;
        }

        static void Subscribe(ulong id)
        {
            try
            {
                if (!Subscribed(id))
                {
                    // Only what is not on this PC at all counts as "ours to remove again". Files that are already here mean the player had the item (or Steam cannot tell, as in a
                    // second game instance): subscribing is harmless then, but unsubscribing afterwards could take away something the player owns.
                    bool onDisk = OnDisk(id);
                    SteamUGC.SubscribeItem(new PublishedFileId_t(id));
                    if (!onDisk) { Temp.Add(id); SaveTemp(); }
                    Plugin.Log.LogInfo("[workshop] subscribed to " + id + (onDisk ? " (already installed here: it stays subscribed)" : " for this session"));
                }
                SteamUGC.DownloadItem(new PublishedFileId_t(id), true);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[workshop] could not subscribe to " + id + ": " + e.Message); }
        }

        static void SaveTemp()
        {
            try { if (Temp.Count == 0) { if (File.Exists(BackupPath)) File.Delete(BackupPath); } else File.WriteAllText(BackupPath, string.Join("\n", Temp.Select(x => x.ToString()).ToArray())); }
            catch { }
        }

        static void Notice(IEnumerable<ulong> ids)
        {
            string s = string.Join(", ", ids.Select(x => x.ToString()).ToArray());
            Plugin.Log.LogWarning("[workshop] the host uses Workshop item(s) you do not have: " + s + (Cfg.TemporaryWorkshop.Value ? "" : " (temporary downloads are off: Workshop / TemporaryDownloads)"));
            Session.Status = "Missing Workshop item(s): " + s;
        }

        /// <summary>
        /// Client, before loading the host's house: get the missing items. Returns true when the load has to wait (done() is called later), false to load at once.
        /// </summary>
        public static bool Hold(List<ulong> ids, Action done)
        {
            if (ids.Count == 0) return false;
            if (!Cfg.TemporaryWorkshop.Value || W == null) { Notice(ids); return false; }
            foreach (var id in ids) Subscribe(id);
            _hold = new Wait { Ids = new HashSet<ulong>(ids), Total = ids.Count, Done = done, Deadline = Time.realtimeSinceStartup + 60f };
            Session.Status = "Downloading the host's Workshop items (0/" + ids.Count + ")...";
            Snapshot.SuspendEvents();
            Plugin.Log.LogInfo("[workshop] waiting for " + ids.Count + " Workshop item(s) before loading the host's house");
            return true;
        }

        /// <summary>Client: an item event for a Workshop item we cannot build yet. Returns true when the event was taken (it is applied later, or dropped).</summary>
        public static bool Defer(int origin, string netId, string json, string prefabGuid)
        {
            if (!(Session.Role == Role.Client && Session.Active)) return false;
            ulong id; if (!TryId(prefabGuid, out id)) return false;
            RefreshSubs();
            if (Usable(id)) return false;
            if (!Cfg.TemporaryWorkshop.Value || W == null) { Notice(new[] { id }); return true; }
            Late l;
            if (!_late.TryGetValue(netId, out l)) { _late[netId] = l = new Late { Id = id, NetId = netId, Deadline = Time.realtimeSinceStartup + 120f }; Subscribe(id); }
            l.Origin = origin; l.Json = json; // the newest state wins
            return true;
        }

        // ---- items from addons the host does not have ----
        // A client may own addons the host has not subscribed to. The host cannot build such an item (and must not stay out of step with the client, nor pass it on to other
        // players), so it refuses it, tells the client, and the client sells it back with the game's own sale (the same refund the game gives for a missing item).
        static readonly HashSet<string> _rejected = new HashSet<string>();
        static bool _noRelay;

        /// <summary>Host: true when the item event for this addon item must be dropped here (and not passed on).</summary>
        public static bool RejectOnHost(int origin, string netId, string prefabGuid)
        {
            if (Session.Role != Role.Host || !Session.Active) return false;
            ulong id; if (!TryId(prefabGuid, out id)) return false;
            RefreshSubs();
            if (Usable(id)) return false;
            _noRelay = true;
            if (_rejected.Add(origin + "|" + netId))
            {
                Plugin.Log.LogWarning("[workshop] player #" + origin + " placed an item of Workshop addon " + id + " that the host does not have: refused");
                Session.Status = "A player placed an item from a Workshop addon you do not have (" + id + "): it was refused";
                Events.Send("workshop", origin + "|" + netId, id.ToString());
            }
            return true;
        }

        /// <summary>Events.Handle asks once per event: was the relay of this event blocked?</summary>
        public static bool TakeRelayBlock() { bool b = _noRelay; _noRelay = false; return b; }

        /// <summary>Client: the host refused one of our Workshop items.</summary>
        public static bool OnMessage(string key, string json)
        {
            if (Session.Role != Role.Client) return false;
            int bar = key.IndexOf('|'); int target;
            if (bar < 0 || !int.TryParse(key.Substring(0, bar), out target) || target != Session.MyId) return true;
            string netId = key.Substring(bar + 1);
            var item = ItemSync.ByNetId(netId);
            Session.Status = "The host does not have the Workshop addon of " + (item != null ? item.name : netId) + " (" + json + "): the item was sold back";
            Plugin.Log.LogWarning("[workshop] the host refused " + netId + " (addon " + json + "): selling it back");
            if (item != null) SellBack(item);
            return true;
        }

        static void SellBack(Item item)
        {
            try
            {
                var icm = UnityEngine.Object.FindObjectOfType<Assets.Code.Player.ItemsCoroutineManager>();
                var sm = typeof(Assets.Code.Player.ItemsCoroutineManager).GetMethod("SellSceneObject");
                if (icm == null || sm == null) { UnityEngine.Object.Destroy(item.gameObject); return; }
                var cbT = sm.GetParameters()[1].ParameterType; var cbArg = cbT.GetGenericArguments()[0];
                var cb = Delegate.CreateDelegate(cbT, typeof(Workshop).GetMethod("Noop", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).MakeGenericMethod(cbArg));
                bool was = Events.Applying; Events.Applying = false; // our own sale: the host is told the item is gone
                try { sm.Invoke(icm, new object[] { item, cb }); } finally { Events.Applying = was; }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[workshop] could not sell the item back: " + (e.InnerException ?? e).Message); }
        }
        static void Noop<T>(T x) { }

        public static void Tick()
        {
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 0.5f;
            if (_hold == null && _late.Count == 0) return;
            RefreshSubs();
            if (_hold != null)
            {
                int ready = _hold.Ids.Count(Usable);
                Session.Status = "Downloading the host's Workshop items (" + ready + "/" + _hold.Total + ")...";
                bool timeout = Time.realtimeSinceStartup > _hold.Deadline;
                if (ready == _hold.Total || timeout)
                {
                    var d = _hold.Done; var missing = _hold.Ids.Where(x => !Usable(x)).ToList(); _hold = null;
                    if (missing.Count > 0) Notice(missing);
                    d();
                }
            }
            if (_late.Count > 0)
            {
                foreach (var key in _late.Keys.ToList())
                {
                    var l = _late[key];
                    if (Usable(l.Id))
                    {
                        _late.Remove(key);
                        bool was = Events.Applying; Events.Applying = true;
                        Plugin.Log.LogInfo("[workshop] item " + l.Id + " is here now: placing it");
                        try { ItemSync.Apply(l.Origin, l.NetId, l.Json); } catch (Exception e) { Plugin.Log.LogWarning("[workshop] could not place " + key + ": " + e.Message); } finally { Events.Applying = was; }
                    }
                    else if (Time.realtimeSinceStartup > l.Deadline) { _late.Remove(key); Notice(new[] { l.Id }); }
                }
            }
        }

        /// <summary>Session ended: unsubscribe from what this session subscribed to (Steam removes the files itself).</summary>
        public static void OnSessionEnd()
        {
            _hold = null; _late.Clear(); _rejected.Clear(); _noRelay = false;
            Unsubscribe();
        }

        static void Unsubscribe()
        {
            if (Temp.Count == 0) return;
            foreach (var id in Temp.ToList())
            {
                try { SteamUGC.UnsubscribeItem(new PublishedFileId_t(id)); Plugin.Log.LogInfo("[workshop] unsubscribed from " + id); }
                catch (Exception e) { Plugin.Log.LogWarning("[workshop] could not unsubscribe from " + id + ": " + e.Message); }
            }
            Temp.Clear(); SaveTemp();
        }

        /// <summary>After a crash the temporary subscriptions are still in the file: remove them at the next start. False until Steam is ready.</summary>
        public static bool RecoverIfNeeded()
        {
            if (Session.Active) return false;
            if (!File.Exists(BackupPath)) return true;
            if (W == null) return false;
            try
            {
                foreach (var line in File.ReadAllLines(BackupPath))
                {
                    ulong id; if (!ulong.TryParse(line.Trim(), out id)) continue;
                    SteamUGC.UnsubscribeItem(new PublishedFileId_t(id));
                    Plugin.Log.LogWarning("[workshop] unsubscribed from " + id + " (left over from an unclean co-op session)");
                }
                File.Delete(BackupPath);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[workshop] recovery failed: " + e.Message); return true; }
            return true;
        }
    }
}
