using System;
using System.Collections.Generic;
using Buildings;
using Entities;
using LiteNetLib;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Host-side interior state pusher (Phase 2 of the building-sync feature).
    ///
    /// Subscription model: when a client enters building X, it sends an
    /// InteriorRequest{X}.  The host adds that client to X's subscriber set
    /// and immediately sends an InteriorSnapshot.  While subscribed, every
    /// Tick the host re-reads the building's interior fields, hashes them,
    /// and broadcasts a fresh snapshot if anything changed.  On exit, the
    /// client sends PlayerExitedBuilding{X} and the host removes them from
    /// the subscriber set.  If the set becomes empty, host stops polling X.
    ///
    /// Phase 2a covers Layout / interiorDesigns / retailPrices / dirtSpots.
    /// Phase 2b will add itemInstances (the full ItemInstance graph).
    /// </summary>
    public static class InteriorSync
    {
        private const float PollIntervalSeconds = 2f;

        // addressKey → set of peer ids currently subscribed (i.e. inside that building).
        // A peer can be subscribed to at most one building at a time (we drop
        // its previous sub when a new request comes in).
        private static readonly Dictionary<string, HashSet<int>> _subsByBuilding = new();
        // Inverse map: peer id → addressKey it's currently subscribed to.
        private static readonly Dictionary<int, string>          _buildingByPeer  = new();
        // addressKey → last-broadcast hash, so we only push when something changed.
        private static readonly Dictionary<string, int>          _lastHashByAddr  = new();
        private sealed class OwnerInteriorState
        {
            public string OwnerPlayerId = "";
            public InteriorSnapshotPayload Snapshot = new();
            public int Hash;
        }
        private static readonly Dictionary<string, OwnerInteriorState> _ownerSnapshotsByAddr = new();
        private static readonly Dictionary<string, int> _lastLocalOwnerHashByAddr = new();
        private static float _lastPollAt;
        private static float _lastOwnerPollAt;
        private static string _localOwnerAddress = "";

        /// <summary>Reset all subscription + cache state.  Called on host shutdown / new game.</summary>
        public static void Reset()
        {
            _subsByBuilding.Clear();
            _buildingByPeer.Clear();
            _lastHashByAddr.Clear();
            _ownerSnapshotsByAddr.Clear();
            _lastLocalOwnerHashByAddr.Clear();
            _lastPollAt = 0f;
            _lastOwnerPollAt = 0f;
            _localOwnerAddress = "";
        }

        // ── Subscription management ───────────────────────────────────────────

        /// <summary>
        /// Handle a client's InteriorRequest.  Adds them to the subscriber set
        /// for that building (removing any prior subscription) and sends the
        /// initial snapshot.
        /// </summary>
        public static void HandleRequest(MPLink peer, string playerId, string addressKey)
        {
            if (peer == null || string.IsNullOrEmpty(addressKey)) return;
            try
            {
                // Drop any prior subscription for this peer.
                if (_buildingByPeer.TryGetValue(peer.Id, out var oldAddr))
                {
                    if (_subsByBuilding.TryGetValue(oldAddr, out var oldSet))
                    {
                        oldSet.Remove(peer.Id);
                        if (oldSet.Count == 0) _subsByBuilding.Remove(oldAddr);
                    }
                }

                if (!_subsByBuilding.TryGetValue(addressKey, out var set))
                {
                    set = new HashSet<int>();
                    _subsByBuilding[addressKey] = set;
                }
                set.Add(peer.Id);
                _buildingByPeer[peer.Id] = addressKey;

                Plugin.Logger.LogInfo($"[InteriorSync] Sub: peer={peer.Id} player='{playerId}' addr='{addressKey}' (now {set.Count} subscriber(s) on this building, {_subsByBuilding.Count} active building(s)).");

                // Send initial snapshot to this peer only.
                var snap = BuildSnapshotForHostSend(addressKey);
                if (snap == null) return;
                _lastHashByAddr[addressKey] = ComputeHash(snap);
                MPServer.SendInteriorSnapshotTo(peer, snap);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] HandleRequest: {ex.Message}"); }
        }

        /// <summary>Handle a client's PlayerExitedBuilding.  Drops them from the subscriber set.</summary>
        public static void HandleExit(MPLink peer, string playerId, string addressKey)
        {
            if (peer == null) return;
            try
            {
                if (_buildingByPeer.TryGetValue(peer.Id, out var cur))
                {
                    _buildingByPeer.Remove(peer.Id);
                    if (_subsByBuilding.TryGetValue(cur, out var set))
                    {
                        set.Remove(peer.Id);
                        if (set.Count == 0)
                        {
                            _subsByBuilding.Remove(cur);
                            _lastHashByAddr.Remove(cur);   // stop tracking; will reseed on next subscriber
                        }
                    }
                }
                Plugin.Logger.LogInfo($"[InteriorSync] Unsub: peer={peer.Id} player='{playerId}' addr='{addressKey}' ({_subsByBuilding.Count} active building(s) remaining).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] HandleExit: {ex.Message}"); }
        }

        /// <summary>Called when a peer disconnects — clean up any lingering subscription.</summary>
        public static void HandlePeerDisconnected(int peerId)
        {
            if (!_buildingByPeer.TryGetValue(peerId, out var cur)) return;
            _buildingByPeer.Remove(peerId);
            if (_subsByBuilding.TryGetValue(cur, out var set))
            {
                set.Remove(peerId);
                if (set.Count == 0)
                {
                    _subsByBuilding.Remove(cur);
                    _lastHashByAddr.Remove(cur);
                }
            }
        }

        // ── Tick (poll subscribed buildings, broadcast diffs) ─────────────────

        public static void Tick()
        {
            if (!MPServer.IsRunning) return;
            if (_subsByBuilding.Count == 0) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _lastPollAt < PollIntervalSeconds) return;
            _lastPollAt = now;

            try
            {
                // Snapshot the keys so we can safely mutate _lastHashByAddr while iterating.
                var addrs = new List<string>(_subsByBuilding.Keys);
                foreach (var addr in addrs)
                {
                    var snap = BuildSnapshotForHostSend(addr);
                    if (snap == null) continue;
                    int hash = ComputeHash(snap);
                    if (_lastHashByAddr.TryGetValue(addr, out var prev) && prev == hash) continue;
                    _lastHashByAddr[addr] = hash;
                    if (_subsByBuilding.TryGetValue(addr, out var set))
                        MPServer.BroadcastInteriorSnapshotTo(set, snap);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] Tick: {ex.Message}"); }
        }

        // ── Snapshot construction ─────────────────────────────────────────────

        public static void TickClientOwner()
        {
            if (!MPClient.IsConnected || MPServer.IsRunning) return;
            if (string.IsNullOrEmpty(_localOwnerAddress)) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _lastOwnerPollAt < PollIntervalSeconds) return;
            _lastOwnerPollAt = now;
            SendLocalOwnerSnapshot(_localOwnerAddress, force: false, reason: "tick");
        }

        // ── [DirtWatch] diagnostic (2026-06-25): does a shop's dirt ever DECREASE, or only climb? ─────────
        // Player report: owned shops accumulate hundreds-to-~1000 dirt spots (a healthy shop should be <10).
        // The mod only SYNCS dirt — it never generates or cleans it — so this just samples each OWNED shop's
        // dirt count over time and logs the delta, to show whether dirt ever goes DOWN (something is cleaning)
        // or only UP / stays flat. Deliberately tracks NOTHING about employees/janitors — raw dirt only. Runs
        // on each owner's own machine (RentedByPlayer). Release-safe but anomaly-gated (only shops over the
        // threshold) + throttled, so a healthy game logs nothing. REMOVE once the question is answered.
        private static float _lastDirtWatchAt = -999f;
        private static readonly Dictionary<string, int> _lastDirtWatch = new(StringComparer.Ordinal);
        private const int   DirtWatchThreshold       = 25;     // a healthy shop should be well under 10
        private const float DirtWatchIntervalSeconds = 60f;

        public static void TickDirtWatch()
        {
            if (!MPServer.IsRunning && !MPClient.IsConnected) return;   // MP only
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _lastDirtWatchAt < DirtWatchIntervalSeconds) return;
            _lastDirtWatchAt = now;
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return;

                int day = -1; float hod = -1f;
                try { var gt = GameStateReader.GetGameTime(); day = gt.day; hod = gt.hourOfDay; } catch { }

                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    bool mine = false; try { mine = MergerFlip.TrulyMine(reg); } catch { }   // TrulyMine (merger flip excluded)
                    if (!mine) continue;
                    int dirt = 0; try { dirt = reg.dirtSpots?.Count ?? 0; } catch { }
                    if (dirt < DirtWatchThreshold) continue;   // only anomalous shops — healthy ones stay silent
                    string addr = GameStateReader.AddressKey(reg);
                    if (string.IsNullOrEmpty(addr)) continue;
                    // Change-gated (2026-07-09, RED ROC report review): 8 shops × "delta=0" × every
                    // minute carried no information after the first line. The question this probe
                    // answers is "does dirt ever DECREASE" — so speak on the first sighting of an
                    // over-threshold shop (baseline) and on any CHANGE; a flat count stays silent.
                    bool seen = _lastDirtWatch.TryGetValue(addr, out var last);
                    int delta = seen ? dirt - last : 0;
                    _lastDirtWatch[addr] = dirt;
                    if (seen && delta == 0) continue;
                    Plugin.Logger.LogWarning(
                        $"[DirtWatch] '{addr}' dirt={dirt} delta={delta:+0;-0;0} since last (~{(int)DirtWatchIntervalSeconds}s) | day {day} h{hod:F1}");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[DirtWatch]: {ex.Message}"); }
        }

        public static bool TrySendOwnerSnapshotOnEntry(BuildingRegistration reg, string addressKey)
        {
            if (!MPClient.IsConnected || MPServer.IsRunning) return false;
            if (reg == null || string.IsNullOrEmpty(addressKey)) return false;
            if (!IsLocalOwnerBusiness(reg)) return false;
            _localOwnerAddress = addressKey;
            SendLocalOwnerSnapshot(addressKey, force: true, reason: "entry");
            return true;
        }

        public static void NotifyLocalBuildingExit(string addressKey)
        {
            if (string.IsNullOrEmpty(addressKey)) return;
            if (_localOwnerAddress == addressKey)
            {
                SendLocalOwnerSnapshot(addressKey, force: true, reason: "exit");
                _localOwnerAddress = "";
            }
        }

        public static void HandleOwnerSnapshot(MPLink peer, string playerId, InteriorSnapshotPayload payload)
        {
            if (!MPServer.IsRunning || peer == null || payload == null || string.IsNullOrEmpty(payload.AddressKey)) return;
            try
            {
                if (!HostKnowsPlayerOwnsAddress(playerId, payload.AddressKey))
                {
                    Plugin.Logger.LogWarning($"[InteriorSync] OwnerSnapshot rejected: player='{playerId}' addr='{payload.AddressKey}' is not the recorded owner.");
                    return;
                }

                // Sanity-gate the owner's pushed prices + item graph the SAME way the dedicated RetailPrices
                // channel does (this path bypassed it) — reject rather than write NaN/insane/negative prices or
                // an unbounded item list into the host reg and rebroadcast it to every subscriber.
                if (payload.RetailPrices != null &&
                    (payload.RetailPrices.Count > 500 ||
                     payload.RetailPrices.Exists(x => x == null || !MPServer.IsSaneMoney(x.Price, 1_000_000f) || x.Price < 0f)))
                {
                    Plugin.Logger.LogWarning($"[InteriorSync] OwnerSnapshot rejected: implausible price table ({payload.RetailPrices.Count}) from '{playerId}' addr='{payload.AddressKey}'.");
                    return;
                }
                if (payload.ItemInstances != null && payload.ItemInstances.Count > 5000)
                {
                    Plugin.Logger.LogWarning($"[InteriorSync] OwnerSnapshot rejected: implausible item count ({payload.ItemInstances.Count}) from '{playerId}' addr='{payload.AddressKey}'.");
                    return;
                }

                payload.OwnerPlayerId = playerId;
                payload.ItemInstancesAuthoritative = true;
                payload.Authoritative = true;   // owner's own push — authoritative for the whole interior
                int hash = ComputeHash(payload);
                bool changed = !_ownerSnapshotsByAddr.TryGetValue(payload.AddressKey, out var prev) || prev.Hash != hash;
                _ownerSnapshotsByAddr[payload.AddressKey] = new OwnerInteriorState
                {
                    OwnerPlayerId = playerId,
                    Snapshot = payload,
                    Hash = hash,
                };
                _lastHashByAddr[payload.AddressKey] = hash;

                Plugin.Logger.LogInfo($"[InteriorSync] OwnerSnapshot accepted from '{playerId}' addr='{payload.AddressKey}': {SnapshotSummary(payload)}{(changed ? "" : " (unchanged)")}.");
                if (!changed) return;

                GameStatePatcher.ApplyInteriorSnapshot(payload);
                if (_subsByBuilding.TryGetValue(payload.AddressKey, out var set))
                    MPServer.BroadcastInteriorSnapshotTo(set, payload);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] HandleOwnerSnapshot: {ex.Message}"); }
        }

        private static bool SendLocalOwnerSnapshot(string addressKey, bool force, string reason)
        {
            try
            {
                var snap = BuildSnapshot(addressKey);
                if (snap == null)
                {
                    Plugin.Logger.LogWarning($"[InteriorSync] OwnerSnapshot not sent ({reason}): no snapshot for '{addressKey}'.");
                    return false;
                }
                snap.OwnerPlayerId = MPConfig.PlayerId;
                snap.ItemInstancesAuthoritative = true;
                snap.Authoritative = true;   // owner's own push — authoritative for the whole interior
                int hash = ComputeHash(snap);
                if (!force && _lastLocalOwnerHashByAddr.TryGetValue(addressKey, out var prev) && prev == hash)
                    return true;
                _lastLocalOwnerHashByAddr[addressKey] = hash;
                MPClient.SendInteriorOwnerSnapshot(snap);
#if BAMP_DEV
                // Change-gated already (hash-skip above), but still ~290 lines/session in one capture —
                // DEV-only in Release. (Worth a later look at why one HQ's interior changes that often.)
                Plugin.Logger.LogInfo($"[InteriorSync] Sent owner snapshot ({reason}) addr='{addressKey}': {SnapshotSummary(snap)}.");
#endif
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[InteriorSync] SendLocalOwnerSnapshot: {ex.Message}");
                return false;
            }
        }

        // WS1 (round-30): once per session, when the world goes live.
        private static bool _publishedAllOwned;

        /// <summary>Publish interior snapshots for ALL businesses/homes this machine owns — once per session,
        /// at world-live. Without this, a client-owned shop's furniture (stations, tills, shelves) only
        /// reached other machines after the owner physically ENTERED it (the pushes were entry-driven) — a
        /// visitor arriving first found an empty shop with nowhere to seat staff, and the host's persisted
        /// replica stayed stale until then. BuildSnapshot reads SAVE data, so no entry is needed. The HOST
        /// skips this: its world IS the source and visitors get it on entry.</summary>
        public static void PublishAllOwnedInteriors(string reason)
        {
            if (_publishedAllOwned) return;
            _publishedAllOwned = true;
            try
            {
                if (MPServer.IsRunning) return;
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return;
                int n = 0;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null || !IsLocalOwnerBusiness(reg)) continue;
                    PushOwnedBuildingNow(GameStateReader.AddressKey(reg));
                    n++;
                }
                if (n > 0) Plugin.Logger.LogInfo($"[InteriorSync] published ALL {n} owned interior(s) ({reason}) — visitors no longer wait for the owner to enter first.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] PublishAllOwnedInteriors: {ex.Message}"); }
        }

        /// <summary>Owner-side: re-sync a building's interior to everyone inside it (after a guest's edit was
        /// applied to reg). COALESCED to one push per address per main-thread flush: a single guest action can
        /// apply many same-frame mutations (a 6-food fridge deposit = 6 BStore PUTs → 6 pushes → the guest
        /// applied 6 full snapshots in one frame, and the per-apply destroy/respawn of the changed fridge with
        /// DEFERRED Destroys left a broken controller — the "fridge menu never opens" bug, round-12 #2). All
        /// same-flush mutations now ride ONE snapshot, so the receiver refreshes each changed item exactly once.</summary>
        public static void PushOwnedBuildingNow(string addressKey)
        {
            if (string.IsNullOrEmpty(addressKey)) return;
            lock (_pendingOwnedPushes)
            {
                _pendingOwnedPushes[addressKey] = _pendingOwnedPushes.TryGetValue(addressKey, out var n) ? n + 1 : 1;
                if (_ownedPushFlushQueued) return;
                _ownedPushFlushQueued = true;
            }
            GameStatePatcher.EnqueueOnMainThread(FlushOwnedPushes);
        }

        // addressKey → number of coalesced mutations awaiting the flush (count is for the log only).
        private static readonly System.Collections.Generic.Dictionary<string, int> _pendingOwnedPushes = new();
        private static bool _ownedPushFlushQueued;

        private static void FlushOwnedPushes()
        {
            System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>> pushes;
            lock (_pendingOwnedPushes)
            {
                pushes = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>(_pendingOwnedPushes);
                _pendingOwnedPushes.Clear();
                _ownedPushFlushQueued = false;
            }
            foreach (var p in pushes)
            {
                if (p.Value > 1) Plugin.Logger.LogInfo($"[InteriorSync] coalesced {p.Value} mutations → 1 push for '{p.Key}'.");
                PushOwnedBuildingImmediate(p.Key);
            }
        }

        /// <summary>The actual push. Works regardless of where the owner's avatar is — BuildSnapshot reads the
        /// SAVE data, not loaded objects. Host owner → broadcast to that building's subscribers; client owner →
        /// push to the host, which rebroadcasts.</summary>
        private static void PushOwnedBuildingImmediate(string addressKey)
        {
            try
            {
                if (MPServer.IsRunning)
                {
                    var snap = BuildSnapshotForHostSend(addressKey);
                    if (snap != null && _subsByBuilding.TryGetValue(addressKey, out var set))
                    {
                        _lastHashByAddr[addressKey] = ComputeHash(snap);
                        MPServer.BroadcastInteriorSnapshotTo(set, snap);
                    }
                }
                else
                {
                    SendLocalOwnerSnapshot(addressKey, force: true, reason: "guest-edit");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] PushOwnedBuildingImmediate: {ex.Message}"); }
        }

        /// <summary>GUEST: forward our just-edited LOCAL interior for this building to the owner, who ADOPTS it
        /// (ApplyInteriorSnapshot) + re-syncs. Called when the interior designer closes (HandleOnClose). Flagged
        /// Authoritative so the owner's apply accepts it as the new truth.</summary>
        public static void ForwardGuestInteriorEdit(string addressKey)
        {
            if (string.IsNullOrEmpty(addressKey)) return;
            try
            {
                HousingDesign.CommitLocalDesigns(addressKey);   // bug #5: flush the guest's live floor/wall edits → reg before snapshotting
                var snap = BuildSnapshot(addressKey);
                if (snap == null) return;
                snap.Authoritative = true;
                snap.ItemInstancesAuthoritative = true;
                snap.OwnerPlayerId = "";   // a guest edit — the owner adopts it as the new truth
                GameStatePatcher.NoteLocalItemState(addressKey, snap.ItemInstances);   // echo-suppression: our own
                                           // edit coming back must ser-match and keep live objects (round-14 #3)
                if (MPServer.IsRunning) MPServer.HandleBuildingInteriorEdit(snap, MPConfig.PlayerId);
                else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.BuildingInteriorEdit, MPConfig.PlayerId, snap));
                Plugin.Logger.LogInfo($"[Housing] guest forwarded interior edit for '{addressKey}' ({SnapshotSummary(snap)}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] ForwardGuestInteriorEdit: {ex.Message}"); }
        }

        private static InteriorSnapshotPayload? BuildSnapshotForHostSend(string addressKey)
        {
            if (_ownerSnapshotsByAddr.TryGetValue(addressKey, out var ownerState))
                return ownerState.Snapshot;

            var snap = BuildSnapshot(addressKey);
            if (snap == null) return null;
            // RULE (2026-06-17): the host is authoritative ONLY for businesses it itself owns (and pure
            // AI/world ones). For anything a PLAYER owns, this is the host's own — possibly blank/stale —
            // replica, so flag the WHOLE snapshot non-authoritative: the receiver must never let it clear
            // their real interior. Only the owner's own push (cached above) is authoritative.
            bool playerOwned = MPServer.BuildingOwners.TryGetValue(addressKey, out var owner)
                               && !string.IsNullOrEmpty(owner) && owner != "host";
            if (playerOwned)
            {
                snap.Authoritative              = false;
                snap.ItemInstancesAuthoritative = false;
                if (TryRemoteOwnerForAddress(addressKey, out var ownerId)) snap.OwnerPlayerId = ownerId;
            }
            return snap;
        }

        private static bool IsLocalOwnerBusiness(BuildingRegistration reg)
        {
            try { if (MergerFlip.TrulyMine(reg)) return true; } catch { }   // TrulyMine: flipped partner shops are NOT locally owner-authoritative
            try
            {
                string owner = reg.businessOwnerRivalId?.ToString() ?? "";
                return !string.IsNullOrEmpty(owner) && owner == MPConfig.PlayerId;
            }
            catch { return false; }
        }

        private static bool HostKnowsPlayerOwnsAddress(string playerId, string addressKey)
        {
            if (string.IsNullOrEmpty(playerId) || string.IsNullOrEmpty(addressKey)) return false;
            try
            {
                if (MPServer.BuildingOwners.TryGetValue(addressKey, out var owner) && owner == playerId)
                    return true;
            }
            catch { }
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return false;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null || GameStateReader.AddressKey(reg) != addressKey) continue;
                    string b = reg.buildingOwnerRivalId?.ToString() ?? "";
                    string biz = reg.businessOwnerRivalId?.ToString() ?? "";
                    return b == playerId || biz == playerId;
                }
            }
            catch { }
            return false;
        }

        private static bool TryRemoteOwnerForAddress(string addressKey, out string ownerId)
        {
            ownerId = "";
            try
            {
                if (MPServer.BuildingOwners.TryGetValue(addressKey, out var owner)
                    && !string.IsNullOrEmpty(owner)
                    && owner != "host"
                    && owner != MPConfig.PlayerId)
                {
                    ownerId = owner;
                    return true;
                }
            }
            catch { }
            return false;
        }

        internal static string SnapshotSummary(InteriorSnapshotPayload snap)
        {
            return $"designs={snap.InteriorDesigns.Count} prices={snap.RetailPrices.Count} dirt={snap.DirtSpots.Count} items={snap.ItemInstances.Count} itemAuth={snap.ItemInstancesAuthoritative}";
        }

        public static InteriorSnapshotPayload? BuildSnapshot(string addressKey)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return null;
                BuildingRegistration? reg = null;
                foreach (var r in gi.BuildingRegistrations)
                {
                    if (r == null) continue;
                    if (GameStateReader.AddressKey(r) == addressKey) { reg = r; break; }
                }
                if (reg == null)
                {
                    Plugin.Logger.LogWarning($"[InteriorSync] BuildSnapshot: no reg for '{addressKey}'");
                    return null;
                }

                var snap = new InteriorSnapshotPayload
                {
                    AddressKey = addressKey,
                    Layout     = reg.Layout?.ToString() ?? "",
                };

                // Round-39d — Phase 3 customer presence: ship the owner's shopper schedule with the
                // interior (guests seed their local spawner table from it). Owner-only: the host's
                // replica-built sends (BuildSnapshotForHostSend) see RentedByPlayer=false and skip,
                // so only the true owner's entries ever travel.
                try { if (MergerFlip.TrulyMine(reg)) snap.CustomerEntries = CustomerEntrySync.CaptureFor(reg); } catch { }
                // Round-39e — complaint parity: the fulfilled-demand set travels too (guests' customers
                // complain against it; without it every demand reads unfulfilled).
                try
                {
                    if (MergerFlip.TrulyMine(reg) && reg.cachedFulfilledCustomerDemands != null)
                        snap.FulfilledDemands = new List<string>(reg.cachedFulfilledCustomerDemands);
                }
                catch { }

                // Interior designs
                if (reg.interiorDesigns != null)
                {
                    for (int i = 0; i < reg.interiorDesigns.Count; i++)
                    {
                        var d = reg.interiorDesigns[i];
                        if (d == null) continue;
                        var dto = new InteriorDesignInfo { UUID = d.UUID?.ToString() ?? "" };
                        if (d.materials != null)
                        {
                            for (int j = 0; j < d.materials.Length; j++)
                            {
                                var m = d.materials[j];
                                dto.Materials.Add(new InteriorMaterialInfo
                                {
                                    MaterialID    = m.MaterialID?.ToString() ?? "",
                                    MaterialIndex = m.MaterialIndex,
                                    ColorIndex    = m.ColorIndex,
                                });
                            }
                        }
                        snap.InteriorDesigns.Add(dto);
                    }
                }

                // Retail prices
                if (reg.retailPrices != null)
                {
                    for (int i = 0; i < reg.retailPrices.Count; i++)
                    {
                        var rp = reg.retailPrices[i];
                        if (rp == null) continue;
                        snap.RetailPrices.Add(new RetailPriceInfo
                        {
                            ItemName = rp.itemName ?? "",
                            Price    = rp.price,
                        });
                    }
                }

                // Dirt spots
                if (reg.dirtSpots != null)
                {
                    for (int i = 0; i < reg.dirtSpots.Count; i++)
                    {
                        var ds = reg.dirtSpots[i];
                        if (ds == null) continue;
                        snap.DirtSpots.Add(new DirtSpotInfo
                        {
                            X         = ds.x,
                            Z         = ds.z,
                            Dirtiness = ds.dirtiness,
                        });
                    }
                }

                // ItemInstances (Phase 2b).  Walk the reg.itemInstances dict
                // and serialize each ItemInstance into a flat ItemInstanceInfo.
                try
                {
                    if (reg.itemInstances != null)
                    {
                        foreach (var kv in reg.itemInstances)
                        {
                            var ii = kv.Value;
                            if (ii == null) continue;
                            snap.ItemInstances.Add(SerializeItemInstance(ii));
                        }
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] item read for {addressKey}: {ex.Message}"); }

                return snap;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] BuildSnapshot('{addressKey}'): {ex.Message}"); return null; }
        }

        // ── ItemInstance serialization (Phase 2b) ────────────────────────────

        private static ItemInstanceInfo SerializeItemInstance(BigAmbitions.Items.ItemInstance ii)
        {
            var info = new ItemInstanceInfo
            {
                Id                  = ii.id?.ToString() ?? "",
                ItemName            = ii.itemName ?? "",
                Px = ii.position.x,  Py = ii.position.y,  Pz = ii.position.z,
                Qx = ii.rotation.x,  Qy = ii.rotation.y,  Qz = ii.rotation.z,  Qw = ii.rotation.w,
                YRotation           = ii.yRotation,
                ParentId            = ii.parentId?.ToString() ?? "",
                StreetName          = ii.streetName ?? "",
                StreetNumber        = ii.streetNumber,
                LinkedItemName      = ii.linkedItemName ?? "",
                IsSecured           = ii.isSecured,
                WorldSpaceTextValue = ii.worldSpaceTextValue?.ToString() ?? "",
                StateIndex          = ii.stateIndex,
                Alias               = ii.alias?.ToString() ?? "",
                CustomValue         = ii.customValue?.ToString() ?? "",
                PriceOnPurchase     = ii.priceOnPurchase,
            };

            // Stacked items (sub-items attached to this one).
            if (ii.stackedItems != null)
            {
                for (int i = 0; i < ii.stackedItems.Count; i++)
                {
                    var s = ii.stackedItems[i];
                    if (s == null) continue;
                    info.StackedItems.Add(new AttachableChildInfo
                    {
                        ChildId         = s.childId?.ToString() ?? "",
                        ChildItemName   = s.childItemName ?? "",
                        AttachmentIndex = s.attachmentIndex,
                    });
                }
            }

            // Cargo (products sitting on a shelf, ingredients in a fridge, etc).
            if (ii.cargoInstances != null)
            {
                for (int i = 0; i < ii.cargoInstances.Count; i++)
                {
                    var c = ii.cargoInstances[i];
                    if (c == null) continue;
                    var cdto = new CargoInstanceInfo
                    {
                        ItemName     = c.itemName ?? "",
                        Amount       = c.amount,
                        PricePerUnit = c.pricePerUnit,
                        Paid         = c.paid,
                    };
                    if (c.customColors != null)
                    {
                        for (int j = 0; j < c.customColors.Count; j++)
                        {
                            var cc = c.customColors[j];
                            if (cc == null) continue;
                            cdto.CustomColors.Add(new CustomColorInfo { Channel = (int)cc.channel, ColorPacked = cc.color.color });
                        }
                    }
                    if (c.nestedCargoInstances != null)
                    {
                        for (int j = 0; j < c.nestedCargoInstances.Count; j++)
                        {
                            var n = c.nestedCargoInstances[j];
                            if (n == null) continue;
                            var ndto = new NestedCargoInstanceInfo
                            {
                                ItemName     = n.itemName ?? "",
                                Amount       = n.amount,
                                PricePerUnit = n.pricePerUnit,
                            };
                            if (n.customColors != null)
                            {
                                for (int k = 0; k < n.customColors.Count; k++)
                                {
                                    var nc = n.customColors[k];
                                    if (nc == null) continue;
                                    ndto.CustomColors.Add(new CustomColorInfo { Channel = (int)nc.channel, ColorPacked = nc.color.color });
                                }
                            }
                            cdto.NestedCargoInstances.Add(ndto);
                        }
                    }
                    info.CargoInstances.Add(cdto);
                }
            }

            // Dirt-spot indices this item affects (overlapping floor tiles).
            if (ii.dirtSpotsThatAffects != null)
            {
                for (int i = 0; i < ii.dirtSpotsThatAffects.Count; i++)
                    info.DirtSpotsThatAffects.Add(ii.dirtSpotsThatAffects[i]);
            }

            // Custom positions (used by multi-element items like cinema seating).
            if (ii.customPositions != null)
            {
                for (int i = 0; i < ii.customPositions.Count; i++)
                {
                    var p = ii.customPositions[i];
                    info.CustomPositions.Add(new Vector3Info { X = p.x, Y = p.y, Z = p.z });
                }
            }

            // Top-level custom colors (paint applied directly to the item).
            if (ii.customColors != null)
            {
                for (int i = 0; i < ii.customColors.Count; i++)
                {
                    var cc = ii.customColors[i];
                    if (cc == null) continue;
                    info.CustomColors.Add(new CustomColorInfo { Channel = (int)cc.channel, ColorPacked = cc.color.color });
                }
            }

            // Purchaser settings (for ordering machines etc.).
            if (ii.playerItemPurchaserSettings != null)
            {
                var p = ii.playerItemPurchaserSettings;
                info.PurchaserSettings = new PlayerItemPurchaserSettingsInfo
                {
                    Name         = p.name?.ToString() ?? "",
                    Enabled      = p.enabled,
                    ItemName     = p.itemName ?? "",
                    ItemQuantity = p.itemQuantity,
                };
            }

            return info;
        }

        /// <summary>
        /// Order-sensitive hash over the snapshot's contents.  Collisions just
        /// mean one missed broadcast, recoverable on the next change.
        /// </summary>
        internal static int ComputeHash(InteriorSnapshotPayload snap)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + MPAudit.StableHash(snap.Layout);
                foreach (var d in snap.InteriorDesigns)
                {
                    h = h * 31 + MPAudit.StableHash(d.UUID);
                    foreach (var m in d.Materials)
                    {
                        h = h * 31 + MPAudit.StableHash(m.MaterialID);
                        h = h * 31 + m.MaterialIndex;
                        h = h * 31 + m.ColorIndex;
                    }
                }
                foreach (var rp in snap.RetailPrices)
                {
                    h = h * 31 + MPAudit.StableHash(rp.ItemName);
                    h = h * 31 + rp.Price.GetHashCode();
                }
                foreach (var ds in snap.DirtSpots)
                {
                    h = h * 31 + ds.X;
                    h = h * 31 + ds.Z;
                    // Round Dirtiness to 1 decimal place so micro-fluctuations
                    // from NPCs walking around don't trigger broadcasts every
                    // poll cycle.  We still see meaningful changes (0.1 step).
                    h = h * 31 + ((int)System.Math.Round(ds.Dirtiness * 10f)).GetHashCode();
                }
                // Items — hash core identity + position so adds/moves/removes
                // trigger broadcasts.  Cargo amounts/prices are part of items'
                // visible state and worth hashing too.
                foreach (var it in snap.ItemInstances)
                {
                    h = h * 31 + MPAudit.StableHash(it.Id);
                    h = h * 31 + MPAudit.StableHash(it.ItemName);
                    // Round positions to nearest cm so 0.001f jitter doesn't trigger.
                    h = h * 31 + ((int)System.Math.Round(it.Px * 100f)).GetHashCode();
                    h = h * 31 + ((int)System.Math.Round(it.Py * 100f)).GetHashCode();
                    h = h * 31 + ((int)System.Math.Round(it.Pz * 100f)).GetHashCode();
                    h = h * 31 + ((int)System.Math.Round(it.YRotation * 10f)).GetHashCode();
                    h = h * 31 + it.StateIndex;
                    h = h * 31 + MPAudit.StableHash(it.Alias);
                    foreach (var c in it.CargoInstances)
                    {
                        h = h * 31 + MPAudit.StableHash(c.ItemName);
                        h = h * 31 + c.Amount;
                        h = h * 31 + ((int)System.Math.Round(c.PricePerUnit * 100f)).GetHashCode();
                    }
                }
                return h;
            }
        }
    }
}
