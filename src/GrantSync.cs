using System.Collections.Generic;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Host-authoritative player-to-player ACCESS GRANTS — "I gave player P a key to my stuff."
    /// Phase 1 covers VEHICLES: a granted player bypasses a vehicle's passenger lock for riding +
    /// cargo, exactly like holding the owner's keys (see docs/PERMISSIONS-SYSTEM.md). Housing +
    /// business permissions will reuse this same table.
    ///
    /// TWO representations, on purpose:
    ///   • RUNTIME table (_grants): live PlayerId → grantee PlayerIds. Replicated read-everywhere
    ///     (PermissionSnapshot), and the ONLY thing the enforcement checks read (HostCanBoard,
    ///     VehicleStorageSync.OwnerApply, the ride gate). It holds only ONLINE relationships —
    ///     which is all enforcement ever needs (an offline grantee isn't here to board anything).
    ///   • STORE (_store): owner StableId → grantee StableIds — the durable truth, including
    ///     offline grantees. HOST-ONLY. The host rebuilds the runtime table from this + the live
    ///     roster whenever grants or the roster change, persists it (Phase 1c manifest), and feeds
    ///     each owner their grantee list (incl. offline) for the Permissions UI.
    ///
    /// Keeping enforcement in PlayerId space means a client-owner can check its own cargo grants
    /// without knowing anyone's StableId (clients never learn each other's StableId — privacy); the
    /// StableId truth stays on the host.
    /// </summary>
    public static class GrantSync
    {
        // ── Runtime (PlayerId space, replicated, online-only) ─────────────────
        private static readonly Dictionary<string, HashSet<string>> _grants = new();

        // ── Store (StableId space, HOST-ONLY, durable — incl. offline grantees) ─
        private static readonly Dictionary<string, HashSet<string>> _store = new();
        private static readonly Dictionary<string, string> _nameByStable = new();   // last-known display name, for the UI list

        /// <summary>Clear everything (host shutdown / new game). The persistent copy (Phase 1c)
        /// is restored separately by the host on load.</summary>
        public static void Reset()
        {
            _grants.Clear();
            _store.Clear();
            _nameByStable.Clear();
            _myGrantees = new List<OwnGrantEntry>();
        }

        // ── Runtime: the enforcement check (read-everywhere) ──────────────────
        /// <summary>Has <paramref name="ownerId"/> granted <paramref name="granteeId"/> a key?
        /// (PlayerId space — both must be online. The owner's own access is handled by callers.)</summary>
        public static bool IsGranted(string ownerId, string granteeId)
            => !string.IsNullOrEmpty(ownerId) && !string.IsNullOrEmpty(granteeId)
               && _grants.TryGetValue(ownerId, out var set) && set.Contains(granteeId);

        /// <summary>The owners who currently grant <paramref name="granteeId"/> a key (sorted + joined) — a
        /// stable signature for detecting when the local player's drivability changed (see VehicleManager).</summary>
        public static string GrantorSig(string granteeId)
        {
            if (string.IsNullOrEmpty(granteeId)) return "";
            var owners = new List<string>();
            foreach (var kv in _grants) if (kv.Value.Contains(granteeId)) owners.Add(kv.Key);
            owners.Sort();
            return string.Join(";", owners);
        }

        /// <summary>Set a runtime (PlayerId-space) grant. Used by the host rebuild and by the
        /// joiner applying the host snapshot. Idempotent; an owner can't grant themselves.</summary>
        public static void SetGrant(string ownerId, string granteeId, bool granted)
        {
            if (string.IsNullOrEmpty(ownerId) || string.IsNullOrEmpty(granteeId) || ownerId == granteeId) return;
            if (granted)
            {
                if (!_grants.TryGetValue(ownerId, out var set)) { set = new HashSet<string>(); _grants[ownerId] = set; }
                set.Add(granteeId);
            }
            else if (_grants.TryGetValue(ownerId, out var set))
            {
                set.Remove(granteeId);
                if (set.Count == 0) _grants.Remove(ownerId);
            }
        }

        /// <summary>HOST: clear the runtime table (before a rebuild from the store).</summary>
        public static void ClearRuntime() => _grants.Clear();

        // ── Runtime: join replay (full set — see ANTIPATTERNS Class 4) ────────
        /// <summary>HOST: build the full runtime grant table for a connecting peer.</summary>
        public static PermissionSnapshotPayload BuildSnapshot()
        {
            var snap = new PermissionSnapshotPayload();
            foreach (var owner in _grants)
                foreach (var grantee in owner.Value)
                    snap.Grants.Add(new PermissionGrantEntry { OwnerId = owner.Key, GranteeId = grantee });
            return snap;
        }

        /// <summary>JOINER: replace the runtime grant table with the host's authoritative snapshot.</summary>
        public static void ApplySnapshot(PermissionSnapshotPayload snap)
        {
            _grants.Clear();
            if (snap?.Grants == null) return;
            foreach (var g in snap.Grants)
                if (g != null) SetGrant(g.OwnerId, g.GranteeId, true);
        }

        // ── Store (StableId space, HOST-ONLY) ─────────────────────────────────
        /// <summary>HOST: record/clear a durable grant in StableId space. Idempotent; an owner
        /// can't grant themselves.</summary>
        public static void StoreSet(string ownerStable, string granteeStable, bool granted)
        {
            if (string.IsNullOrEmpty(ownerStable) || string.IsNullOrEmpty(granteeStable) || ownerStable == granteeStable) return;
            if (granted)
            {
                if (!_store.TryGetValue(ownerStable, out var set)) { set = new HashSet<string>(); _store[ownerStable] = set; }
                set.Add(granteeStable);
            }
            else if (_store.TryGetValue(ownerStable, out var set))
            {
                set.Remove(granteeStable);
                if (set.Count == 0) _store.Remove(ownerStable);
            }
        }

        /// <summary>HOST: the StableIds <paramref name="ownerStable"/> currently grants (snapshot copy).</summary>
        public static List<string> StoreGrantees(string ownerStable)
            => (!string.IsNullOrEmpty(ownerStable) && _store.TryGetValue(ownerStable, out var set))
               ? new List<string>(set) : new List<string>();

        /// <summary>HOST: every (ownerStable, granteeStable) pair — for the runtime rebuild and the
        /// persistence export. Returns a snapshot copy.</summary>
        public static List<KeyValuePair<string, string>> AllStoreEntries()
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (var owner in _store)
                foreach (var grantee in owner.Value)
                    list.Add(new KeyValuePair<string, string>(owner.Key, grantee));
            return list;
        }

        /// <summary>HOST: remember a StableId's last-known display name (for the owner's UI list).</summary>
        public static void NoteName(string stable, string name)
        {
            if (!string.IsNullOrEmpty(stable) && !string.IsNullOrEmpty(name)) _nameByStable[stable] = name;
        }

        /// <summary>HOST: last-known display name for a StableId ("" if unknown).</summary>
        public static string NameOf(string stable)
            => (!string.IsNullOrEmpty(stable) && _nameByStable.TryGetValue(stable, out var n)) ? n : "";

        // ── Local player's OWN grantee list (for the Permissions UI; incl. offline) ──
        private static List<OwnGrantEntry> _myGrantees = new();

        /// <summary>The local player's own grantee list (incl. offline grantees), for the Permissions
        /// UI. Set from the host's PermissionOwnGrants message (client) or built directly (host).</summary>
        public static List<OwnGrantEntry> MyGrantees() => _myGrantees;

        public static void SetMyGrantees(List<OwnGrantEntry> list) => _myGrantees = list ?? new List<OwnGrantEntry>();
    }
}
