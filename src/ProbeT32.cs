// PROBE-START: P-T32-PRICES
// Log-only probe for T-32, prices part (ACTIVE 2026-11-13; all configurations; .modding/04-probes.md P-T32-PRICES).
// Question: in a merged company a partner who opens the pricing-manager screen of a shared plan gets the
// SUGGESTED prices recomputed from that machine's OWN data: PricingManagerPlanUI.LoadPlan (decompile
// UI.Smartphone.Apps.BizMan.PricingManagers/PricingManagerPlanUI.cs:44, call at :47) runs
// PricingManagerPlan.RecomputeSuggestions (Buildings.Office.Headquarters/PricingManagerPlan.cs:167-185), which
// throws away the host's copied list (CompanyPlans.BuildPricing) and rebuilds it through BuildSuggestion
// (:303-323) -> PricingManagerHelper.ComputeSuggestion (PricingManagerHelper.cs:38-52). Do the two machines
// get different suggestions for the same plan and product, and from which inputs?
// Hook: a Postfix on RecomputeSuggestions - the narrowest PUBLIC method that holds the whole result
// (cachedSuggestions); BuildSuggestion is private and per item. It logs only for a plan ARMED by the screen
// (a LoadPlan prefix) or by the DEV lever `pricesuggest`, once per arming, so the hourly RunUpdate /
// assign / neighbourhood recomputes add nothing; a screen redraw that recomputes the SAME values is not
// logged again. The formula's inputs are re-read in the same frame through the same public game helpers
// the formula calls. Never changes the outcome.
// Remove when: a run or log shows whether suggestions differ and why, or T-32 closes.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Buildings.Office.Headquarters;             // PricingManagerPlan, PricingManagerHelper, PriceSuggestion
using HarmonyLib;
using UI.Smartphone.Apps.BizMan.PricingManagers; // PricingManagerPlanUI
using UnityEngine;

namespace BigAmbitionsMP
{
    internal static class ProbeT32
    {
        private const int MaxProducts = 30;                       // product lines per open
        private static string _armedId;                            // the plan the next recompute is logged for
        private static string _armedBy = "";                       // "screen" or "lever"
        private static string _lastSummary = "";                   // the lever's result text
        private static readonly Dictionary<string, string> _lastScreenSig = new Dictionary<string, string>(StringComparer.Ordinal);
        private static MethodInfo _stable;
        private static bool _stableLooked;

        internal static void Arm(PricingManagerPlan plan, string by)
        {
            try { _armedId = plan?.id; _armedBy = by ?? ""; } catch { }
        }

        internal static void AfterRecompute(PricingManagerPlan plan)
        {
            try
            {
                if (plan == null || _armedId == null || plan.id != _armedId) return;
                string by = _armedBy;
                _armedId = null;
                Describe(plan, by);
            }
            catch (Exception ex) { try { Plugin.Logger.LogWarning("[PROBE T32-PRICES] probe error: " + ex.Message); } catch { } }
        }

        private static string F(float v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }

