using System;
using System.Collections.Generic;
using Buildings;
using Entities;
using Helpers;
using HarmonyLib;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>H-HANDOFF-1 (batch 27, 2026-09-26) - CUSTOMERS CONTINUE ACROSS A HAND-OFF.
    ///
    /// When the machine running a shop's live customers (the "simulator", CustomerPuppets) gives them up -
    /// its player walks out, or authority moves to the booking player who just walked in - the machine that
    /// takes over used to re-spawn every body from its own copy of the schedule: a fresh random citizen, the
    /// entrance fee appended and paid again, the arrival complaint again, and the visit clock read from the
    /// LOCAL entry, so a shopper half-way round the shop restarted as a new arrival with an empty basket.
    ///
    /// This class carries the VISIT STATE across (message CustomerVisitState, 219):
    ///   - the giving machine sends one FINAL snapshot of every live customer at the moment it lets go
    ///     (reason "exit" from a prefix on BuildingManager.ResetIndoors, reason "authority" from the swap in
    ///     CustomerPuppets.ReactToAuthority), taken BEFORE the bodies are released;
    ///   - while simulating with another player inside, it also streams rows ON CHANGE, at most once a second
    ///     (a full set every 10 s), so a simulator that DISCONNECTS still leaves the taker recent state;
    ///   - the taker keeps the newest row per id (a Final wins over a stream row from the same sender) and
    ///     CustomerPuppets.AdoptPuppetAsNative applies it: the visit clock, the order's items with their
    ///     done/paid flags (in place), the completed flag, the citizen, and a walk-out for a body that was
    ///     already leaving. While one adoption runs (AdoptScope) the entrance fee, the gym's PayEntranceFee
    ///     and the arrival complaint are suppressed once, because the source machine already did them.
    ///
    /// Part B (2026-09-27, CustomerSeatPins): the row also carries the HELD SPOT (SeatKind / SeatItem / SeatIndex /
    /// SeatSub, the queue line QueueItem) and the activity's absolute end time EndMin; the taker pins the same seat,
    /// machine, slot chair, casino table spot, cinema seat or queue line and resumes the remaining time.</summary>
    internal static class CustomerHandoff
    {
        // ── Receiver: the newest visit row per customer id ──────────────────────────────────────────
        private sealed class Known
        {
            public CustomerVisitRow Row = null!;
            public string From = "";
            public float At;
            public bool Final;
            public bool SourceBooks;   // fold F3: the sender keeps this shop's books (it charged what the row shows)
        }
        private static readonly Dictionary<string, Known> _visit = new();
        private static readonly Dictionary<string, float> _finalFrom = new();   // sender pid -> when its last Final arrived
        private static string _visitBldg = "";
        internal static int FinalsReceived, StreamRowsReceived;

        /// <summary>How long a received Final counts as "the one for this hand-off". The Final of an exit
        /// normally arrives BEFORE the authority change it belongs to (the host's election keeps a leaver for
        /// one provisional pass), so freshness is a window, not "after I started waiting".</summary>
        private const float FinalFreshSeconds = 20f;

        // ── Source: bodies that have started walking out (Customer.CustomerLeaveBuilding) ─────────────
        // Keyed by instance id and validated by the ORDER reference: bodies are pooled, and a recycled body
        // carries a new order (the round-45 lesson in CustomerPuppets.RowIdOf).
        private static readonly Dictionary<int, Order> _leaving = new();

        // ── Source: the on-change stream ─────────────────────────────────────────────────────────────
        private static readonly Dictionary<string, int> _sentSig = new();
        private static float _nextStreamAt, _nextFullAt;
        private static string _streamBldg = "";
        internal static int StreamSends, FinalsSent;

        /// <summary>The building whose crowd this machine let go with an EXIT Final. Between ResetIndoors and the
        /// shop context clearing (seconds on a vehicle exit, rig run T-HANDOFF1-20260926-202315) this machine is
        /// still the elected simulator with an emptied interior, and its puppet stream would send EMPTY batches
        /// that walk every copy on the partner's machine out before the take-over. Nothing is streamed for it
        /// until the building changes.</summary>
        internal static string StoppedStreamingFor { get; private set; } = "";

        // ── Adoption scope (one-shot suppression) ────────────────────────────────────────────────────
        private static bool _adoptScope, _suppressFeeCheck, _suppressFeePay, _feeCounted;
        internal static int FeeSuppressed, ComplaintSuppressed, FeeRecharged;

        public static void Reset()
        {
            try
            {
                _visit.Clear(); _finalFrom.Clear(); _visitBldg = "";
                _leaving.Clear();
                _sentSig.Clear(); _streamBldg = ""; _nextStreamAt = 0f; _nextFullAt = 0f;
                _adoptScope = false; _suppressFeeCheck = false; _suppressFeePay = false; _feeCounted = false;
                StoppedStreamingFor = "";
                _ledger.Clear(); _fwdBooked.Clear(); _fwdSeen.Clear();
                CustomerSeatPins.Reset();
#if BAMP_DEV
                _armN = 0; _armSeatN = 0;
#endif
            }
            catch { }
        }

        /// <summary>Step 11: a building change drops every row held for the old building.</summary>
        internal static void OnBuildingChanged(string newBldg)
        {
            try
            {
                if (_visitBldg != newBldg) { _visit.Clear(); _visitBldg = newBldg ?? ""; }
                _sentSig.Clear(); _streamBldg = ""; _nextFullAt = 0f;
                if (StoppedStreamingFor != (newBldg ?? "")) StoppedStreamingFor = "";
                CustomerSeatPins.OnBuilding(newBldg ?? "");
            }
            catch { }
        }

        // ── Presence ─────────────────────────────────────────────────────────────────────────────────
        /// <summary>Is this session player standing in that building? Me: my own shop context. Anyone else:
        /// the position stream's building (RemotePlayerManager.BuildingOf), and only while still in the lobby
        /// - a player who disconnected is not inside anywhere.</summary>
        internal static bool PlayerInside(string pid, string addr)
        {
            try
            {
                if (string.IsNullOrEmpty(pid) || string.IsNullOrEmpty(addr)) return false;
                if (pid == MPConfig.PlayerId) return (MPRegisterSync.CurrentShopAddress ?? "") == addr;
                bool inLobby = false;
                foreach (var p in MPRestSync.AllPlayers()) if (p == pid) { inLobby = true; break; }
                return inLobby && RemotePlayerManager.BuildingOf(pid) == addr;
            }
            catch { return false; }
        }

        /// <summary>Is that player still connected to the session at all?</summary>
        internal static bool InLobby(string pid)
        {
            try
            {
                if (string.IsNullOrEmpty(pid)) return false;
                if (pid == MPConfig.PlayerId) return true;
                foreach (var p in MPRestSync.AllPlayers()) if (p == pid) return true;
            }
            catch { }
            return false;
        }

        internal static bool OtherPlayerInside(string addr)
        {
            try
            {
                if (string.IsNullOrEmpty(addr)) return false;
                foreach (var p in MPRestSync.AllPlayers())
                    if (!string.IsNullOrEmpty(p) && p != MPConfig.PlayerId && RemotePlayerManager.BuildingOf(p) == addr) return true;
            }
            catch { }
            return false;
        }

        // ── Source: capture one live customer ────────────────────────────────────────────────────────
        internal static void NoteLeaving(Customer c)
        {
            try { if (c != null && c.order != null) _leaving[c.GetInstanceID()] = c.order; } catch { }
        }

        private static bool IsLeaving(Customer c)
        {
            try { return _leaving.TryGetValue(c.GetInstanceID(), out var o) && ReferenceEquals(o, c.order); }
            catch { return false; }
        }

        /// <summary>Step 3: everything the taker needs to CONTINUE this visit, read live off the body.</summary>
        internal static CustomerVisitRow? Capture(Customer c, string id)
        {
            try
            {
                if (c == null || string.IsNullOrEmpty(id)) return null;
                var r = new CustomerVisitRow { Id = id };
                try { r.SpawnMin = c.customerEntry?.spawnTime != null ? c.customerEntry.spawnTime.GetTotalMinutes() : -1f; } catch { r.SpawnMin = -1f; }
                var o = c.order;
                r.Completed = o != null && o.completed;
                r.Leaving   = IsLeaving(c);
                r.Basket    = c.hasABasket;
                try { r.Spot = c.assignedWaitingLine != null ? c.currentWaitingLineSpot : -1; } catch { r.Spot = -1; }
                try { r.TimeState = (int)c.customerTimeState; } catch { }
                // Part B step A: the held spot (table id + seat index, machine, slot chair, casino table spot, cinema
                // seat), the queue line and the activity's absolute end time (CustomerSeatPins.ReadHeld).
                CustomerSeatPins.CaptureHeld(c, r);
                var cd = c.citizenData;
                if (cd != null)
                {
                    r.NationalID    = cd.NationalID ?? "";
                    r.Name          = cd.Name ?? "";
                    r.Age           = cd.Age;
                    r.Neighbourhood = cd.Neighbourhood ?? "";
                    r.SocialClass   = (int)cd.SocialClass;
                    r.Gender        = (int)cd.Gender;
                }
                if (o?.entries != null)
                    foreach (var e in o.entries)
                        if (e != null)
                            r.Entries.Add(new CustomerVisitEntryInfo
                            {
                                ItemName = e.itemName ?? "", Price = e.price, WholesalePrice = e.wholesalePrice,
                                Available = e.available, Acceptable = e.priceAccceptable, Paid = e.paid, Processed = e.processed,
                            });
                return r;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Handoff] capture: {ex.Message}"); return null; }
        }

        private static int Sig(CustomerVisitRow r)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (r.Completed ? 1 : 0);
                h = h * 31 + (r.Leaving ? 1 : 0);
                h = h * 31 + (r.Basket ? 1 : 0);
                h = h * 31 + r.Spot;
                h = h * 31 + r.TimeState;
                h = h * 31 + r.SeatKind;
                h = h * 31 + (r.SeatItem ?? "").GetHashCode();
                h = h * 31 + r.SeatIndex;
                h = h * 31 + (r.SeatSub ?? "").GetHashCode();
                h = h * 31 + (r.QueueItem ?? "").GetHashCode();
                h = h * 31 + (int)Math.Round(r.EndMin * 10f);   // absolute: moves only when a new activity starts
                h = h * 31 + r.Entries.Count;
                foreach (var e in r.Entries)
                    h = h * 31 + (e.ItemName ?? "").GetHashCode() * 8 + (e.Processed ? 4 : 0) + (e.Paid ? 2 : 0) + (e.Available ? 1 : 0);
                return h;
            }
        }

        // ── Source: send ─────────────────────────────────────────────────────────────────────────────
        private static void Send(CustomerVisitStatePayload p)
        {
            if (MPServer.IsRunning) MPServer.BroadcastCustomerVisitState(p);
            else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.CustomerVisitState, MPConfig.PlayerId, p));
        }

        /// <summary>Steps 1-2: the FINAL snapshot of a simulator letting go. When this machine books the shop,
        /// every handed-off customer whose order is not complete has its entry id put in
        /// CustomerEntrySync's hand-off set: the spawner marked that entry consumed here, and without the mark
        /// the partner's sale for it would be rejected as "already consumed on the owner's machine" while no
        /// body here will ever complete it (step 8).</summary>
        internal static void SendFinal(string addr, BuildingRegistration? reg, List<CustomerVisitRow> rows, string reason)
        {
            try
            {
                if (string.IsNullOrEmpty(addr)) return;
                bool books = false;
                try { books = reg != null && MergerFlip.BooksHere(reg); } catch { }
                int marked = 0;
                if (books)
                    foreach (var r in rows)
                    {
                        if (r == null || string.IsNullOrEmpty(r.Id)) continue;
                        LedgerAdd(r.Id, reg);
                        if (!r.Completed) { CustomerEntrySync.MarkHandedOff(r.Id); marked++; }
                    }
                var p = new CustomerVisitStatePayload
                {
                    AddressKey = addr, SimulatorPid = MPConfig.PlayerId, Final = true, Reason = reason ?? "",
                    SourceBooks = books, Rows = rows,
                };
                if (reason == "exit") StoppedStreamingFor = addr;
                Send(p);
                FinalsSent++;
                _sentSig.Clear();
                Plugin.Logger.LogInfo($"[Handoff] final snapshot sent: {rows.Count} @{addr} ({reason}){(books ? $" - books here, {marked} unfinished entr{(marked == 1 ? "y" : "ies")} marked handed off" : "")}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Handoff] send final: {ex.Message}"); }
        }

        /// <summary>User ruling (a) 2026-09-26, "include the continuous version": while this machine simulates
        /// with another player inside, the rows that CHANGED go out at most once a second, and the whole set
        /// every 10 s (so a player who walks in later is covered). CustomerPuppets calls this only while it is
        /// the simulator and not waiting for a Final.</summary>
        internal static void StreamTick(string addr, Func<(BuildingRegistration? reg, List<CustomerVisitRow> rows)> capture)
        {
            try
            {
                float now = Time.unscaledTime;
                if (now < _nextStreamAt) return;
                _nextStreamAt = now + 1f;
                if (_streamBldg != addr) { _sentSig.Clear(); _streamBldg = addr; _nextFullAt = 0f; }
                if (!OtherPlayerInside(addr)) { _sentSig.Clear(); _nextFullAt = 0f; return; }
                bool full = now >= _nextFullAt;
                if (full) _nextFullAt = now + 10f;
                var (reg, rows) = capture();
                var send = new List<CustomerVisitRow>();
                var seen = new HashSet<string>();
                foreach (var r in rows)
                {
                    if (r == null || string.IsNullOrEmpty(r.Id)) continue;
                    seen.Add(r.Id);
                    int s = Sig(r);
                    if (full || !_sentSig.TryGetValue(r.Id, out var old) || old != s) { send.Add(r); _sentSig[r.Id] = s; }
                }
                if (_sentSig.Count > seen.Count)
                    foreach (var k in new List<string>(_sentSig.Keys)) if (!seen.Contains(k)) _sentSig.Remove(k);
                if (send.Count == 0) return;
                bool books = false;
                try { books = reg != null && MergerFlip.BooksHere(reg); } catch { }
                Send(new CustomerVisitStatePayload
                {
                    AddressKey = addr, SimulatorPid = MPConfig.PlayerId, Final = false, Reason = "stream",
                    SourceBooks = books, Rows = send,
                });
                StreamSends++;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Handoff] stream: {ex.Message}"); }
        }

        // ── Receiver ─────────────────────────────────────────────────────────────────────────────────
        /// <summary>Step 4, MAIN THREAD: keep the newest row per id for the building I am in; a Final is
        /// logged, remembered per sender, and completes a take-over that is waiting for it.</summary>
        public static void Apply(CustomerVisitStatePayload p)
        {
            try
            {
                if (p == null || p.SimulatorPid == MPConfig.PlayerId) return;   // my own echo
                string my = CustomerPuppets.MyBuilding;
                if (string.IsNullOrEmpty(my) || p.AddressKey != my) return;
                if (_visitBldg != my) { _visit.Clear(); _visitBldg = my; }
                float now = Time.unscaledTime;
                int n = 0;
                if (p.Rows != null)
                    foreach (var r in p.Rows)
                    {
                        if (r == null || string.IsNullOrEmpty(r.Id)) continue;
                        // Newest wins, and a Final wins over the stream: a sender stops streaming when it sends
                        // its Final and the channel is ordered, so a stream row can only follow that Final in a
                        // LATER episode (it simulates here again) - where it is the newer truth. The belt: a stream
                        // row is ignored for 2 s after a Final from the same sender.
                        if (!p.Final && _visit.TryGetValue(r.Id, out var k) && k.Final && k.From == p.SimulatorPid && now - k.At < 2f) continue;
                        _visit[r.Id] = new Known { Row = r, From = p.SimulatorPid ?? "", At = now, Final = p.Final, SourceBooks = p.SourceBooks };
                        n++;
                    }
                if (_visit.Count > 400)
                    foreach (var key in new List<string>(_visit.Keys))
                        if (now - _visit[key].At > 900f) _visit.Remove(key);
                if (p.Final)
                {
                    _finalFrom[p.SimulatorPid ?? ""] = now;
                    FinalsReceived++;
                    Plugin.Logger.LogInfo($"[Handoff] final from {p.SimulatorPid} received: {n} @{p.AddressKey} ({p.Reason}{(p.SourceBooks ? ", source books" : "")}).");
                    CustomerPuppets.HoldForHandoff(p.Rows, p.SimulatorPid ?? "");
                    CustomerPuppets.OnFinalReceived(p.SimulatorPid ?? "");
                }
                else StreamRowsReceived += n;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Handoff] apply: {ex.Message}"); }
        }

        internal static bool HasFreshFinal(string pid)
            => !string.IsNullOrEmpty(pid) && _finalFrom.TryGetValue(pid, out var at) && Time.unscaledTime - at <= FinalFreshSeconds;

        internal static void ConsumeFinal(string pid)
        {
            try { if (!string.IsNullOrEmpty(pid)) _finalFrom.Remove(pid); } catch { }
        }

        /// <summary>The row the taker applies: only one sent by the machine it takes over FROM.</summary>
        internal static CustomerVisitRow? RowFrom(string id, string fromPid)
            => !string.IsNullOrEmpty(id) && _visit.TryGetValue(id, out var k) && k.From == fromPid ? k.Row : null;

        /// <summary>Fold F3: did the machine that sent this row keep the books (so it charged the fee it shows)?</summary>
        internal static bool RowSourceBooks(string id)
            => !string.IsNullOrEmpty(id) && _visit.TryGetValue(id, out var k) && k.SourceBooks;

        /// <summary>Fold F5: every row of the last Final from <paramref name="fromPid"/> (the taker spawns the ones
        /// it holds no copy of).</summary>
        internal static List<CustomerVisitRow> FinalRowsFrom(string fromPid)
        {
            var l = new List<CustomerVisitRow>();
            try
            {
                if (string.IsNullOrEmpty(fromPid)) return l;
                foreach (var kv in _visit) if (kv.Value.Final && kv.Value.From == fromPid && kv.Value.Row != null) l.Add(kv.Value.Row);
            }
            catch { }
            return l;
        }

        internal static bool RowIsFinal(string id)
            => !string.IsNullOrEmpty(id) && _visit.TryGetValue(id, out var k) && k.Final;

        internal static int KnownRows => _visit.Count;

        /// <summary>Fold D1 (2026-09-26): seconds since <paramref name="fromPid"/> last sent a visit row (stream or
        /// Final) for this id in the building I am in; float.MaxValue when it sent none.</summary>
        internal static float RowAgeFrom(string id, string fromPid)
        {
            try
            {
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(fromPid)) return float.MaxValue;
                if (_visitBldg != CustomerPuppets.MyBuilding) return float.MaxValue;
                return _visit.TryGetValue(id, out var k) && k.From == fromPid ? Time.unscaledTime - k.At : float.MaxValue;
            }
            catch { return float.MaxValue; }
        }
        internal static bool HasRow(string id) => !string.IsNullOrEmpty(id) && _visit.ContainsKey(id);
        internal static bool IsLeavingBody(Customer c) => c != null && IsLeaving(c);

        // ── Adoption scope ───────────────────────────────────────────────────────────────────────────
        /// <summary>Step 7: opened around ONE adoption's SpawnCustomer call (Customer.Init runs inside it,
        /// synchronously - IndoorCustomerSpawner.SpawnCustomer :253-275). scope = a visit row was applied, or the
        /// body is adopted without touching an order that is already booked (fold F2) - the arrival complaint is
        /// suppressed. suppressFee is decided by the caller (fold F3, CustomerPuppets.AdoptPuppetAsNative): the
        /// fee is suppressed only when the row's SOURCE kept the books (it charged the fee), when this machine
        /// does not keep them (no money here), or when the order is already booked; a taker that books a row
        /// from a source that did not keeps the snapshot's fee line and books it here once (fold K5 below).
        ///
        /// Fold K5 (2026-09-26): the fee CHECK (TryToProcessEntranceFeeEntryToOrder - a new random citizen judges
        /// the fee and may refuse a shopper already inside) and the gym's fee PAYMENT (PayEntranceFee, which
        /// completes a fee-only order into the till) are suppressed separately: a booking taker re-charging a
        /// row from a non-booking source keeps the snapshot's own fee line (check suppressed) and lets the
        /// payment book it here once.</summary>
        internal static void BeginAdopt(bool scope, bool suppressFeeCheck, bool suppressFeePay)
        {
            _adoptScope = scope;
            _suppressFeeCheck = suppressFeeCheck;
            _suppressFeePay = suppressFeePay;
            _feeCounted = false;
        }

        internal static void EndAdopt() { _adoptScope = false; _suppressFeeCheck = false; _suppressFeePay = false; _feeCounted = false; }

        internal static bool SuppressFeeNow => _suppressFeeCheck;
        internal static bool SuppressFeePayNow => _suppressFeePay;
        internal static bool SuppressComplaintNow => _adoptScope;

        /// <summary>Fold K7: 'fee suppressed' counts CUSTOMERS - a gym adoption passes both fee patches and was
        /// counted twice (rig run T-HANDOFF2-20260926-213941 leg 1: 12 for 6 adopted).</summary>
        internal static void CountFeeSuppressed()
        {
            try { if (_feeCounted) return; _feeCounted = true; FeeSuppressed++; } catch { }
        }

        // ── Fold F1: the owner's hourly pass books a handed-off entry FIRST ─────────────────────────────
        /// <summary>Before an hourly pass of <paramref name="reg"/> for <paramref name="hour"/> on the machine that
        /// keeps its books: the handed-off entries it is about to take (null = nothing marked here).</summary>
        internal static List<KeyValuePair<AI.Customers.CustomerEntries.CustomerEntry, string>>? HourlyPassBegin(BuildingRegistration? reg, int hour)
        {
            try
            {
                if (reg == null || CustomerEntrySync.HandedOffCount == 0) return null;
                if (!MergerFlip.BooksHere(reg)) return null;
                return CustomerEntrySync.HandedOffEntriesAt(reg, hour);
            }
            catch { return null; }
        }

        internal static int HourlyBookedHandedOff;

        /// <summary>After that pass: every handed-off entry whose order the pass put in the till is BOOKED - its
        /// mark is spent, so the partner's later forward for it is rejected as "already consumed" (first booking
        /// wins). An entry the pass only capped (ProcessAllCustomersFromThisHour :183-191 marks it completed
        /// without an order) keeps its mark: the partner's sale is still the only one.</summary>
        internal static void HourlyPassEnd(BuildingRegistration? reg, List<KeyValuePair<AI.Customers.CustomerEntries.CustomerEntry, string>>? before, int hour, string pass)
        {
            try
            {
                if (reg == null || before == null) return;
                int booked = 0;
                var till = reg.unprocessedCompletedOrders;
                foreach (var kv in before)
                {
                    var o = kv.Key?.order;
                    bool inTill = false;
                    if (o != null && till != null)
                        foreach (var t in till) if (ReferenceEquals(t, o)) { inTill = true; break; }
                    if (inTill && CustomerEntrySync.UnmarkHandedOff(kv.Value, $"booked by the {pass} hourly pass h{hour}")) booked++;
                }
                HourlyBookedHandedOff += booked;
                Plugin.Logger.LogInfo($"[Handoff] hourly pass ({pass}) @{GameStateReader.AddressKey(reg)} h{hour}: {before.Count} handed-off entr{(before.Count == 1 ? "y" : "ies")} of this hour, {booked} booked here - first booking wins ({CustomerEntrySync.HandedOffCount} mark(s) left).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Handoff] hourly pass: {ex.Message}"); }
        }

        // ── Till ledger: the rig's order-count identity (read by the DEV lever `handoffbook`) ─────────
        // Every customer that crossed a hand-off on the machine that keeps the books, with the Order its
        // schedule entry held; plus every forward the partner sent for one (booked Order, or rejected).
        private sealed class LedgerRow { public string Addr = ""; public Order? EntryOrder; }
        private static readonly Dictionary<string, LedgerRow> _ledger = new();
        private static readonly Dictionary<string, Order> _fwdBooked = new();
        private static readonly HashSet<string> _fwdSeen = new();

        internal static void LedgerAdd(string id, BuildingRegistration? reg)
        {
            try
            {
                if (string.IsNullOrEmpty(id) || reg == null || _ledger.Count > 2000) return;
                if (_ledger.TryGetValue(id, out var have) && have.EntryOrder != null) return;
                var e = CustomerEntrySync.TryFindEntry(reg, id);
                _ledger[id] = new LedgerRow { Addr = GameStateReader.AddressKey(reg), EntryOrder = e?.order };
            }
            catch { }
        }

        internal static void NoteForward(string id, Order? booked)
        {
            try
            {
                if (string.IsNullOrEmpty(id) || _fwdSeen.Count > 4000) return;
                _fwdSeen.Add(id);
                if (booked != null) _fwdBooked[id] = booked;
            }
            catch { }
        }

        /// <summary>Per hand-off customer on this (booking) machine: b = distinct Orders in the till among its entry's
        /// Order and the Order a forward booked for it; f = the partner forwarded a sale for it. paid = b > 0 or f.
        /// The identity: booked (sum of b) == paid, with no id booked twice (dupIds) and no forwarded sale left
        /// unbooked (lostIds).</summary>
        internal static string LedgerReport(BuildingRegistration reg)
        {
            try
            {
                string key = GameStateReader.AddressKey(reg);
                var till = reg.unprocessedCompletedOrders;
                int ids = 0, booked = 0, paid = 0, dup = 0, lost = 0, fwdIn = 0;
                var bad = new List<string>();
                foreach (var kv in _ledger)
                {
                    if (kv.Value.Addr != key) continue;
                    ids++;
                    string id = kv.Key;
                    Order? eo = null;
                    try { eo = CustomerEntrySync.TryFindEntry(reg, id)?.order; } catch { }
                    Order? lo = kv.Value.EntryOrder;
                    _fwdBooked.TryGetValue(id, out var fo);
                    // b counts DISTINCT Orders booked for this customer: a second reference to the SAME Order is a
                    // till duplicate (the `tilldupes` lever and the ProcessDailyOrders tripwire own that one); two
                    // different Orders for one visit - the hourly pass's entry Order and a forward's new Order - is
                    // the double booking this ledger exists to catch.
                    int b = 0;
                    if (till != null)
                    {
                        bool hitE = false, hitL = false, hitF = false;
                        foreach (var o in till)
                        {
                            if (o == null) continue;
                            if (eo != null && ReferenceEquals(o, eo)) hitE = true;
                            else if (lo != null && ReferenceEquals(o, lo)) hitL = true;
                            else if (fo != null && ReferenceEquals(o, fo)) hitF = true;
                        }
                        b = (hitE ? 1 : 0) + (hitL ? 1 : 0) + (hitF ? 1 : 0);
                    }
                    bool f = _fwdSeen.Contains(id);
                    if (f) fwdIn++;
                    booked += b;
                    if (b > 0 || f) paid++;
                    if (b > 1) { dup++; bad.Add($"{id}:dup{b}"); }
                    if (f && b == 0) { lost++; bad.Add($"{id}:lost"); }
                }
                bool ok = booked == paid && dup == 0 && lost == 0;
                return $"ledger={ids} booked={booked} paid={paid} dupIds={dup} lostIds={lost} identity={(ok ? "ok" : "BAD")} forwardsIn={fwdIn} "
                     + $"marks={CustomerEntrySync.HandedOffCount} hourlyBooked={HourlyBookedHandedOff} feeRecharged={FeeRecharged} bad={string.Join(";", bad)}";
            }
            catch (Exception ex) { return "ERR ledger " + ex.Message; }
        }

