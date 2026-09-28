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
    /// machine, slot chair, casino table spot, cinema seat or queue line and resumes the remaining time.
    ///
    /// Money (2026-09-27): BookOnce - every visit that crosses a hand-off is booked exactly once on the machine that
    /// keeps the books, by whichever booking reaches it first.</summary>
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
        private static readonly Dictionary<string, float> _finalAt = new();     // fold W5: sender pid -> the At of its LATEST Final's rows (never consumed)
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
                _visit.Clear(); _finalFrom.Clear(); _finalAt.Clear(); _visitBldg = "";
                _leaving.Clear();
                _sentSig.Clear(); _streamBldg = ""; _nextStreamAt = 0f; _nextFullAt = 0f;
                _adoptScope = false; _suppressFeeCheck = false; _suppressFeePay = false; _feeCounted = false;
                StoppedStreamingFor = "";
                _ledger.Clear(); _fwdBooked.Clear(); _fwdSeen.Clear();
                ClearStock();   // H-HANDOFF-1 stock once: per session, like the book-once registry
                BookOnce.Reset();
                CustomerSeatPins.Reset();
#if BAMP_DEV
                _armN = 0; _armSeatN = 0;
                PremiseReset();
#endif
            }
            catch { }
        }

        /// <summary>Step 11: a building change drops every row held for the old building.</summary>
        internal static void OnBuildingChanged(string newBldg)
        {
            try
            {
                // Fold W5 (review of 81830db): a Final from the old building is stale here - its freshness goes with its rows.
                if (_visitBldg != newBldg) { _visit.Clear(); _visitBldg = newBldg ?? ""; _finalFrom.Clear(); _finalAt.Clear(); }
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
                                Picked = IsPicked(e) || IsMarkedTaken(e),   // stock once: a unit already grabbed for this line
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

        /// <summary>Steps 1-2: the FINAL snapshot of a simulator letting go. When this machine books the shop, every
        /// handed-off customer is registered in BookOnce (book-once restructure 2026-09-27, replacing the hand-off
        /// marks of step 8): the first booking of that visit that reaches this machine - the partner's forwarded
        /// sale, a checkout here, an hourly pass - books it, every later one is suppressed.</summary>
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
                        if (BookOnce.Register(reg, r.Id, null, "final sent")) marked++;
                    }
                if (books) NoteFinalStock(reg, addr, rows);   // H-HANDOFF-1 stock once: the units that leave with the crowd
                var p = new CustomerVisitStatePayload
                {
                    AddressKey = addr, SimulatorPid = MPConfig.PlayerId, Final = true, Reason = reason ?? "",
                    SourceBooks = books, Rows = rows,
                };
                if (reason == "exit") StoppedStreamingFor = addr;
                Send(p);
                FinalsSent++;
                _sentSig.Clear();
                Plugin.Logger.LogInfo($"[Handoff] final snapshot sent: {rows.Count} @{addr} ({reason}){(books ? $" - books here, {marked} visit(s) newly registered to book once" : "")}.");
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
                if (books) foreach (var r in send) BookOnce.Register(reg, r.Id, null, "stream sent");
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
                // Book once: on the machine that keeps this shop's books, every visit a partner sends is registered.
                BuildingRegistration? boReg = null;
                try
                {
                    var bmR = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                    if (bmR != null && GameStateReader.AddressKey(bmR) == my && MergerFlip.BooksHere(bmR)) boReg = bmR;
                }
                catch { }
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
                        if (boReg != null) BookOnce.Register(boReg, r.Id, null, p.Final ? "final received" : "stream received");
                        if (boReg != null && r.Leaving) ReturnUnsoldWalkOut(boReg, r);   // fold S4
                    }
                if (_visit.Count > 400)
                    foreach (var key in new List<string>(_visit.Keys))
                        if (now - _visit[key].At > 900f) _visit.Remove(key);
                if (p.Final)
                {
                    _finalFrom[p.SimulatorPid ?? ""] = now;
                    _finalAt[p.SimulatorPid ?? ""] = now;
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
        /// it holds no copy of). Fold W5: only the rows of the LATEST Final from it (an older Final's rows of the same
        /// sender are not part of this hand-off).</summary>
        internal static List<CustomerVisitRow> FinalRowsFrom(string fromPid)
        {
            var l = new List<CustomerVisitRow>();
            try
            {
                if (string.IsNullOrEmpty(fromPid)) return l;
                bool haveAt = _finalAt.TryGetValue(fromPid, out float lastAt);
                foreach (var kv in _visit)
                    if (kv.Value.Final && kv.Value.From == fromPid && kv.Value.Row != null && (!haveAt || kv.Value.At >= lastAt)) l.Add(kv.Value.Row);
            }
            catch { }
            return l;
        }

        /// <summary>H-HANDOFF-1 walk-in race (2026-09-27): the partner whose fresh, unconsumed Final holds rows for the
        /// building I am in ("" = none) - newest first.</summary>
        internal static string FreshFinalSender()
        {
            try
            {
                if (_visitBldg != CustomerPuppets.MyBuilding || _visitBldg.Length == 0) return "";
                string best = ""; float bestAt = float.MinValue, now = Time.unscaledTime;
                foreach (var kv in _finalFrom)
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Key != MPConfig.PlayerId && now - kv.Value <= FinalFreshSeconds
                        && kv.Value > bestAt && FinalRowsFrom(kv.Key).Count > 0) { best = kv.Key; bestAt = kv.Value; }
                return best;
            }
            catch { return ""; }
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
                    var bo = BookOnce.BookedOrderOf(id);   // book once: the Order that booked the visit (a stand-alone entry's)
                    // b counts DISTINCT Orders booked for this customer: a second reference to the SAME Order is a
                    // till duplicate (the `tilldupes` lever and the ProcessDailyOrders tripwire own that one); two
                    // different Orders for one visit - the hourly pass's entry Order and a forward's new Order - is
                    // the double booking this ledger exists to catch.
                    int b = 0;
                    if (till != null)
                    {
                        bool hitE = false, hitL = false, hitF = false, hitB = false;
                        foreach (var o in till)
                        {
                            if (o == null) continue;
                            if (eo != null && ReferenceEquals(o, eo)) hitE = true;
                            else if (lo != null && ReferenceEquals(o, lo)) hitL = true;
                            else if (fo != null && ReferenceEquals(o, fo)) hitF = true;
                            else if (bo != null && ReferenceEquals(o, bo)) hitB = true;
                        }
                        b = (hitE ? 1 : 0) + (hitL ? 1 : 0) + (hitF ? 1 : 0) + (hitB ? 1 : 0);
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
                     + $"feeRecharged={FeeRecharged} bad={string.Join(";", bad)} " + BookOnce.Readout(key);
            }
            catch (Exception ex) { return "ERR ledger " + ex.Message; }
        }

        // ── H-HANDOFF-1 STOCK ONCE (user-approved 2026-09-26, batch 27) ────────────────────────────────────
        // RULE: across a hand-off every unit a customer takes comes off a shelf EXACTLY ONCE. Only the machine that keeps
        // the books has real stock: a replica's picks, serves and in-action catch-ups take none (ProcessSelfServiceOrder
        // :21-28, Customer.GrabItem :511, FullServiceEmployee :95/:243, FullServiceOnEnterBuildingInAction :15-18,
        // OrderHelper.Validate :26), and a replica's forwarded sale is deducted on the owner (CustomerEntrySync).
        //  - OUT: when this booking machine lets its crowd go (SendFinal), the units its shelves already gave each open
        //    visit leave with the body. They are remembered per visit here and never deducted again.
        //  - FORWARD: a forwarded sale of that visit books those units without a deduction (credits: this list first,
        //    then the units a live body of the same visit holds here); only the rest comes off a shelf. Credits the sale
        //    does not use go back on a shelf, as the native Leave / ReturnUnacceptablePriceItems would put them.
        //  - ADOPT: a body this machine takes back gets its credits; every other unit its row shows taken (on the
        //    partner's machine, where no real shelf moved) comes off a shelf here now, or the line becomes unavailable
        //    when there is none. Those lines are MARKED so no native re-take (a re-serve's GrabOrderEntryItems, the
        //    in-action catch-up, a basket-less shop's ProcessSelfServiceOrder, a walking pick) takes them a second time.
        private sealed class OutVisit { public string Addr = ""; public readonly List<KeyValuePair<string, float>> Units = new(); }
        private static readonly Dictionary<string, OutVisit> _out = new();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<OrderEntry, object> _takenMark = new();
        private static readonly object _markObj = new object();
        internal static int StockOutUnits, StockFwdCredited, StockFwdLiveCredited, StockAdoptCredited, StockAdoptDeducted, StockAdoptFailed, StockReturned, StockMarkSkips;

        private static void ClearStock() { try { _out.Clear(); } catch { } }

        internal static bool IsPaperBag(string? n)
            => !string.IsNullOrEmpty(n) && n!.IndexOf("bag", StringComparison.OrdinalIgnoreCase) >= 0 && n.IndexOf("paper", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>A line whose unit came off a shelf on a machine with real stock: paid, or judged available and acceptable
        /// (a walking pick validates then grabs; an employee grabs the available lines) - the rig's held test too.</summary>
        internal static bool TakenLine(bool paid, bool available, bool acceptable) => paid || (available && acceptable);

        internal static void MarkTaken(OrderEntry? e) { try { if (e != null && !_takenMark.TryGetValue(e, out _)) _takenMark.Add(e, _markObj); } catch { } }
        internal static bool IsMarkedTaken(OrderEntry? e) { try { return e != null && _takenMark.TryGetValue(e, out _); } catch { return false; } }
        internal static bool AnyMarked(Order? o)
        {
            try { if (o?.entries != null) foreach (var e in o.entries) if (IsMarkedTaken(e)) return true; } catch { }
            return false;
        }

        // Picked (fold of rig run T-HANDOFFSEAT-20260927-033547): 'paid, or available and acceptable' also counted a
        // full-service line the employee had judged (CheckConditions :164-186) but not grabbed yet. A unit is TAKEN when
        // its line was grabbed: a walking pick returned (Customer.GrabItem - marked Picked, on any machine, and carried in
        // the row), or the line is processed, available and acceptable (GrabOrderEntryItems :242-245, ProcessSelfServiceOrder
        // :40-51, the in-action catch-up), or this visit's unit was settled at an adoption (MarkTaken).
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<OrderEntry, object> _picked = new();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Order, object> _outOrders = new();
        internal static void MarkPicked(OrderEntry? e) { try { if (e != null && !_picked.TryGetValue(e, out _)) _picked.Add(e, _markObj); } catch { } }
        internal static bool IsPicked(OrderEntry? e) { try { return e != null && _picked.TryGetValue(e, out _); } catch { return false; } }
        internal static bool TakenOe(OrderEntry? e)
            => e != null && (IsPicked(e) || IsMarkedTaken(e) || (e.processed && e.available && e.priceAccceptable));
        internal static bool TakenRow(CustomerVisitEntryInfo? e)
            => e != null && (e.Picked || (e.Processed && e.Available && e.Acceptable));
        /// <summary>The adopted order's lines were built one per non-null row line, in order (CustomerPuppets).</summary>
        internal static void MarkPickedFromRow(List<OrderEntry>? l, List<CustomerVisitEntryInfo>? rows)
        {
            try
            {
                if (l == null || rows == null) return;
                int i = 0;
                foreach (var r in rows)
                {
                    if (r == null) continue;
                    if (i >= l.Count) return;
                    if (r.Picked && l[i] != null && l[i].itemName == (r.ItemName ?? "")) MarkPicked(l[i]);
                    i++;
                }
            }
            catch { }
        }
        /// <summary>The booking machine's own Order of a visit whose units are OUT (a native hourly pass may complete it into
        /// the till as an unpaid snapshot, BookOnce.HourlyEnd - its taken lines are the out units, not lost ones).</summary>
        internal static bool IsOutOrder(Order? o)
        {
            try
            {
                if (o == null) return false;
                if (_outOrders.TryGetValue(o, out _)) return true;
                var src = BookOnce.SnapOrigin(o);   // fold S1: a kept till snapshot of that Order too
                return src != null && _outOrders.TryGetValue(src, out _);
            }
            catch { return false; }
        }

        /// <summary>SendFinal on the booking machine: per open visit, the units this machine's shelves gave it.</summary>
        private static void NoteFinalStock(BuildingRegistration? reg, string addr, List<CustomerVisitRow> rows)
        {
            try
            {
                if (reg == null || rows == null) return;
                int visits = 0, units = 0;
                foreach (var r in rows)
                {
                    if (r == null || string.IsNullOrEmpty(r.Id)) continue;
                    _out.Remove(r.Id);
                    if (r.Completed || BookOnce.IsBooked(r.Id) || r.Entries == null) continue;   // sold already: its Order is in the till
                    var ov = new OutVisit { Addr = addr };
                    foreach (var e in r.Entries)
                    {
                        if (e == null || string.IsNullOrEmpty(e.ItemName)) continue;
                        bool bag = IsPaperBag(e.ItemName);
                        if (bag ? !e.Available : !TakenRow(e)) continue;
                        if (!bag && !CustomerEntrySync.IsShelfItem(reg, e.ItemName)) continue;   // a fee or a service: no stock
                        ov.Units.Add(new KeyValuePair<string, float>(e.ItemName, e.WholesalePrice));
                    }
                    if (ov.Units.Count == 0) continue;
                    _out[r.Id] = ov; visits++; units += ov.Units.Count;
                    try { var eo = CustomerEntrySync.TryFindEntry(reg, r.Id)?.order; if (eo != null && !_outOrders.TryGetValue(eo, out _)) _outOrders.Add(eo, _markObj); } catch { }
                }
                StockOutUnits += units;
                if (visits > 0) Plugin.Logger.LogInfo($"[Stock] {units} unit(s) of {visits} open visit(s) leave @{addr} with the hand-off (taken off this machine's shelves; never deducted again).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Stock] note final: {ex.Message}"); }
        }

        /// <summary>Fold W1 (review of 81830db): the late adopt releases this machine's own DUPLICATE native of a visit in the
        /// partner's Final. On the machine that keeps the books, the units that duplicate took off this machine's shelves are
        /// recorded as that visit's credits before the release - the bookkeeping NoteFinalStock uses - so the adoption of the
        /// row uses them (StockOnAdopt) instead of taking the same items off a shelf again. A duplicate of a visit that walks
        /// out UNSOLD on the partner's machine (a leaving row, nothing paid) puts its units back on a shelf, as the native Leave
        /// would (the row's own ReturnUnsoldWalkOut ran at receipt, before this body was found).</summary>
        internal static int CreditDuplicate(BuildingRegistration? reg, string id, Customer? c, CustomerVisitRow? row)
        {
            try
            {
                if (reg == null || c == null || string.IsNullOrEmpty(id)) return 0;
                bool books = false;
                try { books = MergerFlip.BooksHere(reg); } catch { }
                if (!books) return 0;
                var o = c.order;
                if (o?.entries == null || o.completed || BookOnce.IsBooked(id)) return 0;   // sold already: its Order is in the till
                var units = new List<KeyValuePair<string, float>>();
                foreach (var e in o.entries)
                {
                    if (e == null || string.IsNullOrEmpty(e.itemName)) continue;
                    bool bag = IsPaperBag(e.itemName);
                    if (bag ? !e.available : !TakenOe(e)) continue;
                    if (!bag && !CustomerEntrySync.IsShelfItem(reg, e.itemName)) continue;   // a fee or a service: no stock
                    units.Add(new KeyValuePair<string, float>(e.itemName, e.wholesalePrice));
                }
                if (units.Count == 0) return 0;
                string addr = "";
                try { addr = GameStateReader.AddressKey(reg); } catch { }
                bool unsoldWalkOut = row != null && row.Leaving;
                if (unsoldWalkOut && row!.Entries != null) foreach (var re in row.Entries) if (re != null && re.Paid) { unsoldWalkOut = false; break; }
                if (unsoldWalkOut)
                {
                    StockUnsoldReturns++;
                    int back = ReturnLeftovers(reg, units, id, "its late-adopt duplicate was released; the visit walks out unsold");
                    Plugin.Logger.LogInfo($"[Stock] late-adopt duplicate {id} @{addr}: {back}/{units.Count} unit(s) it took off this machine's shelves back on a shelf (the visit walks out unsold).");
                    return units.Count;
                }
                if (!_out.TryGetValue(id, out var ov)) { ov = new OutVisit { Addr = addr }; _out[id] = ov; }
                ov.Units.AddRange(units);
                try { var eo = CustomerEntrySync.TryFindEntry(reg, id)?.order; if (eo != null && !_outOrders.TryGetValue(eo, out _)) _outOrders.Add(eo, _markObj); } catch { }
                StockOutUnits += units.Count;
                Plugin.Logger.LogInfo($"[Stock] late-adopt duplicate {id} @{addr}: {units.Count} unit(s) it took off this machine's shelves recorded as the visit's credits before its release (the adoption of its row uses them).");
                return units.Count;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Stock] credit duplicate {id}: {ex.Message}"); return 0; }
        }

        /// <summary>The units this machine gave a visit whose body is elsewhere - removed: the caller uses them up.</summary>
        internal static List<KeyValuePair<string, float>> TakeOut(string id)
        {
            try
            {
                if (!string.IsNullOrEmpty(id) && _out.TryGetValue(id, out var ov)) { _out.Remove(id); return ov.Units; }
            }
            catch { }
            return new List<KeyValuePair<string, float>>();
        }

        internal static bool UseCredit(List<KeyValuePair<string, float>>? l, string item, out float ws)
        {
            ws = 0f;
            if (l == null) return false;
            for (int i = 0; i < l.Count; i++)
                if (l[i].Key == item) { ws = l[i].Value; l.RemoveAt(i); return true; }
            return false;
        }

        /// <summary>Units taken here for a visit that nothing sold: back on a shelf (the native Leave -> ReturnItemsToShelf,
        /// Customer.cs:316-328). A bag handed out is gone, as natively.</summary>
        internal static int ReturnLeftovers(BuildingRegistration? reg, List<KeyValuePair<string, float>>? l, string id, string why,
            List<KeyValuePair<string, float>>? done = null)
        {
            int n = 0;
            try
            {
                if (reg == null || l == null || l.Count == 0) return 0;
                foreach (var kv in l)
                {
                    try
                    {
                        if (IsPaperBag(kv.Key)) continue;
                        if (ItemHelper.ReturnToAShelf(new BigAmbitions.Items.CargoInstance(kv.Key, 1, kv.Value), reg.Address)) { n++; done?.Add(kv); }
                    }
                    catch { }
                }
                StockReturned += n;
                Plugin.Logger.LogInfo($"[Stock] {id}: {l.Count} unit(s) taken here for this visit were not sold ({why}) - {n} back on a shelf.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Stock] return {id}: {ex.Message}"); }
            return n;
        }

        /// <summary>What an adoption's stock settlement did, so a refused spawn can undo ALL of it (fold S2, 2026-09-27): the
        /// units credited / deducted, the carried units put back on a shelf, the lines it marked, the line flags it
        /// changed and the bag it settled.</summary>
        internal sealed class StockUndo
        {
            public string Addr = "";
            public readonly List<KeyValuePair<string, float>> Credits = new(), Deducted = new(), Left = new(), Returned = new();
            public readonly List<OrderEntry> Marked = new();
            public readonly List<(OrderEntry e, bool available, bool paid, float ws)> Flags = new();
            public Order? BagOrder;
        }

        // Fold S5 (2026-09-27): an adopted body whose paper bag is already settled - a booked visit (its booking carried the
        // bag), a body that walks straight out (its forward's bag), or a bag credited from the units it carried from here.
        // Neither a re-serve (FullServiceEmployee.ServeCustomer :91) nor the in-action catch-up takes a bag for it again.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Order, object> _bagSettled = new();
        internal static int StockBagSkips;
        internal static bool ServeBagSkip;
        internal static bool IsBagSettled(Order? o) { try { return o != null && _bagSettled.TryGetValue(o, out _); } catch { return false; } }
        private static void MarkBagSettledFor(StockUndo u, Order? o)
        {
            try { if (o != null && !_bagSettled.TryGetValue(o, out _)) { _bagSettled.Add(o, _markObj); u.BagOrder = o; } } catch { }
        }
        private static void MarkTakenFor(StockUndo u, OrderEntry? e)
        {
            if (e == null || IsMarkedTaken(e)) return;
            MarkTaken(e);
            u.Marked.Add(e);
        }

        /// <summary>ADOPT on the booking machine, BEFORE the spawn (rig run T-HANDOFFSEAT-20260927-034255: the in-action
        /// catch-up ran inside the spawn, before a settlement made after it, and took 14 units a second time).
        /// <paramref name="o"/> is the Order the body is handed (the entry's own, or a booked visit's detached copy).
        /// A booked visit, a finished order or a body that walks straight out takes nothing here: its sale is (or will be)
        /// booked by the source's checkout or forward, which settles its units; its lines are only marked.</summary>
        internal static StockUndo? StockOnAdopt(BuildingRegistration? reg, string id, Order? o, bool booked, bool leaving)
        {
            try
            {
                if (reg == null || o?.entries == null) return null;
                var u = new StockUndo { Addr = GameStateReader.AddressKey(reg) };
                if (!booked && (leaving || o.completed))
                {
                    foreach (var e in o.entries) MarkTakenFor(u, e);   // the coming forward uses this visit's credits (left in place)
                    MarkBagSettledFor(u, o);   // fold S5: the forward's bag (a credit or the register) is this visit's bag
                    return u;
                }
                var credits = TakeOut(id);
                int fail = 0;
                if (booked)
                {
                    foreach (var e in o.entries) MarkTakenFor(u, e);   // the booking (a forward, a checkout) took them
                    MarkBagSettledFor(u, o);   // fold S5: the booking carried the bag
                }
                else
                    foreach (var e in o.entries)
                    {
                        if (e == null || string.IsNullOrEmpty(e.itemName)) continue;
                        float ws;
                        if (IsPaperBag(e.itemName))
                        {
                            if (e.available && UseCredit(credits, e.itemName, out ws))
                            {
                                MarkTakenFor(u, e);
                                u.Credits.Add(new KeyValuePair<string, float>(e.itemName, ws));
                                MarkBagSettledFor(u, o);
                            }
                            continue;
                        }
                        if (!TakenOe(e) || !CustomerEntrySync.IsShelfItem(reg, e.itemName)) continue;
                        if (UseCredit(credits, e.itemName, out ws)) { MarkTakenFor(u, e); u.Credits.Add(new KeyValuePair<string, float>(e.itemName, ws)); continue; }
                        if (CustomerEntrySync.DeductDisplayStock(reg, e.itemName, out ws))
                        {
                            MarkTakenFor(u, e);
                            if (e.wholesalePrice <= 0f && ws > 0f) { u.Flags.Add((e, e.available, e.paid, e.wholesalePrice)); e.wholesalePrice = ws; }
                            u.Deducted.Add(new KeyValuePair<string, float>(e.itemName, ws > 0f ? ws : e.wholesalePrice));
                        }
                        else { u.Flags.Add((e, e.available, e.paid, e.wholesalePrice)); e.available = false; e.paid = false; fail++; }   // the shop cannot give what it does not have
                    }
                int cred = u.Credits.Count, ded = u.Deducted.Count, left = credits.Count;
                StockAdoptCredited += cred; StockAdoptDeducted += ded; StockAdoptFailed += fail;
                if (left > 0) { u.Left.AddRange(credits); ReturnLeftovers(reg, credits, id, "adopted back without them", u.Returned); }
                if (cred + ded + fail + left > 0)
                    Plugin.Logger.LogInfo($"[Stock] adopt {id}{(booked ? " (booked)" : "")}: {cred} unit(s) it carried from here, {ded} taken off a shelf now (picked on the partner's machine), {fail} unavailable, {left} returned.");
                return u;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Stock] adopt {id}: {ex.Message}"); return null; }
        }

        /// <summary>The spawn was refused (or threw) after the settlement - fold S2: ALL of it is undone. The units taken now
        /// go back on a shelf, the carried units put back on a shelf come off it again, every carried unit is out again,
        /// and the marks, line flags and bag it set are restored.</summary>
        internal static void StockAdoptUndo(BuildingRegistration? reg, string id, StockUndo? u)
        {
            try
            {
                if (u == null || reg == null) return;
                StockAdoptDeducted -= u.Deducted.Count; StockAdoptCredited -= u.Credits.Count;
                if (u.Deducted.Count > 0) ReturnLeftovers(reg, u.Deducted, id, "spawn refused");
                foreach (var e in u.Marked) { try { if (e != null) _takenMark.Remove(e); } catch { } }
                for (int i = u.Flags.Count - 1; i >= 0; i--)
                {
                    var f = u.Flags[i];
                    if (f.e == null) continue;
                    f.e.available = f.available; f.e.paid = f.paid; f.e.wholesalePrice = f.ws;
                }
                if (u.BagOrder != null) { try { _bagSettled.Remove(u.BagOrder); } catch { } }
                var leftAgain = new List<KeyValuePair<string, float>>(u.Left);
                int back = 0;
                foreach (var kv in u.Returned)
                {
                    if (CustomerEntrySync.DeductDisplayStock(reg, kv.Key, out _)) back++;
                    else UseCredit(leftAgain, kv.Key, out _);   // it went back on a shelf and is gone from there: not out any more
                }
                StockReturned -= back;
                if (u.Credits.Count + leftAgain.Count > 0)
                {
                    var ov = new OutVisit { Addr = u.Addr };
                    ov.Units.AddRange(u.Credits);
                    ov.Units.AddRange(leftAgain);
                    _out[id] = ov;
                }
                if (u.Deducted.Count + u.Credits.Count + u.Left.Count + u.Marked.Count + u.Flags.Count > 0 || u.BagOrder != null)
                    Plugin.Logger.LogInfo($"[Stock] adopt {id} undone (spawn refused): {u.Deducted.Count} unit(s) back on a shelf, {u.Credits.Count + leftAgain.Count} out again ({back}/{u.Returned.Count} carried unit(s) taken back off a shelf), {u.Marked.Count} mark(s), {u.Flags.Count} line flag(s){(u.BagOrder != null ? " and the bag" : "")} restored.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Stock] adopt undo {id}: {ex.Message}"); }
        }

        /// <summary>Fold S4 (2026-09-27): a visit row from the partner that is walking out with nothing paid ended UNSOLD - no
        /// forward will come (Patch_Order_Pay_HelperForward sends only a paid basket). The units this machine's shelves
        /// gave the visit (out with the body, or held by its kept till snapshot) go back on a shelf, as the native Leave
        /// (Customer.cs:309-313) would put them. Reached only through a row this machine receives (the partner streams
        /// while this machine's player is inside, or sends a Final); once per visit (the units are used up).</summary>
        private static void ReturnUnsoldWalkOut(BuildingRegistration reg, CustomerVisitRow r)
        {
            try
            {
                if (r == null || string.IsNullOrEmpty(r.Id) || !r.Leaving) return;
                if (r.Entries != null) foreach (var e in r.Entries) if (e != null && e.Paid) return;   // paid: its forward settles the units
                if (BookOnce.IsBooked(r.Id)) return;
                var units = TakeOut(r.Id);
                foreach (var e in TillTakenOf(reg, r.Id)) { MarkCreditedLine(e); units.Add(new KeyValuePair<string, float>(e.itemName, e.wholesalePrice)); }
                if (units.Count == 0) return;
                StockUnsoldReturns++;
                ReturnLeftovers(reg, units, r.Id, "the visit walked out unsold on the partner's machine");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Stock] unsold walk-out {r?.Id}: {ex.Message}"); }
        }
        internal static int StockUnsoldReturns;

        /// <summary>A live body of a visit just booked by another Order is finished (CustomerPuppets.FinishLiveBodiesOf): the
        /// units it holds that the booking does not sell go back on a shelf; the booking sold the rest.</summary>
        internal static int ReturnUnbooked(Order? body, Order? booking)
        {
            try
            {
                var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                if (reg == null || body?.entries == null || !MergerFlip.BooksHere(reg) || BookOnce.IsBookedCopy(body)) return 0;
                var sold = new List<KeyValuePair<string, float>>();
                if (booking?.entries != null)
                    foreach (var e in booking.entries)
                        if (e != null && e.paid && !string.IsNullOrEmpty(e.itemName)) sold.Add(new KeyValuePair<string, float>(e.itemName, 0f));
                var back = new List<KeyValuePair<string, float>>();
                foreach (var e in body.entries)
                {
                    if (e == null || string.IsNullOrEmpty(e.itemName) || IsPaperBag(e.itemName)) continue;
                    if (!TakenOe(e) || !CustomerEntrySync.IsShelfItem(reg, e.itemName)) continue;
                    if (UseCredit(sold, e.itemName, out _)) continue;
                    back.Add(new KeyValuePair<string, float>(e.itemName, e.wholesalePrice));
                }
                return back.Count > 0 ? ReturnLeftovers(reg, back, BookOnce.IdOf(body) ?? "?", "its body was finished; the booking does not sell them") : 0;
            }
            catch { return 0; }
        }

        // Units of a visit that sit in the till as a SNAPSHOT: a native exit clean-up (Customer.ForceFinishOrder :363-371 ->
        // CompleteOrder) keeps the processed lines of an adopted body's order and BookOnce keeps that Order in the till
        // ("kept in the till as a snapshot"); a later forward of the visit carries the whole visit and BookOnce unpays the
        // snapshot. Those units came off a shelf here once (rig run T-HANDOFFSEAT-20260927-032513: Client1-177, 8 units
        // taken at the adoption, then 8 more by its forward).
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<OrderEntry, object> _creditedLines = new();
        internal static int StockFwdTillCredited;
        internal static bool IsCreditedLine(OrderEntry? e)
        {
            try
            {
                if (e == null) return false;
                if (_creditedLines.TryGetValue(e, out _)) return true;
                var src = BookOnce.SnapLineOrigin(e);   // fold S1: a snapshot line whose frozen line was credited
                return src != null && _creditedLines.TryGetValue(src, out _);
            }
            catch { return false; }
        }
        internal static void MarkCreditedLine(OrderEntry? e) { try { if (e != null && !_creditedLines.TryGetValue(e, out _)) _creditedLines.Add(e, _markObj); } catch { } }
        /// <summary>Fold S1: a till snapshot's line keeps the stock marks its line had when it was frozen.</summary>
        internal static void CopyLineMarks(OrderEntry? src, OrderEntry? dst)
        {
            try
            {
                if (src == null || dst == null) return;
                if (IsPicked(src)) MarkPicked(dst);
                if (IsMarkedTaken(src)) MarkTaken(dst);
                if (IsCreditedLine(src)) MarkCreditedLine(dst);
            }
            catch { }
        }

        internal static List<OrderEntry> TillTakenOf(BuildingRegistration? reg, string id)
        {
            var l = new List<OrderEntry>();
            try
            {
                var till = reg?.unprocessedCompletedOrders;
                if (till == null || string.IsNullOrEmpty(id)) return l;
                foreach (var o in till)
                {
                    if (o?.entries == null || BookOnce.IdOf(o) != id) continue;
                    if (IsOutOrder(o)) continue;   // its taken lines are the visit's out units - credited through TakeOut
                    foreach (var e in o.entries)
                        if (e != null && !string.IsNullOrEmpty(e.itemName) && !IsPaperBag(e.itemName) && TakenOe(e) && !IsCreditedLine(e))
                            l.Add(e);
                }
            }
            catch { }
            return l;
        }

        internal static bool UseLine(List<OrderEntry>? l, string item, out float ws)
        {
            ws = 0f;
            if (l == null) return false;
            for (int i = 0; i < l.Count; i++)
                if (l[i].itemName == item)
                {
                    ws = l[i].wholesalePrice;
                    try { _creditedLines.Add(l[i], _markObj); } catch { }
                    l.RemoveAt(i);
                    return true;
                }
            return false;
        }

        internal static string StockReadout()
            => $"once=out{StockOutUnits}/fwdCred{StockFwdCredited}/fwdLive{StockFwdLiveCredited}/fwdTill{StockFwdTillCredited}/adoptCred{StockAdoptCredited}/adoptDed{StockAdoptDeducted}/adoptFail{StockAdoptFailed}/ret{StockReturned}/skip{StockMarkSkips}/bagSkip{StockBagSkips}/unsold{StockUnsoldReturns}";

#if BAMP_DEV
        // ── DEV: `custstate arm <n>` - one log line the moment this interior holds n customers ────────
        private static int _armN, _armSeatN;
        internal static void Arm(int n) { _armN = n; }
        /// <summary>Part B: `custstate arm seated <n>` - one line the moment n live natives sit on a table seat.</summary>
        internal static void ArmSeated(int n) { _armSeatN = n; }
        // ── DEV: `stockdelta <shop> [mark]` - the rig's STOCK oracle (fold H2; an identity since the stock-once effort) ──
        // On the machine that keeps the books, per stocked item between two readouts (each readout re-marks), every unit
        // that left the building's stock (every cargo slot: shelves, storage, the register) is accounted for:
        //   drop == live + fwd + hour + other + lost + heldDelta + outDelta + theft + leftUnprocessed
        //   live   paid lines of new till Orders paid by a live checkout HERE (Order.Pay on this machine)
        //   fwd    paid lines of new till Orders booked from a partner's forwarded sale
        //   hour   paid lines of new till Orders the native hourly pass added (BusinessSimulatorHelper.SimulateBusiness)
        //   other  paid lines of any other new till Order (an exit clean-up, a gym's fee completion)
        //   lost   taken-but-unpaid lines of the new Orders (processed, available, acceptable, not paid)
        //   heldDelta  change in units held by live bodies here (open orders; a booked visit's copy is sold, not held)
        //   outDelta   change in units this machine's shelves gave visits whose bodies now live on the partner's machine
        //   theft      units the game's own hourly shoplifting took (BusinessHelper.RunHourly -> BusinessSecurityHelper.SimulateTheft;
        //              the P-STOCKTRACE finding, T-HANDOFF3-20260927-061856) - counted by Patch_SimulateTheft_StockOracle
        //   leftUnprocessed  units a customer picked (stock taken at the pick, Customer.GrabItem) but whose line was not yet
        //              processed when it left with its order open: native Leave returns only PROCESSED lines (Customer.cs:316-327),
        //              so the unit is gone - a NATIVE loss, also in single-player (fold F5, 2026-09-27) - Patch_CustomerLeave_StockOracle
        // stockOk <=> the identity holds for every stocked item (no unit taken twice, none from nothing). Items never
        // stocked here (a fee) are listed as made; paper bags (a random bag name) are report-only.
        private static string _sdKey = "", _sdAt = "";
        private static Dictionary<string, int>? _sdStock, _sdHeld, _sdOut;
        private static List<Order>? _sdTill;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Order, object> _sdLive = new(), _sdHour = new();

        internal static void SdNotePaid(Order? o) { try { if (o != null && !_sdLive.TryGetValue(o, out _)) _sdLive.Add(o, _markObj); } catch { } }
        internal static void SdNoteHourly(BuildingRegistration? reg, int from)
        {
            try
            {
                var t = reg?.unprocessedCompletedOrders;
                if (t == null) return;
                for (int i = Math.Max(0, from); i < t.Count; i++) { var o = t[i]; if (o != null && !_sdHour.TryGetValue(o, out _)) _sdHour.Add(o, _markObj); }
            }
            catch { }
        }

        // DEV trace behind the identity: between a mark and the next readout every native SubtractFromStock on this shop's
        // item instances (and the mod's own DeductDisplayStock) is counted per item and caller - `takes=` in the readout.
        private static HashSet<BigAmbitions.Items.ItemInstance>? _sdTraceSet;
        private static readonly Dictionary<string, int> _sdTakes = new();
        internal static bool SdTracing => _sdTraceSet != null;

        private static void SdTraceArm(BuildingRegistration reg)
        {
            try
            {
                var s = new HashSet<BigAmbitions.Items.ItemInstance>(new TillDupes.RefEq<BigAmbitions.Items.ItemInstance>());
                if (reg.itemInstances != null) foreach (var kv in reg.itemInstances) if (kv.Value != null) s.Add(kv.Value);
                _sdTraceSet = s;
                _sdTakes.Clear();
                _sdTheft.Clear();
                _sdLeft.Clear();
            }
            catch { _sdTraceSet = null; }
        }

        internal static void SdTrace(BigAmbitions.Items.ItemInstance ii, string item, int n)
        {
            try
            {
                if (_sdTraceSet == null || ii == null || n <= 0 || !_sdTraceSet.Contains(ii)) return;
                string who = "?";
                var st = new System.Diagnostics.StackTrace(1, false);
                for (int i = 0; i < st.FrameCount && i < 10; i++)
                {
                    var m = st.GetFrame(i)?.GetMethod();
                    if (m == null) continue;
                    string dn = m.DeclaringType?.Name ?? "";
                    if (dn.IndexOf("Patch", StringComparison.Ordinal) >= 0 || dn == "ItemHelper" || m.Name.IndexOf("SubtractFromStock", StringComparison.Ordinal) >= 0 || m.Name.StartsWith("DMD", StringComparison.Ordinal)) continue;
                    who = dn + "." + m.Name;
                    break;
                }
                SdTraceNote(item, who, n);
            }
            catch { }
        }

        internal static void SdTraceNote(string item, string who, int n)
        {
            try
            {
                if (_sdTraceSet == null || _sdTakes.Count > 60) return;
                string k = CustomerEntrySync.ShortItem(item) + "@" + (who ?? "?").Replace(' ', '_');
                _sdTakes.TryGetValue(k, out var v); _sdTakes[k] = v + n;
            }
            catch { }
        }

        // DEV: units the native hourly shoplifting (BusinessSecurityHelper.SimulateTheft) removed from the marked shop between a
        // mark and the readout, per item: the stock before vs after each SimulateTheft call (its own shelf refill from storage
        // nets to zero in this count). Cleared at every mark (SdTraceArm). Read-only on the game.
        private static readonly Dictionary<string, int> _sdTheft = new();

        // DEV (fold F5, 2026-09-27): the named term for the native pick-then-leave loss. A Leave prefix records, per item, the
        // taken-but-unprocessed lines of a live native leaving the marked shop with its order still open (the same lines
        // SdHeld counts as held; native Leave returns only the processed ones). Cleared at every mark. Read-only on the game.
        private static readonly Dictionary<string, int> _sdLeft = new();
        internal static void SdNoteLeave(Customer? c)
        {
            try
            {
                if (_sdTraceSet == null || c == null || c.isPlayer || c.order == null || c.order.completed || c.order.entries == null) return;
                var bm = InstanceBehavior<BuildingManager>.Instance;
                if (bm == null || !bm.IsPlayerOwnedBusiness || bm.buildingRegistration == null) return;
                if (GameStateReader.AddressKey(bm.buildingRegistration) != _sdKey) return;
                if (BookOnce.IsBookedCopy(c.order)) return;   // a booked visit's units are sold (as SdHeld)
                foreach (var e in c.order.entries)
                    if (e != null && !string.IsNullOrEmpty(e.itemName) && !e.processed && TakenOe(e)) SdAdd(_sdLeft, e.itemName, 1);
            }
            catch { }
        }

        internal static Dictionary<string, int>? SdTheftBefore(BuildingRegistration? reg)
        {
            try
            {
                if (_sdTraceSet == null || reg == null || _sdKey.Length == 0 || GameStateReader.AddressKey(reg) != _sdKey) return null;
                return SdStock(reg);
            }
            catch { return null; }
        }

        internal static void SdTheftAfter(BuildingRegistration? reg, Dictionary<string, int>? before)
        {
            try
            {
                if (before == null || reg == null || _sdTraceSet == null) return;
                var after = SdStock(reg);
                foreach (var kv in before) { int n = kv.Value - SdGet(after, kv.Key); if (n > 0) SdAdd(_sdTheft, kv.Key, n); }
            }
            catch { }
        }

        private static string SdTakesReadout()
        {
            if (_sdTakes.Count == 0) return "takes=-";
            var l = new List<string>();
            foreach (var kv in _sdTakes) l.Add(kv.Key + "x" + kv.Value);
            return "takes=" + string.Join(";", l);
        }

        private static void SdAdd(Dictionary<string, int> d, string k, int n) { d.TryGetValue(k, out var v); d[k] = v + n; }
        private static int SdGet(Dictionary<string, int>? d, string k) => d != null && d.TryGetValue(k, out var v) ? v : 0;

        private static Dictionary<string, int> SdStock(BuildingRegistration reg)
        {
            var d = new Dictionary<string, int>();
            try
            {
                if (reg.itemInstances != null)
                    foreach (var kv in reg.itemInstances)
                    {
                        var ii = kv.Value;
                        if (ii?.cargoInstances == null) continue;
                        foreach (var ci in ii.cargoInstances)
                            if (ci != null && !string.IsNullOrEmpty(ci.itemName)) SdAdd(d, ci.itemName, (int)ci.amount);
                    }
            }
            catch { }
            return d;
        }

        private static Dictionary<string, int> SdHeld(BuildingRegistration reg)
        {
            var d = new Dictionary<string, int>();
            try
            {
                var bm = InstanceBehavior<BuildingManager>.Instance;
                if (bm == null || !ReferenceEquals(bm.buildingRegistration, reg)) return d;
                foreach (var c in IndoorCustomerSpawner.Customers)
                {
                    if (c == null || c.isPlayer || c.order == null || c.order.completed || c.order.entries == null) continue;
                    if (BookOnce.IsBookedCopy(c.order)) continue;   // a booked visit's units are sold: its Order sits in the till
                    foreach (var e in c.order.entries)
                        if (e != null && !string.IsNullOrEmpty(e.itemName) && TakenOe(e)) SdAdd(d, e.itemName, 1);
                }
            }
            catch { }
            return d;
        }

        private static Dictionary<string, int> SdOut(string key)
        {
            var d = new Dictionary<string, int>();
            try { foreach (var ov in _out.Values) if (ov.Addr == key) foreach (var u in ov.Units) SdAdd(d, u.Key, 1); } catch { }
            return d;
        }

        internal static string StockDelta(BuildingRegistration reg, bool markOnly)
        {
            try
            {
                string key = GameStateReader.AddressKey(reg);
                var stock = SdStock(reg);
                var held = SdHeld(reg);
                var outNow = SdOut(key);
                var till = reg.unprocessedCompletedOrders;
                var tillNow = till != null ? new List<Order>(till) : new List<Order>();
                string now = "";
                try { var tm = TimeHelper.Now(); now = $"d{tm.Day}-{tm.Hour:00}:{(int)tm.Minute:00}"; } catch { }
                int heldSum = 0; foreach (var v in held.Values) heldSum += v;
                int outSum = 0; foreach (var v in outNow.Values) outSum += v;
                string res;
                if (markOnly || _sdStock == null || _sdHeld == null || _sdTill == null || _sdOut == null || _sdKey != key)
                    res = $"{key} marked at={now} items={stock.Count} held={heldSum} out={outSum} till={tillNow.Count}{(markOnly ? "" : " (no earlier mark - marked now)")}";
                else
                {
                    var fwdSet = new HashSet<Order>(new TillDupes.RefEq<Order>());
                    foreach (var fo in _fwdBooked.Values) if (fo != null) fwdSet.Add(fo);
                    var oldSet = new HashSet<Order>(new TillDupes.RefEq<Order>());
                    foreach (var q in _sdTill) if (q != null) oldSet.Add(q);
                    var live = new Dictionary<string, int>(); var fwd = new Dictionary<string, int>();
                    var hour = new Dictionary<string, int>(); var oth = new Dictionary<string, int>();
                    var lost = new Dictionary<string, int>();
                    // Fold S1 oracle: every lost unit needs a NAMED cause - exitNative (the native exit clean-up of a visit that
                    // never crossed a hand-off: the game itself drops those units) or pending (a kept snapshot of a registered
                    // visit not booked yet: its forward or its walk-out settles them). Anything else is unexplained -> stockOk=False.
                    var lostExit = new Dictionary<string, int>(); var lostPend = new Dictionary<string, int>(); var lostUnx = new Dictionary<string, int>();
                    int newOrders = 0, nLive = 0, nFwd = 0, nHour = 0, nOth = 0;
                    foreach (var o in tillNow)
                    {
                        if (o == null || o.entries == null || oldSet.Contains(o)) continue;
                        newOrders++;
                        Dictionary<string, int> cat;
                        if (fwdSet.Contains(o)) { cat = fwd; nFwd++; }
                        else if (_sdLive.TryGetValue(o, out _)) { cat = live; nLive++; }
                        else if (_sdHour.TryGetValue(o, out _)) { cat = hour; nHour++; }
                        else { cat = oth; nOth++; }
                        foreach (var e in o.entries)
                        {
                            if (e == null || string.IsNullOrEmpty(e.itemName)) continue;
                            if (e.paid) SdAdd(cat, e.itemName, 1);
                            else if (TakenOe(e) && !IsCreditedLine(e) && !IsOutOrder(o))   // a line a forward carried is sold there
                            {
                                SdAdd(lost, e.itemName, 1);
                                string? vid = BookOnce.IdOf(o);
                                if (vid == null && BookOnce.IsExitOrder(o)) SdAdd(lostExit, e.itemName, 1);
                                else if (vid != null && BookOnce.IsSnapshot(o) && BookOnce.IsRegistered(vid) && !BookOnce.IsBooked(vid)) SdAdd(lostPend, e.itemName, 1);
                                else SdAdd(lostUnx, e.itemName, 1);
                            }
                        }
                    }
                    var names = new SortedSet<string>(StringComparer.Ordinal);
                    foreach (var dd in new[] { stock, _sdStock, live, fwd, hour, oth, lost, held, _sdHeld, outNow, _sdOut, _sdTheft, _sdLeft })
                        foreach (var k in dd.Keys) names.Add(k);
                    var per = new List<string>();
                    var bad = new List<string>();
                    int lostExitT = 0, lostPendT = 0, lostUnxT = 0;
                    int dropT = 0, soldT = 0, lostT = 0, heldT = 0, outT = 0, liveT = 0, fwdT = 0, hourT = 0, othT = 0, made = 0, bagDrop = 0, bagSold = 0;
                    int theftT = 0, bagTheft = 0, leftT = 0;
                    foreach (var nm in names)
                    {
                        int b0 = SdGet(_sdStock, nm), b1 = SdGet(stock, nm);
                        int L = SdGet(live, nm), F = SdGet(fwd, nm), H = SdGet(hour, nm), O = SdGet(oth, nm), lo = SdGet(lost, nm);
                        int hd = SdGet(held, nm) - SdGet(_sdHeld, nm), od = SdGet(outNow, nm) - SdGet(_sdOut, nm);
                        int th = SdGet(_sdTheft, nm);
                        int lf = SdGet(_sdLeft, nm);
                        int drop = b0 - b1, s = L + F + H + O;
                        if (drop == 0 && s == 0 && lo == 0 && hd == 0 && od == 0 && th == 0 && lf == 0) continue;
                        string sn = CustomerEntrySync.ShortItem(nm);
                        if (IsPaperBag(nm)) { bagDrop += drop; bagSold += s; bagTheft += th; continue; }
                        if (!_sdStock.ContainsKey(nm) && !stock.ContainsKey(nm)) { made += s; per.Add($"{sn}:made{s}"); continue; }
                        dropT += drop; soldT += s; lostT += lo; heldT += hd; outT += od; liveT += L; fwdT += F; hourT += H; othT += O; theftT += th; leftT += lf;
                        per.Add($"{sn}:{drop}={L}+{F}+{H}+{O}+{lo}+{hd}+{od}+{th}+{lf}");
                        if (drop != s + lo + hd + od + th + lf) bad.Add($"{sn}:{drop}!={s}+{lo}+{hd}+{od}+{th}+{lf}");
                        int lx = SdGet(lostExit, nm), lp = SdGet(lostPend, nm), lu = SdGet(lostUnx, nm);
                        lostExitT += lx; lostPendT += lp; lostUnxT += lu;
                        if (lu > 0) bad.Add($"{sn}:lost{lu}unexplained");
                    }
                    res = $"{key} since={_sdAt} at={now} newOrders={newOrders} orders=live{nLive}/fwd{nFwd}/hour{nHour}/other{nOth} dropTotal={dropT} soldTotal={soldT} "
                        + $"tillLive={liveT} fwdSold={fwdT} hourSold={hourT} otherSold={othT} lostTotal={lostT} lostNamed=exitNative{lostExitT}/pending{lostPendT} lostUnexplained={lostUnxT} heldDelta={heldT} outDelta={outT} outNow={outSum} theftTotal={theftT} leftUnprocessed={leftT} "
                        + $"madeSold={made} bags={bagDrop}/{bagSold}/stolen{bagTheft} {StockReadout()} stockOk={(bad.Count == 0 ? "True" : "False")} bad={(bad.Count == 0 ? "-" : string.Join(";", bad))} "
                        + $"per={(per.Count == 0 ? "-" : string.Join(";", per))} (per=item:drop=live+fwd+hour+other+lost+heldDelta+outDelta+theft+leftUnprocessed) {SdTakesReadout()}";
                }
                _sdKey = key; _sdStock = stock; _sdHeld = held; _sdOut = outNow; _sdTill = tillNow; _sdAt = now;
                SdTraceArm(reg);
                return res + " ";
            }
            catch (Exception ex) { return "ERR stockdelta " + ex.Message; }
        }

        // ── DEV: `premise await|get|set|do` - the rig's bounded premise waits (scenario hardening, 2026-09-27) ──────────
        // A leg's premise (n live customers, n unbooked, n unbooked that arrived in the CURRENT game hour after the wait
        // was armed - the booking owner's hourly pass books every body already present -, n seated, n seated with at least
        // m game minutes of their seat activity left) is waited for up to a bound, the wait is retried once, and a premise
        // never reached is logged "[Premise] <tag> NOT MET": TestDrive `premise do` then SKIPS the assertions that need it
        // (report line), never a fail. One wait at a time; the tag verdicts live until the session resets.
        private sealed class PremiseWait
        {
            public string Tag = "", Conds = "";
            public float Bound, Start, First, NextAt, EatMin = 20f, BusyMin = 10f;
            public int Retries, Live = -1, Unbooked = -1, Fresh = -1, Seated = -1, Eating = -1, Busy = -1;
            public readonly HashSet<string> Old = new();
            public readonly Dictionary<string, int> Seen = new();
        }
        private static PremiseWait? _pw;
        private static readonly Dictionary<string, bool> _premise = new();
        private static void PremiseReset() { try { _pw = null; _premise.Clear(); } catch { } }

        internal static string PremiseAwait(string tag, float bound, string conds)
        {
            try
            {
                float now = Time.unscaledTime;
                var w = new PremiseWait { Tag = tag, Bound = bound, Start = now, First = now, Conds = conds };
                foreach (var part in conds.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split('=');
                    if (kv.Length != 2) return "ERR premise await: bad condition '" + part + "'";
                    string k = kv[0].Trim().ToLowerInvariant(), v = kv[1].Trim(), vn = v;
                    float em = -1f;
                    int sl = v.IndexOf('/');
                    if (sl > 0)
                    {
                        vn = v.Substring(0, sl);
                        if (!float.TryParse(v.Substring(sl + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out em) || em < 0f)
                            return "ERR premise await: bad minutes in '" + part + "'";
                    }
                    if (!int.TryParse(vn, out int cn) || cn < 0) return "ERR premise await: bad count in '" + part + "'";
                    switch (k)
                    {
                        case "live": w.Live = cn; break;
                        case "unbooked": w.Unbooked = cn; break;
                        case "fresh": w.Fresh = cn; break;
                        case "seated": w.Seated = cn; break;
                        case "eating": w.Eating = cn; if (em >= 0f) w.EatMin = em; break;
                        case "busy": w.Busy = cn; if (em >= 0f) w.BusyMin = em; break;
                        default: return "ERR premise await: unknown condition '" + k + "' (live, unbooked, fresh, seated, eating=n[/minutes], busy=n[/minutes])";
                    }
                }
                var all = IndoorCustomerSpawner.Customers;
                if (all != null)
                    foreach (var c in all)
                    {
                        if (c == null || c.isPlayer) continue;
                        string id = CustomerPuppets.RowIdForCustomer(c) ?? "";
                        if (id.Length > 0) w.Old.Add(id);
                    }
                _premise.Remove(tag);
                _pw = w;
                return $"OK premise await tag={tag} bound={bound:F0}s conds={conds} retries=1 bldg='{CustomerPuppets.MyBuilding}' present={w.Old.Count}";
            }
            catch (Exception ex) { return "ERR premise await: " + ex.Message; }
        }

        /// <summary>met / unmet / pending (the wait still runs) / unknown (never awaited or set on this machine).</summary>
        internal static string PremiseState(string tag)
        {
            try
            {
                if (_premise.TryGetValue(tag ?? "", out bool m)) return m ? "met" : "unmet";
                if (_pw != null && _pw.Tag == tag) return "pending";
            }
            catch { }
            return "unknown";
        }

        /// <summary>The partner machine's verdict, handed over by the rig (a captured `premise get`).</summary>
        internal static void PremiseSet(string tag, bool met)
        {
            try
            {
                _premise[tag ?? ""] = met;
                if (!met) Plugin.Logger.LogInfo($"[Premise] not met ({tag}): the assertions that need it are skipped on this machine (premise not met).");
            }
            catch { }
        }

        private static void PremiseTick(int natives, int copies)
        {
            var w = _pw;
            if (w == null) return;
            try
            {
                float now = Time.unscaledTime;
                if (now < w.NextAt) return;
                w.NextAt = now + 0.5f;
                int hourKey = -1;
                try { var tm = TimeHelper.Now(); hourKey = (int)tm.Day * 24 + (int)tm.Hour; } catch { }
                float nowMin = -1f;
                try { nowMin = TimeHelper.NowInMinutes(); } catch { }
                int unb = 0, fresh = 0, seat = 0, eat = 0, busy = 0;
                var all = IndoorCustomerSpawner.Customers;
                if (all != null)
                    foreach (var c in all)
                    {
                        if (c == null || c.isPlayer) continue;
                        string id = CustomerPuppets.RowIdForCustomer(c) ?? "";
                        bool isNew = false;
                        int firstHour = hourKey;
                        if (id.Length > 0 && !w.Old.Contains(id))
                        {
                            isNew = true;
                            if (!w.Seen.TryGetValue(id, out firstHour)) { firstHour = hourKey; w.Seen[id] = hourKey; }
                        }
                        bool lv = IsLeavingBody(c);
                        bool done = c.order != null && c.order.completed;
                        if (!lv && !done && !BookOnce.IsBooked(id))
                        {
                            unb++;
                            if (isNew && firstHour == hourKey) fresh++;
                        }
                        if (!lv && c.isSittingOn != null)
                        {
                            seat++;
                            float end = -1f;
                            try { end = CustomerSeatPins.ReadHeld(c).EndMin; } catch { }
                            if (end < 0f || nowMin < 0f || end - nowMin >= w.EatMin) eat++;
                        }
                        // busy: IN a timed activity (seat, gym machine, slot, table / play spot) whose end is known and at least
                        // BusyMin game minutes away - the taker can resume that timer (the rig's resumed >= 1).
                        if (!lv && w.Busy >= 0)
                        {
                            float bend = -1f;
                            try { bend = CustomerSeatPins.ReadHeld(c).EndMin; } catch { }
                            if (bend >= 0f && (nowMin < 0f || bend - nowMin >= w.BusyMin)) busy++;
                        }
                    }
                int live = natives + copies;
                bool met = (w.Live < 0 || live >= w.Live) && (w.Unbooked < 0 || unb >= w.Unbooked) && (w.Fresh < 0 || fresh >= w.Fresh)
                        && (w.Seated < 0 || seat >= w.Seated) && (w.Eating < 0 || eat >= w.Eating) && (w.Busy < 0 || busy >= w.Busy);
                string counts = $"live={live} unbooked={unb} fresh={fresh} seated={seat} eating={eat} (seat time left >= {w.EatMin:F0} game min) busy={(w.Busy >= 0 ? busy.ToString() : "-")} (activity end >= {w.BusyMin:F0} game min away) - wanted {w.Conds} @{CustomerPuppets.MyBuilding}";
                if (met)
                {
                    _premise[w.Tag] = true; _pw = null;
                    Plugin.Logger.LogInfo($"[Premise] {w.Tag} met after {now - w.First:F0}s ({w.Retries} retr{(w.Retries == 1 ? "y" : "ies")}): {counts}");
                    return;
                }
                if (now - w.Start < w.Bound) return;
                if (w.Retries == 0)
                {
                    w.Retries = 1; w.Start = now;
                    Plugin.Logger.LogInfo($"[Premise] {w.Tag} not reached within {w.Bound:F0}s - retrying the set-up once (a second wait of {w.Bound:F0}s): {counts}");
                    return;
                }
                _premise[w.Tag] = false; _pw = null;
                Plugin.Logger.LogInfo($"[Premise] {w.Tag} NOT MET within {now - w.First:F0}s (1 retry): {counts} - the assertions that need it are SKIPPED (premise not met), not failed.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Premise] tick: {ex.Message}"); }
        }

        internal static void ArmTick(int natives, int copies)
        {
            PremiseTick(natives, copies);
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

    /// <summary>Book once (2026-09-27, replaces fold F1): every native hourly pass of a business - direct or through the
    /// DistributedWork queue (BusinessSimulatorHelper.RunHourly :36-39) - runs this one method (:58-67). On the machine
    /// that keeps the books, a registered visit that is already booked is set aside from the schedule list for the
    /// pass (suppressed: no till add, no stock, no fee), and an unbooked one the pass puts in the till is booked by it.
    /// The Finalizer always puts the set-aside entries back, even if the native pass threw.</summary>
    [HarmonyPatch(typeof(BusinessSimulatorHelper), "SimulateBusiness")]
    public static class Patch_SimulateBusiness_HandoffFirstBooking
    {
        static void Prefix(ValueTuple<BuildingRegistration, int> tuple, out BookOnce.Pass? __state)
        {
            __state = null;
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                __state = BookOnce.HourlyBegin(tuple.Item1, tuple.Item2, "native");
            }
            catch { __state = null; }
        }

        static void Finalizer(BookOnce.Pass? __state)
        {
            try { if (__state != null) BookOnce.HourlyEnd(__state); } catch { }
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

    /// <summary>H-HANDOFF-1 STOCK ONCE: a re-serve on the booking machine (FullServiceEmployee.ServeCustomer :61 hands EVERY
    /// retail line of the order to GrabOrderEntryItems, which takes a unit per line at :243-245 with no processed check)
    /// skips the lines whose unit this visit already took (CustomerHandoff.MarkTaken at the adoption).</summary>
    [HarmonyPatch(typeof(FullServiceEmployee), nameof(FullServiceEmployee.GrabOrderEntryItems))]
    public static class Patch_FullServiceGrab_StockOnce
    {
        static void Prefix(ref IEnumerable<OrderEntry> orderEntries)
        {
            try
            {
                if (orderEntries == null) return;
                var bm = InstanceBehavior<BuildingManager>.Instance;
                if (bm == null || !bm.IsPlayerOwnedBusiness) return;
                var keep = new List<OrderEntry>();
                int skip = 0;
                foreach (var e in orderEntries) { if (CustomerHandoff.IsMarkedTaken(e)) skip++; else keep.Add(e); }
                if (skip == 0) return;
                orderEntries = keep;
                CustomerHandoff.StockMarkSkips += skip;
            }
            catch { }
        }
    }

    /// <summary>H-HANDOFF-1 STOCK ONCE: the full-service in-action catch-up (FullServiceOnEnterBuildingInAction.OnStart
    /// :13-37 - a body spawned with its visit already under way) validates, pays and takes a unit for EVERY line and a
    /// paper bag. For an adopted body whose lines this visit already took, the taken lines are kept as they are (paid
    /// when available and acceptable, as the catch-up would pay them) and only the other lines run the native steps.</summary>
    [HarmonyPatch(typeof(FullServiceOnEnterBuildingInAction), nameof(FullServiceOnEnterBuildingInAction.OnStart))]
    public static class Patch_FullServiceInAction_StockOnce
    {
        private static System.Reflection.MethodInfo? _regs;

        static bool Prefix(FullServiceOnEnterBuildingInAction __instance)
        {
            try
            {
                var bm = InstanceBehavior<BuildingManager>.Instance;
                if (bm == null || !bm.IsPlayerOwnedBusiness) return true;
                var cust = __instance?.sharedCustomer?.Value;
                var o = cust?.order;
                if (cust == null || o?.entries == null || !CustomerHandoff.AnyMarked(o)) return true;
                _regs ??= AccessTools.Method(typeof(FullServiceOnEnterBuildingInAction), "AreThereCashRegisters");
                bool registers = true;
                try { if (_regs != null) registers = (bool)_regs.Invoke(null, null); } catch { }
                int skipped = 0;
                if (registers)
                    foreach (var e in o.entries)
                    {
                        if (e == null) continue;
                        if (CustomerHandoff.IsMarkedTaken(e))
                        {
                            e.processed = true;
                            if (e.available && e.priceAccceptable) e.paid = true;
                            skipped++;
                            continue;
                        }
                        e.processed = true;
                        var ic = bm.FindOptimalItemController(e.itemName);
                        if (ic == null) continue;
                        OrderHelper.Validate(cust.citizenData, e, ic, payIfAcceptable: true);
                        if (e.paid) ic.ItemInstance.SubtractFromStock();
                    }
                bool anyPaid = false, bagDone = false;
                foreach (var e in o.entries)
                {
                    if (e == null) continue;
                    if (e.paid) anyPaid = true;
                    if (CustomerHandoff.IsPaperBag(e.itemName) && e.available && CustomerHandoff.IsMarkedTaken(e)) bagDone = true;
                }
                if (!bagDone && CustomerHandoff.IsBagSettled(o)) { bagDone = true; CustomerHandoff.StockBagSkips++; }   // fold S5
                if (anyPaid)
                {
                    if (!bagDone)
                    {
                        var bag = bm.FindOptimalItemController("ba:itemname_paperbag");
                        if (bag == null || !bag.ItemInstance.SubtractFromStock())
                        {
                            foreach (var e in o.entries) if (e != null) e.paid = false;
                            cust.Leave();
                        }
                    }
                }
                else
                {
                    foreach (var e in o.entries) if (e != null) e.processed = true;
                    cust.Leave();
                }
                CustomerHandoff.StockMarkSkips += skipped;
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Stock] in-action: {ex.Message}"); return true; }
        }
    }

    /// <summary>H-HANDOFF-1 STOCK ONCE: a basket-less self-service shop processes the whole order at once
    /// (ProcessSelfServiceOrder.OnStart :38-55, a unit per acceptable line). An adopted body's already-taken lines are
    /// held out of that pass and put back where they were afterwards (always - a finalizer).</summary>
    [HarmonyPatch(typeof(ProcessSelfServiceOrder), nameof(ProcessSelfServiceOrder.OnStart))]
    public static class Patch_ProcessSelfService_StockOnce
    {
        static void Prefix(ProcessSelfServiceOrder __instance, out List<KeyValuePair<int, OrderEntry>>? __state)
        {
            __state = null;
            try
            {
                var bm = InstanceBehavior<BuildingManager>.Instance;
                if (bm == null || !bm.IsPlayerOwnedBusiness) return;
                var l = __instance?.sharedCustomer?.Value?.order?.entries;
                if (l == null) return;
                List<KeyValuePair<int, OrderEntry>>? st = null;
                for (int i = 0; i < l.Count; i++)
                    if (CustomerHandoff.IsMarkedTaken(l[i])) (st ??= new List<KeyValuePair<int, OrderEntry>>()).Add(new KeyValuePair<int, OrderEntry>(i, l[i]));
                if (st == null) return;
                for (int i = st.Count - 1; i >= 0; i--) l.RemoveAt(st[i].Key);
                __state = st;
                CustomerHandoff.StockMarkSkips += st.Count;
            }
            catch { __state = null; }
        }

        static void Finalizer(ProcessSelfServiceOrder __instance, List<KeyValuePair<int, OrderEntry>>? __state)
        {
            try
            {
                if (__state == null) return;
                var l = __instance?.sharedCustomer?.Value?.order?.entries;
                if (l == null) return;
                foreach (var kv in __state) l.Insert(Math.Min(kv.Key, l.Count), kv.Value);
            }
            catch { }
        }
    }

    /// <summary>H-HANDOFF-1 STOCK ONCE: a walking pick (Customer.GrabItem :504-530, SubtractFromStock at :513) of a line this
    /// visit already took takes nothing again.</summary>
    [HarmonyPatch(typeof(Customer), nameof(Customer.GrabItem), new Type[] { typeof(ItemController), typeof(Customer), typeof(OrderEntry) })]
    public static class Patch_CustomerGrabItem_StockOnce
    {
        static bool Prefix(OrderEntry orderEntry, ref bool __result)
        {
            try
            {
                if (!CustomerHandoff.IsMarkedTaken(orderEntry)) return true;
                var bm = InstanceBehavior<BuildingManager>.Instance;
                if (bm == null || !bm.IsPlayerOwnedBusiness) return true;
                CustomerHandoff.StockMarkSkips++;
                __result = orderEntry.available && orderEntry.priceAccceptable;
                return false;
            }
            catch { return true; }
        }

        static void Postfix(OrderEntry orderEntry, bool __result)
        {
            try { if (__result && orderEntry != null) CustomerHandoff.MarkPicked(orderEntry); } catch { }
        }
    }

    /// <summary>Fold S5 (2026-09-27): a re-serve of an adopted body whose bag is already settled (CustomerHandoff.IsBagSettled)
    /// takes no second paper bag - FullServiceEmployee.ServeCustomer :91 subtracts one from the register inside its own
    /// MoveNext. While that MoveNext runs for such a body, the register's paper-bag subtract is skipped once.</summary>
    [HarmonyPatch]
    public static class Patch_FullServiceServe_BagOnce
    {
        private static System.Reflection.FieldInfo? _this;

        static bool Prepare() => AccessTools.Method(typeof(FullServiceEmployee), "ServeCustomer") != null;

        static System.Reflection.MethodBase TargetMethod()
            => AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(FullServiceEmployee), "ServeCustomer"));

        static void Prefix(object __instance)
        {
            try
            {
                CustomerHandoff.ServeBagSkip = false;
                if (__instance == null) return;
                _this ??= AccessTools.Field(__instance.GetType(), "<>4__this");
                var emp = _this?.GetValue(__instance) as FullServiceEmployee;
                var o = emp != null && emp.customer != null ? emp.customer.order : null;
                if (o != null && CustomerHandoff.IsBagSettled(o)) CustomerHandoff.ServeBagSkip = true;
            }
            catch { CustomerHandoff.ServeBagSkip = false; }
        }

        static void Finalizer() { CustomerHandoff.ServeBagSkip = false; }
    }

    /// <summary>Fold S5: the paper-bag subtract a bag-settled re-serve would make (see Patch_FullServiceServe_BagOnce).</summary>
    [HarmonyPatch(typeof(ItemHelper), nameof(ItemHelper.SubtractFromStock))]
    public static class Patch_SubtractFromStock_BagOnce
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(BigAmbitions.Items.ItemInstance itemInstance, ref bool __result)
        {
            try
            {
                if (!CustomerHandoff.ServeBagSkip || itemInstance == null) return true;
                var ci = itemInstance.GetStockInstance();
                if (ci == null || !CustomerHandoff.IsPaperBag(ci.itemName)) return true;
                CustomerHandoff.ServeBagSkip = false;
                CustomerHandoff.StockBagSkips++;
                __result = true;
                return false;
            }
            catch { return true; }
        }
    }

#if BAMP_DEV
    /// <summary>DEV stock oracle trace: who took each unit off this shop's stock between a mark and the readout.</summary>
    [HarmonyPatch(typeof(ItemHelper), nameof(ItemHelper.SubtractFromStock))]
    public static class Patch_SubtractFromStock_StockOracle
    {
        static void Prefix(BigAmbitions.Items.ItemInstance itemInstance, out KeyValuePair<string, float>? __state)
        {
            __state = null;
            try
            {
                if (!CustomerHandoff.SdTracing || itemInstance == null) return;
                var ci = itemInstance.GetStockInstance();
                if (ci != null && !string.IsNullOrEmpty(ci.itemName)) __state = new KeyValuePair<string, float>(ci.itemName, (float)ci.amount);
            }
            catch { __state = null; }
        }

        static void Postfix(BigAmbitions.Items.ItemInstance itemInstance, KeyValuePair<string, float>? __state)
        {
            try
            {
                if (__state == null || itemInstance == null) return;
                var ci = itemInstance.GetStockInstance();
                float after = ci != null && ci.itemName == __state.Value.Key ? (float)ci.amount : 0f;
                int n = (int)Math.Round(__state.Value.Value - after);
                if (n > 0) CustomerHandoff.SdTrace(itemInstance, __state.Value.Key, n);
            }
            catch { }
        }
    }
#endif

#if BAMP_DEV
    /// <summary>DEV stock oracle: the till Orders a native hourly pass appends are classed "hour" (CustomerHandoff.StockDelta).</summary>
    [HarmonyPatch(typeof(BusinessSimulatorHelper), "SimulateBusiness")]
    public static class Patch_SimulateBusiness_StockOracle
    {
        static void Prefix(ValueTuple<BuildingRegistration, int> tuple, out int __state)
        {
            __state = -1;
            try { __state = tuple.Item1?.unprocessedCompletedOrders?.Count ?? -1; } catch { }
        }

        static void Finalizer(ValueTuple<BuildingRegistration, int> tuple, int __state)
        {
            try { if (__state >= 0) CustomerHandoff.SdNoteHourly(tuple.Item1, __state); } catch { }
        }
    }
#endif

#if BAMP_DEV
    /// <summary>DEV stock oracle: the units the game's own hourly shoplifting (BusinessHelper.RunHourly ->
    /// BusinessSecurityHelper.SimulateTheft) takes off the marked shop are the identity's `theft` term
    /// (CustomerHandoff.StockDelta). The P-STOCKTRACE finding (T-HANDOFF3-20260927-061856). Read-only.</summary>
    [HarmonyPatch(typeof(BusinessSecurityHelper), nameof(BusinessSecurityHelper.SimulateTheft))]
    public static class Patch_SimulateTheft_StockOracle
    {
        static void Prefix(BuildingRegistration buildingRegistration, out Dictionary<string, int>? __state)
        {
            __state = null;
            try { __state = CustomerHandoff.SdTheftBefore(buildingRegistration); } catch { __state = null; }
        }

        static void Finalizer(BuildingRegistration buildingRegistration, Dictionary<string, int>? __state)
        {
            try { if (__state != null) CustomerHandoff.SdTheftAfter(buildingRegistration, __state); } catch { }
        }
    }
#endif

#if BAMP_DEV
    /// <summary>DEV stock oracle (fold F5, 2026-09-27): the identity's `leftUnprocessed` term - a leaving customer's picked but
    /// unprocessed units, which native Leave never returns (Customer.cs:316-327). Read-only.</summary>
    [HarmonyPatch(typeof(Customer), nameof(Customer.Leave))]
    public static class Patch_CustomerLeave_StockOracle
    {
        static void Prefix(Customer __instance)
        {
            try { CustomerHandoff.SdNoteLeave(__instance); } catch { }
        }
    }
#endif
}
