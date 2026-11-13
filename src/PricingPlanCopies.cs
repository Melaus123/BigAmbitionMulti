// T-32 (prices out of sync between merged partners).  A pricing-manager plan's product list is computed from
// the shelves of the shops it supervises (PricingManagerPlan.RecomputeSuggestions, decompile
// Buildings.Office.Headquarters/PricingManagerPlan.cs:167-185, items from GetSellableItems :411-430 = the
// business type's standard items + each store's cachedAvailableProducts).  A partner's machine holds only a
// COPY of the runner's plan (CompanyPlans partner rows, BuildPricing) and stale shelf lists of the runner's
// shops, so the screen's own recompute on open (PricingManagerPlanUI.LoadPlan:47) used to throw the runner's
// list away and rebuild a shorter one.  Established practice, owner-agnostic: the machine that RUNS the plan
// computes, every other machine displays.
//   (a) a partner's copy never recomputes locally: it keeps the runner's list (Patch_PricingPlanCopy_KeepRunnersList);
//   (b) opening the screen on a copy asks the runner to recompute NOW from its own shelves (routed plan edit
//       op `recompute` on the existing `mergerplanedit` carrier -> CompanyPlans.ApplyPricing); the runner's
//       urgent bundle carries the list back (PaperworkSync pricing rows -> CompanyPlans.Receive -> OnOwnerLists),
//       which writes it into the copy in place and reloads the open screen when what it shows changed;
//   (c) "Apply all" naming every row on screen travels as AllTargets and the runner prices over ITS OWN list
//       (CompanyPlans.ApplyPricing `suggestall`).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Buildings.Office.Headquarters;             // PricingManagerPlan, PriceSuggestion
using HarmonyLib;
using UI.Smartphone.Apps.BizMan.PricingManagers; // PricingManagerPlanUI

namespace BigAmbitionsMP
{
    internal static class PricingPlanCopies
    {
        /// <summary>The routed plan-edit op a partner's screen sends to the runner: "recompute now".</summary>
        internal const string RecomputeOp = "recompute";

        /// <summary>`suggestall` StrValue meaning "every product of the runner's own list".</summary>
        internal const string AllTargets = "*";

        private const float AnswerWindow = 20f;   // s: how long after an ask the open screen follows the answer
        // s: one ask per plan per answer window - a reselect the answer itself causes (a refused ask rebuilds the
        // plan list, which hides and reloads the pane) must not ask again and loop until the runner's next bundle
        private const float AskDebounce = AnswerWindow;
        private static string _pendingReload = "";   // a plan whose screen reload waits for the edit window to close
        private static float _nextPendingCheck;

        private static readonly Dictionary<string, float> _asked = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> _shown = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly HashSet<string> _keptLogged = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>A routed op that only READS (the runner recomputes its cache): no save mark, no redraw of
        /// the runner's own screen.</summary>
        internal static bool IsViewOp(string family, string op)
            => string.Equals(family, "pricing", StringComparison.Ordinal) && string.Equals(op, RecomputeOp, StringComparison.Ordinal);

        internal static bool IsAllTargets(string strValue) => string.Equals(strValue, AllTargets, StringComparison.Ordinal);

        /// <summary>The runner's published suggestions into a plan object (a copy's cachedSuggestions, which
        /// the screen's product table is built from one model per entry,
        /// PricingManagerProductsScrollerController.LoadPlan :70-79).</summary>
        internal static void FillSuggestions(PricingManagerPlan pl, List<PwPriceSuggestion>? src)
        {
            if (pl == null || pl.cachedSuggestions == null) return;
            pl.cachedSuggestions.Clear();
            foreach (var s in src ?? new List<PwPriceSuggestion>())
            {
                if (s == null || string.IsNullOrEmpty(s.ItemName)) continue;
                pl.cachedSuggestions.Add(new PriceSuggestion
                {
                    itemName = s.ItemName, suggestedMin = s.SuggestedMin, suggestedMax = s.SuggestedMax,
                    rivalReferencePrice = s.RivalReferencePrice, isPlayerSelling = s.IsPlayerSelling,
                    sellingBusinessTypes = new HashSet<string>(s.SellingBusinessTypes ?? new List<string>(), StringComparer.Ordinal),
                });
            }
        }

