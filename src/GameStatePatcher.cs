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

        /// <summary>
        /// Public entry point so MPServer and MPClient can dispatch game-start actions
        /// from their background network threads onto the Unity main thread.
        /// </summary>
        public static void EnqueueOnMainThread(Action action) => _mainThreadQueue.Enqueue(action);

        // ── Snapshot application ──────────────────────────────────────────────

        public static void ApplyWorldSnapshot(WorldSnapshotPayload snap)
        {
            RunOnMainThread(() =>
            {
                Plugin.Logger.LogInfo("[Patcher] Applying world snapshot...");

                // Mark any buildings owned by other players as unavailable locally
                foreach (var kv in snap.BuildingOwners)
                {
                    if (kv.Value != "" && kv.Value != MPConfig.PlayerId)
                        MarkBuildingUnavailable(kv.Key);
                }

                // Same for BOUGHT real estate owned by other players.
                foreach (var kv in snap.BuildingRealEstateOwners)
                {
                    if (kv.Value != "" && kv.Value != MPConfig.PlayerId)
                        MarkBuildingUnavailable(kv.Key);
                }

                // Apply market entries
                if (!string.IsNullOrEmpty(snap.MarketEntriesJson))
                    ApplyMarketSnapshot(snap.MarketEntriesJson);

                Plugin.Logger.LogInfo("[Patcher] World snapshot applied.");
            });
        }

        public static void ApplyBuildingOwnership(BuildingOwnershipPayload payload)
        {
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
            RunOnMainThread(() =>
            {
                // Mirror the native terminate-lease so the building reads empty +
                // rentable on THIS machine too: clear any synced business identity so
                // it can't keep rendering as an occupied shop.  The !RentedByPlayer
                // guard makes this safe — it never wipes a building the local player
                // actively rents (only the owner vacates, and they already cleared it
                // natively before this notify arrives).
                var reg = FindRegistration(addressKey);
                if (reg != null)
                {
                    try
                    {
                        // Gate ALL the vacate writes on !RentedByPlayer (was: only the name wipe) — a
                        // stale/misrouted VacateNotify must not flip a building the LOCAL player actively
                        // rents to "available" or clear its owner mark.
                        if (!reg.RentedByPlayer)
                        {
                            reg.AvailableForRent      = true;
                            reg.buildingOwnerRivalId  = "";
                            reg.BusinessName          = null;
                            reg.businessTypeName      = "ba:businesstype_empty";
                        }
                    }
                    catch { }
                }
                else MarkBuildingAvailable(addressKey);
                Plugin.Logger.LogInfo($"[Patcher] Building {addressKey} is now available.");
            });
        }

        public static void ApplyGameTime(GameTimeSyncPayload payload)
        {
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

        // ── Rivals roster sync (Phase 1d Wave 2) ─────────────────────────────

        /// <summary>
        /// Plain-C# id→name dict managed by us.  IL2CPP-Interop wrappers don't
        /// expose private fields like RivalsHelper.RivalDataCache, so we can't
        /// populate the game's cache directly.  Instead, our Harmony patch on
        /// RivalsHelper.GetRivalName consults THIS dict first and short-
        /// circuits with the host's name — bypassing the game's cache lookup
        /// entirely for known IDs.
        /// </summary>
        // CONCURRENT: written on the poll thread (PlayerProfile handlers, MPServer/
        // MPClient) and bulk-rebuilt on the main thread (ApplyRivalsSnapshot).
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ClientRivalNames = new();

        /// <summary>
        /// Ordered FIFO of rival IDs to be consumed by client's GenerateRivals.
        /// Populated when host's RivalsSnapshot arrives.  Drained by the
        /// Harmony Prefix on UuidHelper.GenerateBase64Uuid (which only fires
        /// while RivalsGenerateRunning is true).
        /// </summary>
        public static readonly System.Collections.Generic.Queue<string> PendingRivalIdQueue = new();

        /// <summary>Set to true by Patch_RivalsHelper_GenerateRivals around the original method's execution.</summary>
        public static bool RivalsGenerateRunning = false;

        /// <summary>True only while RivalLeaderboard.Load is executing (set by its
        /// Prefix, cleared by its Postfix).  Gates the GetAllRivalData Postfix so
        /// we append remote-player RivalData ONLY for the leaderboard build — not
        /// for GetAllRivalData's other callers (CityMapFilters, GetPlayerRanking),
        /// which must not see synthetic players.</summary>
        public static bool RivalsLeaderboardLoadRunning = false;

        /// <summary>Becomes true once the client has received host's RivalsSnapshot — used by autopilot to gate StartGame.</summary>
        public static bool ClientRivalsReady = false;

        /// <summary>
        /// Set to true the first time our UUID-queue injection successfully
        /// runs through the original GenerateRivals.  Subsequent GenerateRivals
        /// calls are suppressed so the natural new-game flow can't wipe our
        /// injected cache.
        /// </summary>
        public static bool ClientRivalsInjected = false;

        /// <summary>
        /// PlayerId → CharacterName for entries that are HUMAN PLAYERS (not AI
        /// rivals).  Populated alongside ClientRivalNames when RivalsSnapshot
        /// arrives.  Used by Patch_RivalLeaderboard_Load_AddPlayers to inject
        /// additional rows into the rivals leaderboard for each non-local
        /// player — same approach the game uses to add the local player row.
        /// </summary>
        // CONCURRENT: written on the poll thread (host PlayerProfile handler),
        // bulk-rebuilt and read on the main thread (leaderboard injection).
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ClientPlayerRoster = new();

        /// <summary>
        /// id → RivalData reference, captured when the local game's
        /// GetRivalLeaderboardData runs (Postfix).  Host uses this to access
        /// rd.WeeklyIncome (the correct calc) when computing stats to send
        /// clients — avoids the inaccurate RentPerDay*7 approximation.
        /// Client uses this to access rivals' actual data for further sync.
        /// </summary>
        public static readonly System.Collections.Generic.Dictionary<string, BigAmbitions.Rivals.RivalData> AiRivalDataRefs = new();

        /// <summary>
        /// Per-rival stats cache (Phase 1d Wave 4).  Populated by
        /// ApplyRivalsStatsSnapshot when host responds to a stats request.
        /// Consulted by the RivalLeaderboard.GetRivalLeaderboardData Postfix
        /// to override the locally-computed (and therefore stale) values.
        /// </summary>
        // CONCURRENT: written on the poll thread (RivalsStatsRequest handler),
        // bulk-rebuilt and read on the main thread (leaderboard build).
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, RivalStatsInfo> ClientRivalStats = new();

        /// <summary>
        /// AddressKey → host-authoritative weekly income for a rival-owned
        /// business.  Populated from RivalStatsInfo.Businesses in
        /// ApplyRivalsStatsSnapshot.  Consulted by the RivalBusinessesCellView
        /// .SetData patch to override the client's $0 per-business income (AI
        /// business sales aren't simulated on the client).
        /// </summary>
        public static readonly System.Collections.Generic.Dictionary<string, float> ClientBusinessIncomeByAddress = new();

        /// <summary>PlayerId → their actual portrait Sprite (decoded from the
        /// relayed PNG).  Consulted by the RivalPortraitHelper patches so a
        /// remote player's profile shows their real face.</summary>
        public static readonly System.Collections.Generic.Dictionary<string, UnityEngine.Sprite> ClientPlayerPortraits = new();

        /// <summary>PlayerId → their character age in years (from PlayerProfile),
        /// so the rivals profile shows the real age, not a default.</summary>
        // CONCURRENT: written on the poll thread (PlayerProfile handlers).
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> ClientPlayerAges = new();
        /// <summary>playerId → gender (BigAmbitions.Characters.Gender as int) —
        /// generated-portrait fallback fidelity.</summary>
        // CONCURRENT: written on the poll thread (PlayerProfile handlers).
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> ClientPlayerGenders = new();

        /// <summary>True once we've sent our profile WITH a non-empty portrait —
        /// whether on the initial game-entry send or a later re-send.  Ensures
        /// the (large) portrait image is transmitted exactly once per session.
        /// Reset when leaving the game.</summary>
        public static bool LocalPortraitSent = false;

        /// <summary>Local player's character age in years = ageInDays / daysPerYear.</summary>
        public static int LocalAgeInYears()
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null || gi.charactersData == null || gi.charactersData.Count == 0) return 0;
                var cd = gi.charactersData[0];
                if (cd == null) return 0;
                int dpy = 60;
                try { if (gi.gameVariables != null && gi.gameVariables.daysPerYear > 0) dpy = gi.gameVariables.daysPerYear; } catch { }
                return dpy > 0 ? cd.ageInDays / dpy : 0;
            }
            catch { return 0; }
        }

        /// <summary>Read the LOCAL player's rendered portrait PNG as Base64 (for
        /// relaying to other players).  Empty if not on disk yet.</summary>
        public static string ReadLocalPortraitBase64()
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return "";
                string path = Character.Customization.PortraitGenerator.GetCharacterPortraitPath(gi);
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return "";
                var bytes = System.IO.File.ReadAllBytes(path);
                if (bytes == null || bytes.Length == 0) return "";
                return Convert.ToBase64String(bytes);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] ReadLocalPortraitBase64: {ex.Message}"); return ""; }
        }

        /// <summary>Decode a relayed portrait PNG into a Sprite and cache it for
        /// the given player.  MUST run on the main thread (creates a Texture2D).</summary>
        public static void ApplyPlayerPortrait(string playerId, string base64)
        {
            if (string.IsNullOrEmpty(playerId) || string.IsNullOrEmpty(base64)) return;
            try
            {
                var managed = Convert.FromBase64String(base64);
                if (managed == null || managed.Length == 0) return;
                var tex = new UnityEngine.Texture2D(2, 2);
                bool loaded = UnityEngine.ImageConversion.LoadImage(tex, managed);
                if (!loaded) { Plugin.Logger.LogWarning($"[Patcher] Portrait decode failed for '{playerId}'."); return; }
                var sprite = UnityEngine.Sprite.Create(
                    tex,
                    new UnityEngine.Rect(0, 0, tex.width, tex.height),
                    new UnityEngine.Vector2(0.5f, 0.5f));
                ClientPlayerPortraits[playerId] = sprite;
                Plugin.Logger.LogInfo($"[Patcher] Applied portrait for player '{playerId}' ({tex.width}x{tex.height}, {managed.Length}B).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] ApplyPlayerPortrait '{playerId}': {ex.Message}"); }
        }

        /// <summary>AddressKeys the host reports as a rival's businesses (the
        /// exact membership the host's leaderboard/breakdown use).  Lets the
        /// client reconcile its factory-inclusive owner-id matching down to the
        /// host's set.  Keyed by rivalId → set of address keys.</summary>
        public static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>> ClientRivalBusinessAddrs = new();

        /// <summary>
        /// Apply host's rival roster.  Wave 2 just maintains our own
        /// id→name dict (consulted by the Patch_RivalsHelper_GetRivalName
        /// patch) and adds matching RivalState entries to gi.rivalStates so
        /// leaderboard/history queries don't NRE.
        /// </summary>
        public static void ApplyRivalsSnapshot(RivalsSnapshotPayload payload)
        {
            if (payload == null) return;
            RunOnMainThread(() =>
            {
                try
                {
                    ClientRivalNames.Clear();
                    int added = 0;
                    foreach (var r in payload.Rivals)
                    {
                        if (string.IsNullOrEmpty(r.Id)) continue;
                        ClientRivalNames[r.Id] = r.Name ?? "";
                        added++;
                    }
                    Plugin.Logger.LogInfo($"[Patcher] ClientRivalNames populated: {added} rival(s).");

                    // Populate the UUID queue with AI rivals ONLY.  Player IDs are
                    // segregated into ClientPlayerRoster; they get their own
                    // leaderboard rows injected via Patch_RivalLeaderboard_Load_AddPlayers,
                    // matching how the local player is rendered (separate from
                    // RivalDataCache).  Putting player IDs in the queue would
                    // misalign them with AI templates and produce wrong names.
                    PendingRivalIdQueue.Clear();
                    ClientPlayerRoster.Clear();
                    foreach (var r in payload.Rivals)
                    {
                        if (string.IsNullOrEmpty(r.Id)) continue;
                        if (r.IsPlayer)
                            ClientPlayerRoster[r.Id] = r.Name ?? r.Id;
                        else
                            PendingRivalIdQueue.Enqueue(r.Id);
                    }
                    Plugin.Logger.LogInfo($"[Patcher] PendingRivalIdQueue seeded with {PendingRivalIdQueue.Count} AI id(s); ClientPlayerRoster has {ClientPlayerRoster.Count} player(s).");
                    ClientRivalsReady = true;

                    EnsureRivalCachesPopulated(payload);

                    // Manually trigger GenerateRivals NOW — the natural startup
                    // call was suppressed (queue not ready then), and the new-
                    // game call from customizer.StartGame might not fire if the
                    // user manually clicks before we're ready.  Our Prefix
                    // checks ClientRivalsInjected to ensure this is the ONLY
                    // successful run; subsequent calls (e.g. from the natural
                    // new-game flow) are skipped to preserve our cache.
                    if (!ClientRivalsInjected)
                    {
                        try
                        {
                            var gi = SaveGameManager.Current;
                            if (gi != null)
                            {
                                Plugin.Logger.LogInfo("[Patcher] Manually triggering RivalsHelper.GenerateRivals to consume the queue.");
                                BigAmbitions.Rivals.RivalsHelper.GenerateRivals(gi);
                            }
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] Manual GenerateRivals: {ex.Message}"); }
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] ApplyRivalsSnapshot: {ex.Message}"); }
            });
        }

        /// <summary>
        /// Populates gi.rivalStates + RivalsHelper.RivalDataCache on the
        /// LOCAL machine from a rivals snapshot.  Tries Harmony's AccessTools
        /// for the private cache field; falls back to RivalsHelper.FillData
        /// if direct access fails.  Safe to call from either role (client
        /// applying snapshot, or host applying its own roster).
        /// </summary>
        public static void EnsureRivalCachesPopulated(RivalsSnapshotPayload payload)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return;

                // 1. gi.rivalStates — top up missing AI rival IDs only.
                //    Player IDs are EXCLUDED here.  Why: the game's leaderboard
                //    iterates gi.rivalStates and renders rows for every id.  If
                //    we add player IDs but never get RivalData entries for them
                //    in the private RivalDataCache, the game renders ghost rows
                //    with raw template placeholders (e.g. "{numBusinesses}").
                //    Player rows come exclusively from our Patch_Load_AddPlayers
                //    Postfix; the game must never see player IDs in rivalStates.
                if (gi.rivalStates != null)
                {
                    var existing = new System.Collections.Generic.HashSet<string>();
                    for (int i = 0; i < gi.rivalStates.Count; i++)
                    {
                        var s = gi.rivalStates[i];
                        if (s == null) continue;
                        string sid = s.rivalId?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(sid)) existing.Add(sid);
                    }
                    int statesAdded = 0;
                    foreach (var r in payload.Rivals)
                    {
                        if (string.IsNullOrEmpty(r.Id)) continue;
                        if (r.IsPlayer) continue;          // ← key change
                        if (existing.Contains(r.Id)) continue;
                        gi.rivalStates.Add(new BigAmbitions.Rivals.RivalState { rivalId = r.Id });
                        statesAdded++;
                    }
                    if (statesAdded > 0)
                        Plugin.Logger.LogInfo($"[Patcher] gi.rivalStates topped up: +{statesAdded} AI entries ({gi.rivalStates.Count} total).");
                }

                // 2. RivalsHelper.RivalDataCache — try AccessTools first, then fall back to FillData.
                int cacheAdded = 0;
                try
                {
                    var fi = HarmonyLib.AccessTools.Field(typeof(BigAmbitions.Rivals.RivalsHelper), "RivalDataCache");
                    if (fi != null)
                    {
                        var dictObj = fi.GetValue(null);
                        if (dictObj is System.Collections.Generic.Dictionary<string, BigAmbitions.Rivals.RivalData> dict)
                        {
                            int playerPurged = 0;
                            foreach (var r in payload.Rivals)
                            {
                                if (string.IsNullOrEmpty(r.Id)) continue;
                                // PLAYERS never enter the cache (same rule as the
                                // rivalStates half above).  On 0.10 this whole
                                // AccessTools path silently failed, so the missing
                                // filter never fired; Mono resurrected it and the
                                // leaderboard grew a bare unselectable "defeated"
                                // stub per player NEXT TO our synthetic row
                                // (user, 2026-06-12).
                                if (r.IsPlayer) { if (dict.Remove(r.Id)) playerPurged++; continue; }
                                if (dict.ContainsKey(r.Id)) continue;
                                dict[r.Id] = new BigAmbitions.Rivals.RivalData
                                {
                                    id        = r.Id,
                                    rivalName = string.IsNullOrEmpty(r.Name) ? r.Id : r.Name,
                                    // Init owned-lists empty (Site-2/BuildSyntheticPlayerRivalData does the same) so
                                    // RivalData.WeeklyIncome / leaderboard reads never NRE if anything touches this
                                    // cache entry before the game's RefreshRivals backfills them.
                                    ownedBuildings              = new System.Collections.Generic.List<BuildingRegistration>(),
                                    ownedBusinesses             = new System.Collections.Generic.List<BuildingRegistration>(),
                                    ownedRetailOfficeBusinesses = new System.Collections.Generic.List<BuildingRegistration>(),
                                };
                                cacheAdded++;
                            }
                            Plugin.Logger.LogInfo($"[Patcher] RivalDataCache populated via AccessTools: +{cacheAdded} entries{(playerPurged > 0 ? $", purged {playerPurged} player stub(s)" : "")}.");
                        }
                        else
                        {
                            Plugin.Logger.LogWarning($"[Patcher] AccessTools got field but value is {(dictObj == null ? "null" : dictObj.GetType().FullName)} — falling back to FillData.");
                            cacheAdded = -1;
                        }
                    }
                    else
                    {
                        Plugin.Logger.LogWarning("[Patcher] AccessTools.Field returned null for RivalDataCache — falling back to FillData.");
                        cacheAdded = -1;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"[Patcher] RivalDataCache AccessTools path: {ex.Message} — falling back to FillData.");
                    cacheAdded = -1;
                }

                if (cacheAdded < 0)
                {
                    // FillData was a candidate fallback here, but calling it
                    // at runtime triggers Addressables.LoadAssetsAsync which
                    // hangs the client at 100% loading (FillData is only safe
                    // during SaveGameManager.Load context).  Leaving the cache
                    // alone — leaderboard will fall back to whatever rivals the
                    // client's own GenerateRivals produced.  Building popups
                    // still resolve via the GetRivalName Prefix override.
                    Plugin.Logger.LogInfo("[Patcher] Skipping RivalDataCache populate; leaderboard will use client-local rivals (see context log 2026-05-22 Wave 5 rollback).");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] EnsureRivalCachesPopulated: {ex.Message}"); }
        }

        /// <summary>
        /// Apply host's rivals stats snapshot.  Replaces ClientRivalStats dict.
        /// The RivalLeaderboard.GetRivalLeaderboardData Postfix in MPPatches
        /// reads from this dict to override the leaderboard rows with host's
        /// authoritative values.
        /// </summary>
        public static void ApplyRivalsStatsSnapshot(RivalsStatsSnapshotPayload payload)
        {
            if (payload == null) return;
            RunOnMainThread(() =>
            {
                try
                {
                    ClientRivalStats.Clear();
                    ClientBusinessIncomeByAddress.Clear();
                    ClientRivalBusinessAddrs.Clear();
                    int bizRows = 0;
                    foreach (var s in payload.Stats)
                    {
                        if (string.IsNullOrEmpty(s.Id)) continue;
                        ClientRivalStats[s.Id] = s;
                        if (s.Businesses != null && s.Businesses.Count > 0)
                        {
                            var addrs = new System.Collections.Generic.HashSet<string>();
                            foreach (var b in s.Businesses)
                            {
                                if (string.IsNullOrEmpty(b.AddressKey)) continue;
                                ClientBusinessIncomeByAddress[b.AddressKey] = b.WeeklyIncome;
                                addrs.Add(b.AddressKey);
                                bizRows++;
                            }
                            ClientRivalBusinessAddrs[s.Id] = addrs;
                        }
                    }
                    Plugin.Logger.LogInfo($"[Patcher] ClientRivalStats populated: {payload.Stats.Count} entries, {bizRows} business income rows.");

                    // If the leaderboard UI is currently visible (its
                    // GameObject is active in the hierarchy), re-call its
                    // Load() so the just-arrived stats are reflected.  We
                    // CHECK activeInHierarchy explicitly — Load() on a
                    // hidden/uninitialized RivalLeaderboard can dereference
                    // null serialized fields and crash the process natively.
                    try
                    {
                        var lbs = UnityEngine.Object.FindObjectsOfType(typeof(UI.Smartphone.Apps.Rivals.RivalLeaderboard));
                        if (lbs != null && lbs.Length > 0)
                        {
                            for (int i = 0; i < lbs.Length; i++)
                            {
                                var lb = lbs[i] as UI.Smartphone.Apps.Rivals.RivalLeaderboard;
                                if (lb == null) continue;
                                bool active = false;
                                try { active = lb.gameObject != null && lb.gameObject.activeInHierarchy; } catch { }
                                if (!active)
                                {
                                    Plugin.Logger.LogInfo("[Patcher] Skipping Load() on inactive RivalLeaderboard.");
                                    continue;
                                }
                                try { lb.Load(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] RivalLeaderboard.Load post-stats: {ex.Message}"); }
                            }
                        }
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] post-stats Load re-call: {ex.Message}"); }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] ApplyRivalsStatsSnapshot: {ex.Message}"); }
            });
        }

        /// <summary>
        /// Build a synthetic RivalData for a REMOTE human player so the game's
        /// own RivalLeaderboard.Load can include + rank them by income alongside
        /// AI rivals.  Owned-business lists are populated from local registrations
        /// (owner ids synced from host) so the count + estimated income resolve;
        /// non-null lists are mandatory (downstream code iterates them natively).
        /// Used by the GetAllRivalData Postfix (leaderboard) and the GetRivalData
        /// fallback (detail view click).
        /// </summary>
        public static BigAmbitions.Rivals.RivalData? BuildSyntheticPlayerRivalData(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return null;
            string name = playerId;
            try { if (ClientRivalNames.TryGetValue(playerId, out var n) && !string.IsNullOrWhiteSpace(n)) name = n; } catch { }
            int age = 30;
            try { if (ClientPlayerAges.TryGetValue(playerId, out var a) && a > 0) age = a; } catch { }
            var rd = new BigAmbitions.Rivals.RivalData
            {
                id        = playerId,
                rivalName = name,
                startingAgeInYears = age,
                ownedBuildings              = new System.Collections.Generic.List<BuildingRegistration>(),
                ownedBusinesses             = new System.Collections.Generic.List<BuildingRegistration>(),
                ownedRetailOfficeBusinesses = new System.Collections.Generic.List<BuildingRegistration>(),
            };
            // Generated-portrait fallback matches the real character's gender
            // (the synced PNG overrides this whenever it has arrived).
            try
            {
                if (ClientPlayerGenders.TryGetValue(playerId, out var g) && g >= 0)
                    rd.gender = (BigAmbitions.Characters.Gender)g;
            }
            catch { }
            PopulateRivalOwnedFromSync(rd, playerId);
            return rd;
        }

        /// <summary>
        /// (Re)populate a rival/player RivalData's owned-* lists from local
        /// registrations.  The leaderboard COUNT and the detail BREAKDOWN both use
        /// ownedRetailOfficeBusinesses, which (confirmed via HostBizDiag) contains
        /// Retail+Office+Cinema+Theater and excludes factories — so we mirror the
        /// HOST'S EXACT membership by address (ClientRivalStats[id].Businesses,
        /// built host-side from rd.ownedRetailOfficeBusinesses) rather than guess
        /// by BuildingType (which dropped cinema/theater).  ownedBusinesses gets
        /// the same set so the breakdown matches regardless of which list the game
        /// reads.  Falls back to owner-id-match-minus-factory before stats arrive.
        /// ownedBuildings = real estate (buildingOwnerRivalId), independent.
        /// </summary>
        public static void PopulateRivalOwnedFromSync(BigAmbitions.Rivals.RivalData rd, string id)
        {
            if (rd == null || string.IsNullOrEmpty(id)) return;
            try
            {
                if (rd.ownedBuildings  != null) rd.ownedBuildings.Clear();
                if (rd.ownedBusinesses != null) rd.ownedBusinesses.Clear();
                if (rd.ownedRetailOfficeBusinesses != null) rd.ownedRetailOfficeBusinesses.Clear();

                System.Collections.Generic.HashSet<string>? syncedAddrs = null;
                System.Collections.Generic.Dictionary<string, float>? syncedIncome = null;
                if (ClientRivalStats.TryGetValue(id, out var stat) && stat.Businesses != null && stat.Businesses.Count > 0)
                {
                    syncedAddrs  = new System.Collections.Generic.HashSet<string>();
                    syncedIncome = new System.Collections.Generic.Dictionary<string, float>();
                    foreach (var b in stat.Businesses)
                        if (!string.IsNullOrEmpty(b.AddressKey))
                        {
                            syncedAddrs.Add(b.AddressKey);
                            syncedIncome[b.AddressKey] = b.WeeklyIncome;
                        }
                }

                var gi = SaveGameManager.Current;
                if (gi == null || gi.BuildingRegistrations == null) return;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    try
                    {
                        string bldg = reg.buildingOwnerRivalId?.ToString() ?? "";
                        string biz  = reg.businessOwnerRivalId?.ToString() ?? "";
                        if (bldg == id && rd.ownedBuildings != null) rd.ownedBuildings.Add(reg);

                        bool isBiz;
                        if (syncedAddrs != null)
                            isBiz = syncedAddrs.Contains(GameStateReader.AddressKey(reg));   // host's exact set
                        else
                        {
                            // Fallback before stats arrive: owner match minus factory.
                            bool isFactory = false; try { isFactory = reg.businessTypeName == "ba:businesstype_factory"; } catch { }
                            isBiz = (biz == id) && !isFactory;
                        }
                        if (isBiz)
                        {
                            if (rd.ownedBusinesses             != null) rd.ownedBusinesses.Add(reg);
                            if (rd.ownedRetailOfficeBusinesses != null) rd.ownedRetailOfficeBusinesses.Add(reg);
                            // Feed the replica's dailyIncomes from the synced
                            // per-business weekly figure: RivalData.WeeklyIncome
                            // and the detail-view graphs compute NATIVELY from
                            // dailyIncomes.TakeLast(7) — replicas had it empty,
                            // so player rows graphed flat zero (0.11 UI map).
                            try
                            {
                                if (syncedIncome != null
                                    && syncedIncome.TryGetValue(GameStateReader.AddressKey(reg), out var wk)
                                    && reg.dailyIncomes != null
                                    && !reg.RentedByPlayer)   // never overwrite the OWNER'S own income series from a rival's synced figure
                                {
                                    reg.dailyIncomes.Clear();
                                    for (int d = 0; d < 7; d++) reg.dailyIncomes.Add(wk / 7f);
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                // Real graphs: install the synced per-day series as this
                // player's RivalState (runtime-only; stripped at save).
                InstallPlayerRivalStateHistory(id);
            }
            catch { }
        }

        // ── Interior sync (Phase 2a: simple fields only — items deferred to 2b) ──

        /// <summary>
        /// Apply a host's interior snapshot to the local BuildingRegistration.
        /// Phase 2a writes Layout / interiorDesigns / retailPrices / dirtSpots.
        /// ItemInstances are NOT yet synced (Phase 2b).
        /// </summary>
        public static void ApplyInteriorSnapshot(InteriorSnapshotPayload payload)
        {
            if (payload == null || string.IsNullOrEmpty(payload.AddressKey)) return;
            RunOnMainThread(() =>
            {
                try
                {
                    var reg = FindRegistration(payload.AddressKey);
                    if (reg == null)
                    {
                        Plugin.Logger.LogWarning($"[Patcher] ApplyInteriorSnapshot: no reg for '{payload.AddressKey}'");
                        return;
                    }

                    // Housing: a guest editing this interior holds in-progress LOCAL edits they forward on close;
                    // don't let an incoming snapshot clobber them mid-session.
                    if (HousingDesign.GuestIsDesigning(payload.AddressKey)) return;

                    // CATCH-ALL (2026-06-17): the host's own replica of a PLAYER-owned business is flagged
                    // non-authoritative (InteriorSync.BuildSnapshotForHostSend). Such a snapshot must NEVER
                    // touch the owner's real interior — only the owner's OWN authoritative push may. This one
                    // gate protects EVERY collection below (designs / prices / dirt / items + any added later).
                    bool isPlayerBusiness = false;
                    try { isPlayerBusiness = IsAnyPlayerBusiness(reg) || reg.RentedByPlayer || !string.IsNullOrEmpty(payload.OwnerPlayerId); }
                    catch { }
                    if (isPlayerBusiness && !payload.Authoritative)
                    {
                        Plugin.Logger.LogWarning($"[Patcher] Interior apply SKIPPED for '{payload.AddressKey}': non-authoritative snapshot for a player-owned business — kept local interior.");
                        return;
                    }

                    // Layout (string; controls which BusinessLayoutSet is used).
                    // Capture whether it ACTUALLY changes — the visual refresh below re-calls
                    // LoadBuilding only when it did.  Re-calling LoadBuilding on a building that already
                    // shows the right layout (the IRS; or a 2nd/Nth snapshot for the same shop) spawns a
                    // DUPLICATE set of stations on top → the "8 booths vs 4 / 30 stations" duplication
                    // (user 2026-06-23) plus malformed duplicates that NRE on staffing.
                    string _layoutBefore = reg.Layout;
                    if (!string.IsNullOrEmpty(payload.Layout))
                        reg.Layout = payload.Layout;
                    bool _layoutChanged = !string.Equals(_layoutBefore, reg.Layout, StringComparison.Ordinal);

                    // Interior designs (wall/floor/ceiling material+color).
                    // Diffed like items: only elements whose serialized form
                    // changed get re-Deserialized in the refresh below.
                    var changedDesignUuids = new HashSet<string>();
                    try
                    {
                        if (reg.interiorDesigns != null)
                        {
                            if (!_lastDesignSer.TryGetValue(payload.AddressKey, out var lastDSer))
                                lastDSer = new Dictionary<string, string>();
                            var newDSer = new Dictionary<string, string>();
                            reg.interiorDesigns.Clear();
                            foreach (var d in payload.InteriorDesigns)
                            {
                                // `materials` MUST be a non-null array: the game's InteriorElement.Deserialize does
                                // `materials.Length` with NO null-check, so a null here NRE's inside LoadBuilding →
                                // aborts the building-enter coroutine → the player is stuck on a BLACK SCREEN on load
                                // (user 2026-06-30). The game's own Serialize() always uses an array (empty, never
                                // null) — match that. (The old `if (Count>0)` left it null for empty designs.)
                                int mcount = d.Materials?.Count ?? 0;
                                var arr = new SerializedInteriorDesign.SerializableInteriorMaterial[mcount];
                                for (int i = 0; i < mcount; i++)
                                {
                                    var m = d.Materials[i];
                                    arr[i] = new SerializedInteriorDesign.SerializableInteriorMaterial
                                    {
                                        MaterialID    = m.MaterialID,
                                        MaterialIndex = m.MaterialIndex,
                                        ColorIndex    = m.ColorIndex,
                                    };
                                }
                                var sd = new SerializedInteriorDesign { UUID = d.UUID, materials = arr };
                                reg.interiorDesigns.Add(sd);

                                string uuid = d.UUID ?? "";
                                if (string.IsNullOrEmpty(uuid)) continue;
                                string ser = Newtonsoft.Json.JsonConvert.SerializeObject(d);
                                newDSer[uuid] = ser;
                                if (!lastDSer.TryGetValue(uuid, out var prev) || prev != ser)
                                    changedDesignUuids.Add(uuid);
                            }
                            _lastDesignSer[payload.AddressKey] = newDSer;
                        }
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] interiorDesigns apply: {ex.Message}"); }

                    // Retail prices
                    try
                    {
                        if (reg.retailPrices != null)
                        {
                            reg.retailPrices.Clear();
                            foreach (var rp in payload.RetailPrices)
                                reg.retailPrices.Add(new RetailPrice { itemName = rp.ItemName, price = rp.Price });
                        }
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] retailPrices apply: {ex.Message}"); }

                    // Dirt spots
                    try
                    {
                        if (reg.dirtSpots != null)
                        {
                            reg.dirtSpots.Clear();
                            foreach (var ds in payload.DirtSpots)
                                reg.dirtSpots.Add(new DirtSpot { x = ds.X, z = ds.Z, dirtiness = ds.Dirtiness });
                        }
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] dirtSpots apply: {ex.Message}"); }

                    // ItemInstances (Phase 2b).  PER-ITEM DIFF apply (2026-06-12):
                    // a full wipe-and-rebuild re-rolled every shelf's visual
                    // shuffle on every snapshot (ShelfController.CollectVisualItems
                    // uses global UnityEngine.Random at spawn; flicker every few
                    // seconds — user bug).  Items whose serialized form is
                    // UNCHANGED keep their live ItemInstance object so their
                    // ItemController (and its shuffled look) survives untouched.
                    var changedIds = new HashSet<string>();
                    var removedIds = new HashSet<string>();
                    var movedIds   = new HashSet<string>();   // transform-only deltas → move in place (round-22)
                    try
                    {
                        if (reg.itemInstances != null)
                        {
                            bool protectPlayerBusiness = false;
                            try
                            {
                                protectPlayerBusiness = IsAnyPlayerBusiness(reg)
                                                        || reg.RentedByPlayer
                                                        || !string.IsNullOrEmpty(payload.OwnerPlayerId);
                            }
                            catch { }

                            if (protectPlayerBusiness
                                && payload.ItemInstances.Count == 0
                                && !payload.ItemInstancesAuthoritative)
                            {
                                Plugin.Logger.LogWarning($"[Patcher] Interior item apply skipped for '{payload.AddressKey}': empty non-authoritative player-business snapshot would clear {reg.itemInstances.Count} local item(s).");
                            }
                            else
                            {
                            if (!_lastItemSer.TryGetValue(payload.AddressKey, out var lastSer))
                                lastSer = new Dictionary<string, string>();
                            var newSer  = new Dictionary<string, string>();
                            var newDict = new Dictionary<string, BigAmbitions.Items.ItemInstance>();
                            int restocked = 0;
                            // Round-38d — CARGO AUTHORITY SHIELD. The only guest→owner interior flow is the
                            // furniture-edit forward, and its snapshot carries the guest's REPLICA cargo — never
                            // authoritative (all legit cargo mutations route via BStore ops). Field-proven leak
                            // (2026-07-07): a guest's UNROUTED local register take diverged their replica; their
                            // next furniture forward was adopted wholesale and EMPTIED the owner's real register
                            // (''×0 at the 1467 setstock audit) — laundering a replica divergence into owner
                            // state. When THIS machine owns the building, pre-existing items keep their LIVE
                            // cargo; new items (a placed box) keep the snapshot's. Guests applying owner pushes
                            // are untouched (RentedByPlayer false there).
                            bool receiverOwnsThis = false;
                            try { receiverOwnsThis = MergerFlip.TrulyMine(reg); } catch { }   // TrulyMine: never shield replica cargo against the true owner
                            int cargoShielded = 0;
                            foreach (var i in payload.ItemInstances)
                            {
                                if (string.IsNullOrEmpty(i.Id)) continue;
                                string ser = Newtonsoft.Json.JsonConvert.SerializeObject(i);
                                newSer[i.Id] = ser;
                                if (lastSer.TryGetValue(i.Id, out var prev) && prev == ser
                                    && reg.itemInstances.TryGetValue(i.Id, out var live) && live != null)
                                {
                                    newDict[i.Id] = live;   // unchanged → keep the live object
                                    continue;
                                }
                                var ii = DeserializeItemInstance(i);
                                if (ii == null) continue;
                                // Round-37j — IN-PLACE APPLY (the flatbed model, user-mandated after the 6th
                                // destroy+respawn bug): deltas that don't change WHAT the item is keep the LIVE
                                // ItemInstance object — its native callbacks (ShelfController.UpdateVisuals et al,
                                // wired at Start) and any PlacementSystem references stay valid. IdentitySig is
                                // 'core#cargo#stacked':
                                //   whole sig equal            → transform-only  → move in place (round-22);
                                //   core+stacked equal         → cargo-only      → swap the cargo list on the live
                                //                                 object + fire the native cargo callback (visuals
                                //                                 refresh exactly like a local deposit);
                                //   core or stacked changed    → a genuinely different item → destroy+respawn.
                                if (reg.itemInstances.TryGetValue(i.Id, out var prevLive) && prevLive != null)
                                {
                                    string ns = IdentitySig(i), ls = IdentitySig(prevLive);
                                    bool transformOnly = ns == ls;
                                    bool cargoOnly = false;
                                    if (!transformOnly)
                                    {
                                        var np = ns.Split('#'); var lp = ls.Split('#');
                                        cargoOnly = np.Length == 3 && lp.Length == 3 && np[0] == lp[0] && np[2] == lp[2];
                                    }
                                    if (transformOnly || cargoOnly)
                                    {
                                        if (cargoOnly)
                                        {
                                            if (receiverOwnsThis)
                                            {
                                                cargoShielded++;             // keep the live (owner-true) cargo
                                            }
                                            else
                                            {
                                                prevLive.cargoInstances = ii.cargoInstances;
                                                try { prevLive.OnItemsInCargoUpdated()?.Invoke(); } catch { }
                                                restocked++;
                                            }
                                        }
                                        prevLive.position  = ii.position;    // data transform follows the wire
                                        prevLive.yRotation = ii.yRotation;
                                        newDict[i.Id] = prevLive;            // KEEP the live object
                                        movedIds.Add(i.Id);                  // GameObject transform sync pass
                                        continue;
                                    }
                                    // Core/stacked changed → respawn below; the shield still applies: the
                                    // replacement keeps the OWNER's live cargo, not the guest replica's.
                                    if (receiverOwnsThis)
                                    {
                                        ii.cargoInstances = prevLive.cargoInstances;
                                        cargoShielded++;
                                    }
                                }
                                newDict[i.Id] = ii;
                                changedIds.Add(i.Id);
                            }
                            foreach (var k in reg.itemInstances.Keys)
                                if (!newDict.ContainsKey(k)) removedIds.Add(k);

                            // Buyer-side purchasability (2026-06-12): hover highlight
                            // + take-product in a shop come from playerItemPurchaserSettings
                            // (that's what AI shops set; ItemController.Start builds the
                            // PlayerItemPurchaser from it).  Owners replicate their items
                            // with it DISABLED (owners manage, not buy) — enable it here
                            // on cargo shelves so this machine can shop natively.  Price
                            // display reads reg.retailPrices = the synced store table.
                            int purchasable = 0;
                            // RESIDENCE GUARD (round-15): fresh food etc. are RetailProducts, so a guest's stocked
                            // FRIDGE qualified for shop-shelf purchaser settings — and the game then treats it as a
                            // SHOP SHELF for NON-owners: ItemController.Interact (:492) buys instead of opening the
                            // menu, and OverlayHelper.GetRelevantEntity's non-owner branch (:178) remaps the entity.
                            // Buyer-purchasing only makes sense in a building that RUNS A BUSINESS — skip the rest.
                            bool hasBusiness = false;
                            try { hasBusiness = !string.IsNullOrEmpty(reg.businessTypeName?.ToString()); } catch { }
                            if (hasBusiness)
                            foreach (var kv in newDict)
                            {
                                var ii = kv.Value;
                                try
                                {
                                    if (ii == null) continue;
                                    // Round-34 (supersedes round-33's storage-shelf skip): buyer-purchasability
                                    // belongs ONLY on retail DISPLAY surfaces — the game's own stock-carrier set
                                    // (PointOfSale | ShowcaseShelf, ItemHelper.IsStockCarrier). It was landing on
                                    // ANY cargo item holding retail products: backroom STORAGE shelves (no box
                                    // visuals) and guest-placed BOXES — and because the OWNER adopts guest edits
                                    // through this same apply, the flags hit the owner's machine and SAVE too
                                    // (owner couldn't grab the box or manage the shelf; user 2026-07-04).
                                    // Whitelist display types, skip on the owner's own machine, heal the rest.
                                    bool qualifies = false;
                                    try
                                    {
                                        qualifies = !reg.RentedByPlayer
                                            && ii.ItemCached != null
                                            && (ii.ItemCached.type & (BigAmbitions.Items.ItemType.PointOfSale | BigAmbitions.Items.ItemType.ShowcaseShelf)) != 0;
                                    }
                                    catch { }
                                    if (!qualifies)
                                    {
                                        try { if (ii.playerItemPurchaserSettings != null && ii.playerItemPurchaserSettings.enabled) ii.playerItemPurchaserSettings.enabled = false; } catch { }
                                        continue;
                                    }
                                    var cargo = ii.cargoInstances;
                                    if (cargo == null || cargo.Count == 0) continue;
                                    if (ii.playerItemPurchaserSettings != null && ii.playerItemPurchaserSettings.enabled) continue;
                                    string? product = null;
                                    foreach (var c in cargo)
                                    {
                                        if (c == null || string.IsNullOrEmpty(c.itemName)) continue;
                                        var it = BigAmbitions.Items.ItemsGetter.GetByName(c.itemName);
                                        if (it != null && (it.type & BigAmbitions.Items.ItemType.RetailProduct) != 0)
                                        { product = c.itemName; break; }
                                    }
                                    if (product == null) continue;
                                    ii.playerItemPurchaserSettings = new BigAmbitions.Items.PlayerItemPurchaserSettings
                                    {
                                        enabled      = true,
                                        itemName     = product,
                                        itemQuantity = 1,
                                    };
                                    purchasable++;
                                }
                                catch { }
                            }
                            if (purchasable > 0)
                                Plugin.Logger.LogInfo($"[Patcher] enabled buyer purchasing on {purchasable} cargo shelf/shelves for '{payload.AddressKey}'.");

                            if (restocked > 0)
                                Plugin.Logger.LogInfo($"[Patcher] {restocked} item(s) restocked IN PLACE for '{payload.AddressKey}' (live objects kept; native cargo callbacks fired).");
                            if (cargoShielded > 0)
                                Plugin.Logger.LogInfo($"[Patcher] cargo authority shield kept OWNER cargo on {cargoShielded} item(s) for '{payload.AddressKey}' (guest-edit adoption carries replica cargo — never authoritative).");
                            reg.itemInstances.Clear();
                            foreach (var kv in newDict) reg.itemInstances[kv.Key] = kv.Value;
                            _lastItemSer[payload.AddressKey] = newSer;
                            }
                        }
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] itemInstances apply: {ex.Message}"); }

                    // Round-39d — Phase 3 customer presence: seed the local shopper table from the owner's
                    // schedule (no-op on the owner; local completed flags preserved inside SeedFor).
                    try
                    {
                        if (payload.Authoritative && payload.CustomerEntries != null && payload.CustomerEntries.Count > 0)
                            CustomerEntrySync.SeedFor(reg, payload.CustomerEntries);
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] customer-entry seed: {ex.Message}"); }
                    // Round-39e — complaint parity: adopt the owner's fulfilled-demand set so the seeded
                    // customers complain about the RIGHT things (never on the owner's own machine).
                    try
                    {
                        if (payload.Authoritative && !reg.RentedByPlayer
                            && payload.FulfilledDemands != null && payload.FulfilledDemands.Count > 0)
                            reg.cachedFulfilledCustomerDemands = new System.Collections.Generic.List<string>(payload.FulfilledDemands);
                    }
                    catch { }

                    Plugin.Logger.LogInfo($"[Patcher] Interior applied for '{payload.AddressKey}': layout='{payload.Layout}' {InteriorSync.SnapshotSummary(payload)} (changed={changedIds.Count} moved={movedIds.Count} removed={removedIds.Count}).");

                    // Trigger a visual refresh of the interior IF the local
                    // player is currently inside THIS building.  Writing to
                    // reg.* only updates the data model; the walls/floor/items
                    // GameObjects were already instantiated from stale data
                    // when LoadBuilding ran on entry.  Calling LoadBuilding
                    // again re-runs the full pipeline (layout, designs, items)
                    // against the now-fresh fields.
                    TryRefreshActiveInteriorIfMatches(payload.AddressKey, changedIds, removedIds, changedDesignUuids, _layoutChanged, movedIds);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] ApplyInteriorSnapshot: {ex.Message}"); }
            });
        }

        /// <summary>
        /// If the local player is inside the building whose interior we just
        /// applied, re-paint the wall/floor/ceiling visuals to match the new
        /// reg.interiorDesigns data.  No-op if not in that building.
        ///
        /// Implementation: element-level.  For each InteriorElement GameObject
        /// in the scene, look up its UUID in our fresh reg.interiorDesigns dict
        /// and call element.Deserialize(design) directly.  This is the primitive
        /// the game uses internally and re-applies materials on every call.
        ///
        /// Two higher-level alternatives we tried and disproved:
        ///   - BuildingManager.LoadBuilding(false): returns true but doesn't
        ///     re-paint existing InteriorElements on subsequent calls.
        ///   - BuildingManager.ApplyInteriorDesign(building, elements): same —
        ///     likely resolves the registration via building.GetRegistration()
        ///     which returns a different instance than our gi.BuildingRegistrations
        ///     write target, so it reads stale designs.
        /// element.Deserialize works because it operates directly on the scene
        /// element using the design object we pass in.
        /// </summary>
        /// <summary>Last-applied serialized form per item id, per address —
        /// the diff baseline that keeps unchanged shelf visuals alive.</summary>
        private static readonly Dictionary<string, Dictionary<string, string>> _lastItemSer = new();

        /// <summary>Same baseline for interior-design elements (walls/doors):
        /// UUID → serialized form, per address.  Re-Deserializing all ~120
        /// elements on every snapshot made the door (+ its light shaft)
        /// visibly flicker (user, 2026-06-12).</summary>
        private static readonly Dictionary<string, Dictionary<string, string>> _lastDesignSer = new();

        /// <summary>True if this address has had an interior snapshot applied —
        /// i.e. it's ANOTHER session player's building replicated here.  Used by
        /// the shelf-fill patch to tell player shops from AI shops.</summary>
        public static bool IsReplicatedInterior(string addressKey)
            => !string.IsNullOrEmpty(addressKey) && _lastItemSer.ContainsKey(addressKey);

        /// <summary>Addresses whose interiors are replicas here (audit scope —
        /// the source machine's copy is the reference to compare against).</summary>
        public static List<string> ReplicatedInteriorAddresses()
            => new List<string>(_lastItemSer.Keys);

        /// <summary>Replace gi.marketEvents with the host's authoritative list
        /// (MAIN THREAD).  MarketEvent is a plain data class — Newtonsoft
        /// round-trips it wholesale.</summary>
        public static void ApplyMarketEvents(string json)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.marketEvents == null) return;
                var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<MarketEvent>>(json);
                if (list == null) return;
                gi.marketEvents.Clear();
                foreach (var e in list) if (e != null) gi.marketEvents.Add(e);
                Plugin.Logger.LogInfo($"[Patcher] market events applied: {list.Count} event(s) from host.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] ApplyMarketEvents: {ex.Message}"); }
        }

        /// <summary>True when this registration's business belongs to ANY session
        /// player — YOU **or** another connected player.  This is NOT "is this mine?"
        /// (after the rival-translation, businessOwnerRivalId carries the owning
        /// player's id; this delegates to IsSessionPlayerRivalId).  Its only job is
        /// shielding EVERY player's shop from the local AI economy, which otherwise
        /// adopts any replica: cashiers auto-spawn on entry (SetupAiEmployeeStations)
        /// and the daily rival sim re-prices it (CompetitionHelper) — both
        /// decompile-confirmed to validate NOTHING beyond RentedByPlayer/non-empty
        /// owner id.
        ///
        /// FOOTGUN — do NOT use this to ask "is this MY shop?":
        ///   • it is true for EVERY player's shop, not just yours; and
        ///   • it is FALSE for your own freshly-loaded shop (businessOwnerRivalId is
        ///     empty until the rival-translation stamps it).
        /// For "is this the local player's own shop?" use IsReceiversOwnBusiness.</summary>
        public static bool IsAnyPlayerBusiness(BuildingRegistration? reg)
        {
            if (reg == null) return false;
            if (IsSessionPlayerRivalId(reg.businessOwnerRivalId)) return true;
            // Re-host durability (2026-06-24): the live roster forgets a player who is
            // disconnected, but the host's ownership LEDGER (restored from the manifest)
            // still reserves their building to them.  A building reserved to a player —
            // even one currently absent — is a player's shop and must stay shielded from
            // the local AI economy, exactly as it is while the owner is online.  Host-only
            // (clients keep no ledger); correct, since the AI economy runs on the host.
            return IsLedgerReservedToPlayer(reg);
        }

        /// <summary>HOST-only: true when this building is reserved in the ownership
        /// ledger — BuildingOwners (rents/operates) or BuildingRealEstateOwners (bought)
        /// — to a player: a connected player's pid OR an absent owner's stable id held
        /// for reconnect.  "host" and empty are excluded (the host's own buildings are
        /// already native via RentedByPlayer; empty = unowned/AI).</summary>
        private static bool IsLedgerReservedToPlayer(BuildingRegistration reg)
        {
            try
            {
                if (!MPServer.IsRunning) return false;
                string addr = GameStateReader.AddressKey(reg);
                if (string.IsNullOrEmpty(addr)) return false;
                if (MPServer.BuildingOwners.TryGetValue(addr, out var o)
                    && !string.IsNullOrEmpty(o) && o != "host") return true;
                if (MPServer.BuildingRealEstateOwners.TryGetValue(addr, out var r)
                    && !string.IsNullOrEmpty(r) && r != "host") return true;
                return false;
            }
            catch { return false; }
        }

        /// <summary>True for the LOCAL player's OWN business — the correct "is this
        /// mine?" test (contrast IsAnyPlayerBusiness, which is true for everyone's
        /// shop and false for your own fresh shop).  TrulyMine = RentedByPlayer MINUS
        /// the merger flip: a flipped partner shop reads rented for native MENUS, but
        /// to the SYNC layer it is a replica whose true owner's pushes must apply
        /// (run-4 inversion, 2026-07-07). Where a business payload is available,
        /// callers also OR in OwnerPlayerId == MPConfig.PlayerId for positive
        /// attribution (see ApplyBusinessInfoLocal's receiverOwnsThis).</summary>
        public static bool IsReceiversOwnBusiness(BuildingRegistration? reg)
            => MergerFlip.TrulyMine(reg);

        /// <summary>True if this rival-owner id actually belongs to a session MP player (our own id,
        /// a roster client, or any session player) — i.e. a "rival" that is really another player, NOT a
        /// game AI rival. Used to shield player-owned shops from AI-economy administration (shutdown /
        /// re-price / re-value / overtake) while leaving them in the world so competition still applies.
        /// NOTE: a disconnected player STAYS in ClientPlayerRoster for the host session (their buildings
        /// are held for reconnect), so this keeps returning true while they're away — the shield does not
        /// lapse on a drop, only on a full host restart.</summary>
        public static bool IsSessionPlayerRivalId(string? owner)
        {
            try
            {
                if (string.IsNullOrEmpty(owner)) return false;
                if (owner == MPConfig.PlayerId) return true;
                if (ClientPlayerRoster.ContainsKey(owner!)) return true;
                return MPRestSync.AllPlayers().Contains(owner);
            }
            catch { return false; }
        }

        /// <summary>GUEST: record the item state we just FORWARDED as already-applied, so the owner's echo of
        /// our own edit ser-matches (`prev == ser` keep-branch above) and the live objects stay put. Without
        /// this, every guest placement echoed back as "changed" and destroy+respawned the just-placed
        /// ItemController — a same-frame click on the dying zombie then NRE'd inside StartPlacementMode with
        /// the PlacementMode navigation blocker stranded = the guest locked in place (round-14 #3,
        /// client log :1232+). The echo must match byte-for-byte: both sides build the DTO with the same
        /// SerializeItemInstance and the diff key is the same JsonConvert of it.</summary>
        public static void NoteLocalItemState(string addressKey, System.Collections.Generic.List<ItemInstanceInfo> items)
        {
            try
            {
                if (string.IsNullOrEmpty(addressKey) || items == null) return;
                var ser = new Dictionary<string, string>();
                foreach (var i in items)
                    if (i != null && !string.IsNullOrEmpty(i.Id))
                        ser[i.Id] = Newtonsoft.Json.JsonConvert.SerializeObject(i);
                _lastItemSer[addressKey] = ser;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] NoteLocalItemState: {ex.Message}"); }
        }

        private static void TryRefreshActiveInteriorIfMatches(string addressKey,
            HashSet<string>? changedIds = null, HashSet<string>? removedIds = null,
            HashSet<string>? changedDesignUuids = null, bool layoutChanged = true,
            HashSet<string>? movedIds = null)
        {
            try
            {
                var bms = UnityEngine.Object.FindObjectsOfType(typeof(BuildingManager));
                if (bms == null || bms.Length == 0) return;
                BuildingManager? matched = null;
                for (int i = 0; i < bms.Length; i++)
                {
                    var bm = bms[i] as BuildingManager;
                    if (bm == null) continue;
                    var activeReg = bm.buildingRegistration;
                    if (activeReg == null) continue;
                    if (GameStateReader.AddressKey(activeReg) != addressKey) continue;
                    matched = bm;
                    break;
                }
                if (matched == null) return;
                var reg = matched.buildingRegistration;
                if (reg == null || reg.interiorDesigns == null) return;

                // For AI businesses (CoffeeShop, FastFoodRestaurant, etc.),
                // the items come from the BusinessLayoutSet template named by
                // reg.Layout — they're NOT in reg.itemInstances.  At first
                // entry the client's LoadBuilding ran with reg.Layout still
                // empty (Phase 1b suppression prevented CityGenerator from
                // setting it), so nothing was template-spawned.  Now that our
                // snapshot has set reg.Layout, re-call LoadBuilding so the
                // template spawns its furniture/shelves.  Idempotent for
                // already-loaded layouts.
                // DE-DUP (2026-06-23): only re-spawn the layout when it actually changed.  Re-calling
                // LoadBuilding when the layout is already correct stacks a DUPLICATE set of stations
                // (the IRS 8-vs-4 / 30-station duplication + malformed duplicates that NRE on staffing).
                try { if (layoutChanged) matched.LoadBuilding(false); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] LoadBuilding re-trigger for layout '{reg.Layout}': {ex.Message}"); }

                // MP STAFFING FIX (2026-06-23): native AI staffing (BuildingManager.SetupAiEmployeeStations)
                // runs on building ENTRY — but for AI businesses the layout is Phase-1b-suppressed on the
                // client and only just spawned its stations via the re-load ABOVE, AFTER that staffing
                // already ran.  So the stations are UNSTAFFED on the client (e.g. hairdresser chairs / IRS
                // booths → a customer joins an unservable queue and the cancel path NREs on the null employee
                // → hard lock; user 2026-06-23: client hairdresser had 0 staffed vs host's 5).  Re-run the
                // native staffing now that the stations exist: AssignEmployee self-skips already-staffed
                // stations (idempotent) and SetupAiEmployeeStations self-skips player-owned shops.
                // Re-staff the (possibly freshly-spawned) stations — DEFERRED a couple frames so their Start
                // has run.  Staffing them the SAME frame the re-load spawns them NREs inside the game's
                // AssignEmployee (uninitialised item data — Braids and Blowouts, 2026-06-23).  Run on the
                // building's own MonoBehaviour; ReStaffStationsNow is idempotent (skips already-staffed).
                try { matched.StartCoroutine(ReStaffAfterInit(matched, addressKey)); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] schedule re-staff for '{addressKey}': {ex.Message}"); }

                // UUID → SerializedInteriorDesign dict from the fresh data we just wrote.
                var dict = new System.Collections.Generic.Dictionary<string, SerializedInteriorDesign>();
                for (int i = 0; i < reg.interiorDesigns.Count; i++)
                {
                    var d = reg.interiorDesigns[i];
                    if (d == null) continue;
                    string u = d.UUID?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(u)) dict[u] = d;
                }

                int deserialized = 0;
                int matchedUuids = 0;
                int skippedUnchanged = 0;
                var elementsObj = UnityEngine.Object.FindObjectsOfType(typeof(InteriorElement));
                if (elementsObj != null)
                {
                    for (int i = 0; i < elementsObj.Length; i++)
                    {
                        var el = elementsObj[i] as InteriorElement;
                        if (el == null) continue;
                        string uuid = el.UUID?.ToString() ?? "";
                        if (string.IsNullOrEmpty(uuid)) continue;
                        if (!dict.TryGetValue(uuid, out var design)) continue;
                        matchedUuids++;
                        // Diff: re-Deserializing an UNCHANGED element makes
                        // doors + their light visibly flicker on every snapshot
                        // (user, 2026-06-12).  null set = no diff info → all.
                        if (changedDesignUuids != null && !changedDesignUuids.Contains(uuid))
                        { skippedUnchanged++; continue; }
                        try { el.Deserialize(design); deserialized++; }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] InteriorElement.Deserialize for UUID '{uuid}': {ex.Message}"); }
                    }
                }
                Plugin.Logger.LogInfo($"[Patcher] Interior refresh for '{addressKey}': deserialized {deserialized}/{matchedUuids} elements (skipped {skippedUnchanged} unchanged, of {dict.Count} designs).");

                // ── Item refresh (Phase 2b) ─────────────────────────────────
                // Per-item diff: only touch the ItemControllers whose data
                // actually changed (or vanished); unchanged shelves keep their
                // GameObjects — and with them the game's random stock-visual
                // shuffle (full rebuilds re-rolled it = flicker, 2026-06-12).
                RefreshItemsForActiveBuilding(matched, reg, changedIds, removedIds, movedIds);

                // Round-34 (user: a guest-placed register must warn the OWNER about paper bags exactly like
                // an owner-placed one): adoption skips the native placement tail, so refresh warning icons +
                // the without-stock todo tasks for the adopted items on the OWNER's machine. Deferred like
                // the re-staff so the respawned controllers' Start has run.
                try
                {
                    if (reg.RentedByPlayer)
                        matched.StartCoroutine(OwnerWarningRefreshAfterInit(matched, addressKey, changedIds, movedIds));
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] schedule owner warning refresh: {ex.Message}"); }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] TryRefreshActiveInteriorIfMatches: {ex.Message}"); }
        }

        private static System.Collections.IEnumerator OwnerWarningRefreshAfterInit(
            BuildingManager bm, string addressKey,
            System.Collections.Generic.HashSet<string>? changed, System.Collections.Generic.HashSet<string>? moved)
        {
            yield return null;
            yield return null;
            OwnerWarningRefreshNow(bm, addressKey, changed, moved);
        }

        private static void OwnerWarningRefreshNow(
            BuildingManager bm, string addressKey,
            System.Collections.Generic.HashSet<string>? changed, System.Collections.Generic.HashSet<string>? moved)
        {
            try
            {
                var reg = bm?.buildingRegistration;
                if (reg == null || !reg.RentedByPlayer) return;
                if (GameStateReader.AddressKey(reg) != addressKey) return;   // owner left this building → skip
                var ids = new System.Collections.Generic.HashSet<string>();
                if (changed != null) foreach (var i in changed) ids.Add(i);
                if (moved   != null) foreach (var i in moved)   ids.Add(i);
                if (ids.Count == 0) return;
                // The game's own "items changed" hub (designer close funnels here too): available producers,
                // customer capacity from seating, promotion, employee↔work-station assignments, and the
                // onBuildingRegistrationChange listeners. Self-gates on owner-inside.
                try { bm.OnItemChanged(forced: true); } catch { }
                try { InstanceBehavior<Player.HUD.ItemWarningIcons.ItemWarningIconManager>.Instance?.UpdateWarningIconByIds(ids); } catch { }
                try { Helpers.BusinessHelper.GenerateItemsWithoutStockTasks(reg); } catch { }
                // Security devices are the one family the hub skips (native placement covers them in
                // OnItemPositionUpdated :1387): recompute per-panel coverage + the registration's level.
                try
                {
                    bool anySecurity = false;
                    foreach (var id in ids)
                        if (reg.itemInstances != null && reg.itemInstances.TryGetValue(id, out var ii) && ii?.ItemCached != null
                            && ii.ItemCached.HasTag(BigAmbitions.Tags.TagRef.Itemtag.issecuritypanel))
                        { try { ii.UpdateSecurityPanelCoverage(); anySecurity = true; } catch { } }
                    if (anySecurity) try { reg.UpdateSecurityLevel(); } catch { }
                }
                catch { }
                Plugin.Logger.LogInfo($"[Patcher] owner edit-tail refresh for '{addressKey}': {ids.Count} adopted item(s).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] owner warning refresh: {ex.Message}"); }
        }

        /// <summary>Round-37b: drop the per-item byte-diff baseline for one building so the NEXT interior
        /// apply replaces EVERY live item object. The "unchanged → keep live object" optimization assumes
        /// replicas are never mutated locally; when an unrouted path corrupted one anyway (register-bag
        /// take), the keep-live branch preserved the corruption FOREVER while the owner's bytes stayed
        /// unchanged. Callers pair this with an interior re-request = forced full heal.</summary>
        public static void ForgetInteriorBaseline(string addressKey)
        {
            try { if (!string.IsNullOrEmpty(addressKey)) _lastItemSer.Remove(addressKey); } catch { }
        }

        /// <summary>Round-34: pre-fix sessions PERSISTED injected buyer-purchaser flags into saves (the owner
        /// adopts guest edits through the same interior apply that used to inject on any cargo item, and
        /// owners save their own buildings) — a flagged item then boots its controller in SHOP mode (owner
        /// couldn't grab a guest-placed box or manage their storage shelf). One pass at scene-ready heals
        /// the loaded world: purchaser stays only on retail DISPLAY types (PointOfSale | ShowcaseShelf —
        /// the game's stock-carrier set) on buildings the local player does NOT own. The apply-time
        /// whitelist prevents re-pollution; native saves never carry enabled purchaser flags (owners
        /// manage, not buy), so everything cleared here is ours.</summary>
        public static void SweepPurchaserPollution(string reason)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return;
                int healed = 0;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    var items = reg?.itemInstances;
                    if (items == null) continue;
                    foreach (var kv in items)
                    {
                        var ii = kv.Value;
                        try
                        {
                            if (ii?.playerItemPurchaserSettings == null || !ii.playerItemPurchaserSettings.enabled) continue;
                            bool display = ii.ItemCached != null
                                && (ii.ItemCached.type & (BigAmbitions.Items.ItemType.PointOfSale | BigAmbitions.Items.ItemType.ShowcaseShelf)) != 0;
                            if (!display || reg.RentedByPlayer) { ii.playerItemPurchaserSettings.enabled = false; healed++; }
                        }
                        catch { }
                    }
                }
                if (healed > 0) Plugin.Logger.LogInfo($"[Patcher] purchaser-pollution sweep ({reason}): healed {healed} item(s).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] purchaser sweep: {ex.Message}"); }
        }

        // Deferred re-staff: wait a couple frames so freshly-spawned stations' Start has initialised them
        // (item data, employeeType) before staffing.  Staffing the same frame the re-load spawns them NREs
        // inside the game's AssignEmployee (Braids and Blowouts: all 7 stations failed, 2026-06-23).
        private static System.Collections.IEnumerator ReStaffAfterInit(BuildingManager bm, string addressKey)
        {
            yield return null;
            yield return null;
            ReStaffStationsNow(bm, addressKey);
        }

        // Staff every unstaffed customer-serve station in the building individually (robust — one bad
        // station can't abort the rest), reusing the game's public AssignAiToEmployeeStation.  Idempotent
        // (skips already-staffed); self-skips player shops are handled upstream.  Logs the real cause + stack
        // per failure so a genuine (non-timing) NRE is still localizable.
        private static void ReStaffStationsNow(BuildingManager bm, string addressKey)
        {
            try
            {
                if (bm == null || bm.buildingRegistration == null) return;
                if (GameStateReader.AddressKey(bm.buildingRegistration) != addressKey) return;   // player left this building → skip
                // Round-33: NEVER AI-staff a SESSION-PLAYER business. This pass exists for AI businesses whose
                // layout-suppressed stations spawn late; AssignAiToEmployeeStation has NO player-shop self-skip
                // (that lives in SetupAiEmployeeStations, which this per-station loop bypasses) — so a helper
                // placing a register forwarded the interior and the apply spawned a ghost AI cashier in the
                // player shop on BOTH machines (user + logs 2026-07-04: "AI-staffed 1 station(s)").
                try { if (bm.buildingRegistration.RentedByPlayer || IsAnyPlayerBusiness(bm.buildingRegistration)) return; } catch { }
                var stations = new System.Collections.Generic.List<EmployeeStationController>(
                    bm.indoorItemContainer.GetComponentsInChildren<EmployeeStationController>(true));
                if (bm.currentLayout != null)
                    stations.AddRange(bm.currentLayout.GetComponentsInChildren<EmployeeStationController>(true));
                int staffed = 0, skipped = 0, failed = 0;
                foreach (var st in stations)
                {
                    try
                    {
                        if (st == null || st.employee != null) { skipped++; continue; }                 // already staffed
                        if (st.parentItemController is BusinessEmployeeController) { skipped++; continue; } // game routes these via the worker roster
                        if (st.GetType().Name == "ComputerController") { skipped++; continue; }           // IKA-style, not customer-serve
                        if (st.playerItemPurchaserSettings != null && st.playerItemPurchaserSettings.enabled) { skipped++; continue; } // purchaser shelf, not a serve station
                        bm.AssignAiToEmployeeStation(st);
                        staffed++;
                    }
                    catch (Exception sx)
                    {
                        failed++;
                        Plugin.Logger.LogWarning($"[Patcher] AI re-staff of {st?.GetType().Name} failed: {sx.Message}");
                    }
                }
                if (staffed > 0 || failed > 0)
                    Plugin.Logger.LogInfo($"[Patcher] AI-staffed {staffed} station(s) for '{addressKey}'" + (failed > 0 ? $" ({failed} failed)" : "") + ".");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] re-staff loop for '{addressKey}': {ex.Message}"); }
        }

        /// <summary>
        /// Reconciles ItemController GameObjects in the active building with
        /// reg.itemInstances — PER-ITEM: destroy controllers for changed/removed
        /// ids, spawn for changed ids and ids with no live controller (self-heal),
        /// leave everything else alone so shelf stock visuals keep their shuffle.
        /// changedIds/removedIds == null means no diff info → full rebuild
        /// (first apply / legacy callers).
        ///
        /// IMPORTANT: skipped when reg.itemInstances is empty.  AI businesses
        /// (CoffeeShop, FastFoodRestaurant, etc.) spawn their items from the
        /// `BusinessLayoutSet` template at LoadBuilding time — those template-
        /// spawned items are NOT in reg.itemInstances.  If we wipe them and
        /// replace with an empty dict, the building visibly goes empty.  When
        /// host's snapshot reports zero items we trust the layout-driven
        /// spawn and don't touch the scene.
        /// </summary>
        private static void RefreshItemsForActiveBuilding(BuildingManager bm, BuildingRegistration reg,
            HashSet<string>? changedIds = null, HashSet<string>? removedIds = null,
            HashSet<string>? movedIds = null)
        {
            try
            {
                // Skip refresh entirely when host has no per-instance items —
                // see XML doc above for why.
                int dictCount = reg.itemInstances?.Count ?? 0;
                if (dictCount == 0)
                {
                    Plugin.Logger.LogInfo($"[Patcher] Items refresh skipped (empty itemInstances; layout-spawned items left intact).");
                    return;
                }
                bool fullRebuild = changedIds == null;

                // Round-37i (dead shelf hover, probe-chain-proven): our destroy+respawn interrupting a LIVE
                // placement skips PlacementSystem.StopPlacingItem (:622-647) — the ONLY place that pairs the
                // parent item's Outline() with RemoveOutline(). The stranded parent (e.g. the shelf a box was
                // being placed onto) keeps disableHighlightInteraction=TRUE: hover check never runs again,
                // right-click (visible-gated) still works — the user's exact asymmetry. Complete the native
                // lifecycle BEFORE destroying, only when the refresh actually touches the placed item or its
                // outlined parent (or on a full rebuild, which touches everything).
                try
                {
                    if (BigAmbitions.PlacementSystem.PlacementSystem.IsInPlacementMode)
                    {
                        string placedId = "", parentId = "";
                        try { placedId = BigAmbitions.PlacementSystem.PlacementSystem.CurrentPlaceableItemBeingPlaced?.GetItemInstance()?.id?.ToString() ?? ""; } catch { }
                        try { parentId = BigAmbitions.PlacementSystem.PlacementSystem.lastParentItem?.GetItemInstance()?.id?.ToString() ?? ""; } catch { }
                        bool touched = fullRebuild
                            || (!string.IsNullOrEmpty(placedId) && (changedIds!.Contains(placedId) || (removedIds?.Contains(placedId) ?? false)))
                            || (!string.IsNullOrEmpty(parentId) && (changedIds!.Contains(parentId) || (removedIds?.Contains(parentId) ?? false)));
                        if (touched)
                        {
                            Plugin.Logger.LogWarning($"[Patcher] interior refresh intersects a LIVE placement (placed={placedId} parent={parentId}) — running native StopPlacingItem so parent outlines/flags don't strand (round-37i).");
                            BigAmbitions.PlacementSystem.PlacementSystem.StopPlacingItem();
                        }
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] placement-intersect guard: {ex.Message}"); }

                // Live controllers of THIS building, by instance id.
                var liveById = new Dictionary<string, ItemController>();
                int destroyed = 0;
                try
                {
                    var existing = UnityEngine.Object.FindObjectsOfType(typeof(ItemController));
                    if (existing != null)
                    {
                        for (int i = 0; i < existing.Length; i++)
                        {
                            var ic = existing[i] as ItemController;
                            if (ic == null) continue;
                            // Be conservative: only touch ItemControllers whose registration
                            // matches the current building (avoid wiping inventory items etc.).
                            try
                            {
                                var icReg = ItemHelper.GetBuildingRegistration(ic.ItemInstance);
                                if (icReg != reg) continue;
                            }
                            catch { continue; }
                            string id = "";
                            try { id = ic.ItemInstance?.id ?? ""; } catch { }
                            bool kill = fullRebuild
                                        || string.IsNullOrEmpty(id)
                                        || (changedIds != null && changedIds.Contains(id))
                                        || (removedIds != null && removedIds.Contains(id));
                            if (kill)
                            {
                                UnityEngine.Object.Destroy(ic.gameObject);
                                destroyed++;
                            }
                            else if (!string.IsNullOrEmpty(id) && !liveById.ContainsKey(id))
                            {
                                liveById[id] = ic;
                            }
                        }
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] item destroy pass: {ex.Message}"); }

                // Spawn: changed ids + any id with no surviving controller.
                int spawned = 0;
                int failed = 0;
                try
                {
                    if (reg.itemInstances != null)
                    {
                        foreach (var kv in reg.itemInstances)
                        {
                            var ii = kv.Value;
                            if (ii == null) continue;
                            if (!fullRebuild && liveById.ContainsKey(kv.Key)) continue;   // untouched
                            try { bm.InstantiateSingleInstance(ii, false); spawned++; }
                            catch (Exception ex) { failed++; if (failed <= 3) Plugin.Logger.LogWarning($"[Patcher] InstantiateSingleInstance id={ii.id}: {ex.Message}"); }
                        }
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] item spawn pass: {ex.Message}"); }

                // MOVE pass (round-22): transform-only deltas keep their live GameObject — re-bind it to the
                // fresh instance and move it to the owner-authoritative pose. No destroy/respawn → no zombie
                // window for the guest's next click on a just-placed item.
                int moved = 0;
                try
                {
                    if (movedIds != null && reg.itemInstances != null)
                        foreach (var id in movedIds)
                        {
                            if (!liveById.TryGetValue(id, out var ic) || ic == null) continue;   // no survivor → spawn pass covered it
                            if (!reg.itemInstances.TryGetValue(id, out var ni) || ni == null) continue;
                            try
                            {
                                ic.ItemInstance = ni;
                                ic.transform.position = ni.position;
                                ic.transform.rotation = ni.Rotation;
                                moved++;
                            }
                            catch { }
                        }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] item move pass: {ex.Message}"); }

                Plugin.Logger.LogInfo($"[Patcher] Items refresh: destroyed={destroyed} spawned={spawned} moved={moved} kept={liveById.Count} failed={failed} (dict size {dictCount}{(fullRebuild ? ", FULL rebuild" : "")}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] RefreshItemsForActiveBuilding: {ex.Message}"); }
        }

        /// <summary>Install a session player's REAL per-day series as the
        /// RivalState behind their synthetic row: SelectedRivalUI plots
        /// RivalState.weeklyIncomeHistory / numberOfBusinessesHistory, and
        /// FillRivalState only backfills MISSING days (with random noise — the
        /// game faking AI graphs) — pre-installed truth plots verbatim.
        /// Runtime-only: StripSyntheticRivalStates removes these at the save
        /// boundary.</summary>
        public static void InstallPlayerRivalStateHistory(string id)
        {
            try
            {
                if (string.IsNullOrEmpty(id)) return;
                if (!ClientRivalStats.TryGetValue(id, out var stat)) return;
                bool hasInc = stat.IncomeHistory   != null && stat.IncomeHistory.Count   > 0;
                bool hasBiz = stat.BizCountHistory != null && stat.BizCountHistory.Count > 0;
                if (!hasInc && !hasBiz) return;
                var states = SaveGameManager.Current?.rivalStates;
                if (states == null) return;

                BigAmbitions.Rivals.RivalState? st = null;
                foreach (var s in states)
                    if (s != null && s.rivalId == id) { st = s; break; }
                if (st == null)
                {
                    st = new BigAmbitions.Rivals.RivalState { rivalId = id };
                    states.Add(st);
                }
                if (hasInc)
                {
                    st.weeklyIncomeHistory = new List<Tuple<int, float>>();
                    foreach (var pnt in stat.IncomeHistory!)
                        if (pnt != null) st.weeklyIncomeHistory.Add(new Tuple<int, float>(pnt.Day, pnt.Value));
                }
                if (hasBiz)
                {
                    st.numberOfBusinessesHistory = new List<Tuple<int, int>>();
                    foreach (var pnt in stat.BizCountHistory!)
                        if (pnt != null) st.numberOfBusinessesHistory.Add(new Tuple<int, int>(pnt.Day, pnt.Value));
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] InstallPlayerRivalStateHistory '{id}': {ex.Message}"); }
        }

        /// <summary>Remove RivalState entries the rivals UI auto-created for
        /// SESSION-PLAYER ids (SelectedRivalUI.FillRivalState fills missing
        /// histories for anything it displays — synthetic player rows included).
        /// Players are not rivals; left in, these serialize into the .hsg and
        /// accumulate across sessions (0.11 UI map, save-contamination risk).
        /// Called at the save boundary like StripGhostVehicles.</summary>
        public static int StripSyntheticRivalStates(string when)
        {
            int removed = 0;
            try
            {
                var list = SaveGameManager.Current?.rivalStates;
                if (list == null) return 0;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    string rid = list[i]?.rivalId ?? "";
                    if (string.IsNullOrEmpty(rid)) continue;
                    bool player = rid == MPConfig.PlayerId || ClientPlayerRoster.ContainsKey(rid);
                    try { if (!player) player = MPRestSync.AllPlayers().Contains(rid); } catch { }
                    if (!player) continue;
                    list.RemoveAt(i);
                    removed++;
                }
                if (removed > 0)
                    Plugin.Logger.LogInfo($"[Patcher] stripped {removed} synthetic player RivalState(s) from save data ({when}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] rivalState strip: {ex.Message}"); }
            return removed;
        }

        /// <summary>Remove leaked ghost vehicles from the save data.  Ghost ids
        /// are 'BAMP_' + original (the test rig's own REAL vehicles are
        /// 'BAMP_TESTRIG_*'; ghost-of-ghost is 'BAMP_BAMP…').  Ghosts enter
        /// gi.VehicleInstances via CreateAndSpawnVehicle registration and
        /// snowball one duplicate per save/load cycle (run-17 evidence: extra
        /// carts/flatbeds frozen at stale cargo states).  Called at every save
        /// (PerformLocalSave) and at world-ready.</summary>
        public static int StripGhostVehicles(string when)
        {
            int removed = 0;
            try
            {
                var gi = SaveGameManager.Current;
                var list = gi?.VehicleInstances;
                if (list == null) return 0;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    string id = list[i]?.id ?? "";
                    bool ghost = id.Contains("BAMP_BAMP")
                                 || (id.StartsWith("BAMP_") && !id.StartsWith("BAMP_TESTRIG"));
                    if (!ghost) continue;
                    list.RemoveAt(i);
                    removed++;
                    Plugin.Logger.LogInfo($"[Vehicle] ghost stripped from save data ({when}): '{id}'.");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Vehicle] ghost strip: {ex.Message}"); }
            return removed;
        }

        /// <summary>
        /// Slice 2 (2026-06-12): a cross-player sale consumes REAL stock.  Runs
        /// on the HOST (interior authority) on the main thread: walks the shop's
        /// item instances and decrements the sold amounts; empty cargo entries
        /// are removed.  The interior diff hash covers cargo Amount, so the
        /// change re-broadcasts to every machine automatically.  NOTE: the
        /// owner's local shelf VISUAL (box models) may lag until the next
        /// interior refresh — data and sync are correct immediately.
        /// </summary>
        public static string ApplySaleStockDecrement(string addressKey, System.Collections.Generic.List<SaleItem>? items, string buyerId)
        {
            var shortfall = new System.Text.StringBuilder();
            try
            {
                if (items == null || items.Count == 0 || string.IsNullOrEmpty(addressKey)) return "";
                var reg = FindRegistration(addressKey);
                if (reg?.itemInstances == null)
                {
                    Plugin.Logger.LogWarning($"[Stock] no registration/items for '{addressKey}' — sale not decremented.");
                    return "";
                }
                foreach (var s in items)
                {
                    if (s == null || s.Amount <= 0) continue;
                    int remaining = s.Amount;
                    foreach (var kv in reg.itemInstances)
                    {
                        var cargo = kv.Value?.cargoInstances;
                        if (cargo == null) continue;
                        for (int i = cargo.Count - 1; i >= 0 && remaining > 0; i--)
                        {
                            var c = cargo[i];
                            if (c == null || c.itemName != s.ItemName) continue;
                            int dec = System.Math.Min(c.amount, remaining);
                            c.amount -= dec;
                            remaining -= dec;
                            if (c.amount <= 0) cargo.RemoveAt(i);
                        }
                        if (remaining <= 0) break;
                    }
                    int sold = s.Amount - remaining;
                    Plugin.Logger.LogInfo(
                        $"[Stock] '{addressKey}': -{sold} {s.ItemName} (sold to {buyerId})" +
                        (remaining > 0 ? $" — SHORT by {remaining} (stock didn't cover the sale)." : "."));
                    if (remaining > 0)
                        shortfall.Append($"{s.ItemName} x{remaining}, ");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Stock] decrement '{addressKey}': {ex.Message}"); }
            return shortfall.ToString().TrimEnd(' ', ',');
        }

        /// <summary>
        /// Constructs a new IL2CPP ItemInstance from a network-side DTO and
        /// populates all the fields we serialize.  Cargo + stacked + colors +
        /// purchaser-settings are rebuilt as fresh IL2CPP objects.
        /// </summary>
        // ── Move-vs-change identity (round-22) ────────────────────────────────
        // What an item IS, minus where it sits: two sigs equal ⇒ the delta is transform/location-only ⇒ the
        // live GameObject is moved in place instead of destroy+respawned (the new instance is adopted into
        // reg and the controller in BOTH paths, so excluding volatile metadata here only trades respawns for
        // moves — never loses data). Any mismatch (cargo, state, attachments…) keeps the respawn path.
        private static string IdentitySig(ItemInstanceInfo i)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.Append(i.ItemName ?? "").Append('|').Append(i.ParentId ?? "").Append('|')
              .Append(i.LinkedItemName ?? "").Append('|').Append(i.IsSecured).Append('|')
              .Append(i.StateIndex).Append('|').Append(i.Alias ?? "").Append('|')
              .Append(i.CustomValue ?? "").Append('|').Append(i.WorldSpaceTextValue ?? "").Append('|')
              .Append(i.PriceOnPurchase.ToString(inv)).Append('#');
            if (i.CargoInstances != null)
                foreach (var c in i.CargoInstances)
                    if (c != null) sb.Append(c.ItemName ?? "").Append(':').Append(c.Amount).Append(':')
                                     .Append(c.Paid).Append(':').Append(c.PricePerUnit.ToString(inv)).Append(':')
                                     .Append(c.CustomColors?.Count ?? 0).Append(':')
                                     .Append(c.NestedCargoInstances?.Count ?? 0).Append(';');
            sb.Append('#');
            if (i.StackedItems != null)
                foreach (var s in i.StackedItems)
                    if (s != null) sb.Append(s.ChildId ?? "").Append(':').Append(s.ChildItemName ?? "").Append(':')
                                     .Append(s.AttachmentIndex).Append(';');
            return sb.ToString();
        }

        private static string IdentitySig(BigAmbitions.Items.ItemInstance ii)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.Append(ii.itemName ?? "").Append('|').Append(ii.parentId?.ToString() ?? "").Append('|')
              .Append(ii.linkedItemName ?? "").Append('|').Append(ii.isSecured).Append('|')
              .Append(ii.stateIndex).Append('|').Append(ii.alias?.ToString() ?? "").Append('|')
              .Append(ii.customValue?.ToString() ?? "").Append('|').Append(ii.worldSpaceTextValue?.ToString() ?? "").Append('|')
              .Append(ii.priceOnPurchase.ToString(inv)).Append('#');
            if (ii.cargoInstances != null)
                foreach (var c in ii.cargoInstances)
                    if (c != null) sb.Append(c.itemName ?? "").Append(':').Append(c.amount).Append(':')
                                     .Append(c.paid).Append(':').Append(c.pricePerUnit.ToString(inv)).Append(':')
                                     .Append(c.customColors?.Count ?? 0).Append(':')
                                     .Append(c.nestedCargoInstances?.Count ?? 0).Append(';');
            sb.Append('#');
            if (ii.stackedItems != null)
                foreach (var s in ii.stackedItems)
                    if (s != null) sb.Append(s.childId?.ToString() ?? "").Append(':').Append(s.childItemName ?? "").Append(':')
                                     .Append(s.attachmentIndex).Append(';');
            return sb.ToString();
        }

        private static BigAmbitions.Items.ItemInstance? DeserializeItemInstance(ItemInstanceInfo i)
        {
            try
            {
                var ii = new BigAmbitions.Items.ItemInstance(i.ItemName)
                {
                    id                  = i.Id,
                    itemName            = i.ItemName,
                    position            = new SerializableVector3 { x = i.Px, y = i.Py, z = i.Pz },
                    rotation            = new SerializableQuaternion { x = i.Qx, y = i.Qy, z = i.Qz, w = i.Qw },
                    yRotation           = i.YRotation,
                    parentId            = i.ParentId,
                    streetName          = i.StreetName,
                    streetNumber        = i.StreetNumber,
                    linkedItemName      = i.LinkedItemName,
                    isSecured           = i.IsSecured,
                    worldSpaceTextValue = i.WorldSpaceTextValue,
                    stateIndex          = i.StateIndex,
                    alias               = i.Alias,
                    customValue         = i.CustomValue,
                    priceOnPurchase     = i.PriceOnPurchase,
                };

                // Stacked items
                if (ii.stackedItems != null && i.StackedItems != null)
                {
                    ii.stackedItems.Clear();
                    foreach (var s in i.StackedItems)
                    {
                        ii.stackedItems.Add(new AttachableChild
                        {
                            childId         = s.ChildId,
                            childItemName   = s.ChildItemName,
                            attachmentIndex = s.AttachmentIndex,
                        });
                    }
                }

                // Cargo
                if (ii.cargoInstances != null && i.CargoInstances != null)
                {
                    ii.cargoInstances.Clear();
                    foreach (var c in i.CargoInstances)
                    {
                        // Display correctness (user, 2026-06-12): the CHARGE comes
                        // from the store table; the replica cargo must show the
                        // same number (basket said $18 while the charge was $22).
                        // Stamp from the table when it has an entry.
                        float price = c.PricePerUnit;
                        try
                        {
                            float t = MPRegisterSync.GetShopPriceAt(
                                $"{i.StreetNumber} {i.StreetName}", c.ItemName);
                            if (t >= 0f) price = t;
                        }
                        catch { }
                        var ci = new BigAmbitions.Items.CargoInstance(
                            c.ItemName,
                            c.Amount,
                            price,
                            c.Paid);
                        if (ci.customColors != null && c.CustomColors != null)
                        {
                            ci.customColors.Clear();
                            foreach (var cc in c.CustomColors)
                                ci.customColors.Add(new BigAmbitions.Items.CustomColor { channel = (BigAmbitions.Items.CustomColorChannel)cc.Channel, color = new SerializableColor(cc.ColorPacked) });
                        }
                        if (ci.nestedCargoInstances != null && c.NestedCargoInstances != null)
                        {
                            ci.nestedCargoInstances.Clear();
                            foreach (var n in c.NestedCargoInstances)
                            {
                                var nci = new BigAmbitions.Items.NestedCargoInstance
                                {
                                    itemName     = n.ItemName,
                                    amount       = n.Amount,
                                    pricePerUnit = n.PricePerUnit,
                                };
                                if (nci.customColors != null && n.CustomColors != null)
                                {
                                    nci.customColors.Clear();
                                    foreach (var cc in n.CustomColors)
                                        nci.customColors.Add(new BigAmbitions.Items.CustomColor { channel = (BigAmbitions.Items.CustomColorChannel)cc.Channel, color = new SerializableColor(cc.ColorPacked) });
                                }
                                ci.nestedCargoInstances.Add(nci);
                            }
                        }
                        ii.cargoInstances.Add(ci);
                    }
                }

                // Dirt spots
                if (ii.dirtSpotsThatAffects != null && i.DirtSpotsThatAffects != null)
                {
                    ii.dirtSpotsThatAffects.Clear();
                    foreach (var d in i.DirtSpotsThatAffects) ii.dirtSpotsThatAffects.Add(d);
                }

                // Custom positions
                if (ii.customPositions != null && i.CustomPositions != null)
                {
                    ii.customPositions.Clear();
                    foreach (var p in i.CustomPositions)
                        ii.customPositions.Add(new SerializableVector3 { x = p.X, y = p.Y, z = p.Z });
                }

                // Top-level custom colors
                if (ii.customColors != null && i.CustomColors != null)
                {
                    ii.customColors.Clear();
                    foreach (var cc in i.CustomColors)
                        ii.customColors.Add(new BigAmbitions.Items.CustomColor { channel = (BigAmbitions.Items.CustomColorChannel)cc.Channel, color = new SerializableColor(cc.ColorPacked) });
                }

                // Purchaser settings
                if (i.PurchaserSettings != null)
                {
                    ii.playerItemPurchaserSettings = new BigAmbitions.Items.PlayerItemPurchaserSettings
                    {
                        name         = i.PurchaserSettings.Name,
                        enabled      = i.PurchaserSettings.Enabled,
                        itemName     = i.PurchaserSettings.ItemName,
                        itemQuantity = i.PurchaserSettings.ItemQuantity,
                    };
                }

                return ii;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Patcher] DeserializeItemInstance id={i.Id}: {ex.Message}");
                return null;
            }
        }

        // ── Business sync (Phase 1) ───────────────────────────────────────────

        public static void ApplyBusinessSnapshot(BusinessSnapshotPayload payload)
        {
            MPLoadProfiler.Mark($"CLIENT ApplyBusinessSnapshot QUEUED ({payload.Businesses.Count} buildings)");
            RunOnMainThread(() =>
            {
                long _profT0 = MPLoadProfiler.NowMs;
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
                MPLoadProfiler.Span($"CLIENT ApplyBusinessSnapshot APPLIED ({applied}/{payload.Businesses.Count})", _profT0);

                // Frozen-until-synced: the business table is the bulk of the world
                // state.  Mark it applied so the overlay-freeze gate knows the world
                // sync is done.  We only tell the host we're world-ready once we are
                // BOTH synced (here) AND our loading overlay has cleared (= frozen).
                // If we're already frozen, send now; otherwise the overlay gate sends
                // it the moment our overlay clears (covers the apply-before-freeze
                // ordering).
                MPClient.WorldSyncApplied = true;
                if (MPClient.IsConnected && TimeSync.IsStartupHeld)
                    MPClient.SendWorldReady();
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
                var all = UnityEngine.Object.FindObjectsOfType(typeof(CityMapFilters));
                if (all == null || all.Length == 0) return;
                for (int i = 0; i < all.Length; i++)
                {
                    var f = all[i] as CityMapFilters;
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

        /// <summary>HOST / MAIN THREAD: remove a building from the authoritative for-sale
        /// market after a player bought it.  BusinessSync.CheckBuildingsForSaleChange then
        /// broadcasts the shorter list, so the building leaves every player's market.</summary>
        public static void HostRemoveFromForSale(string addressKey)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.buildingsForSale == null) return;
                int removed = gi.buildingsForSale.RemoveAll(x => GameStateReader.AddressKey(x.address) == addressKey);
                if (removed > 0) Plugin.Logger.LogInfo($"[Patcher/Host] {addressKey} removed from for-sale market (bought).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher/Host] HostRemoveFromForSale: {ex.Message}"); }
        }

        /// <summary>MAIN THREAD: read the for-sale market entry for an address into a
        /// transferable BuildingForSaleInfo (the seller's chosen price/sqm/acceptRate),
        /// or null if the address isn't currently listed.</summary>
        public static BuildingForSaleInfo? GetForSaleInfo(string addressKey)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.buildingsForSale == null) return null;
                foreach (var bfs in gi.buildingsForSale)
                {
                    if (GameStateReader.AddressKey(bfs.address) != addressKey) continue;
                    return new BuildingForSaleInfo
                    {
                        AddressKey      = addressKey,
                        BuildingPrice   = bfs.buildingPrice,
                        SquareMeters    = bfs.squareMeters,
                        AcceptOfferRate = bfs.acceptOfferRate,
                    };
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] GetForSaleInfo: {ex.Message}"); }
            return null;
        }

        /// <summary>HOST / MAIN THREAD: add (or refresh) a building in the authoritative
        /// for-sale market from a client's listing, preserving the seller's price.  The
        /// for-sale poll then broadcasts it so every player sees the listing.</summary>
        public static void HostAddToForSale(BuildingForSaleInfo info)
        {
            try
            {
                if (info == null || string.IsNullOrEmpty(info.AddressKey)) return;
                var gi = SaveGameManager.Current;
                if (gi?.buildingsForSale == null) return;
                var reg = FindRegistration(info.AddressKey);
                if (reg == null) { Plugin.Logger.LogWarning($"[Patcher/Host] list-for-sale: no reg for {info.AddressKey}."); return; }
                gi.buildingsForSale.RemoveAll(x => GameStateReader.AddressKey(x.address) == info.AddressKey);   // replace any prior listing
                gi.buildingsForSale.Add(new BuildingForSale
                {
                    address         = reg.Address,
                    buildingPrice   = info.BuildingPrice,
                    squareMeters    = info.SquareMeters,
                    acceptOfferRate = info.AcceptOfferRate,
                });
                Plugin.Logger.LogInfo($"[Patcher/Host] {info.AddressKey} listed for sale @ {info.BuildingPrice:F0}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher/Host] HostAddToForSale: {ex.Message}"); }
        }

        /// <summary>MAIN THREAD: the address keys of all currently-for-sale buildings the
        /// local player OWNS — the set SimulateCompetitorBuyingPlayerBuildings may sell
        /// this tick.  Captured before that method runs so the postfix can tell which
        /// actually sold.</summary>
        public static System.Collections.Generic.List<string> SnapshotOwnedForSale()
        {
            var result = new System.Collections.Generic.List<string>();
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.buildingsForSale == null) return result;
                foreach (var bfs in gi.buildingsForSale)
                {
                    string key = GameStateReader.AddressKey(bfs.address);
                    var reg = FindRegistration(key);
                    if (reg != null && reg.BuildingOwnedByPlayer) result.Add(key);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] SnapshotOwnedForSale: {ex.Message}"); }
            return result;
        }

        /// <summary>MAIN THREAD: of the previously-owned-for-sale addresses, those the
        /// local player no longer owns — i.e. the AI just bought them.</summary>
        public static System.Collections.Generic.List<string> DetectSold(System.Collections.Generic.List<string> before)
        {
            var sold = new System.Collections.Generic.List<string>();
            try
            {
                if (before == null) return sold;
                foreach (var key in before)
                {
                    var reg = FindRegistration(key);
                    if (reg == null || !reg.BuildingOwnedByPlayer) sold.Add(key);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] DetectSold: {ex.Message}"); }
            return sold;
        }

        /// <summary>MAIN THREAD: the host denied our optimistic buy (someone else already
        /// owns it) — undo it: drop the real-estate entry and refund what we paid.  The
        /// host's for-sale broadcast restores the building to our market.</summary>
        public static void RollbackBuy(string addressKey)
        {
            RunOnMainThread(() =>
            {
                try
                {
                    var gi = SaveGameManager.Current;
                    if (gi?.realEstate == null) return;
                    float refund = 0f;
                    foreach (var re in gi.realEstate)
                        if (GameStateReader.AddressKey(re.address) == addressKey) refund += re.purchasePrice;
                    int removed = gi.realEstate.RemoveAll(x => GameStateReader.AddressKey(x.address) == addressKey);
                    if (removed > 0)
                    {
                        if (refund > 0f)
                        {
                            try { gi.Money += refund; } catch { }
                            // Slice 4: the denied buy's CHARGE went through native ChangeMoney (forwarded
                            // to the shared ledger) — the refund must follow or the reconcile erases it.
                            MergerWallet.ForwardExternal(refund, "buy-rollback refund");
                        }
                        Plugin.Logger.LogInfo($"[Patcher] Rolled back buy of {addressKey} (refunded {refund:F0}).");
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] RollbackBuy: {ex.Message}"); }
            });
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
                    try { if (reg.businessTypeName != "ba:businesstype_empty")                    { withBiz++;      dirty = true; } } catch { }
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

                // Rejoin scoping (2026-07-09, RED ROC report review): the suppression question is only
                // meaningful on the FIRST world load of this app run — CityGenerator ran under
                // suppression, so a dirty baseline there is a genuine escape. Any LATER load (rejoin
                // after host loss, reload) re-enters an already-populated world where prior state is
                // EXPECTED; the report showed 6 false alarms in one evening (~1 per rejoin), each
                // ~1340 regs = the whole previously-loaded city. The snapshot overwrites either way.
                if (dirtyTotal > 0 && !_baselineCheckedBefore)
                    Plugin.Logger.LogWarning($"[Patcher] Baseline NOT clean — {dirtyTotal} reg(s) had state before host's snapshot wrote them.  CityGenerator suppression may be incomplete.");
                else if (dirtyTotal > 0)
                    Plugin.Logger.LogInfo($"[Patcher] Baseline has prior state ({dirtyTotal} reg(s)) — rejoin/reload into a previously loaded world; expected, snapshot overwrites.");
                else
                    Plugin.Logger.LogInfo("[Patcher] Baseline is 100% clean ✓");
                _baselineCheckedBefore = true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] VerifyCleanBaseline: {ex.Message}"); }
        }

        private static bool _baselineCheckedBefore;

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
                    string clientBiz; try { clientBiz = reg.businessTypeName ?? ""; } catch { clientBiz = "?"; }
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
            RunOnMainThread(() =>
            {
                if (ApplyBusinessInfoLocal(info))
                    Plugin.Logger.LogInfo($"[Patcher] Business change applied: {info.AddressKey} = '{info.BusinessName}'");
            });
        }

        /// <summary>HOST: apply a business a CLIENT built/changed (in a building it owns)
        /// to the host's own world, so the host sees it.  ApplyBusinessInfoLocal is
        /// identity-relative, so OwnerPlayerId != host resolves to that player as the
        /// building's owner (buildingOwnerRivalId).  The host's BusinessSync.Tick then
        /// detects the changed reg and relays it to the other clients.  MAIN THREAD.</summary>
        public static void ApplyClientBusinessChange(BusinessInfo info)
        {
            if (info == null) return;
            RunOnMainThread(() =>
            {
                var reg = FindRegistration(info.AddressKey);
                bool signObjNull = reg == null || reg.signAppearanceSettings == null;
                if (ApplyBusinessInfoLocal(info))
                    Plugin.Logger.LogInfo($"[Patcher/Host] Client business applied: {info.AddressKey} = '{info.BusinessName}' sign=type{info.SignType} (owner '{info.OwnerPlayerId}', signObjNull={signObjNull}).");
                else
                    Plugin.Logger.LogWarning($"[Patcher/Host] Client business NOT applied (no reg?): {info.AddressKey}");
            });
        }

        private static bool ApplyBusinessInfoLocal(BusinessInfo info)
        {
            try
            {
                var reg = FindRegistration(info.AddressKey);
                if (reg == null) return false;

                // Is this the RECEIVER'S OWN business?  The owner is the authority for their own shop's
                // name / description / sign / logo / hours — the host holds only a (possibly stale or blank)
                // replica and must NOT overwrite those here.  RentedByPlayer is the reliable "mine" flag (the
                // game sets it for buildings THIS player rents); OwnerPlayerId==self is the host's positive
                // attribution.  Other players' shops + AI businesses are NOT "mine" → the host's relay applies
                // normally.  (NOTE: IsAnyPlayerBusiness is the WRONG test here — it's true for ANY player's
                // shop and false for your OWN freshly-loaded shop, because it keys on the empty businessOwnerRivalId.)
                bool receiverOwnsThis = false;
                try
                {
                    receiverOwnsThis = IsReceiversOwnBusiness(reg)
                                     || (!string.IsNullOrEmpty(info.OwnerPlayerId) && info.OwnerPlayerId == MPConfig.PlayerId)
                                     // Rent-vs-deed split (2026-07-07): OwnerPlayerId is tenancy-only now;
                                     // a building I BOUGHT (deed) is mine even with no business in it.
                                     || (!string.IsNullOrEmpty(info.DeedOwnerPlayerId) && info.DeedOwnerPlayerId == MPConfig.PlayerId);
                }
                catch { }

                // Owner-authoritative open/closed truth: store verbatim (keyed by address) so the host can
                // relay a client's value through its own Tick (ReadInfo reads it back) and the IsBusinessOpen
                // patch can use it for shops we don't run. 0 = unknown → leave any prior known value intact.
                try { if (info.OwnerOpenState != 0) BusinessSync.OwnerOpenByAddress[info.AddressKey] = info.OwnerOpenState; } catch { }

                // Tier A (name/type/closed) + rental-marketplace state.  Owner-authored / host-AI-economy view —
                // skip entirely for the receiver's OWN shop so a stale/blank host replica can't overwrite it.
                if (!receiverOwnsThis)
                {
                    // Tier A — name, type, closed.
                    reg.BusinessName        = info.BusinessName;
                    reg.businessTypeName    = info.BusinessTypeName;
                    reg.temporarilyClosed   = info.TemporarilyClosed;

                    // Rental marketplace state (Phase 1b).  Overrides client's
                    // local AI-economy decisions.  If host considers this building
                    // vacant + rentable but client's AI has filled it, these
                    // writes flip the client's view back to "for rent."
                    reg.AvailableForRent    = info.AvailableForRent;
                    reg.RentPerDay          = info.RentPerDay;
                    reg.lastDeposit         = info.LastDeposit;
                }

                // AI-business retail prices (host-authoritative; the client's
                // rival sim is suppressed so its tables stay empty otherwise).
                // Player shops never carry Prices here — MPPriceSync owns them.
                try
                {
                    if (info.Prices != null && info.Prices.Count > 0 && reg.retailPrices != null
                        && !IsAnyPlayerBusiness(reg))
                    {
                        reg.retailPrices.Clear();
                        foreach (var rp in info.Prices)
                            reg.retailPrices.Add(new RetailPrice { itemName = rp.ItemName, price = rp.Price });
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] AI prices apply '{info.AddressKey}': {ex.Message}"); }

                // Tier B — description + sign + logo (owner-authored).  Skip for the receiver's OWN shop.
                if (!receiverOwnsThis)
                {
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
                }

                // Operating hours (Phase 1c) — without these, suppression on
                // client leaves scheduleDays empty and every business looks
                // closed.  Replace verbatim from host.
                try
                {
                    // The owner's OWN shop hours live in their save — never overwrite them from the host's
                    // (possibly stale/blank) replica.  AI + other players' shops take the host's relayed hours.
                    // Slice 5: while OUR schedule edit to a flipped partner shop is in flight, hold the
                    // owner heartbeat for that shop — a pre-edit snapshot must not clobber the member's edit.
                    if (reg.scheduleDays != null && info.Schedule != null && info.Schedule.Count > 0
                        && !receiverOwnsThis
                        && !MergerEmployeeSync.HoldScheduleApply(info.AddressKey))
                    {
                        reg.scheduleDays.Clear();
                        foreach (var d in info.Schedule)
                        {
                            var sd = new ScheduleDay
                            {
                                day    = (BigAmbitions.DayNightCycle.DayOfWeekOrdered)d.Day,
                                isOpen = d.IsOpen,
                            };
                            if (sd.openingHourSlots != null && d.OpeningHourSlots != null)
                            {
                                foreach (var slot in d.OpeningHourSlots)
                                    sd.openingHourSlots.Add(new OpeningHourSlot(slot.StartingHour, slot.EndingHour));
                            }
                            if (sd.workShifts != null && d.WorkShifts != null)
                            {
                                foreach (var shift in d.WorkShifts)
                                {
                                    if (shift == null) continue;
                                    sd.AddWorkShift(new WorkShift
                                    {
                                        employeeId     = shift.EmployeeId ?? "",
                                        itemInstanceId = shift.ItemInstanceId ?? "",
                                        startingHour   = shift.StartingHour,
                                        endingHour     = shift.EndingHour,
                                        type           = (WorkShiftType)shift.Type,
                                    });
                                }
                            }
                            reg.scheduleDays.Add(sd);
                        }
                        // Slice 5: the owner's truth is the write-back diff baseline (and an echo of our
                        // own edit clears the pending hold).
                        MergerEmployeeSync.NoteOwnerScheduleApplied(info.AddressKey, reg);
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] schedule apply for {info.AddressKey}: {ex.Message}"); }

                // ── Ownership (Phase 1d Wave 1) ─────────────────────────────
                // Sync the rival-id strings verbatim — for AI-owned buildings
                // this immediately fixes the "undefined" / wrong-rival display
                // because the same AI rival IDs exist on both sides.  For
                // PLAYER-owned buildings (host bought it), host's value here
                // will likely be either empty or a player-id string; we log
                // it so the test data tells us what to translate.
                // RentedByPlayer is TRANSLATED, not copied: the host's RentedByPlayer means the HOST rents it,
                // which is meaningless on the client.  The client's own tenancy is derived from OwnerPlayerId
                // (the host's positive per-player attribution) and otherwise PRESERVED (never blind-cleared).
                string priorBuildingOwner = reg.buildingOwnerRivalId?.ToString() ?? "";
                string priorBusinessOwner = reg.businessOwnerRivalId?.ToString() ?? "";
                bool   priorRented        = reg.RentedByPlayer;
                try
                {
                    // Default: mirror host's rival-id fields verbatim — EXCEPT a player id in the
                    // DEED field (buildingOwnerRivalId), which is contamination from the pre-split
                    // plumbing (renters were written there — the "rival page says he OWNS it" bug):
                    // never re-apply it; keep what we had (the AI landlord, or empty).
                    string newBuildingOwner = info.BuildingOwnerRivalId ?? "";
                    if (IsSessionPlayerId(newBuildingOwner) && newBuildingOwner != (info.DeedOwnerPlayerId ?? ""))
                        newBuildingOwner = priorBuildingOwner != null && !IsSessionPlayerId(priorBuildingOwner) ? priorBuildingOwner : "";
                    string newBusinessOwner = info.BusinessOwnerRivalId ?? "";
                    // Tenancy: PRESERVE what the client already had unless the host POSITIVELY attributes the
                    // building to a specific player.  The old default of `false` clobbered the client's own
                    // tenancy whenever the host's OwnerPlayerId was momentarily empty (ownership-sync gap at
                    // join / reclaim miss) — silently losing the building, persisted on the next autosave.
                    bool   newRented        = priorRented;

                    // Rent-vs-deed split (2026-07-07). OwnerPlayerId = TENANCY: the receiver being the
                    // tenant restores RentedByPlayer (the HQ-brick guard, 2026-06-19 — do NOT weaken);
                    // anyone else displays the tenant as the business RUNNER (businessOwnerRivalId) —
                    // NEVER as the building's deed owner. That deed write was the community bug: "friend
                    // rented a building, rival page shows he OWNS it" — and it also clobbered the AI
                    // landlord, which worldgen never re-assigns.
                    if (!string.IsNullOrEmpty(info.OwnerPlayerId))
                    {
                        if (info.OwnerPlayerId == MPConfig.PlayerId) newRented = true;
                        else                                         newBusinessOwner = info.OwnerPlayerId;
                        // newRented stays = priorRented for other players' buildings: whether the LOCAL
                        // player RUNS a business here is the client's OWN authority; a host delta must
                        // never take it away (the HQ brick, 2026-06-19).
                    }
                    // DeedOwnerPlayerId = the player who BOUGHT the building — the one attribution that
                    // belongs in buildingOwnerRivalId. My own deed: my save is the truth, keep it.
                    if (!string.IsNullOrEmpty(info.DeedOwnerPlayerId))
                    {
                        if (info.DeedOwnerPlayerId == MPConfig.PlayerId) newBuildingOwner = priorBuildingOwner ?? "";
                        else                                             newBuildingOwner = info.DeedOwnerPlayerId;
                    }
                    if (!string.IsNullOrEmpty(info.BusinessOwnerPlayerId)
                        && info.BusinessOwnerPlayerId != MPConfig.PlayerId)
                    {
                        newBusinessOwner = info.BusinessOwnerPlayerId;
                    }

                    reg.buildingOwnerRivalId = newBuildingOwner;
                    reg.businessOwnerRivalId = newBusinessOwner;
                    reg.RentedByPlayer       = newRented;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] ownership apply for {info.AddressKey}: {ex.Message}"); }

                // Diagnostic: log only when ownership actually CHANGES (host's reported state vs the
                // client's prior state). Logging every sync, including unchanged owned records, was
                // ~270 no-op repeats/session; a transition (rent/unrent, owner A→B, AI↔player) is the
                // part worth seeing.
                if (info.BuildingOwnerRivalId != priorBuildingOwner
                    || info.BusinessOwnerRivalId != priorBusinessOwner
                    || info.RentedByPlayer != priorRented)
                {
                    Plugin.Logger.LogInfo($"[Patcher] Ownership for {info.AddressKey}: host[bldg='{info.BuildingOwnerRivalId}' biz='{info.BusinessOwnerRivalId}' rented={info.RentedByPlayer}] client-was[bldg='{priorBuildingOwner}' biz='{priorBusinessOwner}' rented={priorRented}].");
                }

                // HQ DIAGNOSTIC (2026-06-19, release-safe): a client's own HQ can be bricked if a host delta
                // clears its RentedByPlayer (→ receiverOwnsThis false → schedule overwritten → "never open" +
                // can't schedule workers). Log every HQ apply so an affected report shows whether ownership +
                // schedule held. Fires only for HQ (rare); the fix above should keep rentedNow=rentedPrior and
                // receiverOwns=true for an owned HQ. Remove once confirmed fixed in the wild.
                try
                {
                    if (reg.businessTypeName == "ba:businesstype_headquarters"
                        || info.BusinessTypeName == "ba:businesstype_headquarters")
                    {
                        // THE BRICK TRIGGER (2026-06-25): the host delta carries an EMPTY OwnerPlayerId for an
                        // HQ this client RENTS. That is the exact upstream condition that bricked HQs before the
                        // 2026-06-19 guard (old default newRented=false → receiverOwns=false → schedule wiped).
                        // The guard now PRESERVES tenancy, so there may be NO visible brick — but this is the
                        // moment to capture. Log it loudly + distinctly so a bug report pins the exact apply
                        // (and the surrounding host deltas) even on a near-miss, regardless of the guard saving it.
                        if (priorRented && string.IsNullOrEmpty(info.OwnerPlayerId))
                            Plugin.Logger.LogWarning($"[HQDiag] *** BRICK TRIGGER *** host delta has EMPTY OwnerPlayerId for owned HQ '{info.AddressKey}' — guard preserved tenancy (rentedNow={reg.RentedByPlayer}). bizOwnerPlayer='{info.BusinessOwnerPlayerId}' bldgRival='{info.BuildingOwnerRivalId}' bizRival='{info.BusinessOwnerRivalId}' infoSched={(info.Schedule?.Count ?? -1)} regSched={(reg.scheduleDays?.Count ?? -1)} tempClosed={reg.temporarilyClosed}");

                        Plugin.Logger.LogWarning($"[HQDiag] apply '{info.AddressKey}' receiverOwns={receiverOwnsThis} rentedPrior={priorRented} rentedNow={reg.RentedByPlayer} ownerPlayer='{info.OwnerPlayerId}' bizOwnerPlayer='{info.BusinessOwnerPlayerId}' infoSched={(info.Schedule?.Count ?? -1)} regSched={(reg.scheduleDays?.Count ?? -1)} tempClosed={reg.temporarilyClosed}");
                    }
                }
                catch { }

                // Writes above only update the DATA model.  The visible sign /
                // logo / map marker are GameObjects that were initialised from
                // this registration when the building first loaded — they will
                // not auto-refresh.  Find the matching CityBuildingController
                // and call UpdateSign + UpdatePoi to repaint the visuals.
                //
                // REPAINT ONLY WHEN THE VISUALS ACTUALLY CHANGED.  Every repaint
                // wave makes the game's interactive-object system re-fire
                // OnIoEnter on buildings near the local player — visible as
                // street buildings "highlight flickering" whenever deltas apply
                // (HiDiag-confirmed 2026-06-09).  Post-load field noise sends
                // deltas whose visible fields didn't change at all; the data is
                // already written above, so skip the repaint for those entirely.
                string vsig = VisualSig(info);
                if (_lastVisualSig.TryGetValue(info.AddressKey, out var prevVs) && prevVs == vsig)
                    return true;
                _lastVisualSig[info.AddressKey] = vsig;

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
                        var signCtrls = cbc.GetComponentsInChildren(typeof(BuildingSignController), true);
                        if (signCtrls != null)
                        {
                            for (int i = 0; i < signCtrls.Length; i++)
                            {
                                var sc = signCtrls[i] as BuildingSignController;
                                if (sc == null) continue;
                                try { sc.ConfigureSign(reg); refreshed++; }
                                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] BuildingSignController.ConfigureSign: {ex.Message}"); }
                            }
                        }
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] BuildingSignController scan: {ex.Message}"); }

                    try
                    {
                        var logoCtrls = cbc.GetComponentsInChildren(typeof(BuildingLogoSignController), true);
                        if (logoCtrls != null)
                        {
                            for (int i = 0; i < logoCtrls.Length; i++)
                            {
                                var lc = logoCtrls[i] as BuildingLogoSignController;
                                if (lc == null) continue;
                                try { lc.UpdateSign(reg); refreshed++; }
                                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] BuildingLogoSignController.UpdateSign: {ex.Message}"); }
                            }
                        }
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] BuildingLogoSignController scan: {ex.Message}"); }

                    // Verbose log only for non-trivial businesses (matches the
                    // host-side filter so we get host/client side-by-side).
                    bool nonTrivial = !string.IsNullOrEmpty(info.BusinessName) || info.BusinessTypeName != "ba:businesstype_empty";
                    if (nonTrivial)
                    {
                        Plugin.Logger.LogInfo($"[Patcher] Refresh {info.AddressKey}: name='{info.BusinessName}' type={info.BusinessTypeName} signType={info.SignType} sign=0x{info.SignLightPacked:X8}/0x{info.LampPacked:X8} logoShape='{info.LogoShape}' logoFont={info.LogoFont} refreshed={refreshed}");
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
                    if (info.LogoFiles != null && info.LogoFiles.Count > 0 && !string.IsNullOrEmpty(info.BusinessName)
                        && !receiverOwnsThis)   // don't overwrite the owner's own logo image files with the host's replica
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

        // Last-repainted visual signature per building (see ApplyBusinessInfoLocal)
        // — a delta whose visible fields match what we last painted skips the
        // repaint (and its OnIoEnter highlight-flicker side effect) entirely.
        private static readonly System.Collections.Generic.Dictionary<string, string> _lastVisualSig = new();
        public static void ResetVisualSigCache() => _lastVisualSig.Clear();

        /// <summary>Signature over the fields that affect a building's EXTERIOR
        /// visuals (sign, logo, ownership/for-rent state).  Schedule, deposits,
        /// prices etc. are data-only — they don't require a repaint.</summary>
        private static string VisualSig(BusinessInfo info)
        {
            int logoBytes = 0, logoCount = 0;
            if (info.LogoFiles != null)
            {
                logoCount = info.LogoFiles.Count;
                for (int i = 0; i < info.LogoFiles.Count; i++)
                    logoBytes += info.LogoFiles[i]?.Base64?.Length ?? 0;
            }
            return $"{info.BusinessName}|{info.BusinessTypeName}|{info.SignType}|{info.SignLightPacked}|{info.LampPacked}"
                 + $"|{info.LogoShape}|{info.LogoFont}|{info.LogoColorPacked}|{info.FontColorPacked}|{info.BackgroundColorPacked}"
                 + $"|{logoCount}:{logoBytes}|{info.AvailableForRent}|{info.TemporarilyClosed}"
                 + $"|{info.BuildingOwnerRivalId}|{info.BusinessOwnerRivalId}|{info.RentedByPlayer}";
        }

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
                            var logoCtrls = cbc.GetComponentsInChildren(typeof(BuildingLogoSignController), true);
                            if (logoCtrls != null)
                                for (int j = 0; j < logoCtrls.Length; j++)
                                {
                                    var lc = logoCtrls[j] as BuildingLogoSignController;
                                    if (lc != null) { try { lc.UpdateSign(reg); } catch { } }
                                }
                            var signCtrls = cbc.GetComponentsInChildren(typeof(BuildingSignController), true);
                            if (signCtrls != null)
                                for (int j = 0; j < signCtrls.Length; j++)
                                {
                                    var sc = signCtrls[j] as BuildingSignController;
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
                var all = UnityEngine.Object.FindObjectsOfType(typeof(CityBuildingController));
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        var cbc = all[i] as CityBuildingController;
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
            RunOnMainThread(() =>
            {
                try
                {
                    var dtos = Newtonsoft.Json.JsonConvert.DeserializeObject<List<MarketEntryDto>>(marketJson);
                    if (dtos == null) return;

                    var gi = SaveGameManager.Current;
                    if (gi == null) return;

                    foreach (var dto in dtos)
                    {
                        // Find matching entry; update import price + per-neighborhood demand (host-authoritative).
                        foreach (var entry in gi.productMarketEntries)
                        {
                            if (entry.itemName == dto.ItemName)
                            {
                                entry.importPriceIndex = dto.ImportPriceIndex;
                                if (dto.DemandValues != null && entry.demandValues != null)
                                    foreach (var ndDto in dto.DemandValues)
                                        foreach (var nd in entry.demandValues)
                                            if (nd != null && nd.neighborhood == ndDto.Neighborhood)
                                            {
                                                nd.demand                   = ndDto.Demand;
                                                nd.providers                = ndDto.Providers;
                                                nd.lastDaySold              = ndDto.LastDaySold;
                                                nd.lastDayProvidersExceeded = ndDto.LastDayProvidersExceeded;
                                                nd.hasPlayerMonopoly        = ndDto.HasPlayerMonopoly;
                                                break;
                                            }
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

        /// <summary>HOST: reflect a client's RENT in the host's OWN game — the building leaves the
        /// for-rent pool and shows as that player's BUSINESS. Rent-vs-deed split (2026-07-07): the
        /// tenant goes in businessOwnerRivalId (who RUNS the business); buildingOwnerRivalId is the
        /// DEED (the AI landlord, never re-assigned after worldgen) and is NOT touched — writing the
        /// renter there was the "rival page shows he OWNS the building" community bug, and the old
        /// vacate erased the landlord permanently.  MAIN THREAD.</summary>
        public static void HostReflectPlayerRent(string addressKey, string playerId)
        {
            try
            {
                var reg = FindRegistration(addressKey);
                if (reg == null) { Plugin.Logger.LogWarning($"[Patcher/Host] rent reflect: no reg for '{addressKey}'."); return; }
                reg.AvailableForRent = false;                       // out of the for-rent pool
                try { reg.businessOwnerRivalId = playerId; } catch { }   // the TENANT — runs the business here
                Plugin.Logger.LogInfo($"[Patcher/Host] {addressKey} now rented by player '{playerId}' (removed from for-rent; landlord '{reg.buildingOwnerRivalId}' kept).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher/Host] HostReflectPlayerRent: {ex.Message}"); }
        }

        /// <summary>HOST: reverse HostReflectPlayerRent — a player vacated their
        /// building, so clear the tenant mark and put it back in the for-rent pool in
        /// the host's OWN game. The deed field (landlord) stays.  MAIN THREAD.</summary>
        public static void HostReflectPlayerVacate(string addressKey)
        {
            try
            {
                var reg = FindRegistration(addressKey);
                if (reg == null) { Plugin.Logger.LogWarning($"[Patcher/Host] vacate reflect: no reg for '{addressKey}'."); return; }
                reg.AvailableForRent = true;                       // back into the for-rent pool
                try { reg.businessOwnerRivalId = ""; } catch { }   // no tenant any more (landlord untouched)
                Plugin.Logger.LogInfo($"[Patcher/Host] {addressKey} vacated — back on the for-rent market.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher/Host] HostReflectPlayerVacate: {ex.Message}"); }
        }

        /// <summary>Is this id one of the session's HUMAN players? (Rent-vs-deed repair: a player id
        /// in the DEED field is contamination from the pre-split plumbing.) Checks the live roster +
        /// self — an offline ex-member's pid isn't matchable, so the sweep is best-effort by design.</summary>
        public static bool IsSessionPlayerId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (id == MPConfig.PlayerId) return true;
            try { foreach (var p in MPRestSync.AllPlayers()) if (p == id) return true; } catch { }
            return false;
        }

        /// <summary>World-integrity line (approved diagnostics batch, 2026-07-09): one line per session
        /// describing the world-population invariants MP degradation can break (ANTIPATTERNS Class 14).
        /// A rival-poor / deed-contaminated world announces itself in the FIRST report instead of being
        /// reverse-engineered from downstream NREs. Called ~30s after scene-ready (the rival cache
        /// fills after load; an at-ready read would false-alarm zero).</summary>
        public static void LogWorldHealth(string reason)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return;
                int regs = 0, landlorded = 0, playerDeeds = 0, tenanted = 0;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    regs++;
                    string deed = ""; try { deed = reg.buildingOwnerRivalId ?? ""; } catch { }
                    if (!string.IsNullOrEmpty(deed)) landlorded++;
                    if (IsSessionPlayerId(deed)) playerDeeds++;
                    try { if (!string.IsNullOrEmpty(reg.businessOwnerRivalId)) tenanted++; } catch { }
                }
                int nonSpecialRivals = -1;
                try { nonSpecialRivals = BigAmbitions.Rivals.RivalsHelper.GetNonSpecialRivals()?.Count ?? -1; } catch { }
                int forSale = -1; try { forSale = gi.buildingsForSale?.Count ?? -1; } catch { }
                int injected = MPRegisterSync.InjectedCount;
                int players = 0; try { players = MPRestSync.AllPlayers().Count; } catch { }

                bool sick = nonSpecialRivals == 0 || playerDeeds > 0;
                string line = $"[WorldHealth] ({reason}) regs={regs} landlorded={landlorded} rivalBiz={tenanted} playerDeeds={playerDeeds} " +
                              $"nonSpecialRivals={nonSpecialRivals} forSale={forSale} injectedStaff={injected} players={players}";
                if (sick) Plugin.Logger.LogWarning(line + "  ← DEGRADED WORLD (rival-poor and/or deed contamination)");
                else      Plugin.Logger.LogInfo(line);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[WorldHealth] {ex.Message}"); }
        }

        /// <summary>One-shot save repair (scene-ready, every machine): the pre-split plumbing wrote
        /// RENTERS into buildingOwnerRivalId (the deed field) and erased AI landlords on vacate —
        /// both persisted into saves. Any session player id found in a deed field moves to the tenant
        /// field (businessOwnerRivalId) where the display actually belongs; the landlord itself is
        /// unrecoverable (worldgen never re-assigns) and stays empty — benign: the building simply
        /// isn't listed under any rival's owned buildings.</summary>
        public static void SweepRivalFieldContamination(string reason)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return;
                int repaired = 0;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    string deed; try { deed = reg.buildingOwnerRivalId ?? ""; } catch { continue; }
                    if (!IsSessionPlayerId(deed)) continue;
                    string addr; try { addr = GameStateReader.AddressKey(reg); } catch { continue; }
                    // LEGITIMATE deed vs the bug: the real-estate ledger is the authority. If it names
                    // this player as the BUYER of this building, the attribution is correct — keep it.
                    // (Only the host holds the ledgers; on clients this finds nothing, and a wrongly
                    // stripped legitimate deed self-heals on the next BusinessInfo apply, which now
                    // carries DeedOwnerPlayerId explicitly.)
                    bool legitimateDeed = false;
                    try
                    {
                        if (MPServer.IsRunning && MPServer.BuildingRealEstateOwners.TryGetValue(addr, out var buyer))
                            legitimateDeed = (buyer == deed) || (buyer == "host" && deed == MPConfig.PlayerId);
                    }
                    catch { }
                    if (legitimateDeed) continue;
                    try
                    {
                        if (string.IsNullOrEmpty(reg.businessOwnerRivalId)) reg.businessOwnerRivalId = deed;
                        reg.buildingOwnerRivalId = "";
                        repaired++;
                        Plugin.Logger.LogInfo($"[Patcher] rent-vs-deed repair: '{addr}' deed field held player '{deed}' with no purchase on the ledger → moved to tenant field.");
                    }
                    catch { }
                }
                if (repaired > 0)
                    Plugin.Logger.LogWarning($"[Patcher] rent-vs-deed repair ({reason}): {repaired} building(s) had a PLAYER in the deed field (pre-split contamination) — repaired.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] SweepRivalFieldContamination: {ex.Message}"); }
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

        internal static BuildingRegistration? FindRegistration(string addressKey)
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
