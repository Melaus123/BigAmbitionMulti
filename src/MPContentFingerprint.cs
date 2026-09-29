using System;

namespace BigAmbitionsMP
{
    /// <summary>
    /// A short fingerprint of the GAME CONTENT this machine loaded — item and business-type
    /// data, not the mod and not the save.
    ///
    /// Why (2026-07-26, round 102): the existing join gate compares the game VERSION FOLDER
    /// name ("EA 0.11"), which is far too coarse — two installs a month apart, differing in
    /// item data, both report "EA 0.11". Our own test rig drifted exactly that way and the
    /// resulting host-vs-client difference read as a mod bug for four investigation rounds.
    /// Real players can drift the same way — typically a pending game update on one machine.
    /// That is expected to be RARE and rarely harmful, so this is deliberately NOT a join gate
    /// and NOT a player-facing warning (user, 2026-07-26) — it is EVIDENCE:
    /// one line in every log, one field in every bug report, and a host-side WARNING naming the
    /// two fingerprints when a joiner's content differs. A future "their numbers don't match"
    /// report then answers itself instead of costing days.
    /// </summary>
    public static class MPContentFingerprint
    {
        private static volatile string _cached = "";

        /// <summary>The fingerprint, or "" if it has not been computed yet. SAFE ON ANY THREAD —
        /// it only reads the cached string.
        ///
        /// CRASH 2026-07-27, my own: the first version computed lazily inside MPClient.OnConnected,
        /// which runs on the LiteNetLib NETWORK thread. Computing touches
        /// BusinessTypeHelper.GetAllPlayerAvailableBusinesses → TagRef.GetDb →
        /// AssetBundleRequest.WaitForCompletion — a Unity ASSET load, which is main-thread-only —
        /// and the client hard-crashed on connect. Same hazard MPSaveManager documents for
        /// SaveGamePathHelper. Compute on the main thread (EnsureCached, driven from Update);
        /// every off-thread caller reads this.</summary>
        public static string Cached => _cached;

        /// <summary>Identity of the game BUILD, written "b&lt;buildNumber&gt;" (e.g. "b3680") — the very number the game
        /// shows as "Build 3680" (GameVersion.GetCurrent().buildNumber). This is the value the join gate compares.
        ///
        /// MACBUILD-1 (field 20260919-144115: a Mac client and a Windows host on the SAME Build 3680 refused each
        /// other). Until now this was the game assembly's ModuleVersionId, chosen on 2026-09-01 because the version
        /// FOLDER ("1.0") and the content name hashes both sat still across a Steam update that changed the save
        /// schema, while the module id moves on every game compile. It moves too much: the module id is per-PLATFORM.
        /// The build number moves on exactly the occasions the gate cares about and is identical on both platforms —
        /// the 2026-09-01 update did move it (rig notes: Build 3670 before, 3672 after). The module id is kept as a
        /// separate DIAGNOSTIC value, GameModuleId.
        ///
        /// THREAD-SAFETY: computed on the MAIN THREAD in EnsureCached, because GameVersion.GetCurrent() does
        /// Resources.Load&lt;GameVersion&gt;("Versioning/Current") — a Unity asset load — while this value is read on the
        /// LiteNetLib NETWORK thread at Hello time (MPClient/MPServer). Same hazard as the fingerprint itself
        /// (crash 2026-07-27). Reading this only reads the cached string; "" until the first main-thread pass, which
        /// the gate already treats as "skip".</summary>
        public static string GameBuildId => _gameBuild;
        private static volatile string _gameBuild = "";

        /// <summary>DIAGNOSTIC ONLY, never a gate: the game assembly's module version id ("N" format). It changes on
        /// every game COMPILE and differs per PLATFORM — which is why it stopped being the join identity
        /// (MACBUILD-1) and also why it is the finer of the two values in a bug report: two machines reporting the
        /// same "b3680" with different module ids are the same build compiled for different platforms. Pure assembly
        /// metadata (no Unity API) — safe on any thread; computed once. "" if unreadable.</summary>
        public static string GameModuleId
        {
            get
            {
                if (_gameModule != null) return _gameModule;
                try { _gameModule = typeof(GameManager).Assembly.ManifestModule.ModuleVersionId.ToString("N"); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Content] game module id unreadable: {ex.Message}"); _gameModule = ""; }
                return _gameModule;
            }
        }
        private static volatile string? _gameModule;

        /// <summary>MAIN THREAD ONLY. Idempotent and cheap after the first successful pass —
        /// called every frame from the UI tick so the value is ready before any connect.</summary>
        public static void EnsureCached()
        {
            // Round-253: the mod list rides the same main-thread lifecycle as the fingerprint.
            // H-MODSDIFFER-1 step 2 (2026-09-20): REFRESH, no longer compute-once. The set of
            // loaded mods changes while the game runs (closing the mods panel re-discovers, and
            // so does window focus after a folder edit), and a stale list is a WRONG list — the
            // one-shot cache was itself one of the false-mismatch causes.
            // Why polling and not ModDiscoveryRegistry.OnDiscoveryUpdated: the game sets that
            // event to null in ResetStaticState (ModDiscoveryRegistry.cs ~:80, a
            // [RuntimeInitializeOnLoadMethod(SubsystemRegistration)] that fires during Unity
            // start-up, i.e. around the time our plugin is chainloaded). A subscription made on
            // the wrong side of that call is dropped SILENTLY — the list would simply freeze,
            // with no error to notice. Polling a tick we already own cannot lose its trigger.
            // Cost: one file stat per mod every 2 s (the per-DLL assembly name is cached by
            // path + write time + length), so the value any Hello reads is at most 2 s old.
            // MODS-FOLD (review 2026-09-29, R1/R2; re-check fold F3): the game's own mod state decides WHEN a list may be
            // stored (TickModsScan / ComputeMods) - only while it is READY (ModsListReady: no scan running, the scan state
            // initialised, no mods-panel change being applied) - and a change that just ended is re-read within a quarter
            // second instead of waiting out the 2 s timer.
            TickModsScan();
            float nowT = UnityEngine.Time.unscaledTime;
            if (nowT >= _modsRefreshAt || ((_cachedMods.Length == 0 || ModsChangePending) && nowT >= _modsFastAt))
            {
                _modsRefreshAt = nowT + 2f;
                _modsFastAt = nowT + 0.25f;
                ComputeMods();
            }
            if (_gameBuild.Length == 0) ComputeGameBuild();  // MACBUILD-1: Unity asset load, so main thread only
            if (_cached.Length > 0) return;
            try { Compute(); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Content] fingerprint compute deferred: {ex.Message}"); }
        }