        /// <summary>Every product and its suggested min/max, sorted by item: what the screen's table shows.</summary>
        internal static string Signature(PricingManagerPlan pl)
        {
            var parts = new List<string>();
            if (pl != null && pl.cachedSuggestions != null)
                foreach (var sg in pl.cachedSuggestions)
                    if (sg != null && !string.IsNullOrEmpty(sg.itemName))
                        parts.Add(sg.itemName + ":" + sg.suggestedMin.ToString("0.00", CultureInfo.InvariantCulture)
                                  + "-" + sg.suggestedMax.ToString("0.00", CultureInfo.InvariantCulture));
            parts.Sort(StringComparer.Ordinal);
            return string.Join(";", parts);
        }

        /// <summary>FNV-1a 64 of a signature, as 16 hex digits (a log-sized fingerprint of the whole list).</summary>
        internal static string Digest(string sig)
        {
            ulong h = 14695981039346656037UL;
            foreach (byte b in Encoding.UTF8.GetBytes(sig ?? "")) { h ^= b; h *= 1099511628211UL; }
            return h.ToString("x16", CultureInfo.InvariantCulture);
        }

        /// <summary>(b) Ask the plan's runner to recompute now; a no-op for any plan that is not a partner's copy.</summary>
        internal static bool AskRunner(PricingManagerPlan plan, string why)
        {
            try
            {
                if (plan == null || !CompanyPlans.IsOverlayPlan(plan)) return false;
                string id = plan.id ?? "";
                if (id.Length == 0) return false;
                float now = UnityEngine.Time.unscaledTime;
                if (_asked.TryGetValue(id, out var t) && now - t < AskDebounce) return true;
                _asked[id] = now;
                CompanyPlans.RoutePaneEdit("pricing", why ?? "", plan, RecomputeOp);
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[PricingCopies] error asking the runner: {ex.Message}"); return false; }
        }

        /// <summary>What the open screen was last loaded with, per plan (LoadPlan postfix).</summary>
        internal static void Shown(PricingManagerPlan plan)
        {
            if (plan == null || string.IsNullOrEmpty(plan.id)) return;
            _shown[plan.id] = Signature(plan);
        }

        internal static void LogKept(PricingManagerPlan plan)
        {
            string id = plan?.id ?? "?";
            if (_keptLogged.Add(id))
                Plugin.Logger.LogInfo($"[PricingCopies] plan {id}: a partner's copy - kept the runner's list instead of recomputing here.");
        }

        /// <summary>(b) An owner's lists arrived (CompanyPlans.Receive, main thread): every pricing copy of that
        /// owner takes the runner's suggestions in place (same object, so it stays a routed row); a plan asked
        /// for within AnswerWindow reloads the open screen through the one pane refresh when what it shows
        /// changed.</summary>
        internal static void OnOwnerLists(CompanyListsPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.OwnerPid) || p.PricingManagerPlans == null) return;
                float now = UnityEngine.Time.unscaledTime;
                foreach (var d in p.PricingManagerPlans)
                {
                    if (d == null || string.IsNullOrEmpty(d.Id)) continue;
                    var copy = CompanyPlans.PricingCopyOf(p.OwnerPid, d.Id);
                    if (copy == null) continue;
                    string before = Signature(copy);
                    FillSuggestions(copy, d.CachedSuggestions);
                    string after = Signature(copy);
                    if (!_asked.TryGetValue(d.Id, out var t) || now - t > AnswerWindow) continue;
                    Plugin.Logger.LogInfo($"[PricingCopies] plan {d.Id} of '{p.OwnerPid}': the runner's list is in "
                                        + $"(n={copy.cachedSuggestions?.Count ?? 0}, sig={Digest(after)}, changed={before != after}).");
                    if (!string.Equals(CompanyPlans.OpenPanePlanId("pricing"), d.Id, StringComparison.Ordinal)) continue;
                    if (_shown.TryGetValue(d.Id, out var shown) && shown == after) continue;
                    string busy = CompanyPlans.EditWindowOpen();
                    if (busy.Length > 0) { _pendingReload = d.Id; Plugin.Logger.LogInfo($"[PricingCopies] plan {d.Id}: screen reload deferred - {busy}."); continue; }
                    if (_pendingReload == d.Id) _pendingReload = "";
                    CompanyPlans.RefreshOpenPaneInPlace("pricing", d.Id);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[PricingCopies] error on '{p?.OwnerPid}' lists: {ex.Message}"); }
        }

