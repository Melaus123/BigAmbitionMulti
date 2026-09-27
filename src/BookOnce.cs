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
    /// An UNPAID till add (nothing the day roll would pay - the native exit clean-up ForceFinishOrder, a refused fee) never
    /// books a registered visit: it is taken out of the till again and the visit stays unbooked (DropUnpaid).
    /// The till watcher: every Order appended to the till of the building this machine stands in is looked up
    /// (registered entry Order, or a detached body copy); an Unbooked visit is booked by it, a Booked one is removed
    /// again at once with one '[BookOnce] &lt;id&gt; already booked by &lt;source&gt; - &lt;funnel&gt; suppressed' line.
    /// NOT GATED (physical stock a live body moves BEFORE its till add): the full-service paper bag
    /// (FullServiceEmployee :95 SubtractFromStock) and the items the employee / the customer takes off the shelves.</summary>
    internal static class BookOnce
    {
        private sealed class Rec
        {
            public string Addr = "";
            public int RegisteredMin;
            public bool Booked;
            public string Source = "";
            public Order? BookedOrder;
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
        internal static int Suppressed;

        internal static void Reset()
        {
            try
            {
                _recs.Clear();
                _orderIds = new ConditionalWeakTable<Order, string>();
                _paidHere = new ConditionalWeakTable<Order, object>();
                _registeredBy.Clear(); _bookedBy.Clear(); _suppressedBy.Clear(); _unpaidBy.Clear(); UnpaidDropped = 0;
                _watchReg = null; _watchCount = -1; _nextPruneMin = -1; _inFee = false; Suppressed = 0;
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
                var r = new Rec { Addr = GameStateReader.AddressKey(reg), RegisteredMin = NowMin() };
                if (CustomerEntrySync.ForwardBooked(id)) { r.Booked = true; r.Source = "forwarded sale (before registration)"; }
                else if (entry?.order != null && InTill(reg, entry.order))
                {
                    if (HasPaid(entry.order)) { r.Booked = true; r.Source = "till (before registration)"; r.BookedOrder = entry.order; }
                    else DropUnpaid(reg, entry.order, id, "till (before registration)");
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
            Plugin.Logger.LogInfo($"[BookOnce] {id} booked by {source}{(o != null ? $" (${PaidTotal(o):F2} paid)" : "")}.");
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

        /// <summary>An UNPAID till add of a registered, unbooked visit is no booking: the native exit clean-up
        /// (Customer.OnExitBuilding -> ForceFinishOrder :366-373 -> CompleteOrder) completes every live customer's order
        /// with nothing paid while that visit goes on on the partner's machine (rig run T-HANDOFFSEAT-20260927-011125:
        /// it 'booked' Client1-170 at $0 and the partner's real sale was suppressed). The day roll pays nothing for it,
        /// so it is taken out of the till again (money-neutral) and the visit stays unbooked.</summary>
        internal static int UnpaidDropped;
        private static readonly Dictionary<string, int> _unpaidBy = new();
        private static void DropUnpaid(BuildingRegistration reg, Order o, string id, string funnel, int index = -1)
        {
            try
            {
                var till = reg.unprocessedCompletedOrders;
                int n = 0;
                if (till != null && index >= 0 && index < till.Count && ReferenceEquals(till[index], o)) { till.RemoveAt(index); n++; }
                else if (till != null && index < 0) for (int i = till.Count - 1; i >= 0; i--) if (ReferenceEquals(till[i], o)) { till.RemoveAt(i); n++; }
                if (n == 0) return;
                UnpaidDropped++;
                Bump(_unpaidBy, funnel);
                Plugin.Logger.LogInfo($"[BookOnce] {id} unpaid till add ({funnel}) is no booking - removed, the visit stays unbooked.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BookOnce] drop unpaid {id}: {ex.Message}"); }
        }

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

        /// <summary>Once per frame (CustomerPuppets.Tick) and right after the patched till-adding methods: every Order
        /// appended to the till of the building this machine stands in since the last look.</summary>
        internal static void Scan(string? funnel)
        {
            try
            {
                var bm = InstanceBehavior<BuildingManager>.Instance;
                var reg = bm != null ? bm.buildingRegistration : null;
                if (reg == null) { _watchReg = null; _watchCount = -1; return; }
                var till = reg.unprocessedCompletedOrders;
                if (till == null) return;
                if (!ReferenceEquals(reg, _watchReg) || _watchCount < 0 || till.Count < _watchCount)
                {
                    _watchReg = reg; _watchCount = till.Count; return;
                }
                if (till.Count == _watchCount) return;
                if (_recs.Count == 0 || !MergerFlip.BooksHere(reg)) { _watchCount = till.Count; return; }
                for (int i = _watchCount; i < till.Count; i++)
                {
                    var o = till[i];
                    string? id = IdOfOrder(o);
                    if (id == null || !_recs.TryGetValue(id, out var r)) continue;
                    string fn = funnel ?? (_inFee ? "gym entrance fee (PayEntranceFee -> CompleteOrder)"
                                        : (o != null && _paidHere.TryGetValue(o, out _)) ? "employee checkout (FullServiceEmployee after Order.Pay)"
                                        : "till add (ticket kiosk / other)");
                    if (funnel != null && _inFee) fn = "gym entrance fee (PayEntranceFee -> CompleteOrder)";
                    if (!r.Booked)
                    {
                        if (o != null && !HasPaid(o))
                        {
                            DropUnpaid(reg, o, id, fn, i);
                            i--;
                            continue;
                        }
                        MarkBooked(id, r, fn, o); continue;
                    }
                    // The booking's own reference (an hourly pass or a first checkout seen here): it stays - unless
                    // the same Order already sits in the till (a second reference is a second payment).
                    if (o != null && ReferenceEquals(r.BookedOrder, o))
                    {
                        bool other = false;
                        for (int j = 0; j < till.Count; j++) if (j != i && ReferenceEquals(till[j], o)) { other = true; break; }
                        if (!other) continue;
                    }
                    NoteSuppressed(id, r, fn);
                    till.RemoveAt(i); i--;
                }
                _watchCount = till.Count;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BookOnce] till watch: {ex.Message}"); }
        }

        internal static void Tick()
        {
            try { Scan(null); } catch { }
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
        }

        /// <summary>Before an hourly pass of <paramref name="reg"/> for <paramref name="hour"/>: a Booked registered entry
        /// of that hour is set aside (suppressed), an Unbooked one is remembered. Null = nothing to do.</summary>
        internal static Pass? HourlyBegin(BuildingRegistration? reg, int hour, string pass)
        {
            try
            {
                if (reg == null || _recs.Count == 0 || !MergerFlip.BooksHere(reg)) return null;
                var entries = CustomerEntrySync.EntriesOf(reg);
                if (entries == null) return null;
                var p = new Pass { Reg = reg, Entries = entries, Hour = hour, Name = pass };
                string fn = $"{pass} hourly pass h{hour}";
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    var e = entries[i];
                    if (e?.spawnTime == null || e.spawnTime.Hour != hour) continue;
                    string? id = IdOfOrder(e.order);
                    if (id == null || !_recs.TryGetValue(id, out var r)) continue;
                    if (r.Booked)
                    {
                        NoteSuppressed(id, r, fn);
                        p.Aside.Add(new KeyValuePair<int, CustomerEntry>(i, e));
                        entries.RemoveAt(i);
                    }
                    else p.Open.Add(new KeyValuePair<CustomerEntry, string>(e, id));
                }
                p.Aside.Reverse();   // descending index order -> ascending, for the re-insert
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
                    else DropUnpaid(p.Reg, o, kv.Value, $"{p.Name} hourly pass h{p.Hour}");
                }
                Plugin.Logger.LogInfo($"[BookOnce] hourly pass ({p.Name}) @{GameStateReader.AddressKey(p.Reg)} h{p.Hour}: {p.Open.Count + p.Aside.Count} registered entr{(p.Open.Count + p.Aside.Count == 1 ? "y" : "ies")} of this hour, {booked} booked here, {p.Aside.Count} already booked (set aside).");
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
                     + $"boUnpaidDropped={UnpaidDropped} boUnpaidBy={Tally(_unpaidBy)}";
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
}