        private static void Compute()
        {
            int items = 0, bizTypes = 0, nbh = 0, sizes = 0;
            unchecked
            {
                int h = 17;
                try
                {
                    foreach (var it in BigAmbitions.Items.ItemsGetter.AllItems)
                    {
                        if (it == null) continue;
                        items++;
                        h ^= MPAudit.StableHash(it.itemName);   // XOR = order-independent
                    }
                }
                catch { }
                try
                {
                    // Business types carry the product lists that drive demand/market data —
                    // the exact data whose divergence started this.
                    foreach (var bt in Helpers.BusinessTypeHelper.GetAllPlayerAvailableBusinesses())
                    {
                        if (bt == null) continue;
                        bizTypes++;
                        h ^= MPAudit.StableHash(bt.businessTypeName);
                    }
                }
                catch { }
                // Round-253 (field: two players quoted 3.567× different rent for the same
                // warehouse): rent/valuation quotes are computed PURELY from static asset data
                // (Building.GetBuildingDailyMarketRent — neighborhood price fields × type
                // multipliers × sqm), which the name-set hashes above are blind to: a pricing
                // rebalance mod changes NUMBERS, not names. Fold the pricing numbers into a
                // fourth fingerprint segment so two installs quoting different prices differ
                // visibly in every log line and bug report.
                int p = 17;
                try
                {
                    var nds = NeighborhoodHelper.NeighborhoodsData;
                    if (nds != null)
                        foreach (var nd in nds)
                        {
                            if (nd == null) continue;
                            nbh++;
                            int r = MPAudit.StableHash(nd.neighbourhood ?? "");
                            r = r * 31 ^ FloatBits(nd.baseBuildingPricePerSqm);
                            r = r * 31 ^ FloatBits(nd.realEstateMultiplier);
                            r = r * 31 ^ FloatBits(nd.rentMultiplierPerSqmPrice);
                            p ^= r;   // XOR across records = order-independent
                        }
                }
                catch { }
                try
                {
                    foreach (var bs in Buildings.BuildingSizeHelper.GetAllBuildingSizes())
                    {
                        if (bs == null) continue;
                        sizes++;
                        p ^= MPAudit.StableHash(bs.buildingSize ?? "") * 31 ^ bs.squareMeters;
                    }
                }
                catch { }
                // Only publish once BOTH sides produced data — a half-loaded menu-time pass would
                // otherwise cache a bogus value for the whole process and mis-flag every join.
                if (items > 0 && bizTypes > 0)
                {
                    string pricing = nbh > 0 ? $"/p{p:X8}" : "";
                    _cached = $"{h:X8}/{items}i/{bizTypes}b{pricing}";
                    Plugin.Logger.LogInfo($"[Content] fingerprint {_cached} (items={items}, businessTypes={bizTypes}, pricing over {nbh} neighborhood(s) + {sizes} size(s)).");
                }
            }
        }

        /// <summary>MAIN THREAD ONLY (called from EnsureCached). GameVersion.GetCurrent() loads a ScriptableObject
        /// through Resources.Load, which is main-thread-only; the value is then read off-thread at Hello time.
        /// Leaves "" — which the join gate treats as "skip" — until a pass succeeds.</summary>
        private static void ComputeGameBuild()
        {
            // Review MEDIUM-1: GameVersion.GetCurrent() caches only on success, so a failing read would re-run
            // Resources.Load EVERY FRAME for the whole session (EnsureCached is called from the UI tick). Back off:
            // at most one try per 5 s, and give up with one WARNING after 12 tries (the gate then falls back to the
            // module ids - MPServer.ValidateHello, review HIGH-1).
            if (_gameBuildGaveUp || UnityEngine.Time.unscaledTime < _gameBuildRetryAt) return;
            _gameBuildRetryAt = UnityEngine.Time.unscaledTime + 5f;
            try
            {
                var gv = GameVersion.GetCurrent();
                int bn = gv != null ? gv.buildNumber : 0;   // Unity object: `!= null`, never `?.`
                if (bn <= 0)
                {
                    if (++_gameBuildTries >= 12)
                    {
                        _gameBuildGaveUp = true;
                        Plugin.Logger.LogWarning("[Content] the game's build number could not be read (12 tries) - join checks fall back to the module id.");
                    }
                    return;
                }
                _gameBuild = "b" + bn.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Plugin.Logger.LogInfo($"[Content] game build {_gameBuild} (module {GameModuleId}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Content] game build id deferred: {ex.Message}"); }
        }

        private static float _gameBuildRetryAt;
        private static int   _gameBuildTries;
        private static bool  _gameBuildGaveUp;

        private static int FloatBits(float f)
        {
            try { return BitConverter.ToInt32(BitConverter.GetBytes(f), 0); } catch { return 0; }
        }

        /// <summary>Host-side: compare a joiner's fingerprint with ours and say so in the host log
        /// when they differ. Never refuses the join. Runs on the NETWORK thread (HandleHello) —
        /// reads the cached value only, never computes.
        /// Round-253: SEGMENT-WISE — an older build sends fewer segments (no '/p' pricing part),
        /// and a whole-string compare would false-flag every such join. Only a differing segment
        /// BOTH sides sent counts as a mismatch.</summary>
        public static void HostCompare(string playerId, string theirs)
        {
            try
            {
                string mine = Cached;
                if (string.IsNullOrEmpty(mine)) return;   // ours not computed yet — say nothing rather than mis-flag
                if (string.IsNullOrEmpty(theirs))
                {
                    Plugin.Logger.LogInfo($"[Content] '{playerId}' sent no content fingerprint (older build). Ours={mine}.");
                    return;
                }
                if (theirs == mine) return;   // the common case — silent
                var a = theirs.Split('/'); var b = mine.Split('/');
                int common = Math.Min(a.Length, b.Length);
                bool differs = false, pricingDiffers = false;
                for (int i = 0; i < common; i++)
                    if (a[i] != b[i]) { differs = true; if (a[i].StartsWith("p") && b[i].StartsWith("p")) pricingDiffers = true; }
                if (!differs)
                {
                    Plugin.Logger.LogInfo($"[Content] '{playerId}' fingerprint matches on all shared segments (theirs={theirs}, ours={mine} — older build without the newer segment(s)).");
                    return;
                }
                Plugin.Logger.LogWarning(
                    $"[Content] MISMATCH: '{playerId}' loaded DIFFERENT game content — theirs={theirs} vs ours={mine}. " +
                    "Same mod + protocol, but the two games' data differ — a content mod or a pending game update on one side. " +
                    (pricingDiffers ? "The PRICING segment differs: expect different rent/valuation quotes for the same buildings (round-253). " : "") +
                    "Expect their market/demand/item-derived numbers to differ from the host's; this is NOT a mod defect.");
            }
            catch { }
        }

