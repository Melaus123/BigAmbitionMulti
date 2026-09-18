using System;
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>WALLET-DUPE-1 (W3), user ruling D43 (2026-09-13): a merged member's .hsg records their equal
    /// SHARE of the company, not the wallet mirror.
    ///
    /// While merged, every member's local SaveGameManager.Current.Money is a MIRROR of the ONE company balance
    /// (MergerWallet.SetMirror) — that is what keeps every native UI and overdraft gate working untouched. It is
    /// also what made the save file lie: each member's .hsg recorded the WHOLE balance as personal cash, so a
    /// load on which the merger did not restore (roster absent) left two members each holding the full pot, and
    /// a re-formed merger pooled both copies. The equal shares sum exactly to the balance, so no load path can
    /// mint money whether the roster restores or not.
    ///
    /// Modelled on OfflineForkSave.cs, which wraps the SAME method with the same prefix/finalizer pair: a
    /// Postfix does not run when the original throws, a Finalizer runs on both paths, so the mirror is always
    /// put back. The game serialises SYNCHRONOUSLY inside Save (SerializeSaveGame, on the live GameInstance;
    /// the thread that follows only compresses), so the wrap is exact.
    ///
    /// NOTED, accepted: GlobalEvents.onSaveGame fires INSIDE Save while Money holds the share. Any listener
    /// that reads Money at that instant sees the share rather than the mirror — correct for anything recording
    /// the save, momentary and un-displayed for anything else (the wrap holds for one synchronous call).
    ///
    /// INERT outside a merger: the prefix returns before touching anything when IAmMember is false, and in
    /// single-player it is doubly inert (no merger, no session).
    ///
    /// FOLD b (review r1): the wrap now runs ONLY when the share is KNOWN — MPSaveCoordinator.TryMergedShare
    /// answering true (X1) — and only on the OUTERMOST Save call (X6). Before the first wallet state of a
    /// world, and for a nested Save, this patch deliberately does nothing and the .hsg keeps whatever
    /// Money holds, which in that window is already this member's own restored share.</summary>
    [HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.Save), typeof(SaveGameManager.SaveType), typeof(string), typeof(string))]
    public static class Patch_SaveGameManager_Save_MergerShare
    {
        private static bool  _stashed;
        private static float _mirror;
        private static int   _depth;        // FOLD b X6: Save call depth — only the OUTERMOST call may swap the wallet
        private static bool  _skipLogged;   // FOLD b X1: the "not mirrored yet" notice is once per session, not once per save

        static void Prefix()
        {
            int depth = ++_depth;   // incremented FIRST and unconditionally, so the finalizer's decrement always pairs
            try
            {
                // X6 (r1 F7): a NESTED Save must do nothing at all. The old belt stopped the inner call
                // STASHING, but the inner call's finalizer still restored the mirror and cleared _stashed —
                // while the outer call was still before serialisation, so the outer save then wrote the
                // MIRROR (the whole company balance) into the .hsg, the exact figure this patch exists to
                // keep out of it. Depth makes both halves agree: outermost stashes and restores, nested
                // neither.
                if (depth > 1) return;
                if (_stashed) return;               // re-entrancy belt: never stash a share over a share
                if (!MergerSync.IAmMember) return;  // not merged — the .hsg's own wallet is already the truth
                var gi = SaveGameManager.Current;
                if (gi == null) return;
                float balance = gi.Money;
                // X1 (r1 F1/F5): the wrap runs ONLY on a figure known to be the company balance. TryMergedShare
                // is that contract (host: an authoritative ledger figure; client: the wallet state for this world
                // has actually been mirrored, with a roster to divide by) — SavedCashFor's float signature has no
                // way to say "I don't know", and its no-figure fallbacks (a client's empty CashByStableId → $0,
                // or a client's un-mirrored Money → the restored SHARE halved again) were both money-destroying
                // writes. When it answers false we simply RETURN: the .hsg then records whatever Money holds,
                // which in that window is already this member's own restored share.
                if (!MPSaveCoordinator.TryMergedShare(MPConfig.StableId, out float share))
                {
                    if (!_skipLogged)
                    {
                        _skipLogged = true;
                        Plugin.Logger.LogInfo("[MergerWallet] save left the wallet as is - the company balance is not mirrored yet.");
                    }
                    return;
                }
                if (Math.Abs(share - balance) < 0.005f) return;   // a one-member company: the share IS the balance
                _mirror = balance; _stashed = true;
                // DESIGNER-MONEY-1 (M2): an `x = value` wallet write — forward the DELTA actually applied.
                DesignerBalanceKeeper.OnExternalMoneyChange(share - balance, "merger share-save stash");
                gi.Money = share;
                Plugin.Logger.LogInfo($"[MergerWallet] save wrote this member's share ${share:F0} of ${balance:F0} into the save file (mirror restored).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MergerWallet] share-save prefix: {ex.Message}"); }
        }

        static void Finalizer()
        {
            int depth = _depth;
            if (_depth > 0) _depth--;
            try
            {
                if (depth > 1) return;   // X6: a nested call stashed nothing, so it restores nothing
                if (!_stashed) return;
                var gi = SaveGameManager.Current;
                if (gi != null)
                {
                    // DESIGNER-MONEY-1 (M2): an `x = value` wallet write — forward the DELTA actually applied.
                    float back = _mirror - gi.Money;
                    gi.Money = _mirror;   // unconditional on BOTH paths — the mirror is the live truth
                    DesignerBalanceKeeper.OnExternalMoneyChange(back, "merger share-save restore");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MergerWallet] share-save finalizer: {ex.Message}"); }
            finally { if (depth <= 1) { _stashed = false; _mirror = 0f; } }
        }
    }
}
