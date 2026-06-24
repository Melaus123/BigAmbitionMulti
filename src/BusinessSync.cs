using System;
using System.Collections.Generic;
using Buildings;
using Helpers;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Host-side change detector for the exterior business state (Phase 1 of
    /// the business-sync feature).
    ///
    /// Approach: every PollIntervalSeconds the host walks every BuildingRegistration,
    /// reads the fields we care about into a BusinessInfo, and compares them
    /// against the last broadcast version.  Buildings whose info changed get
    /// a BusinessChange message; all clients receive it.
    ///
    /// Change detection is polling-based rather than Harmony-patched setters
    /// because: (a) we'd need to find every setter for every relevant field,
    /// (b) some fields are nested structs (SignAppearanceSettings, LogoSettings)
    /// which makes per-setter patching even more fragile, (c) business edits
    /// are rare so the 2-second polling lag is invisible.
    ///
    /// Per-business value cache uses the address string as the key.  Equality
    /// is checked by comparing the freshly-read BusinessInfo against the last
    /// one we sent.
    /// </summary>
    public static class BusinessSync
    {
        private const float PollIntervalSeconds = 2f;

        private static readonly Dictionary<string, BusinessInfo> _lastSent = new();

        /// <summary>Owner-authoritative open/closed truth per business address (0 = unknown, 1 = open,
        /// 2 = closed). Written when a BusinessInfo is received/relayed (GameStatePatcher apply) and read by
        /// the IsBusinessOpen patch so non-owners use the OWNER's computed open-state instead of re-deriving
        /// it from a replicated schedule. Cleared on session reset.</summary>
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> OwnerOpenByAddress = new();

        /// <summary>
        /// Build the full table — used by Hello/Welcome to bootstrap a connecting
        /// client.  Also populates _lastSent so the first Tick after a connect
        /// won't re-broadcast everything as deltas.
        /// </summary>
        public static BusinessSnapshotPayload BuildFullSnapshot()
        {
            var snap = new BusinessSnapshotPayload();
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return snap;

                // Per-BuildingType counters for the Phase 1b residential/warehouse
                // discrepancy investigation.  AvailableForRent turned out to be
                // false on every reg on both sides — so the for-rent map filter
                // reads something else.  Track the candidate signals:
                //   tot      = total regs of that type
                //   empty    = BusinessTypeName == Empty (no business)
                //   playerR  = RentedByPlayer true
                //   rivalOwn = buildingOwnerRivalId not empty (AI owns it)
                //   rivalBiz = businessOwnerRivalId not empty (AI runs biz)
                var stats = new TypeStats();
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    var info = ReadInfo(reg);
                    if (info == null) continue;
                    snap.Businesses.Add(info);
                    _lastSent[info.AddressKey] = info;
                    stats.Accumulate(reg, info);
                }

                stats.Log("BuildFullSnapshot", snap.Businesses.Count);
                LogForSaleAndRealEstate("BuildFullSnapshot", gi);
                LogSceneCBCCounts("BuildFullSnapshot");

                // ── Buy marketplace (gi.buildingsForSale) ────────────────────
                // The host's authoritative for-sale list.  Client wipes its
                // local list and rebuilds from this every snapshot.
                ReadBuildingsForSale(gi, snap);
                _lastSentForSaleHash = ComputeForSaleHash(snap.BuildingsForSale);
                Plugin.Logger.LogInfo($"[BusinessSync] BuildFullSnapshot.BuildingsForSale: count={snap.BuildingsForSale.Count} hash=0x{_lastSentForSaleHash:X8}");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BusinessSync] BuildFullSnapshot: {ex.Message}"); }
            return snap;
        }

        private static int _lastSentForSaleHash = 0;

        /// <summary>Read every entry from gi.buildingsForSale into the snapshot.</summary>
        private static void ReadBuildingsForSale(GameInstance gi, BusinessSnapshotPayload snap)
        {
            try
            {
                var lst = gi.buildingsForSale;
                if (lst == null) return;
                for (int i = 0; i < lst.Count; i++)
                {
                    var bfs = lst[i];
                    if (bfs == null) continue;
                    try
                    {
                        var reg = bfs.BuildingRegistration;
                        string addr = reg != null ? GameStateReader.AddressKey(reg) : "";
                        if (string.IsNullOrEmpty(addr)) continue;
                        snap.BuildingsForSale.Add(new BuildingForSaleInfo
                        {
                            AddressKey      = addr,
                            BuildingPrice   = bfs.buildingPrice,
                            SquareMeters    = bfs.squareMeters,
                            AcceptOfferRate = bfs.acceptOfferRate,
                        });
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[BusinessSync] ReadBuildingsForSale entry {i}: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BusinessSync] ReadBuildingsForSale: {ex.Message}"); }
        }

        /// <summary>
        /// Cheap order-sensitive hash over the buildingsForSale list so we can
        /// detect changes without keeping a full prior snapshot.  Changes are
        /// rare (the game updates the list once a day) so a hash collision
        /// risk of one missed update is fine.
        /// </summary>
        private static int ComputeForSaleHash(System.Collections.Generic.List<BuildingForSaleInfo> list)
        {
            unchecked
            {
                int h = 17;
                if (list == null) return h;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    h = h * 31 + (e.AddressKey?.GetHashCode() ?? 0);
                    h = h * 31 + e.BuildingPrice.GetHashCode();
                    h = h * 31 + e.SquareMeters;
                    h = h * 31 + e.AcceptOfferRate.GetHashCode();
                }
                return h;
            }
        }

        /// <summary>
        /// Polls gi.buildingsForSale on the host every Tick.  If the list's hash
        /// changed (entries added, removed, or prices updated by the daily
        /// RealEstateHelper.RunDaily), broadcast a fresh BusinessSnapshot to all
        /// clients so they re-mirror the buy marketplace.
        /// </summary>
        public static void CheckBuildingsForSaleChange()
        {
            if (!MPServer.IsRunning) return;
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return;
                var probe = new BusinessSnapshotPayload();
                ReadBuildingsForSale(gi, probe);
                int hash = ComputeForSaleHash(probe.BuildingsForSale);
                if (hash == _lastSentForSaleHash) return;
                Plugin.Logger.LogInfo($"[BusinessSync] BuildingsForSale changed (hash 0x{_lastSentForSaleHash:X8} → 0x{hash:X8}, count={probe.BuildingsForSale.Count}). Broadcasting full snapshot.");
                _lastSentForSaleHash = hash;
                MPServer.BroadcastBusinessSnapshot();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BusinessSync] CheckBuildingsForSaleChange: {ex.Message}"); }
        }

        /// <summary>
        /// Walks every CityBuildingController GameObject in the scene and counts
        /// per BuildingType.  Compare to gi.BuildingRegistrations: if the CBC
        /// scene-object count differs from the registration count, the map UI
        /// is iterating over a different set of buildings than what's in the
        /// save list — which would explain map-filter discrepancies even when
        /// gi.BuildingRegistrations is identical on both sides.
        /// </summary>
        public static void LogSceneCBCCounts(string label)
        {
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType(typeof(CityBuildingController));
                if (all == null) { Plugin.Logger.LogInfo($"[BusinessSync] {label} CBC scan: (null)"); return; }
                int total = all.Length;
                int[] byType = new int[7];
                int[] forRentByType = new int[7];   // CBCs whose reg has BusinessTypeName==Empty
                int noReg = 0;
                int unknownType = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    var cbc = all[i] as CityBuildingController;
                    if (cbc == null) continue;
                    var reg = cbc.buildingRegistration;
                    if (reg == null) { noReg++; continue; }
                    int idx = TryGetBuildingTypeIndex(reg);
                    if (idx < 0 || idx >= 7) { unknownType++; continue; }
                    byType[idx]++;
                    try { if (reg.businessTypeName == "ba:businesstype_empty") forRentByType[idx]++; } catch { }
                }
                string[] names = { "Residential", "Retail", "Office", "Warehouse", "Special", "Cinema", "Theater" };
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 7; i++)
                    sb.Append($"{names[i]}={byType[i]}/{forRentByType[i]}empty ");
                Plugin.Logger.LogInfo($"[BusinessSync] {label} sceneCBCs: total={total} noReg={noReg} unknownType={unknownType} | {sb}");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BusinessSync] LogSceneCBCCounts: {ex.Message}"); }
        }

        /// <summary>
        /// Logs gi.buildingsForSale and gi.realEstate counts + a per-type breakdown.
        /// These lists drive CityMapFilters.IsBuildingForSale; a building in either
        /// list is hidden from the "for rent" filter.  If host and client differ
        /// in either list, that explains map-filter discrepancies even when all
        /// the BuildingRegistration fields are identical.
        /// </summary>
        public static void LogForSaleAndRealEstate(string label, GameInstance gi)
        {
            try
            {
                int forSaleCount  = 0;
                int realEstateCount = 0;
                int[] forSaleByType   = new int[7];
                int[] realEstByType   = new int[7];

                try
                {
                    var lst = gi.buildingsForSale;
                    if (lst != null)
                        for (int i = 0; i < lst.Count; i++)
                        {
                            forSaleCount++;
                            try
                            {
                                var b = lst[i]?.Building;
                                if (b != null)
                                {
                                    int idx = BuildingTypeIndex(b.BuildingType);
                                    if (idx >= 0 && idx < 7) forSaleByType[idx]++;
                                }
                            }
                            catch { }
                        }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[BusinessSync] buildingsForSale enum: {ex.Message}"); }

                try
                {
                    var lst = gi.realEstate;
                    if (lst != null)
                        for (int i = 0; i < lst.Count; i++)
                        {
                            realEstateCount++;
                            try
                            {
                                var b = lst[i]?.Building;
                                if (b != null)
                                {
                                    int idx = BuildingTypeIndex(b.BuildingType);
                                    if (idx >= 0 && idx < 7) realEstByType[idx]++;
                                }
                            }
                            catch { }
                        }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[BusinessSync] realEstate enum: {ex.Message}"); }

                string[] names = { "Residential", "Retail", "Office", "Warehouse", "Special", "Cinema", "Theater" };
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 7; i++)
                    sb.Append($"{names[i]}={forSaleByType[i]}/{realEstByType[i]} ");
                Plugin.Logger.LogInfo($"[BusinessSync] {label} forSale/realEstate: forSale={forSaleCount} realEstate={realEstateCount} | {sb}");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BusinessSync] LogForSaleAndRealEstate: {ex.Message}"); }
        }

        // ── Diagnostic helpers (Phase 1b residential/warehouse discrepancy) ─────

        /// <summary>EA 0.11 building-type ids → our 7 histogram buckets
        /// (Residential=0..Theater=6, matching the legacy enum order).</summary>
        public static int BuildingTypeIndex(string? buildingType) => buildingType switch
        {
            "ba:buildingtype_residential" => 0,
            "ba:buildingtype_retail"      => 1,
            "ba:buildingtype_office"      => 2,
            "ba:buildingtype_warehouse"   => 3,
            "ba:buildingtype_special"     => 4,
            "ba:buildingtype_cinema"      => 5,
            "ba:buildingtype_theater"     => 6,
            _ => -1,
        };

        /// <summary>
        /// Returns the BuildingType ordinal (0..6) for a registration via its
        /// cached Building reference, or -1 if unavailable.
        /// </summary>
        public static int TryGetBuildingTypeIndex(BuildingRegistration reg)
        {
            try
            {
                var b = reg.BuildingCached;
                if (b == null) return -1;
                return BuildingTypeIndex(b.BuildingType);
            }
            catch { return -1; }
        }

        /// <summary>
        /// Per-BuildingType accumulator over the candidate "for rent" signals.
        /// Lets us tell which of the candidate fields actually drives the map
        /// filter — whichever differs between host and client.
        /// </summary>
        public class TypeStats
        {
            // 7 building types: Residential=0, Retail=1, Office=2, Warehouse=3,
            // Special=4, Cinema=5, Theater=6.
            public int[] Total          = new int[7];
            public int[] AvailForRent   = new int[7];    // BuildingRegistration.AvailableForRent
            public int[] EmptyBizType   = new int[7];    // BusinessTypeName == 0 (Empty)
            public int[] PlayerRented   = new int[7];    // RentedByPlayer
            public int[] RivalOwns      = new int[7];    // buildingOwnerRivalId != ""
            public int[] RivalBiz       = new int[7];    // businessOwnerRivalId != ""
            public int   UnknownType    = 0;

            public void Accumulate(BuildingRegistration reg, BusinessInfo info)
            {
                int idx = TryGetBuildingTypeIndex(reg);
                if (idx < 0 || idx >= 7) { UnknownType++; return; }
                Total[idx]++;
                if (info.AvailableForRent)                                Total[idx] = Total[idx];   // no-op; kept clear
                if (info.AvailableForRent)                                AvailForRent[idx]++;
                if (info.BusinessTypeName == "ba:businesstype_empty")     EmptyBizType[idx]++;
                try { if (reg.RentedByPlayer)                              PlayerRented[idx]++; } catch { }
                try { if (!string.IsNullOrEmpty(reg.buildingOwnerRivalId)) RivalOwns[idx]++;    } catch { }
                try { if (!string.IsNullOrEmpty(reg.businessOwnerRivalId)) RivalBiz[idx]++;     } catch { }
            }

            /// <summary>
            /// Variant for client-side counting where we don't have a BusinessInfo —
            /// reads the candidate fields directly from the registration.
            /// </summary>
            public void AccumulateFromReg(BuildingRegistration reg)
            {
                int idx = TryGetBuildingTypeIndex(reg);
                if (idx < 0 || idx >= 7) { UnknownType++; return; }
                Total[idx]++;
                try { if (reg.AvailableForRent)                            AvailForRent[idx]++;  } catch { }
                try { if (reg.businessTypeName == "ba:businesstype_empty")                  EmptyBizType[idx]++;  } catch { }
                try { if (reg.RentedByPlayer)                              PlayerRented[idx]++;  } catch { }
                try { if (!string.IsNullOrEmpty(reg.buildingOwnerRivalId)) RivalOwns[idx]++;     } catch { }
                try { if (!string.IsNullOrEmpty(reg.businessOwnerRivalId)) RivalBiz[idx]++;      } catch { }
            }

            public void Log(string label, int totalRegs)
            {
                string[] names = { "Residential", "Retail", "Office", "Warehouse", "Special", "Cinema", "Theater" };
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 7; i++)
                {
                    sb.Append($"{names[i]}={Total[i]}");
                    sb.Append($"[avail={AvailForRent[i]} empty={EmptyBizType[i]} playerR={PlayerRented[i]} rivalOwn={RivalOwns[i]} rivalBiz={RivalBiz[i]}] ");
                }
                Plugin.Logger.LogInfo($"[BusinessSync] {label}: total={totalRegs} unknownType={UnknownType} | {sb}");
            }
        }

        /// <summary>Back-compat shim retained for the simpler client diagnostic call sites.</summary>
        public static void LogTypeBreakdown(string label, int[] cntTotal, int[] cntForRent, int unknown, int totalRegs)
        {
            string[] names = { "Residential", "Retail", "Office", "Warehouse", "Special", "Cinema", "Theater" };
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 7; i++)
                sb.Append($"{names[i]}={cntTotal[i]}/{cntForRent[i]}forRent ");
            Plugin.Logger.LogInfo($"[BusinessSync] {label}: totalRegs={totalRegs} unknownType={unknown} | {sb}");
        }

        // ── Time-boxed host sweep ─────────────────────────────────────────────
        // Reading all ~825 registrations in one frame cost 70–150ms of main-
        // thread time every PollIntervalSeconds (a visible stutter cadence,
        // profiler-measured).  Instead, walk the list with a persistent cursor
        // and stop after HostScanBudgetMs per frame — a full cycle completes in
        // ~15–25 frames (a fraction of a second), then the next cycle starts no
        // sooner than PollIntervalSeconds after the previous one BEGAN.  Worst-
        // case change-detection latency rises to PollInterval + sweep length;
        // rent/ownership stays instant (event-driven, not this poll).
        private const float HostScanBudgetMs = 6f;
        private static bool  _scanInProgress;
        private static int   _scanCursor;
        private static float _cycleStartedAt;
        private static int   _cycleChanges;

        /// <summary>
        /// Called once per Update on the host.  Runs the change detector as a
        /// time-boxed incremental sweep (resumes every frame until complete).
        /// </summary>
        // ── Market events (Phase A of fair-rival economy, 2026-06-12) ─────────
        // gi.marketEvents (shortages/hype/backorders) is generated host-side
        // only (clients suppress the sim) and drives shelf fills + price
        // multipliers locally — without this sync clients never see ANY event.
        private static int _lastMarketEventsHash;
        private static float _nextMarketEventsAt;

        private static void TickMarketEvents()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _nextMarketEventsAt) return;
            _nextMarketEventsAt = now + 5f;
            try
            {
                var events = SaveGameManager.Current?.marketEvents;
                if (events == null) return;
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(events);
                int h = MPAudit.StableHash(json);
                if (h == _lastMarketEventsHash) return;
                _lastMarketEventsHash = h;
                MPServer.BroadcastMarketEvents(json);
                Plugin.Logger.LogInfo($"[BusinessSync] market events changed ({events.Count} event(s)) — broadcast.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BusinessSync] market events: {ex.Message}"); }
        }

        public static void Tick()
        {
            if (!MPServer.IsRunning) return;
            // During the startup hold the initial full table is delivered per-client by
            // SendWorldStateTo (which also seeds _lastSent + the for-sale hash).  Running
            // the change-detector / for-sale broadcast here too would send a SECOND full
            // table to everyone (the duplicate seen in the load profile).  Skip until the
            // world is live; steady-state deltas resume after release.
            if (TimeSync.IsStartupHeld) return;
            TickMarketEvents();
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (!_scanInProgress)
            {
                if (now - _cycleStartedAt < PollIntervalSeconds) return;
                _cycleStartedAt = now;
                _scanInProgress = true;
                _scanCursor     = 0;
                _cycleChanges   = 0;
            }

            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) { _scanInProgress = false; return; }
                var regs  = gi.BuildingRegistrations;
                int count = regs.Count;
                var sw = System.Diagnostics.Stopwatch.StartNew();

                while (_scanCursor < count)
                {
                    var reg = regs[_scanCursor];
                    _scanCursor++;
                    if (reg != null)
                    {
                        var info = ReadInfo(reg);
                        if (info != null
                            && !(_lastSent.TryGetValue(info.AddressKey, out var prev) && EqualInfo(prev, info)))
                        {
                            _lastSent[info.AddressKey] = info;
                            MPServer.BroadcastBusinessChange(info);
                            _cycleChanges++;
                            // Log only non-trivial changes (something that has a name or
                            // isn't BusinessTypeName.Empty) so we don't spam at startup.
                            if (!string.IsNullOrEmpty(info.BusinessName) || info.BusinessTypeName != "ba:businesstype_empty"
                                || !string.IsNullOrEmpty(info.BuildingOwnerRivalId)
                                || !string.IsNullOrEmpty(info.BusinessOwnerRivalId)
                                || info.RentedByPlayer)
                            {
                                Plugin.Logger.LogInfo($"[BusinessSync] Sent change {info.AddressKey}: name='{info.BusinessName}' type={info.BusinessTypeName} owners[bldg='{info.BuildingOwnerRivalId}' biz='{info.BusinessOwnerRivalId}' rented={info.RentedByPlayer}] sign=0x{info.SignLightPacked:X8}");
                            }
                        }
                    }
                    if (sw.ElapsedMilliseconds >= HostScanBudgetMs) return;   // out of budget — resume next frame
                }

                // Cycle complete.
                _scanInProgress = false;
                if (_cycleChanges > 0)
                    Plugin.Logger.LogInfo($"[BusinessSync] Broadcast {_cycleChanges} business change(s) this sweep.");

                // Buy marketplace (gi.buildingsForSale) — host's daily real-
                // estate update modifies this list once per game day.  Hash-
                // diff once per completed sweep; re-broadcast the full snapshot
                // when it changes.  Cheap: hash compute is O(N), N ~10-15 entries.
                CheckBuildingsForSaleChange();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BusinessSync] Tick: {ex.Message}"); _scanInProgress = false; }
        }

        // ── Client → host: push THIS player's owned-business changes up ───────
        private static readonly Dictionary<string, BusinessInfo> _lastSentClient = new();
        private static float _lastClientPollAt;

        /// <summary>CLIENT: change-driven sync of the businesses THIS player runs (rented
        /// buildings) up to the host, so the host + other players see them.  Mirrors the
        /// host detector: polls every PollIntervalSeconds, sends a building ONLY when its
        /// info actually changed — no idle "nothing changed" traffic.  The host applies it
        /// to its world and its own Tick relays it to the other clients.</summary>
        public static void TickClient()
        {
            if (!MPClient.IsConnected) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _lastClientPollAt < PollIntervalSeconds) return;
            _lastClientPollAt = now;

            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return;

                int changes = 0;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    bool mine = false;
                    try { mine = reg.RentedByPlayer; } catch { }
                    if (!mine) continue;                       // only buildings this client runs a business in

                    var info = ReadInfo(reg);
                    if (info == null) continue;
                    info.OwnerPlayerId         = MPConfig.PlayerId;   // it's ours — the host attributes it to us
                    info.BusinessOwnerPlayerId = MPConfig.PlayerId;

                    if (_lastSentClient.TryGetValue(info.AddressKey, out var prev) && EqualInfo(prev, info))
                        continue;                              // unchanged — don't send

                    _lastSentClient[info.AddressKey] = info;
                    MPClient.SendBusinessChange(info);
                    changes++;
                    Plugin.Logger.LogInfo($"[BusinessSync/Client] → host {info.AddressKey}: name='{info.BusinessName}' type={info.BusinessTypeName} sign=type{info.SignType}/light0x{info.SignLightPacked:X8}/lamp0x{info.LampPacked:X8}");
                }
                if (changes > 0) Plugin.Logger.LogInfo($"[BusinessSync/Client] Pushed {changes} owned-business change(s) to host.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BusinessSync] TickClient: {ex.Message}"); }
        }

        /// <summary>Reset state on host shutdown / new game start.</summary>
        public static void Reset()
        {
            _lastSent.Clear();
            _lastSentClient.Clear();
            OwnerOpenByAddress.Clear();
            _lastClientPollAt = 0f;
            _logoCache.Clear();
            _scanInProgress = false;
            _scanCursor     = 0;
            _cycleStartedAt = 0f;
            _lastMarketEventsHash = 0;   // re-emit market events to a fresh session / re-host (don't suppress via a stale hash)
            _nextMarketEventsAt   = 0f;
        }

        // ── Field reading ─────────────────────────────────────────────────────

        private static BusinessInfo? ReadInfo(BuildingRegistration reg)
        {
            try
            {
                string addr = GameStateReader.AddressKey(reg);
                if (string.IsNullOrEmpty(addr)) return null;

                var info = new BusinessInfo
                {
                    AddressKey         = addr,
                    BusinessName       = SafeStr(reg.BusinessName),
                    BusinessTypeName   = reg.businessTypeName ?? "",
                    TemporarilyClosed  = reg.temporarilyClosed,
                    AvailableForRent   = reg.AvailableForRent,
                    RentPerDay         = reg.RentPerDay,
                    LastDeposit        = reg.lastDeposit,
                    BusinessDescription = SafeStr(reg.BusinessDescription),
                    BuildingOwnerRivalId = SafeStr(reg.buildingOwnerRivalId),
                    BusinessOwnerRivalId = SafeStr(reg.businessOwnerRivalId),
                    RentedByPlayer       = reg.RentedByPlayer,
                    // Two separate concepts:
                    //   * Building owner (landlord) — host bought the property.
                    //     reg.BuildingOwnedByPlayer is the canonical check;
                    //     internally it looks up the registration in gi.realEstate.
                    //   * Business runner (tenant) — host operates a business
                    //     in the building.  reg.RentedByPlayer.
                    // A player can be both at once (buy + run own business),
                    // either, or neither.
                    OwnerPlayerId         = ResolveOwnerPlayerId(addr, reg),
                    BusinessOwnerPlayerId = reg.RentedByPlayer           ? MPConfig.PlayerId : "",
                };

                // AI-business retail prices (host-authoritative; audit catch
                // 2026-06-12: clients suppress the rival sim so their AI shops
                // had EMPTY tables → default prices instead of the host's
                // competition-adjusted ones).  Player shops stay with the live
                // MPPriceSync channel — never carried here.
                try
                {
                    if (!reg.RentedByPlayer && !GameStatePatcher.IsAnyPlayerBusiness(reg)
                        && reg.retailPrices != null && reg.retailPrices.Count > 0)
                    {
                        for (int i = 0; i < reg.retailPrices.Count; i++)
                        {
                            var rp = reg.retailPrices[i];
                            if (rp == null) continue;
                            info.Prices.Add(new RetailPriceInfo { ItemName = rp.itemName ?? "", Price = rp.price });
                        }
                    }
                }
                catch { }

                var sign = reg.signAppearanceSettings;
                if (sign != null)
                {
                    info.SignType        = (int)sign.signType;
                    info.SignLightPacked = sign.signLight.color;
                    info.LampPacked      = sign.lamp.color;
                }

                var logo = reg.logoSettings;
                if (logo != null)
                {
                    info.LogoShape             = SafeStr(logo.logoShape);
                    info.LogoFont              = (int)logo.font;
                    info.LogoColorPacked       = logo.logoColor.color;
                    info.FontColorPacked       = logo.fontColor.color;
                    info.BackgroundColorPacked = logo.backgroundColor.color;
                }

                // Operating hours (Phase 1c).  Without these the client sees
                // every business as closed because CityGenerator suppression
                // also skips default schedule population.
                try
                {
                    if (reg.scheduleDays != null)
                    {
                        for (int i = 0; i < reg.scheduleDays.Count; i++)
                        {
                            var sd = reg.scheduleDays[i];
                            if (sd == null) continue;
                            var dto = new ScheduleDayInfo
                            {
                                Day    = (int)sd.day,
                                IsOpen = sd.isOpen,
                            };
                            if (sd.openingHourSlots != null)
                            {
                                for (int j = 0; j < sd.openingHourSlots.Count; j++)
                                {
                                    var s = sd.openingHourSlots[j];
                                    if (s == null) continue;
                                    dto.OpeningHourSlots.Add(new OpeningHourSlotInfo
                                    {
                                        StartingHour = s.startingHour,
                                        EndingHour   = s.endingHour,
                                    });
                                }
                            }
                            if (sd.workShifts != null)
                            {
                                for (int j = 0; j < sd.workShifts.Count; j++)
                                {
                                    var w = sd.workShifts[j];
                                    if (w == null || IsSyntheticDutyEmployee(w.employeeId)) continue;
                                    dto.WorkShifts.Add(new WorkShiftInfo
                                    {
                                        EmployeeId     = w.employeeId ?? "",
                                        ItemInstanceId = w.itemInstanceId ?? "",
                                        StartingHour   = w.startingHour,
                                        EndingHour     = w.endingHour,
                                        Type           = (int)w.type,
                                    });
                                }
                            }
                            info.Schedule.Add(dto);
                        }
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[BusinessSync] schedule read for {addr}: {ex.Message}"); }

                // For player-customized businesses, the on-screen sign uses
                // a directory full of per-size JPG files (Billboard.jpg /
                // SquareSign.jpg / WideSign.jpg) generated by the host's
                // BizMan UI.  Client doesn't have them.  Enumerate the
                // directory, attach every file's bytes — client writes them
                // to its own equivalent directory.  AI businesses have no
                // such directory (they use Addressables), so we just get
                // an empty list.
                // Owner-authoritative open/closed truth. The machine that RUNS this business has the real
                // schedule + the live clock, so it computes the canonical IsBusinessOpen; everyone else (the
                // host's relay included) passes through the last value received, so a client's truth travels
                // owner→host→other-clients verbatim and nobody re-derives it (2026-06-19 shop-closed bug).
                try
                {
                    if (reg.RentedByPlayer)
                        info.OwnerOpenState = BusinessHelper.IsBusinessOpen(reg) ? 1 : 2;
                    else if (OwnerOpenByAddress.TryGetValue(addr, out var ownerSt))
                        info.OwnerOpenState = ownerSt;
                }
                catch { }

                if (!string.IsNullOrEmpty(info.BusinessName))
                    info.LogoFiles = ReadLogoFilesCached(info.BusinessName, info.LogoShape);
                return info;
            }
            catch
            {
                return null;
            }
        }

        // IL2CPP-Interop may expose BuildingRegistration.BusinessName as either
        // a C# string or an Il2CppSystem.String depending on its mapping rules.
        // Accept either and force a clean .NET string at the boundary.
        private static string SafeStr(object? s)
        {
            if (s == null) return "";
            try { return s.ToString() ?? ""; }
            catch { return ""; }
        }

        /// <summary>
        /// Wraps the BuildingOwnedByPlayer getter so an exception inside the
        /// IL2CPP-Interop lookup doesn't kill snapshot building.  Defaults to
        /// false on failure (safer to mis-attribute "not owned" than to
        /// falsely claim ownership across the wire).
        /// </summary>
        private static bool SafeBuildingOwnedByPlayer(BuildingRegistration reg)
        {
            try { return reg.BuildingOwnedByPlayer; }
            catch { return false; }
        }

        /// <summary>Who owns this building, as a network playerId, for the BusinessInfo
        /// receivers translate: if a CLIENT rented it (tracked in BuildingOwners), send
        /// that player's id so the renter sees RentedByPlayer=true and others see it as
        /// that player's; otherwise fall back to the host's own bought-property check.</summary>
        private static string ResolveOwnerPlayerId(string addr, BuildingRegistration reg)
        {
            try
            {
                if (MPServer.BuildingOwners.TryGetValue(addr, out var o) && !string.IsNullOrEmpty(o) && o != "host")
                    return o;   // a client rented this building
            }
            catch { }
            return SafeBuildingOwnedByPlayer(reg) ? MPConfig.PlayerId : "";
        }

        private static bool PricesEqual(System.Collections.Generic.List<RetailPriceInfo> a, System.Collections.Generic.List<RetailPriceInfo> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].ItemName != b[i].ItemName || a[i].Price != b[i].Price) return false;
            return true;
        }

        private static bool EqualInfo(BusinessInfo a, BusinessInfo b)
        {
            return a.BusinessName             == b.BusinessName
                && PricesEqual(a.Prices, b.Prices)
                && a.BusinessTypeName         == b.BusinessTypeName
                && a.TemporarilyClosed        == b.TemporarilyClosed
                && a.AvailableForRent         == b.AvailableForRent
                && a.RentPerDay               == b.RentPerDay
                && a.LastDeposit              == b.LastDeposit
                && a.BusinessDescription      == b.BusinessDescription
                && a.SignType                 == b.SignType
                && a.SignLightPacked          == b.SignLightPacked
                && a.LampPacked               == b.LampPacked
                && a.LogoShape                == b.LogoShape
                && a.LogoFont                 == b.LogoFont
                && a.LogoColorPacked          == b.LogoColorPacked
                && a.FontColorPacked          == b.FontColorPacked
                && a.BackgroundColorPacked    == b.BackgroundColorPacked
                && LogoFilesEqual(a.LogoFiles, b.LogoFiles)
                && ScheduleEqual(a.Schedule, b.Schedule)
                && a.BuildingOwnerRivalId     == b.BuildingOwnerRivalId
                && a.BusinessOwnerRivalId     == b.BusinessOwnerRivalId
                && a.RentedByPlayer           == b.RentedByPlayer
                && a.OwnerOpenState           == b.OwnerOpenState;
        }

        private static bool ScheduleEqual(System.Collections.Generic.List<ScheduleDayInfo> a, System.Collections.Generic.List<ScheduleDayInfo> b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Day    != b[i].Day)    return false;
                if (a[i].IsOpen != b[i].IsOpen) return false;
                var sa = a[i].OpeningHourSlots;
                var sb = b[i].OpeningHourSlots;
                if (sa.Count != sb.Count) return false;
                for (int j = 0; j < sa.Count; j++)
                {
                    if (sa[j].StartingHour != sb[j].StartingHour) return false;
                    if (sa[j].EndingHour   != sb[j].EndingHour)   return false;
                }
                var wa = a[i].WorkShifts;
                var wb = b[i].WorkShifts;
                if (wa.Count != wb.Count) return false;
                for (int j = 0; j < wa.Count; j++)
                {
                    if (wa[j].EmployeeId     != wb[j].EmployeeId)     return false;
                    if (wa[j].ItemInstanceId != wb[j].ItemInstanceId) return false;
                    if (wa[j].StartingHour   != wb[j].StartingHour)   return false;
                    if (wa[j].EndingHour     != wb[j].EndingHour)     return false;
                    if (wa[j].Type           != wb[j].Type)           return false;
                }
            }
            return true;
        }

        private static bool IsSyntheticDutyEmployee(string? employeeId)
            => !string.IsNullOrEmpty(employeeId)
            && employeeId.StartsWith(MPRegisterSync.SyntheticDutyEmployeeIdPrefix, StringComparison.Ordinal);

        // ── Logo-file payload cache ───────────────────────────────────────────
        // ReadInfo runs for every business each poll tick; re-scanning the logo
        // directory (and re-reading ~50KB of JPGs) every 2s was pure disk churn
        // (43k "No logo dir" log lines in one session).  Cache per business and
        // rescan at most every LogoRescanSeconds — sign/logo edits are rare, so
        // a ≤12s propagation delay is invisible while the I/O drops to ~zero.
        // LogoFilesEqual is content-based, so a same-content rescan never
        // triggers a rebroadcast.
        private const float LogoRescanSeconds        = 10f;    // existing dir: cheap metadata re-check cadence
        private const float LogoMissingRescanSeconds = 120f;   // no dir (AI/rival/uncustomized): back off hard
        private sealed class LogoCacheEntry
        {
            public float NextScanAt;
            public System.Collections.Generic.List<LogoFile> Files = new();
            public bool DirMissingLogged;   // "No logo dir" logged once per business
            public long MetaSig = -1;       // file name/size/mtime signature — content re-read only on change
        }
        private static readonly System.Collections.Generic.Dictionary<string, LogoCacheEntry> _logoCache = new();

        // Reading + Base64-encoding every named business's logo files every 10s was a
        // main-thread BusinessSync spike (~70ms, BizHost) AND the bulk of the per-frame
        // save-folder I/O (Procmon-confirmed 2026-06-14): hundreds of AI/rival businesses
        // have NO logo dir yet were stat'd every 10s, and player businesses had their logo
        // CONTENT re-read+encoded every 10s even when unchanged.  Now: no dir -> back off
        // to 120s; a real dir -> a cheap metadata signature (names/sizes/mtimes) every 10s,
        // and the expensive content read happens ONLY when that signature changes (i.e. the
        // player actually edited the logo).  LogoFilesEqual stays content-based, and an
        // unchanged scan returns the SAME cached list, so it never triggers a rebroadcast.
        private static System.Collections.Generic.List<LogoFile> ReadLogoFilesCached(string businessName, string logoShape)
        {
            if (!_logoCache.TryGetValue(businessName, out var e))
            { e = new LogoCacheEntry(); _logoCache[businessName] = e; }

            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < e.NextScanAt) return e.Files;
            e.NextScanAt = now + LogoMissingRescanSeconds;   // default: back off; a real dir drops to 10s below

            try
            {
                string dir = LogoHelper.GetPlayerBusinessLogoPath(businessName);
                if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
                {
                    if (!string.IsNullOrEmpty(logoShape) && !e.DirMissingLogged)
                    {
                        e.DirMissingLogged = true;
                        Plugin.Logger.LogInfo($"[BusinessSync] No logo dir for '{businessName}' — rescan backed off to {LogoMissingRescanSeconds:F0}s.");
                    }
                    return e.Files;   // no content read; stays as-is (empty for AI businesses)
                }

                e.NextScanAt = now + LogoRescanSeconds;   // real dir — re-check at 10s, but CHEAPLY

                // Cheap change signature: file name + size + mtime, NO content read.
                var paths = System.IO.Directory.GetFiles(dir);
                System.Array.Sort(paths, System.StringComparer.Ordinal);
                long sig = 17;
                foreach (var f in paths)
                {
                    try
                    {
                        var fi = new System.IO.FileInfo(f);
                        sig = sig * 31 + System.IO.Path.GetFileName(f).GetHashCode();
                        sig = sig * 31 + fi.Length;
                        sig = sig * 31 + fi.LastWriteTimeUtc.Ticks;
                    }
                    catch { }
                }
                if (sig == e.MetaSig) return e.Files;   // unchanged — skip the expensive Base64 content read
                e.MetaSig = sig;

                // Changed (or first read) — now do the expensive read + encode.
                var files = new System.Collections.Generic.List<LogoFile>();
                int total = 0;
                foreach (var f in paths)
                {
                    try
                    {
                        var bytes = System.IO.File.ReadAllBytes(f);
                        files.Add(new LogoFile { Name = System.IO.Path.GetFileName(f), Base64 = Convert.ToBase64String(bytes) });
                        total += bytes.Length;
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[BusinessSync] read {f}: {ex.Message}"); }
                }
                e.Files = files;
                if (files.Count > 0)
                    Plugin.Logger.LogInfo($"[BusinessSync] Logo files for '{businessName}' changed: {files.Count} file(s), {total}B (re-read).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BusinessSync] Logo dir read for {businessName}: {ex.Message}"); }
            return e.Files;
        }

        // List comparison — order- and content-sensitive; both produced by
        // the same Directory.GetFiles call so order is stable.
        private static bool LogoFilesEqual(System.Collections.Generic.List<LogoFile> a, System.Collections.Generic.List<LogoFile> b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Name != b[i].Name) return false;
                if (a[i].Base64 != b[i].Base64) return false;
            }
            return true;
        }
    }
}