        // ── Round-253 / H-MODSDIFFER-1: the mod list that rides Hello ──
        // (Hello is built on the NETWORK thread; computing this touches the game's mod
        // registry — which async discovery mutates — and, in the fallback,
        // Application.persistentDataPath. Both are main-thread-only by contract, the same
        // hazard the fingerprint compute hit on 2026-07-27. EnsureCached runs on the UI tick
        // and refreshes this every 2 s; every off-thread caller reads the cached string.)
        private static volatile string _cachedMods = "";
        public static string CachedMods => _cachedMods;
        private static float _modsRefreshAt;
        private static bool  _modsCountSaid;
        private static bool  _modsFallbackSaid;
        private static bool  _hadRegistryList;   // a registry-built list has been cached at least once this session (review HIGH)
        private static volatile bool _cachedModsFromFolders;   // MODS-FOLD R1: the cached list came from the FOLDER walk
        private static float _modsFastAt;
        /// <summary>MODS-FOLD R1: true when <see cref="CachedMods"/> came from the installed-FOLDER walk - reached only when
        /// the game's scan state cannot be read at all. The Refuse gate never refuses on such a list.</summary>
        public static bool CachedModsFromFolders => _cachedModsFromFolders;
#if BAMP_DEV
        private static readonly System.Collections.Generic.List<string> _fakeMods = new System.Collections.Generic.List<string>();
        private static readonly System.Collections.Generic.List<string> _fakeFolders = new System.Collections.Generic.List<string>();
        private static volatile bool _simEmptyRegistry, _simScanUnknown;
        private static long _simBusyUntilMs;
#endif

        // ── MODS-FOLD (review 2026-09-29) R1/R2: the game's own mod-scan state ──
        // ModDiscoveryRegistry (BigAmbitions.ModsInternal; the installed assembly is the 2026-09-16 one, unchanged by
        // Build 3682) has no public "scan done" signal: HasDiscoveredEntries is false both BEFORE the first scan and AFTER a
        // scan that loaded nothing. Two private statics answer it, read by reflection:
        //   DiscoverySemaphore (SemaphoreSlim(1,1)) is HELD for the whole of every discovery - DiscoverAllModsAsync takes it
        //     before Clear() and releases it after NotifyDiscoveryUpdated (DiscoverSteamModsByIdsAsync the same) - so
        //     CurrentCount == 0 <=> the registry is being emptied/refilled right now. CurrentCount is safe on any thread.
        //   Initialized (bool) is set by Initialize(), which a discovery calls inside the semaphore - but GetAllModsAsync calls
        //     it too, OUTSIDE the semaphore (ModDiscoveryRegistry.cs:244-246), so Initialized alone does not prove that a
        //     discovery ran to its end. With the semaphore free it says the registry is set up and nothing is emptying or
        //     refilling it right now; the game's own start-up discovery sets it long before any lobby exists.
        // OnDiscoveryUpdated (public; raised at the end of every discovery and by RemoveDiscoveredSteamMods) bumps a
        // generation counter, so a cached list older than the last raise reads as stale. The subscription is renewed every
        // 2 s because the game's ResetStaticState nulls the event (see EnsureCached).
        // Re-check fold F2 (2026-09-29): 'ready' is decided by EVENTS, not a time window (the 3 s settle window is gone). The
        // mods panel's apply (ModsView.OnManifestChanged, when the panel closes: discover the added mods -> apply the scope
        // changes, awaiting each mod's load code with no time limit -> RemoveDiscoveredSteamMods, ModsView.cs:187-195) holds
        // old and new mods in the registry with the semaphore free, so it is bracketed by two Harmony patches (end of this
        // file): a prefix on OnManifestChanged marks it, a postfix on RemoveDiscoveredSteamMods (its only caller in Build
        // 3682; the method's early return is inside it, so the postfix runs even when nothing was removed) clears it.
        //   ready = semaphore free AND Initialized AND no apply in progress (ModsListReady).
        // If either patch did not bind, readiness falls back to 'semaphore free AND Initialized' (logged once). A flag still
        // set after 60 s (the apply threw before its remove step) is CLEARED and logged - that backstop only unsticks the
        // flag, it never decides a verdict.
        private enum ModsScan { Unknown, NotScanned, Busy, Done }
        private static System.Threading.SemaphoreSlim? _regSem;
        private static System.Reflection.FieldInfo? _regInitField;
        private static bool  _regProbed, _regProbeOk, _modsWaitSaid, _modsScanUnknownSaid, _regSawBusy;
        private static int   _regGen;            // OnDiscoveryUpdated raises (and ended scans / applies) seen - Interlocked
        private static int   _cachedGen = -1;    // _regGen when _cachedMods was last captured
        private static float _regSubAt, _applyHookAt;
        private static readonly Action _onRegUpdated = OnRegistryUpdated;
        private static long NowMs => System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 1000L);
        private static bool ModsChangePending => System.Threading.Volatile.Read(ref _regGen) != System.Threading.Volatile.Read(ref _cachedGen);
        // F2: the mods-panel apply bracket.
        private static int  _applyDepth;                 // apply starts not yet matched by an end - Interlocked
        private static long _applySinceMs;               // NowMs of the last apply start - Interlocked
        private static volatile bool _applyHooksOk;      // both apply patches bound (checked after patching)
        private static bool _applyHooksChecked;
        private static int  _applyHookTries;
        private const long  ApplyStuckMs = 60000;
        private const string HarmonyOwner = "com.bamp.bigambitionsmp";
        private static bool ApplyInProgress => _applyHooksOk && System.Threading.Volatile.Read(ref _applyDepth) > 0;

        private static void OnRegistryUpdated()
        {
            try { System.Threading.Interlocked.Increment(ref _regGen); }
            catch { }
        }

        /// <summary>F2. MAIN THREAD (Harmony prefix on ModsView.OnManifestChanged): a mods-panel change starts being applied.</summary>
        internal static void ModsApplyStarted()
        {
            try
            {
                System.Threading.Interlocked.Increment(ref _applyDepth);
                System.Threading.Interlocked.Exchange(ref _applySinceMs, Math.Max(1L, NowMs));
                Plugin.Logger.LogInfo("[Mods] a mods-panel change is being applied - the host's mod list is not ready until it ends.");
            }
            catch { }
        }

        /// <summary>F2. The game's continuation thread (Harmony postfix on ModDiscoveryRegistry.RemoveDiscoveredSteamMods, the
        /// apply's last step): the change is applied; the list is re-read (the generation bump).</summary>
        internal static void ModsApplyEnded()
        {
            try
            {
                int d, left = 0;
                while (true)
                {
                    d = System.Threading.Volatile.Read(ref _applyDepth);
                    if (d <= 0) break;
                    if (System.Threading.Interlocked.CompareExchange(ref _applyDepth, d - 1, d) == d) { left = d - 1; break; }
                }
                if (left == 0) System.Threading.Interlocked.Exchange(ref _applySinceMs, 0);
                System.Threading.Interlocked.Increment(ref _regGen);
                Plugin.Logger.LogInfo(left > 0
                    ? $"[Mods] a mods-panel change finished applying ({left} more still running)."
                    : "[Mods] a mods-panel change finished applying - the host's mod list is re-read now.");
            }
            catch { }
        }

        /// <summary>F2. MAIN THREAD: did our patch (prefix or postfix) bind to <paramref name="m"/>?</summary>
        private static bool OwnsPatch(System.Reflection.MethodBase? m, bool prefix)
        {
            try
            {
                if (m == null) return false;
                var info = HarmonyLib.Harmony.GetPatchInfo(m);
                if (info == null) return false;
                foreach (var pt in (prefix ? info.Prefixes : info.Postfixes))
                    if (pt != null && pt.owner == HarmonyOwner) return true;
            }
            catch { }
            return false;
        }

