// PROBE-START: P-STOCKTRACE (this whole file is the probe - delete it, the `stocktrace` lever in TestDrive.cs and the one-line tick call at the top of InteriorSync.Tick to remove it)
// WHY (2026-09-27, pre-approved log-only): the hand-off stock oracle finds ONE unit of classiccheapmaleclothing missing in
// the clothing store '20 ba:street_fifthavenue' when the owner stays outside across a game hour and the partner forwards
// sales (T-HANDOFF3-20260927-050743 leg 2, T-HANDOFF1-20260927-051403 leg 3); no traced take accounts for it.
// WHAT: once ARMED by the DEV lever `stocktrace <num> <ba:street_x> <item>`, every change of that shop's stock of that item
// (the same count the oracle reads: the sum of the item's CargoInstance.amount over every reg.itemInstances slot -
// shelves, storage, register) is logged with before/after, the hook that made it, the call stack, and the message context
// (which machine's message was being applied). Hooks: every mod write path to shop cargo (interior snapshot / cargo sync
// applies incl. remote snapshots, the sale decrement, storage ops, the forwarded-order adoption and DeductDisplayStock, the
// hand-off stock adopt / undo / returns, the routed sell-all) and the native take / restock funnels (the two the oracle
// already hooks - ItemHelper.SubtractFromStock, BusinessSimulatorHelper.SimulateBusiness - plus the 1.0 hourly restock and
// the redistribution helpers). A per-frame watcher names any change NO hook saw ("unhooked writer").
// LOG-ONLY: reads the registration, changes nothing. BAMP_DEV only; disarmed by default; budget 400 lines per arm.
#if BAMP_DEV
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Buildings;
using Entities;
using Helpers;
using HarmonyLib;
using UnityEngine;

namespace BigAmbitionsMP
{
    internal static class StockTraceProbe
    {
        private const int Budget = 400;
        private static BuildingRegistration? _reg;
        private static string _addr = "", _item = "", _itemShort = "";
        private static int _lines, _base, _mainTid, _nextResolveFrame, _depth;
        private static bool _budgetSaid;
        private static Dictionary<string, int> _baseSlots = new();
        private static readonly HashSet<BigAmbitions.Items.ItemInstance> _iiSet =
            new(new TillDupes.RefEq<BigAmbitions.Items.ItemInstance>());
        private static readonly List<string> _ctx = new();   // message / hook context stack (main thread only)

        internal static bool Armed => _reg != null;

        internal static string Arm(BuildingRegistration reg, string item)
        {
            try
            {
                _reg = reg;
                _addr = GameStateReader.AddressKey(reg);
                _item = item.Trim();
                _itemShort = CustomerEntrySync.ShortItem(_item);
                _lines = 0; _budgetSaid = false; _ctx.Clear(); _depth = 0;
                var slots = new Dictionary<string, int>();
                _base = Count(reg, slots);
                _baseSlots = slots;
                string who = ""; try { who = MPConfig.PlayerId; } catch { }
                string line = $"armed on '{who}' host={MPServer.IsRunning} shop={_addr} item={_itemShort} count={_base} slots={SlotList(slots)} at={Clock()} budget={Budget}";
                Plugin.Logger.LogInfo("[StockTrace] " + line);
                return line;
            }
            catch (Exception ex) { _reg = null; return "ERR " + ex.Message; }
        }

        internal static string Disarm()
        {
            string s = _reg == null ? "not armed" : $"disarmed shop={_addr} item={_itemShort} lines={_lines}";
            _reg = null; _ctx.Clear(); _depth = 0;
            Plugin.Logger.LogInfo("[StockTrace] " + s);
            return s;
        }

        private static bool Match(string? n)
            => !string.IsNullOrEmpty(n) && (n == _item || CustomerEntrySync.ShortItem(n!) == _itemShort);

