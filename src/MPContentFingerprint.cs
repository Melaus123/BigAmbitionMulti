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
            if (_cachedMods.Length == 0) ComputeMods();   // round-253: mod list rides the same lifecycle
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

        // ── Round-253: installed-mod list, cached at startup beside the fingerprint ──
        // (Hello is built on the NETWORK thread; the directory scan touches
        // Application.persistentDataPath — main-thread-only by contract, same hazard the
        // fingerprint compute hit on 2026-07-27. EnsureCached runs on the UI tick.)
        private static volatile string _cachedMods = "";
        public static string CachedMods => _cachedMods;

        /// <summary>MAIN THREAD ONLY (called from EnsureCached).</summary>
        private static void ComputeMods()
        {
            try
            {
                string m = MPBugReport.ListInstalledMods();
                _cachedMods = string.IsNullOrEmpty(m) ? "(none)" : m;   // "(none)" ≠ "" so an empty list still reads as "computed"
            }
            catch { }
        }

#if BAMP_DEV
        /// <summary>TestDrive fixture (round-253): append an imitation mod to the CACHED
        /// list the Hello handshake sends. Dev builds only; see the 'fakemod' verb.</summary>
        internal static void TestAddFakeMod(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            _cachedMods = (_cachedMods.Length == 0 || _cachedMods == "(none)") ? $"test:{name}" : $"{_cachedMods}, test:{name}";
        }
        internal static void TestClearFakeMods() { _cachedMods = ""; ComputeMods(); }
#endif

        /// <summary>Diff two comma-separated mod lists (the report.md InstalledMods format).
        /// Returns true when they differ, with capped human-readable only-lists.
        /// Round-258: entries tagged "layout:" (shared building-layout blueprints, pure
        /// save-side templates) are excluded from the comparison — they cannot desync
        /// gameplay, and comparing them made every layout subscriber trip mismatch
        /// warnings against non-subscribers (rig 2026-08-15, workshop:3428251077).</summary>
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
