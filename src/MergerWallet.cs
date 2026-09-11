using System;
using HarmonyLib;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Shared wallet — merger slice 4 (.modding/03-systems/company-merger-map.md §11, user-ratified:
    /// money is ONE COMBINED entity for a merged company).
    ///
    /// Model: the HOST holds one authoritative balance per merger group (delta ledger). Every member's
    /// local `SaveGameManager.Current.Money` is a MIRROR of that balance — which is exactly why every
    /// native UI, overdraft gate, and stat keeps working untouched: they all read the local Money.
    ///
    /// Flow per native money change on a member machine:
    ///   native ChangeMoney applies locally (responsiveness, transactions, tax tracking — all native,
    ///   all ONCE, on the originating machine) → postfix forwards the DELTA + transaction key to the
    ///   host → host ledger += delta → host broadcasts the balance → members set mirror = balance.
    ///   Absolutes are never a source of truth (lost-update class); the sender's own local apply is
    ///   confirmed, not doubled, by the reconcile (the ledger already includes its delta).
    ///
    /// Merge-time pooling: on the membership rising edge each member contributes its ENTIRE personal
    /// wallet once (host dedupes by StableId — restore/join replays can't double-pool). Leave/dissolve
    /// pays each departing member an equal share (balance / member count at that moment).
    ///
    /// INERT: not a merger member → the postfix is a pass-through; SP doubly inert (no session).
    /// [EconProbe] lines are PERMANENT (user contract) — the wallet's runtime tripwire.
    /// </summary>
    public static class MergerWallet
    {
        /// <summary>True while a reconcile is writing the mirror — the ChangeMoney postfix must not
        /// forward deltas the wallet itself caused (there are none today — reconcile writes the field
        /// directly — but the guard makes that structural).</summary>
        private static bool _applyingReconcile;

        // ── Outbound: native money changes → host ledger ──────────────────────

        /// <summary>The single runtime chokepoint: every native mutation (ChangeMoneySafe, cheats,
        /// force paths) funnels through private GameManager.ChangeMoney (:1249). Postfix = the native
        /// local apply already happened; forward the delta.</summary>
        [HarmonyPatch]
        public static class Patch_GameManager_ChangeMoney_WalletForward
        {
            static System.Reflection.MethodBase TargetMethod()
            {
                var m = AccessTools.Method(typeof(GameManager), "ChangeMoney");
                if (m == null) Plugin.Logger.LogError("[Wallet] GameManager.ChangeMoney NOT FOUND — shared wallet inert (game update?).");
                return m;
            }

            static void Postfix(float amount, TransactionInfo transactionInfo)
            {
                try
                {
                    if (amount == 0f || _applyingReconcile) return;
                    if (!MergerSync.IAmMember) return;
                    // PHASE 4b (T1): the record the native path has just enqueued rides this same
                    // forward - the shared feed needs no message of its own on this leg.
                    Forward(amount, transactionInfo?.Type ?? "", contribution: false, CompanyFeed.CaptureOwn());
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Wallet] forward: {ex.Message}"); }
            }
        }

        // The exactly-two runtime bypasses that write Current.Money directly (compat force-sale
        // credits; decompile-verified 2026-07-07: BuildingHelper.SellBuilding :539, RealEstateHelper.
        // SellBuildingForCompat :186 — both 'ba:transaction_compatibilityfix', neither routes through
        // ChangeMoney). Unmirrored, the credit would be erased by the next reconcile. Money-diff
        // capture (prefix/postfix) forwards exactly what the method did; neither nests ChangeMoney
        // (read at the line), so nothing double-forwards.
        [HarmonyPatch(typeof(Helpers.BuildingHelper), nameof(Helpers.BuildingHelper.SellBuilding))]
        public static class Patch_BuildingHelper_SellBuilding_WalletForward
        {
            static void Prefix(out float __state) => __state = MoneyNow();
            static void Postfix(float __state)    => ForwardMoneyDiff(__state, "compat force-sale (building)");
        }

        // Explicit argument types (2026-07-09, own-log catch): SellBuildingForCompat has THREE
        // overloads — the name-only patch failed "Ambiguous match" and the wallet bypass guard was
        // silently NOT applied. The (float, Address) overload is the one that writes Money directly;
        // the other two funnel into it.
        // 1.0 retyped the price parameter float -> double (2026-08-29). The old typeof(float) still
        // BOUND, because .NET's default binder widens float->double when only one candidate matches —
        // but that is leniency, not a contract, and would turn ambiguous the moment another overload
        // appears. Declared exactly now. The pre/postfix only snapshot Money, so behaviour is unchanged.
        [HarmonyPatch(typeof(Helpers.RealEstateHelper), "SellBuildingForCompat", typeof(double), typeof(Address))]
        public static class Patch_RealEstateHelper_SellForCompat_WalletForward
        {
            static void Prefix(out float __state) => __state = MoneyNow();
            static void Postfix(float __state)    => ForwardMoneyDiff(__state, "compat force-sale (real estate)");
        }

        private static float MoneyNow()
        { try { return SaveGameManager.Current?.Money ?? 0f; } catch { return 0f; } }

        private static void ForwardMoneyDiff(float before, string key)
        {
            try
            {
                if (!MergerSync.IAmMember) return;
                float diff = MoneyNow() - before;
                if (Math.Abs(diff) < 0.005f) return;
                // m5 (review r2 of 4b): these two paths enqueue a native Transaction of their own -
                // 'SaveGameManager.Current.Transactions.Enqueue(new Transaction(info)' at
                // BuildingHelper.cs:645 and RealEstateHelper.cs:270, each one line after its
                // 'SaveGameManager.Current.Money += num'.  The enqueue PRECEDES the method's return, so
                // this POSTFIX sees it as the queue's newest entry and CaptureOwn shares it.  Without
                // this the compat force-sale moved the shared wallet but was the one movement no
                // partner ever saw.  (Neither path calls ChangeMoney, so nothing double-forwards.)
                Forward(diff, key, contribution: false, CompanyFeed.CaptureOwn());
            }
            catch { }
        }

        /// <summary>Membership edge (called by MergerSync.ApplyState on EVERY machine): on the rising
        /// edge, pool my whole personal wallet into the company. Reads Money synchronously AT the edge —
        /// before any wallet broadcast can have overwritten the mirror — so the pooled figure is always
        /// the member's true pre-merge cash. Idempotent at the host (contributed-set, manifest-persisted).</summary>
        public static void OnMembershipEdge(bool now, bool was)
        {
            try
            {
                if (!now || was) return;   // rising edge only
                float mine = MoneyNow();
                Plugin.Logger.LogInfo($"[EconProbe] wallet POOL: contributing my ${mine:N0} to the merged company.");
                Forward(mine, "merger_pool", contribution: true);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Wallet] pool: {ex.Message}"); }
        }

        /// <summary>MOD-SIDE money writes that bypass native ChangeMoney (MPHub.ApplyMoneyDelta —
        /// gifts/loans/purchase relay — and GameStatePatcher.RollbackBuy's refund) call this beside
        /// their local apply, or the next reconcile would erase them. No-op when not merged.</summary>
        public static void ForwardExternal(float amount, string key)
        {
            try
            {
                if (amount == 0f || !MergerSync.IAmMember) return;
                Forward(amount, key, contribution: false);
            }
            catch { }
        }

        private static void Forward(float amount, string key, bool contribution, PwTransaction? tx = null)
        {
            Plugin.Logger.LogInfo($"[EconProbe] wallet Δ{(amount >= 0 ? "+" : "")}{amount:N0} '{key}'{(contribution ? " (pool)" : "")} → host ledger.");
            if (MPServer.IsRunning)
            {
                MPServer.HostWalletDelta(MPConfig.PlayerId, amount, key, contribution);
                // PHASE 4b (T1): the host is a member too - its own record goes straight into the ring
                // and the fan-out, with no envelope to itself.
                if (tx != null) CompanyFeed.HostIngest(tx, MPConfig.PlayerId);
            }
            else if (MPClient.IsConnected)
                MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.MergerWalletDelta, MPConfig.PlayerId,
                    new MergerWalletDeltaPayload { PlayerId = MPConfig.PlayerId, Amount = amount, Key = key, Contribution = contribution, Tx = tx }));
        }

        // ── Inbound: authoritative balance → local mirror ─────────────────────

        /// <summary>A wallet broadcast arrived. Group-tagged states apply only when they name MY group;
        /// GroupId="" is a targeted personal set (leave payout) and applies unconditionally.</summary>
        public static void ApplyState(MergerWalletStatePayload p)
        {
            try
            {
                if (p == null) return;
                if (!string.IsNullOrEmpty(p.GroupId))
                {
                    if (!MergerSync.IAmMember || p.GroupId != MergerSync.MyGroupId) return;   // another company's (or stale) state
                    SetMirror(p.Balance, "company balance");
                }
                else
                    SetMirror(p.Balance, "leave payout — personal wallet");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Wallet] apply: {ex.Message}"); }
        }

        /// <summary>Write the mirror. Direct field set (the proven MPHub.ApplyMoneyDelta pattern) —
        /// NEVER through ChangeMoney: that would re-run TaxHelper.TrackTransaction and the transaction
        /// log for money whose origin machine already recorded both (the §11 double-count class).</summary>
        private static void SetMirror(float balance, string why)
        {
            var gi = SaveGameManager.Current;
            if (gi == null) return;
            float diff = balance - gi.Money;
            if (Math.Abs(diff) < 0.005f) return;
            _applyingReconcile = true;
            try
            {
                gi.Money = balance;
                Plugin.Logger.LogInfo($"[EconProbe] wallet SET ${balance:N0} ({why}; local drift {(diff >= 0 ? "+" : "")}{diff:N0}).");
            }
            finally { _applyingReconcile = false; }
        }
    }
}
