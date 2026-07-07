using System.Collections.Generic;

namespace BigAmbitionsMP
{
    /// <summary>The kind of asset an access grant covers. A grant is per-person and GLOBAL within its
    /// kind: "I gave player P a key to all my VEHICLES" is independent of "...to all my HOMES" and
    /// "...helper access to all my BUSINESSES."</summary>
    public enum GrantKind { Vehicle = 0, Housing = 1, Business = 2 }

    /// <summary>
    /// Host-authoritative player-to-player ACCESS GRANTS — "I gave player P a key to my stuff," tracked
    /// per KIND (vehicle / housing). A granted player bypasses that kind's owner gate. See
    /// docs/PERMISSIONS-SYSTEM.md.
    ///
    /// TWO representations, on purpose:
    ///   • RUNTIME table (_grants[kind]): live PlayerId → grantee PlayerIds. Replicated read-everywhere
    ///     (PermissionSnapshot) and the ONLY thing enforcement reads (HostCanBoard, the cargo + ride
    ///     gates, the building gates). ONLINE relationships only — all enforcement ever needs.
    ///   • STORE (_store[kind]): owner StableId → grantee StableIds — the durable truth, incl. offline
    ///     grantees. HOST-ONLY. The host rebuilds the runtime from this + the live roster whenever grants
    ///     or the roster change, persists it (manifest), and feeds each owner their grantee list for the UI.
    ///
    /// Enforcement stays in PlayerId space so a client-owner can check its own grants without knowing
    /// anyone's StableId (clients never learn each other's StableId — privacy); the StableId truth is host-side.
    /// </summary>
    public static class GrantSync
    {
        // Per-kind runtime (PlayerId space, replicated, online-only) + store (StableId space, host-only).
        private static readonly Dictionary<GrantKind, Dictionary<string, HashSet<string>>> _grants = NewKindMap();
        private static readonly Dictionary<GrantKind, Dictionary<string, HashSet<string>>> _store  = NewKindMap();
        private static readonly Dictionary<string, string> _nameByStable = new();   // kind-independent display-name cache

        /// <summary>Every kind a grant can have — single source of truth for iteration.</summary>
        public static readonly GrantKind[] AllKinds = { GrantKind.Vehicle, GrantKind.Housing, GrantKind.Business };

        private static Dictionary<GrantKind, Dictionary<string, HashSet<string>>> NewKindMap()
        {
            // NOTE: runs during static field init, BEFORE AllKinds exists — enumerate explicitly.
            var m = new Dictionary<GrantKind, Dictionary<string, HashSet<string>>>();
            m[GrantKind.Vehicle]  = new Dictionary<string, HashSet<string>>();
            m[GrantKind.Housing]  = new Dictionary<string, HashSet<string>>();
            m[GrantKind.Business] = new Dictionary<string, HashSet<string>>();
            return m;
        }

        private static Dictionary<string, HashSet<string>> Runtime(GrantKind k) => _grants[k];
        private static Dictionary<string, HashSet<string>> Store(GrantKind k)   => _store[k];

        /// <summary>SCENE-scoped state only: the runtime grant table + the local player's replicated caches
        /// (grantee list, enterable set). Cleared on scene death/load; everything here is rebuilt from the
        /// store + roster (host) or the host's broadcasts (client). Does NOT touch the durable store or the
        /// name cache — their lifecycle is SESSION boundaries (StartNewGame / manifest restore), not scene
        /// transitions. The old full Reset() here was the "shares lost on load" root cause: it ran at
        /// scene-ready, AFTER RestoreOwnershipFromManifest had already repopulated the store at
        /// HostLoadSession, wiping every restored grant — and the next persist-on-change then wrote the
        /// empty set back over the loaded save's manifest (2026-06-30).</summary>
        public static void ResetSceneState()
        {
            foreach (var k in AllKinds) _grants[k].Clear();
            _myGrantees = new List<OwnGrantEntry>();
            _enterable = new HashSet<string>();
            _helperBiz = new HashSet<string>();
        }

        /// <summary>HOST, session boundary only (fresh new-game world, or clear-before-apply at manifest
        /// restore): wipe the durable store + display-name cache so a previous session's grants can never
        /// leak into (or be persisted onto) a different session.</summary>
        public static void ResetStore()
        {
            foreach (var k in AllKinds) _store[k].Clear();
            _nameByStable.Clear();
        }