        private static int Count(BuildingRegistration reg, Dictionary<string, int>? slots)
        {
            int total = 0;
            _iiSet.Clear();
            if (reg.itemInstances == null) return 0;
            foreach (var kv in reg.itemInstances)
            {
                var ii = kv.Value;
                if (ii == null) continue;
                _iiSet.Add(ii);
                if (ii.cargoInstances == null) continue;
                int n = 0;
                foreach (var ci in ii.cargoInstances)
                    if (ci != null && Match(ci.itemName)) n += (int)ci.amount;
                if (n != 0) { total += n; if (slots != null) slots[kv.Key ?? "?"] = n; }
            }
            return total;
        }

        private static string SlotList(Dictionary<string, int> s)
        {
            if (s.Count == 0) return "-";
            var sb = new StringBuilder();
            foreach (var kv in s) { if (sb.Length > 0) sb.Append(','); sb.Append(Short(kv.Key)).Append(':').Append(kv.Value); }
            return sb.ToString();
        }

        private static string Short(string k) => k.Length > 8 ? k.Substring(0, 8) : k;

        private static string SlotDiff(Dictionary<string, int> a, Dictionary<string, int> b)
        {
            var sb = new StringBuilder();
            foreach (var kv in b)
            {
                a.TryGetValue(kv.Key, out var o);
                if (o != kv.Value) { if (sb.Length > 0) sb.Append(','); sb.Append(Short(kv.Key)).Append(':').Append(o).Append("->").Append(kv.Value); }
            }
            foreach (var kv in a)
                if (!b.ContainsKey(kv.Key)) { if (sb.Length > 0) sb.Append(','); sb.Append(Short(kv.Key)).Append(':').Append(kv.Value).Append("->0"); }
            return sb.Length == 0 ? "-" : sb.ToString();
        }

        private static string Clock()
        {
            try { var tm = TimeHelper.Now(); return $"d{tm.Day}-{tm.Hour:00}:{(int)tm.Minute:00}"; } catch { return "?"; }
        }

        private static void Emit(string s)
        {
            if (_lines >= Budget)
            {
                if (!_budgetSaid) { _budgetSaid = true; Plugin.Logger.LogInfo($"[StockTrace] budget of {Budget} lines spent - further changes not logged."); }
                return;
            }
            _lines++;
            Plugin.Logger.LogInfo("[StockTrace] " + s);
        }

        /// <summary>Compare the live count with the last observed one; log a change as caused by <paramref name="site"/>.</summary>
        private static void Observe(string site, string caller)
        {
            var reg = _reg;
            if (reg == null) return;
            var slots = new Dictionary<string, int>();
            int now = Count(reg, slots);
            if (now != _base)
                Emit($"{_addr} {_itemShort} {_base}->{now} ({(now - _base > 0 ? "+" : "")}{now - _base}) by {site} ctx={(_ctx.Count == 0 ? "local (no hook or message in flight)" : string.Join(" > ", _ctx))} "
                     + $"caller={caller} slots={SlotDiff(_baseSlots, slots)} at={Clock()} f={Time.frameCount}");
            _base = now; _baseSlots = slots;
        }

        /// <summary>Per frame (InteriorSync.Tick, main thread): names changes no hook saw.</summary>
        internal static void Tick()
        {
            try
            {
                if (_mainTid == 0) _mainTid = System.Threading.Thread.CurrentThread.ManagedThreadId;
                if (_reg == null) return;
                if (Time.frameCount >= _nextResolveFrame)
                {
                    _nextResolveFrame = Time.frameCount + 60;
                    var r = GameStatePatcher.FindRegistration(_addr);
                    if (r != null && !ReferenceEquals(r, _reg))
                    {
                        var slots = new Dictionary<string, int>();
                        int n = Count(r, slots);
                        Emit($"{_addr} {_itemShort} registration object REPLACED (old count {_base}, new object {n}) at={Clock()} f={Time.frameCount}");
                        _reg = r; _base = n; _baseSlots = slots;
                    }
                }
                if (_depth == 0) Observe("an UNHOOKED writer (seen by the frame watcher)", "-");
            }
            catch { }
        }

        // ── hook plumbing ──
        private static readonly string[] SenderProps = { "PlayerId", "OwnerPlayerId", "TakerPid", "FromPid", "SenderPid", "BuyerId", "HelperPid" };
        private static readonly Dictionary<Type, List<MemberInfo>> _senderMembers = new();

