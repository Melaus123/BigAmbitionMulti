using System.Collections.Generic;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Host-authoritative passenger ("ride shotgun") state: per-vehicle lock + single-seat
    /// occupancy, vehicle ownership, and board eligibility. This is the netcode backbone —
    /// the gameplay (board CTA, walk-to-door, pin-to-ghost, camera, the in-car UI) lives in
    /// the ride/UI layer and consumes this state. See docs/PASSENGER-SYSTEM.md.
    ///
    /// MVP: one passenger seat per vehicle (shotgun). Multi-seat comes later (P3).
    /// </summary>
    public static class PassengerSync
    {
        // vehicleId → owner playerId. The HOST builds this from every player's VehicleSync
        // fleet, so it can answer "who owns V?" when validating a board request.
        private static readonly Dictionary<string, string> _ownerOf = new();
        // vehicleId → locked (owner refuses NEW passengers; never affects owner or exits).
        private static readonly Dictionary<string, bool> _locked = new();
        // vehicleId → rider playerId (MVP: at most one).
        private static readonly Dictionary<string, string> _riderOf = new();
        // rider playerId → vehicleId (reverse index for clean exits/disconnects).
        private static readonly Dictionary<string, string> _vehicleOfRider = new();

        /// <summary>The vehicle the LOCAL player is currently riding ("" = on foot).</summary>
        public static string LocalRidingVehicleId { get; private set; } = "";

        /// <summary>Clear all state — host shutdown / new game.</summary>
        public static void Reset()
        {
            _ownerOf.Clear();
            _locked.Clear();
            _riderOf.Clear();
            _vehicleOfRider.Clear();
            LocalRidingVehicleId = "";
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

        // ── Lock (authoritative state, mirrored everywhere) ───────────────────
        public static bool IsLocked(string vehicleId)
            => vehicleId != null && _locked.TryGetValue(vehicleId, out var l) && l;

        public static void SetLock(string vehicleId, bool locked)
        {
            if (string.IsNullOrEmpty(vehicleId)) return;
            _locked[vehicleId] = locked;
        }

        // ── Occupancy ─────────────────────────────────────────────────────────
        public static string RiderOf(string vehicleId)
            => (vehicleId != null && _riderOf.TryGetValue(vehicleId, out var r)) ? r : "";

        public static bool IsRiding(string playerId)
            => !string.IsNullOrEmpty(playerId) && _vehicleOfRider.ContainsKey(playerId);

        /// <summary>Apply an authoritative board (host-approved). A player rides at most one
        /// vehicle, so any prior seat is released first.</summary>
        public static void ApplyBoard(string vehicleId, string playerId, int seat)
        {
            if (string.IsNullOrEmpty(vehicleId) || string.IsNullOrEmpty(playerId) || seat < 0) return;
            ApplyExit(playerId);
            _riderOf[vehicleId] = playerId;
            _vehicleOfRider[playerId] = vehicleId;
            if (playerId == MPConfig.PlayerId) LocalRidingVehicleId = vehicleId;
        }

        /// <summary>Apply an authoritative exit (rider left / kicked / disconnected). Always
        /// allowed — a passenger can leave even a locked vehicle.</summary>
        public static void ApplyExit(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            if (_vehicleOfRider.TryGetValue(playerId, out var vid))
            {
                _vehicleOfRider.Remove(playerId);
                if (_riderOf.TryGetValue(vid, out var r) && r == playerId) _riderOf.Remove(vid);
            }
            if (playerId == MPConfig.PlayerId) LocalRidingVehicleId = "";
        }

        // ── Host eligibility ──────────────────────────────────────────────────
        /// <summary>HOST: may <paramref name="requesterPid"/> board <paramref name="vehicleId"/>?
        /// On success sets <paramref name="seat"/> (≥0); on failure sets <paramref name="reason"/>
        /// (shown to the requester, e.g. the "door locked" popup). The owner is never a valid
        /// requester (they drive their own car), and a locked vehicle refuses new boards.</summary>
        public static bool HostCanBoard(string vehicleId, string requesterPid, out int seat, out string reason)
        {
            seat = -1;
            reason = "";
            string owner = OwnerOf(vehicleId);
            if (string.IsNullOrEmpty(owner))                 { reason = "vehicle unknown"; return false; }
            if (owner == requesterPid)                       { reason = "your own vehicle"; return false; }
            if (IsLocked(vehicleId))                          { reason = "door locked"; return false; }
            if (!string.IsNullOrEmpty(RiderOf(vehicleId)))    { reason = "seat taken"; return false; }
            seat = 1;   // MVP: single shotgun seat
            return true;
        }
    }
}
