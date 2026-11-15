using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using Assets.Code.Player;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Shared bank balance. The host's balance is authoritative: clients send signed deltas, the host applies them and broadcasts the absolute balance.
    /// A client's own profile balance is backed up to disk when joining and restored when leaving (or at next start after a crash).
    /// </summary>
    internal static class MoneySync
    {
        static readonly FieldInfo FMoney = typeof(Money).GetField("_money", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static PropertyInfo _setter;
        static PlayerController _pc;
        static double _last = double.NaN, _next;

        static string BackupPath { get { return Path.Combine(BepInEx.Paths.BepInExRootPath, "hfcoop-balance-backup.txt"); } }

        static FieldInfo _srcField; static bool _srcLooked;

        /// <summary>The player's own profile balance: what is shared outside budget jobs, and what a client backs up and gets back when it leaves.</summary>
        static Money Mon()
        {
            if (_pc == null) _pc = UnityEngine.Object.FindObjectOfType<PlayerController>();
            return _pc != null && _pc.progress != null ? _pc.progress.moneyProgress : null;
        }

        /// <summary>The order's own budget while the game spends from one (farm, DineOut and HGTV jobs: GameManager.SetupMoneySource(new OrderBudget(...)));
        /// null when the game spends from the profile balance. The game keeps the current source in a static property of GameManager.</summary>
        static Money Budget()
        {
            if (!_srcLooked)
            {
                _srcLooked = true;
                foreach (var f in typeof(Code.GameManagers.GameManager).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                    if (typeof(Money).IsAssignableFrom(f.FieldType)) { _srcField = f; break; }
                Plugin.Log.LogInfo("[money] money source field " + (_srcField != null ? "found" : "not found (order budgets are not shared)"));
            }
            if (_srcField == null) return null;
            var m = _srcField.GetValue(null) as Money;
            return m != null && !ReferenceEquals(m, Mon()) ? m : null;
        }

        static double Get(Money m) { return (double)FMoney.GetValue(m); }

        static void Set(Money m, double v)
        {
            if (_setter == null)
                foreach (var p in typeof(Money).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (p.PropertyType == typeof(double) && p.GetSetMethod(true) != null) { _setter = p; break; }
            if (_setter != null) _setter.SetValue(m, v, null); else FMoney.SetValue(m, v);
        }

        /// <summary>Called at plugin start: if a previous client session crashed before restoring, put the player's own balance back.</summary>
        public static bool RecoverIfNeeded()
        {
            if (Session.Active) return false;
            if (!File.Exists(BackupPath)) return true;
            var m = Mon(); if (m == null) return false;
            double v;
            if (double.TryParse(File.ReadAllText(BackupPath).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { Set(m, v); Plugin.Log.LogWarning("[money] restored your own balance (" + v + ") after an unclean co-op session"); }
            File.Delete(BackupPath);
            return true;
        }

        public static void DevAdd(double d)
        {
            var m = Mon(); if (m == null) return;
            Set(m, Get(m) + d);
            Plugin.Log.LogInfo("[dev] balance now " + Get(m));
        }

        public static void DevAddBudget(double d)
        {
            var m = Budget(); if (m == null) { Plugin.Log.LogInfo("[dev] no order budget here"); return; }
            Set(m, Get(m) + d);
            Plugin.Log.LogInfo("[dev] budget now " + Get(m));
        }

        public static double BudgetValue() { var m = Budget(); return m == null ? double.NaN : Get(m); }

        public static double Balance() { var m = Mon(); return m == null ? double.NaN : Get(m); }

        public static void OnSessionStart()
        {
            var m = Mon(); if (m == null) return;
            _last = Get(m);
            if (Session.Role == Role.Client)
            {
                File.WriteAllText(BackupPath, _last.ToString("R", CultureInfo.InvariantCulture));
                Plugin.Log.LogInfo("[money] backed up own balance " + _last);
            }
        }

        public static void OnSessionEnd()
        {
            var m = Mon();
            if (m != null && File.Exists(BackupPath))
            {
                double v;
                if (double.TryParse(File.ReadAllText(BackupPath).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { Set(m, v); Plugin.Log.LogInfo("[money] restored own balance " + v); }
                File.Delete(BackupPath);
            }
            _last = double.NaN;
        }

        public static void SendBalanceTo(Conn c)
        {
            var m = Mon(); if (m != null) c.Send(new W(Msg.Event).S("money").I(Session.MyId).S("bal").S(Get(m).ToString("R", CultureInfo.InvariantCulture)).Done());
            var bm = Budget(); if (bm != null) c.Send(new W(Msg.Event).S("money").I(Session.MyId).S("bbal").S(Get(bm).ToString("R", CultureInfo.InvariantCulture)).Done());
        }

        static string _pendingBal, _pendingBud;
        static Money _budObj; static double _budLast = double.NaN;

        public static void Tick()
        {
            if (_pendingBal != null && Snapshot.CanApplyState && Mon() != null) { var p = _pendingBal; _pendingBal = null; Apply("bal", p); }
            if (_pendingBud != null && Snapshot.CanApplyState && Budget() != null) { var p = _pendingBud; _pendingBud = null; Apply("bbal", p); }
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 0.25f;
            var m = Mon();
            if (m != null)
            {
                double cur = Get(m);
                if (double.IsNaN(_last)) _last = cur;
                else if (cur != _last)
                {
                    double delta = cur - _last; _last = cur;
                    if (Cfg.VerboseLog.Value) Plugin.Log.LogInfo("[money] local change " + delta + " -> " + cur);
                    if (Session.Role == Role.Host) Events.Send("money", "bal", cur.ToString("R", CultureInfo.InvariantCulture));
                    else Events.Send("money", "delta", delta.ToString("R", CultureInfo.InvariantCulture));
                }
            }
            // the order budget: a new budget object (another house) is a new baseline, not spending
            var bm = Budget();
            if (!ReferenceEquals(bm, _budObj)) { _budObj = bm; _budLast = bm != null ? Get(bm) : double.NaN; if (bm != null) Plugin.Log.LogInfo("[money] this job has its own budget (" + _budLast + ")"); }
            else if (bm != null)
            {
                double cur = Get(bm);
                if (cur != _budLast)
                {
                    double delta = cur - _budLast; _budLast = cur;
                    if (Cfg.VerboseLog.Value) Plugin.Log.LogInfo("[money] budget change " + delta + " -> " + cur);
                    if (Session.Role == Role.Host) Events.Send("money", "bbal", cur.ToString("R", CultureInfo.InvariantCulture));
                    else Events.Send("money", "bdelta", delta.ToString("R", CultureInfo.InvariantCulture));
                }
            }
        }

        public static bool Apply(string key, string json)
        {
            if (key == "bbal" || key == "bdelta")
            {
                var bm = Budget();
                if (bm == null || !Snapshot.CanApplyState)
                {
                    // the client's house (and its budget) is not there yet: the host's number is applied once it is; a delta for a house without a budget is dropped
                    if (key == "bbal" && Session.Role == Role.Client) { _pendingBud = json; return true; }
                    return key == "bdelta";
                }
                double bv = double.Parse(json, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (key == "bbal") { Set(bm, bv); _budObj = bm; _budLast = Get(bm); return true; }
                if (Session.Role != Role.Host) return false;
                Set(bm, Get(bm) + bv); _budObj = bm; _budLast = Get(bm);
                Events.Send("money", "bbal", _budLast.ToString("R", CultureInfo.InvariantCulture));
                return true;
            }
            if (key == "bal" && Session.Role == Role.Client && (!Snapshot.CanApplyState || Mon() == null)) { _pendingBal = json; return true; }
            var m = Mon(); if (m == null) return false;
            double v = double.Parse(json, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (Session.Role == Role.Client && !File.Exists(BackupPath)) { File.WriteAllText(BackupPath, Get(m).ToString("R", CultureInfo.InvariantCulture)); Plugin.Log.LogInfo("[money] backed up own balance " + Get(m) + " (before first sync)"); }
            if (key == "bal") { Set(m, v); _last = Get(m); return true; }
            if (key == "delta" && Session.Role == Role.Host)
            {
                Set(m, Get(m) + v); _last = Get(m);
                Events.Send("money", "bal", _last.ToString("R", CultureInfo.InvariantCulture)); // everyone (incl. sender) converges on the host's number
                return true;
            }
            return false;
        }
    }
}
