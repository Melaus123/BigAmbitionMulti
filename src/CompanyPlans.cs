using System.Reflection;
using Buildings.Office.Headquarters;
using Entities;          // ImportPartnership lives here, not with the four HQ plan types
using Helpers;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// MERGER PHASE 4c PART 1 - HQ OPERATIONS: the UNION VIEW of the company's headquarters plans
    /// (D20-6, user 2026-09-11).
    ///
    /// A headquarters plan stays owned by the headquarters that created it: `headquartersAddress` is
    /// the key every native helper filters on (PricingManagerHelper.cs:126, PurchasingAgentHelper.cs:14,
    /// HrManagerHelper.cs:30, HeadhunterHelper.cs:61) and the game has nowhere to put a plan that
    /// belongs to the company rather than to one building.  "Union" therefore means: open ANY member's
    /// headquarters and its own plans are on the page - a partner's headquarters, flipped onto this
    /// machine by the merger, shows THAT partner's plans.
    ///
    /// SCREEN LAYER ONLY (rule 5, and the highest risk in the design read).  These four families are
    /// NEVER installed into this machine's game lists, not even tagged.  HrManagerPlan.TrainEmployees /
    /// PayHealthInsurance run from Entities/HRManager.cs:31-38 (WorkDaily) and
    /// HeadhunterPlan.CheckForRecruiting from Entities/Headhunter.cs:50-57 (WorkHourly) - driven by the
    /// EMPLOYEE, looked up by assignedEmployeeId, and NOT inside the authority veil.  With
    /// MergerEmployeeSync replicating employees, an installed copy plus a replicated manager would
    /// train, insure and recruit twice and charge the shared wallet twice, silently.  So the rows here
    /// are DETACHED objects, built fresh from the wire DTOs, handed to the list's own row builder and
    /// dropped when the feed changes.  (The fifth family, LOGISTICS, keeps wave 4's TAGGED INSTALLED
    /// display copies: its execution pass is already guarded by the S3 leg gate and the display tag, and
    /// its rows are what wave 4's routed edits mutate in place.)
    ///
    /// The registry is fed by the SAME fan-out wave 4 built - CompanyLists (MessageType 213), which now
    /// carries all five families - so there is no new message type and no second cadence.
    /// INERT without a merger: every entry point returns on one MergerSync.IAmMember / registry-empty
    /// read.  MAIN THREAD only (the feed arrives through CompanyLists.Receive, which is enqueued).
    /// </summary>
    public static class CompanyPlans
    {
        /// <summary>owner pid -> that owner's HQ plan families, as last published.  The whole registry:
        /// nothing here is ever written into a game list.</summary>
        private static readonly Dictionary<string, CompanyListsPayload> _byOwner = new();

        /// <summary>Owners this machine is currently STANDING IN for.  Their real plans are installed by
        /// the absence installer and the page must show those, not a second detached copy.</summary>
        private static readonly HashSet<string> _suspended = new();

        /// <summary>(owner|family|planId) -> the detached row object, so the same plan keeps ONE identity
        /// across refreshes.  That identity is what IsOverlayPlan tests, so a native commit landing on a
        /// partner's row can be refused rather than silently mutating a throwaway.  The FAMILY is in the key
        /// (r2 MINOR-4) so one tab's rebuild drops only its own rows and leaves the other tabs' rows - which
        /// are still on screen and must still be refusable - alone.</summary>
        private static readonly Dictionary<string, object> _rows = new(StringComparer.Ordinal);

        /// <summary>Row object -> the pid it belongs to (reference identity, so two plans that share an
        /// id across owners never collide).</summary>
        private static readonly Dictionary<object, string> _rowOwner = new(ReferenceComparer.Instance);

        private static readonly HashSet<string> _refusalLogged = new();

        /// <summary>r2 MINOR-4: "owner|family" pairs whose rows are OUT OF DATE because a newer feed
        /// arrived.  A receipt does not redraw, so the rows stay on screen - and stay refusable - until the
        /// list that owns them is rebuilt; ShowPartnerRows drops them there, immediately before re-adding
        /// (dropping them in Receive emptied _rowOwner for live rows, so IsOverlayPlan turned false and
        /// RefuseRow stopped refusing them).</summary>
        private static readonly HashSet<string> _stale = new(StringComparer.Ordinal);

        /// <summary>The four screen-layer families, in the order the tabs sit in.  Logistics is not here:
        /// it keeps wave 4's tagged installed display copies.</summary>
        private static readonly string[] Families = { "pricing", "purchasing", "hr", "headhunter" };

        public static int OwnerCount => _byOwner.Count;

        // -- registry feed (called from CompanyLists, which owns the wire) -----

        /// <summary>MAIN THREAD.  One owner's lists arrived.  Only the four screen-layer families are
        /// kept here; the contracts and the logistics plans stay with CompanyLists' installer.</summary>
        public static void Receive(CompanyListsPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.OwnerPid)) return;
                if (p.OwnerPid == MPConfig.PlayerId) return;
                if (!MergerSync.IAmMember) { ClearOwner(p.OwnerPid, "this machine is not a company member"); return; }
                _byOwner[p.OwnerPid] = p;
                foreach (var f in Families) _stale.Add(p.OwnerPid + "|" + f);   // r2 MINOR-4
                RefreshOpenTabsFor(p.OwnerPid);
                Plugin.Logger.LogInfo($"[Plans] received {p.OwnerPid}: pricing {p.PricingManagerPlans?.Count ?? 0} "
                                    + $"logistics {p.LogisticsManagerPlans?.Count ?? 0} purchasing {p.ImportPartnerships?.Count ?? 0} "
                                    + $"hr {p.HrManagerPlans?.Count ?? 0} headhunter {p.HeadhunterPlans?.Count ?? 0}");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] receive: {ex.Message}"); }
        }

        public static void ClearOwner(string ownerPid, string why)
        {
            try
            {
                if (string.IsNullOrEmpty(ownerPid)) return;
                bool had = _byOwner.Remove(ownerPid);
                _suspended.Remove(ownerPid);
                DropRowsOf(ownerPid);
                if (had) Plugin.Logger.LogInfo($"[Plans] cleared ({why}: '{ownerPid}')");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] clear '{ownerPid}': {ex.Message}"); }
        }

        public static void ClearAll(string why)
        {
            if (_byOwner.Count == 0 && _rows.Count == 0) { _suspended.Clear(); return; }
            _byOwner.Clear(); _suspended.Clear(); _rows.Clear(); _rowOwner.Clear(); _refusalLogged.Clear(); _stale.Clear();
            Plugin.Logger.LogInfo($"[Plans] cleared ({why})");
        }

        /// <summary>This machine has become the STAND-IN for that owner: its REAL plans are installed by
        /// the absence installer, so the overlay must step aside (both sets would double every row).  The
        /// registry entry is kept, exactly as CompanyLists keeps it, for the return leg.</summary>
        public static void SuspendOwner(string ownerPid, string why)
        {
            if (string.IsNullOrEmpty(ownerPid) || !_byOwner.ContainsKey(ownerPid)) return;
            if (_suspended.Add(ownerPid))
            {
                DropRowsOf(ownerPid);
                Plugin.Logger.LogInfo($"[Plans] cleared (suspended for '{ownerPid}': {why})");
            }
        }

        public static void ReinstallOwner(string ownerPid, string why)
        {
            if (string.IsNullOrEmpty(ownerPid)) return;
            if (_suspended.Remove(ownerPid))
                Plugin.Logger.LogInfo($"[Plans] overlay resumed for '{ownerPid}' - {why}");
        }

        private static void DropRowsOf(string ownerPid) => DropRows(ownerPid + "|");

        /// <summary>r2 MINOR-4: one owner's rows of ONE family - what a single tab's rebuild replaces.</summary>
        private static void DropRowsOf(string ownerPid, string family) => DropRows(ownerPid + "|" + family + "|");

        private static void DropRows(string prefix)
        {
            var kill = new List<string>();
            foreach (var kv in _rows) if (kv.Key.StartsWith(prefix, StringComparison.Ordinal)) kill.Add(kv.Key);
            foreach (var k in kill) { if (_rows.TryGetValue(k, out var o) && o != null) _rowOwner.Remove(o); _rows.Remove(k); }
        }

        /// <summary>r2 MINOR-4, THE REDRAW.  A feed arrived for an owner whose headquarters is the page on
        /// screen: ask each of the four lists that is actually active to rebuild itself through its OWN
        /// private refresh (PricingManagersPlanList.RefreshPlansList :90, HrManagersPlanList :82,
        /// HeadhuntersPlanList :73 and PurchasingAgentsPlanList.RefreshManagersList :71) - the same seam
        /// SharedShopWorkTabs uses for the warehouse tabs.  Each rebuild runs the overlay postfix, which
        /// drops that family's stale rows and re-adds them from the new feed.  MAIN THREAD: the only caller
        /// is Receive, which CompanyLists already enqueues.</summary>
        private static void RefreshOpenTabsFor(string ownerPid)
        {
            try
            {
                if (string.IsNullOrEmpty(ownerPid)) return;
                if (!PartnerHqOpen(out _, out var open) || open != ownerPid) return;
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var bm = ui != null && ui.fullMenu != null ? ui.fullMenu.bizMan : null;
                if (bm == null) return;
                int n = 0;
                n += Rebuild(bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagersPlanList>(true), "RefreshPlansList");
                n += Rebuild(bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.PurchasingAgentsPlanList>(true), "RefreshManagersList");
                n += Rebuild(bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.HRManagers.HrManagersPlanList>(true), "RefreshManagersList");
                n += Rebuild(bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.Headhunters.HeadhuntersPlanList>(true), "RefreshManagersList");
                if (n > 0) Plugin.Logger.LogInfo($"[Plans] redrew {n} open tab(s) for '{ownerPid}' - a newer feed arrived.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] redraw for '{ownerPid}': {ex.Message}"); }
        }

        private static int Rebuild(Component list, string method)
        {
            try
            {
                if (list == null || !list.gameObject.activeInHierarchy) return 0;   // only the tab on screen
                var m = list.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (m == null) return 0;
                m.Invoke(list, null);
                return 1;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] {method}: {ex.Message}"); return 0; }
        }

        // -- H1: is the open BizMan page a PARTNER's headquarters? -------------

        /// <summary>The BizMan page currently on screen is a merged PARTNER's headquarters run on another
        /// machine.  False for this player's own headquarters (the list stays native), for a headquarters
        /// this machine is standing in for (the real plans are installed there) and off a merger.</summary>
        public static bool PartnerHqOpen(out string hqKey, out string ownerPid)
        {
            hqKey = ""; ownerPid = "";
            try
            {
                if (_byOwner.Count == 0 || !MergerSync.IAmMember) return false;
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var biz = ui != null && ui.fullMenu != null && ui.fullMenu.bizMan != null ? ui.fullMenu.bizMan.business : null;
                var reg = biz != null ? biz.buildingRegistration : null;
                if (reg == null) return false;
                if (reg.businessTypeName != "ba:businesstype_headquarters") return false;
                bool rented; try { rented = reg.RentedByPlayer; } catch { return false; }
                if (!rented || MergerFlip.TrulyMine(reg)) return false;
                hqKey = GameStateReader.AddressKey(reg);
                if (string.IsNullOrEmpty(hqKey)) return false;
                if (MergerAbsence.SimulatesHere(hqKey)) { hqKey = ""; return false; }
                if (!CompanyLists.TryOwnerOfAddress(hqKey, out ownerPid) || string.IsNullOrEmpty(ownerPid)) { hqKey = ""; return false; }
                if (_suspended.Contains(ownerPid)) { hqKey = ""; ownerPid = ""; return false; }
                return _byOwner.ContainsKey(ownerPid);
            }
            catch { hqKey = ""; ownerPid = ""; return false; }
        }

        /// <summary>H1 DRIVER.  Build the partner rows of one family for the open headquarters and hand
        /// each to the list's OWN row builder, then tint what that builder just added.  `setUp` is the
        /// native private SetUpPlanEntry; `rowParent` is the template's parent, whose newest children are
        /// the rows the builder created.</summary>
        public static int ShowPartnerRows(string family, Transform rowParent, Action<object> setUp)
        {
            if (setUp == null) return 0;
            if (!PartnerHqOpen(out var hq, out var owner)) return 0;
            // r2 MINOR-4: the rebuild that replaces them is the moment the old row objects may go.
            if (_stale.Remove(owner + "|" + family)) DropRowsOf(owner, family);
            var plans = RowsFor(owner, hq, family);
            if (plans.Count == 0) return 0;
            bool tinted = PlayerColours.TryColourFor(owner, out var tint);
            int shown = 0, refused = 0;
            foreach (var row in plans)
            {
                int before = rowParent != null ? rowParent.childCount : 0;
                try { setUp(row); shown++; }
                catch (Exception ex)
                {
                    refused++;
                    if (_refusalLogged.Add(family + "|" + owner))
                        Plugin.Logger.LogWarning($"[Plans] a {family} row of '{owner}' could not be drawn: {ex.Message} (once per family and owner).");
                    continue;
                }
                if (rowParent == null) continue;
                for (int i = before; i < rowParent.childCount; i++)
                {
                    StripDragHandle(rowParent.GetChild(i));
                    if (tinted) TintRow(rowParent.GetChild(i), tint);
                }
            }
            if (shown > 0) Plugin.Logger.LogInfo($"[Plans] shown {shown} partner rows ({family}, {hq})");
            if (refused > 0) Plugin.Logger.LogInfo($"[Plans] {refused} partner {family} row(s) of '{owner}' not drawn for '{hq}'.");
            return shown;
        }

        /// <summary>r2 MAJOR-1 (a): a partner row must never become DRAGGABLE.  ReorderableList.OnEnable and
        /// Reinitialize are both `CoroutineUtility.RunAfterOneFrame(InitializeItems)` (ReorderableList.cs:42-51)
        /// and InitializeItems (:52-63) enrols EVERY active child that has a ReorderableListItem - so a row
        /// added by the postfix in this frame would be enrolled next frame, and a drag would then index the
        /// NATIVE list (which holds none of these rows) out of range.  The component is taken off the row the
        /// instant it is built: DestroyImmediate, not Destroy, because Destroy only takes effect at the end of
        /// the current frame and would leave the component alive for anything that re-initialises the list
        /// synchronously in this same frame; DestroyImmediate guarantees InitializeItems can never see it.</summary>
        private static void StripDragHandle(Transform entry)
        {
            try
            {
                if (entry == null) return;
                foreach (var it in entry.GetComponentsInChildren<UI.Components.ReorderableListItem>(true))
                    if (it != null) UnityEngine.Object.DestroyImmediate(it);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] drag handle: {ex.Message}"); }
        }

        /// <summary>D18 colour: the row's own name/manager labels take the owner's colour.  Sums are left
        /// plain - there are none on these rows.  No new text and no new element: only a colour changes.</summary>
        private static void TintRow(Transform entry, Color32 c)
        {
            try
            {
                if (entry == null) return;
                foreach (var t in entry.GetComponentsInChildren<TMPro.TMP_Text>(true))
                {
                    if (t == null) continue;
                    string n = t.gameObject.name ?? "";
                    if (n.IndexOf("Manager", StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("Contract", StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("Name", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    t.color = c;
                }
            }
            catch { }
        }

        // -- the detached row objects -----------------------------------------

        /// <summary>The partner's rows of one family for one headquarters, built once and cached.</summary>
        private static List<object> RowsFor(string owner, string hqKey, string family)
        {
            var rows = new List<object>();
            if (!_byOwner.TryGetValue(owner, out var p) || p == null) return rows;
            switch (family)
            {
                case "pricing":
                    foreach (var d in p.PricingManagerPlans ?? new List<PwPricingPlan>())
                    { var dd = d; Add(rows, owner, family, hqKey, dd?.Id, dd?.HeadquartersAddressKey, () => BuildPricing(dd)); }
                    break;
                case "purchasing":
                    foreach (var d in p.ImportPartnerships ?? new List<PwImportPartnership>())
                    { var dd = d; Add(rows, owner, family, hqKey, dd?.Id, dd?.HeadquartersAddressKey, () => BuildPurchasing(dd)); }
                    break;
                case "hr":
                    foreach (var d in p.HrManagerPlans ?? new List<PwHrPlan>())
                    { var dd = d; Add(rows, owner, family, hqKey, dd?.Id, dd?.HeadquartersAddressKey, () => BuildHr(dd)); }
                    break;
                case "headhunter":
                    foreach (var d in p.HeadhunterPlans ?? new List<PwHeadhunterPlan>())
                    { var dd = d; Add(rows, owner, family, hqKey, dd?.Id, dd?.HeadquartersAddressKey, () => BuildHeadhunter(dd)); }
                    break;
            }
            return rows;
        }

        private static void Add(List<object> into, string owner, string family, string hqKey, string id, string planHq, Func<object> build)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (!Same(planHq, hqKey)) return;
            string key = owner + "|" + family + "|" + id;
            if (!_rows.TryGetValue(key, out var row) || row == null)
            {
                try { row = build(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] row {id} of '{owner}': {ex.Message}"); return; }
                if (row == null) return;
                _rows[key] = row; _rowOwner[row] = owner;
            }
            into.Add(row);
        }

        private static object BuildPricing(PwPricingPlan d)
        {
            var pl = new PricingManagerPlan
            {
                assignedEmployeeId = d.AssignedEmployeeId,
                headquartersAddress = AddrOf(d.HeadquartersAddressKey),
                supervisedNeighborhood = d.SupervisedNeighborhood,
                nextUpdateDay = d.NextUpdateDay,
                nextUpdateHour = d.NextUpdateHour,
            };
            SetId(pl, d.Id);
            if (pl.manuallyPricedItems != null)
                foreach (var s in d.ManuallyPricedItems ?? new List<string>()) if (!string.IsNullOrEmpty(s)) pl.manuallyPricedItems.Add(s);
            return pl;
        }

        private static object BuildPurchasing(PwImportPartnership d)
        {
            var ip = new ImportPartnership
            {
                id = d.Id,
                headquartersAddress = AddrOf(d.HeadquartersAddressKey),
                importAddress = AddrOf(d.ImportAddressKey),
                employeeInstanceId = d.EmployeeInstanceId,
                nextDeliveryDay = d.NextDeliveryDay,
                isRepeatingOrder = d.IsRepeatingOrder,
                daysUntilRepeat = d.DaysUntilRepeat,
                isActive = d.IsActive,
                isUrgentOrder = d.IsUrgentOrder,
                isTarget = d.IsTarget,
            };
            // The purchasing row names the IMPORTER's business (PurchasingAgentsPlanList.cs:95-96
            // `BuildingHelper.GetBuildingRegistration(plan.importAddress)` then `.BusinessName`): a row
            // whose importer this machine cannot resolve would throw inside the native builder.
            if (ip.importAddress == null) return null;
            return ip;
        }

        private static object BuildHr(PwHrPlan d)
        {
            var pl = new HrManagerPlan
            {
                assignedEmployeeId = d.AssignedEmployeeId,
                headquartersAddress = AddrOf(d.HeadquartersAddressKey),
                replaceAbsentEmployees = d.ReplaceAbsentEmployees,
                trainingTarget = d.TrainingTarget,
            };
            SetId(pl, d.Id);
            if (pl.assignedEmployees != null)
            {
                pl.assignedEmployees.Clear();
                foreach (var s in d.AssignedEmployees ?? new List<string>()) if (!string.IsNullOrEmpty(s)) pl.assignedEmployees.Add(s);
            }
            return pl;
        }

        private static object BuildHeadhunter(PwHeadhunterPlan d)
        {
            var pl = new HeadhunterPlan
            {
                assignedEmployeeId = d.AssignedEmployeeId,
                headquartersAddress = AddrOf(d.HeadquartersAddressKey),
                isRecruiting = d.IsRecruiting,
                skillRecruiting = string.IsNullOrEmpty(d.SkillRecruiting) ? "ba:skill_customerservice" : d.SkillRecruiting,
                skillValueTarget = d.SkillValueTarget,
                automaticallyReplaceOnRetire = d.AutomaticallyReplaceOnRetire,
                automaticallyReplaceOnResign = d.AutomaticallyReplaceOnResign,
                remainingCandidatesToRecruit = d.RemainingCandidatesToRecruit,
                amountOfCandidatesToRecruitPreference = d.AmountOfCandidatesToRecruitPreference,
            };
            SetId(pl, d.Id);
            // `public string[] assignedHrPlans = new string[2]` (HeadhunterPlan.cs:25) - a fixed pair.
            var src = d.AssignedHrPlans ?? new List<string>();
            if (pl.assignedHrPlans != null)
                for (int i = 0; i < pl.assignedHrPlans.Length; i++) pl.assignedHrPlans[i] = i < src.Count ? src[i] : null;
            if (pl.dealBreakerTypes != null)
            {
                pl.dealBreakerTypes.Clear();
                foreach (var s in d.DealBreakerTypes ?? new List<string>()) if (!string.IsNullOrEmpty(s)) pl.dealBreakerTypes.Add(s);
            }
            return pl;
        }

        /// <summary>`public readonly string id` on three of the four plan types (PricingManagerPlan.cs:17,
        /// HrManagerPlan.cs:12, HeadhunterPlan.cs:19) - reflection sets it, and a refusal only leaves the
        /// fresh uuid the constructor minted, which is exactly what MergerAbsence's installer does.</summary>
        private static void SetId(object plan, string id)
        {
            try
            {
                if (plan == null || string.IsNullOrEmpty(id)) return;
                for (var t = plan.GetType(); t != null; t = t.BaseType)
                {
                    var f = t.GetField("id", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null) { f.SetValue(plan, id); return; }
                }
            }
            catch { }
        }

        private static Address AddrOf(string key)
        {
            try { var r = GameStatePatcher.FindRegistration(key); return r != null ? r.Address : null; }
            catch { return null; }
        }

        // -- H3 / H4: what a member may do on a partner's headquarters --------

        /// <summary>Is this plan object one of the DETACHED partner rows?  Reference identity, so a plan of
        /// this player's own is never mistaken for one.</summary>
        public static bool IsOverlayPlan(object plan) => plan != null && _rowOwner.ContainsKey(plan);

        /// <summary>H3: the four non-logistics families are READ-ONLY on a partner's headquarters in this
        /// part.  The refusal is silent to the player (the native commit does not run) and reuses the
        /// existing merger wording - no new on-screen text.</summary>
        public static bool RefuseEdit(string family, string what)
        {
            try
            {
                if (!PartnerHqOpen(out var hq, out _)) return false;
                Plugin.Logger.LogWarning($"[Merger] {family} edit on partner HQ '{hq}' refused - part 2 ({what}).");
                return true;
            }
            catch { return false; }
        }

        /// <summary>H3, the per-ROW form: a native commit reached one of the DETACHED partner rows. The
        /// object is not in any game list, so the native method would mutate a throwaway; it is refused
        /// instead so the refusal is visible in the log and the screen keeps showing the owner's truth.</summary>
        public static bool RefuseRow(string family, string what, object plan)
        {
            try
            {
                if (!IsOverlayPlan(plan)) return false;
                string owner = _rowOwner.TryGetValue(plan, out var o) ? o : "?";
                string hq = ""; try { PartnerHqOpen(out hq, out _); } catch { }
                Plugin.Logger.LogWarning($"[Merger] {family} edit on partner HQ '{hq}' refused - part 2 ({what}; the plan belongs to '{owner}').");
                return true;
            }
            catch { return false; }
        }

        /// <summary>r2 MAJOR-2 (a): a partner's row is NOT SELECTABLE.  The native row click runs SelectPlan
        /// -> LoadPlan, and the pane it opens commits onto REAL objects of this machine (HrManagerPlanUI.cs:261
        /// writes assignedHrManagerPlanId onto one of THIS player's own employees, which then persists in this
        /// player's .hsg under a plan id that exists in no list here).  The pane never opens for a partner
        /// plan.  No new on-screen text: the click simply does nothing and the reason goes to the log.</summary>
        public static bool RefuseSelect(string family, object plan)
        {
            try
            {
                if (!IsOverlayPlan(plan)) return false;
                string owner = _rowOwner.TryGetValue(plan, out var o) ? o : "?";
                Plugin.Logger.LogWarning($"[Merger] {family} plan of '{owner}' is view-only on this machine (part 2)");
                return true;
            }
            catch { return false; }
        }

        /// <summary>r2 MAJOR-2 (b), BELT AND BRACES: a pane commit, keyed on the pane's CURRENT plan being a
        /// partner row OR the open headquarters being a partner's.  Refusing here as well means a pane that
        /// somehow opened (or stayed open across a tab change) still cannot write.</summary>
        public static bool RefusePane(string family, string what, object plan)
            => RefuseRow(family, what, plan) || RefuseEdit(family, what);

        /// <summary>r2 MAJOR-1 (b): the drop handler.  `UpdatePlanOrder`/`OnPlanReordered` index
        /// GetPlansForHeadquarters (PricingManagersPlanList.cs:81, HrManagersPlanList.cs:73,
        /// HeadhuntersPlanList.cs:58, PurchasingAgentsPlanList.cs:56) - the NATIVE rows only, which on a
        /// partner's headquarters is an EMPTY list.  The throw happens inside ReorderableList.EndDrag at :173,
        /// BEFORE `_draggingItem = null` (:176) and the layout restore (:177-188), which leaves the tab stuck
        /// mid-drag.  Refusing in a prefix lets EndDrag finish its own cleanup and makes the drop a no-op.
        /// The range check stands on its own: an index outside the native list is never safe.</summary>
        public static bool RefuseReorder(string family, int fromIndex, int toIndex)
        {
            try
            {
                if (MergerFlip.FlippedCount == 0) return false;         // inert without a merger
                if (PartnerHqOpen(out var hq, out var owner))
                {
                    Plugin.Logger.LogWarning($"[Merger] {family} edit on partner HQ '{hq}' refused - part 2 (row order {fromIndex}->{toIndex}; the plans belong to '{owner}').");
                    return true;
                }
                int n = NativeRowCount(family);
                if (n < 0 || fromIndex < 0 || toIndex < 0 || fromIndex >= n || toIndex >= n)
                {
                    Plugin.Logger.LogWarning($"[Merger] {family} row order {fromIndex}->{toIndex} refused - outside the {n} native row(s) on this machine.");
                    return true;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] reorder gate: {ex.Message}"); }
            return false;
        }

        /// <summary>How many rows the NATIVE list holds for the open headquarters - exactly what the drop
        /// handler indexes, read through the same helper call it makes.  -1 when it cannot be read.</summary>
        private static int NativeRowCount(string family)
        {
            try
            {
                var a = OpenHqAddress();
                if (a == null) return -1;
                switch (family)
                {
                    case "pricing":     return PricingManagerHelper.GetPlansForHeadquarters(a).Count;
                    case "purchasing":  return PurchasingAgentHelper.GetAssignedPlansForHeadquarters(a).Count;
                    case "hr":          return HrManagerHelper.GetAssignedPlansForHeadquarters(a).Count;
                    case "headhunter":  return HeadhunterHelper.GetAssignedPlansForHeadquarters(a).Count;
                }
            }
            catch { }
            return -1;
        }

        /// <summary>The headquarters address of the BizMan page on screen - what every native reorder handler
        /// passes to its helper.  Null when no business page is open.</summary>
        public static Address OpenHqAddress()
        {
            try
            {
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var biz = ui != null && ui.fullMenu != null && ui.fullMenu.bizMan != null ? ui.fullMenu.bizMan.business : null;
                return biz != null ? biz.address : null;
            }
            catch { return null; }
        }

        /// <summary>r2 MAJOR-3: an import partnership is keyed to the chosen AGENT's headquarters
        /// (ImportManagerDialog.cs:104 `headquartersAddress = inputComponent.selectedEmployeeInstance.assignedAddress`)
        /// and is then added to THIS machine's own gi list (:107).  The agent list is global and a merged
        /// partner's staff are exempt from the injected-staff filter, so an agent of a PARTNER's headquarters
        /// can be picked here: the partnership would execute on this machine and republish as this member's.
        /// Refused while merged when that headquarters is a FLIPPED building that is not truly mine.</summary>
        public static bool RefusePartnershipCreate(Address headquarters)
        {
            try
            {
                if (MergerFlip.FlippedCount == 0) return false;         // single player / no merger
                string key = ""; try { key = GameStateReader.AddressKey(headquarters); } catch { }
                if (string.IsNullOrEmpty(key) || !MergerFlip.IsFlipped(key)) return false;   // my own headquarters
                var reg = GameStatePatcher.FindRegistration(key);
                if (reg != null && MergerFlip.TrulyMine(reg)) return false;
                Plugin.Logger.LogWarning($"[Merger] purchasing partnership on partner HQ '{key}' refused - part 2.");
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] partnership gate: {ex.Message}"); return false; }
        }

        /// <summary>H4 (D20-8): the game's one-pricing-manager-per-neighbourhood rule becomes COMPANY-wide.
        /// PricingManagerHelper.IsNeighborhoodSupervised (:144) walks this player's own plans only, so two
        /// members could each supervise the same neighbourhood and fight over its prices.  A partner's plan
        /// counts here too, which takes that neighbourhood out of the dropdown the game itself builds
        /// (PricingManagerPlanUI.cs:81) - the game's own refusal path, with the game's own wording.</summary>
        public static bool SupervisedByPartner(string neighborhood, string exceptPlanId)
        {
            try
            {
                if (_byOwner.Count == 0 || string.IsNullOrEmpty(neighborhood) || !MergerSync.IAmMember) return false;
                foreach (var kv in _byOwner)
                {
                    if (_suspended.Contains(kv.Key)) continue;    // those plans are installed and already counted
                    foreach (var d in kv.Value?.PricingManagerPlans ?? new List<PwPricingPlan>())
                    {
                        if (d == null || d.Id == exceptPlanId) continue;
                        if (!string.Equals(d.SupervisedNeighborhood ?? "", neighborhood, StringComparison.Ordinal)) continue;
                        Plugin.Logger.LogInfo($"[Plans] neighbourhood '{neighborhood}' is already supervised by '{kv.Key}' (plan {d.Id}) - company-wide rule.");
                        return true;
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] neighbourhood check: {ex.Message}"); }
            return false;
        }

        // -- H5: the `plans` verb ---------------------------------------------

        public static string TestDriveLine(string arg)
        {
            try
            {
                string want = (arg ?? "").Trim();
                if (want.Length == 0)
                {
                    string own = $"own={{pricing:{OwnCount("pricing")},logistics:{OwnCount("logistics")},purchasing:{OwnCount("purchasing")},hr:{OwnCount("hr")},headhunter:{OwnCount("headhunter")}}}";
                    var parts = new List<string>();
                    foreach (var kv in _byOwner)
                    {
                        var q = kv.Value;
                        parts.Add($"{kv.Key}:{{pricing:{q?.PricingManagerPlans?.Count ?? 0},logistics:{q?.LogisticsManagerPlans?.Count ?? 0},"
                                + $"purchasing:{q?.ImportPartnerships?.Count ?? 0},hr:{q?.HrManagerPlans?.Count ?? 0},headhunter:{q?.HeadhunterPlans?.Count ?? 0}}}");
                    }
                    return $"OK plans {own} partners={{{string.Join(",", parts)}}}";
                }
                if (_byOwner.TryGetValue(want, out var p) && p != null)
                    return $"OK plans '{want}' pricing:{p.PricingManagerPlans?.Count ?? 0} logistics:{p.LogisticsManagerPlans?.Count ?? 0} "
                         + $"purchasing:{p.ImportPartnerships?.Count ?? 0} hr:{p.HrManagerPlans?.Count ?? 0} headhunter:{p.HeadhunterPlans?.Count ?? 0}"
                         + (_suspended.Contains(want) ? " (overlay suspended - this machine is standing in)" : "");

                // an ADDRESS: that headquarters' plans by family, with the owner they belong to
                string ownerOf = CompanyLists.TryOwnerOfAddress(want, out var opid) && !string.IsNullOrEmpty(opid) ? opid : "own";
                int pr = 0, lo = 0, pu = 0, hr = 0, hh = 0;
                if (ownerOf == "own")
                {
                    var a = AddrOf(want);
                    if (a == null) return $"ERR no headquarters '{want}' known here";
                    pr = PricingManagerHelper.GetPlansForHeadquarters(a).Count;
                    lo = LogisticsManagerHelper.GetAssignedPlansForHeadquarters(a).Count;
                    pu = PurchasingAgentHelper.GetAssignedPlansForHeadquarters(a).Count;
                    hr = HrManagerHelper.GetAssignedPlansForHeadquarters(a).Count;
                    hh = HeadhunterHelper.GetAssignedPlansForHeadquarters(a).Count;
                }
                else if (_byOwner.TryGetValue(ownerOf, out var p2) && p2 != null)
                {
                    foreach (var d in p2.PricingManagerPlans ?? new List<PwPricingPlan>()) if (Same(d?.HeadquartersAddressKey, want)) pr++;
                    foreach (var d in p2.LogisticsManagerPlans ?? new List<PwLogisticsPlan>()) if (Same(d?.HeadquartersAddressKey, want)) lo++;
                    foreach (var d in p2.ImportPartnerships ?? new List<PwImportPartnership>()) if (Same(d?.HeadquartersAddressKey, want)) pu++;
                    foreach (var d in p2.HrManagerPlans ?? new List<PwHrPlan>()) if (Same(d?.HeadquartersAddressKey, want)) hr++;
                    foreach (var d in p2.HeadhunterPlans ?? new List<PwHeadhunterPlan>()) if (Same(d?.HeadquartersAddressKey, want)) hh++;
                }
                else return $"ERR no plans held for '{want}'";
                return $"OK plans '{want}' owner={ownerOf} pricing:{pr} logistics:{lo} purchasing:{pu} hr:{hr} headhunter:{hh}";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        private static bool Same(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

        /// <summary>r2 MINOR-7: THIS machine's own plans of one family.  Wave 4's TAGGED DISPLAY COPIES sit in
        /// the same gi lists (logistics today) and are a partner's rows, not mine - the rig read own=4 while
        /// merged and own=2 after.  They are excluded through the same tag test every wave-4 predicate uses,
        /// MergerAbsence.IsDisplayInstall (an install tagged with a REAL pid is this machine standing in for an
        /// absent owner: those plans DO run here and are counted).</summary>
        private static int OwnCount(string family)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return 0;
                switch (family)
                {
                    case "pricing":     return Mine(gi.pricingManagerPlans);
                    case "logistics":   return Mine(gi.logisticsManagerPlans);
                    case "purchasing":  return Mine(gi.importPartnerships);
                    case "hr":          return Mine(gi.hrManagerPlans);
                    case "headhunter":  return Mine(gi.headhunterPlans);
                }
            }
            catch { }
            return 0;
        }

        private static int Mine<T>(List<T> list) where T : class
        {
            if (list == null) return 0;
            int n = 0;
            foreach (var x in list)
            {
                if (x == null) continue;
                bool display; try { display = MergerAbsence.IsDisplayInstall(x); } catch { display = false; }
                if (!display) n++;
            }
            return n;
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
    }
}
