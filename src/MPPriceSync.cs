using System;
using System.Collections.Generic;
using Buildings;
using Entities;
using Helpers;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Live retail-price sync — the economy-coupling feature (2026-06-10).
    ///
    /// The game's price competition is per-neighbourhood (CompetitionHelper)
    /// and runs LOCALLY on every machine over its local registration table.
    /// Business existence/type already syncs, so competitor COUNTS are right
    /// everywhere — but other players' retail PRICES only used to arrive on
    /// interior entry, so the regional price picture drifted between machines.
    ///
    /// Each machine scans the businesses IT runs (RentedByPlayer on the local
    /// save) every few seconds and broadcasts any registration whose price
    /// list changed (hash-gated).  Receivers write the list into their local
    /// copy of that registration — both sims then run the same deterministic
    /// competition math over the same inputs.
    /// </summary>
    public static class MPPriceSync
    {
        /// <summary>DIAGNOSTIC (2026-06-10): temporarily OFF — our hand-built
        /// RetailPrice entries are the prime suspect for the native crash in
        /// the taxi map's ApplyFilters tail (it renders business price info
        /// the normal map doesn't read).  One taxi run with this off is the
        /// decisive experiment; re-enable with a proper entry-construction fix
        /// if confirmed.</summary>
        public static bool Enabled = false;

        private const float ScanSeconds = 5f;

        private static readonly Dictionary<string, int> _lastHash = new();
        // Addresses whose prices arrived from another player — never rebroadcast
        // those as our own (guards against ownership-flag ambiguity on synced
        // foreign businesses).
        private static readonly HashSet<string> _foreignAddrs = new();
        private static float _nextScanAt;

        public static void Reset()
        {
            _lastHash.Clear();
            _foreignAddrs.Clear();
            _nextScanAt = 0f;
        }

        /// <summary>Main thread, while MP active + in game: broadcast changed
        /// price lists of locally-run businesses.</summary>
        public static void Tick()
        {
            if (!Enabled) return;
            if (Time.unscaledTime < _nextScanAt) return;
            _nextScanAt = Time.unscaledTime + ScanSeconds;
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    bool mine;
                    try { mine = reg.RentedByPlayer; } catch { continue; }
                    if (!mine) continue;

                    var prices = reg.retailPrices;
                    if (prices == null || prices.Count == 0) continue;
                    string addr = GameStateReader.AddressKey(reg);
                    if (string.IsNullOrEmpty(addr) || _foreignAddrs.Contains(addr)) continue;

                    int h = 17;
                    unchecked
                    {
                        for (int i = 0; i < prices.Count; i++)
                        {
                            var rp = prices[i];
                            if (rp == null) continue;
                            h = h * 31 + (int)rp.itemName;
                            h = h * 31 + rp.price.GetHashCode();
                        }
                    }
                    if (_lastHash.TryGetValue(addr, out var prev) && prev == h) continue;
                    _lastHash[addr] = h;

                    var p = new RetailPricesPayload { AddressKey = addr, OwnerId = MPConfig.PlayerId };
                    for (int i = 0; i < prices.Count; i++)
                    {
                        var rp = prices[i];
                        if (rp == null) continue;
                        p.Prices.Add(new RetailPriceInfo { ItemName = (int)rp.itemName, Price = rp.price });
                    }

                    if (MPServer.IsRunning)        MPServer.BroadcastRetailPrices(p);
                    else if (MPClient.IsConnected) MPClient.SendRetailPrices(p);
                    Plugin.Logger.LogInfo($"[PriceSync] sent {p.Prices.Count} price(s) for '{addr}'.");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[PriceSync] Tick: {ex.Message}"); }
        }

        /// <summary>Write a received price list into the local registration copy.
        /// MAIN THREAD ONLY (touches IL2CPP game state).</summary>
        public static void Apply(RetailPricesPayload? p)
        {
            try
            {
                if (!Enabled) return;
                if (p == null || string.IsNullOrEmpty(p.AddressKey)) return;
                if (p.OwnerId == MPConfig.PlayerId) return;   // own relay echo
                _foreignAddrs.Add(p.AddressKey);

                var reg = GameStatePatcher.FindRegistration(p.AddressKey);
                if (reg == null || reg.retailPrices == null) return;   // building unknown here (yet)
                reg.retailPrices.Clear();
                foreach (var rp in p.Prices)
                    reg.retailPrices.Add(new RetailPrice { itemName = (BigAmbitions.Items.ItemName)rp.ItemName, price = rp.Price });
                Plugin.Logger.LogInfo($"[PriceSync] applied {p.Prices.Count} price(s) for '{p.AddressKey}' (from {p.OwnerId}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[PriceSync] Apply: {ex.Message}"); }
        }
    }
}
