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

        /// <summary>Identity of the game BUILD: the main game assembly's module version id ("N" format).
        /// It changes on every game compile — unlike the version folder ("1.0") and the content name hashes,
        /// which the 2026-09-01 Steam update left untouched while changing the save schema. Pure assembly
        /// metadata (no Unity API) — safe on any thread; computed once. "" if unreadable (gate then skips).</summary>
        public static string GameBuildId
        {
            get
            {
                if (_gameBuild != null) return _gameBuild;
                try { _gameBuild = typeof(GameManager).Assembly.ManifestModule.ModuleVersionId.ToString("N"); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Content] game build id unreadable: {ex.Message}"); _gameBuild = ""; }
                return _gameBuild;
            }
        }
        private static volatile string? _gameBuild;

        /// <summary>MAIN THREAD ONLY. Idempotent and cheap after the first successful pass —
        /// called every frame from the UI tick so the value is ready before any connect.</summary>
        public static void EnsureCached()
        {
            if (_cachedMods.Length == 0) ComputeMods();   // round-253: mod list rides the same lifecycle
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
