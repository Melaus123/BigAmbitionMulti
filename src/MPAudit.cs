using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Periodic cross-machine state audit (2026-06-12, user request: catch
    /// silent divergence instead of waiting for someone to SEE it).
    ///
    /// Every cycle each CLIENT hashes its shared world state (clock, the full
    /// business table, lobby roster, interior replicas it holds) and sends an
    /// AuditReport to the host.  The host rebuilds the same hashes from ITS
    /// state and compares: a field that mismatches on two CONSECUTIVE cycles
    /// (transient mid-sync noise is expected) logs a loud "[Audit] MISMATCH"
    /// line.  Matching cycles log one compact OK line so absence of evidence
    /// is visible too.  Money and vehicle counts legitimately differ per
    /// machine — they ride along report-only.
    ///
    /// All hashes are FNV-1a over the SAME fields on both machines — never
    /// string.GetHashCode (runtime-dependent).
    /// </summary>
    public static class MPAudit
    {
        private const float IntervalSeconds = 30f;
        private const int   MaxInteriors    = 8;     // bound per-cycle snapshot cost
        private const int   MaxOwnInteriors = 40;    // round-104: one int per own shop — cheap, but still bounded
        private const int   StreakToReport  = 2;     // consecutive mismatches before alarm

        private static float _nextAt;

        // ── Stable hashing (FNV-1a) ───────────────────────────────────────────

        public static int StableHash(string? s)
        {
            unchecked
            {
                uint h = 2166136261;
                if (s != null)
                    for (int i = 0; i < s.Length; i++)
                    { h ^= s[i]; h *= 16777619; }
                return (int)h;
            }
        }

        private static int Combine(int h, int v) { unchecked { return h * 31 + v; } }

        // ── Report construction (both roles, main thread) ─────────────────────

        public static AuditReportPayload BuildReport()
        {
            var p = new AuditReportPayload { PlayerId = MPConfig.PlayerId };
            try
            {
                var (d, h) = GameStateReader.GetGameTime();
                p.Day = d; p.Hour = h;
            }
            catch { }
            try { p.Money = SaveGameManager.Current?.Money ?? 0f; } catch { }
            try { p.VehicleCount = SaveGameManager.Current?.VehicleInstances?.Count ?? 0; } catch { }
            try { p.RosterHash = RosterHash(); } catch { }
            try { (p.BizHash, p.BizCount, p.BizBuckets) = BizTableHash(); } catch { }
            try
            {
                var ev = SaveGameManager.Current?.marketEvents;
                if (ev != null) p.MarketEventsHash = StableHash(Newtonsoft.Json.JsonConvert.SerializeObject(ev));
            }
            catch { }
            try
            {
                // Round-278/F5 (field 20260818-222130): audit ONLY the address currently being
                // live-fed — the building we are inside.  Replicas of buildings we left are
                // frozen BY DESIGN (we unsubscribed); that bundle's loudest "mismatches" were
                // healthy stale replicas drowning out the real signal.
                string liveAddr = "";
                try { liveAddr = MPRegisterSync.CurrentShopAddress ?? ""; } catch { }
                foreach (var addr in GameStatePatcher.ReplicatedInteriorAddresses())
                {
                    if (addr != liveAddr) continue;
                    if (p.Interiors.Count >= MaxInteriors) break;
                    int? ih = InteriorHash(addr);
                    if (ih != null) p.Interiors.Add(new AddressHashInfo { AddressKey = addr, Hash = ih.Value, StructHash = InteriorStructHash(addr) ?? 0 });
                }
            }
            catch { }
            // Round-104: MY OWN businesses + their item counts. One int each — the whole point is
            // to stay small enough to ride every 30s audit while still catching "my shop is empty
            // here but full on the host", the exact divergence that went unseen in the field.
            try
            {
                var gi2 = SaveGameManager.Current;
                if (gi2?.BuildingRegistrations != null)
                    foreach (var reg in gi2.BuildingRegistrations)
                    {
                        if (reg == null || p.OwnInteriors.Count >= MaxOwnInteriors) continue;
                        bool mine; try { mine = MergerFlip.TrulyMine(reg); } catch { continue; }
                        if (!mine) continue;
                        string type; try { type = reg.businessTypeName ?? ""; } catch { continue; }
                        if (type.Length == 0 || type == "ba:businesstype_empty") continue;
                        p.OwnInteriors.Add(new AddressCountInfo
                        {
                            AddressKey = GameStateReader.AddressKey(reg),
                            Items      = reg.itemInstances?.Count ?? 0,
                        });
                    }
            }
            catch { }
            return p;
        }

        private static int RosterHash()
        {
            var names = new List<string>(MPRestSync.AllPlayers());
            names.Sort(StringComparer.Ordinal);
            int h = 17;
            foreach (var n in names) h = Combine(h, StableHash(n));
            return h;
        }

        /// <summary>Hash of ONE registration's mirrored business state (names,
        /// types, owners, closed flag, retail prices).</summary>
        private static int RegHash(BuildingRegistration reg)
        {
            int h = 17;
            h = Combine(h, StableHash(GameStateReader.AddressKey(reg)));
            h = Combine(h, StableHash(reg.BusinessName?.ToString()));
            h = Combine(h, StableHash(reg.businessTypeName));
            h = Combine(h, reg.temporarilyClosed ? 1 : 0);
            // Round-101 (round-89 drill, first field firing, proved this): the rival-owner fields
            // are INTENTIONALLY different per machine — a session player's business is TRANSLATED
            // to rival-owned on every other machine, carrying the owner's id as the rival id
            // (GameStatePatcher ownership apply, "TRANSLATED, not copied"). Hashing them made
            // EVERY player-owned business diverge forever, so "biz DIVERGED" fired on healthy
            // sessions and the alarm carried no information. Hash the OWNERSHIP FACT instead:
            // "is this owned by some session player" — identical on all machines by design.
            // (Ownership attribution itself is audited by the roster/business snapshot paths.)
            bool anyPlayerOwned = false;
            try
            {
                // Round-204 (rig: DIVERGED '36 thirdstreet' after a client bought a
                // rival-tenanted building): the ledger arm of IsAnyPlayerBusiness is
                // HOST-only, so a client-bought building read "player-owned" on the host
                // but "AI-owned" on every client — different hash COMPOSITION (rival ids
                // hashed on one side only), a guaranteed false alarm. The ownership fact
                // must include the DEED dimension from each machine's own viewpoint:
                // native realEstate for my own purchase, the translated session-player
                // id in the deed field for everyone else's.
                anyPlayerOwned = reg.RentedByPlayer || GameStatePatcher.IsAnyPlayerBusiness(reg)
                    || reg.BuildingOwnedByPlayer
                    || GameStatePatcher.IsSessionPlayerId(reg.buildingOwnerRivalId?.ToString() ?? "");
            }
            catch { }
            h = Combine(h, anyPlayerOwned ? 1 : 0);
            // Rival ids still hashed for genuinely AI-owned buildings (a real divergence there
            // means the rival sim disagrees) — but skipped when a player owns it.
            if (!anyPlayerOwned)
            {
                h = Combine(h, StableHash(reg.buildingOwnerRivalId));
                h = Combine(h, StableHash(reg.businessOwnerRivalId));
            }
            var prices = reg.retailPrices;
            if (prices != null)
                for (int i = 0; i < prices.Count; i++)
                {
                    var rp = prices[i];
                    if (rp == null) continue;
                    h = Combine(h, StableHash(rp.itemName));
                    h = Combine(h, (int)System.Math.Round(rp.price * 100f));
                }
            return h;
        }

        private static int BucketOf(BuildingRegistration reg)
            => StableHash(GameStateReader.AddressKey(reg)) & 15;

        /// <summary>Order-independent hash of the whole business table, plus 16
        /// per-bucket sub-hashes (bucket = address hash & 15) so a divergence
        /// can be localized to ~1/16th of the city — and from there to the
        /// exact registration via the drill log.</summary>
        private static (int hash, int count, List<int> buckets) BizTableHash()
        {
            int acc = 0, count = 0;
            var buckets = new int[16];
            var gi = SaveGameManager.Current;
            if (gi?.BuildingRegistrations == null) return (0, 0, new List<int>(new int[16]));
            foreach (var reg in gi.BuildingRegistrations)
            {
                if (reg == null) continue;
                int h;
                int b;
                try { h = RegHash(reg); b = BucketOf(reg); }
                catch { continue; }
                acc ^= h;          // order-independent fold
                buckets[b] ^= h;
                count++;
            }
            return (acc, count, new List<int>(buckets));
        }

        // LogBizDrill (the per-registration "[Audit] drill bN: ..." dump, ~340 lines per
        // machine per episode) REMOVED round-291 (user-approved 2026-08-21): it predates the
        // round-89 reply diff and was only useful if BOTH machines' logs were in hand, which
        // a bug bundle never provides. BuildDrillRows + HostHandleDrillReply carry the answer.

        /// <summary>Round-89: the drill rows a CLIENT sends back — per-reg hash + the same
        /// summary the host diff prints, for the diverged bucket(s).</summary>
        public static List<DrillRegRow> BuildDrillRows(List<int>? bucketIds)
        {
            var rows = new List<DrillRegRow>();
            try
            {
                if (bucketIds == null || bucketIds.Count == 0) return rows;
                var want = new HashSet<int>(bucketIds);
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return rows;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    try
                    {
                        if (!want.Contains(BucketOf(reg))) continue;
                        rows.Add(new DrillRegRow
                        {
                            AddressKey = GameStateReader.AddressKey(reg),
                            Hash       = RegHash(reg),
                            Summary    = $"biz='{reg.BusinessName}' type={reg.businessTypeName} prices={reg.retailPrices?.Count ?? 0} bizOwner='{reg.businessOwnerRivalId}'",
                        });
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Audit] drill rows: {ex.Message}"); }
            return rows;
        }

        /// <summary>Round-89, host main thread: diff a client's drill rows against our own
        /// registrations and NAME the diverging address(es) — one machine's log now carries
        /// the whole answer (field reports never include both sides).</summary>
        public static void HostHandleDrillReply(AuditDrillReplyPayload? p)
        {
            if (!MPServer.IsRunning || p == null || p.Regs == null || p.Regs.Count == 0) return;
            try
            {
                var mine = new Dictionary<string, (int hash, string summary)>(StringComparer.Ordinal);
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations != null)
                    foreach (var reg in gi.BuildingRegistrations)
                    {
                        if (reg == null) continue;
                        try
                        {
                            mine[GameStateReader.AddressKey(reg)] =
                                (RegHash(reg), $"biz='{reg.BusinessName}' type={reg.businessTypeName} prices={reg.retailPrices?.Count ?? 0} bizOwner='{reg.businessOwnerRivalId}'");
                        }
                        catch { }
                    }

                int matched = 0, diverged = 0, missingHere = 0, logged = 0;
                const int LogCap = 16;
                foreach (var row in p.Regs)
                {
                    if (row == null || string.IsNullOrEmpty(row.AddressKey)) continue;
                    if (!mine.TryGetValue(row.AddressKey, out var h))
                    {
                        missingHere++;
                        if (logged++ < LogCap)
                            Plugin.Logger.LogWarning($"[Audit] DIVERGED '{p.PlayerId}' reg '{row.AddressKey}': present on client ({row.Summary}) but NOT on host.");
                        continue;
                    }
                    if (h.hash == row.Hash) { matched++; continue; }
                    diverged++;
                    if (logged++ < LogCap)
                        Plugin.Logger.LogWarning($"[Audit] DIVERGED '{p.PlayerId}' reg '{row.AddressKey}': client 0x{row.Hash:X8} ({row.Summary}) vs host 0x{h.hash:X8} ({h.summary}).");
                }
                Plugin.Logger.LogWarning(
                    $"[Audit] drill diff for '{p.PlayerId}': {diverged} diverged, {matched} matched, {missingHere} client-only of {p.Regs.Count} row(s){(logged > LogCap ? $" (first {LogCap} logged)" : "")}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Audit] drill diff: {ex.Message}"); }
        }

        /// <summary>Audit-stable interior hash: layout + designs + price table +
        /// item structure/stock.  EXCLUDES dirt (evolves continuously on the
        /// source; a replica is always one broadcast behind → permanent false
        /// positive) and cargo PricePerUnit (the receiver deliberately re-stamps
        /// it from the synced store table — by-design divergence).</summary>
        private static int? InteriorHash(string addressKey)
        {
            try
            {
                var snap = InteriorSync.BuildSnapshot(addressKey);
                if (snap == null) return null;
                unchecked
                {
                    int h = 17;
                    h = Combine(h, StableHash(snap.Layout));
                    foreach (var d in snap.InteriorDesigns)
                    {
                        h = Combine(h, StableHash(d.UUID));
                        foreach (var m in d.Materials)
                        {
                            h = Combine(h, StableHash(m.MaterialID));
                            h = Combine(h, m.MaterialIndex);
                            h = Combine(h, m.ColorIndex);
                        }
                    }
                    foreach (var rp in snap.RetailPrices)
                    {
                        h = Combine(h, StableHash(rp.ItemName));
                        h = Combine(h, (int)System.Math.Round(rp.Price * 100f));
                    }
                    foreach (var it in snap.ItemInstances)
                    {
                        h = Combine(h, StableHash(it.Id));
                        h = Combine(h, StableHash(it.ItemName));
                        h = Combine(h, (int)System.Math.Round(it.Px * 100f));
                        h = Combine(h, (int)System.Math.Round(it.Py * 100f));
                        h = Combine(h, (int)System.Math.Round(it.Pz * 100f));
                        h = Combine(h, it.StateIndex);
                        foreach (var c in it.CargoInstances)
                        {
                            h = Combine(h, StableHash(c.ItemName));
                            h = Combine(h, c.Amount);
                        }
                    }
                    return h;
                }
            }
            catch { return null; }
        }

        /// <summary>Round-278/F5: the STRUCTURAL half of InteriorHash — identical in every way
        /// except the cargo loop is omitted, so till churn (sales changing amounts) does not move
        /// it.  MUST mirror InteriorHash field-for-field; edit them together.</summary>
        private static int? InteriorStructHash(string addressKey)
        {
            try
            {
                var snap = InteriorSync.BuildSnapshot(addressKey);
                if (snap == null) return null;
                unchecked
                {
                    int h = 17;
                    h = Combine(h, StableHash(snap.Layout));
                    foreach (var d in snap.InteriorDesigns)
                    {
                        h = Combine(h, StableHash(d.UUID));
                        foreach (var m in d.Materials)
                        {
                            h = Combine(h, StableHash(m.MaterialID));
                            h = Combine(h, m.MaterialIndex);
                            h = Combine(h, m.ColorIndex);
                        }
                    }
                    foreach (var rp in snap.RetailPrices)
                    {
                        h = Combine(h, StableHash(rp.ItemName));
                        h = Combine(h, (int)System.Math.Round(rp.Price * 100f));
                    }
                    foreach (var it in snap.ItemInstances)
                    {
                        h = Combine(h, StableHash(it.Id));
                        h = Combine(h, StableHash(it.ItemName));
                        h = Combine(h, (int)System.Math.Round(it.Px * 100f));
                        h = Combine(h, (int)System.Math.Round(it.Py * 100f));
                        h = Combine(h, (int)System.Math.Round(it.Pz * 100f));
                        h = Combine(h, it.StateIndex);
                    }
                    return h;
                }
            }
            catch { return null; }
        }

        // ── Client tick ───────────────────────────────────────────────────────

        /// <summary>Main thread, once per frame while in-game.  Clients send a
        /// report every cycle; the host is the comparison reference and sends
        /// nothing.</summary>
        public static void Tick()
        {
            if (!MPClient.IsConnected || MPServer.IsRunning) return;
            if (Time.unscaledTime < _nextAt) return;
            _nextAt = Time.unscaledTime + IntervalSeconds;
            try
            {
                var p = BuildReport();
                MPClient.SendAuditReport(p);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Audit] tick: {ex.Message}"); }
        }

        // ── Host comparison ───────────────────────────────────────────────────

        // playerId → last self-heal snapshot push (ms) — divergence heal throttle.
        private static readonly Dictionary<string, long> _lastHealMs = new();

        // "playerId/field" → consecutive mismatch count.
        private static readonly Dictionary<string, int> _streaks = new();

        // "playerId" → next Time.unscaledTime the routine all-OK summary may log.
        // Release noise control (2026-06-24): when everything matches, the summary
        // logs only as a ~5-min heartbeat so it doesn't dilute the bug-report ring
        // buffer; a streak-confirmed divergence bypasses this and logs every cycle.
        private static readonly Dictionary<string, float> _nextSummaryAt = new();
        private const float SummaryHeartbeatSeconds = 300f;

        private static bool Check(string player, string field, bool match, string detail)
        {
            string key = player + "/" + field;
            if (match) { _streaks.Remove(key); return true; }
            int n = (_streaks.TryGetValue(key, out var v) ? v : 0) + 1;
            _streaks[key] = n;
            // Alarm on the Nth consecutive mismatch (then every 4th — no spam).
            if (n == StreakToReport || (n > StreakToReport && (n - StreakToReport) % 4 == 0))
                Plugin.Logger.LogWarning($"[Audit] MISMATCH '{player}' {field} (x{n}): {detail}");
            return false;
        }

        /// <summary>Host, main thread: compare a client's report to our state.</summary>
        public static void HostHandle(AuditReportPayload? p)
        {
            if (!MPServer.IsRunning || p == null || string.IsNullOrEmpty(p.PlayerId)) return;
            try
            {
                var mine = BuildReport();

                double cTotal = p.Day * 24.0 + p.Hour;
                double hTotal = mine.Day * 24.0 + mine.Hour;
                bool clockOk = System.Math.Abs(cTotal - hTotal) <= 0.1;   // 6 game-minutes
                Check(p.PlayerId, "clock", clockOk,
                    $"client day {p.Day} {p.Hour:F2}h vs host day {mine.Day} {mine.Hour:F2}h");

                bool bizOk = p.BizHash == mine.BizHash && p.BizCount == mine.BizCount;
                bool bizAlarmed = !Check(p.PlayerId, "biz", bizOk,
                    $"client 0x{p.BizHash:X8}/{p.BizCount} regs vs host 0x{mine.BizHash:X8}/{mine.BizCount} regs")
                    && _streaks.TryGetValue(p.PlayerId + "/biz", out var bizStreak) && bizStreak == StreakToReport;
                if (bizAlarmed && p.BizBuckets != null && p.BizBuckets.Count == 16 && mine.BizBuckets.Count == 16)
                {
                    // Localize: which 16th(s) of the table diverged → ask the client
                    // for its per-registration hashes in those buckets; the reply diff
                    // (HostHandleDrillReply) names the exact address(es) in THIS log.
                    // Round-291 (user-approved 2026-08-21): the per-registration dumps
                    // both machines used to write (~340 lines each) are gone — a bundle
                    // carries one log, so a one-sided dump was never diffable.
                    var diverged = new List<int>();
                    for (int b = 0; b < 16; b++)
                        if (p.BizBuckets[b] != mine.BizBuckets[b]) diverged.Add(b);
                    Plugin.Logger.LogWarning($"[Audit] biz divergence localized to bucket(s): {string.Join(",", diverged)} — requesting the client's rows (the drill diff below names the address(es)).");
                    MPServer.SendAuditDrill(p.PlayerId, diverged);
                    // SELF-HEAL (2026-07-20, severity review of the silent-loss class):
                    // detection alone left the client divergent until luck of the delta
                    // traffic — a CONFIRMED divergence now pushes a fresh business
                    // snapshot (fragmentation makes it deliverable regardless of size).
                    // Throttled per player so a persistent mismatch can't spam.
                    long nowMs = System.DateTime.UtcNow.Ticks / System.TimeSpan.TicksPerMillisecond;
                    if (!_lastHealMs.TryGetValue(p.PlayerId, out var lh) || nowMs - lh > 120_000)
                    {
                        _lastHealMs[p.PlayerId] = nowMs;
                        bool sent = MPServer.SendBusinessSnapshotToPlayer(p.PlayerId);
                        Plugin.Logger.LogWarning($"[Audit] divergence self-heal: business snapshot {(sent ? "re-sent to" : "COULD NOT be sent to (no link)")} '{p.PlayerId}'.");
                    }
                }

                bool rosterOk = p.RosterHash == mine.RosterHash;
                Check(p.PlayerId, "roster", rosterOk, $"client 0x{p.RosterHash:X8} vs host 0x{mine.RosterHash:X8}");

                bool eventsOk = p.MarketEventsHash == mine.MarketEventsHash;
                Check(p.PlayerId, "marketEvents", eventsOk,
                    $"client 0x{p.MarketEventsHash:X8} vs host 0x{mine.MarketEventsHash:X8}");

                int intOk = 0, intChecked = 0;
                foreach (var ci in p.Interiors)
                {
                    if (string.IsNullOrEmpty(ci.AddressKey)) continue;
                    int? mh = InteriorHash(ci.AddressKey);
                    if (mh == null) continue;   // host can't build it — not comparable
                    intChecked++;
                    bool intMatch = ci.Hash == mh.Value;
                    string detail = $"client 0x{ci.Hash:X8} vs host 0x{mh.Value:X8}";
                    // Round-278/F5: when the client sent the structural half (new builds), a
                    // mismatch line says WHICH kind — cargo churn (benign lag) or structure.
                    if (!intMatch && ci.StructHash != 0)
                    {
                        int? sh = InteriorStructHash(ci.AddressKey);
                        if (sh != null) detail += ci.StructHash == sh.Value
                            ? " | struct MATCH (till churn — cargo only)"
                            : " | struct DIFF (structural divergence)";
                    }
                    if (Check(p.PlayerId, "interior:" + ci.AddressKey, intMatch, detail)) intOk++;
                }

                // Round-104: the client's OWN shops vs ours. Compares item COUNTS only, and emits
                // at most ONE line naming at most 4 addresses — enough to identify the shops without
                // bloating the ring buffer. This is the check that was missing when a client's four
                // shops were emptied while the audit reported "interiors 0/0 OK" (field 2026-07-27).
                int ownChecked = 0, ownDiverged = 0, clientItems = 0, hostItems = 0;
                var ownDetail = new System.Text.StringBuilder();
                try
                {
                    foreach (var oi in p.OwnInteriors)
                    {
                        if (string.IsNullOrEmpty(oi.AddressKey)) continue;
                        var reg = GameStatePatcher.FindRegistration(oi.AddressKey);
                        if (reg == null) continue;   // not comparable here
                        int hostCount = reg.itemInstances?.Count ?? 0;
                        ownChecked++; clientItems += oi.Items; hostItems += hostCount;
                        if (oi.Items == hostCount) continue;
                        ownDiverged++;
                        if (ownDiverged <= 4) ownDetail.Append($" '{oi.AddressKey}' {oi.Items}/{hostCount}");
                    }
                }
                catch { }
                if (ownDiverged > 0)
                    Plugin.Logger.LogWarning(
                        $"[Audit] MISMATCH '{p.PlayerId}' own-interiors: {ownDiverged} of {ownChecked} shop(s) differ " +
                        $"(items client {clientItems} vs host {hostItems}) —{ownDetail}" +
                        (ownDiverged > 4 ? $" (+{ownDiverged - 4} more)" : "") +
                        (clientItems == 0 && hostItems > 0
                            ? "  ← client holds NOTHING for its own shops: its character save disagrees with this session's ownership ledger."
                            : ""));

                // Throttle the routine summary (see _nextSummaryAt): log every cycle
                // while a divergence is streak-confirmed, otherwise only as a periodic
                // heartbeat so normal play doesn't crowd the bug-report ring buffer.
                bool alarmed = false;
                string streakPrefix = p.PlayerId + "/";
                foreach (var kv in _streaks)
                    if (kv.Value >= StreakToReport && kv.Key.StartsWith(streakPrefix, StringComparison.Ordinal)) { alarmed = true; break; }
                bool logSummary = alarmed;
                if (!logSummary)
                {
                    float now = Time.unscaledTime;
                    if (!_nextSummaryAt.TryGetValue(p.PlayerId, out var nextAt) || now >= nextAt)
                    { _nextSummaryAt[p.PlayerId] = now + SummaryHeartbeatSeconds; logSummary = true; }
                }
                if (logSummary)
                    Plugin.Logger.LogInfo(
                        $"[Audit] '{p.PlayerId}': clock {(clockOk ? "OK" : "DRIFT")} biz {(bizOk ? "OK" : "DIVERGED")} " +
                        $"roster {(rosterOk ? "OK" : "DIVERGED")} events {(eventsOk ? "OK" : "DIVERGED")} interiors {intOk}/{intChecked} OK " +
                        $"own-shops {ownChecked - ownDiverged}/{ownChecked} OK " +
                        $"(${p.Money:F0}, veh {p.VehicleCount}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Audit] HostHandle: {ex.Message}"); }
        }

        public static void Reset() { _streaks.Clear(); _nextSummaryAt.Clear(); _nextAt = 0f; }
    }
}
