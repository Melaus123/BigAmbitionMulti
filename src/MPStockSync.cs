using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Per-shop STOCK digest (2026-06-24): the owner of a shop broadcasts the set of its PRICED shelves
    /// that are actually STOCKED (a stock instance with amount &gt; 0).  Every machine keeps the last-known
    /// set per address, so the host's economy floor — via the GetShelfFillState override — can judge an
    /// UN-ENTERED shop's real stock without loading its interior.
    ///
    /// The set is only RECOMPUTED for a shop while its owner is inside it (live itemInstances present);
    /// otherwise the last-known set persists.  That catches the priced-but-empty-shelf exploit (set up
    /// while inside → recorded "not stocked" → skipped by the floor thereafter) and stays roughly right
    /// for legit shops (a sold-out-while-away shop only briefly still counts — no worse than before).
    ///
    /// Mirrors MPPriceSync: a throttled owner scan, change-detected, with a periodic re-assert so a
    /// dropped digest (or a late joiner) self-heals.
    /// </summary>
    public static class MPStockSync
    {
        private const float ScanSeconds     = 5f;
        private const float ReassertSeconds = 30f;

        // addressKey → set of stocked goods item names (kept on EVERY machine).
        private static readonly Dictionary<string, HashSet<string>> _stockByAddr = new();
        private static readonly Dictionary<string, int>   _lastHash   = new();
        private static readonly Dictionary<string, float> _lastSentAt = new();
        private static float _nextScanAt;

        public static void Reset()
        {
            _stockByAddr.Clear();
            _lastHash.Clear();
            _lastSentAt.Clear();
            _nextScanAt = 0f;
        }

        // ── Owner scan: broadcast my shops' stocked sets (main thread, in an MP game) ──
        public static void Tick()
        {
            if (!MPServer.IsRunning && !MPClient.IsConnected) return;
            if (Time.unscaledTime < _nextScanAt) return;
            _nextScanAt = Time.unscaledTime + ScanSeconds;
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    bool mine; try { mine = reg.RentedByPlayer; } catch { continue; }
                    if (!mine) continue;
                    var items = reg.itemInstances;
                    if (items == null || items.Count == 0) continue;   // not inside / no live interior → leave the last-known digest untouched
                    string addr = GameStateReader.AddressKey(reg);
                    if (string.IsNullOrEmpty(addr)) continue;

                    var set = MPPatches.StockedGoodsOf(reg);   // the SAME determination GetShelfFillState uses
                    int h = set.Count;
                    foreach (var n in set) unchecked { h += MPAudit.StableHash(n); }   // order-independent
                    bool changed = !(_lastHash.TryGetValue(addr, out var prev) && prev == h);
                    bool due = !_lastSentAt.TryGetValue(addr, out var at) || (Time.unscaledTime - at) >= ReassertSeconds;
                    if (!changed && !due) continue;
                    _lastHash[addr] = h;
                    _lastSentAt[addr] = Time.unscaledTime;

                    var p = new ShopStockDigestPayload { AddressKey = addr, OwnerId = MPConfig.PlayerId, StockedItems = new List<string>(set) };
                    _stockByAddr[addr] = set;   // apply to our own floor immediately
                    if (MPServer.IsRunning)        MPServer.BroadcastStockDigest(p);
                    else if (MPClient.IsConnected) MPClient.SendStockDigest(p);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[StockSync] Tick: {ex.Message}"); }
        }

        /// <summary>Store a received digest (every machine keeps the last-known set per address).</summary>
        public static void Apply(ShopStockDigestPayload p)
        {
            if (p == null || string.IsNullOrEmpty(p.AddressKey)) return;
            _stockByAddr[p.AddressKey] = new HashSet<string>(p.StockedItems ?? new List<string>());
        }

        /// <summary>True when we hold a stock digest for this address (so the floor can use it vs native).</summary>
        public static bool HasDigest(string addressKey)
            => !string.IsNullOrEmpty(addressKey) && _stockByAddr.ContainsKey(addressKey);

        /// <summary>Is this goods item stocked on a shelf in this shop, per the last-known digest?</summary>
        public static bool IsStocked(string addressKey, string itemName)
            => !string.IsNullOrEmpty(addressKey) && _stockByAddr.TryGetValue(addressKey, out var set) && set.Contains(itemName);

        /// <summary>Join replay (Class 4): every current digest, for a connecting peer.</summary>
        public static List<ShopStockDigestPayload> AllDigests()
        {
            var outList = new List<ShopStockDigestPayload>();
            foreach (var kv in _stockByAddr)
                outList.Add(new ShopStockDigestPayload { AddressKey = kv.Key, StockedItems = new List<string>(kv.Value) });
            return outList;
        }
    }
}
