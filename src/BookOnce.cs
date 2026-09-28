using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AI.Customers.CustomerEntries;
using Buildings;
using Entities;
using HarmonyLib;
using Helpers;

namespace BigAmbitionsMP
{
    /// <summary>H-HANDOFF-1 money restructure (2026-09-27) - BOOK ONCE.
    ///
    /// ONE rule on the machine that keeps a shop's books (MergerFlip.BooksHere): every customer visit that crosses a
    /// hand-off is booked EXACTLY ONCE, by whichever booking reaches this machine first; every later booking of that
    /// visit is suppressed here. It replaces the hand-off marks (CustomerEntrySync._handedOff), first-booking-wins
    /// (fold F1), the mark clearing on a take-back (F4), the 24 h mark life (F6), the booked-body walk-out (F2) and the
    /// F5 'entry consumed' refusal.
    ///
    /// REGISTERED (keyed by the schedule entry's cross-machine id, CustomerEntrySync.EntryIdForOrder): every entry this
    /// booking machine sends in a Final or stream row (CustomerHandoff.SendFinal / StreamTick), receives in one
    /// (CustomerHandoff.Apply) or adopts (CustomerPuppets.AdoptPuppetAsNative). Initial state: Booked when its Order
    /// already sits in the till (reference check) or a forward for it was already adopted; else Unbooked. Lives 24
    /// game-hours; cleared on CustomerHandoff.Reset (session / scene).
    ///
    /// THE FUNNELS on the booking machine (decompile Build 3682) and how each consults the registry:
    ///   - Customer.CompleteOrder (Customer.cs:385-397) - the customer's own till add; also reached from the gym fee
    ///     (Customer.PayEntranceFee :224-238), BarmanEmployee :131, CoatCheckEmployee :92, CompleteOrderIfAllIsProcessed
    ///     :19 and Customer.cs:369. Postfix -> Scan (till watcher, below).
    ///   - FullServiceEmployee.ServeCustomer (:143-157, Order.Pay then the till add, possibly one yield later) and
    ///     TicketKioskController.ServeCustomer (:144): inline adds inside coroutines - the per-frame till watcher
    ///     (Tick -> Scan) takes them; Order.Pay (postfix) only names the funnel.
    ///   - SelfServiceEmployee.TellCurrentCustomerToLeave (:141-156). Postfix -> Scan.
    ///   - The native hourly pass (BusinessSimulatorHelper.SimulateBusiness :58-67 -> every simulator's ProcessCustomer:
    ///     Retail :229-273, Gym :42-70, Cinema :27-63, SelfService :65, Hairdresser :38, Nightclub :102, Office :61)
    ///     and the MP skip pass (MPPatches Patch_RunHourly_SimulateOccupiedShopDuringSkip): HourlyBegin sets a Booked
    ///     entry of that hour ASIDE from the schedule list for the length of the pass (no till add, no stock, no fee);
    ///     HourlyEnd puts it back and books every Unbooked entry whose Order the pass put in the till.
    ///   - The forwarded sale (CustomerEntrySync.OwnerAdoptForwardedOrder): TryBook before any stock is touched.
    /// An UNPAID till add (nothing the day roll would pay - a refused fee, the exit clean-up of an unpaid order) never
    /// books a registered visit (fold M2, 2026-09-27): it STAYS in the till - it feeds the pricing satisfaction
    /// (BusinessHelper.cs:830-842), the hourly quota (CustomerEntriesCalculator :100) and the security count
    /// (BusinessSecurityHelper :188) - as a frozen SNAPSHOT (a copy), so the visit's own Order is free to go on and
    /// book once later without a second reference in the till. A till add made inside the native exit clean-up
    /// (Customer.ForceFinishOrder :363-371, fold M1) is never a booking either, paid lines or not: its paid lines
    /// (a gym fee, a coat-check fee, a bar round) are counted exactly once - by the snapshot while the visit stays
    /// unbooked, and the snapshot is UNPAID the moment a later booking of the visit (which carries those lines: the
    /// row / forward holds them paid) books it (UnpaySnap).
    /// The till watcher (fold M3: by reference, not by list length): every Order occurrence appended to the till of
    /// the building this machine stands in is looked up (registered entry Order, or a detached body copy); an Unbooked
    /// visit is booked by a paid one, a Booked one is removed again at once with one
    /// '[BookOnce] &lt;id&gt; already booked by &lt;source&gt; - &lt;funnel&gt; suppressed' line.
    /// Fold H2 (stock): a visit booked while this machine's own body of it is alive with an open order - that body's
    /// order is finished (CustomerPuppets.FinishLiveBodiesOf) so it is never served a second time; a booked adopted
    /// body's detached copy keeps only its done lines and never returns items to this machine's shelves.</summary>
    internal static class BookOnce
    {
        private sealed class Rec
        {
            public string Addr = "";
            public int RegisteredMin;
            public bool Booked;
            public string Source = "";
            public Order? BookedOrder;
            public Order? ExitSnap;                  // fold M1: a partly paid exit clean-up kept in the till (unbooked visit)
            public BuildingRegistration? Reg;
        }

        /// <summary>Fold M3: Orders compared by reference (the till watcher's occurrence counts).</summary>
        private sealed class RefEq : IEqualityComparer<Order>
        {
            internal static readonly RefEq I = new RefEq();
            public bool Equals(Order x, Order y) => ReferenceEquals(x, y);
            public int GetHashCode(Order o) => RuntimeHelpers.GetHashCode(o);
        }

        private static readonly Dictionary<string, Rec> _recs = new();
        private static ConditionalWeakTable<Order, string> _orderIds = new();
        private static ConditionalWeakTable<Order, object> _paidHere = new();
        private static readonly object _mark = new();
        private static readonly Dictionary<string, int> _registeredBy = new(), _bookedBy = new(), _suppressedBy = new();
        private const int LifeMinutes = 24 * 60;
        private const int MaxRecs = 3000;
        private static BuildingRegistration? _watchReg;
        private static int _watchCount = -1;
        private static int _nextPruneMin = -1;
        private static bool _inFee;
        private static Order? _exitOrder;            // fold M1 / S6: the Order Customer.ForceFinishOrder is finishing (null: none)
        // Fold S1: a kept till snapshot -> the Order it froze (and each of its lines -> the line it froze); fold S6 oracle:
        // the Orders an exit clean-up finished.
        private static ConditionalWeakTable<Order, Order> _snapOf = new();
        private static ConditionalWeakTable<OrderEntry, OrderEntry> _snapLineOf = new();
        private static ConditionalWeakTable<Order, object> _exitOrders = new();
        internal static int Suppressed;
        // Fold M3: the till watcher's occurrence counts per Order reference (two maps swapped per look).
        private static readonly Dictionary<Order, int> _seenA = new(RefEq.I), _seenB = new(RefEq.I);
        private static Dictionary<Order, int> _seen = _seenA;
        private static Order? _watchLast;
        // Fold H2(b): detached copies of booked visits (a booked adopted body's order).
        private static ConditionalWeakTable<Order, object> _bookedCopies = new();
        // Fold H2(a): visits booked this frame whose live body here must be finished (processed in Tick).
        private static readonly List<KeyValuePair<string, Order?>> _finish = new();
        internal static int UnpaidKept, ExitKept, ExitUnpaid, Finished, CopyReturnsBlocked;
        private static readonly Dictionary<string, int> _keptBy = new();