        private static string Sender(MethodBase m, object[] args)
        {
            try
            {
                var ps = m.GetParameters();
                for (int i = 0; i < ps.Length && i < args.Length; i++)
                {
                    string pn = (ps[i].Name ?? "").ToLowerInvariant();
                    if (args[i] is string s && s.Length > 0 && s.Length < 64 && s.IndexOf("ba:", StringComparison.Ordinal) < 0
                        && (pn.Contains("playerid") || pn.Contains("pid") || pn.Contains("buyer") || pn.Contains("sender")))
                        return s;
                }
                foreach (var a in args)
                {
                    if (a == null || a is string || a.GetType().IsPrimitive) continue;
                    var t = a.GetType();
                    if (!_senderMembers.TryGetValue(t, out var mem))
                    {
                        mem = new List<MemberInfo>();
                        foreach (var n in SenderProps)
                        {
                            var pr = t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
                            if (pr != null && pr.PropertyType == typeof(string)) { mem.Add(pr); continue; }
                            var f = t.GetField(n, BindingFlags.Public | BindingFlags.Instance);
                            if (f != null && f.FieldType == typeof(string)) mem.Add(f);
                        }
                        _senderMembers[t] = mem;
                    }
                    foreach (var mi in mem)
                    {
                        string? v = mi is PropertyInfo p ? p.GetValue(a) as string : (mi as FieldInfo)?.GetValue(a) as string;
                        if (!string.IsNullOrEmpty(v)) return $"{v} ({mi.Name})";
                    }
                }
            }
            catch { }
            return "";
        }

        /// <summary>False = this call cannot touch the armed shop (another shop's registration / address / item instance).</summary>
        private static bool Relevant(MethodBase m, object[] args)
        {
            var reg = _reg;
            if (reg == null) return false;
            foreach (var a in args)
            {
                if (a == null) continue;
                if (a is BuildingRegistration br) { if (!ReferenceEquals(br, reg)) return false; continue; }
                if (a is BigAmbitions.Items.ItemInstance ii) { if (!_iiSet.Contains(ii)) return false; continue; }
                if (a is string s) { if (s.IndexOf("ba:street_", StringComparison.Ordinal) >= 0 && s != _addr) return false; continue; }
                var t = a.GetType();
                if (t.IsGenericType && t.FullName != null && t.FullName.StartsWith("System.ValueTuple", StringComparison.Ordinal))
                {
                    var f1 = t.GetField("Item1");
                    if (f1?.GetValue(a) is BuildingRegistration tr && !ReferenceEquals(tr, reg)) return false;
                    continue;
                }
                var ak = t.GetProperty("AddressKey", BindingFlags.Public | BindingFlags.Instance);
                if (ak != null && ak.PropertyType == typeof(string) && ak.GetValue(a) is string k && k.Length > 0 && k != _addr) return false;
            }
            return true;
        }

        private static string Name(MethodBase m) => (m.DeclaringType?.Name ?? "?") + "." + m.Name;

        internal static bool Enter(MethodBase m, object[] args)
        {
            if (_reg == null || _mainTid == 0 || System.Threading.Thread.CurrentThread.ManagedThreadId != _mainTid) return false;
            if (!Relevant(m, args)) return false;
            // anything that changed since the last observation happened OUTSIDE the hooks - say so before this hook runs
            Observe(_ctx.Count > 0 ? "the body of " + _ctx[_ctx.Count - 1] + " (before it called " + Name(m) + ")"
                                   : "an UNHOOKED writer (seen at the entry of " + Name(m) + ")", _ctx.Count > 0 ? Caller() : "-");
            string snd = Sender(m, args);
            _ctx.Add(snd.Length > 0 ? $"{Name(m)} from {snd}" : Name(m));
            _depth++;
            return true;
        }

        internal static void Exit(MethodBase m, bool entered)
        {
            if (!entered) return;
            try { Observe(Name(m), Caller()); }
            finally
            {
                if (_ctx.Count > 0) _ctx.RemoveAt(_ctx.Count - 1);
                if (_depth > 0) _depth--;
            }
        }

