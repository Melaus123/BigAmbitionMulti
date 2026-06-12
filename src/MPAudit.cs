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
            try { (p.BizHash, p.BizCount) = BizTableHash(); } catch { }
            try
            {
                foreach (var addr in GameStatePatcher.ReplicatedInteriorAddresses())
                {
                    if (p.Interiors.Count >= MaxInteriors) break;
                    int? ih = InteriorHash(addr);
                    if (ih != null) p.Interiors.Add(new AddressHashInfo { AddressKey = addr, Hash = ih.Value });
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

        /// <summary>Order-independent hash of the whole business table — the
        /// state BusinessSync keeps mirrored (names, types, owners, closed
        /// flags, retail prices).  XOR-combined per registration so iteration
        /// order can't cause false mismatches.</summary>
        private static (int hash, int count) BizTableHash()
        {
            int acc = 0, count = 0;
            var gi = SaveGameManager.Current;
            if (gi?.BuildingRegistrations == null) return (0, 0);
            foreach (var reg in gi.BuildingRegistrations)
            {
                if (reg == null) continue;
                int h = 17;
                try
                {
                    h = Combine(h, StableHash(GameStateReader.AddressKey(reg)));
                    h = Combine(h, StableHash(reg.BusinessName?.ToString()));
                    h = Combine(h, StableHash(reg.businessTypeName));
                    h = Combine(h, reg.temporarilyClosed ? 1 : 0);
                    h = Combine(h, StableHash(reg.buildingOwnerRivalId));
                    h = Combine(h, StableHash(reg.businessOwnerRivalId));
                    var prices = reg.retailPrices;
                    if (prices != null)
                        for (int i = 0; i < prices.Count; i++)
                        {
                            var rp = prices[i];
                            if (rp == null) continue;
                            h = Combine(h, StableHash(rp.itemName));
                            h = Combine(h, (int)System.Math.Round(rp.price * 100f));
                        }
                }
                catch { continue; }
                acc ^= h;          // order-independent fold
                count++;
            }
            return (acc, count);
        }

        private static int? InteriorHash(string addressKey)
        {
            try
            {
                var snap = InteriorSync.BuildSnapshot(addressKey);
                return snap != null ? InteriorSync.ComputeHash(snap) : (int?)null;
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

        // "playerId/field" → consecutive mismatch count.
        private static readonly Dictionary<string, int> _streaks = new();

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
                Check(p.PlayerId, "biz", bizOk,
                    $"client 0x{p.BizHash:X8}/{p.BizCount} regs vs host 0x{mine.BizHash:X8}/{mine.BizCount} regs");

                bool rosterOk = p.RosterHash == mine.RosterHash;
                Check(p.PlayerId, "roster", rosterOk, $"client 0x{p.RosterHash:X8} vs host 0x{mine.RosterHash:X8}");

                int intOk = 0, intChecked = 0;
                foreach (var ci in p.Interiors)
                {
                    if (string.IsNullOrEmpty(ci.AddressKey)) continue;
                    int? mh = InteriorHash(ci.AddressKey);
                    if (mh == null) continue;   // host can't build it — not comparable
                    intChecked++;
                    if (Check(p.PlayerId, "interior:" + ci.AddressKey, ci.Hash == mh.Value,
                              $"client 0x{ci.Hash:X8} vs host 0x{mh.Value:X8}")) intOk++;
                }

                Plugin.Logger.LogInfo(
                    $"[Audit] '{p.PlayerId}': clock {(clockOk ? "OK" : "DRIFT")} biz {(bizOk ? "OK" : "DIVERGED")} " +
                    $"roster {(rosterOk ? "OK" : "DIVERGED")} interiors {intOk}/{intChecked} OK " +
                    $"(${p.Money:F0}, veh {p.VehicleCount}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Audit] HostHandle: {ex.Message}"); }
        }

        public static void Reset() { _streaks.Clear(); _nextAt = 0f; }
    }
}