        /// <summary>F2. MAIN THREAD, after patching: log whether both apply patches bound. True when the answer is final
        /// (bound, or not bound on the last try).</summary>
        private static bool CheckApplyHooks(bool lastTry)
        {
            try
            {
                bool pre = OwnsPatch(HarmonyLib.AccessTools.Method(typeof(global::BigAmbitions.ModsView), "OnManifestChanged"), true);
                bool post = OwnsPatch(HarmonyLib.AccessTools.Method(typeof(global::BigAmbitions.ModsInternal.ModDiscoveryRegistry), "RemoveDiscoveredSteamMods"), false);
                if (pre && post)
                {
                    _applyHooksOk = true;
                    Plugin.Logger.LogInfo("[Mods] mods-panel apply tracking bound (ModsView.OnManifestChanged prefix=True, ModDiscoveryRegistry.RemoveDiscoveredSteamMods postfix=True) - the mod list is ready only when no scan and no mods-panel change is running.");
                    return true;
                }
                if (!lastTry) return false;
                _applyHooksOk = false;
                Plugin.Logger.LogWarning($"[Mods] mods-panel apply tracking NOT bound (ModsView.OnManifestChanged prefix={pre}, ModDiscoveryRegistry.RemoveDiscoveredSteamMods postfix={post}) - readiness falls back to 'no scan running AND scan state initialised'.");
                return true;
            }
            catch (Exception ex)
            {
                if (!lastTry) return false;
                _applyHooksOk = false;
                Plugin.Logger.LogWarning($"[Mods] mods-panel apply tracking check failed ({ex.GetType().Name}) - readiness falls back to 'no scan running AND scan state initialised'.");
                return true;
            }
        }

        /// <summary>MAIN THREAD, once.</summary>
        private static void ProbeRegistry()
        {
            try
            {
                var t = typeof(global::BigAmbitions.ModsInternal.ModDiscoveryRegistry);
                const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
                _regSem = t.GetField("DiscoverySemaphore", F)?.GetValue(null) as System.Threading.SemaphoreSlim;
                var fi = t.GetField("Initialized", F);
                _regInitField = fi != null && fi.FieldType == typeof(bool) ? fi : null;
                _regProbeOk = _regSem != null && _regInitField != null;
                Plugin.Logger.LogInfo(_regProbeOk
                    ? "[Mods] the game's mod-scan state is readable - the join list is taken only from a completed scan."
                    : $"[Mods] the game's mod-scan state is NOT readable (semaphore={_regSem != null}, flag={_regInitField != null}) - old list rule, and a folder-derived list never refuses a join.");
            }
            catch (Exception ex) { _regProbeOk = false; Plugin.Logger.LogWarning($"[Mods] mod-scan state probe failed ({ex.GetType().Name}) - old list rule in force."); }
        }

        /// <summary>MAIN THREAD (reads the registry's Initialized flag).</summary>
        private static ModsScan ScanState()
        {
#if BAMP_DEV
            if (System.Threading.Interlocked.Read(ref _simBusyUntilMs) > NowMs) return ModsScan.Busy;
            if (_simScanUnknown) return ModsScan.Unknown;
#endif
            try
            {
                var sem = _regSem; var fi = _regInitField;
                if (!_regProbeOk || sem == null || fi == null) return ModsScan.Unknown;
                if (sem.CurrentCount == 0) return ModsScan.Busy;
                return (bool)fi.GetValue(null) ? ModsScan.Done : ModsScan.NotScanned;
            }
            catch { return ModsScan.Unknown; }
        }

        /// <summary>MAIN THREAD, every frame (EnsureCached): probe once, keep the event subscribed, and mark the list
        /// stale when a scan ends - also one the subscription missed.</summary>
        private static void TickModsScan()
        {
            try
            {
                if (!_regProbed) { _regProbed = true; ProbeRegistry(); }
                float now = UnityEngine.Time.unscaledTime;
                if (now >= _regSubAt)
                {
                    _regSubAt = now + 2f;
                    global::BigAmbitions.ModsInternal.ModDiscoveryRegistry.OnDiscoveryUpdated -= _onRegUpdated;
                    global::BigAmbitions.ModsInternal.ModDiscoveryRegistry.OnDiscoveryUpdated += _onRegUpdated;
                }
#if BAMP_DEV
                long simEnd = System.Threading.Interlocked.Read(ref _simBusyUntilMs);
                if (simEnd != 0 && NowMs >= simEnd)
                {
                    System.Threading.Interlocked.Exchange(ref _simBusyUntilMs, 0);
                    OnRegistryUpdated();   // a real discovery raises OnDiscoveryUpdated as it ends
                    Plugin.Logger.LogInfo("[Mods] DEV simulated rescan ended.");
                }
#endif
                if (ScanState() == ModsScan.Busy) _regSawBusy = true;
                else if (_regSawBusy) { _regSawBusy = false; System.Threading.Interlocked.Increment(ref _regGen); }
                // F2: once patching has run (MPSaveManager.PatchingStarted), check the apply patches bound - up to 3 tries 2 s apart.
                if (!_applyHooksChecked && MPSaveManager.PatchingStarted && now >= _applyHookAt)
                {
                    _applyHookAt = now + 2f;
                    if (CheckApplyHooks(++_applyHookTries >= 3)) _applyHooksChecked = true;
                }
                // F2 backstop: a flag stuck for 60 s (the apply threw before its remove step) is cleared - never a verdict.
                long since = System.Threading.Interlocked.Read(ref _applySinceMs);
                if (since != 0 && System.Threading.Volatile.Read(ref _applyDepth) > 0 && NowMs - since >= ApplyStuckMs)
                {
                    System.Threading.Interlocked.Exchange(ref _applyDepth, 0);
                    System.Threading.Interlocked.Exchange(ref _applySinceMs, 0);
                    System.Threading.Interlocked.Increment(ref _regGen);
                    Plugin.Logger.LogWarning($"[Mods] a mods-panel change was still marked as being applied after {ApplyStuckMs / 1000} s (its apply step likely failed before the remove step) - the mark is cleared and the list is re-read.");
                }
            }
            catch { }
        }

        /// <summary>Re-check fold F2. ANY THREAD: the game's mod state is READY - no discovery running (the semaphore free),
        /// the scan state initialised, and no mods-panel change being applied (when both apply patches bound). Read LIVE.
        /// The one readiness function: ComputeMods captures a list only when it is true (F3), and the Refuse verdict
        /// (ModsVerdictReady) builds on it. The DEV lever 'fakemod busy N' makes it false. Otherwise <paramref name="why"/>
        /// names what is not ready.</summary>
        public static bool ModsListReady(out string why)
        {
            why = "";
            try
            {
#if BAMP_DEV
                if (System.Threading.Interlocked.Read(ref _simBusyUntilMs) > NowMs) { why = "the game is rescanning its mods (DEV simulated)"; return false; }
#endif
                var sem = _regSem; var fi = _regInitField;
                if (sem != null && sem.CurrentCount == 0) { why = "the game is scanning its mods"; return false; }
                if (ApplyInProgress) { why = "a mods-panel change is being applied"; return false; }
                if (fi != null && !(bool)fi.GetValue(null)) { why = "the game has not completed a mod scan yet"; return false; }
                return true;
            }
            catch { why = "the game's mod-scan state could not be read"; return false; }
        }

