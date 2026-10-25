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

        /// <summary>The money object the game currently spends from: GameManager keeps it in a private field (profile balance, or an order's own budget).</summary>
        static Money Mon()
        {
            if (!_srcLooked)
            {
                _srcLooked = true;
                foreach (var f in typeof(Code.GameManagers.GameManager).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    if (typeof(Money).IsAssignableFrom(f.FieldType)) { _srcField = f; break; }
            }
            if (_srcField != null)
            {
                object gm = null;
                foreach (var f in typeof(Code.GameManagers.GameManager).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) if (f.FieldType == typeof(Code.GameManagers.GameManager)) { gm = f.GetValue(null); if (gm != null) break; }
                var m = gm == null ? null : _srcField.GetValue(gm) as Money;
                if (m != null) return m;
            }
            if (_pc == null) _pc = UnityEngine.Object.FindObjectOfType<PlayerController>();
            return _pc != null && _pc.progress != null ? _pc.progress.moneyProgress : null;
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
            var m = Mon(); if (m == null) return;
            c.Send(new W(Msg.Event).S("money").I(Session.MyId).S("bal").S(Get(m).ToString("R", CultureInfo.InvariantCulture)).Done());
        }

        static string _pendingBal;

        public static void Tick()
        {
            if (_pendingBal != null && Snapshot.CanApplyState && Mon() != null) { var p = _pendingBal; _pendingBal = null; Apply("bal", p); }
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 0.25f;
            var m = Mon(); if (m == null) return;
            double cur = Get(m);
            if (double.IsNaN(_last)) { _last = cur; return; }
            if (cur == _last) return;
            double delta = cur - _last; _last = cur;
            if (Cfg.VerboseLog.Value) Plugin.Log.LogInfo("[money] local change " + delta + " -> " + cur);
            if (Session.Role == Role.Host) Events.Send("money", "bal", cur.ToString("R", CultureInfo.InvariantCulture));
            else Events.Send("money", "delta", delta.ToString("R", CultureInfo.InvariantCulture));
        }

        public static bool Apply(string key, string json)
        {
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