        /// <summary>One header line and up to MaxProducts product lines for the plan's fresh suggestions.
        /// Inputs, as ComputeSuggestion (PricingManagerHelper.cs:38-52) reads them: maxAcceptable =
        /// ItemHelper.CalculateMaxAcceptablePriceByNeighborhood (lowest over social classes of marketRef x
        /// CitizenHelper.MaxAcceptableRelativePrice, +0.3 with a player monopoly; ItemHelper.cs:624-647), capped
        /// at 10000; marketRef = ItemHelper.GetMarketReferencePrice = min(DefaultMarketPrice, GetLowestMarketPrice
        /// over this machine's rival shops) (:619-622); skill01 = analyst's ba:skill_pricingmanager / 100
        /// (PricingManagerPlan.cs:55, :174); jitter = RngHelper.StableValue01(id + assignedEmployeeId + item)
        /// (:309); settings minSkillCapture / captureJitter / visibleSpread.</summary>
        private static void Describe(PricingManagerPlan plan, string by)
        {
            string id = plan.id ?? "";
            string nb = plan.supervisedNeighborhood ?? "";
            string emp = plan.assignedEmployeeId ?? "";
            bool empFound = false; float skill = 0f;
            try { empFound = plan.AnalystInstance != null; skill = plan.PricingManagerSkillValue; } catch { }
            float skill01 = Mathf.Clamp01(skill / 100f);
            int stores = 0;
            try
            {
                foreach (var r in SaveGameManager.Current.BuildingRegistrations)
                    if (r != null && PricingManagerHelper.IsManageableBusiness(r) && r.Neighborhood == nb) stores++;
            }
            catch { }
            bool display = false; try { display = MergerAbsence.IsDisplayInstall(plan); } catch { }
            string settings = "n/a";
            try
            {
                var s = PricingManagerHelper.Settings;
                if (s != null) settings = "minSkillCapture=" + F(s.minSkillCapture) + " captureJitter=" + F(s.captureJitter) + " visibleSpread=" + F(s.visibleSpread);
            }
            catch { }
            var list = new List<PriceSuggestion>();
            if (plan.cachedSuggestions != null)
                foreach (var sg in plan.cachedSuggestions) if (sg != null && !string.IsNullOrEmpty(sg.itemName)) list.Add(sg);
            list.Sort((a, b) => string.CompareOrdinal(a.itemName, b.itemName));

            var lines = new List<string>();
            var sb = new StringBuilder();
            int n = 0;
            foreach (var sg in list)
            {
                if (n >= MaxProducts) break;
                n++;
                string item = sg.itemName;
                string inputs;
                try
                {
                    float maxAcc = global::ItemHelper.CalculateMaxAcceptablePriceByNeighborhood(item, nb);
                    float mref = global::ItemHelper.GetMarketReferencePrice(item, nb);
                    float def = global::ItemHelper.GetDefaultMarketPrice(item);
                    float low = global::ItemHelper.GetLowestMarketPrice(item, nb);
                    bool mono = global::ItemHelper.HasPlayerMonopoly(item, nb);
                    inputs = "maxAcceptable=" + F(maxAcc) + " marketRef=" + F(mref) + " default=" + F(def) + " lowestHere=" + F(low)
                           + " monopolyHere=" + mono + " skill01=" + F(skill01) + " jitter=" + Jitter(id + emp + item)
                           + " rivalRef=" + F(sg.rivalReferencePrice) + " playerSellingHere=" + sg.isPlayerSelling;
                }
                catch (Exception ex) { inputs = "unreadable(" + ex.GetType().Name + ")"; }
                lines.Add("[PROBE T32-PRICES] agreement=" + id + " product=" + item + " suggested=" + F(sg.suggestedMin) + "-" + F(sg.suggestedMax) + " inputs=" + inputs);
                if (sb.Length > 0) sb.Append(';');
                sb.Append(item).Append(':').Append(F(sg.suggestedMax));
            }
            string head = "[PROBE T32-PRICES] agreement=" + id + " by=" + by + " hq=" + plan.headquartersAddress + " neighborhood=" + nb
                        + " manager=" + emp + " managerFoundHere=" + empFound + " skill=" + F(skill) + " supervisedStoresHere=" + stores
                        + " displayCopy=" + display + " suggestions=" + list.Count + " settings: " + settings;
            _lastSummary = "agreement=" + id + " manager=" + emp + " managerFoundHere=" + empFound + " skill=" + F(skill)
                         + " stores=" + stores + " suggestions=" + list.Count + " sugg=" + (sb.Length == 0 ? "-" : sb.ToString());

            if (by == "screen")
            {
                string sig = _lastSummary + "|" + string.Join("|", lines);
                if (_lastScreenSig.TryGetValue(id, out var prev) && prev == sig) return;   // a redraw with the same values
                _lastScreenSig[id] = sig;
            }
            Plugin.Logger.LogInfo(head);
            foreach (var l in lines) Plugin.Logger.LogInfo(l);
            if (list.Count > n) Plugin.Logger.LogInfo("[PROBE T32-PRICES] agreement=" + id + " " + (list.Count - n) + " more product(s) not logged (cap " + MaxProducts + ")");
        }