        // ── Runtime: the enforcement check (read-everywhere) ──────────────────
        /// <summary>Has <paramref name="ownerId"/> granted <paramref name="granteeId"/> a key of
        /// <paramref name="kind"/>? (PlayerId space — both must be online. Owner's own access is caller-handled.)</summary>
        public static bool IsGranted(GrantKind kind, string ownerId, string granteeId)
            => !string.IsNullOrEmpty(ownerId) && !string.IsNullOrEmpty(granteeId)
               && ((Runtime(kind).TryGetValue(ownerId, out var set) && set.Contains(granteeId))
                   // Merger slice 1: merged members mutually hold EVERY kind of key. Unioning here —
                   // the one chokepoint every gate and the host's building-access classifier read —
                   // is what makes the merger equal full permissions with zero grant-store writes.
                   || MergerSync.MergedRuntime(ownerId, granteeId));

        /// <summary>Vehicle convenience (the original 2-arg check) — keeps vehicle enforcement call sites unchanged.</summary>
        public static bool IsGranted(string ownerId, string granteeId) => IsGranted(GrantKind.Vehicle, ownerId, granteeId);

        /// <summary>The owners who currently grant <paramref name="granteeId"/> a key of <paramref name="kind"/>
        /// (sorted + joined) — a stable signature for detecting when the local player's access changed.</summary>
        public static string GrantorSig(GrantKind kind, string granteeId)
        {
            if (string.IsNullOrEmpty(granteeId)) return "";
            var owners = new List<string>();
            foreach (var kv in Runtime(kind)) if (kv.Value.Contains(granteeId)) owners.Add(kv.Key);
            owners.Sort();
            return string.Join(";", owners);
        }
        public static string GrantorSig(string granteeId) => GrantorSig(GrantKind.Vehicle, granteeId);

        /// <summary>Set a runtime (PlayerId-space) grant. Used by the host rebuild and the joiner applying
        /// the host snapshot. Idempotent; an owner can't grant themselves.</summary>
        public static void SetGrant(GrantKind kind, string ownerId, string granteeId, bool granted)
        {
            if (string.IsNullOrEmpty(ownerId) || string.IsNullOrEmpty(granteeId) || ownerId == granteeId) return;
            var rt = Runtime(kind);
            if (granted)
            {
                if (!rt.TryGetValue(ownerId, out var set)) { set = new HashSet<string>(); rt[ownerId] = set; }
                set.Add(granteeId);
            }
            else if (rt.TryGetValue(ownerId, out var set))
            {
                set.Remove(granteeId);
                if (set.Count == 0) rt.Remove(ownerId);
            }
        }

        /// <summary>HOST: clear the runtime table for all kinds (before a rebuild from the store).</summary>
        public static void ClearRuntime() { foreach (var k in AllKinds) _grants[k].Clear(); }

        // ── Runtime: join replay (full set — see ANTIPATTERNS Class 4) ────────
        /// <summary>HOST: build the full runtime grant table (all kinds) for a connecting peer.</summary>
        public static PermissionSnapshotPayload BuildSnapshot()
        {
            var snap = new PermissionSnapshotPayload();
            foreach (var kind in AllKinds)
                foreach (var owner in _grants[kind])
                    foreach (var grantee in owner.Value)
                        snap.Grants.Add(new PermissionGrantEntry { Kind = kind, OwnerId = owner.Key, GranteeId = grantee });
            return snap;
        }

        /// <summary>JOINER: replace the runtime grant table with the host's authoritative snapshot.</summary>
        public static void ApplySnapshot(PermissionSnapshotPayload snap)
        {
            ClearRuntime();
            if (snap?.Grants == null) return;
            foreach (var g in snap.Grants)
                if (g != null) SetGrant(g.Kind, g.OwnerId, g.GranteeId, true);
        }

        // ── Store (StableId space, HOST-ONLY) ─────────────────────────────────
        /// <summary>HOST: record/clear a durable grant in StableId space. Idempotent; no self-grant.</summary>
        public static void StoreSet(GrantKind kind, string ownerStable, string granteeStable, bool granted)
        {
            if (string.IsNullOrEmpty(ownerStable) || string.IsNullOrEmpty(granteeStable) || ownerStable == granteeStable) return;
            var st = Store(kind);
            if (granted)
            {
                if (!st.TryGetValue(ownerStable, out var set)) { set = new HashSet<string>(); st[ownerStable] = set; }
                set.Add(granteeStable);
            }
            else if (st.TryGetValue(ownerStable, out var set))
            {
                set.Remove(granteeStable);
                if (set.Count == 0) st.Remove(ownerStable);
            }
        }

