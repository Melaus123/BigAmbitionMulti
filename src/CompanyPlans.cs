using System.Reflection;
using Buildings.Office.Headquarters;
using Entities;          // ImportPartnership lives here, not with the four HQ plan types
using Helpers;
using Streets;           // Address.IsUndefined() extension (AddressHelper.cs:27), as PlayerColours.cs does
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
            _rowInfo.Clear(); _seq.Clear(); _applied.Clear();
            _seqBase = NewSeqBase();                      // r2 MAJOR-2: a reload must never restart a sender's seq LOWER
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
            foreach (var k in kill) { if (_rows.TryGetValue(k, out var o) && o != null) { _rowOwner.Remove(o); _rowInfo.Remove(o); } _rows.Remove(k); }
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
                if (!CompanyHqPageOpen(out _, out _)) return;     // U1: every company HQ page shows this owner's rows
                if (EditWindowOpen())
                {
                    // HO-1a H5: a rebuild under an open list or dropdown closes it under the player's hand.
                    if (_pendingRedraw != ownerPid)
                        Plugin.Logger.LogInfo($"[Plans] redraw for '{ownerPid}' DEFERRED - an edit window is open.");
                    _pendingRedraw = ownerPid;                       // last writer wins: a redraw is a full rebuild
                    return;
                }
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

        /// <summary>HO-1a H5.  The owner whose redraw is waiting for the player to finish an edit; "" = none.</summary>
        private static string _pendingRedraw = "";

        /// <summary>HO-1c L4.9: SharedShopWorkTabs.Tick runs EVERY FRAME (MPCanvasUI.cs:813, no cadence gate) and
        /// the state re-read below costs two GetComponentsInChildren sweeps plus reflection, so the check itself is
        /// gated to 1 Hz here.</summary>
        private static float _nextDeferredCheck;

        /// <summary>H5, EVENTS OVER TIMERS.  Called from SharedShopWorkTabs.Tick (per frame; gated to 1 Hz here):
        /// the deferred redraw waits on the AUTHORITATIVE UI state re-read here, never on a delay.  The pending
        /// owner is cleared only when the rebuild actually ran, so no redraw is lost to a window that reopened.</summary>
        public static void TickDeferredRedraw()
        {
            try
            {
                if (_pendingRedraw.Length == 0) return;
                if (UnityEngine.Time.unscaledTime < _nextDeferredCheck) return;
                _nextDeferredCheck = UnityEngine.Time.unscaledTime + 1f;
                if (!CompanyHqPageOpen(out _, out _)) { _pendingRedraw = ""; return; }
                if (EditWindowOpen()) return;
                string owner = _pendingRedraw;
                _pendingRedraw = "";
                Plugin.Logger.LogInfo($"[Plans] deferred redraw for '{owner}' RUNS - the edit window closed.");
                RefreshOpenTabsFor(owner);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] deferred redraw: {ex.Message}"); }
        }

        /// <summary>H5.  Is the player mid-edit on the headquarters page?  Three NATIVE states, no flag of our
        /// own: HR's assign list (decompile HrManagerPlanUI.cs:74 `IsAssignEmployeesListOpen =>
        /// assignEmployeesList.gameObject.activeInHierarchy`); any open dropdown (UI.Elements/Dropdown.cs:116
        /// `public static Dropdown currentDropdown`, set :229 and nulled :276) whose panel is still showing
        /// (:200 `optionsPanelParentRect.gameObject.activeSelf`); and purchasing's expanded product row, which
        /// carries NO flag at all - its only readable state is the cell's own LayoutElement.minHeight, 200 while
        /// open (PurchasingAgentProductCellView.cs:99) against 100 while closed (:140).</summary>
        private static bool EditWindowOpen()
        {
            try
            {
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var bm = ui != null && ui.fullMenu != null ? ui.fullMenu.bizMan : null;
                if (bm == null) return false;

                var hr = bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI>(true);
                if (hr != null && hr.gameObject.activeInHierarchy && hr.IsAssignEmployeesListOpen) return true;

                var dd = UI.Elements.Dropdown.currentDropdown;
                if (dd != null && dd.gameObject.activeInHierarchy)
                {
                    var f = typeof(UI.Elements.Dropdown).GetField("optionsPanelParentRect",
                                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var rect = f != null ? f.GetValue(dd) as RectTransform : null;
                    if (rect != null && rect.gameObject.activeSelf) return true;
                }

                foreach (var cell in bm.GetComponentsInChildren<UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentProductCellView>(false))
                {
                    if (cell == null) continue;
                    var lf = cell.GetType().GetField("_layoutElement", BindingFlags.Instance | BindingFlags.NonPublic);
                    object le = lf != null ? lf.GetValue(cell) : null;
                    if (le == null) continue;
                    var mh = le.GetType().GetProperty("minHeight");
                    if (mh != null && mh.GetValue(le) is float h && h > 100f) return true;
                }
                return false;
            }
            catch { return false; }
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

        /// <summary>U1 (user ruling 2026-09-12, plan D37).  The BizMan page on screen is a COMPANY
        /// headquarters: MINE (pageOwner = my pid, MergerFlip.TrulyMine) or a merged PARTNER's, decided
        /// exactly as PartnerHqOpen decides it (flipped, not simulated here, a known owner, not suspended).
        /// This is what drives the UNION row view, so EVERY company headquarters page shows EVERY company
        /// headquarters' plans and nobody has to hop between cards.  PartnerHqOpen stays for the callers
        /// that really mean "a PARTNER's page": RefuseEdit, RefuseRow, RefuseReorder and RoutePlanCreate;
        /// SwallowPaneOpen and the assign-list fail-closed branch (MPPatches.cs, Patch_HrAssignList_MergerFilter)
        /// use THIS predicate plus HasOverlayRows since HQ-UNION-1b.</summary>
        public static bool CompanyHqPageOpen(out string pageHq, out string pageOwner)
        {
            pageHq = ""; pageOwner = "";
            try
            {
                if (!MergerSync.IAmMember) return false;
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var biz = ui != null && ui.fullMenu != null && ui.fullMenu.bizMan != null ? ui.fullMenu.bizMan.business : null;
                var reg = biz != null ? biz.buildingRegistration : null;
                if (reg == null) return false;
                if (reg.businessTypeName != "ba:businesstype_headquarters") return false;
                bool rented; try { rented = reg.RentedByPlayer; } catch { return false; }
                if (!rented) return false;
                pageHq = GameStateReader.AddressKey(reg);
                if (string.IsNullOrEmpty(pageHq)) { pageHq = ""; return false; }
                if (MergerFlip.TrulyMine(reg)) { pageOwner = MPConfig.PlayerId; return true; }   // my own card
                if (MergerAbsence.SimulatesHere(pageHq)) { pageHq = ""; return false; }
                if (!CompanyLists.TryOwnerOfAddress(pageHq, out pageOwner) || string.IsNullOrEmpty(pageOwner)) { pageHq = ""; pageOwner = ""; return false; }
                if (_suspended.Contains(pageOwner)) { pageHq = ""; pageOwner = ""; return false; }
                if (!_byOwner.ContainsKey(pageOwner)) { pageHq = ""; pageOwner = ""; return false; }
                return true;
            }
            catch { pageHq = ""; pageOwner = ""; return false; }
        }

        /// <summary>U1 DRIVER (the union).  Build the rows of one family for EVERY company headquarters
        /// and hand each to the list's OWN row builder, then strip and tint what that builder just added.
        /// Two kinds of row go in, and the difference is the whole point:
        ///   * a CO-MEMBER's plan is a DISPLAY COPY (RowsFor, registered in _rowInfo/_rowOwner), so
        ///     IsOverlayPlan is true and every edit on it ROUTES to the machine that runs it;
        ///   * one of MY OWN other headquarters' plans is the REAL plan object out of the game's own lists,
        ///     NEVER registered here, so IsOverlayPlan stays false and its edits run natively, exactly as
        ///     they do on that headquarters' own page.
        /// Neither kind is draggable (StripDragHandle: the native reorder indexes the PAGE headquarters'
        /// list).  Only partner rows are tinted - my own rows are mine on every card.  `setUp` is the native
        /// private SetUpPlanEntry; `rowParent` is the template's parent, whose newest children are the rows
        /// the builder created.</summary>
        public static int ShowPartnerRows(string family, Transform rowParent, Action<object> setUp)
        {
            if (setUp == null) return 0;
            if (!CompanyHqPageOpen(out var pageHq, out _)) return 0;
            int shown = 0, own = 0, refused = 0;
            foreach (var owner in new List<string>(_byOwner.Keys))
            {
                if (string.IsNullOrEmpty(owner) || owner == MPConfig.PlayerId) continue;
                if (_suspended.Contains(owner)) continue;        // standing in: that owner's REAL plans are installed here
                // r2 MINOR-4: the rebuild that replaces them is the moment the old row objects may go.
                if (_stale.Remove(owner + "|" + family)) DropRowsOf(owner, family);
                bool tinted = PlayerColours.TryColourFor(owner, out var tint);
                foreach (var hq in OwnerHqKeys(owner))
                    foreach (var row in RowsFor(owner, hq, family))
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
            }
            foreach (var plan in OwnOtherHqPlans(family, pageHq))
            {
                int before = rowParent != null ? rowParent.childCount : 0;
                try { setUp(plan); own++; }
                catch (Exception ex)
                {
                    refused++;
                    if (_refusalLogged.Add(family + "|own"))
                        Plugin.Logger.LogWarning($"[Plans] one of my own other-HQ {family} rows could not be drawn: {ex.Message} (once per family).");
                    continue;
                }
                if (rowParent == null) continue;
                for (int i = before; i < rowParent.childCount; i++) StripDragHandle(rowParent.GetChild(i));
            }
            if (shown > 0 || own > 0)
                Plugin.Logger.LogInfo($"[Plans] union: {shown} partner row(s) + {own} own other-HQ row(s) ({family}, page {pageHq})");
            if (refused > 0) Plugin.Logger.LogInfo($"[Plans] {refused} {family} row(s) not drawn on '{pageHq}'.");
            return shown + own;
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

        /// <summary>r2c: the headquarters keys an owner's published plans name (distinct, in list order across the
        /// four families) - read from the fan-out DTOs, never from the rows (a row exists only once a screen has
        /// drawn it; on the rig no screen is drawn, which is what emptied run 2's `plans <pid> hqs`).</summary>
        private static List<string> OwnerHqKeys(string owner)
        {
            var hqs = new List<string>();
            if (!_byOwner.TryGetValue(owner, out var p) || p == null) return hqs;
            void Take(string k) { if (!string.IsNullOrEmpty(k) && !hqs.Contains(k)) hqs.Add(k); }
            foreach (var d in p.PricingManagerPlans ?? new List<PwPricingPlan>()) Take(d?.HeadquartersAddressKey);
            foreach (var d in p.ImportPartnerships  ?? new List<PwImportPartnership>()) Take(d?.HeadquartersAddressKey);
            foreach (var d in p.HrManagerPlans      ?? new List<PwHrPlan>()) Take(d?.HeadquartersAddressKey);
            foreach (var d in p.HeadhunterPlans     ?? new List<PwHeadhunterPlan>()) Take(d?.HeadquartersAddressKey);
            return hqs;
        }

        /// <summary>r2c: build every row an owner's screens WOULD have built - the rig's levers (`plans <pid> rows`,
        /// `planedit`) act on the same detached objects a drawn list holds, so they create them the same way
        /// (RowsFor, cached) instead of waiting for a screen that never opens there.</summary>
        private static void MaterialiseRows(string owner)
        {
            foreach (var hq in OwnerHqKeys(owner))
                foreach (var fam in new[] { "pricing", "purchasing", "hr", "headhunter" })
                    try { RowsFor(owner, hq, fam); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] materialise {fam} rows of '{owner}' at '{hq}': {ex.Message}"); }
        }

        /// <summary>The partner's rows of one family for one headquarters, built once and cached.</summary>
        private static List<object> RowsFor(string owner, string hqKey, string family)
        {
            var rows = new List<object>();
            if (!_byOwner.TryGetValue(owner, out var p) || p == null) return rows;
            // HQ-UNION-1b (review MAJOR-2): the union draws every partner headquarters, not only the open one.
            // A headquarters this machine cannot resolve would give its copies a null address, the dropdown
            // rescope would not fire and a manager pick would name one of MY people (the page roster). Such
            // rows are not drawn at all (logged once per owner and headquarters).
            if (GameStatePatcher.FindRegistration(hqKey) == null)
            {
                if (_refusalLogged.Add("hq|" + owner + "|" + hqKey))
                    Plugin.Logger.LogInfo($"[Plans] '{owner}' headquarters '{hqKey}' is not registered here - its rows are not drawn (a manager dropdown could not be scoped to it).");
                return rows;
            }
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
                _rowInfo[row] = new RowInfo { Owner = owner, Family = family, PlanId = id, Hq = hqKey };   // part 2a
            }
            into.Add(row);
        }

        /// <summary>U1: MY OWN other headquarters' REAL plans of one family - every headquarters I run
        /// (MergerFlip.TrulyMine of its registration) whose key is not the page's.  These are the game's own
        /// objects out of SaveGameManager.Current (pricingManagerPlans / importPartnerships / hrManagerPlans /
        /// headhunterPlans), NOT display copies: they are never put in _rows/_rowOwner/_rowInfo, so
        /// IsOverlayPlan stays false and every edit on them runs natively.  Wave 4's TAGGED DISPLAY installs
        /// sit in the same lists and are a partner's rows, so they are excluded through the same test every
        /// wave-4 predicate uses (MergerAbsence.IsDisplayInstall), as OwnCount does.</summary>
        private static List<object> OwnOtherHqPlans(string family, string pageHq)
        {
            var mine = new List<object>();
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return mine;
                System.Collections.IEnumerable src = null;
                switch (family)
                {
                    case "pricing":     src = gi.pricingManagerPlans; break;
                    case "purchasing":  src = gi.importPartnerships;  break;
                    case "hr":          src = gi.hrManagerPlans;      break;
                    case "headhunter":  src = gi.headhunterPlans;     break;
                }
                if (src == null) return mine;
                var run = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var o in src)
                {
                    if (o == null) continue;
                    bool display; try { display = MergerAbsence.IsDisplayInstall(o); } catch { display = false; }
                    if (display) continue;
                    var hq = HqOfPlan(o);
                    if (hq == null) continue;
                    string key = KeyOf(hq);
                    if (key.Length == 0 || Same(key, pageHq)) continue;      // the page's own plans are the native list's
                    if (!run.TryGetValue(key, out var ok))
                    {
                        ok = false;
                        try { var reg = BuildingHelper.GetBuildingRegistration(hq); ok = reg != null && MergerFlip.TrulyMine(reg); } catch { }
                        run[key] = ok;
                    }
                    if (!ok) continue;                                       // a partner's headquarters, not one of mine
                    mine.Add(o);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] own other-HQ {family} rows: {ex.Message}"); }
            return mine;
        }

        /// <summary>The headquarters a REAL plan of any of the four families names (public field on all four:
        /// HrManagerPlan.cs:16, PricingManagerPlan.cs:21, HeadhunterPlan.cs:23, ImportPartnership.cs:39).</summary>
        private static Address HqOfPlan(object plan)
        {
            var pm = plan as PricingManagerPlan; if (pm != null) return pm.headquartersAddress;
            var ip = plan as ImportPartnership;  if (ip != null) return ip.headquartersAddress;
            var hr = plan as HrManagerPlan;      if (hr != null) return hr.headquartersAddress;
            var hh = plan as HeadhunterPlan;     if (hh != null) return hh.headquartersAddress;
            return null;
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

        /// <summary>HQ-UNION-1b: are any partner rows registered right now (drawn on whichever company page)?</summary>
        public static bool HasOverlayRows => _rowOwner.Count > 0;

        /// <summary>HO-1a H3.  WHOSE headquarters a display row belongs to, or "" when the plan is not one
        /// of the detached rows.  The assign list uses it to offer that company's own people only - the hole
        /// that let a member drop the HOST's staff onto a CLIENT's plan (EmployeesScrollerController.cs:21-23
        /// adds every local record without a plan, injected partner copies included).</summary>
        public static string OwnerOfOverlayPlan(object plan)
        {
            try { return plan != null && _rowInfo.TryGetValue(plan, out var ri) && ri != null ? (ri.Owner ?? "") : ""; }
            catch { return ""; }
        }

        /// <summary>H3 (part 1) / the REFUSAL TAIL (part 2a): a commit on a partner's headquarters that could
        /// not be turned into a route - no plan id, no headquarters key, nobody running that headquarters.
        /// The refusal stays silent to the player (the native commit simply does not run) and reuses the
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

        /// <summary>PART 2a (E2): a partner's row is SELECTABLE again.  Part 1 refused the click because the
        /// pane it opens commits onto REAL objects of this machine (HrManagerPlanUI.cs:261 writes
        /// assignedHrManagerPlanId onto one of THIS player's own employees).  Every one of those commits is
        /// now intercepted and ROUTED (RoutePaneEdit below), so the pane may open on the detached row - which
        /// is exactly the TEMP plan object E2 asks for: built from the registry DTO, never in a gi list, and
        /// IsOverlayPlan true.  Kept as a named seam so the log still says once per family that a partner's
        /// pane was opened, and so a future family that has no route can be refused here again.</summary>
        public static bool RefuseSelect(string family, object plan)
        {
            try
            {
                if (!IsOverlayPlan(plan)) return false;
                string owner = _rowOwner.TryGetValue(plan, out var o) ? o : "?";
                if (_refusalLogged.Add("select|" + family + "|" + owner))
                    Plugin.Logger.LogInfo($"[Merger] {family} plan of '{owner}' opened here on the detached row - its commits route to the machine that runs that headquarters (once per family and owner).");
                return false;        // part 2a: never refuse the select
            }
            catch { return false; }
        }

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
        /// PART 2a (E1): the creation is no longer refused - it ROUTES.  The partnership is created on the
        /// RUNNER of that agent's headquarters, with the importer address and the agent the member chose, and
        /// comes back on the next fan-out as one of that owner's rows.  Nothing is added here.
        /// True = the dialog's own null-return path (no new text) because the leg has left this machine.</summary>
        public static bool RoutePartnershipCreate(Address headquarters, string agentEmployeeId, Address importer)
        {
            try
            {
                if (MergerFlip.FlippedCount == 0) return false;         // single player / no merger
                string key = ""; try { key = GameStateReader.AddressKey(headquarters); } catch { }
                if (string.IsNullOrEmpty(key) || !MergerFlip.IsFlipped(key)) return false;   // my own headquarters
                var reg = GameStatePatcher.FindRegistration(key);
                if (reg != null && MergerFlip.TrulyMine(reg)) return false;
                string importKey = ""; try { importKey = GameStateReader.AddressKey(importer); } catch { }
                if (string.IsNullOrEmpty(importKey))
                { Plugin.Logger.LogWarning($"[Merger] purchasing create on partner HQ '{key}' refused - the importer's address could not be read."); return true; }
                string owner = CompanyLists.TryOwnerOfAddress(key, out var o) ? o : "";
                return Send("purchasing", Guid.NewGuid().ToString("N"), key, owner, "create", "new partnership",
                            null, agentEmployeeId ?? "", 0, 0f, false, importKey);
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
                // r2 F1: `plans <ownerPid> rows [family]` - the PLAN IDS, so the rig can drive `planedit`.
                {
                    var w = want.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (w.Length >= 2 && string.Equals(w[1], "rows", StringComparison.OrdinalIgnoreCase))
                        return RowsLine(w[0], w.Length > 2 ? w[2].ToLowerInvariant() : "");
                    // r2b: `plans <ownerPid> hqs` - that owner's headquarters address keys (the rows' HQ, distinct,
                    // in row order), so the rig can name the HQ a `planedit ... add` creates on.
                    if (w.Length >= 2 && string.Equals(w[1], "hqs", StringComparison.OrdinalIgnoreCase))
                        return HqsLine(w[0]);
                }
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

        /// <summary>r2 F1, THE PLAN-ROWS LEVER.  One owner's rows as `<family>:<planId>:<name>`, walked off
        /// THE OWNER'S OWN DTO LISTS in `_byOwner` (r3 G3): the fan-out order IS that owner's list order,
        /// newest appended last, while enumerating `_rows` stopped being insertion-ordered the moment a
        /// removal (DropRows, :141) freed a bucket for re-use - so "newest last" was not reliable there.
        /// The NAME still comes from the materialised row where one exists; a DTO with no row of its own is
        /// listed all the same, named off the DTO.  Format unchanged: these are exactly the rows `planedit`
        /// can address.</summary>
        private static string RowsLine(string ownerPid, string family)
        {
            if (!_byOwner.TryGetValue(ownerPid, out var p) || p == null) return $"ERR no plans held for '{ownerPid}'";
            MaterialiseRows(ownerPid);   // r2c: the rows exist only once drawn; the rig draws nothing
            var parts = new List<string>();
            void Take(string fam, string id, string dtoName)
            {
                if (string.IsNullOrEmpty(id)) return;
                if (family.Length > 0 && !string.Equals(fam, family, StringComparison.Ordinal)) return;
                string name = null;
                if (_rows.TryGetValue(ownerPid + "|" + fam + "|" + id, out var row) && row != null) name = RowName(row, fam);
                if (string.IsNullOrEmpty(name)) name = dtoName;
                parts.Add($"{fam}:{id}:{RowFieldSafe(name)}");
            }
            foreach (var d in p.PricingManagerPlans ?? new List<PwPricingPlan>()) Take("pricing", d?.Id, EmpName(d?.AssignedEmployeeId ?? ""));
            foreach (var d in p.ImportPartnerships ?? new List<PwImportPartnership>())
            {
                string who = EmpName(d?.EmployeeInstanceId ?? "");
                Take("purchasing", d?.Id, who.Length > 0 ? who : (d?.ImportAddressKey ?? ""));
            }
            foreach (var d in p.HrManagerPlans ?? new List<PwHrPlan>()) Take("hr", d?.Id, EmpName(d?.AssignedEmployeeId ?? ""));
            foreach (var d in p.HeadhunterPlans ?? new List<PwHeadhunterPlan>()) Take("headhunter", d?.Id, EmpName(d?.AssignedEmployeeId ?? ""));
            return $"OK plans '{ownerPid}' rows=[{string.Join(",", parts)}]";
        }

        private static string HqsLine(string ownerPid)
        {
            if (!_byOwner.ContainsKey(ownerPid)) return $"ERR no plans held for '{ownerPid}'";
            return $"OK plans '{ownerPid}' hqs=[{string.Join(",", OwnerHqKeys(ownerPid))}]";   // r2c: from the DTOs
        }

        /// <summary>The three characters the row format itself uses are replaced, so a name can never split a
        /// row; an empty name is `-`.</summary>
        private static string RowFieldSafe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "-";
            var b = new System.Text.StringBuilder(s.Length);
            foreach (var c in s) b.Append(c == ',' || c == ':' || c == ']' ? '_' : c);
            return b.Length == 0 ? "-" : b.ToString();
        }

        private static string RowName(object row, string family)
        {
            try
            {
                switch (family)
                {
                    case "pricing":    return EmpName((row as PricingManagerPlan)?.assignedEmployeeId);
                    case "hr":         return EmpName((row as HrManagerPlan)?.assignedEmployeeId);
                    case "headhunter": return EmpName((row as HeadhunterPlan)?.assignedEmployeeId);
                    case "purchasing":
                    {
                        var ip = row as ImportPartnership; if (ip == null) return "";
                        string n = EmpName(ip.employeeInstanceId);
                        return n.Length > 0 ? n : KeyOf(ip.importAddress);
                    }
                }
            }
            catch { }
            return "";
        }

        private static string EmpName(string eid)
        {
            try
            {
                if (string.IsNullOrEmpty(eid)) return "";
                var e = EmployeeHelper.GetEmployeeById(eid);
                return e?.characterData?.name?.ToString() ?? "";
            }
            catch { return ""; }
        }

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


        // =============== MERGER PHASE 4c PART 2a - THE PLAN-EDIT ROUTE (E1/E2/E4) ===============
        //
        // ONE mechanism for all four families.  A member's commit on a PARTNER's plan never touches this
        // machine's state: it becomes a `mergerplanedit` leg on the EXISTING SharedWorkEdit envelope
        // (Protocol.cs, rule 5 - no new MessageType) carrying {family, plan id, HQ address, op, the whole
        // plan DTO as this screen holds it, edit seq}; the host union-checks it and RouteTargetFor()s it to
        // the machine that RUNS that headquarters; that machine applies the op onto its OWN native plan by
        // id, with the game's own method, then MarkChange + PaperworkSync.MarkDirty so the next fan-out
        // refreshes every member's rows.  The member's temp row is updated only for DISPLAY - the next
        // commit re-reads the registry, never the optimistic copy.
        //
        // E4, the pressed-twice rules: the seq is per plan id and monotonic on the sender; the runner
        // no-ops a (plan id, seq, op) it has already applied; a plan that is gone on the runner is refused
        // ('gone'); a headquarters nobody runs is refused at the host ('offline'), never queued.

        private sealed class RowInfo { internal string Owner = "", Family = "", PlanId = "", Hq = ""; }

        /// <summary>Row object -> what a routed edit needs to address it.  Reference identity, like _rowOwner.</summary>
        private static readonly Dictionary<object, RowInfo> _rowInfo = new(ReferenceComparer.Instance);

        /// <summary>plan id -> the last edit seq this machine sent for it (E4).</summary>
        private static readonly Dictionary<string, int> _seq = new(StringComparer.Ordinal);

        /// <summary>r2 MAJOR-2.  The seq used to start at 1 on every machine and after every reload, so the
        /// SECOND member's first edit on the same (plan, op) - and a sender's first edit after a reload -
        /// looked like a resend and was dropped.  Two fixes: the duplicate stamp now carries the SENDER at
        /// both ends, and the base is seeded per SESSION from UTC seconds, so within one machine's life the
        /// numbers only ever climb.</summary>
        private static int _seqBase = NewSeqBase();

        private static int NewSeqBase()
        { try { return (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond) & 0x3FFFFFFF; } catch { return 1; } }

        /// <summary>RUNNER: (plan id|seq|op) already applied, so a resend is a no-op (E4).</summary>
        private static readonly HashSet<string> _applied = new(StringComparer.Ordinal);

        /// <summary>THE PANE COMMIT (E2).  False = this is one of my own plans and the native body must run.
        /// True = it was a partner's row: the native body is SKIPPED and the edit has left as a leg (or was
        /// refused with a logged reason, which is still a skip - a partner's row must never be written here).
        /// `mutate` is the DISPLAY-ONLY optimistic touch on the temp object; it runs before the DTO is taken
        /// so the leg carries the plan as the player just left it.</summary>
        public static bool RoutePaneEdit(string family, string what, object plan, string op,
                                         string strValue = "", int intValue = 0, float number = 0f,
                                         bool flag = false, Action<object> mutate = null, string station = "")
        {
            try
            {
                if (!IsOverlayPlan(plan)) return false;                       // my own plan: nothing changes
                if (!_rowInfo.TryGetValue(plan, out var ri) || ri == null)
                { Refused(family, op, "?", "the row is no longer in the registry"); return true; }
                if (mutate != null) { try { mutate(plan); } catch (Exception mx) { Plugin.Logger.LogWarning($"[Plans] {family} {op} display: {mx.Message}"); } }
                return Send(ri.Family, ri.PlanId, ri.Hq, ri.Owner, op, what, DtoOf(plan, ri.Family),
                            strValue, intValue, number, flag, station);   // HO-1a H6: StationId carries the warehouse key
            }
            catch (Exception ex)
            {
                // A half-built route must still swallow the native body: the alternative is the part-1
                // contamination (a real local employee tagged with a partner's plan id).
                Plugin.Logger.LogWarning($"[Merger] {family} {op} route failed and the local commit was refused: {ex.Message}");
                return true;
            }
        }

        // ── D2, THE CONFIRMATION FOLD ────────────────────────────────────────────────────────────────
        // `end` and `urgent` on a purchasing partnership are the only two pane commits the GAME itself puts
        // behind a HudConfirm (PurchasingAgentPlanUI.cs:205 and :227).  Routing them at the METHOD skipped the
        // native body, so a partner's plan lost that confirmation - the one behaviour difference in the block.
        // Now the prefix ARMS instead: the native body runs, its own guards run, it reaches HudConfirm.Show -
        // and the shared wrapper in SharedShopStaff REPLACES the confirm delegate with the routed send, so the
        // native callback (which would write the detached temp object / this machine's own list) never runs.
        // The arming lives exactly as long as the pane method call, confirmed or cancelled either way.

        private static object _confirmRow;
        private static string _confirmFamily = "", _confirmOp = "", _confirmWhat = "";

        /// <summary>True = armed, the native body must run.  False = not a partner's row (nothing changes).</summary>
        public static bool ArmConfirmRoute(string family, object plan, string op, string what)
        {
            try
            {
                if (!IsOverlayPlan(plan) || !_rowInfo.ContainsKey(plan)) return false;
                _confirmRow = plan; _confirmFamily = family ?? ""; _confirmOp = op ?? ""; _confirmWhat = what ?? "";
                return true;
            }
            catch { DisarmConfirmRoute(); return false; }
        }

        public static void DisarmConfirmRoute()
        { _confirmRow = null; _confirmFamily = ""; _confirmOp = ""; _confirmWhat = ""; }

        /// <summary>THE WRAPPER'S SIDE.  Null = nothing armed, leave the dialog's own callback alone.  A
        /// delegate = use this INSTEAD: the routed send, taken once so a nested dialog cannot take it twice.</summary>
        public static Action TakeConfirmRoute()
        {
            object row = _confirmRow;
            if (row == null) return null;
            string fam = _confirmFamily, op = _confirmOp, what = _confirmWhat;
            DisarmConfirmRoute();
            return () => { try { RoutePaneEdit(fam, what, row, op); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] {fam} {op} confirmed route: {ex.Message}"); } };
        }

        /// <summary>CREATION (E1).  'Add plan' on a PARTNER's headquarters: nothing is added here, the runner
        /// creates it and the next fan-out brings the row back.  False = my own headquarters (native).</summary>
        public static bool RoutePlanCreate(string family)
        {
            try
            {
                if (!PartnerHqOpen(out var hq, out var owner)) return false;
                return Send(family, Guid.NewGuid().ToString("N"), hq, owner, "add", "add plan", null, "", 0, 0f, false);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] {family} create route: {ex.Message}"); return true; }
        }

        /// <summary>The rig's form of the create - `planedit <family> 0 add <hqAddressKey>` - names the partner
        /// headquarters outright, because no pane is open on the rig (run 1 of T-P4C-PART2A: the pane form
        /// answered routed=False silently). Refused with a log when nothing names an HQ, when no company member
        /// owns that key here, or when the key is this machine's own headquarters (the game's own screen does
        /// that natively). True = the leg has left.</summary>
        public static bool RoutePlanCreateAt(string family, string hqKey)
        {
            try
            {
                if (string.IsNullOrEmpty(hqKey))
                { Plugin.Logger.LogWarning($"[Merger] plan-edit-refused ({family} add): no partner headquarters is open here and none was named - use `planedit {family} 0 add <hqAddressKey>`."); return false; }
                if (!CompanyLists.TryOwnerOfAddress(hqKey, out var owner) || string.IsNullOrEmpty(owner))
                { Plugin.Logger.LogWarning($"[Merger] plan-edit-refused ({family} add): no company member owns headquarters '{hqKey}' here."); return false; }
                if (string.Equals(owner, MPConfig.PlayerId, StringComparison.Ordinal))
                { Plugin.Logger.LogWarning($"[Merger] plan-edit-refused ({family} add): '{hqKey}' is this machine's own headquarters - nothing to route."); return false; }
                return Send(family, Guid.NewGuid().ToString("N"), hqKey, owner, "add", "add plan (planedit)", null, "", 0, 0f, false);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] {family} create route at '{hqKey}': {ex.Message}"); return false; }
        }

        /// <summary>D23 (E3): the ACCEPT / DECLINE of a RELAYED staff-insurance offer.  The HR plan lives in
        /// the owner's save, so the commit goes to the runner of that plan's headquarters exactly like every
        /// other plan edit - the local pane commit part 1 refused is never run.  True = the leg has left.</summary>
        public static bool RouteInsurance(string op, string hrPlanId, string hqKey, string offerId, float price)
        {
            try
            {
                if (string.IsNullOrEmpty(hrPlanId) || string.IsNullOrEmpty(hqKey))
                { Refused("hr", op, hrPlanId ?? "?", "the relayed offer named no HR plan or headquarters"); return true; }
                string owner = CompanyLists.TryOwnerOfAddress(hqKey, out var o) ? o : "";
                return Send("hr", hrPlanId, hqKey, owner, op, "staff insurance", null, offerId ?? "", 0, price, false);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] insurance route: {ex.Message}"); return true; }
        }

        private static bool Send(string family, string planId, string hq, string owner, string op, string what,
                                 object dto, string strValue, int intValue, float number, bool flag,
                                 string stationId = "")
        {
            if (string.IsNullOrEmpty(planId) || string.IsNullOrEmpty(hq))
            { Refused(family, op, planId ?? "?", "no plan id or no headquarters address"); return true; }
            int seq = (_seq.TryGetValue(planId, out var s) ? s : _seqBase) + 1;
            _seq[planId] = seq;
            var pay = new SharedWorkEditPayload
            {
                PlayerId = MPConfig.PlayerId, AddressKey = hq, Op = "mergerplanedit",
                Family = family, PlanId = planId, PlanOp = op, EditSeq = seq,
                StrValue = strValue ?? "", IntValue = intValue, Estimate = number, BoolValue = flag,
                StationId = stationId ?? "",
            };
            switch (family)
            {
                case "pricing":    pay.PricingPlan    = dto as PwPricingPlan;       break;
                case "purchasing": pay.Partnership    = dto as PwImportPartnership; break;
                case "hr":         pay.HrPlan         = dto as PwHrPlan;            break;
                case "headhunter": pay.HeadhunterPlan = dto as PwHeadhunterPlan;    break;
            }
            SharedShopWorkTabs.SendEdit(pay);
            Plugin.Logger.LogInfo($"[Merger] {family} {op} routed to '{(owner.Length > 0 ? owner : "?")}' for '{hq}' ({planId}; seq {seq}; {what})");
            return true;
        }

        /// <summary>E2's safety net on the SELECT.  LoadPlan on a DETACHED partner row reads fields this
        /// machine may not be able to resolve (an importer it does not know, an employee id that is not
        /// replicated); a throw inside the row's click delegate would leave the list mid-update and the tab
        /// unusable.  The finaliser swallows it, logs it once per family, and leaves the pane as it was - no
        /// new on-screen text.  HQ-UNION-1b (review MAJOR-1): since the union, a partner's row can sit on MY
        /// OWN card too, so the test is "a company headquarters page with partner rows on it", not "a
        /// partner's page"; only when NO partner row is drawn is a throw the game's own and re-thrown.</summary>
        public static Exception SwallowPaneOpen(string family, Exception ex)
        {
            try
            {
                if (ex == null) return null;
                if (!CompanyHqPageOpen(out var hq, out var owner) || _rowOwner.Count == 0) return ex;   // no partner row here: the game's own bug
                if (_refusalLogged.Add("pane|" + family))
                    Plugin.Logger.LogWarning($"[Plans] the {family} pane could not open on a row of '{owner}' at '{hq}': {ex.Message} (once per family; the pane stays as it was).");
                return null;
            }
            catch { return null; }
        }

        /// <summary>Every refusal says why (rule 5) and re-reads the registry, so the screen goes back to the
        /// owner's truth rather than sitting on an edit that never happened.</summary>
        private static void Refused(string family, string op, string planId, string why)
        {
            Plugin.Logger.LogWarning($"[Merger] plan edit refused ({family} {op} {planId}): {why}.");
            try { if (CompanyHqPageOpen(out _, out var owner)) RefreshOpenTabsFor(owner); } catch { }
        }

        /// <summary>The member's answer leg: the runner could not apply the edit.  The rows on screen are
        /// redrawn from the registry, which still holds the owner's truth.</summary>
        public static void ReceiveRefusal(string family, string planId, string reason)
        {
            Plugin.Logger.LogWarning($"[Merger] plan-edit-refused ({family} {planId}): {reason}.");
            try { if (CompanyHqPageOpen(out _, out var owner)) RefreshOpenTabsFor(owner); } catch { }
        }

        // -- the DTO of one temp row (the reverse of BuildPricing/BuildPurchasing/BuildHr/BuildHeadhunter) --

        private static object DtoOf(object plan, string family)
        {
            try
            {
                switch (family)
                {
                    case "pricing":
                    {
                        var pl = plan as PricingManagerPlan; if (pl == null) return null;
                        var d = new PwPricingPlan
                        {
                            Id = pl.id, AssignedEmployeeId = pl.assignedEmployeeId,
                            HeadquartersAddressKey = KeyOf(pl.headquartersAddress),
                            SupervisedNeighborhood = pl.supervisedNeighborhood,
                            NextUpdateDay = pl.nextUpdateDay, NextUpdateHour = pl.nextUpdateHour,
                        };
                        if (pl.manuallyPricedItems != null) foreach (var x in pl.manuallyPricedItems) d.ManuallyPricedItems.Add(x);
                        return d;
                    }
                    case "purchasing":
                    {
                        var ip = plan as ImportPartnership; if (ip == null) return null;
                        return new PwImportPartnership
                        {
                            Id = ip.id, HeadquartersAddressKey = KeyOf(ip.headquartersAddress),
                            ImportAddressKey = KeyOf(ip.importAddress), EmployeeInstanceId = ip.employeeInstanceId,
                            NextDeliveryDay = ip.nextDeliveryDay, IsRepeatingOrder = ip.isRepeatingOrder,
                            DaysUntilRepeat = ip.daysUntilRepeat, IsActive = ip.isActive,
                            IsUrgentOrder = ip.isUrgentOrder, IsTarget = ip.isTarget,
                        };
                    }
                    case "hr":
                    {
                        var pl = plan as HrManagerPlan; if (pl == null) return null;
                        var d = new PwHrPlan
                        {
                            Id = pl.id, AssignedEmployeeId = pl.assignedEmployeeId,
                            HeadquartersAddressKey = KeyOf(pl.headquartersAddress),
                            ReplaceAbsentEmployees = pl.replaceAbsentEmployees, TrainingTarget = pl.trainingTarget,
                        };
                        if (pl.assignedEmployees != null) foreach (var x in pl.assignedEmployees) d.AssignedEmployees.Add(x);
                        return d;
                    }
                    case "headhunter":
                    {
                        var pl = plan as HeadhunterPlan; if (pl == null) return null;
                        var d = new PwHeadhunterPlan
                        {
                            Id = pl.id, AssignedEmployeeId = pl.assignedEmployeeId,
                            HeadquartersAddressKey = KeyOf(pl.headquartersAddress),
                            IsRecruiting = pl.isRecruiting, SkillRecruiting = pl.skillRecruiting,
                            SkillValueTarget = pl.skillValueTarget,
                            AutomaticallyReplaceOnRetire = pl.automaticallyReplaceOnRetire,
                            AutomaticallyReplaceOnResign = pl.automaticallyReplaceOnResign,
                            RemainingCandidatesToRecruit = pl.remainingCandidatesToRecruit,
                            AmountOfCandidatesToRecruitPreference = pl.amountOfCandidatesToRecruitPreference,
                        };
                        if (pl.assignedHrPlans != null) foreach (var x in pl.assignedHrPlans) if (!string.IsNullOrEmpty(x)) d.AssignedHrPlans.Add(x);
                        if (pl.dealBreakerTypes != null) foreach (var x in pl.dealBreakerTypes) d.DealBreakerTypes.Add(x);
                        return d;
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] {family} dto: {ex.Message}"); }
            return null;
        }

        private static string KeyOf(Address a) { try { return GameStateReader.AddressKey(a); } catch { return ""; } }

        // -- THE RUNNER SIDE (E1): apply one routed op onto MY OWN native plan ---------------------------

        /// <summary>RUNNER, MAIN THREAD.  Called from SharedShopWorkTabs.OwnerApplyEdit, which has already
        /// established that this machine runs `p.AddressKey` (mine, or an absent partner's I stand in for).
        /// Every op runs the game's OWN method on the real object; nothing here trusts the DTO for anything
        /// but the fields the op is about.  Idempotent by (plan id, edit seq, op).</summary>
        public static void ApplyRouted(BuildingRegistration reg, SharedWorkEditPayload p)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null || p == null) return;
                string fam = p.Family ?? "", op = p.PlanOp ?? "", id = p.PlanId ?? "";
                if (fam.Length == 0 || op.Length == 0 || id.Length == 0)
                { Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED for '{p?.AddressKey}': the leg named no family, op or plan."); return; }
                string stamp = id + "|" + p.EditSeq + "|" + op + "|" + (p.PlayerId ?? "");   // r2 MAJOR-2: per SENDER, like MPServer._planEditSeen
                if (!_applied.Add(stamp))
                { Plugin.Logger.LogInfo($"[Merger] plan edit {fam} {op} ({id}, seq {p.EditSeq}) already applied here - no-op."); return; }
                var addr = reg != null ? reg.Address : null;

                _refusal = "";
                bool ok;
                switch (fam)
                {
                    case "pricing":    ok = ApplyPricing(gi, addr, p, op, id);    break;
                    case "purchasing": ok = ApplyPurchasing(gi, addr, p, op, id); break;
                    case "hr":         ok = ApplyHr(gi, addr, p, op, id);         break;
                    case "headhunter": ok = ApplyHeadhunter(gi, addr, p, op, id); break;
                    default:
                        Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED for '{p.AddressKey}': unknown family '{fam}'.");
                        SendRefusal(p, $"unknown family '{fam}'"); return;
                }
                // r2 MAJOR-6: the helper logged the reason HERE; the sender is told too, so its optimistic
                // row goes back to the registry's truth instead of waiting for the next fan-out.
                if (!ok) { SendRefusal(p, _refusal.Length > 0 ? _refusal : $"{op}: the runner refused the edit"); return; }
                SaveGameManager.MarkChange();
                // HO-1a H4: a routed plan edit is a button somebody is WATCHING, so this bundle does not wait
                // out the 30 s dirty interval - MarkUrgent publishes at 2 s, and a burst coalesces into one.
                PaperworkSync.MarkUrgent();
                Plugin.Logger.LogInfo($"[Merger] plan edit applied for '{p.AddressKey}' from '{p.PlayerId}' ({fam} {op}, plan {id}, seq {p.EditSeq}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED: {ex.Message}"); }
        }

        /// <summary>r2 MAJOR-6.  The WHY of the refusal the runner is about to answer with.  Every helper that
        /// returns false sets it through Refuse(); ApplyRouted reads it once and clears it.</summary>
        private static string _refusal = "";

        private static bool Refuse(string why) { _refusal = why ?? ""; return false; }

        private static bool Gone(string fam, string op, string id)
        {
            Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED ({fam} {op}): plan '{id}' is gone on this machine.");
            return Refuse($"{op}: plan '{id}' is gone on this machine");
        }

        /// <summary>r2 MAJOR-6, THE ANSWER.  ReceiveRefusal had no caller: a refusal only ever reached the
        /// runner's own log, so the member sat on an optimistic row until the next fan-out.  The runner now
        /// answers the SENDER on the same carrier - Op `mergerplanedit`, PlanOp `refused`, the reason in
        /// StrValue, StationId naming the one machine it is for, so the host FORWARDS it and never fans it
        /// out (MPServer.HostRouteSharedWorkEdit).  A refusal of this machine's own leg is handled here.</summary>
        private static void SendRefusal(SharedWorkEditPayload p, string why)
        {
            try
            {
                string to = p?.PlayerId ?? "";
                if (to.Length == 0) return;
                if (to == MPConfig.PlayerId) { ReceiveRefusal(p.Family ?? "", p.PlanId ?? "", why ?? ""); return; }
                SharedShopWorkTabs.SendEdit(new SharedWorkEditPayload
                {
                    PlayerId = MPConfig.PlayerId, AddressKey = p.AddressKey ?? "", Op = "mergerplanedit",
                    Family = p.Family ?? "", PlanId = p.PlanId ?? "", PlanOp = "refused", EditSeq = p.EditSeq,
                    StrValue = why ?? "", StationId = to,
                });
                Plugin.Logger.LogInfo($"[Merger] plan edit refusal answered to '{to}' ({p.Family} {p.PlanOp}, plan {p.PlanId}): {why}");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] refusal answer: {ex.Message}"); }
        }

        /// <summary>r2 MAJOR-3.  An employee id that resolves HERE to an INJECTED COPY of a partner's person
        /// is not this machine's to assign: writing it would persist a foreign id into this save's .hsg.</summary>
        private static bool InjectedHere(string eid)
        { try { return !string.IsNullOrEmpty(eid) && MPRegisterSync.IsInjectedStaff(eid); } catch { return false; } }

        private static bool RefuseInjected(string fam, string op, string id, string eid)
        {
            Plugin.Logger.LogWarning($"[Merger] {fam} {op} REFUSED for plan '{id}': employee '{eid}' is an injected copy here - a cross-member assignment needs the host-held transfer first.");
            return Refuse($"{op}: '{eid}' is an injected copy here - a cross-member assignment needs the host-held transfer first");
        }

        private static bool ApplyPricing(GameInstance gi, Address addr, SharedWorkEditPayload p, string op, string id)
        {
            PricingManagerPlan pl = null;
            foreach (var x in gi.pricingManagerPlans) if (x != null && x.id == id) { pl = x; break; }
            if (op == "add")
            {
                if (pl != null) return true;                                  // a resend of the same creation
                if (addr == null) return Gone("pricing", op, id);
                var made = new PricingManagerPlan { headquartersAddress = addr };
                SetId(made, id);
                gi.pricingManagerPlans.Add(made);
                return true;
            }
            if (pl == null) return Gone("pricing", op, id);
            switch (op)
            {
                case "neighborhood":   pl.SetSupervisedNeighborhood(p.StrValue ?? ""); return true;
                case "manualprice":    pl.ApplyManualPrice(p.StrValue ?? "", p.Estimate); return true;
                case "suggestedprice": pl.ApplySuggestedPrice(p.StrValue ?? "", p.Estimate); return true;
                case "suggestall":
                {
                    // HO-1a H1 / HO-1c L4.6: ApplySuggestedPrices (decompile :91-99) loops over its ApplyTargets -
                    // the VISIBLE rows when PricingManagerHelper.Settings.applyOnlyToVisibleProducts (:40-49), else
                    // every model - so replaying the runner's WHOLE cache priced more than the button did.  The
                    // sender names its targets in StrValue ('|'-joined, as the purchasing bulk does) and the runner
                    // prices only those it has a suggestion for; one it does not cache is skipped, never guessed.
                    // The copy is taken because the loop walks it while the live plan is being written (not because
                    // ApplySuggestedPrice edits the list - decompile PricingManagerPlan.cs:128-131 removes from
                    // manuallyPricedItems only).
                    var want = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var nm in (p.StrValue ?? "").Split('|')) if (nm.Length > 0) want.Add(nm);
                    // HO-1d (re-review MINOR-3): the button always names its targets (the visible rows, or all); a
                    // leg naming NONE prices nothing - otherwise an empty leg would price the whole cache (L4.6).
                    if (want.Count == 0) { Plugin.Logger.LogInfo($"[Merger] pricing suggestall on plan '{id}': no items named - nothing applied."); return true; }
                    var snap = pl.cachedSuggestions.GetRange(0, pl.cachedSuggestions.Count);
                    int applied = 0, skipped = 0;
                    foreach (var sg in snap)
                    {
                        if (sg == null) continue;
                        if (!want.Contains(sg.itemName)) { skipped++; continue; }
                        pl.ApplySuggestedPrice(sg.itemName, sg.suggestedMax); applied++;
                    }
                    Plugin.Logger.LogInfo($"[Merger] pricing suggestall on plan '{id}': {applied} applied, {skipped} skipped (not on the sender's screen), {want.Count} named.");
                    return true;
                }
                case "manager":
                    if (InjectedHere(p.StrValue)) return RefuseInjected("pricing", op, id, p.StrValue);   // r2 MAJOR-3
                    pl.assignedEmployeeId = string.IsNullOrEmpty(p.StrValue) ? null : p.StrValue; return true;
                case "delete":         PricingManagerHelper.DeletePlan(id); return true;
            }
            Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED (pricing): unknown op '{op}'."); return Refuse($"unknown op '{op}'");
        }

        private static bool ApplyPurchasing(GameInstance gi, Address addr, SharedWorkEditPayload p, string op, string id)
        {
            ImportPartnership ip = null;
            foreach (var x in gi.importPartnerships) if (x != null && x.id == id) { ip = x; break; }
            if (op == "create")
            {
                if (ip != null) return true;                                  // a resend
                if (addr == null) return Gone("purchasing", op, id);
                var imp = GameStatePatcher.FindRegistration(p.StationId ?? "");
                if (imp == null)
                { Plugin.Logger.LogWarning($"[Merger] purchasing create REFUSED for '{p.AddressKey}': the importer '{p.StationId}' is not known here."); return Refuse($"create: the importer '{p.StationId}' is not known here"); }
                var made = new ImportPartnership
                {
                    id = id, importAddress = imp.Address, headquartersAddress = addr,
                    employeeInstanceId = p.StrValue ?? "", nextDeliveryDay = DeliveryHelper.GetNextDeliveryDay(),
                };
                gi.importPartnerships.Add(made);
                GameEvent.Invoke("ba:gameevent_newimportpartnership");
                return true;
            }
            if (ip == null) return Gone("purchasing", op, id);
            // r2 MINOR-7: the pane's OWN guards (PurchasingAgentPlanUI.cs:201/:259 CanModifyContract, :215 the
            // tomorrow-delivery check, :237-242 StartOrder's no-items / no-warehouse checks) ran on the MEMBER,
            // after the route prefix, and against the member's DETACHED copy.  They are re-checked here on the
            // runner's REAL partnership, before anything is written - never a partial apply.
            if (op == "end" || op == "cancel")
            {
                bool can; try { can = DeliveryHelper.CanModifyContract(ip.nextDeliveryDay); } catch { can = true; }
                if (!can)
                {
                    Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED (purchasing {op}): partnership '{id}' is too close to its delivery to be modified here.");
                    return Refuse($"{op}: the contract is too close to its delivery day to be modified");
                }
            }
            if (op == "urgent" && ip.nextDeliveryDay == gi.Day + 1)
            {
                Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED (purchasing urgent): partnership '{id}' already delivers tomorrow.");
                return Refuse("urgent: there is already a delivery for tomorrow");
            }
            if (op == "start")
            {
                int wanted = 0, unwarehoused = 0;
                try
                {
                    foreach (var pr in ip.products ?? new List<ImportProduct>())
                    {
                        if (pr == null || pr.amount <= 0) continue;
                        wanted++;
                        bool bad; try { bad = pr.assignedWarehouse == null || pr.assignedWarehouse.IsUndefined(); } catch { bad = true; }
                        if (bad) unwarehoused++;
                    }
                }
                catch { }
                if (wanted == 0)
                {
                    Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED (purchasing start): partnership '{id}' has no items to deliver here.");
                    return Refuse("start: there are no items to deliver");
                }
                if (unwarehoused > 0)
                {
                    Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED (purchasing start): partnership '{id}' has {unwarehoused} item(s) with no warehouse here.");
                    return Refuse("start: an ordered item has no warehouse assigned");
                }
            }
            switch (op)
            {
                case "agent":
                    if (InjectedHere(p.StrValue)) return RefuseInjected("purchasing", op, id, p.StrValue);   // r2 MAJOR-3
                    ip.employeeInstanceId = p.StrValue ?? ""; return true;
                case "warehouse":
                case "warehouseall":
                {
                    // HO-1a H6: PurchasingAgentProductCellView.ChangeAssignedWarehouse (decompile :248-251) and
                    // the bulk PurchasingAgentProductsMassActionsUI.MassDesignateWarehouse (:103-118) both write
                    // ImportProduct.assignedWarehouse and had no route at all - on a partner's plan they wrote
                    // the display copy and were lost.  StationId carries the warehouse's address key ("" = none,
                    // the native `index <= 0` branch); StrValue names the item, or every selected item joined by
                    // '|' for the bulk.  Both are fields the payload already has - no protocol change.
                    // HO-1c L4.11: native returns while a contract is ACTIVE (decompile
                    // PurchasingAgentProductsMassActionsUI.cs:105-108), so the bulk must not re-warehouse a live
                    // contract here either - the sender's prefix mirrors the same test.
                    if (op == "warehouseall" && ip.isActive)
                    {
                        Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED (purchasing warehouseall): partnership '{id}' has an active contract here.");
                        return Refuse("warehouseall: the contract is active here");
                    }
                    Address wh = null;
                    if (!string.IsNullOrEmpty(p.StationId))
                    {
                        wh = AddrOf(p.StationId);
                        if (wh == null)
                        {
                            Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED (purchasing {op}): warehouse '{p.StationId}' is not registered here.");
                            return Refuse($"{op}: warehouse '{p.StationId}' is not registered here");
                        }
                    }
                    int hit = 0;
                    foreach (var name in (p.StrValue ?? "").Split('|'))
                    {
                        if (name.Length == 0) continue;
                        foreach (var pr in ip.products) if (pr != null && pr.itemName == name) { pr.assignedWarehouse = wh; hit++; }
                    }
                    if (hit == 0)
                    {
                        Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED (purchasing {op}): none of the named item(s) is on partnership '{id}' here.");
                        return Refuse($"{op}: none of the named item(s) is on this partnership here");
                    }
                    Plugin.Logger.LogInfo($"[Merger] purchasing {op} on partnership '{id}': {hit} product(s) re-warehoused.");
                    return true;
                }
                case "target":
                {
                    // H6: PurchasingAgentProductCellView.ChangeTarget (decompile :234-240) -> UpdateAmount, which
                    // writes ImportProduct.amount; the rest of that body is label refresh with nothing to carry.
                    string item = p.StrValue ?? "";
                    foreach (var pr in ip.products) if (pr != null && pr.itemName == item) { pr.amount = p.IntValue; return true; }
                    Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED (purchasing target): item '{item}' is not on partnership '{id}' here.");
                    return Refuse($"target: item '{item}' is not on this partnership here");
                }
                case "repeating": ip.isRepeatingOrder = p.BoolValue; return true;
                case "autostock": ip.isTarget = p.BoolValue; return true;
                case "urgent":    ip.nextDeliveryDay = gi.Day + 1; ip.isUrgentOrder = true; return true;
                case "start":     ip.isActive = true; ip.nextDeliveryDay = DeliveryHelper.GetNextDeliveryDay(); return true;
                case "cancel":    ip.isActive = false; ip.isUrgentOrder = false; return true;
                case "end":       gi.importPartnerships.Remove(ip); return true;
                case "delete":    PurchasingAgentHelper.DeletePlan(id); return true;
            }
            Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED (purchasing): unknown op '{op}'."); return Refuse($"unknown op '{op}'");
        }

        private static bool ApplyHr(GameInstance gi, Address addr, SharedWorkEditPayload p, string op, string id)
        {
            HrManagerPlan pl = null;
            foreach (var x in gi.hrManagerPlans) if (x != null && x.id == id) { pl = x; break; }
            if (op == "add")
            {
                if (pl != null) return true;
                if (addr == null) return Gone("hr", op, id);
                var made = new HrManagerPlan { headquartersAddress = addr };
                SetId(made, id);
                gi.hrManagerPlans.Add(made);
                return true;
            }
            if (pl == null) return Gone("hr", op, id);
            switch (op)
            {
                case "manager":
                    if (InjectedHere(p.StrValue)) return RefuseInjected("hr", op, id, p.StrValue);   // r2 MAJOR-3
                    pl.assignedEmployeeId = string.IsNullOrEmpty(p.StrValue) ? null : p.StrValue; return true;
                case "delete":  HrManagerHelper.DeletePlan(id); return true;
                case "insurance-cancel":  pl.CancelHealthInsurancePlan(); return true;
                case "insurance-upgrade": pl.UpgradeHealthInsurancePlan(); return true;
                case "assign":
                {
                    // The native pair, HrManagerPlanUI.cs:256-268, run here on THIS machine's REAL employee -
                    // which is what part 1 refused on the member (:261 tagged a local employee with a plan id
                    // that exists in no list there).
                    //
                    // HO-1a H2, A REMOVAL IS NEVER REFUSED.  Both guards used to sit ABOVE the assign/unassign
                    // split, so taking a person OFF a plan was refused whenever the id was an injected copy of a
                    // partner's staff (the usual case on a partner's headquarters) or no longer resolved here at
                    // all - the player pressed the button and nothing happened, with a transfer demanded for an
                    // edit that moves nobody.  Both now guard the ASSIGN branch only, which is the branch that
                    // could persist a foreign id.  An UNASSIGN always runs: it drops the id from the list and,
                    // where a record does exist here and carries this plan, clears the tag - harmless on a copy,
                    // and the only way a stale id ever leaves the list.
                    string eid = p.StrValue ?? "";
                    if (eid.Length == 0) return Gone("hr", op, id);
                    EmployeeInstance emp = null; try { emp = EmployeeHelper.GetEmployeeById(eid); } catch { }
                    if (p.BoolValue)
                    {
                        if (emp == null)
                        { Plugin.Logger.LogWarning($"[Merger] hr assign REFUSED for plan '{id}': employee '{eid}' is not on this machine (a cross-member assignment needs the host-held transfer first)."); return Refuse($"assign: employee '{eid}' is not on this machine"); }
                        // r2 MAJOR-3: an INJECTED partner copy sits on the REAL roster here (MergerEmployeeSync.cs:796,
                        // MPRegisterSync.cs:1374), so `emp != null` was never the test it looked like - a member's own
                        // employee id resolved to the copy and the two writes below would have persisted a FOREIGN id.
                        if (InjectedHere(eid)) return RefuseInjected("hr", op, id, eid);
                        if (!pl.assignedEmployees.Contains(eid)) pl.assignedEmployees.Add(eid);
                        emp.assignedHrManagerPlanId = pl.id;
                    }
                    else
                    {
                        pl.assignedEmployees.Remove(eid);
                        if (emp != null && emp.assignedHrManagerPlanId == pl.id) emp.assignedHrManagerPlanId = null;
                    }
                    return true;
                }
                case "clear":
                {
                    // HO-1a H1: HrManagerPlanUI.ClearAssignedEmployees (decompile :242-254) LOOPS
                    // SetEmployeeAssigned(id, false, refreshData: false), so "unassign all" used to be one leg per
                    // employee and the host's own per-sender cap (MPServer.SharedRateOk, ten work edits a second)
                    // dropped most of the burst in silence.  One leg now; the runner replays the same pair on its
                    // REAL roster, without the two UI calls at :252-253 that have no meaning off-screen.
                    int cleared = 0;
                    foreach (var one in new List<string>(pl.assignedEmployees))
                    {
                        pl.assignedEmployees.Remove(one);
                        EmployeeInstance e = null; try { e = EmployeeHelper.GetEmployeeById(one); } catch { }
                        if (e != null && e.assignedHrManagerPlanId == pl.id) e.assignedHrManagerPlanId = null;
                        cleared++;
                    }
                    Plugin.Logger.LogInfo($"[Merger] hr clear on plan '{id}': {cleared} assignment(s) released.");
                    return true;
                }
                case "fill":
                {
                    // HO-1a H1: HrManagerPlanUI.Fill (decompile :224-240) takes the first (MaxEmployees -
                    // NumberOfAssignedEmployees) unassigned people off its own assignable list, again one
                    // SetEmployeeAssigned per person.  One leg now, IntValue carrying the free count the sender
                    // saw; the runner fills from its OWN eligible set - real records, unassigned, never an
                    // injected copy of a partner's person (H3) - capped by its own plan's real free count.
                    int free = pl.MaxEmployees - pl.NumberOfAssignedEmployees;
                    if (p.IntValue > 0 && p.IntValue < free) free = p.IntValue;
                    if (free <= 0) { Plugin.Logger.LogInfo($"[Merger] hr fill on plan '{id}': no free slot here."); return true; }
                    int added = 0;
                    foreach (var e in EmployeeHelper.GetEmployeeInstances())
                    {
                        if (added >= free) break;
                        if (e == null || !string.IsNullOrEmpty(e.assignedHrManagerPlanId)) continue;   // native :233
                        if (e.id == pl.assignedEmployeeId) continue;                                   // native :233
                        if (InjectedHere(e.id)) continue;                                              // H3: our own people only
                        // HO-1c L4.5: the no-argument GetEmployeeInstances() is the RAW roster (decompile
                        // EmployeeHelper.cs:575-578; the mod filters the QueryInfo overload only), so the synthetic
                        // duty stand-ins are in it - and they are not IsInjectedStaff.  A stand-in must never be
                        // tagged with a plan id.
                        if (MPRegisterSync.IsSyntheticDuty(e.id)) continue;
                        if (pl.assignedEmployees.Contains(e.id)) continue;
                        pl.assignedEmployees.Add(e.id);
                        e.assignedHrManagerPlanId = pl.id;
                        added++;
                    }
                    Plugin.Logger.LogInfo($"[Merger] hr fill on plan '{id}': {added} of {free} free slot(s) taken.");
                    return true;
                }
                case "insurance-accept":
                case "insurance-decline":
                {
                    // D23: the game's own HealthInsurancePlanOffer method on the REAL offer, which is the one
                    // thing that writes HrManagerPlan.healthInsurancePlan (HealthInsurancePlanOffer.cs:86-98).
                    string offerId = p.StrValue ?? "";
                    HealthInsurancePlanOffer offer = null;
                    foreach (var o in gi.healthInsurancePlanOffers ?? new List<HealthInsurancePlanOffer>())
                        if (o != null && o.id == offerId) { offer = o; break; }
                    if (offer == null)
                    { Plugin.Logger.LogWarning($"[Messages] insurance {op} REFUSED for plan '{id}': offer '{offerId}' is gone on this machine."); return Refuse($"{op}: that insurance offer is gone here"); }
                    // r3 G4b: ALREADY SETTLED is a benign no-op, not a refusal - the offer has reached the answer
                    // the sender asked for, and a resend after a reconnect must read as done, not as failed.
                    if (offer.negotiationFinished)
                    { Plugin.Logger.LogInfo($"[Messages] insurance {op} for plan '{id}': offer '{offerId}' was already settled here - no-op."); return true; }
                    if (op == "insurance-accept") offer.AcceptOffer(p.Estimate);
                    else offer.DeclineOffer();
                    Plugin.Logger.LogInfo($"[Messages] insurance {(op == "insurance-accept" ? "accepted" : "declined")} here for plan '{id}' on behalf of '{p.PlayerId}' (offer {offerId}).");
                    return true;
                }
            }
            Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED (hr): unknown op '{op}'."); return Refuse($"unknown op '{op}'");
        }

        private static bool ApplyHeadhunter(GameInstance gi, Address addr, SharedWorkEditPayload p, string op, string id)
        {
            HeadhunterPlan pl = null;
            foreach (var x in gi.headhunterPlans) if (x != null && x.id == id) { pl = x; break; }
            if (op == "add")
            {
                if (pl != null) return true;
                if (addr == null) return Gone("headhunter", op, id);
                var made = new HeadhunterPlan { headquartersAddress = addr };
                SetId(made, id);
                gi.headhunterPlans.Add(made);
                return true;
            }
            if (pl == null) return Gone("headhunter", op, id);
            switch (op)
            {
                case "manager":
                    if (InjectedHere(p.StrValue)) return RefuseInjected("headhunter", op, id, p.StrValue);   // r2 MAJOR-3
                    pl.assignedEmployeeId = string.IsNullOrEmpty(p.StrValue) ? null : p.StrValue; return true;
                case "delete":  HeadhunterHelper.DeletePlan(id); return true;
                // r2 MAJOR-5 (D3).  The recruiting tab's writes are LIVE native writes on
                // planUI.currentPlan (HeadhuntersRecruitingTab.cs:168 skillValueTarget, :312 skillRecruiting,
                // :332/:336 dealBreakerTypes, :239-240 the candidate counts), so on a partner's row they only
                // ever reached the DETACHED temp object - `recruit` then ran on the RUNNER'S OWN settings, and
                // a change made while the plan was already recruiting did nothing at all.  `settings` carries
                // the whole DTO and is what makes those writes real; `recruit` applies it first.
                case "settings":    return ApplyRecruitSettings(pl, p, id);
                case "stoprecruit": pl.isRecruiting = false; return true;     // HeadhuntersRecruitingTab.cs:156
                case "recruit":
                    // r3 G4c: a REFUSED settings copy must stop the start - starting anyway recruits on the
                    // runner's own stale settings, which is r2 MAJOR-5 in a different coat.  ApplyRecruitSettings
                    // has already called Refuse(), so the member gets that failure's own reason.
                    if (!ApplyRecruitSettings(pl, p, id))
                    { Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED (headhunter recruit): plan '{id}' was NOT started - its settings were refused."); return false; }
                    pl.StartRecruiting(); return true;
                case "hrplan":
                {
                    // `public string[] assignedHrPlans = new string[2]` (HeadhunterPlan.cs:25) - a fixed pair.
                    int slot = p.IntValue;
                    if (pl.assignedHrPlans == null || slot < 0 || slot >= pl.assignedHrPlans.Length) return Gone("headhunter", op, id);
                    string want = p.StrValue ?? "";
                    // r2 MAJOR-4: the id was written with NO existence check, and the member's dropdown is built
                    // from ITS OWN hrManagerPlans with no headquarters filter
                    // (HeadhuntersAutomaticReplacementTab.cs:94-97), so a foreign - or simply wrong-building -
                    // plan id could be persisted here.  It must be a plan this machine holds, ON THIS
                    // headquarters.  Clearing the slot ("") is always allowed.
                    if (want.Length > 0)
                    {
                        HrManagerPlan hrp = null;
                        foreach (var x in gi.hrManagerPlans) if (x != null && x.id == want) { hrp = x; break; }
                        if (hrp == null || !Same(KeyOf(hrp.headquartersAddress), KeyOf(pl.headquartersAddress)))
                        {
                            Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED (headhunter hrplan): hr plan '{want}' is not on this headquarters.");
                            return Refuse($"hr plan '{want}' is not on this headquarters");
                        }
                        // r3 G4e: the native list also drops an HR plan ALREADY assigned to ANOTHER headhunter
                        // plan (HeadhuntersAutomaticReplacementTab.cs:94-97); the overlay REPLACED that list, so
                        // the runner enforces the same rule. GLOBAL across every headhunter plan, not just this
                        // headquarters' (re-check r3 of part 2a): the game's own filter walks the whole
                        // gi.headhunterPlans list and so does the overlay's `taken` set (~:1498-1502), so a
                        // per-HQ check here would let the runner accept an assignment both of those refuse.
                        foreach (var other in gi.headhunterPlans)
                        {
                            if (other == null || ReferenceEquals(other, pl) || other.assignedHrPlans == null) continue;
                            bool clash = false;
                            foreach (var a in other.assignedHrPlans) if (!string.IsNullOrEmpty(a) && a == want) { clash = true; break; }
                            if (clash)
                            {
                                Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED (headhunter hrplan): hr plan '{want}' is already assigned to headhunter plan '{other.id}' on this headquarters.");   // wording kept: the refusal and its readout pattern are unchanged
                                return Refuse($"hr plan '{want}' is already assigned to another headhunter plan");
                            }
                        }
                    }
                    pl.assignedHrPlans[slot] = want.Length == 0 ? null : want;
                    return true;
                }
            }
            Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED (headhunter): unknown op '{op}'."); return Refuse($"unknown op '{op}'");
        }

        /// <summary>r2 MAJOR-5.  The recruiting fields the tab writes - and, since r3 G1, the automatic-
        /// replacement pair the replacement tab's two toggles write the same live way - copied off the DTO onto
        /// the runner's REAL plan.  Nothing else on the plan is touched: the DTO is the member's screen, not
        /// its truth.</summary>
        private static bool ApplyRecruitSettings(HeadhunterPlan pl, SharedWorkEditPayload p, string id)
        {
            var d = p.HeadhunterPlan;
            if (d == null)
            {
                Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED (headhunter settings): the leg for plan '{id}' carried no plan.");
                return Refuse("settings: the leg carried no plan");
            }
            try
            {
                if (!string.IsNullOrEmpty(d.SkillRecruiting)) pl.skillRecruiting = d.SkillRecruiting;
                pl.skillValueTarget = d.SkillValueTarget;
                pl.remainingCandidatesToRecruit = d.RemainingCandidatesToRecruit;
                pl.amountOfCandidatesToRecruitPreference = d.AmountOfCandidatesToRecruitPreference;
                if (pl.dealBreakerTypes == null) pl.dealBreakerTypes = new List<string>();
                pl.dealBreakerTypes.Clear();
                foreach (var x in d.DealBreakerTypes ?? new List<string>()) if (!string.IsNullOrEmpty(x)) pl.dealBreakerTypes.Add(x);
                // r3 G1: the two automatic-replacement toggles are bare live writes on planUI.currentPlan
                // (HeadhuntersAutomaticReplacementTab.cs:163-166 and :168-171), so they ride this same leg.
                pl.automaticallyReplaceOnResign = d.AutomaticallyReplaceOnResign;
                pl.automaticallyReplaceOnRetire = d.AutomaticallyReplaceOnRetire;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED (headhunter settings) for plan '{id}': {ex.Message}");
                return Refuse("settings: " + ex.Message);
            }
            return true;
        }

        /// <summary>r2 MAJOR-4, THE MEMBER END.  The automatic-replacement dropdown on an OVERLAY headhunter
        /// plan must offer the HR plans of THAT headquarters, which live only in the registry - the member's
        /// own hrManagerPlans are a different company's.  Ids and names come back in the registry's order,
        /// with the game's own "no selection" entry first, exactly as the native list is shaped.</summary>
        public static bool TryOverlayHrPlanOptions(object headhunterPlan, out List<string> ids, out List<string> names)
        {
            ids = null; names = null;
            try
            {
                if (!IsOverlayPlan(headhunterPlan)) return false;
                if (!_rowInfo.TryGetValue(headhunterPlan, out var ri) || ri == null) return false;
                if (!_byOwner.TryGetValue(ri.Owner, out var q) || q == null) return false;
                // r3 G4e: the native list drops an HR plan already assigned to ANOTHER headhunter plan
                // (HeadhuntersAutomaticReplacementTab.cs:94-97, `where !...headhunterPlans.Exists(plan => plan !=
                // planUI.currentPlan && plan.assignedHrPlans.Contains(x.id))`).  Replacing that list dropped the
                // filter with it, so it is rebuilt here from the owner's OTHER headhunter plans.
                var taken = new HashSet<string>(StringComparer.Ordinal);
                foreach (var h in q.HeadhunterPlans ?? new List<PwHeadhunterPlan>())
                {
                    if (h == null || string.Equals(h.Id, ri.PlanId, StringComparison.Ordinal)) continue;
                    foreach (var a in h.AssignedHrPlans ?? new List<string>()) if (!string.IsNullOrEmpty(a)) taken.Add(a);
                }
                ids = new List<string> { null };
                names = new List<string> { VehicleStoragePanel.Localize("headhunter_select_employee") };
                int n = 0;
                foreach (var d in q.HrManagerPlans ?? new List<PwHrPlan>())
                {
                    if (d == null || string.IsNullOrEmpty(d.Id)) continue;
                    if (!Same(d.HeadquartersAddressKey, ri.Hq)) continue;
                    if (taken.Contains(d.Id)) continue;
                    n++;
                    ids.Add(d.Id);
                    string who = EmpName(d.AssignedEmployeeId);
                    // No authored text: the manager's own name, else the game's OWN unassigned-plan phrase
                    // (localised through the reflection helper - the compile-time GetLocalization(string,Object)
                    // overload is runtime-absent in this build, VehicleStoragePanel.cs:670-672).
                    names.Add(who.Length > 0 ? who : VehicleStoragePanel.Localize("bizman_headhunter_unassigned_hr_plan_name"));
                }
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] overlay hr-plan options: {ex.Message}"); ids = null; names = null; return false; }
        }

        // -- E5: the `planedit` verb ------------------------------------------

        /// <summary>`planedit FAMILY PLANID OP [arg]` - sends exactly the leg the pane would, off the temp row
        /// the registry holds.  It never writes this machine's own state.</summary>
        public static string TestDriveEdit(string arg)
        {
            try
            {
                var a = (arg ?? "").Trim().Split(new[] { ' ' }, 4, StringSplitOptions.RemoveEmptyEntries);
                if (a.Length < 3) return "ERR planedit <family> <planId> <op> [arg]";
                string fam = a[0].ToLowerInvariant(), id = a[1], op = a[2], rest = a.Length > 3 ? a[3] : "";
                // HO-1d MINOR-A (BUILD POPUPS-1 P5): the SAME lookup `planbulk` / `planlist` take, stale
                // drop included.  Materialising without dropping the stale families first read a row built
                // from the PREVIOUS feed, so `planedit` could address a row the runner had already replaced.
                object row = FindTestDriveRow(fam, id);
                if (row == null && op != "add") return $"ERR no {fam} row '{id}' in the registry here";
                float num = 0f; int iv = 0; bool bv = false;
                string str = rest;
                int sp = rest.LastIndexOf(' ');
                if (sp > 0 && float.TryParse(rest.Substring(sp + 1), System.Globalization.NumberStyles.Float,
                                             System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                { num = parsed; str = rest.Substring(0, sp); }
                else { bool.TryParse(rest, out bv); int.TryParse(rest, out iv); }
                bool routed = op == "add" ? RoutePlanCreateAt(fam, str.Trim())   // r2b: the HQ key is the argument (no pane on the rig)
                                          : RoutePaneEdit(fam, "planedit verb", row, op, str, iv, num, bv);
                return $"OK planedit {fam} {id} {op} routed={routed}";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        // -- HO-1c L3: the `planbulk` / `planlist` rig levers ------------------

        /// <summary>The row the two rig verbs address - the same registry lookup `planedit` uses.</summary>
        private static object FindTestDriveRow(string fam, string id)
        {
            // T-HO1 run 3 (2026-09-12): a feed marks every family STALE (Receive) and the screen's ShowPartnerRows is
            // what drops the stale rows before rebuilding - on the rig no screen is open, so the lever must do the
            // same drop itself or it reads the row built from the PREVIOUS feed (assigned=33 after a clear the runner
            // had applied and published urgently). The shipped screen path was never wrong; only the lever was.
            foreach (var ownerPid in new List<string>(_byOwner.Keys))
            {
                foreach (var f in Families) if (_stale.Remove(ownerPid + "|" + f)) DropRowsOf(ownerPid, f);
                MaterialiseRows(ownerPid);   // no screen on the rig
            }
            foreach (var kv in _rows)
                if (kv.Value != null && _rowInfo.TryGetValue(kv.Value, out var q) && q.Family == fam && q.PlanId == id) return kv.Value;
            return null;
        }

        /// <summary>HO-1c L3 (HO-1d: suggestall takes the item names).  `planbulk FAMILY PLANID clear|fill [n]|suggestall <item|item...>` - exactly the ONE leg the bulk
        /// prefix sends off a partner's temp row (IntValue = the free count `fill` carries).  Writes nothing here:
        /// no display mutate is passed, so only the runner's answer changes anything.</summary>
        public static string TestDriveBulk(string arg)
        {
            try
            {
                var a = (arg ?? "").Trim().Split(new[] { ' ' }, 4, StringSplitOptions.RemoveEmptyEntries);
                if (a.Length < 3) return "ERR usage: planbulk <family> <planId> clear|fill [n]|suggestall <item|item...>";
                string fam = a[0].ToLowerInvariant(), id = a[1], op = a[2].ToLowerInvariant();
                if (op != "clear" && op != "fill" && op != "suggestall")
                    return "ERR usage: planbulk <family> <planId> clear|fill [n]|suggestall <item|item...>";
                int n = 0; string names = "";
                if (op == "suggestall") { names = a.Length > 3 ? a[3].Trim() : ""; if (names.Length == 0) return "ERR suggestall needs the item names the button would send: <item|item...>"; }
                else if (a.Length > 3) int.TryParse(a[3].Trim(), out n);
                object row = FindTestDriveRow(fam, id);
                if (row == null) return $"ERR no {fam} row '{id}' in the registry here";
                bool routed = RoutePaneEdit(fam, "planbulk verb", row, op, names, n);
                return $"OK planbulk {fam} {id} {op}{(op == "fill" ? " n=" + n : "")} routed={routed}";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        /// <summary>HO-1c L3.  `planlist FAMILY PLANID` - the DISPLAY copy's assigned employees (hr: the count and
        /// the ids) or its row count (every other family), so a rig run can assert the optimistic mutate and the
        /// urgent feed that follows it.  Read-only.</summary>
        public static string TestDriveList(string arg)
        {
            try
            {
                var a = (arg ?? "").Trim().Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (a.Length < 2) return "ERR usage: planlist <family> <planId>";
                string fam = a[0].ToLowerInvariant(), id = a[1].Trim();
                object row = FindTestDriveRow(fam, id);
                if (row == null) return $"ERR no {fam} row '{id}' in the registry here";
                if (row is Buildings.Office.Headquarters.HrManagerPlan hp)
                {
                    var ids = hp.assignedEmployees ?? new List<string>();
                    return $"OK planlist hr {id} assigned={ids.Count}" + (ids.Count > 0 ? " " + string.Join(",", ids) : "");
                }
                foreach (var member in new[] { "cachedSuggestions", "products", "manuallyPricedItems", "candidates" })
                {
                    object v = null;
                    try
                    {
                        var t = row.GetType();
                        var pi = t.GetProperty(member);
                        var fi = pi == null ? t.GetField(member) : null;
                        v = pi != null ? pi.GetValue(row) : (fi != null ? fi.GetValue(row) : null);
                    }
                    catch { }
                    if (v is System.Collections.ICollection col) return $"OK planlist {fam} {id} {member}={col.Count}";
                }
                return $"OK planlist {fam} {id} rows=unknown";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
    }
}
