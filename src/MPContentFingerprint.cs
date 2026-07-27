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

        /// <summary>MAIN THREAD ONLY. Idempotent and cheap after the first successful pass —
        /// called every frame from the UI tick so the value is ready before any connect.</summary>
        public static void EnsureCached()
        {
            if (_cached.Length > 0) return;
            try { Compute(); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Content] fingerprint compute deferred: {ex.Message}"); }
        }

        private static void Compute()
        {
            int items = 0, bizTypes = 0;
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
                // Only publish once BOTH sides produced data — a half-loaded menu-time pass would
                // otherwise cache a bogus value for the whole process and mis-flag every join.
                if (items > 0 && bizTypes > 0)
                {
                    _cached = $"{h:X8}/{items}i/{bizTypes}b";
                    Plugin.Logger.LogInfo($"[Content] fingerprint {_cached} (items={items}, businessTypes={bizTypes}).");
                }
            }
        }

        /// <summary>Host-side: compare a joiner's fingerprint with ours and say so in the host log
        /// when they differ. Never refuses the join. Runs on the NETWORK thread (HandleHello) —
        /// reads the cached value only, never computes.</summary>
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
                Plugin.Logger.LogWarning(
                    $"[Content] MISMATCH: '{playerId}' loaded DIFFERENT game content — theirs={theirs} vs ours={mine}. " +
                    "Same mod + protocol, but the two games' item/business data differ — typically one machine has a " +
                    "pending game update. Expect their market/demand/item-derived numbers to differ from the host's; " +
                    "this is NOT a mod defect.");
            }
            catch { }
        }
    }
}