        internal static void Reset()
        {
            try
            {
                _recs.Clear();
                _hourEnd.Clear();   // H-HOURROLL H2
                _orderIds = new ConditionalWeakTable<Order, string>();
                _paidHere = new ConditionalWeakTable<Order, object>();
                _registeredBy.Clear(); _bookedBy.Clear(); _suppressedBy.Clear(); _keptBy.Clear();
                UnpaidKept = 0; ExitKept = 0; ExitUnpaid = 0; Finished = 0; CopyReturnsBlocked = 0;
                _watchReg = null; _watchCount = -1; _watchLast = null; _seenA.Clear(); _seenB.Clear(); _seen = _seenA;
                _nextPruneMin = -1; _inFee = false; _exitOrder = null; Suppressed = 0;
                _snapOf = new ConditionalWeakTable<Order, Order>();
                _snapLineOf = new ConditionalWeakTable<OrderEntry, OrderEntry>();
                _exitOrders = new ConditionalWeakTable<Order, object>();
                _bookedCopies = new ConditionalWeakTable<Order, object>();
                _finish.Clear();
                // Fold L: the forward ledger is per session like the registry (H1: a forward-booked entry left the
                // schedule at forward time, so nothing it booked can be re-booked by a pass after this wipe).
                CustomerEntrySync.ClearBookedForwards();
            }
            catch { }
        }

        private static int NowMin()
        {
            try { var t = TimeHelper.Now(); return t.Day * 1440 + t.Hour * 60 + (int)t.Minute; } catch { return 0; }
        }

        private static void Bump(Dictionary<string, int> d, string k)
        {
            d.TryGetValue(k, out var n); d[k] = n + 1;
        }

        private static void Prune()
        {
            try
            {
                int now = NowMin();
                if (now < _nextPruneMin || _recs.Count == 0) return;
                _nextPruneMin = now + 30;
                List<string>? old = null;
                foreach (var kv in _recs) if (now - kv.Value.RegisteredMin > LifeMinutes) (old ??= new List<string>()).Add(kv.Key);
                if (old != null)
                {
                    foreach (var k in old) _recs.Remove(k);
                    Plugin.Logger.LogInfo($"[BookOnce] {old.Count} registration(s) older than 24 game-hours dropped ({_recs.Count} left).");
                }
            }
            catch { }
        }

        // H2 (review of 95cf5e1): visits that walked out UNSOLD (released with nothing paid) on a partner's machine. Their
        // entry leaves the owner's live hourly table (as a forward's claimed entry does, CustomerEntrySync Recheck B2) and
        // the hourly pass sets any entry of theirs aside (HourlyBegin) - single-player never bills a customer who left. A
        // later sale forward can still book the visit. Survives Reset (the entry is gone from the table anyway).
        private static readonly Dictionary<string, string> _ended = new();
        internal static int EndedUnsold;
        internal static bool IsEnded(string id) => !string.IsNullOrEmpty(id) && _ended.ContainsKey(id);

        // Fold R2 (2026-09-28): visits whose customer turned to leave on a partner's machine (early 220) - 'ending': the
        // hourly pass sets their entry aside WITHOUT touching stock; the entry stays in the table so a sale forward still
        // claims and books it (a forward wins over 'ending'). Ends at the release-time settle (EndUnsold), at a booking, or
        // - if neither comes (the partner dropped) - at the owner's second hour boundary after the mark (logged).
        private sealed class Ending { public string Addr = "", From = ""; public int MarkAbs; }
        private static readonly Dictionary<string, Ending> _ending = new();
        internal static int EndingMarked, EndingCleared;

        // H-HOURROLL (2026-09-28): the owner's hourly pass for a shop whose live customers a PARTNER runs right now.
        // PartnerLiveAside = hour entries set aside for it; PassBooked = bookings made by an hourly pass; PassPreempt = a
        // sale forward suppressed because an hourly pass booked the visit first (rig oracle: 0).
        internal static int PartnerLiveAside, PassPreempt, PassBooked;
        // H-HOURROLL folds (review of 480184b, 2026-09-28): NotSentBilled = entries of a partner-run hour billed by the pass
        // because they were never sent to a partner (H1); SilentHours = hour ends where the elected partner stood inside but
        // showed no recent sign of life, so the pass billed the hour normally (H3).
        internal static int NotSentBilled, SilentHours;

        /// <summary>The funnel that booked this visit ("" = not registered here or not booked yet).</summary>
        internal static string SourceOf(string id)
        {
            try { return !string.IsNullOrEmpty(id) && _recs.TryGetValue(id, out var r) && r.Booked ? (r.Source ?? "") : ""; }
            catch { return ""; }
        }

        /// <summary>Is <paramref name="source"/> an hourly pass (native or skip)?</summary>
        internal static bool IsPassSource(string? source) => !string.IsNullOrEmpty(source) && source!.Contains("hourly pass");

        /// <summary>H-HOURROLL (user ruling 2026-09-27): only shop types whose live customers' sales are FORWARDED to the
        /// owner may be set aside. Forwarding happens at Order.Pay with isPlayer false (BusinessPatches
        /// Patch_Order_Pay_HelperForward), reached by SelfServiceEmployee :78 and FullServiceEmployee :136 in the plain
        /// self-service / full-service shops. Gym (Customer.CompleteOrder, no Pay), nightclub (bar / coat check / door
        /// fee), cinema (ticket booth), hairdresser (stylist pays with isPlayer: true) and every other simulator are
        /// billed by the owner's paper pass - never set aside. A shop with an entrance fee is excluded too.</summary>
        private static bool ForwardedSalesType(BuildingRegistration reg)
        {
            try
            {
                var data = BusinessTypeHelper.GetData(reg);
                if (data == null || data.simulator == null || !data.spawnCustomers) return false;
                var t = data.simulator.GetType();
                if (t != typeof(global::Buildings.Retail.Simulation.SelfServiceBusinessSimulator)
                    && t != typeof(global::Buildings.Retail.Simulation.FullServiceBusinessSimulator)) return false;
                string fee = "";
                try { fee = BusinessTypeHelper.GetEntranceFeeNameForBusinessType(data) ?? ""; } catch { }
                return fee.Length == 0;
            }
            catch { return false; }
        }

