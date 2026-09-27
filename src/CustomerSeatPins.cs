using System;
using System.Collections.Generic;
using AI.Customers.CustomerEntries;
using BigAmbitions.DayNightCycle;
using BigAmbitions.Items;
using Buildings;
using Buildings.BuildingTypes.Special;
using Buildings.Retail.Businesses.CinemaTheater;
using EmployeeStations;
using HarmonyLib;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>H-HANDOFF-1 part B (batch 27, 2026-09-27) - THE SAME SEAT OR MACHINE ACROSS A HAND-OFF.
    ///
    /// Part A carried a customer's VISIT (clock, order, citizen) to the machine that takes the crowd over; the body
    /// still re-rolled every spot: a diner eating at table 3 stood up and sat at a random free seat, a gym customer
    /// half-way through a set walked to another machine and started a fresh 15-45 minute workout. This class
    /// carries the HELD SPOT and the ACTIVITY END TIME:
    ///   A. CaptureHeld (source): kind + item id + seat index / sub-spot + the absolute game minute the activity ends
    ///      (EndMin - absolute so the stream's change signature does not move every minute), plus the queue line.
    ///   B. Wants (taker): recorded BEFORE the adopted body's SpawnCustomer, keyed by its CustomerEntry, reserved
    ///      right after it; the game's own pick routines (patches below) take the wanted spot and fall back to
    ///      today's random pick when it is gone, taken or of another kind (every fallback is logged with a reason);
    ///      the activity timers are then set to the row's end time, clamped to 0..the game's own maximum.
    ///   C. FreeHeld (giver): the pooled release (CustomerPool.ActionOnRelease) frees NONE of a customer's table
    ///      seat, queue line place, slot chair, casino table spot or dance spot - every authority swap leaked them
    ///      on the giving machine, and a leaked seat reads "taken" exactly when the crowd comes back.
    /// Casino (manager decision 2026-09-27): a slot chair / table spot is pinned only when the behaviour tree picks
    /// that activity again; an unused slot want is logged. Gym: only the current machine's remaining time.
    /// Hairdresser: the same line/chair only (the station's service progress is the employee's, not handed over).</summary>
    internal static class CustomerSeatPins
    {
        internal const int KNone = 0, KSeat = 1, KSlot = 2, KSpot = 3, KGym = 4, KCinema = 5;
        private static readonly string[] KindNames = { "none", "seat", "slot", "tablespot", "gym", "cinema" };
        internal static string KindName(int k) => k >= 0 && k < KindNames.Length ? KindNames[k] : "k" + k;

        // Game maxima for the clamp (decompile): SitInASeat maxSittingTime (per-task field), FullServiceSitInASeat
        // 18 + 2 prep, InitGymWorkout Lerp(15,45), SitInASlotMachine / GoPlayOnAGameSpot 32.
        private const float MaxFullService = 18f, MaxGym = 45f, MaxCasino = 32f;
        private const float WantTimeoutSeconds = 60f;

        internal struct Held
        {
            public int Kind; public string Item; public int Index; public string Sub; public float EndMin; public string QueueItem;
        }

        private static Held Empty() => new Held { Kind = KNone, Item = "", Index = -1, Sub = "", EndMin = -1f, QueueItem = "" };

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────────
        private static BuildingManager? BM() { try { return InstanceBehavior<BuildingManager>.Instance; } catch { return null; } }

        private static string IdOf(ItemController? ic) { try { return ic != null && ic.ItemInstance != null ? ic.ItemInstance.id ?? "" : ""; } catch { return ""; } }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var ch = s.ToCharArray();
            for (int i = 0; i < ch.Length; i++) if (ch[i] == ' ' || ch[i] == ';' || ch[i] == ':' || ch[i] == '/' || ch[i] == '|' || ch[i] == '=') ch[i] = '_';
            return new string(ch);
        }

        /// <summary>A transform's name is not unique under one item (cinema rows, table sides): name + sibling index.</summary>
        internal static string SpotName(Transform? t)
        {
            try { return t == null ? "" : Clean(t.name) + "@" + t.GetSiblingIndex(); } catch { return ""; }
        }

        private static float NowMin() { try { return TimeHelper.NowInMinutes(); } catch { return 0f; } }

        private static float Min(Timestamp? t) { try { return t != null ? t.GetTotalMinutes() : -1f; } catch { return -1f; } }

        private static List<T> Tasks<T>(Customer c) where T : BehaviorDesigner.Runtime.Tasks.Task
        {
            try
            {
                var bt = c != null ? c.behaviorTree : null;
                var l = bt != null ? bt.FindTasks<T>() : null;
                return l ?? new List<T>();
            }
            catch { return new List<T>(); }
        }

        private static ItemController? FindItem(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var bm = BM();
            if (bm == null || bm.allItemControllers == null) return null;
            foreach (var ic in bm.allItemControllers)
                if (ic != null && ic.ItemInstance != null && ic.ItemInstance.id == id) return ic;
            return null;
        }

        // Seat -> (table, index): SeatSpot is a plain serialized object inside the table's array; the cache is
        // validated on every read (tables move in the interior designer).
        private static readonly Dictionary<SeatSpot, (ItemController table, int idx)> _seatTable = new();

        private static (ItemController? table, int idx) TableOf(SeatSpot seat)
        {
            try
            {
                if (seat == null) return (null, -1);
                if (_seatTable.TryGetValue(seat, out var hit) && hit.table != null && hit.table.SeatSpots != null
                    && hit.idx < hit.table.SeatSpots.Length && ReferenceEquals(hit.table.SeatSpots[hit.idx], seat)) return (hit.table, hit.idx);
                var bm = BM();
                if (bm == null || bm.allItemControllers == null) return (null, -1);
                if (_seatTable.Count > 2000) _seatTable.Clear();
                foreach (var ic in bm.allItemControllers)
                {
                    if (ic == null || ic.Item == null || (ic.Item.type & ItemType.Table) == 0) continue;
                    var ss = ic.SeatSpots;
                    if (ss == null) continue;
                    for (int i = 0; i < ss.Length; i++)
                        if (ReferenceEquals(ss[i], seat)) { _seatTable[seat] = (ic, i); return (ic, i); }
                }
            }
            catch { }
            return (null, -1);
        }

        private static string MgrItemId(PlaySpotsManager? m)
        {
            try { return m != null ? IdOf(m.GetComponentInParent<ItemController>()) : ""; } catch { return ""; }
        }

        private static IEnumerable<PlaySpotsManager> AllPlaySpotManagers()
        {
            var l = new List<PlaySpotsManager>();
            try { if (CasinoBusinessHelper._blackjackTablePlaySpotsManagers != null) l.AddRange(CasinoBusinessHelper._blackjackTablePlaySpotsManagers); } catch { }
            try { if (CasinoBusinessHelper._rouletteTablePlaySpotsManagers != null) l.AddRange(CasinoBusinessHelper._rouletteTablePlaySpotsManagers); } catch { }
            return l;
        }

        // ── A. Capture ───────────────────────────────────────────────────────────────────────────────
        /// <summary>What this live customer holds, first match wins: table seat, gym machine, slot chair, casino
        /// table spot, cinema seat; the queue line alongside. EndMin = the absolute game minute its current activity
        /// ends, -1 while it is still walking there.</summary>
        internal static Held ReadHeld(Customer c)
        {
            var h = Empty();
            if (c == null) return h;
            try { var wl = c.assignedWaitingLine; if (wl != null) h.QueueItem = IdOf(wl.ItemController); } catch { }
            // 1. Table seat (cafe / bar / fast food / restaurant).
            try
            {
                var seat = c.isSittingOn;
                if (seat != null)
                {
                    var (table, idx) = TableOf(seat);
                    if (table != null)
                    {
                        h.Kind = KSeat; h.Item = IdOf(table); h.Index = idx; h.EndMin = SeatEnd(c, seat);
                        return h;
                    }
                }
            }
            catch { }
            // 2. Gym machine: working out (end known) or walking to it.
            try
            {
                foreach (var t in Tasks<UseWorkoutMachine>(c))
                    if (t != null && t._isWorkingOut && t.sharedWorkoutMachineController != null && t.sharedWorkoutMachineController.Value != null)
                    { h.Kind = KGym; h.Item = IdOf(t.sharedWorkoutMachineController.Value); h.EndMin = Min(t._endTime); return h; }
                foreach (var t in Tasks<TryToMoveToWorkoutMachine>(c))
                    if (t != null && t._characterMoveToPosition != null && t._characterMoveToPosition._checkingDestination
                        && t.sharedWorkoutMachineController != null && t.sharedWorkoutMachineController.Value != null)
                    { h.Kind = KGym; h.Item = IdOf(t.sharedWorkoutMachineController.Value); h.EndMin = -1f; return h; }
            }
            catch { }
            // 3. Slot chair (Reset nulls the controller, not the chair).
            try
            {
                foreach (var t in Tasks<SitInASlotMachine>(c))
                    if (t != null && t._slotMachineController != null && t._slotMachineChair != null)
                    { h.Kind = KSlot; h.Item = IdOf(t._slotMachineChair); h.EndMin = t._hasStartedPlaying ? Min(t._stopPlayingTimestamp) : -1f; return h; }
                foreach (var t in Tasks<SitInASlotMachineInstantly>(c))
                    if (t != null && t._slotMachineController != null && t._slotMachineChair != null && t._canMoveToSlotMachine)
                    { h.Kind = KSlot; h.Item = IdOf(t._slotMachineChair); h.EndMin = Min(t._stopPlayingTimestamp); return h; }
            }
            catch { }
            // 4. Casino table spot (blackjack / roulette): the table item + the spot's transform name (the manager's
            // array is shuffled differently on each machine, so an index would not travel).
            try
            {
                foreach (var t in Tasks<GoPlayOnAGameSpot>(c))
                    if (t != null && t._playSpot != null && t._gamePlaySpotsManager != null)
                    {
                        h.Kind = KSpot; h.Item = MgrItemId(t._gamePlaySpotsManager); h.Sub = SpotName(t._playSpot);
                        h.EndMin = t._hasStartedRotating && t._hasStartedPlaying ? Min(t._stopPlayingTimestamp) : -1f;
                        return h;
                    }
                foreach (var t in Tasks<GoPlayOnAGameSpotInstantly>(c))
                    if (t != null && t._playSpot != null && t._gamePlaySpotsManager != null && t._canMoveToPlaySpot)
                    { h.Kind = KSpot; h.Item = MgrItemId(t._gamePlaySpotsManager); h.Sub = SpotName(t._playSpot); h.EndMin = Min(t._stopPlayingTimestamp); return h; }
            }
            catch { }
            // 5. Cinema / theatre seat: the tpc's reservation (the leave time follows the clock hour, no end needed).
            try
            {
                var tpc = c.tpc;
                Transform? sp = null;
                if (tpc != null)
                {
                    foreach (var kv in CinemaTheaterHelper.Reservations)
                        if (kv.Value == tpc && kv.Key) { sp = kv.Key; break; }
                    if (sp == null && tpc.isSittingOn && CinemaTheaterHelper.IsValidSittingPosition(tpc.isSittingOn)) sp = tpc.isSittingOn;
                }
                if (sp != null)
                {
                    h.Kind = KCinema; h.Item = IdOf(sp.GetComponentInParent<ItemController>()); h.Sub = SpotName(sp);
                    return h;
                }
            }
            catch { }
            return h;
        }

        /// <summary>The seated activity's end: SitInASeat(.Instantly) = the waiting stamp once seated; full service =
        /// the stage stamp plus the stages still to come (prep: the whole sitting time; eating food with ice cream
        /// to follow: the ice-cream share).</summary>
        private static float SeatEnd(Customer c, SeatSpot seat)
        {
            foreach (var t in Tasks<SitInASeat>(c))
                if (t != null && ReferenceEquals(t._seatSpot, seat)) return t._hasSitDown ? Min(t._stopWaitingTimeStamp) : -1f;
            foreach (var t in Tasks<SitInASeatInstantly>(c))
                if (t != null && ReferenceEquals(t._seatSpot, seat)) return t._hasSitDown ? Min(t._stopWaitingTimeStamp) : -1f;
            foreach (var t in Tasks<FullServiceSitInASeat>(c))
                if (t != null && ReferenceEquals(t._seatSpot, seat))
                {
                    if (!t._hasStartedPreparation) return -1f;
                    float s = Min(t._stopActionTimeStamp);
                    if (s < 0f) return -1f;
                    if (!t._isEatingFood && !t._isEatingIceCream) s += t._sittingTime;
                    else if (t._isEatingFood && t._hasIceCream) s += t._sittingTime - t._eatingFoodTime;
                    return s;
                }
            return -1f;
        }

        /// <summary>Step A: fill the visit row's held-spot fields (CustomerHandoff.Capture).</summary>
        internal static void CaptureHeld(Customer c, CustomerVisitRow r)
        {
            try
            {
                if (c == null || r == null) return;
                var h = ReadHeld(c);
                r.SeatKind = h.Kind; r.SeatItem = h.Item ?? ""; r.SeatIndex = h.Index; r.SeatSub = h.Sub ?? "";
                r.EndMin = h.EndMin; r.QueueItem = h.QueueItem ?? "";
                r.Remaining = h.EndMin >= 0f ? Math.Max(0f, h.EndMin - NowMin()) : -1f;   // diagnostic only (not in Sig)
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] capture error: {ex.Message}"); }
        }

        // ── C. Free what a released body holds (giver, before ReleaseCustomer) ─────────────────────────
        internal static int FreedSeats, FreedLines, FreedSlots, FreedSpots, FreedDance;

        /// <summary>Step C, CustomerPuppets.ReactToAuthority: before c.ReleaseCustomer(), whose pooled release only
        /// hides the body and stops its tree. Must run BEFORE the release: the tree's clean-up nulls the slot
        /// task's controller and the table task's spot.</summary>
        internal static void FreeHeld(Customer c)
        {
            if (c == null || c.isPlayer) return;
            try { if (c.isSittingOn != null) { c.ResetItemsInTable(); FreedSeats++; } }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] free seat error: {ex.Message}"); }
            try
            {
                var wl = c.assignedWaitingLine;
                if (wl != null)
                {
                    // Not RemoveCustomer: it moves the rest of the line forward, and those bodies are released too
                    // (the game's own OnTimeMachineStarted, Customer.cs:139-142, does the same Remove).
                    if (wl.data != null && wl.data.customers != null) wl.data.customers.Remove(c);
                    c.assignedWaitingLine = null;
                    FreedLines++;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] free line error: {ex.Message}"); }
            try
            {
                foreach (var t in Tasks<SitInASlotMachine>(c))
                    if (t != null && t._slotMachineController != null && t._hasStartedPlaying && t._slotMachineChair != null)
                    { t._slotMachineChair.Occupied = false; try { t._slotMachineController.StopSlotMachineSounds(); } catch { } FreedSlots++; }
                foreach (var t in Tasks<SitInASlotMachineInstantly>(c))
                    if (t != null && t._slotMachineController != null && t._canMoveToSlotMachine && t._slotMachineChair != null)
                    { t._slotMachineChair.Occupied = false; try { t._slotMachineController.StopSlotMachineSounds(); } catch { } FreedSlots++; }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] free slot error: {ex.Message}"); }
            try
            {
                foreach (var t in Tasks<GoPlayOnAGameSpot>(c))
                    if (t != null && t._playSpot != null && t._gamePlaySpotsManager != null)
                    { t._gamePlaySpotsManager.SetPlaySpotStatus(t._playSpot, PlaySpotStatus.Free); FreedSpots++; }
                foreach (var t in Tasks<GoPlayOnAGameSpotInstantly>(c))
                    if (t != null && t._playSpot != null && t._gamePlaySpotsManager != null)
                    { t._gamePlaySpotsManager.SetPlaySpotStatus(t._playSpot, PlaySpotStatus.Free); FreedSpots++; }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] free table spot error: {ex.Message}"); }
            try { if (c is NightclubCustomer nc && nc.danceSpot != null) { nc.ReleaseDanceSpot(); FreedDance++; } }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] free dance error: {ex.Message}"); }
            // Gym machine and cinema seat: their own tree clean-up frees them (UseWorkoutMachine.FinishWorkingOut,
            // WatchShow.Reset). Gym dirt: FinishWorkingOut also applies the machine's dirt here - left as is (see
            // the part-B report: dirt travels owner -> others only, so the two writes never meet in one state).
        }

        // ── B. Wants (taker) ─────────────────────────────────────────────────────────────────────────
        private sealed class Want
        {
            public string Id = "";
            public int Kind; public string Item = ""; public int Index = -1; public string Sub = ""; public float EndMin = -1f;
            public string QueueItem = "";
            public float At;
            public Customer? Body;
            public bool Reserved, Taken, QueueDone, Failed;
            public bool Used;    // gym: UseWorkoutMachine started (the want stays offered until then)
            public int Picks;    // gym: how often the pick handed the wanted machine out
            public SeatSpot? Seat;
            public PlaySpotsManager? Mgr; public Transform? PlaySpot;
        }

        private static readonly Dictionary<CustomerEntry, Want> _wants = new();
        internal static int WantsMade, WantsTaken, QWantsMade, QWantsTaken, Resumed;
        private static readonly Dictionary<string, int> _fallback = new();
        private static readonly Dictionary<string, string> _fallbackById = new();
        private static float _clampMax;   // how far the resume clamp moved an end time (game minutes), max

        // The oracle: what each adopted row held (id -> row), read by `custstate held`.
        private sealed class RowHeld { public int Kind; public string Item = ""; public int Index; public string Sub = ""; public float EndMin; public string QueueItem = ""; }
        private static readonly Dictionary<string, RowHeld> _rowHeld = new();
        private static readonly HashSet<string> _takenIds = new();
        private static string _bldg = "";

        /// <summary>A building change drops the old building's wants (their reservations are gone with its interior)
        /// and the oracle rows.</summary>
        internal static void OnBuilding(string addr)
        {
            try { if ((addr ?? "") != _bldg) { _bldg = addr ?? ""; Reset(); } } catch { }
        }

        internal static void Reset()
        {
            try
            {
                foreach (var w in new List<Want>(_wants.Values)) Unreserve(w);
                _wants.Clear(); _rowHeld.Clear(); _fallbackById.Clear(); _seatTable.Clear(); _takenIds.Clear();
                _ctx = null;
            }
            catch { }
        }

        private static Want? WantOf(Customer? c)
        {
            try
            {
                if (c == null || c.customerEntry == null) return null;
                return _wants.TryGetValue(c.customerEntry, out var w) ? w : null;
            }
            catch { return null; }
        }

        private static void Fallback(Want w, string reason)
        {
            try
            {
                w.Failed = true;
                _fallback[reason] = (_fallback.TryGetValue(reason, out var n) ? n : 0) + 1;
                _fallbackById[w.Id] = reason;
                Plugin.Logger.LogInfo($"[SeatPins] want {w.Id} ({KindName(w.Kind)} {Short(w.Item)}{(w.Index >= 0 ? "#" + w.Index : "")}{(w.Sub.Length > 0 ? "/" + w.Sub : "")}) not taken: {reason} - today's pick instead.");
            }
            catch { }
        }

        internal static string Short(string s) => string.IsNullOrEmpty(s) ? "-" : (s.Length > 8 ? s.Substring(0, 8) : s);

        /// <summary>Step B1, AdoptPuppetAsNative: BEFORE the spawn (the tree may tick inside Init).</summary>
        internal static void WantBefore(CustomerEntry entry, string id, CustomerVisitRow v)
        {
            try
            {
                if (entry == null || v == null || v.Leaving) return;
                int k = v.SeatKind;
                if (k < KSeat || k > KCinema) k = KNone;
                bool q = !string.IsNullOrEmpty(v.QueueItem);
                if (k == KNone && !q) return;
                Sweep();
                _wants[entry] = new Want
                {
                    Id = id ?? "", Kind = k, Item = v.SeatItem ?? "", Index = v.SeatIndex, Sub = v.SeatSub ?? "", EndMin = v.EndMin,
                    QueueItem = v.QueueItem ?? "", At = Time.unscaledTime, QueueDone = !q,
                };
                if (k != KNone) WantsMade++;
                if (q) QWantsMade++;
                if (_rowHeld.Count > 600) _rowHeld.Clear();
                _rowHeld[id ?? ""] = new RowHeld { Kind = k, Item = v.SeatItem ?? "", Index = v.SeatIndex, Sub = v.SeatSub ?? "", EndMin = v.EndMin, QueueItem = v.QueueItem ?? "" };
                _fallbackById.Remove(id ?? ""); _takenIds.Remove(id ?? "");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] want error: {ex.Message}"); }
        }

        /// <summary>A seat or machine want exists for this entry (the adoption skips its 0.75 s position hold:
        /// UseWorkoutMachine switches the navmesh agent off and snaps the body to the machine, and the hold's
        /// transform fallback would pull it off again).</summary>
        internal static bool HasSpotWant(CustomerEntry entry)
        {
            try { return entry != null && _wants.TryGetValue(entry, out var w) && w.Kind != KNone && !w.Failed; } catch { return false; }
        }

        /// <summary>Step B2, right after the spawn: hold the wanted spot so another adopted body's random pick
        /// cannot take it first. Table seat / casino table spot: marked taken the game's own way (seat occupied,
        /// spot Reserved) - their pick patches accept this reservation. Gym machine / slot chair: NOT marked (the
        /// game's walk-to tasks give up on a target that already reads taken); the pick patches skip them for
        /// everyone else instead. Cinema: the game's own reservation, which WatchShow hands back first.</summary>
        internal static void ReserveAfter(CustomerEntry entry, Customer c)
        {
            try
            {
                if (entry == null || c == null || !_wants.TryGetValue(entry, out var w)) return;
                w.Body = c;
                w.At = Time.unscaledTime;
                switch (w.Kind)
                {
                    case KSeat:
                    {
                        var table = FindItem(w.Item);
                        if (table == null || table.SeatSpots == null) { Fallback(w, "table missing"); break; }
                        if (w.Index < 0 || w.Index >= table.SeatSpots.Length) { Fallback(w, "seat index out of range"); break; }
                        var seat = table.SeatSpots[w.Index];
                        if (seat == null || !seat.IsAvailable) { Fallback(w, "seat taken"); break; }
                        seat.occupied = true;
                        var chair = seat.GetAttachedChair;
                        if (chair != null) chair.Occupied = true;
                        w.Seat = seat; w.Reserved = true;
                        break;
                    }
                    case KGym:
                    {
                        var m = FindItem(w.Item) as WorkoutMachineController;
                        if (m == null) { Fallback(w, "machine missing"); break; }
                        if (m.Occupied) { Fallback(w, "machine taken"); break; }
                        w.Reserved = true;
                        break;
                    }
                    case KSlot:
                    {
                        var ch = SlotChair(w.Item);
                        if (ch == null) { Fallback(w, "slot chair missing"); break; }
                        if (ch.Occupied) { Fallback(w, "slot chair taken"); break; }
                        w.Reserved = true;
                        break;
                    }
                    case KSpot:
                    {
                        PlaySpotsManager? mgr = null;
                        foreach (var m in AllPlaySpotManagers()) if (m != null && MgrItemId(m) == w.Item) { mgr = m; break; }
                        if (mgr == null) { Fallback(w, "casino table missing"); break; }
                        Transform? spot = null; PlaySpotStatus st = PlaySpotStatus.Free;
                        foreach (var kv in mgr._playSpotsOccupied) if (kv.Key && SpotName(kv.Key) == w.Sub) { spot = kv.Key; st = kv.Value; break; }
                        if (spot == null) { Fallback(w, "table spot missing"); break; }
                        if (st != PlaySpotStatus.Free) { Fallback(w, "table spot taken"); break; }
                        mgr.SetPlaySpotStatus(spot, PlaySpotStatus.Reserved);
                        w.Mgr = mgr; w.PlaySpot = spot; w.Reserved = true;
                        break;
                    }
                    case KCinema:
                    {
                        Transform? sp = null;
                        foreach (var t in CinemaTheaterHelper.SittingPositions)
                            if (t && SpotName(t) == w.Sub && IdOf(t.GetComponentInParent<ItemController>()) == w.Item) { sp = t; break; }
                        if (sp == null) { Fallback(w, "cinema seat missing"); break; }
                        if (!CinemaTheaterHelper.EnsureSittingPositionReserved(sp, c.tpc)) { Fallback(w, "cinema seat taken"); break; }
                        // WatchShow.OnStart -> TryReserveSittingPosition returns this tpc's reservation first: taken.
                        w.Reserved = true; w.Taken = true; WantsTaken++; _takenIds.Add(w.Id);
                        break;
                    }
                }
                if (w.Failed && w.QueueDone) _wants.Remove(entry);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] reserve error: {ex.Message}"); }
        }

        private static ItemController? SlotChair(string id)
        {
            try
            {
                var l = CasinoBusinessHelper._slotMachineChairs;
                if (l != null) foreach (var ch in l) if (ch != null && IdOf(ch) == id) return ch;
            }
            catch { }
            return null;
        }

        private static void Unreserve(Want w)
        {
            try
            {
                if (w == null || !w.Reserved || w.Taken) return;
                if (w.Kind == KSeat && w.Seat != null)
                {
                    w.Seat.occupied = false;
                    var chair = w.Seat.GetAttachedChair;
                    if (chair != null) chair.Occupied = false;
                }
                else if (w.Kind == KSpot && w.Mgr != null && w.PlaySpot != null)
                {
                    if (w.Mgr._playSpotsOccupied.TryGetValue(w.PlaySpot, out var st) && st == PlaySpotStatus.Reserved)
                        w.Mgr.SetPlaySpotStatus(w.PlaySpot, PlaySpotStatus.Free);
                }
                w.Reserved = false;
            }
            catch { }
        }

        /// <summary>The spawn was refused or the body walks straight out: no want.</summary>
        internal static void DropEntry(CustomerEntry entry, string reason)
        {
            try
            {
                if (entry == null || !_wants.TryGetValue(entry, out var w)) return;
                Unreserve(w);
                if (w.Kind != KNone && !w.Taken && !w.Failed) Fallback(w, "unused: " + reason);
                _wants.Remove(entry);
            }
            catch { }
        }

        internal static void Drop(Customer c, string reason)
        {
            try { if (c != null && c.customerEntry != null && _wants.Count > 0) DropEntry(c.customerEntry, reason); } catch { }
        }

        /// <summary>Lazy time-out: runs whenever a want is made or any customer picks a spot (exactly when a stale
        /// reservation could matter) and on the readout.</summary>
        internal static void Sweep()
        {
            try
            {
                if (_wants.Count == 0) return;
                float now = Time.unscaledTime;
                List<CustomerEntry>? gone = null;
                foreach (var kv in _wants)
                {
                    var w = kv.Value;
                    bool bodyGone = w.Body == null ? now - w.At > 5f : (!w.Body || !w.Body.isActiveAndEnabled);
                    if (bodyGone || now - w.At > WantTimeoutSeconds)
                    {
                        Unreserve(w);
                        if (w.Kind != KNone && !w.Taken && !w.Failed) Fallback(w, bodyGone ? "unused: body gone" : "unused: timeout");
                        (gone ??= new List<CustomerEntry>()).Add(kv.Key);
                    }
                }
                if (gone != null) foreach (var k in gone) _wants.Remove(k);
            }
            catch { }
        }

        private static void Finish(Want w)
        {
            try
            {
                if (w.QueueDone && w.Body != null && w.Body.customerEntry != null) _wants.Remove(w.Body.customerEntry);
            }
            catch { }
        }

        private static void MarkTaken(Want w)
        {
            if (w.Taken) return;
            w.Taken = true; WantsTaken++;
            try { _takenIds.Add(w.Id); } catch { }
            try
            {
                Plugin.Logger.LogInfo($"[SeatPins] want {w.Id} taken: {KindName(w.Kind)} {Short(w.Item)}{(w.Index >= 0 ? "#" + w.Index : "")}{(w.Sub.Length > 0 ? "/" + w.Sub : "")} (row end {(w.EndMin >= 0f ? w.EndMin.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "-")}).");
            }
            catch { }
            // Nothing to resume (walking at capture): spent now - except a gym want, which stays offered until the
            // workout STARTS (a walk that fails - the adoption's warp can leave the body off the mesh for a frame -
            // makes the tree pick again, and that pick must hand out the same machine).
            if (w.EndMin < 0f && w.Kind != KGym) Finish(w);
        }

        // ── Pick hooks (called from the patches) ─────────────────────────────────────────────────────
        /// <summary>Table seat: the reserved seat, or - if the tree ran before the reservation - the seat while free.</summary>
        internal static bool TryTakeSeat(Customer c, out SeatSpot? seat, out ItemController? table)
        {
            seat = null; table = null;
            Sweep();
            var w = WantOf(c);
            if (w == null || w.Kind != KSeat || w.Taken || w.Failed) return false;
            table = FindItem(w.Item);
            if (table == null || table.SeatSpots == null || w.Index < 0 || w.Index >= table.SeatSpots.Length) { Unreserve(w); Fallback(w, "table missing at pick"); return false; }
            var s = table.SeatSpots[w.Index];
            bool mine = w.Reserved && ReferenceEquals(s, w.Seat);
            bool ok = s != null && (mine || s.IsAvailable) && UsableTable(table, mine);
            if (!ok) { Unreserve(w); Fallback(w, "seat taken at pick"); return false; }
            seat = s;
            w.Reserved = true; w.Seat = s;
            MarkTaken(w);
            return true;
        }

        /// <summary>Fold L (2026-09-27): the game's own usability filter for a seat pick (BuildingManager
        /// .GetAllTablesWithSeatsAvailable :1969-1987 - a Table-type item of this interior with a free seat). A seat this
        /// body reserved counts as its free seat (ReserveAfter marked it occupied), so then only the Table type is checked.</summary>
        private static bool UsableTable(ItemController table, bool reservedByMe)
        {
            try
            {
                if (table == null || table.Item == null || (table.Item.type & BigAmbitions.Items.ItemType.Table) == 0) return false;
                if (reservedByMe) return true;
                var bm = InstanceBehavior<BuildingManager>.Instance;
                return bm != null && bm.GetAllTablesWithSeatsAvailable().Contains(table);
            }
            catch { return false; }
        }

        internal static WorkoutMachineController? TakeGym(Customer? c)
        {
            Sweep();
            var w = WantOf(c);
            if (w == null || w.Kind != KGym || w.Used || w.Failed) return null;
            var m = FindItem(w.Item) as WorkoutMachineController;
            if (m == null || m.Occupied || m.ItemInstance == null || !string.IsNullOrEmpty(m.ItemInstance.parentId)
                || ItemHelper.GetMissingRequirements(m.ItemInstance).Count > 0) { Fallback(w, "machine taken or unusable at pick"); return null; }
            if (++w.Picks > 4) { Fallback(w, "walk to the machine failed 4 times"); return null; }
            MarkTaken(w);
            return m;
        }

        internal static ItemController? TakeSlot(Customer? c)
        {
            Sweep();
            var w = WantOf(c);
            if (w == null || w.Kind != KSlot || w.Taken || w.Failed) return null;
            var ch = SlotChair(w.Item);
            if (ch == null || ch.Occupied || !(ch.parentItemController is SlotMachineController)) { Fallback(w, "slot chair taken at pick"); return null; }
            MarkTaken(w);
            return ch;
        }

        /// <summary>Ids of gym machines / slot chairs other customers' pending wants hold (skipped by everyone else's pick).</summary>
        internal static HashSet<string> ReservedIds(int kind, Customer? except)
        {
            var s = new HashSet<string>();
            try
            {
                foreach (var kv in _wants)
                {
                    var w = kv.Value;
                    if (w.Kind != kind || w.Failed || w.Used) continue;
                    bool active = kind == KGym ? (w.Reserved || w.Taken) : (w.Reserved && !w.Taken);   // a gym want holds its machine until the workout starts
                    if (!active) continue;
                    if (except != null && ReferenceEquals(w.Body, except)) continue;
                    if (!string.IsNullOrEmpty(w.Item)) s.Add(w.Item);
                }
            }
            catch { }
            return s;
        }

        internal static PlaySpotsManager? SpotManagerFor(Customer? c, CasinoGameType type)
        {
            Sweep();
            var w = WantOf(c);
            if (w == null || w.Kind != KSpot || w.Taken || w.Failed || w.Mgr == null) return null;
            try
            {
                var l = type == CasinoGameType.Blackjack ? CasinoBusinessHelper._blackjackTablePlaySpotsManagers : CasinoBusinessHelper._rouletteTablePlaySpotsManagers;
                if (l == null || !l.Contains(w.Mgr)) return null;   // the tree chose the other game: leave today's pick alone
            }
            catch { return null; }
            return w.Mgr;
        }

        internal static Transform? TakeSpot(Customer? c, PlaySpotsManager mgr)
        {
            var w = WantOf(c);
            if (w == null || w.Kind != KSpot || w.Taken || w.Failed || !ReferenceEquals(w.Mgr, mgr) || w.PlaySpot == null) return null;
            try
            {
                if (!mgr._playSpotsOccupied.TryGetValue(w.PlaySpot, out var st) || st == PlaySpotStatus.Occupied) { Unreserve(w); Fallback(w, "table spot taken at pick"); return null; }
            }
            catch { return null; }
            MarkTaken(w);
            return w.PlaySpot;
        }

        internal static WaitingLine? TakeLine(Customer? c, string[] names)
        {
            var w = WantOf(c);
            if (w == null || w.QueueDone || string.IsNullOrEmpty(w.QueueItem)) return null;
            w.QueueDone = true;
            try
            {
                foreach (var l in WaitingLinesHelper.GetAvailableWaitingLines(names))
                    if (l != null && IdOf(l.ItemController) == w.QueueItem && l.data != null && l.data.GetAmountOfSpotsAvailable() > 0)
                    {
                        QWantsTaken++;
                        if (w.Kind == KNone || w.Taken || w.Failed) _wants.Remove(c!.customerEntry);
                        return l;
                    }
            }
            catch { }
            _fallback["queue line full or missing"] = (_fallback.TryGetValue("queue line full or missing", out var n) ? n : 0) + 1;
            Plugin.Logger.LogInfo($"[SeatPins] want {w.Id} (queue {Short(w.QueueItem)}) not taken: line full or missing - today's pick instead.");
            if (w.Kind == KNone || w.Taken || w.Failed) { try { _wants.Remove(c!.customerEntry); } catch { } }
            return null;
        }

        /// <summary>Step B4: the end time to set on the activity just started, clamped to now..now+max; the want is
        /// then spent. False = no resume (no want, another kind, another item, or no end captured).</summary>
        internal static bool TryResume(Customer? c, int kind, string item, float max, out float end)
        {
            end = -1f;
            try
            {
                var w = WantOf(c);
                if (w == null || w.Kind != kind || !w.Taken || w.EndMin < 0f) return false;
                if (!string.IsNullOrEmpty(item) && item != w.Item) return false;
                float now = NowMin();
                end = Mathf.Clamp(w.EndMin, now, now + Math.Max(0f, max));
                float moved = Math.Abs(end - w.EndMin);
                if (moved > _clampMax) _clampMax = moved;
                Resumed++;
                Plugin.Logger.LogInfo($"[SeatPins] resumed {w.Id}: {KindName(kind)} ends {end.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} (row {w.EndMin.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}, now {now.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}).");
                w.EndMin = -1f;
                w.Used = true;
                Finish(w);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Gym: UseWorkoutMachine just started - resume the row's end when it is the wanted machine; either
        /// way the want is spent now.</summary>
        internal static bool GymStarted(Customer? c, string machineId, out float end)
        {
            end = -1f;
            try
            {
                var w = WantOf(c);
                if (w == null || w.Kind != KGym || !w.Taken || w.Used) return false;
                if (machineId == w.Item && w.EndMin >= 0f) return TryResume(c, KGym, machineId, MaxGym, out end);
                w.Used = true;
                if (machineId != w.Item) Plugin.Logger.LogInfo($"[SeatPins] want {w.Id} (gym {Short(w.Item)}): the workout started on {Short(machineId)} instead.");
                Finish(w);
            }
            catch { }
            return false;
        }

        // ── Pick context (the game's static pickers take no customer) ─────────────────────────────────
        [ThreadStatic] private static Customer? _ctxT;
        private static Customer? _ctx { get => _ctxT; set => _ctxT = value; }
        internal static void CtxSet(Customer? c) { _ctx = c; }
        internal static void CtxClear() { _ctx = null; }
        internal static Customer? Ctx => _ctx;

        internal static Customer? OwnerCustomer(BehaviorDesigner.Runtime.Tasks.Task t)
        {
            try { return t != null && t.Owner != null ? t.Owner.GetComponent<Customer>() : null; } catch { return null; }
        }

        // ── Readouts (DEV `custstate`) ───────────────────────────────────────────────────────────────
        internal static string HeldTag(Customer c)
        {
            try
            {
                var h = ReadHeld(c);
                string s = "";
                if (h.Kind != KNone)
                    s += $":held={h.Kind}/{Short(h.Item)}/{(h.Kind == KSeat ? h.Index.ToString() : (h.Sub.Length > 0 ? h.Sub : "-"))}/{(h.EndMin >= 0f ? h.EndMin.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "-1")}";
                if (h.QueueItem.Length > 0)
                {
                    int sp = -1; try { sp = c.currentWaitingLineSpot; } catch { }
                    s += $":q={Short(h.QueueItem)}/{sp}";
                }
                return s;
            }
            catch { return ""; }
        }

        /// <summary>`custstate held`: leak counts on THIS machine (spots marked taken that no live customer or
        /// pending want holds) and the taker's want tallies against the rows it adopted.</summary>
        internal static string HeldReport()
        {
            try
            {
                Sweep();
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var live = new List<Customer>();
                try { foreach (var c in IndoorCustomerSpawner.Customers) if (c != null && !c.isPlayer && c.isActiveAndEnabled) live.Add(c); } catch { }
                var liveSet = new HashSet<Customer>(live);
                var wantSeats = new HashSet<SeatSpot>(); var wantSpots = new HashSet<Transform>();
                foreach (var w in _wants.Values)
                {
                    if (w.Reserved && w.Seat != null) wantSeats.Add(w.Seat);
                    if (w.Reserved && w.PlaySpot != null) wantSpots.Add(w.PlaySpot);
                }
                // Seats
                var sat = new HashSet<SeatSpot>();
                foreach (var c in live) if (c.isSittingOn != null) sat.Add(c.isSittingOn);
                int leakSeats = 0, seatsTaken = 0;
                var bm = BM();
                if (bm != null && bm.allItemControllers != null)
                    foreach (var ic in bm.allItemControllers)
                    {
                        if (ic == null || ic.Item == null || (ic.Item.type & ItemType.Table) == 0 || ic.SeatSpots == null) continue;
                        foreach (var s in ic.SeatSpots)
                        {
                            if (s == null) continue;
                            bool taken = s.occupied;
                            try { var ch = s.GetAttachedChair; if (ch != null && ch.Occupied) taken = true; } catch { }
                            if (!taken) continue;
                            seatsTaken++;
                            if (!sat.Contains(s) && !wantSeats.Contains(s)) leakSeats++;
                        }
                    }
                // Slot chairs and casino table spots held by live tasks
                var liveChairs = new HashSet<ItemController>(); var liveSpots = new HashSet<Transform>();
                foreach (var c in live)
                {
                    foreach (var t in Tasks<SitInASlotMachine>(c)) if (t != null && t._slotMachineController != null && t._slotMachineChair != null) liveChairs.Add(t._slotMachineChair);
                    foreach (var t in Tasks<SitInASlotMachineInstantly>(c)) if (t != null && t._slotMachineController != null && t._slotMachineChair != null) liveChairs.Add(t._slotMachineChair);
                    foreach (var t in Tasks<GoPlayOnAGameSpot>(c)) if (t != null && t._playSpot != null) liveSpots.Add(t._playSpot);
                    foreach (var t in Tasks<GoPlayOnAGameSpotInstantly>(c)) if (t != null && t._playSpot != null) liveSpots.Add(t._playSpot);
                }
                int leakSlots = 0, leakSpots = 0, leakLine = 0, leakDance = 0;
                try
                {
                    var chairs = CasinoBusinessHelper._slotMachineChairs;
                    if (chairs != null) foreach (var ch in chairs) if (ch != null && ch.Occupied && !liveChairs.Contains(ch)) leakSlots++;
                }
                catch { }
                try
                {
                    foreach (var m in AllPlaySpotManagers())
                    {
                        if (m == null || m._playSpotsOccupied == null) continue;
                        foreach (var kv in m._playSpotsOccupied)
                            if (kv.Key && kv.Value != PlaySpotStatus.Free && kv.Key != m.playerSpot && !liveSpots.Contains(kv.Key) && !wantSpots.Contains(kv.Key)) leakSpots++;
                    }
                }
                catch { }
                try
                {
                    foreach (var wl in UnityEngine.Object.FindObjectsOfType<WaitingLine>())
                    {
                        if (wl == null || wl.data == null || wl.data.customers == null) continue;
                        foreach (var qc in wl.data.customers)
                            if (qc == null || (!qc.isPlayer && (!qc.isActiveAndEnabled || !liveSet.Contains(qc)))) leakLine++;
                    }
                }
                catch { }
                try
                {
                    var held = new HashSet<DanceSpot>();
                    foreach (var c in live) if (c is NightclubCustomer nc && nc.danceSpot != null) held.Add(nc.danceSpot);
                    var ds = NightclubBusinessHelper.DanceSpots;
                    if (ds != null) foreach (var d in ds) if (d != null && d.isOccupied && !held.Contains(d)) leakDance++;
                }
                catch { }
                // Taker: each adopted row with a held spot against what its body holds now.
                int rows = 0, same = 0, done = 0, pending = 0, fb = 0, mismatch = 0, qRows = 0, qSame = 0, endN = 0;
                float endMax = 0f;
                var bad = new List<string>();
                var byId = new Dictionary<string, Customer>();
                foreach (var c in live) { try { var id = CustomerPuppets.RowIdForCustomer(c); if (!string.IsNullOrEmpty(id)) byId[id] = c; } catch { } }
                float now = NowMin();
                foreach (var kv in _rowHeld)
                {
                    var r = kv.Value;
                    if (!byId.TryGetValue(kv.Key, out var c)) continue;   // gone (left / released): not judged
                    var h = ReadHeld(c);
                    if (r.QueueItem.Length > 0) { qRows++; if (h.QueueItem == r.QueueItem) qSame++; }
                    if (r.Kind == KNone) continue;
                    rows++;
                    bool eq = h.Kind == r.Kind && h.Item == r.Item && (r.Kind == KSeat ? h.Index == r.Index : (r.Kind == KGym || r.Kind == KSlot || h.Sub == r.Sub));
                    if (eq)
                    {
                        same++;
                        if (r.EndMin >= 0f && h.EndMin >= 0f) { endN++; float d = Math.Abs(h.EndMin - r.EndMin); if (d > endMax) endMax = d; }
                    }
                    else if (_fallbackById.ContainsKey(kv.Key)) fb++;
                    else if (_takenIds.Contains(kv.Key) && (r.EndMin < 0f || now >= r.EndMin - 0.5f || h.Kind == KNone)) done++;   // the pinned activity ran and the visit moved on
                    else if (r.EndMin >= 0f && now >= r.EndMin - 0.5f && h.Kind == KNone) done++;   // its end passed before the tree reached the pick
                    else if (h.Kind == KNone && WantPending(kv.Key)) pending++;
                    else { mismatch++; if (bad.Count < 8) bad.Add($"{kv.Key}:{r.Kind}/{Short(r.Item)}/{r.Index}>{h.Kind}/{Short(h.Item)}/{h.Index}"); }
                }
                var fbl = new List<string>();
                foreach (var f in _fallback) fbl.Add($"{Clean(f.Key)}:{f.Value}");
                return $"leakSeats={leakSeats} leakSlots={leakSlots} leakSpots={leakSpots} leakLine={leakLine} leakDance={leakDance} seatsTaken={seatsTaken} live={live.Count} "
                     + $"wantTaken={WantsTaken}/{WantsMade} qTaken={QWantsTaken}/{QWantsMade} wantsPending={_wants.Count} resumed={Resumed} "
                     + $"heldRows={rows} heldSame={same} heldDone={done} heldPending={pending} heldFallback={fb} heldMismatch={mismatch} qRows={qRows} qSame={qSame} "
                     + $"endN={endN} endDelta={endMax.ToString("F2", inv)} clampMax={_clampMax.ToString("F2", inv)} "
                     + $"freed={FreedSeats}/{FreedLines}/{FreedSlots}/{FreedSpots}/{FreedDance} fallback={(fbl.Count > 0 ? string.Join(",", fbl) : "-")} bad={(bad.Count > 0 ? string.Join(",", bad) : "-")}";
            }
            catch (Exception ex) { return "ERR held " + ex.Message; }
        }

        private static bool WantPending(string id)
        {
            foreach (var w in _wants.Values) if (w.Id == id && !w.Taken && !w.Failed) return true;
            return false;
        }
    }

    // ── Patches: release / leave clear a want ─────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(Customer), nameof(Customer.ReleaseCustomer))]
    public static class Patch_Customer_ReleaseCustomer_SeatPins
    {
        static void Prefix(Customer __instance)
        {
            try { if (__instance != null && !__instance.isPlayer) CustomerSeatPins.Drop(__instance, "released"); } catch { }
        }
    }

    [HarmonyPatch(typeof(Customer), nameof(Customer.Leave))]
    public static class Patch_Customer_Leave_SeatPins
    {
        static void Prefix(Customer __instance)
        {
            try { if (__instance != null && !__instance.isPlayer) CustomerSeatPins.Drop(__instance, "left"); } catch { }
        }
    }

    // ── B3 picks: table seats (the game's five assignments, SitInASeat.cs:112-124) ────────────────────
    [HarmonyPatch(typeof(SitInASeat), "GetSeatSpot")]
    public static class Patch_SitInASeat_GetSeatSpot_SeatPins
    {
        static bool Prefix(SitInASeat __instance)
        {
            try
            {
                var c = __instance.sharedCustomer != null ? __instance.sharedCustomer.Value : null;
                if (c == null || !CustomerSeatPins.TryTakeSeat(c, out var seat, out var table) || seat == null) return true;
                // Fold L: everything that can throw first; the seat is marked taken last (a throw falls back to the
                // native pick with nothing half-set).
                var chair = seat.GetAttachedChair;
                __instance._seatItemController = chair != null ? chair : table;
                __instance._seatSpot = seat;
                if (chair != null) chair.Occupied = true;
                seat.occupied = true;
                c.isSittingOn = seat;
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] seat pick error: {ex.Message}"); return true; }
        }
    }

    [HarmonyPatch(typeof(SitInASeatInstantly), "GetSeatSpot")]
    public static class Patch_SitInASeatInstantly_GetSeatSpot_SeatPins
    {
        static bool Prefix(SitInASeatInstantly __instance)
        {
            try
            {
                var c = __instance.sharedCustomer != null ? __instance.sharedCustomer.Value : null;
                if (c == null || !CustomerSeatPins.TryTakeSeat(c, out var seat, out var table) || seat == null) return true;
                // Fold L: everything that can throw first; the seat is marked taken last (a throw falls back to the
                // native pick with nothing half-set).
                var chair = seat.GetAttachedChair;
                __instance._seatItemController = chair != null ? chair : table;
                __instance._seatSpot = seat;
                if (chair != null) chair.Occupied = true;
                seat.occupied = true;
                c.isSittingOn = seat;
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] seat pick error: {ex.Message}"); return true; }
        }
    }

    [HarmonyPatch(typeof(FullServiceSitInASeat), "GetSeatSpot")]
    public static class Patch_FullServiceSitInASeat_GetSeatSpot_SeatPins
    {
        static bool Prefix(FullServiceSitInASeat __instance)
        {
            try
            {
                var c = __instance.sharedCustomer != null ? __instance.sharedCustomer.Value : null;
                if (c == null || !CustomerSeatPins.TryTakeSeat(c, out var seat, out var table) || seat == null) return true;
                // Fold L: everything that can throw first; the seat is marked taken last (a throw falls back to the
                // native pick with nothing half-set).
                var chair = seat.GetAttachedChair;
                __instance._seatItemController = chair != null ? chair : table;
                __instance._seatSpot = seat;
                if (chair != null) chair.Occupied = true;
                seat.occupied = true;
                c.isSittingOn = seat;
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] seat pick error: {ex.Message}"); return true; }
        }
    }

    // ── B4 resume: table seats ────────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(SitInASeat), "StartWaitingTimer")]
    public static class Patch_SitInASeat_StartWaitingTimer_SeatPins
    {
        static void Postfix(SitInASeat __instance)
        {
            try
            {
                var c = __instance.sharedCustomer != null ? __instance.sharedCustomer.Value : null;
                if (CustomerSeatPins.TryResume(c, CustomerSeatPins.KSeat, "", __instance.maxSittingTime, out float end))
                    __instance._stopWaitingTimeStamp = new Timestamp(end);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] seat resume error: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(SitInASeatInstantly), "StartWaitingTimer")]
    public static class Patch_SitInASeatInstantly_StartWaitingTimer_SeatPins
    {
        static void Postfix(SitInASeatInstantly __instance)
        {
            try
            {
                var c = __instance.sharedCustomer != null ? __instance.sharedCustomer.Value : null;
                if (CustomerSeatPins.TryResume(c, CustomerSeatPins.KSeat, "", __instance.maxSittingTime, out float end))
                    __instance._stopWaitingTimeStamp = new Timestamp(end);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] seat resume error: {ex.Message}"); }
        }
    }

    /// <summary>Full service: the visit ends at prep end + _sittingTime (food, then the ice-cream share), so the
    /// remaining time after the 1-2 min prep the game just rolled becomes the new sitting time.</summary>
    [HarmonyPatch(typeof(FullServiceSitInASeat), "OnSeatReached")]
    public static class Patch_FullServiceSitInASeat_OnSeatReached_SeatPins
    {
        static void Postfix(FullServiceSitInASeat __instance)
        {
            try
            {
                var c = __instance.sharedCustomer != null ? __instance.sharedCustomer.Value : null;
                if (!CustomerSeatPins.TryResume(c, CustomerSeatPins.KSeat, "", 18f + 2f, out float end)) return;
                float now = TimeHelper.NowInMinutes();
                float prepEnd = __instance._stopActionTimeStamp != null ? __instance._stopActionTimeStamp.GetTotalMinutes() : now;
                float sit;
                if (end <= prepEnd)
                {
                    __instance._stopActionTimeStamp = new Timestamp(Math.Max(now, end));
                    sit = 0.05f;
                }
                else sit = Mathf.Clamp(end - prepEnd, 0.05f, 18f);
                __instance._sittingTime = sit;
                __instance._eatingFoodTime = __instance._hasIceCream ? sit * 0.5f : sit;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] full-service resume error: {ex.Message}"); }
        }
    }

    // ── Gym ──────────────────────────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(GetWorkoutMachine), nameof(GetWorkoutMachine.OnStart))]
    public static class Patch_GetWorkoutMachine_OnStart_SeatPins
    {
        static void Assign(GetWorkoutMachine t, WorkoutMachineController m)
        {
            t.sharedItemController.Value = m;
            t.sharedWorkoutMachineController.Value = m;
            t.chosenWorkoutType.Value = m.GetWorkoutExercise().workoutType;
            t.workoutTypesDone.Value.Add(m.GetWorkoutExercise().workoutType);
        }

        static bool Prefix(GetWorkoutMachine __instance)
        {
            try
            {
                var c = CustomerSeatPins.OwnerCustomer(__instance);
                var want = CustomerSeatPins.TakeGym(c);
                if (want != null) { Assign(__instance, want); return false; }
                var reserved = CustomerSeatPins.ReservedIds(CustomerSeatPins.KGym, c);
                if (reserved.Count == 0) return true;
                // Today's pick (GetWorkoutMachine.OnStart) minus the machines other adopted customers are resuming.
                var bm = InstanceBehavior<BuildingManager>.Instance;
                var src = new List<WorkoutMachineController>();
                foreach (var x in bm.allItemControllers)
                    if (x != null && x.Item != null && (x.Item.type & ItemType.WorkoutMachine) != 0 && !x.Occupied && x.ItemInstance != null
                        && string.IsNullOrEmpty(x.ItemInstance.parentId) && ItemHelper.GetMissingRequirements(x.ItemInstance).Count <= 0
                        && x is WorkoutMachineController wm && !reserved.Contains(x.ItemInstance.id ?? ""))
                        src.Add(wm);
                var doneTypes = __instance.workoutTypesDone.Value;
                var pool = new List<WorkoutMachineController>();
                foreach (var x in src) if (doneTypes == null || !doneTypes.Contains(x.GetWorkoutExercise().workoutType)) pool.Add(x);
                WorkoutMachineController? pick = pool.Count > 0 ? pool[UnityEngine.Random.Range(0, pool.Count)] : null;
                if (pick == null)
                {
                    var p2 = new List<WorkoutMachineController>();
                    foreach (var x in src) if (x != __instance.sharedItemController.Value) p2.Add(x);
                    pick = p2.Count > 0 ? p2[UnityEngine.Random.Range(0, p2.Count)] : null;
                }
                if (pick == null) { __instance.sharedItemController.Value = null; __instance.sharedWorkoutMachineController.Value = null; return false; }
                Assign(__instance, pick);
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] gym pick error: {ex.Message}"); return true; }
        }
    }

    [HarmonyPatch(typeof(UseWorkoutMachine), nameof(UseWorkoutMachine.OnStart))]
    public static class Patch_UseWorkoutMachine_OnStart_SeatPins
    {
        static void Postfix(UseWorkoutMachine __instance)
        {
            try
            {
                var c = __instance.sharedCustomer != null ? __instance.sharedCustomer.Value : null;
                var m = __instance.sharedWorkoutMachineController != null ? __instance.sharedWorkoutMachineController.Value : null;
                string id = m != null && m.ItemInstance != null ? m.ItemInstance.id ?? "" : "";
                if (id.Length > 0 && CustomerSeatPins.GymStarted(c, id, out float end))
                    __instance._endTime = new Timestamp(end);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] gym resume error: {ex.Message}"); }
        }
    }

    // ── Casino slots: the picker is static, so OnStart carries the customer in ─────────────────────────
    [HarmonyPatch(typeof(SitInASlotMachine), nameof(SitInASlotMachine.OnStart))]
    public static class Patch_SitInASlotMachine_OnStart_SeatPins
    {
        static void Prefix(SitInASlotMachine __instance) { try { CustomerSeatPins.CtxSet(__instance.sharedCustomer?.Value); } catch { } }
        static void Finalizer() { CustomerSeatPins.CtxClear(); }
    }

    [HarmonyPatch(typeof(SitInASlotMachineInstantly), nameof(SitInASlotMachineInstantly.OnStart))]
    public static class Patch_SitInASlotMachineInstantly_OnStart_SeatPins
    {
        static void Prefix(SitInASlotMachineInstantly __instance) { try { CustomerSeatPins.CtxSet(__instance.sharedCustomer?.Value); } catch { } }
        static void Finalizer() { CustomerSeatPins.CtxClear(); }
    }

    [HarmonyPatch(typeof(CasinoBusinessHelper), nameof(CasinoBusinessHelper.GetRandomMachineSlotChair))]
    public static class Patch_Casino_GetRandomMachineSlotChair_SeatPins
    {
        static bool Prefix(ref ItemController? __result)
        {
            try
            {
                var c = CustomerSeatPins.Ctx;
                if (c == null) return true;
                var want = CustomerSeatPins.TakeSlot(c);
                if (want != null) { __result = want; return false; }
                var reserved = CustomerSeatPins.ReservedIds(CustomerSeatPins.KSlot, c);
                if (reserved.Count == 0) return true;
                var all = CasinoBusinessHelper._slotMachineChairs;
                int free = 0;
                var pool = new List<ItemController>();
                foreach (var ch in all)
                {
                    if (ch == null || ch.Occupied) continue;
                    free++;
                    if (!reserved.Contains(ch.ItemInstance != null ? ch.ItemInstance.id ?? "" : "")) pool.Add(ch);
                }
                __result = free > 1 && pool.Count > 0 ? pool[UnityEngine.Random.Range(0, pool.Count)] : null;   // the game never hands out the last free chair
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] slot pick error: {ex.Message}"); return true; }
        }
    }

    [HarmonyPatch(typeof(SitInASlotMachine), "OnPlaySpotReached")]
    public static class Patch_SitInASlotMachine_OnPlaySpotReached_SeatPins
    {
        static void Postfix(SitInASlotMachine __instance)
        {
            try
            {
                var c = __instance.sharedCustomer != null ? __instance.sharedCustomer.Value : null;
                string id = __instance._slotMachineChair != null && __instance._slotMachineChair.ItemInstance != null ? __instance._slotMachineChair.ItemInstance.id ?? "" : "";
                if (id.Length > 0 && CustomerSeatPins.TryResume(c, CustomerSeatPins.KSlot, id, 32f, out float end))
                    __instance._stopPlayingTimestamp = new Timestamp(end);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] slot resume error: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(SitInASlotMachineInstantly), "OnPlaySpotReached")]
    public static class Patch_SitInASlotMachineInstantly_OnPlaySpotReached_SeatPins
    {
        static void Postfix(SitInASlotMachineInstantly __instance)
        {
            try
            {
                var c = __instance.sharedCustomer != null ? __instance.sharedCustomer.Value : null;
                string id = __instance._slotMachineChair != null && __instance._slotMachineChair.ItemInstance != null ? __instance._slotMachineChair.ItemInstance.id ?? "" : "";
                if (id.Length > 0 && CustomerSeatPins.TryResume(c, CustomerSeatPins.KSlot, id, 32f, out float end))
                    __instance._stopPlayingTimestamp = new Timestamp(end);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] slot resume error: {ex.Message}"); }
        }
    }

    // ── Casino tables (blackjack / roulette) ─────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(GoPlayOnAGameSpot), nameof(GoPlayOnAGameSpot.OnStart))]
    public static class Patch_GoPlayOnAGameSpot_OnStart_SeatPins
    {
        static void Prefix(GoPlayOnAGameSpot __instance) { try { CustomerSeatPins.CtxSet(__instance.sharedCustomer?.Value); } catch { } }
        static void Finalizer() { CustomerSeatPins.CtxClear(); }
    }

    [HarmonyPatch(typeof(GoPlayOnAGameSpotInstantly), nameof(GoPlayOnAGameSpotInstantly.OnStart))]
    public static class Patch_GoPlayOnAGameSpotInstantly_OnStart_SeatPins
    {
        static void Prefix(GoPlayOnAGameSpotInstantly __instance) { try { CustomerSeatPins.CtxSet(__instance.sharedCustomer?.Value); } catch { } }

        static void Postfix(GoPlayOnAGameSpotInstantly __instance)
        {
            try
            {
                var c = __instance.sharedCustomer != null ? __instance.sharedCustomer.Value : null;
                if (!__instance._canMoveToPlaySpot || __instance._stopPlayingTimestamp == null) return;
                string id = "";
                try { id = __instance._gamePlaySpotsManager != null ? (__instance._gamePlaySpotsManager.GetComponentInParent<ItemController>()?.ItemInstance?.id ?? "") : ""; } catch { }
                if (id.Length > 0 && CustomerSeatPins.TryResume(c, CustomerSeatPins.KSpot, id, 32f, out float end))
                    __instance._stopPlayingTimestamp = new Timestamp(end);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] table spot resume error: {ex.Message}"); }
        }

        static void Finalizer() { CustomerSeatPins.CtxClear(); }
    }

    [HarmonyPatch(typeof(CasinoBusinessHelper), nameof(CasinoBusinessHelper.GetRandomCasinoGamePlaySpotsManager))]
    public static class Patch_Casino_GetRandomPlaySpotsManager_SeatPins
    {
        static bool Prefix(CasinoGameType casinoGameType, ref PlaySpotsManager? __result)
        {
            try
            {
                var c = CustomerSeatPins.Ctx;
                if (c == null) return true;
                var m = CustomerSeatPins.SpotManagerFor(c, casinoGameType);
                if (m == null) return true;
                __result = m;
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] table pick error: {ex.Message}"); return true; }
        }
    }

    [HarmonyPatch(typeof(PlaySpotsManager), nameof(PlaySpotsManager.GetFreeSpot))]
    public static class Patch_PlaySpotsManager_GetFreeSpot_SeatPins
    {
        static bool Prefix(PlaySpotsManager __instance, ref Transform? __result)
        {
            try
            {
                var c = CustomerSeatPins.Ctx;
                if (c == null) return true;
                var t = CustomerSeatPins.TakeSpot(c, __instance);
                if (t == null) return true;
                __result = t;
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] table spot pick error: {ex.Message}"); return true; }
        }
    }

    [HarmonyPatch(typeof(GoPlayOnAGameSpot), "OnRotatingFinished")]
    public static class Patch_GoPlayOnAGameSpot_OnRotatingFinished_SeatPins
    {
        static void Postfix(GoPlayOnAGameSpot __instance)
        {
            try
            {
                var c = __instance.sharedCustomer != null ? __instance.sharedCustomer.Value : null;
                string id = "";
                try { id = __instance._gamePlaySpotsManager != null ? (__instance._gamePlaySpotsManager.GetComponentInParent<ItemController>()?.ItemInstance?.id ?? "") : ""; } catch { }
                if (id.Length > 0 && CustomerSeatPins.TryResume(c, CustomerSeatPins.KSpot, id, 32f, out float end))
                    __instance._stopPlayingTimestamp = new Timestamp(end);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] table spot resume error: {ex.Message}"); }
        }
    }

    // ── Queue lines (till, coat check, hairdresser chair): the same line when it has room ─────────────
    [HarmonyPatch(typeof(CustomerJoinQueue), "StartJoiningWaitingLine")]
    public static class Patch_CustomerJoinQueue_Start_SeatPins
    {
        static void Prefix(CustomerJoinQueue __instance) { try { CustomerSeatPins.CtxSet(__instance.sharedCustomer?.Value); } catch { } }
        static void Finalizer() { CustomerSeatPins.CtxClear(); }
    }

    [HarmonyPatch(typeof(CustomerJoinQueueInstantly), "StartJoiningWaitingLineInstantly")]
    public static class Patch_CustomerJoinQueueInstantly_Start_SeatPins
    {
        static void Prefix(CustomerJoinQueueInstantly __instance) { try { CustomerSeatPins.CtxSet(__instance.sharedCustomer?.Value); } catch { } }
        static void Finalizer() { CustomerSeatPins.CtxClear(); }
    }

    [HarmonyPatch(typeof(WaitingLinesHelper), nameof(WaitingLinesHelper.GetLessCrowdedWaitingLine))]
    public static class Patch_WaitingLinesHelper_GetLessCrowded_SeatPins
    {
        static bool Prefix(string[] controllerNames, ref WaitingLine? __result)
        {
            try
            {
                var c = CustomerSeatPins.Ctx;
                if (c == null) return true;
                var l = CustomerSeatPins.TakeLine(c, controllerNames);
                if (l == null) return true;
                __result = l;
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SeatPins] queue pick error: {ex.Message}"); return true; }
        }
    }
}
