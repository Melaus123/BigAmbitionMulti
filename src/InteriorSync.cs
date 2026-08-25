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
        private static string _pendingExitPush   = "";   // sweep 3b: an exit push that was refused — retried by the tick until it lands

        /// <summary>Reset all subscription + cache state.  Called on host shutdown / new game.</summary>
        public static void Reset()
        {
            _subsByBuilding.Clear();
            _buildingByPeer.Clear();
            _lastHashByAddr.Clear();
            _ownerSnapshotsByAddr.Clear();
            _lastLocalOwnerHashByAddr.Clear();
            _lastStructHashByAddr.Clear();
            _volatileSentAtByAddr.Clear();
            _lastLocalOwnerStructByAddr.Clear();
            _lastLocalOwnerVolatileAt.Clear();
            _lastDirtHashByAddr.Clear();            // v10
            _lastLocalOwnerDirtByAddr.Clear();      // v10
            _lastLocalOwnerNonCargoByAddr.Clear();  // v10
            _applyTimes.Clear();
            _loopWarnedAt.Clear();
            // Round-281: the struct-version mint and its send-side bookkeeping.  Clearing the version
            // counter is safe by construction — a receiver still holding an old number sees a MISMATCH
            // on the next cargo sync, throws it away and re-requests, which re-seeds both sides.
            _structVersionByAddr.Clear();
            _structVersionHashByAddr.Clear();
            _structVolHashByAddr.Clear();
            _cargoSendsByAddr.Clear();
            _cargoSendLoggedAt.Clear();
            _cargoFallbackReason.Clear();
            _lastPollAt = 0f;
            _lastOwnerPollAt = 0f;
            _localOwnerAddress = "";
            _pendingExitPush   = "";
            // Round-106: this one-shot gate was missed when Reset was written, so
            // PublishAllOwnedInteriors fired only ONCE PER PROCESS. Field-confirmed
            // (RED ROC 2026-07-27): first join logged "published ALL 9 owned interior(s)",
            // the second join in the same launch published nothing, so the host received
            // that client's shop contents only for buildings they physically walked into.
            _publishedAllOwned = false;
            _owedResyncs.Clear();                                          // Stage 0
            lock (_pendingResyncAnswers) _pendingResyncAnswers.Clear();    // Stage 0
            GameStatePatcher.ClearPendingLocalPlacements();                // Stage 0 (BLOCKER-1 registry)
        }

        // ── Interior-edit Stage 0: owed re-syncs (design 2026-08) ─────────────────────────────
        // A routine full snapshot that arrives while THIS machine is mid-edit in that building is
        // DISCARDED (never deferred as bytes — a deferred snapshot is stale by construction) and
        // the address is recorded here.  The drain re-ASKS, so the answer is rebuilt live at send
        // time.  Drained by the edit-end events (StopPlacingItem / designer HandleOnClose
        // postfixes) with the 2 s tick as the belt; an address still busy at drain time stays owed
        // (recurrence-covered).  MAIN THREAD ONLY (noted from applies, drained from postfixes and
        // the tick — all main thread).
        private sealed class OwedResync { public string Reason = ""; public float Since; public float WarnedAt; public float LastAskedAt = -999f; }
        private static readonly Dictionary<string, OwedResync> _owedResyncs = new(StringComparer.Ordinal);
        private static float _lastOwedTickAt;
        private const float OwedTripwireSeconds = 60f;   // long drags legitimately re-owe; it must be visible
        private const float OwedReAskSeconds    = 10f;   // review MAJOR-6: asks repeat until an apply completes; this spaces them

        public static void NoteResyncOwed(string addressKey, string reason)
        {
            if (string.IsNullOrEmpty(addressKey)) return;
            if (!_owedResyncs.TryGetValue(addressKey, out var o))
                _owedResyncs[addressKey] = o = new OwedResync { Since = UnityEngine.Time.unscaledTime };
            o.Reason = reason ?? "";
        }

        /// <summary>Review MAJOR-6: the owed entry retires when an authoritative apply for the address
        /// COMPLETES on this machine (called from the apply's baseline-recording sites) — an ask is
        /// only a request, and the answer can be deferred or skipped on the other side.</summary>
        public static void ClearResyncOwed(string addressKey)
        {
            if (!string.IsNullOrEmpty(addressKey)) _owedResyncs.Remove(addressKey);
        }

        /// <summary>Verification BLOCKER-A2/MAJOR-C seam: whether this address still owes a re-sync.
        /// Main thread only.</summary>
        public static bool IsResyncOwed(string addressKey)
            => !string.IsNullOrEmpty(addressKey) && _owedResyncs.ContainsKey(addressKey);

        /// <summary>Belt for the event drains — self-throttled; called every frame from the UI tick.
        /// Verification MINOR-T: the emptiness fast-path comes first so the empty-queues frame costs
        /// two count reads and nothing else (this file is perf-annotated throughout).</summary>
        public static void TickOwedResyncs()
        {
            if (_owedResyncs.Count == 0 && !GameStatePatcher.HasDeferredDeltaRemoves) return;
            float now = UnityEngine.Time.unscaledTime;
            if (now - _lastOwedTickAt < 2f) return;
            _lastOwedTickAt = now;
            try { GameStatePatcher.DrainDeferredDeltaRemoves(); } catch { }   // Stage 1b MAJOR-M belt
            if (_owedResyncs.Count > 0) DrainOwedResyncs("tick");
        }

        /// <summary>Fire the re-ask for every owed address whose edit has ended.  The re-ask route is
        /// derived LIVE at drain time (live reads at commitment), never stored at note time:
        /// host → ask the building's remote owner for a fresh push (RequestOwnerInteriorResync);
        /// client → re-request the host's serve, but only if still inside (the entry serve heals a
        /// left building anyway).  Either answer arrives flagged SeedOrHeal and applies.
        /// Review MAJOR-6: asking does NOT retire the entry — only a completed apply does
        /// (ClearResyncOwed from the apply's recording sites); asks repeat every OwedReAskSeconds
        /// until then, so a deferred/skipped answer on the other side cannot silently strand us.</summary>
        public static void DrainOwedResyncs(string trigger)
        {
            if (_owedResyncs.Count == 0) return;
            try
            {
                float now = UnityEngine.Time.unscaledTime;
                bool inSession = MPServer.IsRunning || MPClient.IsConnected;
                if (!inSession)
                {
                    Plugin.Logger.LogInfo($"[InteriorSync] {_owedResyncs.Count} owed re-sync(s) dropped — session ended.");
                    _owedResyncs.Clear();
                    return;
                }
                List<string>? drop = null;
                foreach (var kv in _owedResyncs)
                {
                    string addr = kv.Key; var owed = kv.Value;
                    if (HousingDesign.InteriorEditBusyAt(addr) != null)
                    {
                        if (now - owed.Since > OwedTripwireSeconds && now - owed.WarnedAt > OwedTripwireSeconds)
                        {
                            owed.WarnedAt = now;
                            Plugin.Logger.LogWarning($"[InteriorSync] '{addr}' has owed a re-sync for {now - owed.Since:F0}s ({owed.Reason}) — the local edit there has not ended (long drag/designer session, or a stuck placement state).");
                        }
                        continue;   // still busy — stays owed; the next edit-end or tick retries
                    }
                    if (MPServer.IsRunning)
                    {
                        // Host role: the only discardable inbound full state is a remote owner's push.
                        // Verification BLOCKER-A1: the SEND sits INSIDE the throttle — it triggers a
                        // ~300 KB answer, so an unthrottled ask loop would eat the whole per-connection
                        // budget (the first cut throttled only the log line).
                        if (now - owed.LastAskedAt < OwedReAskSeconds && owed.LastAskedAt >= 0f) continue;
                        string ownerPid = "";
                        try
                        {
                            if (MPServer.BuildingOwners.TryGetValue(addr, out var op)
                                && !string.IsNullOrEmpty(op) && !GameStatePatcher.IsHostLedgerId(op)) ownerPid = op;
                        }
                        catch { }
                        if (ownerPid.Length == 0 || !MPServer.RequestOwnerInteriorResyncByPid(ownerPid, addr))
                        {
                            (drop ??= new List<string>()).Add(addr);
                            Plugin.Logger.LogInfo($"[InteriorSync] owed re-sync for '{addr}' dropped ({trigger}, was: {owed.Reason}) — no reachable remote owner (their next push or entry serve heals).");
                        }
                        else
                        {
                            owed.LastAskedAt = now;
                            Plugin.Logger.LogInfo($"[InteriorSync] owed re-sync for '{addr}' — asked the owner for a fresh push ({trigger}, was: {owed.Reason}); retires when an apply completes.");
                        }
                    }
                    else
                    {
                        if (HousingDesign.CurrentBuildingAddr() == addr)
                        {
                            if (now - owed.LastAskedAt >= OwedReAskSeconds || owed.LastAskedAt < 0f)
                            {
                                owed.LastAskedAt = now;
                                MPClient.SendInteriorRequest(addr);   // the host re-serves, flagged SeedOrHeal
                                Plugin.Logger.LogInfo($"[InteriorSync] owed re-sync for '{addr}' — re-requested the host's serve ({trigger}, was: {owed.Reason}); retires when the serve applies.");
                            }
                        }
                        else
                        {
                            (drop ??= new List<string>()).Add(addr);
                            Plugin.Logger.LogInfo($"[InteriorSync] owed re-sync for '{addr}' dropped ({trigger}) — no longer inside; the next entry serve heals.");
                        }
                    }
                }
                if (drop != null) foreach (var a in drop) _owedResyncs.Remove(a);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] DrainOwedResyncs: {ex.Message}"); }
        }

        // Stage 0 review BLOCKER-2 rework: the SeedOrHeal intent travels WITH each push request as an
        // argument (PushOwnedBuildingNow → coalesced pending entry → PushOwnedBuildingImmediate →
        // SendLocalOwnerSnapshot) — the old side-channel set stranded marks on every skip path and a
        // later ROUTINE push then consumed one and bypassed the host's mid-edit gate.  A flag whose
        // push is skipped (all-zero reg, no snapshot) dies with it: nothing was asserted, and the
        // host's owed re-ask machinery keeps re-asking until an apply completes.
        // _pendingResyncAnswers: addresses the HOST re-asked us for (owner side) — retried by
        // TickClientOwner until one send actually lands, exactly the sweep-3b exit-push pattern,
        // because the one-shot answer was silently droppable at every placement/build gate.
        // Locked: queued from the client dispatch thread, consumed on the main thread.
        private static readonly HashSet<string> _pendingResyncAnswers = new(StringComparer.Ordinal);
        public static void QueueResyncAnswer(string addressKey)
        {
            if (string.IsNullOrEmpty(addressKey)) return;
            // Verification BLOCKER-A3: registration ONLY — TickClientOwner's retry (next frame) is
            // the single sender.  Also pushing here raced the tick and could ship the ~300 KB answer
            // twice per ask.
            lock (_pendingResyncAnswers) _pendingResyncAnswers.Add(addressKey);
        }
        private static void ClearPendingResyncAnswer(string addressKey)
        {
            lock (_pendingResyncAnswers) _pendingResyncAnswers.Remove(addressKey);
        }

        /// <summary>Shallow copy with SeedOrHeal set — the host's serve paths hand out the CACHED
        /// owner snapshot object, and stamping the flag on that shared object would leak "always
        /// apply" into every later Tick broadcast of it.  Lists are shared (sends only read them).</summary>
        private static InteriorSnapshotPayload AsSeedOrHeal(InteriorSnapshotPayload s) => new()
        {
            AddressKey = s.AddressKey, Layout = s.Layout, OwnerPlayerId = s.OwnerPlayerId,
            ItemInstancesAuthoritative = s.ItemInstancesAuthoritative, Authoritative = s.Authoritative,
            InteriorDesigns = s.InteriorDesigns, RadioStation = s.RadioStation, RadioVolume = s.RadioVolume,
            RetailPrices = s.RetailPrices, DirtSpots = s.DirtSpots, ItemInstances = s.ItemInstances,
            CustomerEntries = s.CustomerEntries, FulfilledDemands = s.FulfilledDemands,
            StructVersion = s.StructVersion, SeedOrHeal = true,
        };

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
                // Round-280 (S1): stamp ALL THREE trackers — stamping only the full hash left
                // the Tick's 12s clock un-reset, so it could double-send moments later.
                var (hsSub, hvSub, hnSub, hdSub) = ComputeHashes(snap);
                _lastHashByAddr[addressKey] = hvSub;
                _lastStructHashByAddr[addressKey] = hsSub;
                _structVolHashByAddr[addressKey] = hnSub;   // round-281: the cargo-only discriminator's baseline
                _lastDirtHashByAddr[addressKey] = hdSub;    // v10: the entry snapshot carries dirt — subscriber is current
                _volatileSentAtByAddr[addressKey] = UnityEngine.Time.realtimeSinceStartup;
                // Round-281: this snapshot is the receiver's BASELINE — it is what every later cargo
                // sync for this address is measured against, so it must carry the structure's version.
                StampStructVersion(snap, hsSub);
                // Stage 0: an entry serve (and the drain's re-request answer, which re-enters here)
                // is RECOVERY traffic — the receiver applies it even mid-edit.  Cloned, never stamped
                // on the (possibly cached) object itself.
                MPServer.SendInteriorSnapshotTo(peer, AsSeedOrHeal(snap));
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
        // (T8's short-lived subscriber mirror was REMOVED in the review fix pass: puppet routing
        // runs on the _bldgByPeer PRESENCE map in MPServer — review B1: a building's OWNER never
        // subscribes here, so this set was the wrong audience for puppet streams.)

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
                    // Stage 3/MIN1: PER-ADDRESS, not the old whole-loop gate — a drag in building A
                    // must not stall B's and C's pushes.  Before any hash store (task-28 fix 2), so
                    // the next poll retries this address.
                    if (PlacementQuiescedAt(addr, "host-subscriber-push")) continue;
                    var snap = BuildSnapshotForHostSend(addr);
                    if (snap == null) continue;
                    // Round-213: split gate. Structure changes (owner edits) send at the
                    // 2s beat as before; VOLATILE-only churn (cargo as customers buy,
                    // dirt underfoot, item state) coalesces - the rig 2026-08-01 loop
                    // shipped the full 754-item interior every beat for one visitor.
                    var (hs, hv, hn, hd) = ComputeHashes(snap);
                    // v10 (T7/ruling 33): dirt has its own band and its own tiny message — checked
                    // BEFORE the full gate, because a dirt-only beat leaves `full` unchanged and
                    // would otherwise `continue` right past it. Subscribers ARE the players inside
                    // this building, so this is exactly ruling 33's audience; an unsubscribed
                    // building is never even polled here.
                    if (!_lastDirtHashByAddr.TryGetValue(addr, out var pd) || pd != hd)
                    {
                        _lastDirtHashByAddr[addr] = hd;
                        if (_subsByBuilding.TryGetValue(addr, out var dirtSubs))
                            MPServer.BroadcastInteriorDirtSyncTo(dirtSubs, BuildDirtSync(snap));
                    }
                    bool fullChanged = !_lastHashByAddr.TryGetValue(addr, out var pf) || pf != hv;
                    if (!fullChanged) continue;
                    bool structChanged = !_lastStructHashByAddr.TryGetValue(addr, out var ps) || ps != hs;
                    if (!structChanged
                        && _volatileSentAtByAddr.TryGetValue(addr, out var tSent)
                        && now - tSent < VolatileCoalesceSeconds)
                        continue;
                    // Round-281: cargo-only means CARGO-ONLY.  `!structChanged` alone would also be
                    // true for a dirt or item-state delta, and routing those onto the cargo channel
                    // would strand them until some unrelated structural edit forced a full snapshot.
                    // A missing baseline reads as "not cargo-only" — the full snapshot is always a
                    // correct answer, only a larger one.
                    bool cargoOnly = !structChanged
                                     && _structVolHashByAddr.TryGetValue(addr, out var pn) && pn == hn;
                    _lastHashByAddr[addr] = hv;
                    _lastStructHashByAddr[addr] = hs;
                    _structVolHashByAddr[addr] = hn;
                    _volatileSentAtByAddr[addr] = now;
                    int sv = StampStructVersion(snap, hs);
                    if (_subsByBuilding.TryGetValue(addr, out var set))
                    {
                        // The gates above have already decided a send is warranted; all that is left is
                        // WHICH message carries it.  The round-280 trackers are stamped IDENTICALLY
                        // either way — whichever goes out, this address has spoken and the coalescing
                        // clock runs the same.
                        if (cargoOnly && TrySendCargoOnly(addr, set, snap, sv, "tick")) continue;
                        MPServer.BroadcastInteriorSnapshotTo(set, snap);
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] Tick: {ex.Message}"); }
        }

        // ── Snapshot construction ─────────────────────────────────────────────

        public static void TickClientOwner()
        {
            PublishAllOwnedInteriors("world live (settled retry)");   // round-179: no-op once done; retries a deferral

            if (!MPClient.IsConnected || MPServer.IsRunning) return;
            // Sweep item 3b: a parked exit push retries until one send actually lands —
            // recurrence-covered per the deferral contract. Cheap while blocked (the
            // placement check is the first thing the send evaluates).
            if (!string.IsNullOrEmpty(_pendingExitPush))
            {
                if (SendLocalOwnerSnapshot(_pendingExitPush, force: true, reason: "exit-retry"))
                {
                    Plugin.Logger.LogInfo($"[InteriorSync] parked exit push for '{_pendingExitPush}' delivered (sweep 3b).");
                    _pendingExitPush = "";
                }
            }
            // Stage 0 (review BLOCKER-2/MAJOR-6): a host re-ask ANSWER retries until one send lands —
            // same 3b contract; every placement/build gate could otherwise silently drop the one-shot
            // answer while the host keeps re-asking.
            if (_pendingResyncAnswers.Count > 0)
            {
                string[] answers;
                lock (_pendingResyncAnswers) { answers = new string[_pendingResyncAnswers.Count]; _pendingResyncAnswers.CopyTo(answers); }
                foreach (var a in answers)
                    if (SendLocalOwnerSnapshot(a, force: true, reason: "resync-answer", seedOrHeal: true))
                        ClearPendingResyncAnswer(a);
            }
            if (string.IsNullOrEmpty(_localOwnerAddress)) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _lastOwnerPollAt < PollIntervalSeconds) return;
            _lastOwnerPollAt = now;
            SendLocalOwnerSnapshot(_localOwnerAddress, force: false, reason: "tick");
        }

        // ── [DirtWatch] diagnostic (2026-06-25; RE-AIMED 2026-07-14) ──────────────────────────────────────
        // ORIGINAL question ("shops accumulate ~1000 dirt spots") is ANSWERED: reg.dirtSpots is a fixed
        // LATTICE — one entry per floor tile (GetDirtSpotsForBuilding walks the Floors transforms), so a
        // big building legitimately carries 987-1737 entries forever.  Filth is the per-spot dirtiness
        // VALUE: the decal renders only at dirtiness >= 5 (DirtSpotObject.SetDirtiness) and cleanliness
        // scoring averages values, never counts.  The old metric (list Count) therefore cried wolf on
        // every large clean shop.  NOW WATCHED: the count of VISIBLY DIRTY spots (dirtiness >= 5) — the
        // thing a player can actually see — same delta/change-gated reporting.  The mod only SYNCS dirt;
        // it never generates or cleans it.
        private static float _lastDirtWatchAt = -999f;
        private static readonly Dictionary<string, int> _lastDirtWatch = new(StringComparer.Ordinal);
        private const int   DirtWatchThreshold       = 25;     // visibly-dirty spots; a tended shop stays well under this
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
                    // Visibly dirty spots only (dirtiness >= 5 = the decal render threshold) —
                    // raw Count is the per-tile lattice and says nothing about filth.
                    int dirt = 0;
                    try
                    {
                        var spots = reg.dirtSpots;
                        if (spots != null)
                            for (int i = 0; i < spots.Count; i++)
                                if (spots[i] != null && spots[i].dirtiness >= 5f) dirt++;
                    }
                    catch { }
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
                // Sweep item 3b (field 20260816-164914 shape): this was a one-shot — the address
                // was cleared even when the send REFUSED (mid-placement, exception), the tick
                // stopped polling, and the shop's final state never conveyed until re-entry.
                // Clear only on success; otherwise park the address for the tick to retry.
                bool delivered = SendLocalOwnerSnapshot(addressKey, force: true, reason: "exit");
                if (!delivered)
                {
                    _pendingExitPush = addressKey;
                    Plugin.Logger.LogInfo($"[InteriorSync] exit push for '{addressKey}' deferred — parked for tick retry (sweep 3b).");
                }
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
                payload.Authoritative = true;   // owner's own push — authoritative for the whole interior
                // Round-103: same floor as the sender — the host must not PROMOTE an empty item set to
                // authoritative on the owner's behalf. Older clients (pre-fix) still send empty pushes
                // stamped authoritative; re-stamping here is what let one join delete a whole shop.
                payload.ItemInstancesAuthoritative = payload.ItemInstances != null && payload.ItemInstances.Count > 0;
                if (!payload.ItemInstancesAuthoritative)
                    Plugin.Logger.LogWarning(
                        $"[InteriorSync] OwnerSnapshot from '{playerId}' addr='{payload.AddressKey}' carries NO items — " +
                        "item authority DENIED (it cannot clear the stored interior). Their character save likely disagrees " +
                        "with this session's ownership ledger; the stored copy is kept.");
                // Stage 0: the ACCEPT half — the busy oracle is main-thread state, and the
                // toilet-stall loss (field 20260823-110955) was exactly this payload applying while
                // the host player was mid-drag in that room.  Busy means DISCARD BOTH the cache
                // write and the world apply (an accepted cache would serve pre-edit state and
                // diverge from the deferred world), then owe a re-ask that drains at edit end.
                // INLINE, not re-enqueued (review MAJOR-4): this handler ALREADY runs on the main
                // thread (MPServer's dispatch enqueues it, :1429), and a second hop would re-order
                // the cache write behind an already-queued cargo graft — the graft would land on the
                // pre-push cache, then be overwritten by the older full state, and the dual-hash
                // guard misses exactly the common only-cargo-moved case.
                AcceptOwnerSnapshot(playerId, payload);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] HandleOwnerSnapshot: {ex.Message}"); }
        }

        private static void AcceptOwnerSnapshot(string playerId, InteriorSnapshotPayload payload)
        {
            try
            {
                // Stage 0 gate — per-hop flag: read, then cleared, so the cached object the Tick
                // rebroadcasts can never carry "always apply" to subscribers.
                bool seedOrHeal = payload.SeedOrHeal;
                payload.SeedOrHeal = false;
                if (!seedOrHeal)
                {
                    string? busy = HousingDesign.InteriorEditBusyAt(payload.AddressKey);
                    if (busy != null)
                    {
                        NoteResyncOwed(payload.AddressKey, $"owner push from '{playerId}' discarded ({busy} here)");
                        Plugin.Logger.LogInfo($"[InteriorSync] OwnerSnapshot for '{payload.AddressKey}' DISCARDED — local player mid-edit ({busy}); cache and world both kept, re-ask owed (Stage 0).");
                        return;
                    }
                }
                int hash = CacheHash(payload);   // v10 review M1: full + dirt — see CacheHash
                bool changed = !_ownerSnapshotsByAddr.TryGetValue(payload.AddressKey, out var prev) || prev.Hash != hash;
                // Round-103b: an EMPTY push must not replace a cached NON-EMPTY one either. This cache is
                // what BuildSnapshotForHostSend serves to everyone who enters the building, so caching the
                // empty version would keep the world's copy protected while still handing out nothing —
                // and the owner would never get its own contents back. Keep the good snapshot; the owner
                // re-syncs from it on entry.
                bool emptyOverGood = (payload.ItemInstances == null || payload.ItemInstances.Count == 0)
                                     && prev?.Snapshot?.ItemInstances != null && prev.Snapshot.ItemInstances.Count > 0;
                if (emptyOverGood)
                {
                    Plugin.Logger.LogWarning(
                        $"[InteriorSync] KEEPING the stored interior for '{payload.AddressKey}': '{playerId}' pushed an empty one " +
                        $"over {prev!.Snapshot.ItemInstances.Count} stored item(s). They will receive the stored copy when they enter.");
                    // Verification BLOCKER-A2: this IS the re-ask's terminal answer ("the owner has
                    // nothing better; the stored copy stands").  If the address still owes — e.g. the
                    // stored copy's earlier apply withheld its design bands (MAJOR-C partial) — the
                    // best truth to deliver is the STORED snapshot; its completed apply retires the
                    // debt (or re-skips and keeps it while the designer stays open — the drain
                    // terminates at designer close).  Not owed → nothing to do.
                    if (IsResyncOwed(payload.AddressKey) && prev?.Snapshot != null)
                        GameStatePatcher.ApplyInteriorSnapshot(prev.Snapshot, seedOrHealOverride: true);
                    return;
                }
                // v10 (T7): the owner's 2 s tick pushes no longer carry the shopper schedule —
                // an empty list on an accepted push means "unchanged", never "gone". The cache
                // must keep serving the last known schedule to entering guests (round-39d's
                // empty-shop bug returns otherwise). Same convention as round-103's empty-items rule.
                if ((payload.CustomerEntries == null || payload.CustomerEntries.Count == 0)
                    && prev?.Snapshot?.CustomerEntries != null && prev.Snapshot.CustomerEntries.Count > 0)
                    payload.CustomerEntries = prev.Snapshot.CustomerEntries;
                if ((payload.FulfilledDemands == null || payload.FulfilledDemands.Count == 0)
                    && prev?.Snapshot?.FulfilledDemands != null && prev.Snapshot.FulfilledDemands.Count > 0)
                    payload.FulfilledDemands = prev.Snapshot.FulfilledDemands;
                _ownerSnapshotsByAddr[payload.AddressKey] = new OwnerInteriorState
                {
                    OwnerPlayerId = playerId,
                    Snapshot = payload,
                    Hash = hash,
                };

                Plugin.Logger.LogInfo($"[InteriorSync] OwnerSnapshot accepted from '{playerId}' addr='{payload.AddressKey}': {SnapshotSummary(payload)}{(changed ? "" : " (unchanged)")}.");
                // Verification BLOCKER-A2: a byte-identical answer normally means the earlier apply of
                // this exact state already satisfied the debt — but if THAT apply withheld its design
                // bands (MAJOR-C partial: designer was open), the debt is real and the answer must run
                // the apply again: it delivers the bands now, or re-skips and keeps the debt while the
                // designer stays open (the drain terminates at designer close).  Either way the loop
                // the first cut had — re-asking a shop whose cache pre-dated the edit session forever —
                // cannot form: a completed apply clears, and an un-owed unchanged answer is a no-op.
                if (!changed)
                {
                    if (IsResyncOwed(payload.AddressKey))
                        GameStatePatcher.ApplyInteriorSnapshot(payload, seedOrHealOverride: seedOrHeal);
                    return;
                }

                // seedOrHealOverride carries the consumed per-hop flag into the apply's own gate
                // (the flag on the payload object is already cleared — deliberately, see above).
                GameStatePatcher.ApplyInteriorSnapshot(payload, seedOrHealOverride: seedOrHeal);
                // v10 (T7 relay fix, audit item a): DON'T relay the full payload here, and DON'T
                // stamp _lastHashByAddr — both together were what bypassed the Tick's split gate,
                // making every client-owner push a FULL send to subscribers. With the cache updated
                // and the trackers untouched, the 2 s Tick sees the change and routes it through
                // the same cargo-only-when-possible gate the host's own buildings use (≤2 s added
                // latency on guest-visible changes; the round-281 baselines stay coherent because
                // ONE path now stamps them).
                // Review M2: the owner's OWN 12 s volatile coalesce already throttled this inflow —
                // stacking the Tick's 12 s window on top doubled worst-case guest latency to ~24 s.
                // Clearing the relay-side clock lets the next 2 s beat carry an accepted change
                // without re-opening any spam path (inflow stays owner-throttled).
                _volatileSentAtByAddr.Remove(payload.AddressKey);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] AcceptOwnerSnapshot: {ex.Message}"); }
        }
        // (The accept was briefly a second EnqueueOnMainThread hop; review MAJOR-4 showed the
        // handler is already main-thread and the extra hop re-ordered it behind cargo grafts.)

        /// <summary>Task-28 fix 2: true while an item is mid-placement on THIS machine IN THIS
        /// building.  The staged item is already live in reg.itemInstances with a per-frame drag
        /// pose (PlacementSystem writes ItemInstance.position every frame) and security flags churn
        /// around it — a capture in that window ships half-placed state.  Callers are tick-driven,
        /// so a deferral retries until placement ends (retry contract); the throttled log is the
        /// tripwire.  Stage 3/MIN1: per-ADDRESS via HousingDesign.PlacementBusyAt — the old global
        /// IsInPlacementMode form stalled EVERY building's push for the length of any drag, and a
        /// stranded placement state (design ADDITION 2) wedged all outbound sync forever; keyed to
        /// the dragged item's own building, both confine to the one building actually mid-edit.</summary>
        private static readonly System.Collections.Generic.Dictionary<string, float> _nextPlacementDeferLogAt = new();
        internal static bool PlacementQuiescedAt(string addressKey, string what)
        {
            bool placing = false;
            try { placing = HousingDesign.PlacementBusyAt(addressKey); } catch { }
            if (!placing) return false;
            try
            {
                float now = UnityEngine.Time.unscaledTime;
                string key = what + "|" + addressKey;
                if (!_nextPlacementDeferLogAt.TryGetValue(key, out var at) || now >= at)
                {
                    _nextPlacementDeferLogAt[key] = now + 5f;
                    Plugin.Logger.LogInfo($"[Settle] '{what}' for '{addressKey}' deferred — an item is being placed there on this machine (mid-drag state must not convey). Will retry.");
                }
            }
            catch { }
            return true;
        }

        /// <summary>Return contract (sweep 3b): FALSE only when the push should be RETRIED
        /// (deferred mid-placement, or threw).  "Nothing to deliver" outcomes — no snapshot
        /// buildable, all-zero skip, hash-unchanged — return TRUE so a parked exit push
        /// can't spin forever on a shop that has nothing to say.</summary>
        private static bool SendLocalOwnerSnapshot(string addressKey, bool force, string reason, bool seedOrHeal = false)
        {
            try
            {
                if (PlacementQuiescedAt(addressKey, "owner-push")) return false;   // before hash-store — retried by the tick
                var snap = BuildSnapshot(addressKey);
                if (snap == null)
                {
                    Plugin.Logger.LogWarning($"[InteriorSync] OwnerSnapshot not sent ({reason}): no snapshot for '{addressKey}'.");
                    return true;   // nothing retrievable — retrying cannot help (3b contract)
                }
                // Round-177 (field bamp-bug-20260727-184813, 'Jc': fridge/bed despawned from his
                // market AND apartment "each time he spawns"): the join-time PublishAllOwnedInteriors
                // raced his world-load and pushed an UNMATERIALIZED registration — all-zero across
                // items, designs and dirt — as the owner's authoritative truth.  Round-103 stripped
                // item authority from empty pushes, but designs/dirt still travelled and wiped the
                // host's renovations.  An all-zero snapshot asserts NO knowledge for EITHER building
                // type (a residence changes only while occupied and those flows already routed; a
                // business's ambient stock/dirt lives on the host's simulated copy) — skip it
                // entirely; the entry-time push carries the real state (field-proven: the same
                // building pushed dirt=75 later that session once actually loaded).
                if (snap.ItemInstances.Count == 0 && snap.InteriorDesigns.Count == 0 && snap.DirtSpots.Count == 0)
                {
                    Plugin.Logger.LogWarning($"[InteriorSync] owner push for '{addressKey}' ({reason}) SKIPPED — " +
                        "registration reads all-zero (not yet materialized, or truly bare): nothing to assert; " +
                        "the entry-time push will carry the real state.");
                    return true;   // nothing to assert — done, not retry-worthy (3b contract)
                }
                snap.OwnerPlayerId = MPConfig.PlayerId;
                snap.Authoritative = true;   // owner's own push — authoritative for the whole interior
                // Round-103 (field 2026-07-27, Prabaha/RED ROC): an owner push carrying ZERO items
                // must NEVER claim item authority. A client whose stored character disagrees with the
                // host's ownership ledger (e.g. it once fresh-started in this lineage) owns shops in
                // the ledger while its own save holds nothing for them — and PublishAllOwnedInteriors
                // then broadcast "all nine of my shops are empty, authoritatively" at join. The host
                // obeyed and DELETED the real contents (removed=128/33/31/6 in that session), which
                // also emptied cachedAvailableProducts → business requirements failed → zero customer
                // entries → no revenue. "I have nothing" is almost always absence of knowledge, not
                // evidence; only a non-empty push may assert item truth. Designs/dirt/prices still
                // travel, so the heal for everything else is unaffected.
                snap.ItemInstancesAuthoritative = snap.ItemInstances.Count > 0;
                if (snap.ItemInstances.Count == 0)
                    Plugin.Logger.LogWarning(
                        $"[InteriorSync] owner push for '{addressKey}' ({reason}) carries NO items — sent WITHOUT item authority " +
                        "so it cannot clear the stored interior. If this shop really is empty on this machine, its contents are " +
                        "missing locally (stale/mismatched character save) — the host's copy is the good one.");
                // Round-213: same split gate as the host subscriber tick - an owner
                // standing in their own busy shop pushed the full interior every 2s
                // beat on cargo/dirt churn; the host relayed each to every visitor.
                // ROUND-281 SCOPE GUARD (deliberate, not an oversight): this GUEST-OWNER → HOST push
                // stays a FULL snapshot.  The cargo channel is host→subscriber only, and this leg is a
                // different contract: the host does not merely relay this payload, it CACHES it
                // (_ownerSnapshotsByAddr) and serves that cached object to everyone who later enters
                // the building.  A cargo-only message cannot maintain a cache that must answer
                // "what is the whole interior" — it would have to be merged into the stored snapshot,
                // and a merge is where "absolute state" quietly becomes a diff-chain.  Shrinking this
                // leg is its own round with its own design.
                // v10 (T7): the shopper schedule rides only the force pushes (entry/exit/publish) —
                // the 2 s tick strips it, and the host cache keeps its last known copy (empty on an
                // accepted push means "unchanged" there, per the round-103 convention).
                if (!force) snap.CustomerEntries = new List<CustomerEntryInfo>();
                var (ohs, ohv, ohn, ohd) = ComputeHashes(snap);
                float onow = UnityEngine.Time.realtimeSinceStartup;
                // v10 (T7/ruling 33): dirt is its own band and no longer perturbs `full` — a
                // dirt-only change ships as a tiny InteriorDirtSync (owner → host keeps the cache
                // and the save current; the host forwards to inside-players only). Checked BEFORE
                // the full gate; a force push carries DirtSpots in the full snapshot below anyway.
                if (!_lastLocalOwnerDirtByAddr.TryGetValue(addressKey, out var pDirt) || pDirt != ohd)
                {
                    _lastLocalOwnerDirtByAddr[addressKey] = ohd;
                    if (!force)
                    {
                        var dirtUp = BuildDirtSync(snap);
                        MPClient.SendInteriorDirtSync(dirtUp);
                        Plugin.Logger.LogInfo($"[InteriorSync] Sent owner DIRT sync ({reason}) addr='{addressKey}': {dirtUp.Spots.Count} dirty spot(s) (v10).");
                    }
                }
                if (!force)
                {
                    bool fullChanged = !_lastLocalOwnerHashByAddr.TryGetValue(addressKey, out var prev) || prev != ohv;
                    if (!fullChanged) return true;
                    bool structChanged = !_lastLocalOwnerStructByAddr.TryGetValue(addressKey, out var pStruct) || pStruct != ohs;
                    if (!structChanged
                        && _lastLocalOwnerVolatileAt.TryGetValue(addressKey, out var tSent)
                        && onow - tSent < VolatileCoalesceSeconds)
                        return true;
                    // v10 (T7 upload fix, audit item a): only cargo differs — ship the cargo channel
                    // instead of the ~300 KB full snapshot. The v10 handshake refuses any other build,
                    // so the host always has the client→host cargo handler; the OwnerStructHash lets
                    // the host verify its cache still matches this structure before grafting.
                    bool cargoOnlyUp = !structChanged
                                       && _lastLocalOwnerNonCargoByAddr.TryGetValue(addressKey, out var pNc) && pNc == ohn;
                    if (cargoOnlyUp)
                    {
                        var cargoUp = BuildCargoSync(snap, structVersion: 0);
                        // Review m2: 0 means "host-minted direction" on the wire — a computed hash
                        // that legitimately lands on 0 must not be mistaken for it (the refusal
                        // loop would re-push ~300 KB every beat, forever). Review M3: the non-cargo
                        // band travels too, so a graft can never freeze stale item state.
                        cargoUp.OwnerStructHash   = ohs == 0 ? 1 : ohs;
                        cargoUp.OwnerNonCargoHash = ohn == 0 ? 1 : ohn;
                        _lastLocalOwnerHashByAddr[addressKey]     = ohv;
                        _lastLocalOwnerStructByAddr[addressKey]   = ohs;
                        _lastLocalOwnerNonCargoByAddr[addressKey] = ohn;
                        _lastLocalOwnerVolatileAt[addressKey]     = onow;
                        MPClient.SendInteriorCargoSync(cargoUp);
                        Plugin.Logger.LogInfo($"[InteriorSync] Sent owner CARGO sync ({reason}) addr='{addressKey}': {cargoUp.Items.Count} item(s) (v10 — was a full snapshot).");
                        return true;
                    }
                }
                _lastLocalOwnerHashByAddr[addressKey] = ohv;
                _lastLocalOwnerStructByAddr[addressKey] = ohs;
                _lastLocalOwnerNonCargoByAddr[addressKey] = ohn;
                _lastLocalOwnerVolatileAt[addressKey] = onow;
                // Stage 0: only a push ANSWERING the host's re-ask (or the world-live seed) carries
                // SeedOrHeal — routine entry/exit/tick/immediate pushes stay discardable on a
                // mid-edit host (that discardability IS the toilet-stall fix).  The flag arrives as
                // an ARGUMENT with this specific push (review BLOCKER-2 — a stored mark outlived its
                // push and a later routine send wrongly bypassed the host's gate).
                snap.SeedOrHeal = seedOrHeal;
                MPClient.SendInteriorOwnerSnapshot(snap);
                // Sweep 3c (user-approved): promoted to ALL builds — this line is the completion
                // evidence every field triage of "furniture never appeared" lacked (Release logs
                // could show deferrals but structurally never a success). Change-gated by the
                // hash-skip above; one chatty HQ measured ~290/session, accepted.
                Plugin.Logger.LogInfo($"[InteriorSync] Sent owner snapshot ({reason}) addr='{addressKey}': {SnapshotSummary(snap)}.");
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
            // Round-179: the settled-gate — THIS was the once-per-session action that raced a
            // client's world-load and published unmaterialized registrations as the owner's truth.
            // Contract: a deferral does NOT consume the once-flag; TickClientOwner retries until
            // settled, then this runs exactly once.
            if (!MPWorldReady.AssertSettledFor("publish-all-owned-interiors")) return;
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
                    // Stage 0: the world-live publish SEEDS the host cache (flag rides the push itself).
                    PushOwnedBuildingNow(GameStateReader.AddressKey(reg), seedOrHeal: true);
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
        public static void PushOwnedBuildingNow(string addressKey, bool seedOrHeal = false)
        {
            if (string.IsNullOrEmpty(addressKey)) return;
            lock (_pendingOwnedPushes)
            {
                // Review BLOCKER-2: the flag rides the pending entry (OR-merged on coalesce) — never
                // a side channel that can outlive the push it belonged to.
                _pendingOwnedPushes[addressKey] = _pendingOwnedPushes.TryGetValue(addressKey, out var e)
                    ? (e.Count + 1, e.SeedOrHeal || seedOrHeal) : (1, seedOrHeal);
                if (_ownedPushFlushQueued) return;
                _ownedPushFlushQueued = true;
            }
            GameStatePatcher.EnqueueOnMainThread(FlushOwnedPushes);
        }

        // addressKey → (coalesced mutation count — log only; SeedOrHeal intent for the flush).
        private static readonly System.Collections.Generic.Dictionary<string, (int Count, bool SeedOrHeal)> _pendingOwnedPushes = new();
        private static bool _ownedPushFlushQueued;

        private static void FlushOwnedPushes()
        {
            System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, (int Count, bool SeedOrHeal)>> pushes;
            lock (_pendingOwnedPushes)
            {
                pushes = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, (int Count, bool SeedOrHeal)>>(_pendingOwnedPushes);
                _pendingOwnedPushes.Clear();
                _ownedPushFlushQueued = false;
            }
            foreach (var p in pushes)
            {
                if (p.Value.Count > 1) Plugin.Logger.LogInfo($"[InteriorSync] coalesced {p.Value.Count} mutations → 1 push for '{p.Key}'.");
                PushOwnedBuildingImmediate(p.Key, p.Value.SeedOrHeal);
            }
        }

        // Round-280: immediate pushes suppressed by the S1/S2 gates, counted per address and
        // named on the next actual send — coalescing must never be silent.
        private static readonly Dictionary<string, int> _suppressedPushes = new();

        /// <summary>The actual push. Works regardless of where the owner's avatar is — BuildSnapshot reads the
        /// SAVE data, not loaded objects. Host owner → broadcast to that building's subscribers; client owner →
        /// push to the host, which rebroadcasts.</summary>
        private static void PushOwnedBuildingImmediate(string addressKey, bool seedOrHeal = false)
        {
            if (PlacementQuiescedAt(addressKey, "immediate-push")) return;   // task-28 fix 2: hash unchanged — the owner tick re-converges post-placement; a SeedOrHeal answer is retried by _pendingResyncAnswers
            try
            {
                if (MPServer.IsRunning)
                {
                    var snap = BuildSnapshotForHostSend(addressKey);
                    if (snap != null && _subsByBuilding.TryGetValue(addressKey, out var set))
                    {
                        // Round-280 (S1/S2, field 20260818-223659 resend storm): this path had NO
                        // gate — 68% of a 522-broadcast evening came through here (helper-order
                        // adoptions + storage ops), 96% of all sends carrying zero item changes.
                        // S1: an identical snapshot does not send at all.  S2: a CARGO-ONLY delta
                        // obeys the same 12s coalesce as the Tick — the Tick's 2s poll delivers it
                        // when the window opens (recurrence-covered: the un-stamped hash keeps the
                        // Tick's fullChanged true).  STRUCTURAL changes (a guest's furniture) still
                        // send immediately, exactly as before.  Suppressions are counted and named
                        // on the next actual send so the coalescing stays diagnosable.
                        float nowIp = UnityEngine.Time.realtimeSinceStartup;
                        var (hsIp, hvIp, hnIp, _) = ComputeHashes(snap);
                        bool fullChangedIp = !_lastHashByAddr.TryGetValue(addressKey, out var pfIp) || pfIp != hvIp;
                        if (!fullChangedIp)
                        {
                            _suppressedPushes.TryGetValue(addressKey, out var n0); _suppressedPushes[addressKey] = n0 + 1;
                            return;
                        }
                        bool structChangedIp = !_lastStructHashByAddr.TryGetValue(addressKey, out var psIp) || psIp != hsIp;
                        if (!structChangedIp
                            && _volatileSentAtByAddr.TryGetValue(addressKey, out var tSentIp)
                            && nowIp - tSentIp < VolatileCoalesceSeconds)
                        {
                            _suppressedPushes.TryGetValue(addressKey, out var n1); _suppressedPushes[addressKey] = n1 + 1;
                            return;
                        }
                        // Round-281: same cargo-only test as the Tick (dirt/state deltas are NOT
                        // cargo-only and must keep riding the full snapshot).
                        bool cargoOnlyIp = !structChangedIp
                                           && _structVolHashByAddr.TryGetValue(addressKey, out var pnIp) && pnIp == hnIp;
                        _lastHashByAddr[addressKey] = hvIp;
                        _lastStructHashByAddr[addressKey] = hsIp;
                        _structVolHashByAddr[addressKey] = hnIp;
                        _volatileSentAtByAddr[addressKey] = nowIp;
                        int svIp = StampStructVersion(snap, hsIp);
                        if (_suppressedPushes.TryGetValue(addressKey, out var sup) && sup > 0)
                        {
                            _suppressedPushes.Remove(addressKey);
                            Plugin.Logger.LogInfo($"[InteriorSync] immediate push for '{addressKey}' absorbed {sup} identical/cargo-coalesced push(es) since the last send (round-280).");
                        }
                        // Round-281: trackers already stamped identically above — only the wire format
                        // differs.  A guest's helper-order adoption or storage op that moved nothing but
                        // cargo now costs a few KB instead of the whole interior.
                        if (cargoOnlyIp && TrySendCargoOnly(addressKey, set, snap, svIp, "immediate-push")) return;
                        MPServer.BroadcastInteriorSnapshotTo(set, snap);
                    }
                }
                else
                {
                    // Stage 3/MIN3: the reason is a LOG label only (verified: never compared) — the
                    // old "guest-edit" literal read as evidence of a guest edit on every push.
                    bool delivered = SendLocalOwnerSnapshot(addressKey, force: true,
                        reason: seedOrHeal ? "resync-answer" : "immediate-push", seedOrHeal: seedOrHeal);
                    if (delivered && seedOrHeal) ClearPendingResyncAnswer(addressKey);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] PushOwnedBuildingImmediate: {ex.Message}"); }
        }

        /// <summary>Round-169 — HOST: send one building's snapshot DIRECTLY to one player, subscription or
        /// not.  The arbitration hand-over needs the loser's DATA filled even though they are nowhere near
        /// the building (PushOwnedBuildingImmediate broadcasts to SUBSCRIBERS only, which silently sent to
        /// nobody — field: the released copy stayed a default shell).  The client applies unconditionally.</summary>
        /// <summary>forceItemAuthority (round-196 business sale): BuildSnapshotForHostSend flags
        /// remote-player-owned shops non-authoritative so the host's replica can never clobber the
        /// live owner's interior — but a SALE re-keys the ledger to the buyer BEFORE this send, so
        /// the guard mis-read the delivery as "someone else's shop" and the buyer refused to adopt
        /// the 70 items it was sent (rig 2026-07-30, empty purchased shop). At sale time the seller
        /// has released and the receiver IS the new owner — vouch, but never for an EMPTY list.</summary>
        public static void SendSnapshotToPlayer(string addressKey, string pid, bool forceItemAuthority = false)
        {
            try
            {
                if (!MPServer.IsRunning || string.IsNullOrEmpty(addressKey) || string.IsNullOrEmpty(pid)) return;
                var snap = BuildSnapshotForHostSend(addressKey);
                if (snap == null) { Plugin.Logger.LogWarning($"[InteriorSync] direct send: no snapshot for '{addressKey}'."); return; }
                if (forceItemAuthority && snap.ItemInstances != null && snap.ItemInstances.Count > 0)
                {
                    snap.Authoritative              = true;
                    snap.ItemInstancesAuthoritative = true;
                }
                // Round-281: every full send is stamped, this ungated one included — the receiver may
                // never have subscribed (that is this path's whole purpose), so this snapshot can be
                // the only baseline it ever gets for the address.  Deliberately stamps ONLY the
                // version: writing the round-280 trackers from here would give an ungated path a say
                // in the coalescing clock, which is exactly the bug round-280 fixed.
                StampStructVersion(snap);
                // Stage 0: every caller of this path is a heal or hand-over (sale, takeover,
                // arbitration, round-184) — recovery traffic, applies even mid-edit.
                MPServer.SendToPlayer(pid, MessageEnvelope.Create(MessageType.InteriorSnapshot, "host", AsSeedOrHeal(snap)));
                Plugin.Logger.LogInfo($"[InteriorSync] snapshot of '{addressKey}' sent directly to '{pid}' ({SnapshotSummary(snap)}{(forceItemAuthority ? ", sale-authoritative" : "")}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] direct send: {ex.Message}"); }
        }

        /// <summary>STAGE 1b (design 2026-08): forward a placement/removal edit as the 1-3 ops it
        /// actually made (BuildingInteriorDelta) instead of the whole replica. NEVER falls back to
        /// the 140 (review MAJOR-O — a guest whole-replica assertion is the exact class this design
        /// removes): a missing baseline becomes an upserts-only seed and a skewed baseline suppresses
        /// its removes + owes a re-sync, both inside the builder. "no-changes" is silence: the
        /// double-routed second fire of one intent has nothing left to say.</summary>
        public static void ForwardGuestInteriorEditDelta(string addressKey, string? knownRemovedId = null)
        {
            if (string.IsNullOrEmpty(addressKey)) return;
            try
            {
                // Review MAJOR-N: check the send path BEFORE building — the builder advances the
                // baseline, and a delta dropped after that is permanent divergence (unlike the 140,
                // whose next whole-replica send self-healed). Not building keeps this edit inside
                // the next diff, so the next successful forward carries it.
                if (!MPServer.IsRunning && !MPClient.IsConnected)
                {
                    Plugin.Logger.LogWarning($"[Housing] edit delta for '{addressKey}' NOT built — no session to send on; the change stays in the local diff for the next forward.");
                    return;
                }
                var p = GameStatePatcher.BuildLocalEditDelta(addressKey, out string reason, bulkRemovesAllowed: false, knownRemovedId: knownRemovedId);
                if (p == null) return;   // no-changes / no-reg / threw — nothing to convey (guardrails handle the rest inside)
                p.SenderId = MPConfig.PlayerId;
                try { p.PlaythroughId = MPSaveCoordinator.ActivePlaythroughId ?? ""; } catch { }
                if (MPServer.IsRunning) MPServer.HandleBuildingInteriorDelta(p, MPConfig.PlayerId);
                else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.BuildingInteriorDelta, MPConfig.PlayerId, p));
                Plugin.Logger.LogInfo($"[Housing] guest forwarded interior DELTA for '{addressKey}': {p.Ops.Count} op(s) (Stage 1b — was the whole replica).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] ForwardGuestInteriorEditDelta: {ex.Message}"); }
        }

        /// <summary>STAGE 2 (design 2026-08): the DESIGNER CLOSE as a delta — the session's item
        /// edits (diffed with bulk removes allowed; the sell/pack tools mostly pre-conveyed theirs
        /// through the per-action removal forwards) plus the changed design elements, UPDATE-ONLY by
        /// UUID. CommitLocalDesigns runs FIRST (bug #5: a guest's live paint never reaches
        /// reg.interiorDesigns without the scoped serialize — forwarding before it would carry the
        /// OLD walls). Nothing (not even a skewed baseline) falls back to the whole-replica 140.</summary>
        public static void ForwardGuestDesignerClose(string addressKey)
        {
            if (string.IsNullOrEmpty(addressKey)) return;
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected)
                {
                    Plugin.Logger.LogWarning($"[Housing] designer-close delta for '{addressKey}' NOT built — no session to send on; the changes stay in the local diffs for the next forward.");
                    return;
                }
                HousingDesign.CommitLocalDesigns(addressKey);   // bug #5: flush live paint → reg BEFORE diffing
                var p = GameStatePatcher.BuildDesignerCloseDelta(addressKey);
                if (p == null) return;   // nothing changed this session
                p.SenderId = MPConfig.PlayerId;
                try { p.PlaythroughId = MPSaveCoordinator.ActivePlaythroughId ?? ""; } catch { }
                if (MPServer.IsRunning) MPServer.HandleBuildingInteriorDelta(p, MPConfig.PlayerId);
                else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.BuildingInteriorDelta, MPConfig.PlayerId, p));
                Plugin.Logger.LogInfo($"[Housing] guest forwarded DESIGNER-CLOSE delta for '{addressKey}': {p.Ops.Count} item op(s) + {p.Designs.Count} design(s) (Stage 2 — was the whole replica).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] ForwardGuestDesignerClose: {ex.Message}"); }
        }

        /// <summary>HOST, after adopting (or forwarding) a grant-verified delta: the same delta goes
        /// to the building's subscribers minus the sender (their machines apply the identical ops),
        /// and the send trackers are stamped to the post-adopt state so the 2 s Tick does not
        /// double-convey the change as a full snapshot — this retires T7-gap-(b)'s force-push cost
        /// (Q1). MAIN THREAD ONLY.</summary>
        internal static void RelayDeltaAfterHostAdopt(InteriorEditDeltaPayload p, string senderPid)
        {
            try
            {
                // Mint FIRST (review MAJOR-L): the relay carries the new struct version so
                // subscribers stay cargo-sync-coherent without a full re-serve.
                p.StructVersion = StampTrackersCurrent(p.AddressKey);
                if (_subsByBuilding.TryGetValue(p.AddressKey, out var set) && set.Count > 0)
                    MPServer.BroadcastInteriorDeltaTo(set, senderPid, p);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] RelayDeltaAfterHostAdopt: {ex.Message}"); }
        }

        /// <summary>Q1 (user-approved): id-keyed ABSOLUTE-PER-ITEM graft of a guest's delta onto the
        /// host's cached owner snapshot — the cache every entry serve is built from — with the
        /// RequestOwnerInteriorResync fallback when there is no cache to graft (mirrors the accepted
        /// v10 cargo graft). The subscribers get the delta either way (it is grant-verified truth);
        /// only the stamp is skipped in the fallback, so the Tick's next full send can still heal.
        /// MAIN THREAD ONLY.</summary>
        internal static void GraftDeltaOntoOwnerCache(InteriorEditDeltaPayload p, string senderPid, string ownerPid)
        {
            try
            {
                bool grafted = false;
                if (_ownerSnapshotsByAddr.TryGetValue(p.AddressKey, out var st) && st?.Snapshot?.ItemInstances != null)
                {
                    var list = st.Snapshot.ItemInstances;
                    foreach (var op in p.Ops)
                    {
                        if (op == null || string.IsNullOrEmpty(op.Id)) continue;
                        int at = list.FindIndex(x => x != null && x.Id == op.Id);
                        if (op.Kind == "remove") { if (at >= 0) list.RemoveAt(at); }
                        else if (op.Kind == "upsert" && op.Item != null)
                        {
                            if (at >= 0) list[at] = op.Item;
                            else list.Add(op.Item);
                        }
                    }
                    // Stage 2: design entries graft the same way — UPDATE-ONLY by UUID — so entry
                    // serves built from this cache carry the new paint, not the pre-close walls.
                    if (p.Designs != null && p.Designs.Count > 0 && st.Snapshot.InteriorDesigns != null)
                    {
                        var dlist = st.Snapshot.InteriorDesigns;
                        foreach (var d in p.Designs)
                        {
                            if (d == null || string.IsNullOrEmpty(d.UUID)) continue;
                            int at = dlist.FindIndex(x => x != null && x.UUID == d.UUID);
                            if (at >= 0) dlist[at] = d;
                            else dlist.Add(d);
                        }
                    }
                    st.Hash = CacheHash(st.Snapshot);
                    grafted = true;
                }
                // Mint BEFORE broadcasting (review MAJOR-L) so the relay carries the new struct
                // version; in the no-cache fallback nothing is minted (StructVersion stays 0 =
                // "not the stamped stream") and the owner's forced push re-seeds everyone.
                if (grafted) p.StructVersion = StampTrackersCurrent(p.AddressKey);
                if (_subsByBuilding.TryGetValue(p.AddressKey, out var set) && set.Count > 0)
                    MPServer.BroadcastInteriorDeltaTo(set, senderPid, p);
                if (!grafted)
                {
                    Plugin.Logger.LogInfo($"[InteriorSync] no owner cache to graft for '{p.AddressKey}' — asking '{ownerPid}' for a full push (Q1 fallback).");
                    MPServer.RequestOwnerInteriorResyncByPid(ownerPid, p.AddressKey);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] GraftDeltaOntoOwnerCache: {ex.Message}"); }
        }

        /// <summary>Stamp the host send trackers to the CURRENT post-adopt state, exactly as a full
        /// send would have — the subscribers were conveyed by the delta, so the next Tick beat must
        /// see "already spoken" rather than re-shipping the room. Anything that changes after this
        /// moves the hashes again and sends normally.
        /// Review MAJOR-K: the DIRT tracker is deliberately NOT stamped — a delta carries no dirt,
        /// and stamping it would claim a conveyance that never happened, silently eating a pending
        /// one-shot dirt send (a mopped floor has no recurrence).
        /// Review MAJOR-L: returns the MINTED struct version so the relay can CARRY it — the
        /// host→subscriber delta IS the host's stamped stream; minting without conveying made every
        /// later cargo sync mismatch into a full re-serve, a throughput regression in exactly the
        /// busy shops this stage targets.</summary>
        private static int StampTrackersCurrent(string addressKey)
        {
            try
            {
                var snap = BuildSnapshotForHostSend(addressKey);
                if (snap == null) return 0;
                var (hs, hv, hn, _) = ComputeHashes(snap);
                _lastHashByAddr[addressKey] = hv;
                _lastStructHashByAddr[addressKey] = hs;
                _structVolHashByAddr[addressKey] = hn;
                _volatileSentAtByAddr[addressKey] = UnityEngine.Time.realtimeSinceStartup;
                return StampStructVersion(snap, hs);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] StampTrackersCurrent: {ex.Message}"); return 0; }
        }

        /// <summary>CLIENT-OWNER, after adopting a delta for a building it owns: stamp the owner-side
        /// send trackers to the post-adopt state so the 2 s tick does not answer the adoption with a
        /// full ~300 KB push — the host already grafted its cache and conveyed the subscribers with
        /// the same delta (Q1's whole point). ComputeHashes ignores CustomerEntries/FulfilledDemands
        /// and the flag fields (review MINOR-P), so no shape adjustment is needed for the hashes to
        /// compare against what the tick would send. Any LATER real change moves the hashes again
        /// and pushes normally. Review MAJOR-K: the DIRT tracker is deliberately NOT stamped — the
        /// delta carried no dirt, and stamping would eat a pending one-shot dirt sync.</summary>
        public static void NoteOwnerDeltaApplied(string addressKey)
        {
            try
            {
                if (!MPClient.IsConnected || MPServer.IsRunning || string.IsNullOrEmpty(addressKey)) return;
                var snap = BuildSnapshot(addressKey);
                if (snap == null) return;
                var (hs, hv, hn, _) = ComputeHashes(snap);
                _lastLocalOwnerHashByAddr[addressKey] = hv;
                _lastLocalOwnerStructByAddr[addressKey] = hs;
                _lastLocalOwnerNonCargoByAddr[addressKey] = hn;
                _lastLocalOwnerVolatileAt[addressKey] = UnityEngine.Time.realtimeSinceStartup;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] NoteOwnerDeltaApplied: {ex.Message}"); }
        }

        // STAGE 3 (2026-08-25): ForwardGuestInteriorEdit — the guest WHOLE-REPLICA forward
        // (BuildingInteriorEdit, type 140) — is RETIRED.  Placement/removal forwards ride
        // ForwardGuestInteriorEditDelta (Stage 1b) and the designer close rides
        // ForwardGuestDesignerClose (Stage 2); a guest whole-set assertion is exactly the
        // class THE INVARIANT removes.  It had zero callers since Stage 2.

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
            // Round-172: "player-owned" here means owned by a REMOTE player — the host's own ledger
            // entries (legacy "host" OR its real pid, written since round-159 and by arbitration) are
            // HOST-owned, and the host IS authoritative for its own businesses.  The literal-only check
            // flagged the host's own shop non-authoritative, and the arbitration loser's machine then
            // refused to adopt the 754 items it was sent.
            bool playerOwned = MPServer.BuildingOwners.TryGetValue(addressKey, out var owner)
                               && !string.IsNullOrEmpty(owner) && !GameStatePatcher.IsHostLedgerId(owner);
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
                // Round-227: speaker radio state rides along (raw fields, not the
                // owner-gated getters — the snapshot must carry the STORED truth).
                try { snap.RadioStation = (int)reg.radioStation; snap.RadioVolume = reg.radioVolume; } catch { }
                if (reg.interiorDesigns != null)
                {
                    for (int i = 0; i < reg.interiorDesigns.Count; i++)
                    {
                        var d = reg.interiorDesigns[i];
                        if (d == null) continue;
                        snap.InteriorDesigns.Add(SerializeDesign(d));   // Stage 2: shared with the designer-close diff
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

        /// <summary>Stage 2: one design element → its wire DTO. Extracted verbatim from
        /// BuildSnapshot's loop so the designer-close diff serializes designs IDENTICALLY to a full
        /// snapshot — the ser-compare baselines only work if both paths produce the same bytes.</summary>
        internal static InteriorDesignInfo SerializeDesign(SerializedInteriorDesign d)
        {
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
            return dto;
        }

        internal static ItemInstanceInfo SerializeItemInstance(BigAmbitions.Items.ItemInstance ii)   // internal for Stage 1b's delta builder
        {
            var info = new ItemInstanceInfo
            {
                Id                  = ii.id?.ToString() ?? "",
                ItemName            = ii.itemName ?? "",
                Px = ii.position.x,  Py = ii.position.y,  Pz = ii.position.z,
                // Task-28 fix 4: Qx..Qw stay 0 — 'rotation' is obsolete since EA 0.11 (native
                // migrates it away at load, and its property getter MUTATES on read); yRotation
                // is the one live rotation and the receiver no longer writes the quaternion.
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

            // Task-28 fix 1: carry the factory-workstation subclass config.  WorkstationType
            // non-null is the discriminator the receiving deserializer keys on.
            if (ii is FactoryWorkstationInstance fw)
            {
                info.WorkstationType  = fw.workstationType ?? "";
                info.SelectedRecipeId = fw.selectedRecipeId ?? "";
                info.WsPriority       = fw.priority;
                info.ProduceUpTo      = fw.produceUpTo;
                info.ProduceUpToValue = fw.produceUpToValue;
            }

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
            if (ii.customPositions != null
                && !MPRegisterSync.IsPoisonedQueue(ii.customPositions, ii.position))   // round-143: never ship the staging artifact
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

        // ── Round-281: cargo sync (the cheap half of interior sync) ───────────────────────────────
        // Field bundles 20260818-22*: 86% of interior traffic carried NO structural change at all —
        // shelf stock ticking down as customers buy — yet every send shipped the whole interior (306
        // designs + 225 dirt spots, byte-identical every time).  Round-280 cut the RATE; this cuts the
        // SIZE: a cargo-only send becomes InteriorCargoSync, a small ABSOLUTE statement of the
        // building's cargo, and the structure stays where it belongs (the full snapshot).
        //
        // addressKey → the version number currently minted for that address, and the STRUCT HASH that
        // number was minted for.  Keeping the hash is what makes the mint idempotent across send sites:
        // any site can ask for "the version of this structure" and only a genuinely different structure
        // bumps the counter.  Deliberately NOT derived from the round-280 trackers — writing those from
        // an ungated site (SendSnapshotToPlayer) would silently change round-280's coalescing.
        private static readonly Dictionary<string, int> _structVersionByAddr     = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> _structVersionHashByAddr = new(StringComparer.Ordinal);
        // Throttled send instrumentation: sends since the last line, when we last spoke, and the reason
        // we last fell back to a full snapshot (a live-read register — it re-speaks when the reason
        // CHANGES rather than on a timer).
        private static readonly Dictionary<string, (int n, int lastItems, long bytes)> _cargoSendsByAddr = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, float>  _cargoSendLoggedAt   = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> _cargoFallbackReason = new(StringComparer.Ordinal);
        private const float CargoLogIntervalSeconds = 60f;

        /// <summary>The version number for the structure this snapshot describes.  Increments exactly
        /// when the structure hash changes for that address; identical structure always returns the
        /// same number, so the full snapshot and the cargo messages that follow it agree by
        /// construction.  Version 0 is never returned — it is reserved as "an older host said nothing",
        /// which is how a receiver tells a pre-round-281 host apart from one that stamped a real 0.</summary>
        private static int StructVersionFor(string addressKey, int structHash)
        {
            if (_structVersionHashByAddr.TryGetValue(addressKey, out var mintedFor) && mintedFor == structHash
                && _structVersionByAddr.TryGetValue(addressKey, out var cur) && cur > 0)
                return cur;
            _structVersionByAddr.TryGetValue(addressKey, out var next);
            next++;
            _structVersionByAddr[addressKey]     = next;
            _structVersionHashByAddr[addressKey] = structHash;
            return next;
        }

        /// <summary>Stamp a full snapshot with the version of the structure it carries.  Called at
        /// EVERY host→client full send — the number is the receiver's only way to know which structure
        /// the cargo messages that follow are talking about, so a single unstamped send would leave a
        /// receiver permanently re-requesting.</summary>
        private static int StampStructVersion(InteriorSnapshotPayload snap, int structHash)
        {
            int v = StructVersionFor(snap.AddressKey, structHash);
            snap.StructVersion = v;
            return v;
        }

        private static int StampStructVersion(InteriorSnapshotPayload snap)
            => StampStructVersion(snap, ComputeHashes(snap).structure);

        /// <summary>Is EVERY current subscriber of this building able to parse a cargo sync?  One
        /// non-capable subscriber and the answer is no for all of them — the alternative (cargo to
        /// some, full snapshots to others) means two receivers holding the same building from two
        /// different message streams, which is exactly the divergence class this codebase keeps
        /// paying for.  `why` names the first failing peer for the fallback log.</summary>
        private static bool AllSubscribersDeltaCapable(HashSet<int> subs, out string why)
        {
            why = "";
            if (subs == null || subs.Count == 0) { why = "no subscribers"; return false; }
            foreach (var peerId in subs)
                if (!MPServer.IsDeltaCapablePeer(peerId, out why)) return false;
            return true;
        }

        /// <summary>Build the cargo-only message from a snapshot we already built.  Every item is named,
        /// including the ones holding NOTHING: an emptied shelf must be able to convey through this
        /// channel, and a receiver cannot tell "absent because empty" from "absent because unchanged".
        /// That is what keeps the message absolute — apply it twice, apply it late, apply it after a
        /// dropped predecessor, and the building's cargo is correct either way.</summary>
        private static InteriorCargoSyncPayload BuildCargoSync(InteriorSnapshotPayload snap, int structVersion)
        {
            var cargo = new InteriorCargoSyncPayload
            {
                AddressKey    = snap.AddressKey,
                StructVersion = structVersion,
            };
            try { cargo.PlaythroughId = MPSaveCoordinator.ActivePlaythroughId ?? ""; } catch { }
            foreach (var it in snap.ItemInstances)
            {
                if (it == null || string.IsNullOrEmpty(it.Id)) continue;
                cargo.Items.Add(new InteriorCargoItemInfo
                {
                    Id             = it.Id,
                    // The snapshot's own DTO list — no copy, no second serializer.  The payload is
                    // serialized and discarded within this call chain, so sharing the reference cannot
                    // outlive the send.
                    CargoInstances = it.CargoInstances ?? new List<CargoInstanceInfo>(),
                });
            }
            return cargo;
        }

        /// <summary>The cargo-only send.  Returns FALSE when the caller must fall back to the full
        /// snapshot (a non-capable subscriber, or the send itself reached nobody) — the caller has
        /// already stamped the round-280 trackers, so a fallback costs a bigger message, never a lost
        /// update.</summary>
        private static bool TrySendCargoOnly(string addressKey, HashSet<int> subs, InteriorSnapshotPayload snap, int structVersion, string site)
        {
            if (!AllSubscribersDeltaCapable(subs, out var why))
            {
                NoteCargoFallback(addressKey, site, why);
                return false;
            }
            var cargo = BuildCargoSync(snap, structVersion);
            int bytes = MPServer.BroadcastInteriorCargoSyncTo(subs, cargo);
            if (bytes <= 0)
            {
                NoteCargoFallback(addressKey, site, "the cargo broadcast reached no peer");
                return false;
            }
            NoteCargoFallbackCleared(addressKey);
            NoteCargoSend(addressKey, cargo.Items.Count, bytes);
            return true;
        }

        /// <summary>Host-side send counter — one INFO line per address per minute.  A per-send line
        /// would out-shout the very traffic this round exists to reduce.</summary>
        private static void NoteCargoSend(string addressKey, int items, int bytes)
        {
            try
            {
                _cargoSendsByAddr.TryGetValue(addressKey, out var acc);
                acc = (acc.n + 1, items, acc.bytes + bytes);
                _cargoSendsByAddr[addressKey] = acc;
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (_cargoSendLoggedAt.TryGetValue(addressKey, out var at) && now - at < CargoLogIntervalSeconds) return;
                _cargoSendLoggedAt[addressKey] = now;
                _cargoSendsByAddr[addressKey] = (0, items, 0L);
                Plugin.Logger.LogInfo($"[InteriorSync] '{addressKey}': {acc.n} cargo sync(s), last {items} item(s), {acc.bytes / 1024}KB total (round-281 — these replaced full interior snapshots).");
            }
            catch { }
        }

        /// <summary>Fallback-to-full decisions speak once per address, and again whenever the REASON
        /// changes — a live read of the current situation rather than a timer, so "a v0.1.16 player
        /// walked in" and "…walked out again" are both visible without spamming the log in between.</summary>
        private static void NoteCargoFallback(string addressKey, string site, string why)
        {
            try
            {
                string reason = $"{site}: {why}";
                if (_cargoFallbackReason.TryGetValue(addressKey, out var prev) && prev == reason) return;
                _cargoFallbackReason[addressKey] = reason;
                Plugin.Logger.LogInfo($"[InteriorSync] '{addressKey}': cargo-only send FELL BACK to the full snapshot — {reason} (round-281). Everyone gets the full interior while this holds.");
            }
            catch { }
        }

        private static void NoteCargoFallbackCleared(string addressKey)
        {
            try
            {
                if (!_cargoFallbackReason.TryGetValue(addressKey, out var prev)) return;
                _cargoFallbackReason.Remove(addressKey);
                Plugin.Logger.LogInfo($"[InteriorSync] '{addressKey}': cargo-only sends RESUMED (was falling back — {prev}).");
            }
            catch { }
        }

        // ── Round-281 receiver-side stats (tripwire hygiene, contract item 6) ─────────────────────
        // Cargo applies must NOT feed NoteSnapshotApply: that counter's threshold (10/min) is
        // calibrated against the EXPENSIVE full apply, and a healthy round-281 shop legitimately
        // applies cargo far more often than that.  Feeding it would turn the round-213 resend-loop
        // tripwire into a permanent false alarm — and a tripwire that always fires is not a tripwire.
        // These get their own once-a-minute line instead, silent when nothing is arriving.
        private static int   _cargoAppliesMin, _cargoApplyItems;
        private static long  _cargoApplyBytes;
        private static readonly HashSet<string> _cargoApplyAddrs = new(StringComparer.Ordinal);
        private static float _cargoApplyLoggedAt = -999f;

        internal static void NoteCargoSyncApply(string addr, int items, int bytes)
        {
            try
            {
                _cargoAppliesMin++;
                _cargoApplyItems += items;
                _cargoApplyBytes += bytes;
                if (!string.IsNullOrEmpty(addr)) _cargoApplyAddrs.Add(addr);
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now - _cargoApplyLoggedAt < CargoLogIntervalSeconds) return;
                _cargoApplyLoggedAt = now;
                Plugin.Logger.LogInfo($"[InteriorSync] cargo sync applies: {_cargoAppliesMin}/min across {_cargoApplyAddrs.Count} address(es), {_cargoApplyItems} item entries, ~{_cargoApplyBytes / 1024}KB (round-281 — the cheap path; NOT counted by the round-213 resend tripwire).");
                _cargoAppliesMin = 0; _cargoApplyItems = 0; _cargoApplyBytes = 0; _cargoApplyAddrs.Clear();
            }
            catch { }
        }

        /// <summary>
        /// Order-sensitive hash over the snapshot's contents.  Collisions just
        /// mean one missed broadcast, recoverable on the next change.
        /// </summary>
        internal static int ComputeHash(InteriorSnapshotPayload snap) => ComputeHashes(snap).full;

        /// <summary>v10 review M1: the owner-cache change detector. `full` alone no longer sees
        /// dirt, and HandleOwnerSnapshot's `changed` gate decides whether the host APPLIES a push
        /// to its own world — a dirt-only force push (owner re-entering a shop that dirtied while
        /// they were away) must not be judged "unchanged", or the host's world and save keep the
        /// stale values indefinitely.</summary>
        private static int CacheHash(InteriorSnapshotPayload snap)
        {
            var (_, f, _, d) = ComputeHashes(snap);
            unchecked { return f * 31 + d; }
        }

        // Round-213 state: split-gate bookkeeping (subscriber + owner ticks) and the
        // re-send-loop detector.
        private const float VolatileCoalesceSeconds = 12f;
        private static readonly Dictionary<string, int>   _lastStructHashByAddr       = new(StringComparer.Ordinal);
        // Round-281: last-sent "everything except cargo" hash — the send-side discriminator that turns
        // round-280's "structure unchanged" into the stronger, and actually correct, "cargo and nothing
        // else changed".  Stamped wherever the other three are, on the subscriber paths only.
        private static readonly Dictionary<string, int>   _structVolHashByAddr        = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> _volatileSentAtByAddr       = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, int>   _lastLocalOwnerStructByAddr = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> _lastLocalOwnerVolatileAt   = new(StringComparer.Ordinal);
        // v10 (T7): the dirt band's baselines (host subscriber tick / owner tick) and the owner
        // tick's non-cargo baseline (the client→host cargo-only discriminator).
        private static readonly Dictionary<string, int>   _lastDirtHashByAddr         = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, int>   _lastLocalOwnerDirtByAddr   = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, int>   _lastLocalOwnerNonCargoByAddr = new(StringComparer.Ordinal);

        /// <summary>v10: the absolute dirty-set for one building — every lattice spot with a
        /// non-zero value; the receiver zeroes the rest (see MessageType.InteriorDirtSync).
        /// Review B2: entries are lattice-INDEX keyed (X/Z verify) — (X, Z) collides across
        /// stacked storeys, and the snapshot's DirtSpots list preserves the native lattice order.</summary>
        private static InteriorDirtSyncPayload BuildDirtSync(InteriorSnapshotPayload snap)
        {
            var p = new InteriorDirtSyncPayload { AddressKey = snap.AddressKey };
            try { p.PlaythroughId = MPSaveCoordinator.ActivePlaythroughId ?? ""; } catch { }
            for (int i = 0; i < snap.DirtSpots.Count; i++)
            {
                var ds = snap.DirtSpots[i];
                if (ds != null && ds.Dirtiness > 0f)
                    p.Spots.Add(new DirtSpotDeltaInfo { Index = i, X = ds.X, Z = ds.Z, Dirtiness = ds.Dirtiness });
            }
            return p;
        }

        /// <summary>v10 (review m4): does THIS machine own the building at addressKey? Guards the
        /// host→owner full-repush request so a host cannot make a guest upload an arbitrary replica.</summary>
        internal static bool OwnsAddressLocally(string addressKey)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null || string.IsNullOrEmpty(addressKey)) return false;
                foreach (var r in gi.BuildingRegistrations)
                    if (r != null && GameStateReader.AddressKey(r) == addressKey) return IsLocalOwnerBusiness(r);
                return false;
            }
            catch { return false; }
        }

        /// <summary>v10 (T7): HOST — an owner's cargo-only upload for a building this host caches.
        /// Grafts the cargo onto the cached owner snapshot ONLY when the cache's structure hash
        /// matches the sender's (the graft is then exact: same item set, cargo band wholly
        /// replaced — absolute state, not a diff-chain); any doubt → ask the owner for a full
        /// push. The Tick's untouched trackers then relay the change to subscribers through the
        /// normal split gate. MAIN THREAD (marshalled by the dispatcher).</summary>
        public static void HandleOwnerCargoSync(MPLink peer, string playerId, InteriorCargoSyncPayload p)
        {
            if (!MPServer.IsRunning || peer == null || p == null || string.IsNullOrEmpty(p.AddressKey)) return;
            try
            {
                if (!HostKnowsPlayerOwnsAddress(playerId, p.AddressKey))
                {
                    Plugin.Logger.LogWarning($"[InteriorSync] owner cargo sync rejected: '{playerId}' is not the recorded owner of '{p.AddressKey}'.");
                    return;
                }
                if (!_ownerSnapshotsByAddr.TryGetValue(p.AddressKey, out var state) || state?.Snapshot == null)
                {
                    Plugin.Logger.LogInfo($"[InteriorSync] owner cargo sync for '{p.AddressKey}': no cached full snapshot to graft onto — requesting a full push.");
                    MPServer.RequestOwnerInteriorResync(peer, p.AddressKey);
                    return;
                }
                var cache = state.Snapshot;
                // Review M3: verify BOTH the structure band and the non-cargo volatile band — the
                // owner's baselines advance even when a full push was refused (sanity gates,
                // ownership handover window), and structure alone would let a graft freeze stale
                // item state (StateIndex) into the cache every guest is served from.
                var (cacheStruct, _, cacheNonCargo, _) = ComputeHashes(cache);
                cacheStruct   = cacheStruct   == 0 ? 1 : cacheStruct;     // mirror the sender's 0→1 remap
                cacheNonCargo = cacheNonCargo == 0 ? 1 : cacheNonCargo;
                if (p.OwnerStructHash == 0 || p.OwnerNonCargoHash == 0
                    || cacheStruct != p.OwnerStructHash || cacheNonCargo != p.OwnerNonCargoHash)
                {
                    Plugin.Logger.LogWarning($"[InteriorSync] owner cargo sync for '{p.AddressKey}': hash mismatch (cache s={cacheStruct}/n={cacheNonCargo} vs sender s={p.OwnerStructHash}/n={p.OwnerNonCargoHash}) — requesting a full push.");
                    MPServer.RequestOwnerInteriorResync(peer, p.AddressKey);
                    return;
                }
                var byId = new Dictionary<string, List<CargoInstanceInfo>>(StringComparer.Ordinal);
                foreach (var it in p.Items) if (it != null && !string.IsNullOrEmpty(it.Id)) byId[it.Id] = it.CargoInstances ?? new List<CargoInstanceInfo>();
                int grafted = 0, unknown = 0;
                foreach (var it in cache.ItemInstances)
                {
                    if (it == null || string.IsNullOrEmpty(it.Id)) continue;
                    if (byId.TryGetValue(it.Id, out var cargo)) { it.CargoInstances = cargo; byId.Remove(it.Id); grafted++; }
                }
                unknown = byId.Count;
                if (unknown > 0)
                {
                    // Equal structure hashes should preclude this — treat it as divergence, not noise.
                    Plugin.Logger.LogWarning($"[InteriorSync] owner cargo sync for '{p.AddressKey}': {unknown} item(s) not in the cache despite matching structure — requesting a full push.");
                    MPServer.RequestOwnerInteriorResync(peer, p.AddressKey);
                    return;
                }
                state.Hash = CacheHash(cache);   // post-graft recompute — the graft moved the full band
                // Review M2: same relay-clock clear as HandleOwnerSnapshot — the inflow is already
                // owner-throttled, so the next 2 s beat may carry it to subscribers.
                _volatileSentAtByAddr.Remove(p.AddressKey);
                // The host's own world adopts the same cargo (the apply diffs internally and
                // reports cargoOnly=N); subscribers get it from the next Tick beat, whose
                // trackers this handler deliberately does not touch.
                GameStatePatcher.ApplyInteriorSnapshot(cache);
                Plugin.Logger.LogInfo($"[InteriorSync] owner cargo sync accepted from '{playerId}' addr='{p.AddressKey}': {grafted} item(s) grafted (v10).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] HandleOwnerCargoSync: {ex.Message}"); }
        }

        /// <summary>v10 (T7/ruling 33): HOST — an owner's dirt-values upload. Updates the cached
        /// snapshot's dirt band and the host's own world; subscribers get it from the next Tick
        /// beat (dirt band tracker untouched here). MAIN THREAD (marshalled).</summary>
        public static void HandleOwnerDirtSync(MPLink peer, string playerId, InteriorDirtSyncPayload p)
        {
            if (!MPServer.IsRunning || peer == null || p == null || string.IsNullOrEmpty(p.AddressKey)) return;
            try
            {
                if (!HostKnowsPlayerOwnsAddress(playerId, p.AddressKey))
                {
                    Plugin.Logger.LogWarning($"[InteriorSync] owner dirt sync rejected: '{playerId}' is not the recorded owner of '{p.AddressKey}'.");
                    return;
                }
                if (_ownerSnapshotsByAddr.TryGetValue(p.AddressKey, out var state) && state?.Snapshot?.DirtSpots != null)
                {
                    // Review B2: index-keyed with X/Z verify — same rule as the world apply.
                    var cacheSpots = state.Snapshot.DirtSpots;
                    var byIndex = new Dictionary<int, DirtSpotDeltaInfo>();
                    foreach (var s in p.Spots) if (s != null) byIndex[s.Index] = s;
                    for (int i = 0; i < cacheSpots.Count; i++)
                    {
                        var ds = cacheSpots[i];
                        if (ds == null) continue;
                        if (byIndex.TryGetValue(i, out var sent))
                        { if (sent.X == ds.X && sent.Z == ds.Z) ds.Dirtiness = sent.Dirtiness; }
                        else ds.Dirtiness = 0f;
                    }
                    state.Hash = CacheHash(state.Snapshot);   // review M1: dirt is part of the cache hash now
                }
                GameStatePatcher.ApplyInteriorDirtSync(p);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] HandleOwnerDirtSync: {ex.Message}"); }
        }
        private static readonly Dictionary<string, Queue<float>> _applyTimes   = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, float>        _loopWarnedAt = new(StringComparer.Ordinal);

        /// <summary>Round-213 detector: repeated full snapshots for one address are the
        /// SILENT re-send loop - no exception, no warning, invisible to every triage
        /// lens until someone eyeballs a grep (rig 2026-08-01: 15x the same 754-item
        /// interior). Called from the receiver apply; warns loudly, throttled.</summary>
        internal static void NoteSnapshotApply(string addr)
        {
            try
            {
                if (string.IsNullOrEmpty(addr)) return;
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (!_applyTimes.TryGetValue(addr, out var q)) _applyTimes[addr] = q = new Queue<float>();
                q.Enqueue(now);
                while (q.Count > 0 && now - q.Peek() > 60f) q.Dequeue();
                // Threshold 10: the COALESCED steady state is ~6/min (five 12s volatile
                // beats + the on-entry send — rig-measured 2026-08-01, exactly 6 fired a
                // false warn); a genuine loop runs ~30/min. 10 splits them cleanly.
                if (q.Count >= 10 && (!_loopWarnedAt.TryGetValue(addr, out var w) || now - w > 120f))
                {
                    _loopWarnedAt[addr] = now;
                    Plugin.Logger.LogWarning($"[InteriorSync] RESEND LOOP: '{addr}' received {q.Count} interior snapshots in 60s - volatile churn is supposed to coalesce (round-213); include this log in a report.");
                }
            }
            catch { }
        }

        /// <summary>Round-213: one pass, two hashes. `structure` covers owner-meaningful
        /// state - layout, designs, retail prices, the item set with positions, queue
        /// points (round-137), aliases, sign text/product (round-190b), factory config
        /// (task-28), paint (round-99d). `full` additionally folds the VOLATILE fields
        /// that churn continuously in a customer-filled shop: dirt values (the X/Z
        /// lattice itself is fixed), item StateIndex, and cargo. All rounding rules are
        /// unchanged from the single-hash version this replaces.
        /// ROUND-281 adds a third: `structAndNonCargo` = everything EXCEPT cargo.  The
        /// round-280 gate's "structure didn't change" is NOT the same statement as "this
        /// is a cargo-only send" — dirt dirtiness and item StateIndex are volatile too,
        /// and routing a dirt change onto the cargo-only channel would strand it until
        /// some unrelated structural edit forced a full snapshot.  This hash is what
        /// lets the sender say cargo-only and MEAN it: `full` differs while this one
        /// matches ⇔ the delta is cargo and nothing else.</summary>
        internal static (int structure, int full, int structAndNonCargo, int dirt) ComputeHashes(InteriorSnapshotPayload snap)
        {
            unchecked
            {
                int hs = 17, hv = 17, hn = 17, hd = 17;
                void S(int v) { hs = hs * 31 + v; hv = hv * 31 + v; hn = hn * 31 + v; }   // structure -> the three snapshot bands
                void V(int v) { hv = hv * 31 + v; hn = hn * 31 + v; }   // non-cargo volatile (item state) -> full + structAndNonCargo
                void C(int v) { hv = hv * 31 + v; }                     // cargo -> full only
                void D(int v) { hd = hd * 31 + v; }                     // v10 (T7/ruling 33): dirt VALUES -> their own band ONLY —
                                                                        // dirt churn no longer perturbs `full`, so it can never
                                                                        // trigger a snapshot/cargo send; it rides InteriorDirtSync.

                S(MPAudit.StableHash(snap.Layout));
                S(snap.RadioStation); S(((int)System.Math.Round(snap.RadioVolume * 100f)));   // round-227
                foreach (var d in snap.InteriorDesigns)
                {
                    S(MPAudit.StableHash(d.UUID));
                    foreach (var m in d.Materials) { S(MPAudit.StableHash(m.MaterialID)); S(m.MaterialIndex); S(m.ColorIndex); }
                }
                foreach (var rp in snap.RetailPrices) { S(MPAudit.StableHash(rp.ItemName)); S(rp.Price.GetHashCode()); }
                foreach (var ds in snap.DirtSpots)
                {
                    S(ds.X); S(ds.Z);
                    D(((int)System.Math.Round(ds.Dirtiness * 10f)).GetHashCode());   // 0.1 steps, as before
                }
                foreach (var it in snap.ItemInstances)
                {
                    S(MPAudit.StableHash(it.Id));
                    S(MPAudit.StableHash(it.ItemName));
                    S(((int)System.Math.Round(it.Px * 100f)).GetHashCode());
                    S(((int)System.Math.Round(it.Py * 100f)).GetHashCode());
                    S(((int)System.Math.Round(it.Pz * 100f)).GetHashCode());
                    S(((int)System.Math.Round(it.YRotation * 10f)).GetHashCode());
                    var cps = it.CustomPositions;
                    S(cps?.Count ?? 0);
                    if (cps != null)
                        foreach (var cp in cps) { S(((int)System.Math.Round(cp.X * 10f)).GetHashCode()); S(((int)System.Math.Round(cp.Z * 10f)).GetHashCode()); }
                    V(it.StateIndex);
                    S(MPAudit.StableHash(it.Alias));
                    S(MPAudit.StableHash(it.WorldSpaceTextValue));
                    S(MPAudit.StableHash(it.LinkedItemName));
                    if (it.WorkstationType != null)
                    {
                        S(MPAudit.StableHash(it.WorkstationType));
                        S(MPAudit.StableHash(it.SelectedRecipeId));
                        S(it.WsPriority);
                        S(it.ProduceUpTo ? 1 : 0);
                        S(it.ProduceUpToValue);
                    }
                    foreach (var cc in it.CustomColors) { S(cc.Channel); S(cc.ColorPacked); }
                    foreach (var c in it.CargoInstances)
                    {
                        // Round-281: cargo folds into `full` ONLY (C, not V) — that is precisely what
                        // makes "full changed, structAndNonCargo didn't" mean "cargo and nothing else".
                        C(MPAudit.StableHash(c.ItemName));
                        C(c.Amount);
                        // Round-242 (field 20260803-224740, FOURTH member of the round-99
                        // attribute-omitted class): paying at a register flips ONLY this flag —
                        // with it absent here the publisher saw "no change", never re-sent, and
                        // every other machine kept the buyer's boxes unpaid until some other
                        // cargo change forced a publish. Serializer/deserializer/IdentitySig all
                        // carried it already; this hash was the one blind layer.
                        C(c.Paid ? 1 : 0);
                        C(((int)System.Math.Round(c.PricePerUnit * 100f)).GetHashCode());
                        foreach (var cc in c.CustomColors) { C(cc.Channel); C(cc.ColorPacked); }
                    }
                }
                return (hs, hv, hn, hd);
            }
        }
    }
}