        /// <summary>H-HOURROLL: another machine runs this shop's live customers (the host's elected simulator is a partner
        /// who stands inside), outside a skip and outside the hour a skip ended in, for a forwarded-sales shop type, AND
        /// that partner shows a recent sign of life for this shop (H3, review of 480184b). Single-player parity:
        /// BusinessSimulatorHelper.cs:32 skips the whole occupied shop. Evaluated at the hour's END (RecordHourEnd).
        /// <paramref name="why"/> names the rule that decided.</summary>
        private static bool PartnerSimulates(BuildingRegistration reg, int hour, out string pid, out string why)
        {
            pid = ""; why = "";
            string a = GameStateReader.AddressKey(reg);
            if (string.IsNullOrEmpty(a)) { why = "no address"; return false; }
            pid = CustomerPuppets.SimulatorFor(a) ?? "";
            if (pid.Length == 0 || pid == MPConfig.PlayerId) { why = "no partner is the elected simulator"; return false; }
            if (!CustomerHandoff.PlayerInside(pid, a)) { why = "the partner is not inside"; return false; }
            if (!ForwardedSalesType(reg)) { why = "not a forwarded-sales shop type"; return false; }
            if (MPRestSync.SkipActive) { why = "a skip is running"; return false; }   // a skip: the partner's spawner denies bodies - the pass bills them
            try { if (InstanceBehavior<global::UI.UIs>.Instance.timeMachine.isRunning) { why = "the time machine runs"; return false; } } catch { }   // BSH:32 parity
            var (gd, gh) = GameStateReader.GetGameTime();
            double passStartMin = gd * 1440.0 + hour * 60.0;
            if (hour > (int)gh) passStartMin -= 1440.0;   // the pass is for an hour of the previous day
            if (MPRestSync.LastSkipEndMinutes >= passStartMin) { why = "the hour a skip ended in"; return false; }   // H-SKIPTAIL analogue
            // H3 (review of 480184b, 2026-09-28): a partner whose machine went quiet (frozen, stuck, a stale presence) does
            // not get the hour - the owner's pass bills it as before (when in doubt the owner's pass bills).
            if (!CustomerHandoff.SignOfLife(pid, a, out var life)) { why = "no recent sign of life from the partner (" + life + ")"; SilentHours++; return false; }
            why = life;
            return true;
        }

        // H-HOURROLL H2 (review of 480184b, 2026-09-28): WHO runs a shop's live customers is decided at the moment the hour
        // ENDS - the prefix on the game's BusinessSimulatorHelper.RunHourly, its own decision point (BSH:19-32: the occupied
        // shop is skipped right there in single-player) - and the pass reads that record, never a later live read: the
        // pass can be queued on DistributedWork (BSH:36-39) and run after the partner walked in or out.
        private sealed class HourEnd { public string Pid = "", Why = ""; public bool Live; public float AtMin; }
        private static readonly Dictionary<string, HourEnd> _hourEnd = new();

        /// <summary>H2: the RunHourly prefix - one decision per shop that books here and has a partner as its elected
        /// simulator (no record = no partner = the pass bills normally). Main thread.</summary>
        internal static void RecordHourEnd()
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null || gi.BuildingRegistrations == null) return;
                int hour = gi.Hour;
                float now = TimeHelper.NowInMinutes();
                foreach (var reg in gi.BuildingRegistrations)
                {
                    try
                    {
                        if (reg == null || !MergerFlip.BooksHere(reg)) continue;
                        string a = GameStateReader.AddressKey(reg);
                        if (string.IsNullOrEmpty(a)) continue;
                        string key = a + "|" + hour;
                        string sim = CustomerPuppets.SimulatorFor(a) ?? "";
                        if (sim.Length == 0 || sim == MPConfig.PlayerId) { _hourEnd.Remove(key); continue; }
                        bool live = PartnerSimulates(reg, hour, out var pid, out var why);
                        _hourEnd[key] = new HourEnd { Pid = pid, Why = why, Live = live, AtMin = now };
                        Plugin.Logger.LogInfo(live
                            ? $"[BookOnce] hour-end record {a} h{hour}: partner {pid} runs the live customers - the pass sets aside the entries sent to partners ({why})."
                            : $"[BookOnce] hour-end record {a} h{hour}: partner {pid} is the elected simulator but the pass bills normally ({why}).");
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[BookOnce] hour-end record: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BookOnce] hour-end record: {ex.Message}"); }
        }

        /// <summary>H2: the pass's read of the hour-end record (a record older than two game hours is stale: bill).</summary>
        private static bool HourEndSaysPartner(BuildingRegistration reg, int hour, out string pid, out string why)
        {
            pid = ""; why = "no hour-end record";
            string a = GameStateReader.AddressKey(reg);
            if (string.IsNullOrEmpty(a) || !_hourEnd.TryGetValue(a + "|" + hour, out var d) || d == null) return false;
            float age = TimeHelper.NowInMinutes() - d.AtMin;
            if (age < -1f || age > 120f) { why = $"stale hour-end record ({age:0} game min old)"; return false; }
            pid = d.Pid; why = d.Why;
            return d.Live;
        }
        private static int NowAbsHour()
        {
            try { var tm = TimeHelper.Now(); return (int)tm.Day * 24 + (int)tm.Hour; } catch { return -1; }
        }

        /// <summary>R2: the visit's sale arrived (a forward, which also covers a visit never registered here) - the mark ends.</summary>
        internal static void ClearEnding(string id, string why)
        {
            try
            {
                if (string.IsNullOrEmpty(id) || !_ending.Remove(id)) return;
                Plugin.Logger.LogInfo($"[BookOnce] {id} ending mark ended: {why}.");
            }
            catch { }
        }

        /// <summary>R2, OWNER, MAIN THREAD: the early notice of a partner's leaving customer.</summary>
        internal static void MarkEnding(BuildingRegistration? reg, string id, string from)
        {
            try
            {
                if (reg == null || string.IsNullOrEmpty(id) || IsBooked(id) || _ended.ContainsKey(id) || _ending.ContainsKey(id)) return;
                if (_ending.Count > 2000) EvictEnding();   // U4: expired / oldest marks go, never all of them
                var en = new Ending { Addr = GameStateReader.AddressKey(reg), From = from ?? "", MarkAbs = NowAbsHour() };
                _ending[id] = en;
                EndingMarked++;
                Plugin.Logger.LogInfo($"[BookOnce] {id} ending - leaving on {en.From}'s machine: kept out of the hourly pass until its release report or its sale (stock untouched; cleared after the next hour if neither comes).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BookOnce] mark ending: {ex.Message}"); }
        }

        /// <summary>U4 (re-check of f132da7, 2026-09-28): the ending-mark overflow. Marks that are over (booked, ended, or
        /// past the hour boundary ExpireEnding would clear them at) go first, then the oldest, down to 1900 - a Clear()
        /// would reopen every live walk-out to its hourly pass at once.</summary>
        private static void EvictEnding()
        {
            try
            {
                int nowAbs = NowAbsHour();
                var drop = new List<string>();
                foreach (var kv in _ending)
                    if (IsBooked(kv.Key) || _ended.ContainsKey(kv.Key) || (nowAbs >= 0 && kv.Value.MarkAbs >= 0 && nowAbs - 1 > kv.Value.MarkAbs))
                        drop.Add(kv.Key);
                foreach (var id in drop) _ending.Remove(id);
                int oldest = 0;
                if (_ending.Count > 1900)
                {
                    var rest = new List<KeyValuePair<string, Ending>>(_ending);
                    rest.Sort((a, b) => a.Value.MarkAbs.CompareTo(b.Value.MarkAbs));
                    for (int i = 0; i < rest.Count && _ending.Count > 1900; i++) { _ending.Remove(rest[i].Key); oldest++; }
                }
                Plugin.Logger.LogInfo($"[BookOnce] ending marks over the cap: {drop.Count} expired and {oldest} oldest evicted, {_ending.Count} kept.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BookOnce] evict ending: {ex.Message}"); }
        }