        private static string Caller()
        {
            try
            {
                var st = new System.Diagnostics.StackTrace(1, false);
                var l = new List<string>();
                for (int i = 0; i < st.FrameCount && l.Count < 6; i++)
                {
                    var mb = st.GetFrame(i)?.GetMethod();
                    if (mb == null) continue;
                    string dn = mb.DeclaringType?.Name ?? "";
                    if (dn == nameof(StockTraceProbe) || dn == nameof(Patch_StockTrace_Hooks) || dn.StartsWith("Harmony", StringComparison.Ordinal)) continue;
                    l.Add(string.IsNullOrEmpty(dn) ? mb.Name : dn + "." + mb.Name);
                }
                return l.Count == 0 ? "?" : string.Join("<", l);
            }
            catch { return "?"; }
        }

        // ── targets ──
        internal static IEnumerable<MethodBase> Targets()
        {
            var res = new List<MethodBase>();
            var missing = new List<string>();
            void Add(Type? t, params string[] names)
            {
                foreach (var n in names)
                {
                    bool any = false;
                    if (t != null)
                        foreach (var m in AccessTools.GetDeclaredMethods(t))
                            if (m.Name == n && !m.IsGenericMethodDefinition && !m.IsAbstract && m.GetMethodBody() != null) { res.Add(m); any = true; }
                    if (!any) missing.Add((t?.Name ?? "?") + "." + n);
                }
            }
            // mod write paths
            Add(typeof(InteriorSync), "HandleOwnerSnapshot", "AcceptOwnerSnapshot", "HandleOwnerCargoSync", "NotifyLocalBuildingExit");
            Add(typeof(GameStatePatcher), "ApplyInteriorSnapshot", "ApplyInteriorCargoSync", "ApplyOneItem", "FillCargoInstances", "ApplySaleStockDecrement", "ApplyGuestCargoGrab");
            Add(typeof(StorageSync), "OwnerApply", "OnResult", "OwnerBusinessTail", "TakeStock", "ApplySetStock");
            Add(typeof(CustomerEntrySync), "DeductDisplayStock", "OwnerAdoptForwardedOrder");
            Add(typeof(CustomerHandoff), "StockOnAdopt", "StockAdoptUndo", "ClearStock", "ReturnLeftovers", "ReturnUnsoldWalkOut", "ReturnUnbooked", "BeginAdopt", "EndAdopt");
            Add(typeof(SharedShopWorkTabs), "ApplyRoutedSellAll");
            // native take / restock funnels
            Add(typeof(ItemHelper), "SubtractFromStock");
            Add(typeof(BusinessSimulatorHelper), "SimulateBusiness");
            Add(AccessTools.TypeByName("Helpers.BusinessHelper") ?? AccessTools.TypeByName("BusinessHelper"), "RestockCurrentBusinessIfNeeded");
            Add(AccessTools.TypeByName("ReStockingHelper") ?? AccessTools.TypeByName("Helpers.ReStockingHelper"), "RedistributeStockByPercentage", "TryAddStockAmount");
            Plugin.Logger.LogInfo($"[StockTrace] P-STOCKTRACE hooks: {res.Count} method(s){(missing.Count > 0 ? "; NOT FOUND: " + string.Join(", ", missing) : "")} (disarmed until the `stocktrace` lever).");
            return res;
        }
    }

    [HarmonyPatch]
    internal static class Patch_StockTrace_Hooks
    {
        static IEnumerable<MethodBase> TargetMethods() => StockTraceProbe.Targets();

        static void Prefix(MethodBase __originalMethod, object[] __args, out bool __state)
        {
            __state = false;
            try { if (StockTraceProbe.Armed) __state = StockTraceProbe.Enter(__originalMethod, __args); } catch { __state = false; }
        }

        static void Finalizer(MethodBase __originalMethod, bool __state)
        {
            try { StockTraceProbe.Exit(__originalMethod, __state); } catch { }
        }
    }
}
#endif
// PROBE-END: P-STOCKTRACE