        /// <summary>MODS-FOLD R2 / re-check fold. ANY THREAD (the Hello handler): true when the host's list may decide a
        /// Refuse verdict - the mod state is ready (ModsListReady), <see cref="CachedMods"/> was captured after the last
        /// change, and it exists. When false the Hello is admitted as Allow, never refused and never held.</summary>
        public static bool ModsVerdictReady(out string why)
        {
            try
            {
                if (!ModsListReady(out why)) return false;
                if (ModsChangePending) { why = "the host's mod list is being re-read after a change"; return false; }
                if (_cachedMods.Length == 0) { why = "the host's mod list is not computed yet"; return false; }
                return true;
            }
            catch { why = "the host's mod list could not be checked"; return false; }
        }

        /// <summary>MAIN THREAD ONLY (called from EnsureCached).</summary>
        private static void ComputeMods()
        {
            try
            {
                var st = ScanState();
                // R2 / F3: never capture a list mid-scan or mid-apply - the registry is emptied and refilled across awaits,
                // so a list read then is PARTIAL, and a Hello sent with it (or a verdict made on it) is wrong.
                if (st == ModsScan.Busy) return;
                int gen = System.Threading.Volatile.Read(ref _regGen);
                if (st == ModsScan.NotScanned)
                {
                    // R1: the game has not finished its first scan. Nothing is sent yet ("" = not computed) - never the
                    // FOLDER list, which counts switched-off Workshop items and every ModsLocal folder as loaded mods.
                    if (!_modsWaitSaid) { _modsWaitSaid = true; Plugin.Logger.LogInfo("[Mods] the game has not finished its first mod scan - no mod list until it does."); }
                    return;
                }
                if (!ModsListReady(out _)) return;   // F3: a mods-panel change being applied (or the DEV 'busy' lever)
                // Done: an empty registry now MEANS no loaded mods = "(none)" (R1; R3: switching every mod off reads as
                // "(none)", not as the last list). Unknown (state unreadable): a raise of OnDiscoveryUpdated still proves a scan ended.
                bool scanDone = st == ModsScan.Done || gen > 0;
#if BAMP_DEV
                if (_simScanUnknown) scanDone = false;
#endif
                string m = ListLoadedMods(scanDone);
                bool fromFolders = false;
                if (m.Length == 0)
                {
                    // The registry threw, or (state unreadable, no raise seen) it is empty: the last good list stands.
                    if (_hadRegistryList || st == ModsScan.Done) { System.Threading.Volatile.Write(ref _cachedGen, gen); return; }
                    // Only when the scan state cannot be read at all and nothing was ever listed: the old installed-FOLDER
                    // walk, MARKED - the Refuse gate never refuses on it (MPServer.ValidateHelloVersion).
                    if (!_modsScanUnknownSaid)
                    {
                        _modsScanUnknownSaid = true;
                        Plugin.Logger.LogInfo("[Mods] the game's mod-scan state is unreadable and its registry has listed nothing - using the installed-FOLDER list (informational; it never refuses a join).");
                    }
                    m = MPBugReport.ListInstalledMods();
#if BAMP_DEV
                    lock (_fakeFolders)
                        foreach (var f in _fakeFolders)
                            m = m.Length == 0 ? ("local:" + f) : (m + ", local:" + f);
#endif
                    fromFolders = true;
                }
                else _hadRegistryList = true;
#if BAMP_DEV
                lock (_fakeMods)
                    foreach (var f in _fakeMods)
                        m = (m.Length == 0 || m == "(none)") ? ("test:" + f) : (m + ", test:" + f);
#endif
                _cachedModsFromFolders = fromFolders;
                _cachedMods = string.IsNullOrEmpty(m) ? "(none)" : m;   // "(none)" != "" so an empty list still reads as "computed"
                System.Threading.Volatile.Write(ref _cachedGen, gen);
            }
            catch { }
        }

