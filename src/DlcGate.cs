using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace HFCoop
{
    /// <summary>
    /// DLC awareness. Joining players tell the host which DLCs they own (the game's own ownership check, the one its menus use), and the host tells the joiner which it owns.
    /// A player who lacks a DLC gets a plain message instead of a silent failure when the host's house needs it: the host does not send such a house to that player (SendTo is
    /// refused with a "needs the X DLC" reply), and the player stays where they are until the host moves to a house they can load.
    /// </summary>
    internal static class DlcGate
    {
        static MethodInfo _owns; static Type _dlcT; static bool _resolved;
        static List<string> _ownedCache; static double _ownedAt;

        /// <summary>Order type name -> DLC enum name (houses of the base game need nothing).</summary>
        static readonly Dictionary<string, string> OrderToDlc = new Dictionary<string, string>
        {
            { "PrepperOrder", "ApocalypseFlipper" }, { "GardenOrder", "GardenFlipper" }, { "HgtvOrder", "HgtvFlipper" }, { "LuxuryOrder", "LuxuryFlipper" }, { "Cyberpunk", "Cyberpunk" },
            { "PetsOrder", "Pets" }, { "FarmOrder", "Farm" }, { "DineOutOrder", "DineOut" }, { "PopArtOrder", "PopArt" }, { "PartyOrder", "Party" }
        };

        static void Resolve()
        {
            if (_resolved) return; _resolved = true;
            try
            {
                Type[] types; try { types = typeof(HouseInfo).Assembly.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); }
                foreach (var t in types)
                {
                    foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.ReturnType != typeof(bool)) continue; var ps = m.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType.IsEnum && ps[0].ParameterType.FullName == "Empyrean.HouseFlipper.Dlc") { _owns = m; _dlcT = ps[0].ParameterType; return; }
                    }
                }
                Plugin.Log.LogWarning("[dlc] the game's DLC ownership check was not found: DLC checks are off");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[dlc] resolve failed: " + e.Message); }
        }

        /// <summary>DLC enum names this player owns (cached for a minute).</summary>
        public static List<string> Owned()
        {
            if (_ownedCache != null && UnityEngine.Time.realtimeSinceStartup - _ownedAt < 60f) return _ownedCache;
            Resolve(); var l = new List<string>();
            if (_owns != null)
                foreach (var n in Enum.GetNames(_dlcT))
                {
                    try { if ((bool)_owns.Invoke(null, new object[] { Enum.Parse(_dlcT, n) })) l.Add(n); } catch { }
                }
            foreach (var n in Parse(Cfg.PretendMissingDlc.Value)) l.Remove(n.Trim());   // testing aid: report a DLC as not owned
            _ownedCache = l; _ownedAt = UnityEngine.Time.realtimeSinceStartup; return l;
        }

        public static string OwnedCsv() { return string.Join(",", Owned().ToArray()); }

        public static HashSet<string> Parse(string csv)
        {
            return string.IsNullOrEmpty(csv) ? new HashSet<string>() : new HashSet<string>(csv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>Readable name of a DLC enum value.</summary>
        public static string Pretty(string dlc)
        {
            switch (dlc)
            {
                case "ApocalypseFlipper": return "Apocalypse Flipper"; case "GardenFlipper": return "Garden Flipper"; case "LuxuryFlipper": return "Luxury Flipper"; case "HgtvFlipper": return "HGTV";
                case "PopArt": return "Pop Art"; case "DineOut": return "Dine Out"; default: return dlc;
            }
        }

        public static string PrettyList(IEnumerable<string> l) { return string.Join(", ", l.Select(Pretty).ToArray()); }

        /// <summary>Host: the DLC the currently loaded house belongs to, or null (base game house, or unknown).</summary>
        public static string NeededByCurrentHouse()
        {
            try
            {
                var h = Snapshot.Hsm; var rec = h == null || h.Recorder == null ? null : h.Recorder.currentRecording; if (rec == null || rec.houseInfo == null) return null;
                string order = rec.houseInfo.orderType.ToString(); string dlc; return OrderToDlc.TryGetValue(order, out dlc) ? dlc : null;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[dlc] could not read the house's DLC: " + e.Message); return null; }
        }

        /// <summary>DLCs in 'have' that the other side lacks.</summary>
        public static List<string> Lacking(IEnumerable<string> have, HashSet<string> other) { return have.Where(x => !other.Contains(x)).ToList(); }

        // ---- host side ----
        /// <summary>Host: a joined client's handshake carried its DLC list (null = an older mod version that does not send one).</summary>
        public static void OnHello(Conn c, string csv)
        {
            c.Dlcs = csv == null ? null : Parse(csv);
            if (c.Dlcs == null) { Plugin.Log.LogInfo("[dlc] " + c.Name + " runs a mod version that does not report its DLCs: no DLC checks for them"); return; }
            var lack = Lacking(Owned(), c.Dlcs);
            Plugin.Log.LogInfo("[dlc] " + c.Name + " owns: " + (c.Dlcs.Count == 0 ? "none" : string.Join(", ", c.Dlcs.ToArray())) + (lack.Count > 0 ? " | lacks: " + string.Join(", ", lack.ToArray()) : ""));
            if (lack.Count > 0) Session.Status = c.Name + " does not own: " + PrettyList(lack) + " (those houses will not load for them)";
        }

        /// <summary>Host: true when the client can load the house the host is in; otherwise 'need' is the DLC it lacks.</summary>
        public static bool ClientCanLoad(Conn c, out string need)
        {
            need = NeededByCurrentHouse();
            if (need == null || c.Dlcs == null) return true;
            return c.Dlcs.Contains(need);
        }

        // ---- client side ----
        public static string BlockedBy;   // set while the host's house needs a DLC this player lacks

        /// <summary>Client: the host told what it owns.</summary>
        public static void OnHostList(string csv)
        {
            var lack = Lacking(Parse(csv), new HashSet<string>(Owned()));
            if (lack.Count > 0)
            {
                Plugin.Log.LogWarning("[dlc] the host owns DLC you do not: " + string.Join(", ", lack.ToArray()));
                Session.Status = "Host owns " + PrettyList(lack) + " which you do not: those houses will not load for you";
            }
        }

        /// <summary>Client: the host refused to send its house because of a missing DLC.</summary>
        public static void OnBlocked(string need)
        {
            BlockedBy = need;
            Plugin.Log.LogWarning("[dlc] the host's house needs the " + Pretty(need) + " DLC, which you do not own");
            Session.Status = "The host's house needs the " + Pretty(need) + " DLC (you do not own it). Waiting for the host to move to another house";
        }

        public static void Reset() { BlockedBy = null; }
    }
}
