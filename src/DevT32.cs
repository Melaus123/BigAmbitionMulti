#if BAMP_DEV
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Buildings.Office.Headquarters;             // PricingManagerPlan, PricingManagerHelper, PriceSuggestion
using HarmonyLib;                                // AccessTools (PartnerPricingRows)
using Helpers;                                   // EmployeeHelper, UpdateSecurityLevel (extension)
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>T-32 (prices) DEV test levers, dev build only; they drive tools/scenarios/t-pricesuggest.json.
    /// Moved here from the removed log-only probe P-T32-PRICES (.modding/04-probes.md) once T-32 was fixed and
    /// proven (commit 465e6e4): same levers, same result lines, no probe logging and no Harmony patches.</summary>
    internal static class DevT32
    {
        private const int MaxProducts = 30;   // products the result names in sugg= (sig= covers the whole list)

        private static string F(float v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }

        /// <summary>DEV lever `pricesuggest <planId|first>`: runs what the screen runs on open
        /// (PricingManagerPlanUI.LoadPlan:47, RecomputeSuggestions) on one pricing plan without opening any UI,
        /// and answers the plan's list as that recompute left it (Summary). `pricesuggest make [neighbourhood]` creates
        /// one plan first (DevMakePlan); `pricesuggest recruit` gets this machine a pricing-manager candidate for its
        /// headquarters first (DevRecruit); `pricesuggest shift <station> <employee> <fromHour>` puts an employee on a
        /// headquarters desk shift (DevShift). `first` = this machine's own REAL plan with a
        /// manager (lowest id), else a partner's copy with a manager. An id looks in the partner copies first
        /// (CompanyPlans._rows, as CompanyPlans.PlanObjectOf does), then in this machine's own list.
        /// T-32: on a partner's copy the recompute is skipped (PricingPlanCopies), so the summary is the list that
        /// copy's screen shows (`copy=True`, `sig=` the whole list's fingerprint); `pricesuggest ask <id>` (DevAsk)
        /// does what opening that screen does - the routed `recompute` to the plan's runner; `pricesuggest peek <id>`
        /// reports the plan's list as it stands, without recomputing.</summary>
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
                if (want.StartsWith("shift ", StringComparison.Ordinal)) return DevShift(gi, want.Substring(6).Trim());
                if (want.StartsWith("ask ", StringComparison.Ordinal)) return DevAsk(want.Substring(4).Trim());
                bool peek = want.StartsWith("peek ", StringComparison.Ordinal);   // the list as it stands, no recompute
                if (peek) want = want.Substring(5).Trim();
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
                if (!peek) pick.RecomputeSuggestions();
                return "OK pricesuggest " + Summary(pick) + " own=" + own.Count + " partnerRows=" + partnerRows.Count;
            }
            catch (Exception ex) { return "ERR pricesuggest: " + ExText(ex); }
        }

        /// <summary>The plan as the lever's result names it, read from the plan as it stands (for `pricesuggest <id>`
        /// right after the lever's own RecomputeSuggestions): `agreement=<id> manager=<employee id>
        /// managerFoundHere=<bool> skill=<pricing-manager skill> stores=<manageable businesses in the supervised
        /// neighbourhood here> suggestions=<n> sugg=<item:suggestedMax;...> (first MaxProducts by item name, '-' when
        /// none) sig=<PricingPlanCopies fingerprint of the whole list> copy=<a partner's copy>`.</summary>
        private static string Summary(PricingManagerPlan plan)
        {
            string id = plan.id ?? "";
            string nb = plan.supervisedNeighborhood ?? "";
            string emp = plan.assignedEmployeeId ?? "";
            bool empFound = false; float skill = 0f;
            try { empFound = plan.AnalystInstance != null; skill = plan.PricingManagerSkillValue; } catch { }
            int stores = 0;
            try
            {
                foreach (var r in SaveGameManager.Current.BuildingRegistrations)
                    if (r != null && PricingManagerHelper.IsManageableBusiness(r) && r.Neighborhood == nb) stores++;
            }
            catch { }
            var list = new List<PriceSuggestion>();
            if (plan.cachedSuggestions != null)
                foreach (var sg in plan.cachedSuggestions) if (sg != null && !string.IsNullOrEmpty(sg.itemName)) list.Add(sg);
            list.Sort((a, b) => string.CompareOrdinal(a.itemName, b.itemName));
            var sb = new StringBuilder();
            int n = 0;
            foreach (var sg in list)
            {
                if (n >= MaxProducts) break;
                n++;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(sg.itemName).Append(':').Append(F(sg.suggestedMax));
            }
            return "agreement=" + id + " manager=" + emp + " managerFoundHere=" + empFound + " skill=" + F(skill)
                 + " stores=" + stores + " suggestions=" + list.Count + " sugg=" + (sb.Length == 0 ? "-" : sb.ToString())
                 + " sig=" + PricingPlanCopies.Digest(PricingPlanCopies.Signature(plan)) + " copy=" + CompanyPlans.IsOverlayPlan(plan);
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
        /// When every suitable workstation is already on a shift, ONE is freed first (FreeOneShift) and the line
        /// names it as freed=employee@station/from-to (freed=none when a station was already free).
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

            int suitable;
            var station = FreeStation(hq, out suitable);
            string freed = "none";
            if (station == null && suitable > 0)
            {
                string err = FreeOneShift(gi, hq, hqKey, (int)day.day, out freed);
                if (err.Length > 0)
                    return "ERR pricesuggest recruit: headquarters " + hqKey + " has no free workstation for " + PricingSkill + " (suitable="
                         + suitable + ", every one already on a shift) and none could be freed: " + err;
                station = FreeStation(hq, out suitable);
            }
            if (station == null)
                return "ERR pricesuggest recruit: headquarters " + hqKey + " has no free workstation for " + PricingSkill + " (suitable="
                     + suitable + ", every one already on a shift" + (freed == "none" ? "" : ", even after removing the shift " + freed) + ")";

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
                 + " suitable=" + suitable + " freed=" + freed + " agency='" + agencyKey + "' hq=" + hqKey;
        }

        /// <summary>The workstation `pricesuggest recruit` names: an assignable item of the headquarters
        /// (BuildingRegistration.GetAssignableItems :621-640) whose suitableSkills hold ba:skill_pricingmanager, not a
        /// cleaning station, used by no shift on any day; the page's order (itemName, then id). `suitable` counts
        /// every such item, used or not.</summary>
        private static BigAmbitions.Items.ItemInstance FreeStation(BuildingRegistration hq, out int suitable)
        {
            var items = new List<BigAmbitions.Items.ItemInstance>();
            hq.GetAssignableItems(items);
            var taken = new HashSet<string>(StringComparer.Ordinal);
            if (hq.scheduleDays != null)
                foreach (var sd in hq.scheduleDays)
                    if (sd?.workShifts != null)
                        foreach (var w in sd.workShifts) if (w?.itemInstanceId != null) taken.Add(w.itemInstanceId);
            BigAmbitions.Items.ItemInstance station = null;
            suitable = 0;
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
            return station;
        }

        /// <summary>Frees ONE pricing-manager workstation of the headquarters at DATA level, with no phone screen open
        /// (ScheduleHelper's statics - Business, CurrentScheduleDay, WorkstationsById, EmployeesById, the shift caches -
        /// exist only while the BizMan Schedule tab is built, and opening that tab headless failed on the rig). It does what
        /// the shift slider's remove does (WorkShiftSlider.RemoveWorkShift :208-211 -> ScheduleHelper.RemoveWorkShift
        /// :334-355) through the game's own data calls: a headquarters desk shift comes off every open day by
        /// ScheduleDay.RemoveWorkShift (ScheduleDay.cs:26-33: this object on its own day, its copy - same start, end,
        /// employee - on the others), then the employee update of ScheduleHelper.UpdateEmployeeAfterWorkShiftChange
        /// (:231-250: weekly hours, station items, security level, idle + EmployeeIdle to-do when no shift is left,
        /// UpdateHQPlans :414-456 run over this headquarters' address, the to-do sweep flag, MarkChange). Left out, both
        /// display-only: BizManBusiness.UpdateSecurityInfo and the Schedule page's redraw events.
        /// The shift: on day `dayNo`, on a suitable workstation (an assignable item - GetAssignableItems :621-640, the
        /// page's FetchWorkstations source - with ba:skill_pricingmanager, not cleaning), held by an employee
        /// (EmployeeHelper.EmployeeInstancesDictionary) who does NOT have ba:skill_pricingmanager and is no plan's assigned
        /// manager (gi.pricingManagerPlans), not a partner's stand-in row (SharedShopSchedule.IsSynthetic); the page's order
        /// (station itemName, station id, starting hour, employee id). Returns "" on success, else the reason;
        /// `freed` = employee@station/from-to.</summary>
        private static string FreeOneShift(GameInstance gi, BuildingRegistration hq, string hqKey, int dayNo, out string freed)
        {
            freed = "none";
            ScheduleDay cur = null;
            if (hq.scheduleDays != null)
                foreach (var sd in hq.scheduleDays) if (sd != null && (int)sd.day == dayNo) { cur = sd; break; }
            if (cur == null) return "day " + dayNo + " is not in the headquarters' schedule";
            var items = new List<BigAmbitions.Items.ItemInstance>();
            hq.GetAssignableItems(items);
            var wbi = new Dictionary<string, BigAmbitions.Items.ItemInstance>(StringComparer.Ordinal);
            foreach (var it in items) if (it?.id != null) wbi[it.id] = it;
            var onPlan = new HashSet<string>(StringComparer.Ordinal);
            if (gi.pricingManagerPlans != null)
                foreach (var p in gi.pricingManagerPlans) if (!string.IsNullOrEmpty(p?.assignedEmployeeId)) onPlan.Add(p.assignedEmployeeId);
            WorkShift pick = null;
            BigAmbitions.Items.ItemInstance pickSt = null;
            Entities.EmployeeInstance pickEmp = null;
            int shifts = 0, pricing = 0, planned = 0, standIn = 0;
            if (cur.workShifts != null)
                foreach (var w in cur.workShifts)
                {
                    if (w?.itemInstanceId == null || w.employeeId == null) continue;
                    if (!wbi.TryGetValue(w.itemInstanceId, out var it) || it == null) continue;
                    var skills = it.ItemCached?.suitableSkills;
                    if (skills == null || Array.IndexOf(skills, PricingSkill) < 0) continue;
                    if (UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.IsCleaningStation(it)) continue;
                    shifts++;
                    if (SharedShopSchedule.IsSynthetic(w.employeeId)) { standIn++; continue; }
                    if (onPlan.Contains(w.employeeId)) { planned++; continue; }
                    if (!EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(w.employeeId, out var emp) || emp == null) continue;
                    if (emp.HasSkill(PricingSkill)) { pricing++; continue; }
                    int c = pick == null ? -1 : string.CompareOrdinal(it.itemName, pickSt.itemName);
                    if (c == 0) c = string.CompareOrdinal(it.id, pickSt.id);
                    if (c == 0) c = w.startingHour.CompareTo(pick.startingHour);
                    if (c == 0) c = string.CompareOrdinal(w.employeeId, pick.employeeId);
                    if (c < 0) { pick = w; pickSt = it; pickEmp = emp; }
                }
            if (pick == null)
                return "no removable shift on day " + dayNo + " (suitable-station shifts=" + shifts + ": pricing managers=" + pricing
                     + ", on a plan=" + planned + ", stand-ins=" + standIn + ")";
            freed = pick.employeeId + "@" + pick.itemInstanceId + "/" + pick.startingHour + "-" + pick.endingHour;
            foreach (var sd in hq.scheduleDays)
                if (sd != null && sd.isOpen) sd.RemoveWorkShift(pick);
            AfterShiftChange(hq, pickEmp);
            return "";
        }

        /// <summary>The employee and headquarters update the game runs after a work-shift change on the Schedule tab
        /// (ScheduleHelper.UpdateEmployeeAfterWorkShiftChange :231-250, single employee): weekly hours and days over the
        /// headquarters' schedule, station items, security level for an affectssecurity skill, idle + EmployeeIdle to-do
        /// when no shift is left, UpdateHQPlans (:414-456) over this headquarters' address, the to-do sweep flag,
        /// MarkChange. Left out, display-only: BizManBusiness.UpdateSecurityInfo.</summary>
        private static void AfterShiftChange(BuildingRegistration hq, Entities.EmployeeInstance emp)
        {
            emp.UpdateWeeklyHoursAndDays(hq.scheduleDays);
            emp.UpdateAssignedWorkStationItems();
            if (emp.HasAnySkillWithTag(BigAmbitions.Tags.TagRef.Skilltag.affectssecurity)) hq.UpdateSecurityLevel();
            if (!emp.IsAssignedToAnyWorkShift()) { emp.UnAssignWork(); emp.AddTodoTask(Entities.TodoTaskType.EmployeeIdle); }
            var at = hq.Address;
            foreach (var p in HeadhunterHelper.GetAssignedPlansForHeadquarters(at))
                if (p.HeadhunterInstance != null && !p.HeadhunterInstance.IsAssignedToAnyWorkShift()) p.assignedEmployeeId = null;
            foreach (var p in HrManagerHelper.GetAssignedPlansForHeadquarters(at))
                if (p.HrManagerInstance != null && !p.HrManagerInstance.IsAssignedToAnyWorkShift()) p.UnAssignEmployee();
            foreach (var p in LogisticsManagerHelper.GetAssignedPlansForHeadquarters(at))
                if (p.LogisticsManagerInstance != null && !p.LogisticsManagerInstance.IsAssignedToAnyWorkShift()) p.assignedEmployeeId = null;
            foreach (var p in PurchasingAgentHelper.GetAssignedPlansForHeadquarters(at))
                if (p.PurchasingAgentInstance != null && !p.PurchasingAgentInstance.IsAssignedToAnyWorkShift()) p.UnAssignEmployee();
            foreach (var p in PricingManagerHelper.GetPlansForHeadquarters(at))
                if (p.AnalystInstance != null && !p.AnalystInstance.IsAssignedToAnyWorkShift()) p.UnAssignEmployee();
            var ui = InstanceBehavior<UI.UIs>.Instance;
            if (ui != null && ui.tasksUI != null) ui.tasksUI.forceCheckForCompletedTodoTasks = true;
            SaveGameManager.MarkChange();
        }

        /// <summary>DEV lever `pricesuggest shift <station> <employee> <fromHour>`: puts the employee on a work shift at a
        /// headquarters desk at DATA level, with no phone screen open (the TestDrive `shiftat` lever drives the Schedule
        /// tab, whose ScheduleHelper statics exist only while that tab is built - NullReference headless on the rig, run
        /// T-PRICESUGGEST-20261113-161858 step 7). It does what picking the employee on the Schedule tab does
        /// (ScheduleEmployeeSelection.OnEmployeeSelected :60 -> ScheduleHelper.AddWorkShift :292-327) through the
        /// game's own data call: a headquarters desk (not a cleaning station) shift spans the day's first opening slot
        /// (:295-300 - the picked hour is replaced by the slot's start), type Default (GetWorkShiftType :458-465), and a
        /// copy goes onto EVERY open day by ScheduleDay.AddWorkShift (ScheduleDay.cs:21-24; :315-321 - the game's
        /// workShift.Copy(), here WorkShift.Clone(), WorkShift.cs:19-22, a memberwise copy of the same five fields);
        /// then the employee/headquarters update (AfterShiftChange) and the game event ba:gameevent_employeeassigned
        /// (:326). Left out, display-only: the shift cache and the page's redraw events (:322-325).
        /// Headquarters: the rented one whose assignable items (GetAssignableItems :621-640) hold the station; the
        /// slot: its first open schedule day with opening hours (the day `pricesuggest recruit` names). Refused when the
        /// station already holds a shift on any day (the page offers only empty cells) or the employee is unknown.
        /// asked= echoes the hour given; shift= is the one the game's rule made.</summary>
        private static string DevShift(GameInstance gi, string args)
        {
            try
            {
                var a = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int asked;
                if (a.Length != 3 || !int.TryParse(a[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out asked))
                    return "ERR pricesuggest shift: usage pricesuggest shift <station> <employee> <fromHour>";
                string stId = a[0], empId = a[1];
                if (gi.BuildingRegistrations == null) return "ERR pricesuggest shift: no save loaded";
                BuildingRegistration hq = null;
                BigAmbitions.Items.ItemInstance st = null;
                var items = new List<BigAmbitions.Items.ItemInstance>();
                foreach (var r in gi.BuildingRegistrations)
                {
                    if (r == null || !r.RentedByPlayer || r.businessTypeName != "ba:businesstype_headquarters") continue;
                    items.Clear();
                    r.GetAssignableItems(items);
                    foreach (var it in items) if (it?.id == stId) { st = it; break; }
                    if (st != null) { hq = r; break; }
                }
                if (hq == null) return "ERR pricesuggest shift: station " + stId + " is no assignable item of a headquarters rented here";
                string hqKey = null; try { hqKey = GameStateReader.AddressKey(hq.Address); } catch { }
                if (UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.IsCleaningStation(st))
                    return "ERR pricesuggest shift: station " + stId + " is a cleaning station (its shift is per day, not a headquarters desk shift)";
                if (!EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(empId, out var emp) || emp == null)
                    return "ERR pricesuggest shift: no employee " + empId + " here (hire it first)";
                if (hq.scheduleDays == null) return "ERR pricesuggest shift: headquarters " + hqKey + " has no schedule";
                ScheduleDay day = null;
                int open = 0;
                foreach (var sd in hq.scheduleDays)
                {
                    if (sd == null || !sd.isOpen) continue;
                    open++;
                    if (sd.workShifts != null)
                        foreach (var w in sd.workShifts)
                            if (w?.itemInstanceId == stId)
                                return "ERR pricesuggest shift: station " + stId + " already holds the shift " + w.employeeId + "/" + w.startingHour + "-" + w.endingHour + " on day " + (int)sd.day;
                    if (day == null && sd.openingHourSlots != null && sd.openingHourSlots.Count > 0) day = sd;
                }
                if (day == null) return "ERR pricesuggest shift: headquarters " + hqKey + " has no open schedule day with opening hours";
                var slot = day.openingHourSlots[0];
                var shift = new WorkShift
                {
                    startingHour = slot.startingHour,
                    itemInstanceId = stId,
                    employeeId = empId,
                    type = WorkShiftType.Default,
                    endingHour = slot.endingHour
                };
                int days = 0;
                foreach (var sd in hq.scheduleDays)
                    if (sd != null && sd.isOpen) { sd.AddWorkShift(shift.Clone()); days++; }
                AfterShiftChange(hq, emp);
                GameEvent.Invoke("ba:gameevent_employeeassigned");
                string shiftTxt = shift.startingHour + "-" + shift.endingHour;
                return "OK pricesuggest shift st=" + stId + " emp=" + empId + " day=" + (int)day.day + " asked=" + asked + " shift=" + shiftTxt
                     + " days=" + days + "/" + open + " assigned=" + emp.IsAssignedToAnyWorkShift() + " addErr=none hq=" + hqKey;
            }
            catch (Exception ex) { return "ERR pricesuggest shift: " + ExText(ex); }
        }

        /// <summary>An exception as the lever's ERR line carries it: message, type and the first 3 stack frames, so a
        /// failure names its line.</summary>
        private static string ExText(Exception ex)
        {
            var sb = new StringBuilder(ex.Message + " (" + ex.GetType().Name + ")");
            int n = 0;
            foreach (var line in (ex.StackTrace ?? "").Split('\n'))
            {
                string t = line.Trim();
                if (t.Length == 0) continue;
                sb.Append(" | ").Append(t);
                if (++n == 3) break;
            }
            return sb.ToString();
        }

        /// <summary>DEV lever `pricesuggest ask <planId>` (T-32): PricingPlanCopies.AskRunner on a partner's copy,
        /// exactly as opening its pricing screen does.  The answer arrives with the runner's next bundle
        /// ([PricingCopies] plan ... the runner's list is in).</summary>
        private static string DevAsk(string id)
        {
            foreach (var p in PartnerPricingRows())
                if (p.id == id)
                    return PricingPlanCopies.AskRunner(p, "lever: plan screen opened")
                        ? "OK pricesuggest asked agreement=" + id
                        : "ERR pricesuggest ask: '" + id + "' is not a partner's copy here";
            return "ERR pricesuggest ask: no partner copy '" + id + "' here";
        }

        /// <summary>The partner pricing copies CompanyPlans holds (private `_rows`, read by reflection - a DEV
        /// lever does not edit CompanyPlans.cs). When none is built yet, CompanyPlans' own MaterialiseRows is run
        /// for every partner it holds lists of, as opening the page would. A read failure throws, so the lever
        /// answers ERR.</summary>
        private static List<PricingManagerPlan> PartnerPricingRows()
        {
            var res = new List<PricingManagerPlan>();
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
            return res;
        }
    }
}
#endif