        /// <summary>H2, OWNER, MAIN THREAD (CustomerHandoff.SettleUnsoldWalkOut): the visit ended unsold - kept out of the
        /// hourly pass. Idempotent.</summary>
        internal static void EndUnsold(BuildingRegistration? reg, string id)
        {
            try
            {
                if (!string.IsNullOrEmpty(id)) _ending.Remove(id);   // R2: the release report replaces the early mark
                if (reg == null || string.IsNullOrEmpty(id) || IsBooked(id) || _ended.ContainsKey(id)) return;
                if (_ended.Count > 4000) _ended.Clear();
                string addr = GameStateReader.AddressKey(reg);
                _ended[id] = addr;
                EndedUnsold++;
                int removed = 0;
                var entries = CustomerEntrySync.EntriesOf(reg);
                if (entries != null)
                    for (int i = entries.Count - 1; i >= 0; i--)
                    {
                        var e = entries[i];
                        if (e == null) continue;
                        string? eid = IdOfOrder(e.order) ?? CustomerEntrySync.KnownIdOf(e);
                        if (eid != id) continue;
                        // U5 (re-check of f132da7, 2026-09-28): the entry STAYS in the table, marked completed - removing it
                        // let a mid-day entry regeneration create a replacement customer. HourlyBegin sets ended ids aside.
                        e.completed = true;
                        removed++;
                    }
                Plugin.Logger.LogInfo($"[BookOnce] {id} ended unsold - kept out of the hourly pass ({(removed > 0 ? $"{removed} live entr{(removed == 1 ? "y" : "ies")} marked completed in @{addr}'s table" : "no live entry of it here")}; a later sale forward can still book it).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BookOnce] end unsold: {ex.Message}"); }
        }

        internal static int Count => _recs.Count;
        internal static bool IsRegistered(string id) => !string.IsNullOrEmpty(id) && _recs.ContainsKey(id);
        internal static bool IsBooked(string id) => !string.IsNullOrEmpty(id) && _recs.TryGetValue(id, out var r) && r.Booked;
        /// <summary>The Order that booked this visit (null: none, or a forward whose Order the till ledger holds).</summary>
        internal static Order? BookedOrderOf(string id) => !string.IsNullOrEmpty(id) && _recs.TryGetValue(id, out var r) && r.Booked ? r.BookedOrder : null;

        /// <summary>A body's Order (the entry's own, or a detached copy) -> the visit id.</summary>
        internal static void MapOrder(Order? o, string id)
        {
            try
            {
                if (o == null || string.IsNullOrEmpty(id)) return;
                _orderIds.Remove(o);
                _orderIds.Add(o, id);
            }
            catch { }
        }

        private static string? IdOfOrder(Order? o)
            => o != null && _orderIds.TryGetValue(o, out var id) ? id : null;

        /// <summary>The visit id a body's Order is mapped to (null: none).</summary>
        internal static string? IdOf(Order? o) => IdOfOrder(o);

        /// <summary>Fold H2(b): the detached copy a booked adopted body carries.</summary>
        internal static void MarkBookedCopy(Order? o)
        {
            try { if (o != null) { _bookedCopies.Remove(o); _bookedCopies.Add(o, _mark); } } catch { }
        }
        internal static bool IsBookedCopy(Order? o) => o != null && _bookedCopies.TryGetValue(o, out _);
        internal static void NoteCopyReturnBlocked() { CopyReturnsBlocked++; }

        /// <summary>Fold S1: the Order a kept till snapshot froze (null: not a snapshot).</summary>
        internal static Order? SnapOrigin(Order? o)
        {
            try { return o != null && _snapOf.TryGetValue(o, out var src) ? src : null; } catch { return null; }
        }
        internal static bool IsSnapshot(Order? o) => SnapOrigin(o) != null;
        /// <summary>Fold S1: the line a snapshot line froze (null: not a snapshot line).</summary>
        internal static OrderEntry? SnapLineOrigin(OrderEntry? e)
        {
            try { return e != null && _snapLineOf.TryGetValue(e, out var src) ? src : null; } catch { return null; }
        }
        /// <summary>An Order (or a snapshot of one) that the native exit clean-up (Customer.ForceFinishOrder) finished.</summary>
        internal static bool IsExitOrder(Order? o)
        {
            try
            {
                if (o == null) return false;
                if (_exitOrders.TryGetValue(o, out _)) return true;
                var src = SnapOrigin(o);
                return src != null && _exitOrders.TryGetValue(src, out _);
            }
            catch { return false; }
        }

        private static bool InTill(BuildingRegistration reg, Order o)
        {
            try
            {
                var till = reg.unprocessedCompletedOrders;
                if (till != null) foreach (var t in till) if (ReferenceEquals(t, o)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>Register one visit on the machine that keeps <paramref name="reg"/>'s books (no-op anywhere else).
        /// Returns true when it was newly registered.</summary>
        internal static bool Register(BuildingRegistration? reg, string id, CustomerEntry? entry, string how)
        {
            try
            {
                if (reg == null || string.IsNullOrEmpty(id) || id.StartsWith("i", StringComparison.Ordinal)) return false;
                if (_recs.ContainsKey(id))
                {
                    if (entry?.order != null) MapOrder(entry.order, id);
                    return false;
                }
                if (!MergerFlip.BooksHere(reg)) return false;
                Prune();
                if (_recs.Count >= MaxRecs) return false;
                entry ??= CustomerEntrySync.TryFindEntry(reg, id);
                if (entry?.order != null) MapOrder(entry.order, id);
                var r = new Rec { Addr = GameStateReader.AddressKey(reg), RegisteredMin = NowMin(), Reg = reg };
                if (CustomerEntrySync.ForwardBooked(id)) { r.Booked = true; r.Source = "forwarded sale (before registration)"; }
                else if (entry?.order != null && InTill(reg, entry.order))
                {
                    if (HasPaid(entry.order)) { r.Booked = true; r.Source = "till (before registration)"; r.BookedOrder = entry.order; }
                    else KeepInTill(reg, entry.order, id, r, "till (before registration)", false);
                }
                _recs[id] = r;
                Bump(_registeredBy, how);
                if (r.Booked) Bump(_bookedBy, r.Source);
                Plugin.Logger.LogInfo($"[BookOnce] {id} registered ({how}) @{r.Addr}: {(r.Booked ? "booked by " + r.Source : "unbooked")}{(entry == null ? ", no schedule entry here" : "")}.");
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BookOnce] register {id}: {ex.Message}"); return false; }
        }

        private static void MarkBooked(string id, Rec r, string source, Order? o)
        {
            r.Booked = true; r.Source = source; r.BookedOrder = o;
            Bump(_bookedBy, source);
            if (IsPassSource(source)) PassBooked++;   // H-HOURROLL rig counter
            Plugin.Logger.LogInfo($"[BookOnce] {id} booked by {source}{(o != null ? $" (${PaidTotal(o):F2} paid)" : "")}.");
            // Fold M1: an exit snapshot kept paid for this visit - the booking carries those lines now.
            if (r.ExitSnap != null && !ReferenceEquals(r.ExitSnap, o)) UnpaySnap(id, r, source, o);
            // Fold H2(a): a live body of this visit on this machine with another open Order is finished (next Tick).
            if (_finish.Count < 200) _finish.Add(new KeyValuePair<string, Order?>(id, o));
        }

        /// <summary>What the day roll pays for: BusinessHelper.CreateDailyOrderHistory (:234-261) takes an Order only when
        /// it is completed and holds a paid entry.</summary>
        private static bool HasPaid(Order? o)
        {
            try
            {
                if (o == null || !o.completed || o.entries == null) return false;
                foreach (var e in o.entries) if (e != null && e.paid) return true;
            }
            catch { }
            return false;
        }

        private static float PaidTotal(Order o)
        {
            float s = 0f;
            try { if (o.entries != null) foreach (var e in o.entries) if (e != null && e.paid) s += e.price; } catch { }
            return s;
        }

        /// <summary>Folds M1/M2 (2026-09-27): a till add of a registered, UNBOOKED visit that is no booking - an unpaid one
        /// (the day roll pays nothing for it) or any add made inside the native exit clean-up (Customer.ForceFinishOrder
        /// :363-371 completes the live order with whatever it holds; rig run T-HANDOFFSEAT-20260927-011125 'booked'
        /// Client1-170 at $0 and suppressed the partner's real sale). It STAYS in the till (M2: pricing satisfaction,
        /// the hourly quota and the security count read it) as a frozen snapshot, so the visit's own Order - which the
        /// visit goes on with (an adoption writes the partner's row into it in place) - never sits in the till twice.
        /// A paid exit snapshot is remembered: its paid lines count through it until a later booking of the visit
        /// carries them (MarkBooked -> UnpaySnap).</summary>
        private static void KeepInTill(BuildingRegistration reg, Order o, string id, Rec r, string funnel, bool paidExit, int index = -1)
        {
            try
            {
                var till = reg.unprocessedCompletedOrders;
                if (till == null) return;
                if (index < 0 || index >= till.Count || !ReferenceEquals(till[index], o))
                {
                    index = -1;
                    for (int i = till.Count - 1; i >= 0; i--) if (ReferenceEquals(till[i], o)) { index = i; break; }
                }
                if (index < 0) return;
                var snap = Snapshot(o);
                till[index] = snap;
                // Fold S1 (2026-09-27): the snapshot is this visit's - a later forward's till credit (CustomerHandoff.TillTakenOf)
                // finds its taken lines (rig run T-HANDOFFSEAT-20260927-032513, Client1-177: the unmapped snapshot hid them
                // and the forward took the same units off a shelf a second time). Examine never books or suppresses it.
                MapOrder(snap, id);
                Bump(_keptBy, funnel);
                if (paidExit)
                {
                    if (r.ExitSnap != null) UnpaySnap(id, r, "a newer exit snapshot of the same visit", null);
                    r.ExitSnap = snap;
                    ExitKept++;
                    Plugin.Logger.LogInfo($"[BookOnce] {id} exit clean-up ({funnel}) completed a partly paid order - no booking: kept in the till as a snapshot (${PaidTotal(snap):F2} paid on it counts there until a later booking of this visit carries it).");
                }
                else
                {
                    UnpaidKept++;
                    Plugin.Logger.LogInfo($"[BookOnce] {id} unpaid till add ({funnel}) is no booking - kept in the till as a snapshot, the visit stays unbooked.");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BookOnce] keep {id}: {ex.Message}"); }
        }

        private static readonly System.Reflection.MethodInfo? _clone =
            typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>A frozen copy of an Order (every field; its own copies of the entries).</summary>
        private static Order Snapshot(Order o)
        {
            Order? c = null;
            try { c = _clone?.Invoke(o, null) as Order; } catch { }
            c ??= new Order { completed = o.completed, timestamp = o.timestamp, cleanliness = o.cleanliness };
            var l = new List<OrderEntry>();
            if (o.entries != null)
                foreach (var e in o.entries)
                {
                    if (e == null) continue;
                    OrderEntry? ec = null;
                    try { ec = _clone?.Invoke(e, null) as OrderEntry; } catch { }
                    if (ec != null && !ReferenceEquals(ec, e))
                    {
                        try { _snapLineOf.Remove(ec); _snapLineOf.Add(ec, e); } catch { }
                        CustomerHandoff.CopyLineMarks(e, ec);   // fold S1: frozen with the line (picked / settled / credited)
                    }
                    l.Add(ec ?? e);
                }
            c.entries = l;
            try { _snapOf.Remove(c); _snapOf.Add(c, o); } catch { }
            return c;
        }

        /// <summary>Fold M1: the visit's kept exit snapshot stops counting its paid lines - the booking that just
        /// happened carries them (a forward holds every paid line of the partner's order, the row the lines paid).</summary>
        private static void UnpaySnap(string id, Rec r, string why, Order? booking)
        {
            try
            {
                var s = r.ExitSnap;
                r.ExitSnap = null;
                if (s == null) return;
                float amt = PaidTotal(s);
                bool inTill = r.Reg != null && InTill(r.Reg, s);
                int paidLines = 0, moved = 0;
                if (!inTill && s.entries != null)
                {
                    // Fold S3 (2026-09-27): a day roll already paid the snapshot (ProcessDailyOrders pays it and empties the
                    // till; a gym / nightclub / hairdresser / full-service customer normally leaves through ForceFinishOrder).
                    // The booking must not pay those lines again: per paid line the snapshot carried, one matching paid line
                    // of the booking is unpaid here - before the booking reaches the till.
                    var used = new HashSet<OrderEntry>(new TillDupes.RefEq<OrderEntry>());
                    foreach (var se in s.entries)
                    {
                        if (se == null || !se.paid) continue;
                        paidLines++;
                        if (booking?.entries == null) continue;
                        foreach (var be in booking.entries)
                        {
                            if (be == null || !be.paid || used.Contains(be) || !SameItem(be.itemName, se.itemName)) continue;
                            be.paid = false;
                            used.Add(be);
                            moved++;
                            CustomerHandoff.MarkCreditedLine(be);   // its unit is sold by the snapshot - never a lost one
                            break;
                        }
                    }
                }
                if (s.entries != null) foreach (var e in s.entries) if (e != null) e.paid = false;
                ExitUnpaid++;
                Plugin.Logger.LogInfo(inTill
                    ? $"[BookOnce] {id} exit snapshot unpaid (${amt:F2}) - {why} carries the whole visit; the snapshot stays in the till unpaid."
                    : $"[BookOnce] {id} exit snapshot (${amt:F2}) had already left the till (a day roll paid it) - {why}: {moved} of {paidLines} matching paid line(s) of the booking unpaid, so each is counted once{(moved < paidLines ? $"; {paidLines - moved} found no match and may count twice" : "")}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BookOnce] unpay {id}: {ex.Message}"); }
        }

        private static bool SameItem(string? a, string? b)
            => a == b || (CustomerHandoff.IsPaperBag(a) && CustomerHandoff.IsPaperBag(b));

        private static void NoteSuppressed(string id, Rec r, string funnel)
        {
            Suppressed++;
            Bump(_suppressedBy, funnel);
            Plugin.Logger.LogInfo($"[BookOnce] {id} already booked by {r.Source} - {funnel} suppressed");
        }

        /// <summary>One booking of a registered visit reaches this machine through <paramref name="funnel"/>. True = it
        /// books (first, or the visit is not registered at all); false = suppressed (already booked).</summary>
        internal static bool TryBook(string id, string funnel, Order? o)
        {
            try
            {
                if (string.IsNullOrEmpty(id) || !_recs.TryGetValue(id, out var r)) return true;
                if (!r.Booked) { MarkBooked(id, r, funnel, o); return true; }
                if (funnel == "forwarded sale" && IsPassSource(r.Source)) PassPreempt++;   // H-HOURROLL rig counter (must stay 0)
                NoteSuppressed(id, r, funnel);
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BookOnce] book {id}: {ex.Message}"); return true; }
        }

        /// <summary>A booking that already happened outside the till watcher (a forward with nothing coverable).</summary>
        internal static void NoteBooked(string id, string source, Order? o)
        {
            try { if (!string.IsNullOrEmpty(id) && _recs.TryGetValue(id, out var r) && !r.Booked) MarkBooked(id, r, source, o); } catch { }
        }

        // ── The till watcher ─────────────────────────────────────────────────────────────────────────────
        internal static void NotePaid(Order? o)
        {
            try { if (o != null && _recs.Count > 0) { _paidHere.Remove(o); _paidHere.Add(o, _mark); } } catch { }
        }

        internal static void FeeBegin() { _inFee = true; }
        internal static void FeeEnd() { _inFee = false; }
        /// <summary>Fold S6 (2026-09-27): remembers WHICH Order the exit clean-up finishes - only that Order's till add is an
        /// exit add. Returns the one it replaces (a nested clean-up) for ExitEnd.</summary>
        internal static Order? ExitBegin(Order? o)
        {
            var prev = _exitOrder;
            _exitOrder = o;
            try { if (o != null) { _exitOrders.Remove(o); _exitOrders.Add(o, _mark); } } catch { }
            return prev;
        }
        internal static void ExitEnd(Order? prev) { _exitOrder = prev; }

        /// <summary>Once per frame (CustomerPuppets.Tick) and right after the patched till-adding methods: every Order
        /// occurrence appended to the till of the building this machine stands in since the last look. Fold M3: tracked
        /// by reference (occurrence counts per Order), so an Order removed below the old end plus one appended in the
        /// same frame is still examined.</summary>
        internal static void Scan(string? funnel)
        {
            try
            {
                var bm = InstanceBehavior<BuildingManager>.Instance;
                var reg = bm != null ? bm.buildingRegistration : null;
                if (reg == null) { _watchReg = null; _watchCount = -1; _watchLast = null; _seen.Clear(); return; }
                var till = reg.unprocessedCompletedOrders;
                if (till == null) return;
                if (!ReferenceEquals(reg, _watchReg) || _watchCount < 0) { _watchReg = reg; Recount(till); return; }
                Order? last = till.Count > 0 ? till[till.Count - 1] : null;
                if (till.Count == _watchCount && ReferenceEquals(last, _watchLast)) return;
                if (_recs.Count == 0 || !MergerFlip.BooksHere(reg)) { Recount(till); return; }
                var cur = ReferenceEquals(_seen, _seenA) ? _seenB : _seenA;
                cur.Clear();
                for (int i = 0; i < till.Count; i++)
                {
                    var o = till[i];
                    if (o == null) continue;
                    cur.TryGetValue(o, out var k); k++;
                    _seen.TryGetValue(o, out var had);
                    if (k > had)
                    {
                        int act = Examine(reg, till, i, o, funnel);
                        if (act == 1) { i--; continue; }            // removed (suppressed)
                        if (act == 2)                               // replaced by a snapshot
                        {
                            var s = till[i];
                            if (s != null) { cur.TryGetValue(s, out var ks); cur[s] = ks + 1; }
                            continue;
                        }
                    }
                    cur[o] = k;
                }
                _seen = cur;
                _watchCount = till.Count;
                _watchLast = till.Count > 0 ? till[till.Count - 1] : null;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BookOnce] till watch: {ex.Message}"); }
        }

        private static void Recount(List<Order> till)
        {
            _seen.Clear();
            foreach (var o in till)
            {
                if (o == null) continue;
                _seen.TryGetValue(o, out var n); _seen[o] = n + 1;
            }
            _watchCount = till.Count;
            _watchLast = till.Count > 0 ? till[till.Count - 1] : null;
        }

        /// <summary>One new till occurrence. 0 = stays, 1 = removed, 2 = replaced by a snapshot (same index).</summary>
        private static int Examine(BuildingRegistration reg, List<Order> till, int i, Order o, string? funnel)
        {
            if (IsSnapshot(o)) return 0;   // fold S1: a kept snapshot (mapped to its visit) is never a booking
            string? id = IdOfOrder(o);
            if (id == null || !_recs.TryGetValue(id, out var r)) return 0;
            bool exit = _exitOrder != null && ReferenceEquals(o, _exitOrder);   // fold S6: only the Order being finished
            string fn = funnel ?? (_inFee ? "gym entrance fee (PayEntranceFee -> CompleteOrder)"
                                : _paidHere.TryGetValue(o, out _) ? "employee checkout (FullServiceEmployee after Order.Pay)"
                                : "till add (ticket kiosk / other)");
            if (funnel != null && _inFee) fn = "gym entrance fee (PayEntranceFee -> CompleteOrder)";
            if (exit) fn = "exit clean-up (ForceFinishOrder -> CompleteOrder)";
            if (!r.Booked)
            {
                bool paid = HasPaid(o);
                if (exit || !paid) { KeepInTill(reg, o, id, r, fn, exit && paid, i); return ReferenceEquals(till[i], o) ? 0 : 2; }
                MarkBooked(id, r, fn, o);
                return 0;
            }
            // The booking's own reference (an hourly pass or a first checkout seen here): it stays - unless the same
            // Order already sits in the till (a second reference is a second payment).
            if (ReferenceEquals(r.BookedOrder, o))
            {
                bool other = false;
                for (int j = 0; j < till.Count; j++) if (j != i && ReferenceEquals(till[j], o)) { other = true; break; }
                if (!other) return 0;
            }
            NoteSuppressed(id, r, fn);
            till.RemoveAt(i);
            return 1;
        }

        internal static void Tick()
        {
            try { Scan(null); } catch { }
            if (_finish.Count == 0) return;
            try
            {
                var l = _finish.ToArray();
                _finish.Clear();
                foreach (var kv in l) Finished += CustomerPuppets.FinishLiveBodiesOf(kv.Key, kv.Value);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BookOnce] finish: {ex.Message}"); }
        }

        // ── Hourly passes (native SimulateBusiness and the MP skip pass) ───────────────────────────────────
        internal sealed class Pass
        {
            public BuildingRegistration Reg = null!;
            public List<CustomerEntry> Entries = null!;
            public int Hour;
            public string Name = "";
            public readonly List<KeyValuePair<int, CustomerEntry>> Aside = new();
            public readonly List<KeyValuePair<CustomerEntry, string>> Open = new();
            public int PartnerAside;   // H-HOURROLL: of Aside, the entries set aside for a partner's live customers
            public int ForwardAside;   // rotation fix B: of Aside, entries a partner's sale forward already settled
        }

        /// <summary>Before an hourly pass of <paramref name="reg"/> for <paramref name="hour"/>: a Booked registered entry
        /// of that hour is set aside (suppressed), an Unbooked one is remembered. Null = nothing to do.</summary>
        internal static Pass? HourlyBegin(BuildingRegistration? reg, int hour, string pass)
        {
            try
            {
                if (reg == null || !MergerFlip.BooksHere(reg)) return null;
                // H-HOURROLL (2026-09-28): a partner runs this shop's live customers right now - the whole hour is theirs.
                // H2 (review of 480184b): decided at the hour's END (RecordHourEnd), not by a live read now.
                string simPid = "", liveRule = "";
                bool live = false;
                try { live = HourEndSaysPartner(reg, hour, out simPid, out liveRule); } catch { live = false; }
                if (!live && _recs.Count == 0 && _ended.Count == 0 && _ending.Count == 0 && !CustomerEntrySync.AnyForwardKept) return null;
                string fn = $"{pass} hourly pass h{hour}";
                ExpireEnding(reg, hour, fn);   // R2: an 'ending' mark lives through one hour boundary after the one it was marked in
                var entries = CustomerEntrySync.EntriesOf(reg);
                if (entries == null) return null;
                var p = new Pass { Reg = reg, Entries = entries, Hour = hour, Name = pass };
                int partnerAside = 0, notSent = 0;
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    var e = entries[i];
                    if (e?.spawnTime == null || e.spawnTime.Hour != hour) continue;
                    if (live)
                    {
                        // H1 (review of 480184b, 2026-09-28): only an entry this owner SENT to partners can sit on the
                        // partner's schedule with this owner's id, so only its sale can ever reach these books (the
                        // forward drops a sale with no id). Anything else - regenerated at the partner's own midnight
                        // (BusinessHelper.cs:173 -> CustomerEntriesHelper.cs:28), a stale schedule the partner never got
                        // re-pushed, past the 300-entry wire cap - is billed by this pass as before.
                        // H4 (accepted, design risk 4): a late spawn on the partner rewrites only the PARTNER's copy of
                        // the entry to the spawn moment (IndoorCustomerSpawner.cs:198-199), which can be the next hour;
                        // this copy keeps its hour, so the visit is set aside at THIS hour's end and no later pass here
                        // sees it - it is billed by its own sale forward, its walk-out report or not at all.
                        if (CustomerEntrySync.WasSent(reg, e))
                        {
                            // Not billed here: each visit is billed by its own sale forward, its walk-out report, or not at all.
                            p.Aside.Add(new KeyValuePair<int, CustomerEntry>(i, e));
                            entries.RemoveAt(i);
                            partnerAside++;
                            continue;
                        }
                        notSent++;   // falls through: stays in the pass's list and is billed by it
                    }
                    // Rotation fix B (2026-09-28): an entry a partner's sale forward claimed stays in the list, completed
                    // (single-player keeps a served entry); that forward settled the visit, so no pass bills it.
                    if (CustomerEntrySync.ForwardKept(e))
                    {
                        p.Aside.Add(new KeyValuePair<int, CustomerEntry>(i, e));
                        entries.RemoveAt(i);
                        p.ForwardAside++;
                        continue;
                    }
                    string? id = IdOfOrder(e.order);
                    if (id == null)
                    {
                        // Fold M4: an entry whose Order was never mapped - its id from the schedule's own id map.
                        id = CustomerEntrySync.KnownIdOf(e);
                        if (id != null && e.order != null && _recs.ContainsKey(id)) MapOrder(e.order, id);
                    }
                    if (id == null) continue;
                    _recs.TryGetValue(id, out var r);
                    bool isEnded = _ended.ContainsKey(id), isEnding = !isEnded && _ending.ContainsKey(id);
                    bool ended = (isEnded || isEnding) && (r == null || !r.Booked);   // H2 / R2: a visit that left (or is leaving) unsold
                    if (r == null && !ended)
                    {
                        if (CustomerEntrySync.ForwardBooked(id))
                        {
                            p.Aside.Add(new KeyValuePair<int, CustomerEntry>(i, e));   // rotation fix B: forward-booked, never billed here
                            entries.RemoveAt(i);
                            p.ForwardAside++;
                        }
                        continue;
                    }
                    if (ended || (r != null && r.Booked))
                    {
                        if (ended) Plugin.Logger.LogInfo(isEnding
                            ? $"[BookOnce] {id} ending (walking out on a partner's machine) - set aside by the {fn}; a sale forward can still book it."
                            : $"[BookOnce] {id} ended unsold - kept out of the hourly pass ({fn}).");
                        else if (r != null) NoteSuppressed(id, r, fn);
                        p.Aside.Add(new KeyValuePair<int, CustomerEntry>(i, e));
                        entries.RemoveAt(i);
                    }
                    else p.Open.Add(new KeyValuePair<CustomerEntry, string>(e, id));
                }
                p.Aside.Reverse();   // descending index order -> ascending, for the re-insert
                if (live)
                {
                    p.PartnerAside = partnerAside;
                    PartnerLiveAside += partnerAside;
                    NotSentBilled += notSent;
                    Plugin.Logger.LogInfo($"[BookOnce] {GameStateReader.AddressKey(reg)} h{hour}: {partnerAside} entr{(partnerAside == 1 ? "y" : "ies")} set aside - {simPid}'s machine runs this shop's live customers ({fn}; decided at the hour's end: {liveRule}; single-player parity BusinessSimulatorHelper.cs:32 - each visit is billed by its own sale forward, its walk-out report or not at all; restock and stock tasks still run); {notSent} billed by this pass because never sent to a partner.");
                }
                if (p.Aside.Count == 0 && p.Open.Count == 0) return null;
                return p;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BookOnce] hourly begin: {ex.Message}"); return null; }
        }

        /// <summary>After that pass (always - a finally / finalizer): the set-aside entries go back where they were, and
        /// every Unbooked entry whose Order the pass put in the till is booked by the pass.</summary>
        internal static void HourlyEnd(Pass? p)
        {
            if (p == null) return;
            try
            {
                for (int i = 0; i < p.Aside.Count; i++)
                {
                    int at = p.Aside[i].Key;
                    if (at > p.Entries.Count) at = p.Entries.Count;
                    p.Entries.Insert(at, p.Aside[i].Value);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BookOnce] hourly restore: {ex.Message}"); }
            try
            {
                int booked = 0;
                foreach (var kv in p.Open)
                {
                    var o = kv.Key?.order;
                    if (o == null || !_recs.TryGetValue(kv.Value, out var r) || r.Booked) continue;
                    if (!InTill(p.Reg, o)) continue;
                    if (HasPaid(o)) { MarkBooked(kv.Value, r, $"{p.Name} hourly pass h{p.Hour}", o); booked++; }
                    else KeepInTill(p.Reg, o, kv.Value, r, $"{p.Name} hourly pass h{p.Hour}", false);
                }
                int regN = p.Open.Count + p.Aside.Count - p.PartnerAside - p.ForwardAside;
                Plugin.Logger.LogInfo($"[BookOnce] hourly pass ({p.Name}) @{GameStateReader.AddressKey(p.Reg)} h{p.Hour}: {regN} registered entr{(regN == 1 ? "y" : "ies")} of this hour, {booked} booked here, {p.Aside.Count - p.PartnerAside - p.ForwardAside} already booked (set aside){(p.PartnerAside > 0 ? $", {p.PartnerAside} set aside for a partner's live customers" : "")}{(p.ForwardAside > 0 ? $", {p.ForwardAside} settled by a partner's sale forward (set aside)" : "")}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BookOnce] hourly end: {ex.Message}"); }
        }

        // ── Readout (DEV lever `handoffbook`, custstate) ────────────────────────────────────────────────────
        private static string Tally(Dictionary<string, int> d)
        {
            if (d.Count == 0) return "-";
            var l = new List<string>();
            foreach (var kv in d) l.Add($"{kv.Key.Replace(' ', '_')}={kv.Value}");
            return string.Join(",", l);
        }

        /// <summary>R2: at an hourly pass of <paramref name="reg"/> for <paramref name="hour"/>, an 'ending' mark made in an
        /// EARLIER game hour (this is at least its second boundary) is cleared - booked ones silently, the rest logged.</summary>
        private static void ExpireEnding(BuildingRegistration reg, int hour, string fn)
        {
            try
            {
                if (_ending.Count == 0) return;
                string addr = GameStateReader.AddressKey(reg);
                int nowAbs = NowAbsHour();
                if (nowAbs < 0) return;
                int passAbs = nowAbs - (nowAbs % 24) + hour;
                if (hour > nowAbs % 24) passAbs -= 24;   // the pass is for an hour of the previous day
                var drop = new List<string>();
                foreach (var kv in _ending)
                {
                    if (kv.Value.Addr != addr) continue;
                    if (IsBooked(kv.Key)) { drop.Add(kv.Key); continue; }
                    if (kv.Value.MarkAbs >= 0 && passAbs > kv.Value.MarkAbs) drop.Add(kv.Key);
                }
                foreach (var id in drop)
                {
                    var en = _ending[id];
                    _ending.Remove(id);
                    if (IsBooked(id)) continue;
                    EndingCleared++;
                    Plugin.Logger.LogInfo($"[BookOnce] {id} ending mark cleared at the {fn}: no release report or sale from {en.From} since it was marked (h{(en.MarkAbs % 24 + 24) % 24}) - the entry is back in the normal flow.");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BookOnce] expire ending: {ex.Message}"); }
        }

        /// <summary>H2 readout: visits ended unsold at this address, and how many of them got booked all the same (must be 0).</summary>
        private static string EndedReadout(string addr)
        {
            int n = 0, booked = 0;
            try
            {
                foreach (var kv in _ended)
                {
                    if (!string.IsNullOrEmpty(addr) && kv.Value != addr) continue;
                    n++;
                    if (IsBooked(kv.Key)) booked++;
                }
            }
            catch { }
            int ending = 0;
            try { foreach (var kv in _ending) if (string.IsNullOrEmpty(addr) || kv.Value.Addr == addr) ending++; } catch { }
            return $"boEnded={n} boEndedBooked={booked} boEnding={ending} boEndingMarked={EndingMarked} boEndingCleared={EndingCleared}";
        }

        internal static string Readout(string addr)
        {
            try
            {
                int reg = 0, booked = 0;
                foreach (var kv in _recs)
                {
                    if (!string.IsNullOrEmpty(addr) && kv.Value.Addr != addr) continue;
                    reg++;
                    if (kv.Value.Booked) booked++;
                }
                return $"boRegistered={reg} boBooked={booked} boUnbooked={reg - booked} boSuppressed={Suppressed} "
                     + $"boRegisteredBy={Tally(_registeredBy)} boBookedBy={Tally(_bookedBy)} boSuppressedBy={Tally(_suppressedBy)} "
                     + $"boUnpaidKept={UnpaidKept} boExitKept={ExitKept} boExitUnpaid={ExitUnpaid} boKeptBy={Tally(_keptBy)} "
                     + $"boFinished={Finished} boCopyReturnsBlocked={CopyReturnsBlocked} {EndedReadout(addr)} "
                     + $"boPassBooked={PassBooked} boPartnerAside={PartnerLiveAside} boNotSentBilled={NotSentBilled} boSilentHours={SilentHours}";
            }
            catch (Exception ex) { return "boERR " + ex.Message; }
        }
    }

    // ── Patches ──────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>BookOnce funnel: the customer's own till add (Customer.CompleteOrder :385-397).</summary>
    [HarmonyPatch(typeof(Customer), nameof(Customer.CompleteOrder))]
    public static class Patch_Customer_CompleteOrder_BookOnce
    {
        static void Postfix()
        {
            try { if (MPServer.IsRunning || MPClient.IsConnected) BookOnce.Scan("customer till add (Customer.CompleteOrder)"); } catch { }
        }
    }

    /// <summary>BookOnce funnel: the self-service employee's till add (SelfServiceEmployee.TellCurrentCustomerToLeave :141-156).</summary>
    [HarmonyPatch(typeof(SelfServiceEmployee), "TellCurrentCustomerToLeave")]
    public static class Patch_SelfServiceEmployee_TellLeave_BookOnce
    {
        static void Postfix()
        {
            try { if (MPServer.IsRunning || MPClient.IsConnected) BookOnce.Scan("self-service employee checkout"); } catch { }
        }
    }

    /// <summary>BookOnce: names the full-service checkout funnel (Order.Pay precedes FullServiceEmployee's till add :156).</summary>
    [HarmonyPatch(typeof(Order), nameof(Order.Pay))]
    public static class Patch_Order_Pay_BookOnce
    {
        static void Postfix(Order __instance, bool isPlayer, bool __result)
        {
            try { if (__result && !isPlayer && (MPServer.IsRunning || MPClient.IsConnected)) BookOnce.NotePaid(__instance); } catch { }
        }
    }

    /// <summary>BookOnce: names the gym entrance-fee funnel (PayEntranceFee -> CompleteOrder).</summary>
    [HarmonyPatch(typeof(Customer), nameof(Customer.PayEntranceFee))]
    public static class Patch_Customer_PayEntranceFee_BookOnce
    {
        [HarmonyPriority(Priority.First)]
        static void Prefix() { BookOnce.FeeBegin(); }
        static void Finalizer() { BookOnce.FeeEnd(); }
    }

    /// <summary>Fold M1: flags every till add made inside the native exit clean-up (Customer.ForceFinishOrder :363-371 -
    /// OnExitBuilding completes each live order with whatever it holds): never a booking for a registered visit.</summary>
    [HarmonyPatch(typeof(Customer), "ForceFinishOrder")]
    public static class Patch_Customer_ForceFinishOrder_BookOnce
    {
        [HarmonyPriority(Priority.First)]
        static void Prefix(Customer __instance, out Order? __state)
        {
            __state = null;
            try { __state = BookOnce.ExitBegin(__instance != null ? __instance.order : null); } catch { }
        }
        static void Finalizer(Order? __state) { BookOnce.ExitEnd(__state); }
    }

    /// <summary>Fold H2(b): a booked adopted body's detached copy holds the partner's picked items - Customer.Leave ->
    /// ReturnItemsToShelf (Customer.cs:307-313) must never put them onto this machine's shelves.</summary>
    [HarmonyPatch(typeof(Customer), nameof(Customer.ReturnItemsToShelf))]
    public static class Patch_Customer_ReturnItemsToShelf_BookOnce
    {
        static bool Prefix(Customer __instance)
        {
            try
            {
                if (__instance != null && BookOnce.IsBookedCopy(__instance.order))
                {
                    BookOnce.NoteCopyReturnBlocked();
                    return false;
                }
            }
            catch { }
            return true;
        }
    }

    /// <summary>H-HOURROLL H2 (review of 480184b, 2026-09-28): the game's hourly decision point (BusinessSimulatorHelper
    /// .RunHourly :19-32 decides right here which shops are simulated and skips the occupied one) - record, per shop that
    /// books here, whether a partner runs its live customers at the moment the hour ends. The passes it queues read it.</summary>
    [HarmonyPatch(typeof(BusinessSimulatorHelper), nameof(BusinessSimulatorHelper.RunHourly))]
    public static class Patch_RunHourly_BookOnceHourEnd
    {
        [HarmonyPriority(Priority.First)]
        static void Prefix()
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                BookOnce.RecordHourEnd();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BookOnce] hour-end record: {ex.Message}"); }
        }
    }
}