        /// <summary>MODS-FOLD R1: true when a mod list carries installed-FOLDER tokens ("workshop:", "local:") - a list built
        /// by the folder walk (an older build's, or a side whose scan state is unreadable). A folder is not a loaded mod, so
        /// the Refuse gate never refuses on such a list.</summary>
        public static bool IsFolderList(string list)
        {
            try
            {
                foreach (var t in ParseModList(list))
                    if (t.StartsWith("workshop:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("local:", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>H-MODSDIFFER-1 step 2 (user-approved 2026-09-20). MAIN THREAD ONLY.
        /// The mods the GAME ACTUALLY LOADED, as an ordinal-sorted, comma-separated token list —
        /// which is what the join-time comparison should have been reading all along.
        ///
        /// Why this replaces the folder walk (MPBugReport.ListInstalledMods): a folder on disk is
        /// not a loaded mod, and every one of the known FALSE mismatches came from that gap — a
        /// Steam mod subscribed but switched OFF still has its folder; the same mod installed from
        /// the Workshop on one machine and into ModsLocal on the other yielded two different
        /// tokens; leftover folders never disappear; on a Mac the walk lands inside the .app
        /// bundle and finds nothing. It was also BLIND to a version drift of the same workshop id.
        /// The game's registry answers all five: it lists ENABLED Steam mods (steam_mod_manifest)
        /// plus ALL ModsLocal mods (local mods have no toggle), and nothing that failed to load.
        ///
        /// Token: "mod:&lt;AssemblySimpleName&gt;@&lt;AssemblyVersion&gt;", both read from the mod folder's
        /// single root DLL as pure METADATA (System.Reflection.AssemblyName.GetAssemblyName opens
        /// the file and reads its manifest; it NEVER loads or runs the assembly). That is the same
        /// identity the game's own loader keys on, and it is install-location independent. It shows a
        /// version drift ONLY for mods that actually bump their AssemblyVersion - most (ours included:
        /// no <Version> in the csproj) ship the SDK default 1.0.0.0, so expect little from it. ACCEPTED
        /// RISK: two DIFFERENT mods built from one template name (MyMod@1.0.0.0) compare as equal
        /// across machines; adding the display name or steam id to the token would re-break the
        /// workshop-vs-local equality this exists for. Fallbacks, in order, when the DLL cannot be
        /// read: "ws:&lt;steamid&gt;" for a numeric ModId, else "name:&lt;display name&gt;".
        ///
        /// PRIVACY: a LOCAL mod's ModId is its FULL PATH on disk (ModDiscoveryRegistry). It never
        /// leaves this method — only an assembly name, a workshop id or a display name does.
        ///
        /// Returns "(none)" when the registry is empty and <paramref name="scanDone"/> (MODS-FOLD R1: a completed scan that
        /// loaded nothing), "" when it is empty before a completed scan or threw, "(none)" also for entries that yield no token.</summary>
        public static string ListLoadedMods(bool scanDone = false)
        {
            var tokens = new System.Collections.Generic.List<string>();
            int byAsm = 0, byWs = 0, byName = 0, failed = 0;
            try
            {
                bool empty = !global::BigAmbitions.ModsInternal.ModDiscoveryRegistry.HasDiscoveredEntries;
#if BAMP_DEV
                if (_simEmptyRegistry) empty = true;
#endif
                if (empty) return scanDone ? "(none)" : "";
                var entries = global::BigAmbitions.ModsInternal.ModDiscoveryRegistry.Entries;
                if (entries == null) return "";
                // One mod is listed once per ACTIVATION SCOPE it registers in, so dedupe by ModId
                // (the registry's own key). GetActiveMods() would have been the tempting shortcut,
                // but it is scope-limited and would under-report.
                var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in entries)
                {
                    var list = kv.Value;
                    if (list == null) continue;
                    foreach (var e in list)
                    {
                        if (e == null) continue;
                        string id = e.ModId ?? "";
                        if (id.Length > 0 && !seen.Add(id)) continue;
                        string tok = AssemblyToken(e.ModFolder);
                        if (tok.Length > 0) byAsm++;
                        else if (IsAllDigits(id)) { tok = "ws:" + id; byWs++; }   // numeric ModId == the steam id; a LOCAL ModId is a path, so it can never reach here
                        else { tok = "name:" + Clean(e.ModDisplayName); byName++; }
                        tokens.Add(tok);
                    }
                }
                try
                {
                    var f = global::BigAmbitions.ModsInternal.ModDiscoveryRegistry.FailedModReasons;
                    failed = f != null ? f.Count : 0;   // failed mods are NOT in Entries — counted only so the log line says why a count looks short
                }
                catch { }
            }
            catch (Exception ex)
            {
                // No message text: a TypeLoad/FileNotFound message can carry a full disk path, and logs ship in bug reports.
                if (!_modsFallbackSaid)
                {
                    _modsFallbackSaid = true;
                    Plugin.Logger.LogWarning($"[Mods] the game's mod registry could not be read ({ex.GetType().Name}) - the last good list stands.");
                }
                return "";
            }
            tokens.Sort(StringComparer.Ordinal);
            if (!_modsCountSaid)
            {
                _modsCountSaid = true;
                Plugin.Logger.LogInfo($"[Mods] loaded list from the game's registry: {tokens.Count} mod(s) ({byAsm} by assembly, {byWs} by workshop id, {byName} by display name); failed to load: {failed}");
            }
            return tokens.Count == 0 ? "(none)" : string.Join(", ", tokens);
        }

        // Keyed by "<dll path>|<last write ticks>|<length>": a rescan that finds the same file
        // unchanged costs one stat and a dictionary hit, so the 2 s refresh is effectively free
        // and only a DLL that actually changed is re-read.
        private static readonly System.Collections.Generic.Dictionary<string, string> _asmTokenCache =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>"mod:&lt;name&gt;@&lt;version&gt;" from the mod folder's single root DLL, or "" when it
        /// cannot be read. The game guarantees EXACTLY ONE root DLL per mod folder and refuses a
        /// folder with none or several (ModDiscoveryRegistry.TryGetRootDllPath / IsModFolder), so
        /// "not exactly one" here means "not something we should be naming" — fall through.</summary>
        private static string AssemblyToken(string? modFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(modFolder) || !System.IO.Directory.Exists(modFolder)) return "";
                var dlls = System.IO.Directory.GetFiles(modFolder, "*.dll", System.IO.SearchOption.TopDirectoryOnly);
                if (dlls.Length != 1) return "";
                var fi = new System.IO.FileInfo(dlls[0]);
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                string key = dlls[0] + "|" + fi.LastWriteTimeUtc.Ticks.ToString(inv) + "|" + fi.Length.ToString(inv);
                lock (_asmTokenCache)
                {
                    if (_asmTokenCache.TryGetValue(key, out var hit)) return hit;
                    if (_asmTokenCache.Count > 256) _asmTokenCache.Clear();   // only grows when a DLL changes; a hard cap keeps a pathological edit loop bounded
                }
                string tok = "";
                try
                {
                    var an = System.Reflection.AssemblyName.GetAssemblyName(dlls[0]);
                    if (an != null && !string.IsNullOrEmpty(an.Name))
                        tok = "mod:" + Clean(an.Name) + "@" + (an.Version != null ? an.Version.ToString() : "?");
                }
                catch { tok = ""; }
                lock (_asmTokenCache) _asmTokenCache[key] = tok;
                return tok;
            }
            catch { return ""; }
        }

        /// <summary>The list is COMMA-separated, so a comma inside a token would arrive on the other
        /// side as two phantom mods: swap commas (and control characters) out. Parentheses go too —
        /// ParseModList strips everything from the first "(" onwards, because the FALLBACK folder
        /// format spells the inner folder that way ("workshop:123(inner)"), and a display name such
        /// as "My Mod (Beta)" would otherwise be silently cut short in the comparison and in the
        /// full-list log block.</summary>
        private static string Clean(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "?";
            var sb = new System.Text.StringBuilder(s!.Length);
            foreach (char c in s!)
                sb.Append(c == ',' ? ';' : c == '(' ? '[' : c == ')' ? ']' : (char.IsControl(c) ? ' ' : c));
            string outp = sb.ToString().Trim();
            return outp.Length == 0 ? "?" : outp;
        }

        private static bool IsAllDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }

#if BAMP_DEV
        /// <summary>TestDrive fixture (round-253): append an imitation mod to the CACHED
        /// list the Hello handshake sends. Dev builds only; see the 'fakemod' verb.</summary>
        internal static void TestAddFakeMod(string name)
        {
            // H-MODSDIFFER-1 step 2: the list is now RECOMPUTED every 2 s, so a fake appended
            // straight onto the cached string would vanish two seconds later. Keep the fakes in a
            // list that every recompute re-applies.
            if (string.IsNullOrEmpty(name)) return;
            lock (_fakeMods) _fakeMods.Add(name);
            ComputeMods();
        }
        internal static void TestClearFakeMods()
        {
            lock (_fakeMods) _fakeMods.Clear();
            lock (_fakeFolders) _fakeFolders.Clear();
            _simEmptyRegistry = false; _simScanUnknown = false;
            System.Threading.Interlocked.Exchange(ref _simBusyUntilMs, 0);
            ComputeMods();
        }
        /// <summary>MODS-FOLD test (a): the registry reads as EMPTY after a completed scan (nothing loaded).</summary>
        internal static void TestEmptyRegistry(bool on) { _simEmptyRegistry = on; ComputeMods(); }
        /// <summary>MODS-FOLD test (a): an extra/disabled folder on disk - it appears only in the installed-FOLDER walk.</summary>
        internal static void TestAddFakeFolder(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (_fakeFolders) _fakeFolders.Add(name);
            ComputeMods();
        }
        /// <summary>MODS-FOLD test (a): the game's scan state reads as unreadable and nothing was listed yet - the only
        /// condition that still takes the FOLDER walk.</summary>
        internal static void TestScanUnknown(bool on) { _simScanUnknown = on; if (on) _hadRegistryList = false; ComputeMods(); }
        /// <summary>MODS-FOLD test (b): a rescan in progress for <paramref name="seconds"/> s, ending with the raise a real one makes.
        /// Seen through the one readiness function (ModsListReady): the host's list is NOT ready meanwhile, so a Refuse
        /// verdict admits the joiner as Allow.</summary>
        internal static void TestSimulateRescan(int seconds)
        {
            System.Threading.Interlocked.Exchange(ref _simBusyUntilMs, NowMs + Math.Max(1, seconds) * 1000L);
            Plugin.Logger.LogInfo($"[Mods] DEV simulated rescan: {seconds} s.");
        }
#endif