        /// <summary>HOST: the StableIds <paramref name="ownerStable"/> currently grants for <paramref name="kind"/>.</summary>
        public static List<string> StoreGrantees(GrantKind kind, string ownerStable)
            => (!string.IsNullOrEmpty(ownerStable) && Store(kind).TryGetValue(ownerStable, out var set))
               ? new List<string>(set) : new List<string>();

        /// <summary>HOST: the kinds <paramref name="ownerStable"/> grants <paramref name="granteeStable"/> (may be empty).</summary>
        public static List<GrantKind> StoreKindsFor(string ownerStable, string granteeStable)
        {
            var kinds = new List<GrantKind>();
            if (string.IsNullOrEmpty(ownerStable) || string.IsNullOrEmpty(granteeStable)) return kinds;
            foreach (var kind in AllKinds)
                if (Store(kind).TryGetValue(ownerStable, out var set) && set.Contains(granteeStable)) kinds.Add(kind);
            return kinds;
        }

        /// <summary>HOST: every (kind, ownerStable, granteeStable) — for the runtime rebuild and persistence.</summary>
        public static List<(GrantKind Kind, string Owner, string Grantee)> AllStoreEntries()
        {
            var list = new List<(GrantKind, string, string)>();
            foreach (var kind in AllKinds)
                foreach (var owner in _store[kind])
                    foreach (var grantee in owner.Value)
                        list.Add((kind, owner.Key, grantee));
            return list;
        }

        /// <summary>HOST: distinct grantee StableIds across all kinds for an owner (for the UI roster).</summary>
        public static List<string> AllGranteesOf(string ownerStable)
        {
            var seen = new HashSet<string>();
            if (!string.IsNullOrEmpty(ownerStable))
                foreach (var kind in AllKinds)
                    if (Store(kind).TryGetValue(ownerStable, out var set))
                        foreach (var g in set) seen.Add(g);
            return new List<string>(seen);
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

        /// <summary>The local player's own grantee list (incl. offline grantees + their granted kinds), for
        /// the Permissions UI. Set from the host's PermissionOwnGrants message (client) or built directly (host).</summary>
        public static List<OwnGrantEntry> MyGrantees() => _myGrantees;

        public static void SetMyGrantees(List<OwnGrantEntry> list) => _myGrantees = list ?? new List<OwnGrantEntry>();

        // ── Housing: buildings the local player may ENTER as a granted guest (host-computed) ──
        // Clients don't keep a building→owner map, so the host computes "which buildings you may enter" and
        // pushes the set (PermissionBuildingAccess); the CanEnterBuilding patch consults this.
        private static HashSet<string> _enterable = new();
        public static void SetEnterableBuildings(IEnumerable<string> addressKeys)
            => _enterable = addressKeys != null ? new HashSet<string>(addressKeys) : new HashSet<string>();
        public static bool CanEnterGranted(string addressKey)
            => !string.IsNullOrEmpty(addressKey) && _enterable.Contains(addressKey);

        // ── Business-helper access (round-32): which buildings the LOCAL player may WORK IN as a granted
        // helper (addr keys, host-pushed alongside _enterable). A SEPARATE set on purpose: residence-guest
        // behavior (fridge routing, furniture ownership flips) must not light up in businesses, and helper
        // behavior must not light up in homes — each gate opts into exactly one.
        private static HashSet<string> _helperBiz = new();
        // Merger slice 3 repair: addresses the host attributes to OTHER players (operator ledger).
        private static HashSet<string> _otherOwned = new();
        public static void SetOtherOwned(IEnumerable<string> addressKeys)
            => _otherOwned = addressKeys != null ? new HashSet<string>(addressKeys) : new HashSet<string>();
        public static bool IsOtherOwned(string addressKey)
            => !string.IsNullOrEmpty(addressKey) && _otherOwned.Contains(addressKey);

        public static void SetHelperBusinesses(IEnumerable<string> addressKeys)
        {
            var next = addressKeys != null ? new HashSet<string>(addressKeys) : new HashSet<string>();
            // Log transitions — a silent empty set is indistinguishable from "grant never arrived"
            // (round-35d: the register helper probe stayed mute; this line says whether helper access
            // was even live on this machine).
            if (!next.SetEquals(_helperBiz))
                Plugin.Logger.LogInfo($"[Grant] helper businesses: {next.Count} (was {_helperBiz.Count}).");
            _helperBiz = next;
        }
        public static bool IsHelperBusiness(string addressKey)
            => !string.IsNullOrEmpty(addressKey) && _helperBiz.Contains(addressKey);
    }
}
