using System;
using System.Collections.Generic;
using Buildings;

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
        private static float _lastPollAt;

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
                var all = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<CityBuildingController>());
                if (all == null) { Plugin.Logger.LogInfo($"[BusinessSync] {label} CBC scan: (null)"); return; }
                int total = all.Length;
                int[] byType = new int[7];
                int[] forRentByType = new int[7];   // CBCs whose reg has BusinessTypeName==Empty
                int noReg = 0;
                int unknownType = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    var cbc = all[i].TryCast<CityBuildingController>();
                    if (cbc == null) continue;
                    var reg = cbc.buildingRegistration;
                    if (reg == null) { noReg++; continue; }
                    int idx = TryGetBuildingTypeIndex(reg);
                    if (idx < 0 || idx >= 7) { unknownType++; continue; }
                    byType[idx]++;
                    try { if ((int)reg.businessTypeName == 0) forRentByType[idx]++; } catch { }
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
                                    int idx = (int)b.BuildingType;
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
                                    int idx = (int)b.BuildingType;
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

        /// <summary>
        /// Returns the BuildingType ordinal (0..6) for a registration via its
        /// cached Building reference, or -1 if unavailable.  IL2CPP-Interop:
        /// reg.BuildingCached lazy-resolves through BuildingHelper.
        /// </summary>
        public static int TryGetBuildingTypeIndex(BuildingRegistration reg)
        {
            try
            {
                var b = reg.BuildingCached;
                if (b == null) return -1;
                return (int)b.BuildingType;
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
                if (info.BusinessTypeName == 0)                           EmptyBizType[idx]++;
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
                try { if ((int)reg.businessTypeName == 0)                  EmptyBizType[idx]++;  } catch { }
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

        /// <summary>
        /// Called once per Update on the host.  Runs the change detector every
        /// PollIntervalSeconds.
        /// </summary>
        public static void Tick()
        {
            if (!MPServer.IsRunning) return;
            // During the startup hold the initial full table is delivered per-client by
            // SendWorldStateTo (which also seeds _lastSent + the for-sale hash).  Running
            // the change-detector / for-sale broadcast here too would send a SECOND full
            // table to everyone (the duplicate seen in the load profile).  Skip until the
            // world is live; steady-state deltas resume after release.
            if (TimeSync.IsStartupHeld) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _lastPollAt < PollIntervalSeconds) return;
            _lastPollAt = now;

            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return;

                int changes = 0;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    var info = ReadInfo(reg);
                    if (info == null) continue;

                    if (_lastSent.TryGetValue(info.AddressKey, out var prev) && EqualInfo(prev, info))
                        continue;   // unchanged

                    _lastSent[info.AddressKey] = info;
                    MPServer.BroadcastBusinessChange(info);
                    changes++;
                    // Log only non-trivial changes (something that has a name or
                    // isn't BusinessTypeName.Empty) so we don't spam at startup.
                    if (!string.IsNullOrEmpty(info.BusinessName) || info.BusinessTypeName != 0
                        || !string.IsNullOrEmpty(info.BuildingOwnerRivalId)
                        || !string.IsNullOrEmpty(info.BusinessOwnerRivalId)
                        || info.RentedByPlayer)
                    {
                        Plugin.Logger.LogInfo($"[BusinessSync] Sent change {info.AddressKey}: name='{info.BusinessName}' type={info.BusinessTypeName} owners[bldg='{info.BuildingOwnerRivalId}' biz='{info.BusinessOwnerRivalId}' rented={info.RentedByPlayer}] sign=0x{info.SignLightPacked:X8}");
                    }
                }
                if (changes > 0)
                    Plugin.Logger.LogInfo($"[BusinessSync] Broadcast {changes} business change(s).");

                // Buy marketplace (gi.buildingsForSale) — host's daily real-
                // estate update modifies this list once per game day.  Hash-
                // diff on each Tick; re-broadcast the full snapshot when it
                // changes.  Cheap: hash compute is O(N), N ~10-15 entries.
                CheckBuildingsForSaleChange();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BusinessSync] Tick: {ex.Message}"); }
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
            _lastClientPollAt = 0f;
            _lastPollAt = 0f;
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
                    BusinessTypeName   = (int)reg.businessTypeName,
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
                    info.SharedSchedule = reg.sharedSchedule;
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
                if (!string.IsNullOrEmpty(info.BusinessName))
                {
                    try
                    {
                        string dir = LogoHelper.GetPlayerBusinessLogoPath(info.BusinessName);
                        if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                        {
                            int total = 0;
                            foreach (var f in System.IO.Directory.GetFiles(dir))
                            {
                                try
                                {
                                    var bytes = System.IO.File.ReadAllBytes(f);
                                    info.LogoFiles.Add(new LogoFile
                                    {
                                        Name   = System.IO.Path.GetFileName(f),
                                        Base64 = Convert.ToBase64String(bytes),
                                    });
                                    total += bytes.Length;
                                }
                                catch (Exception ex) { Plugin.Logger.LogWarning($"[BusinessSync] read {f}: {ex.Message}"); }
                            }
                            if (info.LogoFiles.Count > 0)
                                Plugin.Logger.LogInfo($"[BusinessSync] Logo files for '{info.BusinessName}': {info.LogoFiles.Count} file(s), {total}B total");
                        }
                        else if (!string.IsNullOrEmpty(info.LogoShape))
                        {
                            Plugin.Logger.LogInfo($"[BusinessSync] No logo dir yet for '{info.BusinessName}' (dir='{dir}')");
                        }
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[BusinessSync] Logo dir read for {info.BusinessName}: {ex.Message}"); }
                }
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

        private static bool EqualInfo(BusinessInfo a, BusinessInfo b)
        {
            return a.BusinessName             == b.BusinessName
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
                && a.SharedSchedule           == b.SharedSchedule
                && ScheduleEqual(a.Schedule, b.Schedule)
                && a.BuildingOwnerRivalId     == b.BuildingOwnerRivalId
                && a.BusinessOwnerRivalId     == b.BusinessOwnerRivalId
                && a.RentedByPlayer           == b.RentedByPlayer;
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
            }
            return true;
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