        /// <summary>RngHelper.StableValue01(string) by reflection: the type is not in the decompiled game
        /// tree, so it is looked up at run time rather than named at compile time.</summary>
        private static string Jitter(string key)
        {
            try
            {
                if (!_stableLooked)
                {
                    _stableLooked = true;
                    var t = AccessTools.TypeByName("RngHelper");
                    if (t != null) _stable = AccessTools.Method(t, "StableValue01", new[] { typeof(string) });
                }
                if (_stable == null) return "n/a";
                object v = _stable.Invoke(null, new object[] { key });
                return v is float f ? F(f) : Convert.ToString(v, CultureInfo.InvariantCulture);
            }
            catch { return "n/a"; }
        }

#if BAMP_DEV
        /// <summary>DEV lever `pricesuggest <planId|first>`: runs what the screen runs on open
        /// (PricingManagerPlanUI.LoadPlan:47, RecomputeSuggestions) on one pricing plan without opening any UI,
        /// with the probe armed, and answers the probe's summary. `pricesuggest make [neighbourhood]` creates
        /// one plan first (DevMakePlan); `pricesuggest recruit` gets this machine a pricing-manager candidate for its
        /// headquarters first (DevRecruit). `first` = this machine's own REAL plan with a
        /// manager (lowest id), else a partner's copy with a manager. An id looks in the partner copies first
        /// (CompanyPlans._rows, as CompanyPlans.PlanObjectOf does), then in this machine's own list.</summary>
        internal static string DevPriceSuggest(string arg)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return "ERR pricesuggest: no game loaded";
                string want = (arg ?? "").Trim();
                if (want.Length == 0) want = "first";
                if (want == "make" || want.StartsWith("make ", StringComparison.Ordinal)) return DevMakePlan(gi, want.Substring(4).Trim());
                if (want == "recruit") return DevRecruit(gi);
                var partnerRows = PartnerPricingRows();
                var own = new List<PricingManagerPlan>();
                if (gi.pricingManagerPlans != null) foreach (var p in gi.pricingManagerPlans) if (p != null) own.Add(p);
                PricingManagerPlan pick = null;
                if (want == "first")
                {
                    var cand = new List<PricingManagerPlan>();
                    foreach (var p in own)
                    {
                        bool disp = false; try { disp = MergerAbsence.IsDisplayInstall(p); } catch { }
                        if (!disp && !string.IsNullOrEmpty(p.assignedEmployeeId)) cand.Add(p);
                    }
                    if (cand.Count == 0) foreach (var p in partnerRows) if (!string.IsNullOrEmpty(p.assignedEmployeeId)) cand.Add(p);
                    cand.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
                    if (cand.Count > 0) pick = cand[0];
                }
                else
                {
                    foreach (var p in partnerRows) if (p.id == want) { pick = p; break; }
                    if (pick == null) foreach (var p in own) if (p.id == want) { pick = p; break; }
                }
                if (pick == null) return "ERR pricesuggest: no pricing plan '" + want + "' here (own=" + own.Count + " partnerRows=" + partnerRows.Count + ")";
                _lastSummary = "";
                Arm(pick, "lever");
                pick.RecomputeSuggestions();
                _armedId = null;
                return "OK pricesuggest " + (_lastSummary.Length > 0 ? _lastSummary : "agreement=" + pick.id + " (probe wrote nothing)")
                     + " own=" + own.Count + " partnerRows=" + partnerRows.Count;
            }
            catch (Exception ex) { return "ERR pricesuggest: " + ex.Message; }
        }

        /// <summary>DEV lever `pricesuggest make [neighbourhood]`: creates ONE pricing-manager plan on THIS machine
        /// through the game's own creation path, as a player does in BizMan > headquarters > Pricing Managers
        /// (tab offered for ba:businesstype_headquarters only, BizManBusiness.cs:225-228):
        /// PricingManagersPlanList.AddPlan (UI.Smartphone.Apps.BizMan.PricingManagers/PricingManagersPlanList.cs:63-75:
        /// new PricingManagerPlan { headquartersAddress }, PricingManagerHelper.AddPlan, MarkChange), then the manager
        /// dropdown's ChangePricingManager (:245 plan.AssignEmployee, :247 MarkChange) and the neighbourhood dropdown's
        /// ChangeNeighborhood (PricingManagerPlanUI.cs:119 plan.SetSupervisedNeighborhood).
        /// Manager: the dropdown's own filter (PricingManagersPlanList.cs:179 - assigned to the headquarters, has
        /// ba:skill_pricingmanager, on no other plan, on a work shift), the headquarters rented by this machine
        /// (RemoveOrphanedPricingManagerPlans drops a plan whose HQ is not); highest skill, then lowest id.
        /// Neighbourhood: the selectable list's filter (PricingManagerPlanUI.cs:76-90 - holds a manageable business,
        /// PricingManagerHelper.IsManageableBusiness, and no other plan supervises it); the named one, else the one
        /// with the most such stores, then the lowest name. Then PaperworkSync.MarkDirty, as CompanyPlans' routed
        /// plan edits do, so the next fan-out carries the plan to merger partners. Run it BEFORE a merge so only
        /// this machine's own employees and buildings are candidates.</summary>
        private static string DevMakePlan(GameInstance gi, string nbWanted)
        {
            Entities.EmployeeInstance best = null;
            float bestSkill = -1f;
            int seen = 0;
            if (gi.EmployeeInstances != null)
                foreach (var e in gi.EmployeeInstances)
                {
                    if (e == null || !e.HasSkill("ba:skill_pricingmanager")) continue;
                    seen++;
                    if (e.assignedAddress == null || !e.IsAssignedToAnyWorkShift()) continue;
                    if (PricingManagerHelper.IsEmployeeAssignedToOtherPlan(e.id, null)) continue;
                    var reg = Helpers.BuildingHelper.GetBuildingRegistration(e.assignedAddress);
                    if (reg == null || !reg.RentedByPlayer || reg.businessTypeName != "ba:businesstype_headquarters") continue;
                    float sk = e.GetSkillValue("ba:skill_pricingmanager");
                    if (best == null || sk > bestSkill || (sk == bestSkill && string.CompareOrdinal(e.id, best.id) < 0)) { best = e; bestSkill = sk; }
                }
            if (best == null)
                return "ERR pricesuggest make: no free pricing manager here (employees with ba:skill_pricingmanager=" + seen
                     + "; one must work at a rented headquarters, on a work shift, on no other plan - PricingManagersPlanList.cs:179)";

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (gi.BuildingRegistrations != null)
                foreach (var r in gi.BuildingRegistrations)
                {
                    if (r == null || string.IsNullOrEmpty(r.Neighborhood) || !PricingManagerHelper.IsManageableBusiness(r)) continue;
                    if (PricingManagerHelper.IsNeighborhoodSupervised(r.Neighborhood, null)) continue;
                    counts.TryGetValue(r.Neighborhood, out int c);
                    counts[r.Neighborhood] = c + 1;
                }
            string nb = null;
            if (nbWanted.Length > 0)
            {
                if (!counts.ContainsKey(nbWanted))
                    return "ERR pricesuggest make: neighbourhood '" + nbWanted + "' not selectable here (selectable: " + (counts.Count == 0 ? "-" : string.Join(",", counts.Keys)) + ")";
                nb = nbWanted;
            }
            else
                foreach (var kv in counts)
                    if (nb == null || kv.Value > counts[nb] || (kv.Value == counts[nb] && string.CompareOrdinal(kv.Key, nb) < 0)) nb = kv.Key;
            if (nb == null)
                return "ERR pricesuggest make: no selectable neighbourhood here (no unsupervised neighbourhood holds a manageable business - PricingManagerPlanUI.cs:76-90)";

            var plan = new PricingManagerPlan { headquartersAddress = best.assignedAddress };
            PricingManagerHelper.AddPlan(plan);
            SaveGameManager.MarkChange();
            plan.AssignEmployee(best.id);
            SaveGameManager.MarkChange();
            plan.SetSupervisedNeighborhood(nb);
            try { PaperworkSync.MarkDirty(); } catch { }
            return "OK pricesuggest made agreement=" + plan.id + " hq=" + plan.headquartersAddress + " neighborhood=" + nb
                 + " manager=" + best.id + " skill=" + F(bestSkill) + " stores=" + counts[nb]
                 + " suggestions=" + (plan.cachedSuggestions?.Count ?? 0);
        }

        private const string PricingSkill = "ba:skill_pricingmanager";

        /// <summary>DEV lever `pricesuggest recruit`: gets THIS machine a pricing-manager candidate for the headquarters it
        /// rents through the game's own recruitment: a campaign shaped as the recruitment agency's dialog builds it
        /// (Dialogs/RecruitmentAgencyDialog.cs:163-176: agency, business = the headquarters, skill ba:skill_pricingmanager
        /// at percentage 20, full time, one candidate) whose candidate search the game itself runs
        /// (Entities/RecruitmentCampaign.cs:58-71 CheckForCandidates -> Helpers/RecruitmentHelper.cs:41-83 GenerateCandidate:
        /// skill Round(Random(20,50)), assignedAddress = the headquarters, sourceAddress = the agency). Differences from the
        /// dialog, all named: its one find time is NOW instead of 1..N days later (:193-197), the fee (:185) is not charged,
        /// and the campaign is not added to RecruitmentCampaigns (no hourly run, no finished text).
        /// It also names what the scenario's next two steps use: the existing `hire <candidate>` lever
        /// (EmployeeHelper.HireCandidate, keeping the headquarters address) and `shiftat <hq> <day> <station> <candidate>
        /// <from> auto` (ScheduleHelper.AddWorkShift on the opened Schedule tab, the call ScheduleEmployeeSelection.cs:60
        /// makes when the player picks the employee). Station: an assignable item of the headquarters
        /// (BuildingRegistration.GetAssignableItems :621-640) whose suitableSkills hold ba:skill_pricingmanager, not a
        /// cleaning station, used by no shift on any day; the page's order (itemName, then id - ScheduleHelper.FetchWorkstations).
        /// Day: the first open schedule day with opening hours; from = its first opening slot's start (a headquarters desk
        /// shift spans that slot on every open day - ScheduleHelper.AddWorkShift :295-321).
        /// Agency and headquarters: the lowest address key when there are several.</summary>
        private static string DevRecruit(GameInstance gi)
        {
            if (gi.BuildingRegistrations == null || gi.CandidateEmployeeInstances == null) return "ERR pricesuggest recruit: no save loaded";
            BuildingRegistration hq = null, agency = null;
            string hqKey = null, agencyKey = null;
            foreach (var r in gi.BuildingRegistrations)
            {
                if (r == null || r.Address == null) continue;
                string k = null;
                try { k = GameStateReader.AddressKey(r.Address); } catch { }
                if (string.IsNullOrEmpty(k)) continue;
                if (r.RentedByPlayer && r.businessTypeName == "ba:businesstype_headquarters")
                { if (hq == null || string.CompareOrdinal(k, hqKey) < 0) { hq = r; hqKey = k; } }
                else if (r.businessTypeName == "ba:businesstype_recruitmentagency")
                { if (agency == null || string.CompareOrdinal(k, agencyKey) < 0) { agency = r; agencyKey = k; } }
            }
            if (hq == null) return "ERR pricesuggest recruit: no rented headquarters here (no registration RentedByPlayer with ba:businesstype_headquarters)";
            if (agency == null) return "ERR pricesuggest recruit: no recruitment agency in this city (no ba:businesstype_recruitmentagency registration)";

            ScheduleDay day = null;
            if (hq.scheduleDays != null)
                foreach (var sd in hq.scheduleDays)
                    if (sd != null && sd.isOpen && sd.openingHourSlots != null && sd.openingHourSlots.Count > 0) { day = sd; break; }
            if (day == null) return "ERR pricesuggest recruit: headquarters " + hqKey + " has no open schedule day with opening hours";

            var items = new List<BigAmbitions.Items.ItemInstance>();
            hq.GetAssignableItems(items);
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sd in hq.scheduleDays)
                if (sd?.workShifts != null)
                    foreach (var w in sd.workShifts) if (w?.itemInstanceId != null) taken.Add(w.itemInstanceId);
            BigAmbitions.Items.ItemInstance station = null;
            int suitable = 0;
            foreach (var it in items)
            {
                var skills = it?.ItemCached?.suitableSkills;
                if (skills == null || Array.IndexOf(skills, PricingSkill) < 0) continue;
                if (UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.IsCleaningStation(it)) continue;
                suitable++;
                if (taken.Contains(it.id)) continue;
                int byName = station == null ? -1 : string.CompareOrdinal(it.itemName, station.itemName);
                if (station == null || byName < 0 || (byName == 0 && string.CompareOrdinal(it.id, station.id) < 0)) station = it;
            }
            if (station == null)
                return "ERR pricesuggest recruit: headquarters " + hqKey + " has no free workstation for ba:skill_pricingmanager (suitable="
                     + suitable + ", every one already on a shift)";

            var before = new HashSet<Entities.EmployeeInstance>(gi.CandidateEmployeeInstances);
            var camp = new Entities.RecruitmentCampaign
            {
                agencyAddress = agency.Address,
                businessAddress = hq.Address,
                skillRequirement = new Entities.RecruitmentCampaign.SkillRequirement { skillName = PricingSkill, percentage = 20f },
                fullTime = true,
                partTime = false,
                amountOfCandidates = 1,
                price = 0f,
                candidateFindTimes = new List<BigAmbitions.DayNightCycle.Timestamp> { new BigAmbitions.DayNightCycle.Timestamp(gi.Day, gi.Hour, 0f) }
            };
            camp.CheckForCandidates();
            Entities.EmployeeInstance cand = null;
            foreach (var c in gi.CandidateEmployeeInstances)
                if (c != null && !before.Contains(c) && c.HasSkill(PricingSkill)) { cand = c; break; }
            if (cand == null) return "ERR pricesuggest recruit: the campaign's candidate search produced no ba:skill_pricingmanager candidate (agency " + agencyKey + ")";
            return "OK pricesuggest recruit candidate=" + cand.id + " skill=" + F(cand.GetSkillValue(PricingSkill))
                 + " day=" + (int)day.day + " station=" + station.id + " from=" + day.openingHourSlots[0].startingHour
                 + " suitable=" + suitable + " agency='" + agencyKey + "' hq=" + hqKey;
        }

        /// <summary>The partner pricing copies CompanyPlans holds (private `_rows`, read by reflection - this
        /// probe may not edit CompanyPlans.cs). When none is built yet, CompanyPlans' own MaterialiseRows is run
        /// for every partner it holds lists of, as opening the page would.</summary>
        private static List<PricingManagerPlan> PartnerPricingRows()
        {
            var res = new List<PricingManagerPlan>();
            try
            {
                var t = typeof(CompanyPlans);
                var rows = AccessTools.Field(t, "_rows")?.GetValue(null) as IDictionary;
                if (rows == null) return res;
                bool any = false;
                foreach (DictionaryEntry kv in rows) if (kv.Value is PricingManagerPlan) { any = true; break; }
                if (!any)
                {
                    var owners = AccessTools.Field(t, "_byOwner")?.GetValue(null) as IDictionary;
                    var mat = AccessTools.Method(t, "MaterialiseRows", new[] { typeof(string) });
                    if (owners != null && mat != null)
                    {
                        var keys = new List<string>();
                        foreach (var k in owners.Keys) if (k is string s) keys.Add(s);
                        foreach (var k in keys) mat.Invoke(null, new object[] { k });
                    }
                }
                foreach (DictionaryEntry kv in rows) if (kv.Value is PricingManagerPlan p) res.Add(p);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("[PROBE T32-PRICES] partner rows unreadable: " + ex.Message); }
            return res;
        }
#endif
    }

    [HarmonyPatch(typeof(PricingManagerPlanUI), nameof(PricingManagerPlanUI.LoadPlan))]
    public static class Patch_ProbeT32_LoadPlan
    {
        static void Prefix(PricingManagerPlan plan) { ProbeT32.Arm(plan, "screen"); }
    }

    [HarmonyPatch(typeof(PricingManagerPlan), nameof(PricingManagerPlan.RecomputeSuggestions))]
    public static class Patch_ProbeT32_Recompute
    {
        static void Postfix(PricingManagerPlan __instance) { ProbeT32.AfterRecompute(__instance); }
    }
}
// PROBE-END: P-T32-PRICES
