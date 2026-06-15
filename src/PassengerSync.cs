using System.Collections.Generic;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Host-authoritative passenger ("ride shotgun") state: per-vehicle lock + multi-seat
    /// occupancy, vehicle ownership, and board eligibility. This is the netcode backbone —
    /// the gameplay (board CTA on a ghost, walk-to-door, pin-to-ghost, camera, the in-car UI)
    /// lives in the ride/UI layer and consumes this state. See docs/PASSENGER-SYSTEM.md.
    ///
    /// Seats: 1 = front passenger (shotgun), 2..N = rear. Boarding fills the lowest free seat,
    /// so it goes front-passenger-first then rear. Seat COUNT is per vehicle TYPE — authored
    /// later (the wheel/NavmeshTarget data gives the door positions); default 1 until then.
    /// The OWNER is never a passenger: on every machine their own car is the real drivable
    /// vehicle (native enter flow); only OTHER players' cars are ghosts, and the "Ride" CTA
    /// only appears on a ghost — so passenger logic only ever runs for non-owners.
    /// </summary>
    public static class PassengerSync
    {
        private const int DefaultPassengerSeats = 1;   // until the per-type seat-count table is authored

        // vehicleId → owner playerId (HOST builds this from every player's VehicleSync fleet).
        private static readonly Dictionary<string, string> _ownerOf = new();
        // vehicleId → locked (owner refuses NEW passengers; never affects the owner or exits).
        private static readonly Dictionary<string, bool> _locked = new();
        // vehicleId → passenger-seat count (authored per type; default 1).
        private static readonly Dictionary<string, int> _seatCount = new();
        // vehicleId → (seat → rider playerId).
        private static readonly Dictionary<string, Dictionary<int, string>> _seatsOf = new();
        // rider playerId → (vehicleId, seat) — reverse index for clean exits/disconnects.
        private static readonly Dictionary<string, (string vid, int seat)> _rideOf = new();

        /// <summary>The vehicle the LOCAL player is currently riding ("" = on foot).</summary>
        public static string LocalRidingVehicleId { get; private set; } = "";
        /// <summary>The LOCAL player's seat while riding (-1 = on foot). 1 = shotgun, 2.. = rear.</summary>
        public static int LocalSeat { get; private set; } = -1;

        /// <summary>Clear all state — host shutdown / new game.</summary>
        public static void Reset()
        {
            _ownerOf.Clear();
            _locked.Clear();
            _seatCount.Clear();
            _seatsOf.Clear();
            _rideOf.Clear();
            LocalRidingVehicleId = "";
            LocalSeat = -1;
        }

        // ── Ownership (host) ──────────────────────────────────────────────────
        /// <summary>HOST: record which player owns these vehicle ids (from a fleet sync).</summary>
        public static void NoteFleet(string ownerId, IEnumerable<string> vehicleIds)
        {
            if (string.IsNullOrEmpty(ownerId) || vehicleIds == null) return;
            foreach (var id in vehicleIds)
                if (!string.IsNullOrEmpty(id)) _ownerOf[id] = ownerId;
        }

        public static string OwnerOf(string vehicleId)
            => (vehicleId != null && _ownerOf.TryGetValue(vehicleId, out var o)) ? o : "";

        // ── Seat count (per type; authored later, default 1) ──────────────────
        public static void SetSeatCount(string vehicleId, int seats)
        {
            if (!string.IsNullOrEmpty(vehicleId) && seats > 0) _seatCount[vehicleId] = seats;
        }

        public static int SeatCount(string vehicleId)
            => (vehicleId != null && _seatCount.TryGetValue(vehicleId, out var n) && n > 0) ? n : DefaultPassengerSeats;

        // ── Lock (authoritative state, mirrored everywhere) ───────────────────
        public static bool IsLocked(string vehicleId)
            => vehicleId != null && _locked.TryGetValue(vehicleId, out var l) && l;

        public static void SetLock(string vehicleId, bool locked)
        {
            if (string.IsNullOrEmpty(vehicleId)) return;
            _locked[vehicleId] = locked;
        }

        // ── Occupancy ─────────────────────────────────────────────────────────
        public static bool IsRiding(string playerId)
            => !string.IsNullOrEmpty(playerId) && _rideOf.ContainsKey(playerId);

        /// <summary>Seat → rider map for a vehicle (null if nobody aboard).</summary>
        public static IReadOnlyDictionary<int, string>? RidersOf(string vehicleId)
            => (vehicleId != null && _seatsOf.TryGetValue(vehicleId, out var d)) ? d : null;

        /// <summary>Apply an authoritative board (host-approved). A player rides at most one
        /// seat, so any prior seat is released first.</summary>
        public static void ApplyBoard(string vehicleId, string playerId, int seat)
        {
            if (string.IsNullOrEmpty(vehicleId) || string.IsNullOrEmpty(playerId) || seat < 0) return;
            ApplyExit(playerId);
            if (!_seatsOf.TryGetValue(vehicleId, out var seats)) { seats = new Dictionary<int, string>(); _seatsOf[vehicleId] = seats; }
            seats[seat] = playerId;
            _rideOf[playerId] = (vehicleId, seat);
            if (playerId == MPConfig.PlayerId) { LocalRidingVehicleId = vehicleId; LocalSeat = seat; }
        }

        /// <summary>Apply an authoritative exit (rider left / kicked / disconnected). Always
        /// allowed — a passenger can leave even a locked vehicle.</summary>
        public static void ApplyExit(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            if (_rideOf.TryGetValue(playerId, out var r))
            {
                _rideOf.Remove(playerId);
                if (_seatsOf.TryGetValue(r.vid, out var seats))
                {
                    if (seats.TryGetValue(r.seat, out var p) && p == playerId) seats.Remove(r.seat);
                    if (seats.Count == 0) _seatsOf.Remove(r.vid);
                }
            }
            if (playerId == MPConfig.PlayerId) { LocalRidingVehicleId = ""; LocalSeat = -1; }
        }

        // ── Join replay (full snapshot — see ANTIPATTERNS Class 4) ────────────
        /// <summary>HOST: build the full lock + seat state for a connecting peer.</summary>
        public static PassengerSnapshotPayload BuildSnapshot()
        {
            var snap = new PassengerSnapshotPayload();
            foreach (var kv in _locked)
                if (kv.Value) snap.Locks.Add(new PassengerLockEntry { VehicleId = kv.Key, Locked = true });
            foreach (var veh in _seatsOf)
                foreach (var s in veh.Value)
                    snap.Seats.Add(new PassengerSeatEntry { VehicleId = veh.Key, Seat = s.Key, PlayerId = s.Value });
            return snap;
        }

        /// <summary>JOINER: replace lock + occupancy with the host's authoritative snapshot.</summary>
        public static void ApplySnapshot(PassengerSnapshotPayload snap)
        {
            if (snap == null) return;
            _locked.Clear();
            _seatsOf.Clear();
            _rideOf.Clear();
            LocalRidingVehicleId = "";
            LocalSeat = -1;
            if (snap.Locks != null)
                foreach (var l in snap.Locks) if (l != null) SetLock(l.VehicleId, l.Locked);
            if (snap.Seats != null)
                foreach (var s in snap.Seats) if (s != null) ApplyBoard(s.VehicleId, s.PlayerId, s.Seat);
        }

        // ── Host eligibility ──────────────────────────────────────────────────
        /// <summary>HOST: may <paramref name="requesterPid"/> board <paramref name="vehicleId"/>?
        /// Assigns the lowest free passenger seat (1 = front shotgun, 2..N = rear), so seating
        /// fills front-passenger-first then rear. On failure sets <paramref name="reason"/>
        /// (shown to the requester, e.g. the "door locked" popup). The owner is rejected (they
        /// drive their own car natively) and a locked vehicle refuses new boards.</summary>
        public static bool HostCanBoard(string vehicleId, string requesterPid, out int seat, out string reason)
        {
            seat = -1;
            reason = "";
            string owner = OwnerOf(vehicleId);
            if (string.IsNullOrEmpty(owner))   { reason = "vehicle unknown"; return false; }
            if (owner == requesterPid)         { reason = "your own vehicle"; return false; }
            if (IsLocked(vehicleId))            { reason = "door locked"; return false; }

            int count = SeatCount(vehicleId);
            _seatsOf.TryGetValue(vehicleId, out var seats);
            for (int s = 1; s <= count; s++)
            {
                if (seats == null || !seats.ContainsKey(s)) { seat = s; return true; }
            }
            reason = "vehicle full";
            return false;
        }
    }
}