        /// <summary>Diff two comma-separated mod lists (the report.md InstalledMods format).
        /// Returns true when they differ, with capped human-readable only-lists.
        /// Round-258: entries tagged "layout:" (shared building-layout blueprints, pure
        /// save-side templates) are excluded from the comparison — they cannot desync
        /// gameplay, and comparing them made every layout subscriber trip mismatch
        /// warnings against non-subscribers (rig 2026-08-15, workshop:3428251077).
        /// H-MODSDIFFER-1 step 2 (2026-09-20): that drop is now dead weight for the NORMAL list —
        /// the tokens are "mod:/ws:/name:" from the game's own registry, and a blueprint never
        /// enters that registry (the workshop tags it "Blueprint", not "mod"). It is kept because
        /// it still does its job on an installed-FOLDER list (an older build's, or the fallback taken only when the
        /// game's scan state cannot be read); against the new tokens it simply matches nothing.</summary>
        public static bool DiffMods(string mine, string theirs, out string onlyMine, out string onlyTheirs, out int onlyMineCount, out int onlyTheirsCount)
        {
            onlyMine = onlyTheirs = ""; onlyMineCount = onlyTheirsCount = 0;
            try
            {
                var setMine   = ParseModList(mine);
                var setTheirs = ParseModList(theirs);
                setMine.RemoveWhere(s => s.StartsWith("layout:", StringComparison.OrdinalIgnoreCase));
                setTheirs.RemoveWhere(s => s.StartsWith("layout:", StringComparison.OrdinalIgnoreCase));
                var om = new System.Collections.Generic.List<string>();
                var ot = new System.Collections.Generic.List<string>();
                foreach (var s in setMine)   if (!setTheirs.Contains(s)) om.Add(s);
                foreach (var s in setTheirs) if (!setMine.Contains(s))   ot.Add(s);
                onlyMineCount = om.Count; onlyTheirsCount = ot.Count;
                const int Cap = 6;
                onlyMine   = string.Join(", ", om.GetRange(0, Math.Min(Cap, om.Count)))  + (om.Count > Cap ? $" +{om.Count - Cap} more" : "");
                onlyTheirs = string.Join(", ", ot.GetRange(0, Math.Min(Cap, ot.Count))) + (ot.Count > Cap ? $" +{ot.Count - Cap} more" : "");
                return om.Count > 0 || ot.Count > 0;
            }
            catch { return false; }
        }

        // ── MODS-GATE-1 (user-approved 2026-09-29): the refusal the host's 'Different mods: Refuse' sends ──
        /// <summary>The disconnect tag for a mod-list refusal: "BAMP:mods:&lt;nMissing&gt;|&lt;nExtra&gt;|&lt;names&gt;|&lt;names&gt;",
        /// names TAB-separated, at most 5 per side, from the JOINER's point of view (missing = only on the host, extra = only
        /// on the joiner) - the same compared sets as <see cref="DiffMods"/>. The tag rides the transport's disconnect data;
        /// by construction it stays under 400 bytes, far under that limit: long names are shortened (40, 24, 12, 6
        /// characters, ellipsized) and, last, the names are dropped.</summary>
        public static string ModsRefusalTag(string hostMods, string joinerMods, out int nMissing, out int nExtra)
        {
            nMissing = nExtra = 0;
            try
            {
                var missing = new System.Collections.Generic.List<string>();
                var extra   = new System.Collections.Generic.List<string>();
                ReadableModDiff(hostMods, joinerMods, missing, extra);
                nMissing = missing.Count; nExtra = extra.Count;
                string head = "BAMP:mods:" + nMissing.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
                            + nExtra.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|";
                foreach (int cap in new[] { 40, 24, 12, 6 })
                {
                    string tag = head + TagNames(missing, cap) + "|" + TagNames(extra, cap);
                    if (System.Text.Encoding.UTF8.GetByteCount(tag) <= 400) return tag;
                }
                return head + "|";
            }
            catch { return "BAMP:mods"; }
        }

