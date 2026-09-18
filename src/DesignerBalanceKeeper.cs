using System;
using HarmonyLib;
using Buildings.Indoors.InteriorDesign;   // InteriorDesignerController, InteriorDesignerHelper
using UI.InteriorDesigner;                // InteriorDesignerUI

namespace BigAmbitionsMP
{
    /// <summary>DESIGNER-MONEY-1 (2026-09-17). The Interior Designer is a BOOKKEEPING session, not a wallet:
    /// OnOpen (decompile InteriorDesignerController.cs:242-244) snapshots <c>PlayerBalanceAfterChanges =
    /// floor(Money)</c>, every buy/sell adds to that snapshot (ChangeBalance :301-310), and HandleOnClose
    /// (:387-403) charges the DIFFERENCE <c>PlayerBalanceAfterChanges - Money</c> as one
    /// "ba:transaction_interiordesigner" transaction. Vanilla gets away with it because opening the designer
    /// PAUSES the world (InteriorDesignerUI.cs:471) so nothing else can touch Money. In multiplayer the mod
    /// deliberately suppresses that pause (MPPatches Patch_GSC_SetPause_Suppress) — the session keeps running,
    /// so every OUTSIDE money change (shop revenue, rent, a partner's transfer) is CLAWED BACK at close, or
    /// refunded twice if it was a cost. Bundle 20260906-194337 lost ~$43.7k of a day's revenue that way.
    ///
    /// The fix keeps the designer's own arithmetic intact and simply tells it about the outside world: each
    /// external delta is pushed through the designer's own public lever (IRevertibleAction.changeBalance, the
    /// same one DesignerSaleRoute already uses) so the snapshot tracks Money instead of drifting from it. The
    /// close-time charge is then the designer's own spending, which is what it was always meant to be.
    /// No on-screen text — log lines only.</summary>
    internal static class DesignerBalanceKeeper
    {
        private const string DesignerKey = "ba:transaction_interiordesigner";

        // Per-session tally, reset on every OnOpen — the close diagnostic reads it.
        private static int   _extCount;
        private static float _extTotal;
        private static readonly System.Collections.Generic.Dictionary<string, float> _extByKey = new System.Collections.Generic.Dictionary<string, float>();
        private static double _moneyAtOpen;

        /// <summary>Every money change that did NOT come from the designer itself, while a designer session is
        /// open. Forwards it into the designer's running balance so the close-time charge stays the designer's
        /// own spend.</summary>
        internal static void OnExternalMoneyChange(float delta, string transactionKey)
        {
            try
            {
                if (delta == 0f) return;
                if (!InteriorDesignerUI.IsOpen) return;
                // Blueprint-creator mode books into BlueprintTotalCost, never into the player's balance
                // (ChangeBalance :303-307), so an outside delta must NOT be pushed there.
                if (InteriorDesignerHelper.BlueprintCreatorMode) return;
                string key = transactionKey ?? "";
                if (key == DesignerKey) return;   // the designer's own close-time charge — not external

                // L2: tally ONLY what we actually forwarded — a null lever means the designer never heard
                // this delta, and counting it made the close diagnostic claim a correction that never happened.
                var changeBalance = BigAmbitions.InteriorDesigner.IRevertibleAction.changeBalance;
                if (changeBalance == null) return;
                changeBalance.Invoke(delta);

                _extCount++;
                _extTotal += delta;
                string k = key.Length == 0 ? "(none)" : key;
                _extByKey.TryGetValue(k, out var sum);
                _extByKey[k] = sum + delta;
            }
            catch (Exception ex)
            { try { Plugin.Logger.LogWarning($"[Designer] external money forward ({transactionKey}): {ex.Message}"); } catch { } }
        }

        internal static void ResetTally()
        {
            _extCount = 0; _extTotal = 0f; _extByKey.Clear();
            try { _moneyAtOpen = SaveGameManager.Current == null ? 0.0 : (double)SaveGameManager.Current.Money; } catch { _moneyAtOpen = 0.0; }
        }

        internal static string TopKeys()
        {
            try
            {
                var list = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, float>>(_extByKey);
                list.Sort((a, b) => Math.Abs(b.Value).CompareTo(Math.Abs(a.Value)));
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < list.Count && i < 3; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append($"{list[i].Key}={list[i].Value:F0}");
                }
                return sb.Length == 0 ? "-" : sb.ToString();
            }
            catch { return "-"; }
        }

        internal static int   ExtCount => _extCount;
        internal static float ExtTotal => _extTotal;
        internal static double MoneyAtOpen => _moneyAtOpen;

        /// <summary>The designer's own running snapshot (private static property on the controller).</summary>
        internal static double AfterChanges()
        {
            try
            {
                var g = AccessTools.PropertyGetter(typeof(InteriorDesignerController), "PlayerBalanceAfterChanges");
                if (g == null) return double.NaN;
                return (double)g.Invoke(null, null)!;
            }
            catch { return double.NaN; }
        }
    }

    /// <summary>DESIGNER-MONEY-1 (C1): a fresh session starts a fresh tally.</summary>
    [HarmonyPatch(typeof(InteriorDesignerController), nameof(InteriorDesignerController.OnOpen))]
    public static class Patch_InteriorDesigner_OnOpen_BalanceKeeper
    {
        static void Postfix()
        {
            try { DesignerBalanceKeeper.ResetTally(); }
            catch (Exception ex) { try { Plugin.Logger.LogWarning($"[Designer] OnOpen keeper reset (Patch_InteriorDesigner_OnOpen_BalanceKeeper): {ex.Message}"); } catch { } }
        }
    }

    /// <summary>DESIGNER-MONEY-1 (C3): one line per close naming the charge the designer is about to make and
    /// how much of the drift came from outside it. A charge far from the session's own spend, with a non-zero
    /// external tally, is this bug recurring.</summary>
    [HarmonyPatch(typeof(InteriorDesignerController), "HandleOnClose")]
    public static class Patch_InteriorDesigner_Close_MoneyDiag
    {
        static void Prefix()
        {
            try
            {
                double after = DesignerBalanceKeeper.AfterChanges();
                double live  = SaveGameManager.Current == null ? 0.0 : (double)SaveGameManager.Current.Money;
                Plugin.Logger.LogInfo(
                    $"[Designer] close: moneyAtOpen={DesignerBalanceKeeper.MoneyAtOpen:F0} afterChanges={after:F0} live={live:F0} "
                    + $"charge={(after - live):F0} external={DesignerBalanceKeeper.ExtCount}/{DesignerBalanceKeeper.ExtTotal:F0} "
                    + $"top={DesignerBalanceKeeper.TopKeys()} (DESIGNER-MONEY-1)");
            }
            catch (Exception ex) { try { Plugin.Logger.LogWarning($"[Designer] close diag (Patch_InteriorDesigner_Close_MoneyDiag): {ex.Message}"); } catch { } }
        }
    }
}