#if BAMP_DEV
        // ── DEV: `custstate arm <n>` - one log line the moment this interior holds n customers ────────
        private static int _armN, _armSeatN;
        internal static void Arm(int n) { _armN = n; }
        /// <summary>Part B: `custstate arm seated <n>` - one line the moment n live natives sit on a table seat.</summary>
        internal static void ArmSeated(int n) { _armSeatN = n; }
        internal static void ArmTick(int natives, int copies)
        {
            if (_armSeatN > 0)
            {
                int k = 0;
                try { foreach (var c in IndoorCustomerSpawner.Customers) if (c != null && !c.isPlayer && c.isSittingOn != null) k++; } catch { }
                if (k >= _armSeatN)
                {
                    Plugin.Logger.LogInfo($"[Handoff] armed seated count reached: {k} >= {_armSeatN} @{CustomerPuppets.MyBuilding}");
                    _armSeatN = 0;
                }
            }
            if (_armN <= 0) return;
            if (natives + copies < _armN) return;
            Plugin.Logger.LogInfo($"[Handoff] armed count reached: {natives + copies} >= {_armN} (natives={natives} copies={copies}) @{CustomerPuppets.MyBuilding}");
            _armN = 0;
        }
#endif
    }

    // ── Patches ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Step 2: the exit snapshot. BuildingManager.ResetIndoors (private, one call site :1552) runs
    /// only for an exit that is really happening - ExitFromBuildingCoroutine also starts for exits it then
    /// refuses (already exiting, a blocked warehouse gate) - and the customers and the building registration
    /// still exist when it starts (ClearBuildingReferences is its own later step, :1683).</summary>
    [HarmonyPatch(typeof(BuildingManager), "ResetIndoors")]
    public static class Patch_BuildingManager_ResetIndoors_Handoff
    {
        static void Prefix(BuildingManager __instance)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                var reg = __instance != null ? __instance.buildingRegistration : null;
                if (reg == null) return;
                string addr = GameStateReader.AddressKey(reg);
                if (!CustomerPuppets.IAmSimulatorFor(addr)) return;
                if (!CustomerHandoff.OtherPlayerInside(addr)) return;
                var rows = CustomerPuppets.CaptureLiveRows(reg);
                CustomerHandoff.SendFinal(addr, reg, rows, "exit");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Handoff] exit snapshot: {ex.Message}"); }
        }
    }

    /// <summary>Fold F1 (2026-09-26): every native hourly pass of a business - direct or through the DistributedWork
    /// queue (BusinessSimulatorHelper.RunHourly :36-39) - runs this one method (:58-67). On the machine that keeps
    /// the books, a handed-off entry it books first loses its hand-off mark, so the partner's later forward for
    /// that customer is rejected and the visit is booked exactly once.</summary>
    [HarmonyPatch(typeof(BusinessSimulatorHelper), "SimulateBusiness")]
    public static class Patch_SimulateBusiness_HandoffFirstBooking
    {
        static void Prefix(ValueTuple<BuildingRegistration, int> tuple, out List<KeyValuePair<AI.Customers.CustomerEntries.CustomerEntry, string>>? __state)
        {
            __state = null;
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                __state = CustomerHandoff.HourlyPassBegin(tuple.Item1, tuple.Item2);
            }
            catch { __state = null; }
        }

        static void Postfix(ValueTuple<BuildingRegistration, int> tuple, List<KeyValuePair<AI.Customers.CustomerEntries.CustomerEntry, string>>? __state)
        {
            try { if (__state != null) CustomerHandoff.HourlyPassEnd(tuple.Item1, __state, tuple.Item2, "native"); } catch { }
        }
    }

    /// <summary>Step 3: the Leaving flag - every walk-out passes Customer.CustomerLeaveBuilding (:399).</summary>
    [HarmonyPatch(typeof(Customer), nameof(Customer.CustomerLeaveBuilding))]
    public static class Patch_Customer_CustomerLeaveBuilding_Handoff
    {
        static void Postfix(Customer __instance)
        {
            try { if (__instance != null && !__instance.isPlayer) CustomerHandoff.NoteLeaving(__instance); } catch { }
        }
    }

    /// <summary>Step 7a: the private static IndoorCustomerSpawner.TryToProcessEntranceFeeEntryToOrder
    /// (:284-307) appends a fee line to the order and may refuse the spawn. For an adopted body whose
    /// snapshot already holds the fee line, it is neither appended again nor judged again.</summary>
    [HarmonyPatch(typeof(IndoorCustomerSpawner), "TryToProcessEntranceFeeEntryToOrder")]
    public static class Patch_IndoorSpawner_EntranceFee_Handoff
    {
        static bool Prefix(ref bool __result)
        {
            try
            {
                if (!CustomerHandoff.SuppressFeeNow) return true;
                CustomerHandoff.CountFeeSuppressed();
                __result = true;
                return false;
            }
            catch { return true; }
        }
    }

    /// <summary>Step 7b: GymCustomer.Init pays the fee (Customer.PayEntranceFee :224-238, which completes a
    /// fee-only order and books it). The source machine already did.</summary>
    [HarmonyPatch(typeof(Customer), nameof(Customer.PayEntranceFee))]
    public static class Patch_Customer_PayEntranceFee_Handoff
    {
        static bool Prefix()
        {
            try
            {
                if (!CustomerHandoff.SuppressFeePayNow) return true;
                CustomerHandoff.CountFeeSuppressed();
                return false;
            }
            catch { return true; }
        }
    }

    /// <summary>Step 7c: the arrival complaint (Customer.Init :205-208) already played on the source machine.</summary>
    [HarmonyPatch(typeof(Customer), "ComplainAboutUnfulfilledDemands")]
    public static class Patch_Customer_Complain_Handoff
    {
        static bool Prefix()
        {
            try
            {
                if (!CustomerHandoff.SuppressComplaintNow) return true;
                CustomerHandoff.ComplaintSuppressed++;
                return false;
            }
            catch { return true; }
        }
    }
}