        private static string TagNames(System.Collections.Generic.List<string> names, int cap)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < names.Count && i < 5; i++)
            {
                string s = names[i].Replace('|', '/').Replace('\t', ' ');
                if (s.Length > cap)
                {
                    int cut = cap - 1;
                    if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;   // R4: never split a UTF-16 surrogate pair
                    s = s.Substring(0, cut).TrimEnd() + "\u2026";
                }
                if (sb.Length > 0) sb.Append('\t');
                sb.Append(s);
            }
            return sb.ToString();
        }

        /// <summary>The DiffMods sets (layout: dropped, case-insensitive) as readable NAMES, sorted: the token's kind prefix
        /// ("mod:", "ws:", "name:", "test:", a fallback folder kind) and a mod token's "@version" are cut; the version is
        /// kept only when the same name is on BOTH sides (a version drift would otherwise read "missing X; extra X").</summary>
        public static void ReadableModDiff(string hostMods, string joinerMods,
            System.Collections.Generic.List<string> hostOnly, System.Collections.Generic.List<string> joinerOnly)
        {
            try
            {
                var sa = ParseModList(hostMods);
                var sj = ParseModList(joinerMods);
                sa.RemoveWhere(s => s.StartsWith("layout:", StringComparison.OrdinalIgnoreCase));
                sj.RemoveWhere(s => s.StartsWith("layout:", StringComparison.OrdinalIgnoreCase));
                var ta = new System.Collections.Generic.List<string>();
                var tj = new System.Collections.Generic.List<string>();
                foreach (var s in sa) if (!sj.Contains(s)) ta.Add(s);
                foreach (var s in sj) if (!sa.Contains(s)) tj.Add(s);
                var na = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var nj = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in ta) na.Add(ReadableModName(s, false));
                foreach (var s in tj) nj.Add(ReadableModName(s, false));
                foreach (var s in ta) { string r = ReadableModName(s, false); hostOnly.Add(nj.Contains(r) ? ReadableModName(s, true) : r); }
                foreach (var s in tj) { string r = ReadableModName(s, false); joinerOnly.Add(na.Contains(r) ? ReadableModName(s, true) : r); }
                hostOnly.Sort(StringComparer.OrdinalIgnoreCase);
                joinerOnly.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
        }

        private static string ReadableModName(string token, bool withVersion)
        {
            try
            {
                string s = token ?? "";
                int c = s.IndexOf(':');
                bool isMod = s.StartsWith("mod:", StringComparison.OrdinalIgnoreCase);
                if (c > 0 && c <= 10)
                {
                    bool kind = true;
                    for (int i = 0; i < c; i++) if (!char.IsLetter(s[i])) { kind = false; break; }
                    if (kind) s = s.Substring(c + 1);
                }
                string ver = "";
                if (isMod)
                {
                    int at = s.LastIndexOf('@');
                    if (at > 0) { ver = s.Substring(at + 1); s = s.Substring(0, at); }
                }
                s = s.Replace('<', '(').Replace('>', ')').Trim();   // the card is TMP rich text: no '<' may open a tag; '(' ')' are in every font (R4)
                if (s.Length == 0) s = token ?? "?";
                return withVersion && ver.Length > 0 ? s + " " + ver : s;
            }
            catch { return token ?? "?"; }
        }

        // ── H-MODSDIFFER-1 step 1 (2026-09-20, log-only) ──
        /// <summary>The UNCAPPED four-line block the 6-token DiffMods summary cannot carry: both full
        /// lists and both only-lists, sorted. The FULL lists keep "layout:" tokens (round-258 drops
        /// those from the COMPARISON because they cannot desync gameplay, but a reader chasing a
        /// content difference still wants to see them); the only-lists are the compared sets, so they
        /// agree with the mismatch warning the player already got. Lines are split at ~900 characters
        /// so a long list survives the log. <paramref name="diffSignature"/> identifies the difference
        /// SET, so a caller can re-arm its once-per-peer budget when the set changes.</summary>
        public static System.Collections.Generic.List<string> FullModBlock(string mine, string theirs, string peer, out string diffSignature)
        {
            var lines = new System.Collections.Generic.List<string>();
            diffSignature = "";
            try
            {
                var fullMine   = SortedTokens(mine,   keepLayouts: true);
                var fullTheirs = SortedTokens(theirs, keepLayouts: true);
                var cmpMine    = SortedTokens(mine,   keepLayouts: false);
                var cmpTheirs  = SortedTokens(theirs, keepLayouts: false);
                var onlyMine   = cmpMine.FindAll(s => !cmpTheirs.Contains(s));
                var onlyTheirs = cmpTheirs.FindAll(s => !cmpMine.Contains(s));
                diffSignature  = string.Join(",", onlyMine) + "|" + string.Join(",", onlyTheirs);
                WrapTokens(lines, $"[Mods] full list MINE ({fullMine.Count}): ", fullMine);
                WrapTokens(lines, $"[Mods] full list '{peer}' ({fullTheirs.Count}): ", fullTheirs);
                WrapTokens(lines, "[Mods] only mine: ", onlyMine);
                WrapTokens(lines, "[Mods] only theirs: ", onlyTheirs);
            }
            catch { }
            return lines;
        }

        private static readonly System.Collections.Generic.Dictionary<string, string> _blockSaid =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Log <see cref="FullModBlock"/> at INFO once per peer per session, re-armed only
        /// when the difference SET changes. Budgeted this way on purpose: the block is uncapped, so it
        /// must never repeat per join attempt or per hour.</summary>
        public static void LogFullModBlockOnce(string peerPid, string mine, string theirs, string peerLabel)
        {
            try
            {
                var lines = FullModBlock(mine, theirs, peerLabel, out string sig);
                if (lines.Count == 0) return;
                lock (_blockSaid)
                {
                    if (_blockSaid.TryGetValue(peerPid ?? "", out var prev) && prev == sig) return;
                    _blockSaid[peerPid ?? ""] = sig;
                }
                foreach (var line in lines) Plugin.Logger.LogInfo(line);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Mods] full-list block: {ex.Message}"); }
        }

        private static System.Collections.Generic.List<string> SortedTokens(string csv, bool keepLayouts)
        {
            var set = ParseModList(csv);
            if (!keepLayouts) set.RemoveWhere(s => s.StartsWith("layout:", StringComparison.OrdinalIgnoreCase));
            var list = new System.Collections.Generic.List<string>(set);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        private static void WrapTokens(System.Collections.Generic.List<string> lines, string prefix, System.Collections.Generic.List<string> tokens)
        {
            const int MaxLine = 900;
            if (tokens.Count == 0) { lines.Add(prefix + "-"); return; }
            var sb = new System.Text.StringBuilder(prefix);
            bool first = true;
            foreach (var t in tokens)
            {
                if (!first && sb.Length + 2 + t.Length > MaxLine)
                {
                    lines.Add(sb.ToString());
                    sb.Length = 0;
                    sb.Append(prefix).Append("(cont.) ");
                    first = true;
                }
                if (!first) sb.Append(", ");
                sb.Append(t);
                first = false;
            }
            lines.Add(sb.ToString());
        }

        private static System.Collections.Generic.HashSet<string> ParseModList(string csv)
        {
            var set = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(csv) || csv == "(none)") return set;
            foreach (var raw in csv.Split(','))
            {
                var s = raw.Trim();
                if (s.Length == 0) continue;
                // Strip the "(innerFolder)" suffix — cosmetic, differs per install layout.
                int paren = s.IndexOf('(');
                if (paren > 0) s = s.Substring(0, paren);
                set.Add(s);
            }
            return set;
        }
    }

    /// <summary>Re-check fold F2 (2026-09-29): the mods panel's apply STARTS here (ModsView.cs:82-84 calls it on the main
    /// thread when the panel closes with a changed manifest; the body is async, the prefix runs at its synchronous start).</summary>
    [HarmonyLib.HarmonyPatch]
    public static class Patch_ModsView_OnManifestChanged_ApplyStart
    {
        static System.Reflection.MethodBase? TargetMethod()
            => HarmonyLib.AccessTools.Method(typeof(global::BigAmbitions.ModsView), "OnManifestChanged");

        static void Prefix()
        {
            try { MPContentFingerprint.ModsApplyStarted(); } catch { }
        }
    }

    /// <summary>Re-check fold F2: the apply's LAST step (ModsView.cs:194, its only caller in Build 3682). Its early return
    /// is inside the method, so this postfix runs even when nothing was removed.</summary>
    [HarmonyLib.HarmonyPatch]
    public static class Patch_ModDiscoveryRegistry_RemoveDiscoveredSteamMods_ApplyEnd
    {
        static System.Reflection.MethodBase? TargetMethod()
            => HarmonyLib.AccessTools.Method(typeof(global::BigAmbitions.ModsInternal.ModDiscoveryRegistry), "RemoveDiscoveredSteamMods");

        static void Postfix()
        {
            try { MPContentFingerprint.ModsApplyEnded(); } catch { }
        }
    }
}