        /// <summary>A screen reload deferred by an open edit window runs when that window closes (called from
        /// CompanyPlans.TickDeferredRedraw, 1 Hz here); dropped when the screen moved to another plan or closed.</summary>
        internal static void TickPendingReload()
        {
            try
            {
                if (_pendingReload.Length == 0) return;
                float now = UnityEngine.Time.unscaledTime;
                if (now < _nextPendingCheck) return;
                _nextPendingCheck = now + 1f;
                string id = _pendingReload;
                if (!string.Equals(CompanyPlans.OpenPanePlanId("pricing"), id, StringComparison.Ordinal)) { _pendingReload = ""; return; }
                if (CompanyPlans.EditWindowOpen().Length > 0) return;
                _pendingReload = "";
                Plugin.Logger.LogInfo($"[PricingCopies] plan {id}: deferred screen reload RUNS - the edit window closed.");
                CompanyPlans.RefreshOpenPaneInPlace("pricing", id);
            }
            catch (Exception ex) { _pendingReload = ""; Plugin.Logger.LogWarning($"[PricingCopies] deferred reload: {ex.Message}"); }
        }
    }

    /// <summary>(a) A partner's copy keeps the runner's list: the game's recompute (screen open
    /// PricingManagerPlanUI.LoadPlan:47, and any other caller) is skipped on it.  The runner's own plans and
    /// every plan off a merger run natively (IsOverlayPlan false).</summary>
    [HarmonyPatch(typeof(PricingManagerPlan), nameof(PricingManagerPlan.RecomputeSuggestions))]
    internal static class Patch_PricingPlanCopy_KeepRunnersList
    {
        static bool Prefix(PricingManagerPlan __instance)
        {
            try
            {
                if (__instance == null || !CompanyPlans.IsOverlayPlan(__instance)) return true;
                PricingPlanCopies.LogKept(__instance);
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[PricingCopies] error on recompute: {ex.Message}"); return true; }
        }
    }

    /// <summary>(b) Opening the pricing screen on a partner's copy asks the runner for a live list.  A reload
    /// of the plan already on screen (the pane active on the same id: the mod's own pane refresh or a list
    /// reselect) does not ask again.  `____currentPlan` = '___' + `_currentPlan` (PricingManagerPlanUI.cs:24).</summary>
    [HarmonyPatch(typeof(PricingManagerPlanUI), nameof(PricingManagerPlanUI.LoadPlan))]
    internal static class Patch_PricingPlanCopy_ScreenOpen
    {
        static void Prefix(PricingManagerPlanUI __instance, PricingManagerPlan plan, PricingManagerPlan ____currentPlan)
        {
            try
            {
                if (plan == null || !CompanyPlans.IsOverlayPlan(plan)) return;
                bool reload = __instance != null && __instance.gameObject.activeInHierarchy && ____currentPlan != null
                              && string.Equals(____currentPlan.id, plan.id, StringComparison.Ordinal);
                if (!reload) PricingPlanCopies.AskRunner(plan, "plan screen opened");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[PricingCopies] error on screen open: {ex.Message}"); }
        }

        static void Postfix(PricingManagerPlan plan)
        {
            try { if (plan != null && CompanyPlans.IsOverlayPlan(plan)) PricingPlanCopies.Shown(plan); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[PricingCopies] error after screen open: {ex.Message}"); }
        }
    }
}
