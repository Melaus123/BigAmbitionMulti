using System.Text.Json;
using Buildings;
using Helpers;
using Entities;
using Enums;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Applies incoming network state to the local GameInstance.
    /// All writes happen on the main Unity thread via the dispatcher queue.
    /// </summary>
    public static class GameStatePatcher
    {
        // ── Thread-safe action queue (network threads enqueue, Unity Update dequeues) ──

        private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> _mainThreadQueue = new();

        /// <summary>Called from the Unity Update loop by MainThreadDispatcher.</summary>
        public static void DrainQueue()
        {
            while (_mainThreadQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Plugin.Logger.LogError($"[Patcher] Dispatch error: {ex.Message}"); }
            }
        }

        private static void RunOnMainThread(Action action) => _mainThreadQueue.Enqueue(action);

        // CLAUDE-DIAGNOSTIC — kill-switch flags for the entry-bug investigation.
        // Each apply path gates on its respective flag; F4 master toggle in
        // MPCanvasUI flips them all at once so we can test which state-mutating
        // sync (if any) is preventing the client's building-entry chain.
        public static bool ClientApplyOwnership { get; set; } = true;
        public static bool ClientApplyTime      { get; set; } = true;
        public static bool ClientApplyMarket    { get; set; } = true;

        /// <summary>
        /// Public entry point so MPServer and MPClient can dispatch game-start actions
        /// from their background network threads onto the Unity main thread.
        /// </summary>
        public static void EnqueueOnMainThread(Action action) => _mainThreadQueue.Enqueue(action);

        // ── Snapshot application ──────────────────────────────────────────────

        public static void ApplyWorldSnapshot(WorldSnapshotPayload snap)
        {
            if (!ClientApplyOwnership && !ClientApplyMarket) return;   // CLAUDE-DIAGNOSTIC F4 gate
            RunOnMainThread(() =>
            {
                Plugin.Logger.LogInfo("[Patcher] Applying world snapshot...");

                // Mark any buildings owned by other players as unavailable locally
                if (ClientApplyOwnership)
                {
                    foreach (var kv in snap.BuildingOwners)
                    {
                        if (kv.Value != "" && kv.Value != MPConfig.PlayerId)
                            MarkBuildingUnavailable(kv.Key);
                    }
                }

                // Apply market entries
                if (ClientApplyMarket && !string.IsNullOrEmpty(snap.MarketEntriesJson))
                    ApplyMarketSnapshot(snap.MarketEntriesJson);

                Plugin.Logger.LogInfo("[Patcher] World snapshot applied.");
            });
        }

        public static void ApplyBuildingOwnership(BuildingOwnershipPayload payload)
        {
            if (!ClientApplyOwnership) return;   // CLAUDE-DIAGNOSTIC F4 gate
            RunOnMainThread(() =>
            {
                if (payload.OwnerPlayerId == MPConfig.PlayerId)
                {
                    // This is confirmation of OUR rent — execute the actual game rent logic
                    ExecuteLocalRent(payload);
                }
                else
                {
                    // Another player rented it — mark unavailable locally
                    MarkBuildingUnavailable(payload.AddressKey);
                }
            });
        }

        public static void ApplyBuildingVacated(string addressKey)
        {
            if (!ClientApplyOwnership) return;   // CLAUDE-DIAGNOSTIC F4 gate
            RunOnMainThread(() =>
            {
                MarkBuildingAvailable(addressKey);
                Plugin.Logger.LogInfo($"[Patcher] Building {addressKey} is now available.");
            });
        }

        public static void ApplyGameTime(GameTimeSyncPayload payload)
        {
            if (!ClientApplyTime) return;        // CLAUDE-DIAGNOSTIC F4 gate
            RunOnMainThread(() =>
            {
                try
                {
                    // Apply speed/pause change first (exact, no lerp)
                    if (payload.Speed >= 0f)
                        TimeSync.ApplyNetwork(payload.Speed);

                    // Apply clock alignment (gradual for small drift, snap for large)
                    if (payload.Day > 0 || payload.TimeOfDay > 0f)
                        TimeSync.ReceiveClockSync(payload.Day, payload.TimeOfDay);
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[Patcher] ApplyGameTime: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Apply only a speed/pause change — no clock data.
        /// Used when relaying an immediate speed-change event.
        /// </summary>
        public static void ApplyTimeSpeed(float speed)
        {
            RunOnMainThread(() =>
            {
                try { TimeSync.ApplyNetwork(speed); }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[Patcher] ApplyTimeSpeed: {ex.Message}");
                }
            });
        }

        // ── Business sync (Phase 1) ───────────────────────────────────────────

        public static void ApplyBusinessSnapshot(BusinessSnapshotPayload payload)
        {
            if (!ClientApplyOwnership) return;   // gated under existing kill-switch flag
            RunOnMainThread(() =>
            {
                // ── BEFORE-apply per-type count (Phase 1b investigation) ────
                LogClientTypeBreakdown("ApplyBusinessSnapshot.BEFORE");

                // ── Verification: confirm baseline is clean ─────────────────
                // With CityGenerator suppression in place, every BuildingRegistration
                // on client should be in default state (no business, no rivals,
                // not rented).  Walk the list and count any that aren't — these
                // are the "extras" that survived our wipe in earlier iterations.
                // Log them so we know the suppression is taking effect.
                VerifyCleanBaseline(payload);

                int applied = 0;
                foreach (var info in payload.Businesses)
                {
                    if (ApplyBusinessInfoLocal(info)) applied++;
                }
                Plugin.Logger.LogInfo($"[Patcher] Business snapshot applied: {applied}/{payload.Businesses.Count} buildings.");

                // ── Buy marketplace (gi.buildingsForSale) ───────────────────
                // Wipe the client's local list and rebuild from host's payload.
                // With Patch_RealEstateHelper_RunDaily_SkipOnClient active, the
                // client's list shouldn't be growing on its own — but wiping
                // first guarantees a clean mirror.
                ApplyBuildingsForSale(payload.BuildingsForSale);

                // ── AFTER-apply per-type count (Phase 1b investigation) ─────
                LogClientTypeBreakdown("ApplyBusinessSnapshot.AFTER");

                // ── POST-apply verification: client must now match host ─────
                VerifyMatchesPayload(payload);

                // ── Refresh map filter overlay ──────────────────────────────
                // Mirrors what the game does natively after rent / sale / daily
                // real-estate ticks.  Without this the data is correct but the
                // colored map highlights stay frozen until the user toggles a
                // filter manually.
                RefreshMapFilters();
            });
        }

        /// <summary>
        /// Finds the active CityMapFilters MonoBehaviour and triggers a full
        /// filter refresh — the same call the game makes natively after any
        /// state change that could affect map highlights (rent, sell, daily
        /// real-estate update).  We suppress those events on client, so we
        /// invoke this manually after our snapshot apply.  No-op if the
        /// component isn't in the scene yet.
        /// </summary>
        private static void RefreshMapFilters()
        {
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<CityMapFilters>());
                if (all == null || all.Length == 0) return;
                for (int i = 0; i < all.Length; i++)
                {
                    var f = all[i].TryCast<CityMapFilters>();
                    if (f == null) continue;
                    try { f.ApplyFilters(); }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] CityMapFilters.ApplyFilters: {ex.Message}"); }
                }
                Plugin.Logger.LogInfo($"[Patcher] Map filters refreshed ({all.Length} CityMapFilters instance(s)).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] RefreshMapFilters: {ex.Message}"); }
        }

        /// <summary>
        /// Replaces gi.buildingsForSale with the host's authoritative list.
        /// Each entry resolves AddressKey → local BuildingRegistration; we
        /// instantiate a new BuildingForSale per entry with the host's
        /// price/sqm/acceptOfferRate fields.
        /// </summary>
        private static void ApplyBuildingsForSale(System.Collections.Generic.List<BuildingForSaleInfo> infos)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return;
                var lst = gi.buildingsForSale;
                if (lst == null) { Plugin.Logger.LogWarning("[Patcher] gi.buildingsForSale is null."); return; }

                int wipedCount = lst.Count;
                lst.Clear();

                int added = 0;
                int skipped = 0;
                foreach (var info in infos)
                {
                    if (string.IsNullOrEmpty(info.AddressKey)) { skipped++; continue; }
                    var reg = FindRegistration(info.AddressKey);
                    if (reg == null) { skipped++; continue; }
                    try
                    {
                        var bfs = new BuildingForSale
                        {
                            address          = reg.Address,
                            buildingPrice    = info.BuildingPrice,
                            squareMeters     = info.SquareMeters,
                            acceptOfferRate  = info.AcceptOfferRate,
                        };
                        lst.Add(bfs);
                        added++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger.LogWarning($"[Patcher] ApplyBuildingsForSale entry {info.AddressKey}: {ex.Message}");
                        skipped++;
                    }
                }
                Plugin.Logger.LogInfo($"[Patcher] BuildingsForSale: wiped={wipedCount} added={added} skipped={skipped} (host sent {infos.Count}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] ApplyBuildingsForSale: {ex.Message}"); }
        }

        /// <summary>
        /// Walks gi.BuildingRegistrations and counts registrations whose state
        /// is NOT in the default "freshly-loaded, nothing assigned" baseline.
        /// With CityGenerator suppression active, this count should be near-zero.
        /// Non-zero means some generator path snuck through and the wipe is
        /// incomplete — those buildings will end up as the "extras" the user sees.
        /// </summary>
        private static void VerifyCleanBaseline(BusinessSnapshotPayload payload)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return;

                int total          = 0;
                int withBiz        = 0;   // BusinessTypeName != Empty
                int withRivalOwn   = 0;   // buildingOwnerRivalId set
                int withRivalBiz   = 0;   // businessOwnerRivalId set
                int playerRented   = 0;
                int availForRent   = 0;
                var dirtyExamples  = new System.Collections.Generic.List<string>();

                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    total++;
                    bool dirty = false;
                    try { if ((int)reg.businessTypeName != 0)                    { withBiz++;      dirty = true; } } catch { }
                    try { if (!string.IsNullOrEmpty(reg.buildingOwnerRivalId))   { withRivalOwn++; dirty = true; } } catch { }
                    try { if (!string.IsNullOrEmpty(reg.businessOwnerRivalId))   { withRivalBiz++; dirty = true; } } catch { }
                    try { if (reg.RentedByPlayer)                                { playerRented++; dirty = true; } } catch { }
                    try { if (reg.AvailableForRent)                              { availForRent++;               } } catch { }
                    if (dirty && dirtyExamples.Count < 5)
                    {
                        try { dirtyExamples.Add(GameStateReader.AddressKey(reg)); } catch { }
                    }
                }

                int dirtyTotal = withBiz + withRivalOwn + withRivalBiz + playerRented;
                string sample = dirtyExamples.Count > 0 ? $" examples=[{string.Join(", ", dirtyExamples)}]" : "";
                Plugin.Logger.LogInfo($"[Patcher] Baseline check (BEFORE apply): total={total} dirty={dirtyTotal} " +
                    $"(withBiz={withBiz} rivalOwn={withRivalOwn} rivalBiz={withRivalBiz} playerRented={playerRented} avail={availForRent}){sample}");

                if (dirtyTotal > 0)
                    Plugin.Logger.LogWarning($"[Patcher] Baseline NOT clean — {dirtyTotal} reg(s) had state before host's snapshot wrote them.  CityGenerator suppression may be incomplete.");
                else
                    Plugin.Logger.LogInfo("[Patcher] Baseline is 100% clean ✓");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] VerifyCleanBaseline: {ex.Message}"); }
        }

        /// <summary>
        /// After applying the snapshot, walk every registration and confirm
        /// its BusinessTypeName matches what the host sent for the same address.
        /// Logs any mismatch — should be empty if the apply was complete.
        /// </summary>
        private static void VerifyMatchesPayload(BusinessSnapshotPayload payload)
        {
            try
            {
                var hostByAddr = new System.Collections.Generic.Dictionary<string, BusinessInfo>(payload.Businesses.Count);
                foreach (var info in payload.Businesses)
                    if (!string.IsNullOrEmpty(info.AddressKey)) hostByAddr[info.AddressKey] = info;

                var gi = SaveGameManager.Current;
                if (gi == null) return;

                int matched = 0, mismatchBiz = 0, missingOnClient = 0;
                var mismatchExamples = new System.Collections.Generic.List<string>();
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    string addr = GameStateReader.AddressKey(reg);
                    if (string.IsNullOrEmpty(addr)) continue;
                    if (!hostByAddr.TryGetValue(addr, out var info)) { missingOnClient++; continue; }
                    int clientBiz; try { clientBiz = (int)reg.businessTypeName; } catch { clientBiz = -1; }
                    if (clientBiz == info.BusinessTypeName) matched++;
                    else
                    {
                        mismatchBiz++;
                        if (mismatchExamples.Count < 5)
                            mismatchExamples.Add($"{addr} host={info.BusinessTypeName} client={clientBiz}");
                    }
                }

                string sample = mismatchExamples.Count > 0 ? $" examples=[{string.Join(" | ", mismatchExamples)}]" : "";
                Plugin.Logger.LogInfo($"[Patcher] Match check (AFTER apply): matched={matched} mismatchBiz={mismatchBiz} addrNotInPayload={missingOnClient}{sample}");

                if (mismatchBiz == 0 && missingOnClient == 0)
                    Plugin.Logger.LogInfo("[Patcher] Client now mirrors host's snapshot 100% ✓");
                else
                    Plugin.Logger.LogWarning($"[Patcher] Post-apply state does NOT match host — {mismatchBiz} mismatch(es), {missingOnClient} not-in-payload.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] VerifyMatchesPayload: {ex.Message}"); }
        }

        /// <summary>
        /// Walks gi.BuildingRegistrations on the client and prints the full
        /// per-BuildingType TypeStats breakdown — Phase 1b diagnostic for the
        /// residential/warehouse for-rent discrepancy.
        /// </summary>
        private static void LogClientTypeBreakdown(string label)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) { Plugin.Logger.LogInfo($"[Patcher] {label}: (no GameInstance)"); return; }
                var stats = new BusinessSync.TypeStats();
                int totalRegs = 0;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    totalRegs++;
                    stats.AccumulateFromReg(reg);
                }
                stats.Log(label, totalRegs);
                BusinessSync.LogForSaleAndRealEstate(label, gi);
                BusinessSync.LogSceneCBCCounts(label);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] {label} count failed: {ex.Message}"); }
        }

        public static void ApplyBusinessChange(BusinessInfo info)
        {
            if (!ClientApplyOwnership) return;
            RunOnMainThread(() =>
            {
                if (ApplyBusinessInfoLocal(info))
                    Plugin.Logger.LogInfo($"[Patcher] Business change applied: {info.AddressKey} = '{info.BusinessName}'");
            });
        }

        private static bool ApplyBusinessInfoLocal(BusinessInfo info)
        {
            try
            {
                var reg = FindRegistration(info.AddressKey);
                if (reg == null) return false;

                // Tier A — name, type, closed.
                reg.BusinessName        = info.BusinessName;
                reg.businessTypeName    = (BusinessTypeName)info.BusinessTypeName;
                reg.temporarilyClosed   = info.TemporarilyClosed;

                // Rental marketplace state (Phase 1b).  Overrides client's
                // local AI-economy decisions.  If host considers this building
                // vacant + rentable but client's AI has filled it, these
                // writes flip the client's view back to "for rent."
                reg.AvailableForRent    = info.AvailableForRent;
                reg.RentPerDay          = info.RentPerDay;
                reg.lastDeposit         = info.LastDeposit;

                // Tier B — description + sign + logo.
                reg.BusinessDescription = info.BusinessDescription;

                if (reg.signAppearanceSettings != null)
                {
                    reg.signAppearanceSettings.signType         = (SignType)info.SignType;
                    reg.signAppearanceSettings.signLight        = new SerializableColor(info.SignLightPacked);
                    reg.signAppearanceSettings.lamp             = new SerializableColor(info.LampPacked);
                }
                if (reg.logoSettings != null)
                {
                    reg.logoSettings.logoShape       = info.LogoShape;
                    reg.logoSettings.font            = (FontFace)info.LogoFont;
                    reg.logoSettings.logoColor       = new SerializableColor(info.LogoColorPacked);
                    reg.logoSettings.fontColor       = new SerializableColor(info.FontColorPacked);
                    reg.logoSettings.backgroundColor = new SerializableColor(info.BackgroundColorPacked);
                }

                // Writes above only update the DATA model.  The visible sign /
                // logo / map marker are GameObjects that were initialised from
                // this registration when the building first loaded — they will
                // not auto-refresh.  Find the matching CityBuildingController
                // and call UpdateSign + UpdatePoi to repaint the visuals.
                //
                // Phase-1 v3 also walks the CBC's children for the two sign
                // controller MonoBehaviours and calls ConfigureSign / UpdateSign
                // directly — because cbc.UpdateSign() may be a no-op when LOD
                // state isn't 0, while a direct call forces a refresh now.
                var cbc = FindCBC(info.AddressKey);
                if (cbc == null)
                {
                    Plugin.Logger.LogWarning($"[Patcher] No CBC found for {info.AddressKey}");
                }
                else
                {
                    try { cbc.UpdateSign(); }    catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] UpdateSign {info.AddressKey}: {ex.Message}"); }
                    try { cbc.UpdatePoi(null); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] UpdatePoi {info.AddressKey}: {ex.Message}"); }

                    // Direct refresh on child sign controllers, in case the CBC's
                    // UpdateSign deferred due to LOD/cull state.
                    int refreshed = 0;
                    try
                    {
                        var signCtrls = cbc.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<BuildingSignController>(), true);
                        if (signCtrls != null)
                        {
                            for (int i = 0; i < signCtrls.Length; i++)
                            {
                                var sc = signCtrls[i].TryCast<BuildingSignController>();
                                if (sc == null) continue;
                                try { sc.ConfigureSign(reg); refreshed++; }
                                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] BuildingSignController.ConfigureSign: {ex.Message}"); }
                            }
                        }
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] BuildingSignController scan: {ex.Message}"); }

                    try
                    {
                        var logoCtrls = cbc.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<BuildingLogoSignController>(), true);
                        if (logoCtrls != null)
                        {
                            for (int i = 0; i < logoCtrls.Length; i++)
                            {
                                var lc = logoCtrls[i].TryCast<BuildingLogoSignController>();
                                if (lc == null) continue;
                                try { lc.UpdateSign(reg); refreshed++; }
                                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] BuildingLogoSignController.UpdateSign: {ex.Message}"); }
                            }
                        }
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] BuildingLogoSignController scan: {ex.Message}"); }

                    // Verbose log only for non-trivial businesses (matches the
                    // host-side filter so we get host/client side-by-side).
                    bool nonTrivial = !string.IsNullOrEmpty(info.BusinessName) || info.BusinessTypeName != 0;
                    if (nonTrivial)
                    {
                        Plugin.Logger.LogInfo($"[Patcher] Refresh {info.AddressKey}: name='{info.BusinessName}' type={(BusinessTypeName)info.BusinessTypeName} signType={info.SignType} sign=0x{info.SignLightPacked:X8}/0x{info.LampPacked:X8} logoShape='{info.LogoShape}' logoFont={info.LogoFont} refreshed={refreshed}");
                    }

                    // Player-customized sign logos are stored as multiple JPG
                    // files (Billboard / SquareSign / WideSign) inside a
                    // directory the host's BizMan UI created.  Client gets
                    // the directory contents from info.LogoFiles — recreate
                    // the directory and write every file so the next
                    // UpdateSign loads the same pixels the host sees.
                    bool wroteLogo = false;
                    if (nonTrivial && !string.IsNullOrEmpty(info.LogoShape) && (info.LogoFiles == null || info.LogoFiles.Count == 0))
                    {
                        Plugin.Logger.LogInfo($"[Patcher] No logo files received for {info.AddressKey} ('{info.BusinessName}') — host had no directory.");
                    }
                    if (info.LogoFiles != null && info.LogoFiles.Count > 0 && !string.IsNullOrEmpty(info.BusinessName))
                    {
                        try
                        {
                            string dir = LogoHelper.GetPlayerBusinessLogoPath(info.BusinessName);
                            if (!string.IsNullOrEmpty(dir))
                            {
                                if (!System.IO.Directory.Exists(dir))
                                    System.IO.Directory.CreateDirectory(dir);
                                int total = 0;
                                foreach (var f in info.LogoFiles)
                                {
                                    if (string.IsNullOrEmpty(f.Name) || string.IsNullOrEmpty(f.Base64)) continue;
                                    var bytes = Convert.FromBase64String(f.Base64);
                                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, f.Name), bytes);
                                    total += bytes.Length;
                                }
                                wroteLogo = total > 0;
                                Plugin.Logger.LogInfo($"[Patcher] Wrote logo files for {info.AddressKey}: {info.LogoFiles.Count} file(s) {total}B → {dir}");
                            }
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] Logo file write for {info.AddressKey}: {ex.Message}"); }
                    }

                    // After writing the files, queue a re-refresh in a fraction
                    // of a second so the sign controller loads them from disk.
                    // Also invalidate the texture cache — the first UpdateSign
                    // for this building cached a null/default when no files
                    // existed yet, so subsequent UpdateSign calls would just
                    // return the cached generic and ignore our newly-written
                    // files.  Sledgehammer Clear() forces all subsequent loads
                    // to re-read from disk; AI logos will simply re-load via
                    // Addressables (small async cost).
                    if (wroteLogo)
                    {
                        InvalidateLogoTextureCacheForBusiness(info.BusinessName);
                        _pendingLogoRefreshes.Add(new PendingLogoRefresh
                        {
                            AddressKey = info.AddressKey,
                            RefreshAt  = UnityEngine.Time.realtimeSinceStartup + 0.5f,
                        });
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Patcher] ApplyBusinessInfoLocal {info.AddressKey}: {ex.Message}");
                return false;
            }
        }

        // Cache CityBuildingController lookups by address.  FindObjectsOfType
        // is expensive; building positions don't change.  Cleared on game load.
        private static readonly System.Collections.Generic.Dictionary<string, CityBuildingController?> _cbcCache = new();
        public static void ResetCBCCache() => _cbcCache.Clear();

        // Queue of addresses whose logo we asked BusinessLogoGenerator.Create
        // to generate.  Ticked from MPCanvasUI.LateUpdate via DrainPendingLogoRefreshes.
        // Each entry has a deadline timestamp; when reached, we re-call the sign
        // controllers to refresh from the now-generated on-disk PNG.
        private struct PendingLogoRefresh
        {
            public string AddressKey;
            public float  RefreshAt;
        }
        private static readonly System.Collections.Generic.List<PendingLogoRefresh> _pendingLogoRefreshes = new();

        // Clear LogoHelper.BusinessLogoTextures so subsequent UpdateSign calls
        // re-load logos from disk.  Necessary after writing new logo files for
        // a player business — without this the dictionary keeps the null/default
        // it cached when the directory was empty (or the previous version of
        // the texture from before the host re-customized).
        //
        // We also Destroy() the cached Texture2D objects so Unity can't keep
        // showing them through the sign's MaterialPropertyBlock reference.
        // Without this, even though the dict no longer holds the texture, the
        // sign's mesh still has it assigned and Unity will keep rendering it
        // until the controller explicitly SetSignTextures a new one.  Destroy
        // forces the new SetSignTexture to actually take effect.
        private static int _logoCacheInvalidationCount = 0;
        /// <summary>
        /// Invalidates LogoHelper.BusinessLogoTextures.
        ///
        /// We can't iterate the Il2CppSystem dictionary safely through
        /// IL2CPP-Interop (the previous round crashed the client mid-foreach,
        /// likely a marshalling/iterator issue with the ValueTuple key type).
        /// And we can't Destroy() the cached Texture2Ds (that caused cross-
        /// business contamination — other signs' MaterialPropertyBlocks still
        /// referenced those destroyed textures and rendered garbage).
        ///
        /// Safe middle ground: call dict.Clear() — removes all entries but
        /// doesn't destroy the texture objects.  Other signs' refs stay
        /// alive and unchanged.  Next UpdateSign for any business: cache
        /// miss → fresh load → SetSignTexture with the new texture → that
        /// sign repaints.  Old textures GC naturally once nothing refers
        /// to them.  Brief one-time perf hit (all signs re-load on next
        /// render), nothing functionally wrong.
        /// </summary>
        public static void InvalidateLogoTextureCacheForBusiness(string businessName)
        {
            if (string.IsNullOrEmpty(businessName)) return;
            try
            {
                var dict = LogoHelper.BusinessLogoTextures;
                if (dict == null) { Plugin.Logger.LogWarning("[Patcher] BusinessLogoTextures is null."); return; }
                int sizeBefore = dict.Count;
                dict.Clear();
                _logoCacheInvalidationCount++;
                if (_logoCacheInvalidationCount <= 3 || _logoCacheInvalidationCount % 50 == 0)
                {
                    Plugin.Logger.LogInfo($"[Patcher] LogoHelper cache cleared for '{businessName}' update (was {sizeBefore} entry(s); op #{_logoCacheInvalidationCount}).");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] InvalidateLogoTextureCacheForBusiness({businessName}): {ex.GetType().Name}: {ex.Message}"); }
        }

        public static void DrainPendingLogoRefreshes()
        {
            if (_pendingLogoRefreshes.Count == 0) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            for (int i = _pendingLogoRefreshes.Count - 1; i >= 0; i--)
            {
                var p = _pendingLogoRefreshes[i];
                if (now < p.RefreshAt) continue;
                _pendingLogoRefreshes.RemoveAt(i);
                try
                {
                    var reg = FindRegistration(p.AddressKey);
                    var cbc = FindCBC(p.AddressKey);
                    if (reg != null && cbc != null)
                    {
                        try { cbc.UpdateSign(); } catch { }
                        // Re-walk children and refresh directly too.
                        try
                        {
                            var logoCtrls = cbc.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<BuildingLogoSignController>(), true);
                            if (logoCtrls != null)
                                for (int j = 0; j < logoCtrls.Length; j++)
                                {
                                    var lc = logoCtrls[j].TryCast<BuildingLogoSignController>();
                                    if (lc != null) { try { lc.UpdateSign(reg); } catch { } }
                                }
                            var signCtrls = cbc.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<BuildingSignController>(), true);
                            if (signCtrls != null)
                                for (int j = 0; j < signCtrls.Length; j++)
                                {
                                    var sc = signCtrls[j].TryCast<BuildingSignController>();
                                    if (sc != null) { try { sc.ConfigureSign(reg); } catch { } }
                                }
                        }
                        catch { }
                        Plugin.Logger.LogInfo($"[Patcher] Deferred logo refresh fired for {p.AddressKey}.");
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] DrainPendingLogoRefreshes: {ex.Message}"); }
            }
        }

        private static CityBuildingController? FindCBC(string addressKey)
        {
            if (_cbcCache.TryGetValue(addressKey, out var cached)) return cached;

            try
            {
                var all = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<CityBuildingController>());
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        var cbc = all[i].TryCast<CityBuildingController>();
                        if (cbc == null) continue;
                        var reg = cbc.buildingRegistration;
                        if (reg == null) continue;
                        var k = GameStateReader.AddressKey(reg);
                        if (!string.IsNullOrEmpty(k)) _cbcCache[k] = cbc;
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] FindCBC scan: {ex.Message}"); }

            _cbcCache.TryGetValue(addressKey, out var found);
            return found;
        }

        public static void ApplyMarketSnapshot(string marketJson)
        {
            if (!ClientApplyMarket) return;      // CLAUDE-DIAGNOSTIC F4 gate
            RunOnMainThread(() =>
            {
                try
                {
                    var dtos = JsonSerializer.Deserialize<List<MarketEntryDto>>(marketJson);
                    if (dtos == null) return;

                    var gi = SaveGameManager.Current;
                    if (gi == null) return;

                    foreach (var dto in dtos)
                    {
                        // Find matching entry and update prices
                        foreach (var entry in gi.productMarketEntries)
                        {
                            if (entry.itemName.ToString() == dto.ItemName)
                            {
                                entry.importPriceIndex = dto.ImportPriceIndex;
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[Patcher] ApplyMarketSnapshot error: {ex.Message}");
                }
            });
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Marks the BuildingRegistration for this address as unavailable for rent
        /// in the local GameInstance — prevents the local player seeing it as rentable.
        /// </summary>
        private static void MarkBuildingUnavailable(string addressKey)
        {
            var reg = FindRegistration(addressKey);
            if (reg == null) return;
            reg.AvailableForRent = false;
            Plugin.Logger.LogInfo($"[Patcher] {addressKey} marked unavailable.");
        }

        private static void MarkBuildingAvailable(string addressKey)
        {
            var reg = FindRegistration(addressKey);
            if (reg == null) return;
            reg.AvailableForRent = true;
        }

        /// <summary>
        /// Executes the actual BuildingHelper.RentBuilding call for the local player
        /// after the host has confirmed approval.
        /// </summary>
        private static void ExecuteLocalRent(BuildingOwnershipPayload payload)
        {
            try
            {
                var building = FindBuilding(payload.AddressKey);
                if (building == null)
                {
                    Plugin.Logger.LogError($"[Patcher] Could not find building for {payload.AddressKey}");
                    return;
                }

                // Execute the actual game logic — this deducts money, sets RentedByPlayer, etc.
                // The Harmony patch will see MPPatches.SuppressNextRentRequest = true and skip
                // sending another network request for this call.
                MPPatches.SuppressNextRentRequest = true;
                BuildingHelper.RentBuilding(building, payload.DailyRent, payload.LastDeposit);
                Plugin.Logger.LogInfo($"[Patcher] Local rent executed for {payload.AddressKey}");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Patcher] ExecuteLocalRent error: {ex.Message}");
            }
        }

        private static BuildingRegistration? FindRegistration(string addressKey)
        {
            var gi = SaveGameManager.Current;
            if (gi == null) return null;

            foreach (var reg in gi.BuildingRegistrations)
            {
                if (GameStateReader.AddressKey(reg) == addressKey)
                    return reg;
            }
            return null;
        }

        private static Building? FindBuilding(string addressKey)
        {
            // BuildingHelper.allBuildings is a static list of all city buildings
            foreach (var b in BuildingHelper.allBuildings)
            {
                if (GameStateReader.AddressKey(b) == addressKey)
                    return b;
            }
            return null;
        }
    }
}
