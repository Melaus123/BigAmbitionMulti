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
            if (_cachedMods.Length == 0 || UnityEngine.Time.unscaledTime >= _modsRefreshAt)
            {
                _modsRefreshAt = UnityEngine.Time.unscaledTime + 2f;
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
#if BAMP_DEV
        private static readonly System.Collections.Generic.List<string> _fakeMods = new System.Collections.Generic.List<string>();
#endif

        /// <summary>MAIN THREAD ONLY (called from EnsureCached).</summary>
        private static void ComputeMods()
        {
            try
            {
                string m = ListLoadedMods();
                // Review HIGH: the game CLEARS its registry and repopulates it across awaits whenever it
                // re-discovers (window focus after a file change, a mod toggled). A refresh landing in that window
                // must NOT swap the good `mod:` list for folder tokens - a HELLO built then would make every mod
                // differ on both sides, and the host keeps that verdict for the whole lobby. The folder fallback is
                // only right BEFORE a registry list has ever been seen; afterwards the last good value stands.
                if (m.Length == 0 && _hadRegistryList) return;
                if (m.Length > 0) _hadRegistryList = true;
                if (m.Length == 0)
                {
                    // Nothing discovered yet, or the registry could not be read. HasDiscoveredEntries
                    // is false for BOTH "no mods installed" and "the scan has not run", so the two
                    // cannot be told apart — fall back to the installed-FOLDER listing for this pass
                    // and ask the registry again on the next one (2 s), which self-heals as soon as
                    // discovery lands. One line, once per session.
                    if (!_modsFallbackSaid)
                    {
                        _modsFallbackSaid = true;
                        Plugin.Logger.LogInfo("[Mods] the game's mod registry has discovered nothing yet - falling back to the installed-FOLDER list until it does.");
                    }
                    m = MPBugReport.ListInstalledMods();
                }
#if BAMP_DEV
                lock (_fakeMods)
                    foreach (var f in _fakeMods)
                        m = (m.Length == 0 || m == "(none)") ? ("test:" + f) : (m + ", test:" + f);
#endif
                _cachedMods = string.IsNullOrEmpty(m) ? "(none)" : m;   // "(none)" ≠ "" so an empty list still reads as "computed"
            }
            catch { }
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
        /// Returns "" when the registry has discovered nothing (or threw), so the caller can fall
        /// back for that pass; "(none)" when it has discovered entries that yield no token.</summary>
        public static string ListLoadedMods()
        {
            var tokens = new System.Collections.Generic.List<string>();
            int byAsm = 0, byWs = 0, byName = 0, failed = 0;
            try
            {
                if (!global::BigAmbitions.ModsInternal.ModDiscoveryRegistry.HasDiscoveredEntries) return "";
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
                    Plugin.Logger.LogWarning($"[Mods] the game's mod registry could not be read ({ex.GetType().Name}) - the last good list stands, or the installed-FOLDER list if there is none yet.");
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
        internal static void TestClearFakeMods() { lock (_fakeMods) _fakeMods.Clear(); ComputeMods(); }
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
        /// it still does its job on the installed-FOLDER list, which is the fallback used before
        /// discovery has run; against the new tokens it simply matches nothing.</summary>
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
}
