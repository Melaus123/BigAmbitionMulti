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
            if (!ClientApplyOwnership) return;
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
            if (!ClientApplyOwnership) return;
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
                                    && reg.dailyIncomes != null)
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
            if (!ClientApplyOwnership) return;
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

                    // Layout (string; controls which BusinessLayoutSet is used)
                    if (!string.IsNullOrEmpty(payload.Layout))
                        reg.Layout = payload.Layout;

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
                                var sd = new SerializedInteriorDesign { UUID = d.UUID };
                                if (d.Materials != null && d.Materials.Count > 0)
                                {
                                    var arr = new SerializedInteriorDesign.SerializableInteriorMaterial[d.Materials.Count];
                                    for (int i = 0; i < d.Materials.Count; i++)
                                    {
                                        var m = d.Materials[i];
                                        arr[i] = new SerializedInteriorDesign.SerializableInteriorMaterial
                                        {
                                            MaterialID    = m.MaterialID,
                                            MaterialIndex = m.MaterialIndex,
                                            ColorIndex    = m.ColorIndex,
                                        };
                                    }
                                    sd.materials = arr;
                                }
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
                    try
                    {
                        if (reg.itemInstances != null)
                        {
                            if (!_lastItemSer.TryGetValue(payload.AddressKey, out var lastSer))
                                lastSer = new Dictionary<string, string>();
                            var newSer  = new Dictionary<string, string>();
                            var newDict = new Dictionary<string, BigAmbitions.Items.ItemInstance>();
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
                                if (ii != null) { newDict[i.Id] = ii; changedIds.Add(i.Id); }
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
                            foreach (var kv in newDict)
                            {
                                var ii = kv.Value;
                                try
                                {
                                    var cargo = ii?.cargoInstances;
                                    if (ii == null || cargo == null || cargo.Count == 0) continue;
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

                            reg.itemInstances.Clear();
                            foreach (var kv in newDict) reg.itemInstances[kv.Key] = kv.Value;
                            _lastItemSer[payload.AddressKey] = newSer;
                        }
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] itemInstances apply: {ex.Message}"); }

                    Plugin.Logger.LogInfo($"[Patcher] Interior applied for '{payload.AddressKey}': layout='{payload.Layout}' designs={payload.InteriorDesigns.Count} prices={payload.RetailPrices.Count} dirt={payload.DirtSpots.Count} items={payload.ItemInstances.Count} (changed={changedIds.Count} removed={removedIds.Count}).");

                    // Trigger a visual refresh of the interior IF the local
                    // player is currently inside THIS building.  Writing to
                    // reg.* only updates the data model; the walls/floor/items
                    // GameObjects were already instantiated from stale data
                    // when LoadBuilding ran on entry.  Calling LoadBuilding
                    // again re-runs the full pipeline (layout, designs, items)
                    // against the now-fresh fields.
                    TryRefreshActiveInteriorIfMatches(payload.AddressKey, changedIds, removedIds, changedDesignUuids);
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

        /// <summary>True when this registration's business belongs to a SESSION
        /// PLAYER (after the rival-translation, businessOwnerRivalId carries the
        /// owning player's id).  The game has NO such concept — to it a replica
        /// is just "not rented by me" and its AI systems adopt it: cashiers
        /// auto-spawn on entry (SetupAiEmployeeStations) and the daily rival
        /// sim re-prices it (CompetitionHelper) — both decompile-confirmed to
        /// validate NOTHING beyond RentedByPlayer/non-empty owner id.</summary>
        public static bool IsSessionPlayerBusiness(BuildingRegistration? reg)
        {
            try
            {
                var owner = reg?.businessOwnerRivalId;
                if (string.IsNullOrEmpty(owner)) return false;
                if (owner == MPConfig.PlayerId) return true;
                if (ClientPlayerRoster.ContainsKey(owner!)) return true;
                return MPRestSync.AllPlayers().Contains(owner);
            }
            catch { return false; }
        }

        private static void TryRefreshActiveInteriorIfMatches(string addressKey,
            HashSet<string>? changedIds = null, HashSet<string>? removedIds = null,
            HashSet<string>? changedDesignUuids = null)
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
                try { matched.LoadBuilding(false); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] LoadBuilding re-trigger for layout '{reg.Layout}': {ex.Message}"); }

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
                RefreshItemsForActiveBuilding(matched, reg, changedIds, removedIds);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] TryRefreshActiveInteriorIfMatches: {ex.Message}"); }
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
            HashSet<string>? changedIds = null, HashSet<string>? removedIds = null)
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

                Plugin.Logger.LogInfo($"[Patcher] Items refresh: destroyed={destroyed} spawned={spawned} kept={liveById.Count} failed={failed} (dict size {dictCount}{(fullRebuild ? ", FULL rebuild" : "")}).");
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
            if (!ClientApplyOwnership) return;   // gated under existing kill-switch flag
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
            if (!ClientApplyOwnership) return;
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

                // AI-business retail prices (host-authoritative; the client's
                // rival sim is suppressed so its tables stay empty otherwise).
                // Player shops never carry Prices here — MPPriceSync owns them.
                try
                {
                    if (info.Prices != null && info.Prices.Count > 0 && reg.retailPrices != null
                        && !IsSessionPlayerBusiness(reg))
                    {
                        reg.retailPrices.Clear();
                        foreach (var rp in info.Prices)
                            reg.retailPrices.Add(new RetailPrice { itemName = rp.ItemName, price = rp.Price });
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher] AI prices apply '{info.AddressKey}': {ex.Message}"); }

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

                // Operating hours (Phase 1c) — without these, suppression on
                // client leaves scheduleDays empty and every business looks
                // closed.  Replace verbatim from host.
                try
                {
                    if (reg.scheduleDays != null)
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
                            reg.scheduleDays.Add(sd);
                        }
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
                // RentedByPlayer is intentionally NOT applied on client — host
                // having RentedByPlayer=true means HOST owns it, but writing
                // true on client would make the CLIENT think they own it.
                // Wave 2 will translate that into a synthetic rival.
                string priorBuildingOwner = reg.buildingOwnerRivalId?.ToString() ?? "";
                string priorBusinessOwner = reg.businessOwnerRivalId?.ToString() ?? "";
                bool   priorRented        = reg.RentedByPlayer;
                try
                {
                    // Default: mirror host's rival-id fields verbatim.
                    string newBuildingOwner = info.BuildingOwnerRivalId ?? "";
                    string newBusinessOwner = info.BusinessOwnerRivalId ?? "";
                    bool   newRented        = false;   // host's true ≠ client's true (Wave 3 translation)

                    // Wave 3 translation: if host marked the building as owned
                    // by a HUMAN player (OwnerPlayerId set), translate per
                    // receiver identity.
                    if (!string.IsNullOrEmpty(info.OwnerPlayerId))
                    {
                        if (info.OwnerPlayerId == MPConfig.PlayerId)
                        { newRented = true; newBuildingOwner = ""; }  // it's OURS — clear any stray rival-owner
                        else
                            newBuildingOwner = info.OwnerPlayerId;    // owned by another player; treat as rival
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

                // Diagnostic for Wave 2 planning: log when the host claims to
                // own a building so we can see exactly how host's reg represents
                // player-ownership.  Only logs interesting cases (host or AI
                // changes), not every default-empty record.
                if (info.RentedByPlayer || !string.IsNullOrEmpty(info.BuildingOwnerRivalId)
                                       || !string.IsNullOrEmpty(info.BusinessOwnerRivalId)
                                       || !string.IsNullOrEmpty(priorBuildingOwner)
                                       || !string.IsNullOrEmpty(priorBusinessOwner)
                                       || priorRented)
                {
                    Plugin.Logger.LogInfo($"[Patcher] Ownership for {info.AddressKey}: host[bldg='{info.BuildingOwnerRivalId}' biz='{info.BusinessOwnerRivalId}' rented={info.RentedByPlayer}] client-was[bldg='{priorBuildingOwner}' biz='{priorBusinessOwner}' rented={priorRented}].");
                }

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
            if (!ClientApplyMarket) return;      // CLAUDE-DIAGNOSTIC F4 gate
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
                        // Find matching entry and update prices
                        foreach (var entry in gi.productMarketEntries)
                        {
                            if (entry.itemName == dto.ItemName)
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

        /// <summary>HOST: reflect a client's rent in the host's OWN game so the host
        /// sees the building leave the for-rent pool and show as that player's
        /// business — mirroring how clients represent the host's player-owned
        /// buildings (buildingOwnerRivalId = the player's id).  MAIN THREAD.</summary>
        public static void HostReflectPlayerRent(string addressKey, string playerId)
        {
            try
            {
                var reg = FindRegistration(addressKey);
                if (reg == null) { Plugin.Logger.LogWarning($"[Patcher/Host] rent reflect: no reg for '{addressKey}'."); return; }
                reg.AvailableForRent = false;                       // out of the for-rent pool
                try { reg.buildingOwnerRivalId = playerId; } catch { }   // owned by that player (as a rival)
                Plugin.Logger.LogInfo($"[Patcher/Host] {addressKey} now owned by player '{playerId}' (removed from for-rent).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Patcher/Host] HostReflectPlayerRent: {ex.Message}"); }
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
