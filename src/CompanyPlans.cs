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
        /// it keeps wave 4's tagged installed display copies.  CROSS-HR-1: HR is now BOTH - it stays here
        /// (the DTOs still arrive and still feed the detached rows) but where a SHADOW is installed for an
        /// owner and headquarters the shadow is the row and the detached copy is not drawn (ShowPartnerRows).</summary>
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
                DropShadowRows(ownerPid);                    // CROSS-HR-1 S4: the shadows go with them
                if (had) Plugin.Logger.LogInfo($"[Plans] cleared ({why}: '{ownerPid}')");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] clear '{ownerPid}': {ex.Message}"); }
        }

        public static void ClearAll(string why)
        {
            if (_byOwner.Count == 0 && _rows.Count == 0) { _suspended.Clear(); return; }
            _byOwner.Clear(); _suspended.Clear(); _rows.Clear(); _rowOwner.Clear(); _refusalLogged.Clear(); _stale.Clear(); _drawn.Clear();
            _rowInfo.Clear(); _seq.Clear(); _applied.Clear(); _shadowRows.Clear();   // CROSS-HR-1 S4
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
                DropShadowRows(ownerPid);                    // CROSS-HR-1 S4: the REAL plans replace the shadows
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
        /// screen: ask each of the five lists that is actually active to rebuild itself through its OWN
        /// private refresh (PricingManagersPlanList.RefreshPlansList :90, HrManagersPlanList :82,
        /// HeadhuntersPlanList :73, PurchasingAgentsPlanList.RefreshManagersList :71 and - U4 - the
        /// LogisticsManagersPlanList.RefreshManagersList :87 the logistics union now hangs off) - the same seam
        /// SharedShopWorkTabs uses for the warehouse tabs.  Each rebuild runs the overlay postfix, which
        /// drops that family's stale rows and re-adds them from the new feed.  MAIN THREAD: the only caller
        /// is Receive, which CompanyLists already enqueues.</summary>
        private static void RefreshOpenTabsFor(string ownerPid)
        {
            try
            {
                if (string.IsNullOrEmpty(ownerPid)) return;
                if (!CompanyHqPageOpen(out _, out _)) return;     // U1: every company HQ page shows this owner's rows
                string why = EditWindowOpen();
                if (why.Length > 0)
                {
                    // HO-1a H5: a rebuild under a HALF-TYPED edit throws the player's own input away.  Since
                    // HQ-PARITY-1 P6 that is ALL it waits for - an open pane is rebuilt and RESELECTED below.
                    if (_pendingRedraw != ownerPid || _pendingReason != why)
                        Plugin.Logger.LogInfo($"[Plans] redraw for '{ownerPid}' DEFERRED - {why}.");
                    _pendingRedraw = ownerPid; _pendingReason = why;  // last writer wins: a redraw is a full rebuild
                    return;
                }
                _pendingReason = "";
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var bm = ui != null && ui.fullMenu != null ? ui.fullMenu.bizMan : null;
                if (bm == null) return;
                int n = 0, kept = 0;
                n += Rebuild(bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagersPlanList>(true), "RefreshPlansList", ref kept);
                n += Rebuild(bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.PurchasingAgentsPlanList>(true), "RefreshManagersList", ref kept);
                n += Rebuild(bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.HRManagers.HrManagersPlanList>(true), "RefreshManagersList", ref kept);
                n += Rebuild(bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.Headhunters.HeadhuntersPlanList>(true), "RefreshManagersList", ref kept);
                n += Rebuild(bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagersPlanList>(true), "RefreshManagersList", ref kept);   // U4: logistics joined the union
                if (n > 0) Plugin.Logger.LogInfo($"[Plans] redrew {n} open tab(s) for '{ownerPid}' - a newer feed arrived"
                                               + (kept > 0 ? $"; {kept} open pane(s) reselected." : "."));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] redraw for '{ownerPid}': {ex.Message}"); }
        }

        /// <summary>HO-1a H5.  The owner whose redraw is waiting for the player to finish an edit; "" = none.</summary>
        private static string _pendingRedraw = "";

        /// <summary>P6: WHY the pending redraw is waiting, so a deferral logs once per REASON rather than
        /// once per feed.</summary>
        private static string _pendingReason = "";

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
                if (!CompanyHqPageOpen(out _, out _)) { _pendingRedraw = ""; _pendingReason = ""; return; }
                if (EditWindowOpen().Length > 0) return;
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
        private static string EditWindowOpen()
        {
            try
            {
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var bm = ui != null && ui.fullMenu != null ? ui.fullMenu.bizMan : null;
                if (bm == null) return "";

                var hr = bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI>(true);
                if (hr != null && hr.gameObject.activeInHierarchy && hr.IsAssignEmployeesListOpen)
                    return "the HR assign list is open";

                // HQ-PARITY-1 c2: the HEADHUNTER family is the one list whose rows carry NO readable plan
                // identity.  Its selection is `private Transform _selectedEntry` (HeadhuntersPlanList.cs:32),
                // its rows are bare Instantiate clones whose only plan reference is the click closure
                // (SetUpPlanEntry :91-94) and its `SelectPlan(Transform, HeadhunterPlan)` is private (:213);
                // a row ORDINAL is no identity either, because the partner rows drawn through that same
                // builder are synthesised plans GetAssignedPlansForHeadquarters never returns.  With no honest
                // reselect available, an OPEN headhunter plan DEFERS the redraw - TickDeferredRedraw lands it
                // the moment the player closes the pane, and the blind sibling index (which could land on the
                // Add-plan row that `buttonEntry.SetAsLastSibling()` :82 parks among the entries) is gone.
                foreach (var hh in bm.GetComponentsInChildren<UI.Smartphone.Apps.BizMan.Headhunters.HeadhuntersPlanList>(true))
                {
                    if (hh == null || !hh.gameObject.activeInHierarchy) continue;
                    var sf = typeof(UI.Smartphone.Apps.BizMan.Headhunters.HeadhuntersPlanList)
                                 .GetField("_selectedEntry", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (sf != null && sf.GetValue(hh) != null) return "a headhunter plan is open";
                }

                // HQ-PARITY-1 P6: the LOGISTICS clause is GONE.  It read "a logistics pane is open at all",
                // which is true for the whole time the player is on that plan - so the very page the feed was
                // about was the one page that never refreshed.  A rebuild now RESELECTS the open plan
                // (Rebuild -> Reselect), which is what the player wanted: the same pane, the fresh numbers.

                var dd = UI.Elements.Dropdown.currentDropdown;
                if (dd != null && dd.gameObject.activeInHierarchy)
                {
                    var f = typeof(UI.Elements.Dropdown).GetField("optionsPanelParentRect",
                                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var rect = f != null ? f.GetValue(dd) as RectTransform : null;
                    if (rect != null && rect.gameObject.activeSelf) return "a dropdown is open";
                }

                // An EXPANDED purchasing product row IS the target-amount input field, so a rebuild there
                // would throw away a half-typed number: this one still defers, and TickDeferredRedraw lands
                // the redraw the moment the row closes.
                foreach (var cell in bm.GetComponentsInChildren<UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentProductCellView>(false))
                {
                    if (cell == null) continue;
                    var lf = cell.GetType().GetField("_layoutElement", BindingFlags.Instance | BindingFlags.NonPublic);
                    object le = lf != null ? lf.GetValue(cell) : null;
                    if (le == null) continue;
                    var mh = le.GetType().GetProperty("minHeight");
                    if (mh != null && mh.GetValue(le) is float h && h > 100f) return "a purchasing product row is expanded";
                }
                return "";
            }
            catch { return ""; }
        }

        private static int Rebuild(Component list, string method, ref int reselected)
        {
            try
            {
                if (list == null || !list.gameObject.activeInHierarchy) return 0;   // only the tab on screen
                var m = list.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (m == null) return 0;
                // HQ-PARITY-1 P6: every one of the five refresh bodies opens with a PlanUI.Hide() and clears
                // its selection (PurchasingAgentsPlanList.RefreshManagersList :73 and its four twins), so a
                // rebuild CLOSES the pane the player is reading.  Note which plan is open first, then put the
                // player back on it through the list's OWN entry click, so the pane returns with the values
                // the feed just brought instead of vanishing.
                var open = OpenPlanOf(list);
                m.Invoke(list, null);
                if (open.Id.Length > 0)
                    if (Reselect(list, open)) reselected++;
                return 1;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] {method}: {ex.Message}"); return 0; }
        }

        /// <summary>P6: which plan one of the five lists has OPEN, in the one form all five can answer.  Id
        /// is the plan id wherever the list makes it readable - the entry component's public Plan (pricing
        /// and logistics entries), the _entriesByPlanIds map (HrManagersPlanList :105) or the entry object's
        /// NAME, which purchasing sets to the plan id (PurchasingAgentsPlanList.SetUpPlanEntry :87) - and ""
        /// for headhunter, whose entries carry no id at all - that family never reselects, it DEFERS
        /// (EditWindowOpen).  c2: there is NO sibling-index fallback; a position is not an identity.</summary>
        private struct OpenPlan { public string Id; }

        private static OpenPlan OpenPlanOf(Component list)
        {
            var r = new OpenPlan { Id = "" };
            try
            {
                var f = list.GetType().GetField("_selectedEntry", BindingFlags.Instance | BindingFlags.NonPublic);
                object sel = f != null ? f.GetValue(list) : null;
                var t = TransformOf(sel);
                if (t == null) return r;
                r.Id = PlanIdOfEntry(list, sel, t);
            }
            catch { }
            return r;
        }

        private static Transform TransformOf(object entry)
        {
            var t = entry as Transform; if (t != null) return t;
            var c = entry as Component;  return c != null ? c.transform : null;
        }

        /// <summary>P6, the ONE entry-to-plan-id resolver for all five families, in order of certainty.</summary>
        private static string PlanIdOfEntry(Component list, object entry, Transform t)
        {
            try
            {
                if (entry != null && !(entry is Transform))
                {
                    var pp = entry.GetType().GetProperty("Plan", BindingFlags.Instance | BindingFlags.Public);
                    object plan = pp != null ? pp.GetValue(entry) : null;
                    if (plan != null)
                    {
                        var idf = plan.GetType().GetField("id", BindingFlags.Instance | BindingFlags.Public);
                        var id = idf != null ? idf.GetValue(plan) as string : null;
                        if (!string.IsNullOrEmpty(id)) return id;
                    }
                }
                var mf = list.GetType().GetField("_entriesByPlanIds", BindingFlags.Instance | BindingFlags.NonPublic);
                if (mf != null && mf.GetValue(list) is System.Collections.IDictionary map && t != null)
                    foreach (System.Collections.DictionaryEntry kv in map)
                        if (ReferenceEquals(TransformOf(kv.Value), t) && kv.Key is string k && k.Length > 0) return k;
                // Purchasing NAMES its entry for the plan (`entry.name = plan.id`,
                // PurchasingAgentsPlanList.SetUpPlanEntry :87).  Every other family leaves the name
                // Instantiate gave it, which ends in "(Clone)" and is the SAME on every row - taking that
                // as a plan id would have matched the first row of the list every time, so it is rejected
                // here.
                if (t != null && t.name.Length > 0 && !t.name.EndsWith("(Clone)", StringComparison.Ordinal)) return t.name;
            }
            catch { }
            return "";
        }

        /// <summary>P6: put the player back on the plan the rebuild just closed, through the LIST'S OWN
        /// selection path, so nothing here duplicates the native selection.  False = the plan is gone from
        /// the rebuilt list (deleted at its owner), the one case where the pane is meant to stay shut.
        /// c1: the path is PER FAMILY - only three of the five wire the click to the entry ROOT.</summary>
        private static bool Reselect(Component list, OpenPlan open)
        {
            try
            {
                if (open.Id.Length == 0) return false;
                string fam = list.GetType().Name;

                // PRICING wires its click to a SERIALIZED CHILD, never the root: `notSelectedButton.onClick
                // .AddListener(... onSelected?.Invoke(this))` (PricingManagersPlanListEntry.cs:46-50), so a
                // press on the entry root fires nothing.  The list selects for us instead:
                // `public void SelectPlanById(string planId)` (PricingManagersPlanList.cs:144) walks _entries
                // and calls its own private SelectPlan - one call, no row hunting here.
                if (fam == "PricingManagersPlanList")
                {
                    var sel = list.GetType().GetMethod("SelectPlanById", BindingFlags.Instance | BindingFlags.Public,
                                                       null, new[] { typeof(string) }, null);
                    if (sel == null) return false;
                    sel.Invoke(list, new object[] { open.Id });
                    var f = list.GetType().GetField("_selectedEntry", BindingFlags.Instance | BindingFlags.NonPublic);
                    return f != null && f.GetValue(list) != null;
                }

                var parent = EntryParentOf(list);
                if (parent == null) return false;
                Transform hit = null; Component hitEntry = null;
                for (int i = 0; i < parent.childCount; i++)
                {
                    var c = parent.GetChild(i);
                    if (!c.gameObject.activeSelf) continue;
                    var ec = EntryComponentOf(c);
                    if (PlanIdOfEntry(list, ec, c) != open.Id) continue;
                    hit = c; hitEntry = ec; break;
                }
                if (hit == null) return false;

                // LOGISTICS wires its click to the same serialized CHILD (LogisticsManagersPlanListEntry.cs:44-48).
                // Its public selection takes the row AND the plan: `public void SelectPlan(Transform
                // entryTransform, LogisticsManagerPlan plan)` (LogisticsManagersPlanList.cs:207), and the plan is
                // the rebuilt row's own `public LogisticsManagerPlan Plan { get; private set; }` (entry :36).
                if (fam == "LogisticsManagersPlanList")
                {
                    var pp = hitEntry != null ? hitEntry.GetType().GetProperty("Plan", BindingFlags.Instance | BindingFlags.Public) : null;
                    object plan = pp != null ? pp.GetValue(hitEntry) : null;
                    if (plan == null) return false;
                    var sel = list.GetType().GetMethod("SelectPlan", BindingFlags.Instance | BindingFlags.Public,
                                                       null, new[] { typeof(Transform), pp.PropertyType }, null);
                    if (sel == null) return false;
                    sel.Invoke(list, new object[] { hit, plan });
                    return true;
                }

                // Purchasing (PurchasingAgentsPlanList.cs:91), HR (HrManagersPlanList.cs:101) and headhunter
                // (HeadhuntersPlanList.cs:91) put the listener on the entry ROOT, so the root Button IS that
                // list's SelectPlan.  (Headhunter never reaches here: it carries no id and defers instead.)
                var btn = hit.GetComponent<UnityEngine.UI.Button>();
                if (btn == null) return false;
                btn.onClick.Invoke();
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] reselect: {ex.Message}"); return false; }
        }

        /// <summary>Every entry is instantiated under the TEMPLATE's parent (all five SetUpPlanEntry bodies
        /// do exactly that), so the template field is the handle on the row container.</summary>
        private static Transform EntryParentOf(Component list)
        {
            try
            {
                foreach (var f in list.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (f.Name != "entryTemplate") continue;
                    var t = TransformOf(f.GetValue(list));
                    if (t != null && t.parent != null) return t.parent;
                }
            }
            catch { }
            return null;
        }

        /// <summary>The entry COMPONENT on a rebuilt row, where that family has one (pricing, logistics).</summary>
        private static Component EntryComponentOf(Transform t)
        {
            try
            {
                foreach (var c in t.GetComponents<Component>())
                {
                    if (c == null) continue;
                    if (c.GetType().Name.EndsWith("PlanListEntry", StringComparison.Ordinal)) return c;
                }
            }
            catch { }
            return null;
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
        /// use THIS predicate plus PartnerRowsDrawn(family) since U7.</summary>
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

        // -- HQ-PARITY-1 P7: ONE HEADQUARTERS CARD PER COMPANY ----------------

        /// <summary>P7, THE ONE DECISION POINT (manager ruling, 2026-09-12).  WHICH headquarters backs the
        /// company's single card in the BizMan hub: this machine's OWN headquarters if it has one (an own
        /// card is never hidden), otherwise the headquarters of the FIRST pid in MergerSync.MyMemberPidsOrdered
        /// (MergerSync.cs:203 - host-ordered, so every machine picks the same one) that has a resolvable
        /// FLIPPED headquarters registration here.  Ties inside one pid break on ascending
        /// GameStateReader.AddressKey.  False = no company card can be named (not a member, or no flipped
        /// headquarters resolves to an owner) - and then NOTHING is hidden, which is the pre-P7 behaviour.
        /// The hub lists every RentedByPlayer headquarters registration (decompile HeadquartersList.cs:26),
        /// and the flip makes a partner's registration RentedByPlayer here, which is why the company was
        /// showing one card per member.</summary>
        public static bool BackingHq(out string hqKey, out string ownerPid)
        {
            hqKey = ""; ownerPid = "";
            try
            {
                if (!MergerSync.IAmMember) return false;
                var gi = SaveGameManager.Current;
                if (gi == null || gi.BuildingRegistrations == null) return false;
                string own = "";
                var byPid = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null || reg.businessTypeName != "ba:businesstype_headquarters") continue;
                    bool rented; try { rented = reg.RentedByPlayer; } catch { continue; }
                    if (!rented) continue;
                    string k = GameStateReader.AddressKey(reg);
                    if (string.IsNullOrEmpty(k)) continue;
                    if (MergerFlip.TrulyMine(reg))
                    { if (own.Length == 0 || string.CompareOrdinal(k, own) < 0) own = k; continue; }
                    if (!MergerFlip.IsFlipped(k)) continue;
                    if (!CompanyLists.TryOwnerOfAddress(k, out var pid) || string.IsNullOrEmpty(pid)) continue;
                    if (!byPid.TryGetValue(pid, out var cur) || string.CompareOrdinal(k, cur) < 0) byPid[pid] = k;
                }
                if (own.Length > 0) { hqKey = own; ownerPid = MPConfig.PlayerId; return true; }
                foreach (var pid in MergerSync.MyMemberPidsOrdered)
                    if (!string.IsNullOrEmpty(pid) && byPid.TryGetValue(pid, out var k2))
                    { hqKey = k2; ownerPid = pid; return true; }
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyPlans] backing headquarters: {ex.Message}"); hqKey = ""; ownerPid = ""; return false; }
        }

        /// <summary>P7: EVERY headquarters this company holds on this machine - my own plus every flipped
        /// partner one.  The union counters on the company card are summed over exactly these.</summary>
        public static List<Address> CompanyHqAddresses()
        {
            var list = new List<Address>();
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null || gi.BuildingRegistrations == null) return list;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null || reg.businessTypeName != "ba:businesstype_headquarters") continue;
                    bool rented; try { rented = reg.RentedByPlayer; } catch { continue; }
                    if (!rented || reg.Address == null) continue;
                    if (!MergerFlip.TrulyMine(reg) && !MergerFlip.IsFlipped(GameStateReader.AddressKey(reg))) continue;
                    list.Add(reg.Address);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyPlans] company headquarters: {ex.Message}"); }
            return list;
        }

        /// <summary>P7: the five card counters, summed over the whole company with the SAME native helper the
        /// card itself uses (HeadquartersList.SetUpEmployeeCounter :69-83) - one query per headquarters,
        /// same query info, so nothing here re-implements what counts as an employee.</summary>
        public static int UnionCounterFor(string skill)
        {
            int n = 0;
            try
            {
                foreach (var a in CompanyHqAddresses())
                    n += EmployeeHelper.GetEmployeeInstances(new EmployeeInstancesQueryInfo
                    {
                        withAssignedAddress = a,
                        withSkills = new string[1] { skill },
                        excludeBeingReplaced = true,
                    }).Count;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyPlans] union counter '{skill}': {ex.Message}"); }
            return n;
        }

        /// <summary>P7: the registration whose card the hub is building RIGHT NOW - recorded by the hub's
        /// entry prefix so the counter postfix, which cannot name an Address of its own, knows whose card it
        /// is filling in.  Set and read on the main thread inside one SetUpEntry call.</summary>
        private static string _cardBeingDrawn = "";
        public static void NoteHqCardDrawn(string hqKey) { _cardBeingDrawn = hqKey ?? ""; }
        public static bool DrawingCompanyCard()
        {
            try { return BackingHq(out var k, out _) && k.Length > 0 && string.Equals(k, _cardBeingDrawn, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>P7: should the hub SKIP this headquarters registration?  True only for a FLIPPED partner
        /// headquarters that is not the backing one.  An own headquarters is never hidden, a non-member and
        /// an off-session machine hide nothing, and when no backing headquarters can be named nothing is
        /// hidden either.</summary>
        public static bool HideExtraHqCard(BuildingRegistration reg)
        {
            try
            {
                if (reg == null || !MergerSync.IAmMember) return false;
                if (MergerFlip.TrulyMine(reg)) return false;
                string k = GameStateReader.AddressKey(reg);
                if (string.IsNullOrEmpty(k) || !MergerFlip.IsFlipped(k)) return false;
                if (!BackingHq(out var backing, out _) || backing.Length == 0) return false;
                if (string.Equals(k, backing, StringComparison.OrdinalIgnoreCase)) return false;
                _hiddenThisPass++;
                return true;
            }
            catch { return false; }
        }

        private static int _hiddenThisPass;
        private static string _hqCardLogged = "";

        /// <summary>P7: the hub finished a pass.  The card state is LOGGED ONCE and again whenever it
        /// changes - a member joining or leaving, a headquarters bought or sold, all of which change either
        /// the flip table or the member order and therefore this string.  There is no timer and no sweep:
        /// the hub redraws the list itself whenever any of that happens.</summary>
        public static void HqCardPassDone()
        {
            try
            {
                if (!MergerSync.IAmMember) { _hqCardLogged = ""; _hiddenThisPass = 0; return; }
                BackingHq(out var k, out var pid);
                string state = $"{k}|{pid}|{_hiddenThisPass}";
                if (state != _hqCardLogged)
                {
                    _hqCardLogged = state;
                    Plugin.Logger.LogInfo($"[CompanyPlans] company headquarters card backed by '{k}' ({pid}); {_hiddenThisPass} partner card(s) hidden.");
                }
                _hiddenThisPass = 0;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyPlans] headquarters card pass: {ex.Message}"); _hiddenThisPass = 0; }
        }

        public static void HqCardPassBegin() { _hiddenThisPass = 0; _cardBeingDrawn = ""; }

        /// <summary>P8: `hqcards` - what the hub would draw, off the SAME BackingHq and the same registration
        /// filter, so the rig reads the rule and not a copy of it.</summary>
        public static string HqCardsLine()
        {
            try
            {
                bool has = BackingHq(out var k, out var pid);
                int visible = 0;
                var gi = SaveGameManager.Current;
                if (gi != null && gi.BuildingRegistrations != null)
                    foreach (var reg in gi.BuildingRegistrations)
                    {
                        if (reg == null || reg.businessTypeName != "ba:businesstype_headquarters") continue;
                        bool rented; try { rented = reg.RentedByPlayer; } catch { continue; }
                        if (!rented) continue;
                        if (GameStatePatcher.HideFromOwnAssetLists(reg)) continue;   // the hub's own prefix, first test
                        if (HideExtraHqCard(reg)) continue;                          // P7, second test
                        visible++;
                    }
                _hiddenThisPass = 0;   // the lever is a READ: it must not disturb the card pass counter
                return $"OK hqcards visible={visible} backing={(has ? k : "-")} owner={(has ? pid : "-")}";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
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
        public static int ShowPartnerRows(string family, Transform rowParent, Action<object> setUp,
                                          List<KeyValuePair<string, Transform>> nativeRows = null)
        {
            _drawn[family] = 0;                                  // U7: the record of this family starts empty every draw
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
                {
                    // CROSS-HR-1 S4: where this owner's HR plans are INSTALLED as SHADOWS, the shadow IS the
                    // row.  On the shadow's OWN headquarters page the native builder already drew it
                    // (HrManagersPlanList.RefreshManagersList :82-90 draws GetAssignedPlansForHeadquarters),
                    // so the overlay adds it nowhere there or every row would appear twice - those rows are
                    // ADOPTED instead (strip, tint, count: AdoptNativeShadowRows below, K3); on every OTHER
                    // company headquarters page the native page filter hides it - exactly as it hid
                    // logistics - so the union draws the INSTALLED object here, never the copy of it.
                    // CROSS-HR-1b K4: the skip is per PLAN, not per (owner, headquarters) PAIR.  Where one
                    // plan of a shadowed pair failed to install there is no shadow of it for native to draw
                    // and the pair-wide skip drew it nowhere at all; its detached copy is drawn here.
                    var shadows = ShadowPlansOf(owner, hq, family);
                    List<object> toDraw;
                    if (shadows == null) toDraw = RowsFor(owner, hq, family);
                    else
                    {
                        var installed = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var s in shadows) { string sid = PlanIdOf(s); if (sid.Length > 0) installed.Add(sid); }
                        toDraw = Same(hq, pageHq) ? new List<object>() : new List<object>(shadows);
                        foreach (var copy in RowsFor(owner, hq, family))
                            if (!installed.Contains(PlanIdOf(copy))) toDraw.Add(copy);
                    }
                    foreach (var row in toDraw)
                    {
                        int before = rowParent != null ? rowParent.childCount : 0;
                        try { setUp(row); shown++; }
                        catch (Exception ex)
                        {
                            refused++;
                            if (_refusalLogged.Add(family + "|" + owner))
                                Plugin.Logger.LogWarning($"[Plans] a {family} row of '{owner}' could not be drawn: {ex.Message} (once per family and owner).");
                        }
                        // HQ-UNION-2 review (carried, CROSS-HR-1 S5): the builder may have ACTIVATED children
                        // before it threw, and ReorderableList.InitializeItems enrols every active child that
                        // still has a ReorderableListItem next frame - so the strip runs on whatever was
                        // added, thrown or not.  It used to sit behind the catch's `continue`.
                        if (rowParent == null) continue;
                        for (int i = before; i < rowParent.childCount; i++)
                        {
                            StripDragHandle(rowParent.GetChild(i));
                            if (tinted) TintRow(rowParent.GetChild(i), tint);
                        }
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
                }
                // S5, as above: strip whatever the builder added before it threw.
                if (rowParent == null) continue;
                for (int i = before; i < rowParent.childCount; i++) StripDragHandle(rowParent.GetChild(i));
            }
            int adopted = AdoptNativeShadowRows(family, nativeRows);   // K3: the rows the game drew itself
            shown += adopted;
            _drawn[family] = shown;                              // U7: what this family actually drew
            if (shown > 0 || own > 0)
                Plugin.Logger.LogInfo($"[Plans] union: {shown} partner row(s) + {own} own other-HQ row(s) ({family}, page {pageHq})"
                    + (adopted > 0 ? $" - {adopted} of them drawn by the game's own list as shadows and adopted here" : ""));
            if (refused > 0) Plugin.Logger.LogInfo($"[Plans] {refused} {family} row(s) not drawn on '{pageHq}'.");
            return shown + own;
        }

        /// <summary>U4 (HQ-UNION-2): the LOGISTICS half of the union.  This family is not drawn from the
        /// screen-layer registry like the other four - a partner's plans are INSTALLED in the real
        /// gi.logisticsManagerPlans as TAGGED DISPLAY COPIES (CompanyLists.Apply -> MergerAbsence
        /// .InstallListsForDisplay under DisplayOwnerTag "display:&lt;pid&gt;", MergerAbsence.cs:479), so the
        /// rows already exist on this machine; what hides them is the NATIVE PAGE FILTER
        /// (LogisticsManagersPlanList.GetFilteredPlans :107-121 = LogisticsManagerHelper
        /// .GetAssignedPlansForHeadquarters(the page's address) narrowed to the tab's isFactory flag).
        /// This lifts that filter for the current tab: every plan whose headquarters is NOT the page's,
        /// either a display copy (a PARTNER's row) or one of MY OWN other headquarters' real plans
        /// (OwnOtherHqPlans, which excludes display copies and demands MergerFlip.TrulyMine, so its rows
        /// are the game's own objects and edit natively).  No tint is applied here: a logistics row is
        /// already painted in its owner's colour by Patch_LogisticsPlanEntry_PartnerTint, the postfix on
        /// the entry's own Initialize, which resolves the owner from the plan's HEADQUARTERS
        /// (CompanyLists.TryOwnerOfAddress) - the tag itself only says "display:&lt;pid&gt;" and
        /// IsDisplayInstall answers yes/no, it does not hand the pid back.  Neither kind is draggable:
        /// OnPlanReordered (:75-86) indexes GetFilteredPlans, which holds none of these rows.</summary>
        public static int ShowLogisticsUnionRows(bool factory, Transform rowParent, Action<object> setUp)
        {
            _drawn["logistics"] = 0;
            if (setUp == null) return 0;
            if (!CompanyHqPageOpen(out var pageHq, out _)) return 0;
            int shown = 0, own = 0, refused = 0;
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null || gi.logisticsManagerPlans == null) return 0;
                var partner = new List<object>();
                foreach (var p in gi.logisticsManagerPlans)
                {
                    if (p == null || p.isFactory != factory) continue;
                    bool display; try { display = MergerAbsence.IsDisplayInstall(p); } catch { display = false; }
                    if (!display) continue;                                  // my own rows come from OwnOtherHqPlans
                    var hq = HqOfPlan(p);
                    if (hq == null) continue;
                    string key = KeyOf(hq);
                    if (key.Length == 0 || Same(key, pageHq)) continue;      // the page's own copies are the native list's
                    partner.Add(p);
                }
                foreach (var row in partner) { if (DrawUnionRow(row, rowParent, setUp, "logistics", "partner")) shown++; else refused++; }
                foreach (var o in OwnOtherHqPlans("logistics", pageHq))
                {
                    var lg = o as Buildings.Office.Headquarters.LogisticsManagerPlan;
                    if (lg == null || lg.isFactory != factory) continue;     // one tab at a time, exactly as native filters
                    if (DrawUnionRow(o, rowParent, setUp, "logistics", "own")) own++; else refused++;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] logistics union: {ex.Message}"); }
            _drawn["logistics"] = shown;
            if (shown > 0 || own > 0)
                Plugin.Logger.LogInfo($"[Plans] union: {shown} partner row(s) + {own} own other-HQ row(s) (logistics, page {pageHq})");
            if (refused > 0) Plugin.Logger.LogInfo($"[Plans] {refused} logistics row(s) not drawn on '{pageHq}'.");
            return shown + own;
        }

        /// <summary>U4: hand one row to the list's OWN builder and take the drag handle off whatever that
        /// builder just added.  False = the builder threw and nothing was drawn (logged once per kind).</summary>
        private static bool DrawUnionRow(object row, Transform rowParent, Action<object> setUp, string family, string kind)
        {
            int before = rowParent != null ? rowParent.childCount : 0;
            bool drew = true;
            try { setUp(row); }
            catch (Exception ex)
            {
                drew = false;
                if (_refusalLogged.Add(family + "|" + kind))
                    Plugin.Logger.LogWarning($"[Plans] a {family} {kind} row could not be drawn: {ex.Message} (once per family and kind).");
            }
            // HQ-UNION-2 review (carried, CROSS-HR-1 S5): the strip runs on whatever the builder added even
            // when it threw - a half-built row that kept its ReorderableListItem is enrolled next frame and
            // a drag on it would index the native list, which holds none of these rows.  It used to return
            // from the catch, above the strip.
            if (rowParent != null)
                for (int i = before; i < rowParent.childCount; i++) StripDragHandle(rowParent.GetChild(i));
            return drew;
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
        /// headhunterPlans, and since U4 logisticsManagerPlans), NOT display copies: they are never put in _rows/_rowOwner/_rowInfo, so
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
                    case "logistics":   src = gi.logisticsManagerPlans; break;   // U4: the caller filters the tab
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

        /// <summary>The headquarters a REAL plan of any of the five families names (public field on all five:
        /// HrManagerPlan.cs:16, PricingManagerPlan.cs:21, HeadhunterPlan.cs:23, ImportPartnership.cs:39 and
        /// LogisticsManagerPlan.cs:36).</summary>
        private static Address HqOfPlan(object plan)
        {
            var pm = plan as PricingManagerPlan; if (pm != null) return pm.headquartersAddress;
            var ip = plan as ImportPartnership;  if (ip != null) return ip.headquartersAddress;
            var hr = plan as HrManagerPlan;      if (hr != null) return hr.headquartersAddress;
            var hh = plan as HeadhunterPlan;     if (hh != null) return hh.headquartersAddress;
            var lg = plan as Buildings.Office.Headquarters.LogisticsManagerPlan; if (lg != null) return lg.headquartersAddress;   // U4
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
            // HQ-PARITY-1 P2.  The pane's product table is built ONE MODEL PER cachedSuggestion
            // (PricingManagerProductsScrollerController.LoadPlan :70-79), so an empty list is an empty
            // table - and the routed manualprice / suggestedprice ops then have no row to be clicked on.
            if (pl.cachedSuggestions != null)
            {
                pl.cachedSuggestions.Clear();
                foreach (var s in d.CachedSuggestions ?? new List<PwPriceSuggestion>())
                {
                    if (s == null || string.IsNullOrEmpty(s.ItemName)) continue;
                    var sg = new PriceSuggestion
                    {
                        itemName = s.ItemName, suggestedMin = s.SuggestedMin, suggestedMax = s.SuggestedMax,
                        rivalReferencePrice = s.RivalReferencePrice, isPlayerSelling = s.IsPlayerSelling,
                        sellingBusinessTypes = new HashSet<string>(s.SellingBusinessTypes ?? new List<string>(), StringComparer.Ordinal),
                    };
                    pl.cachedSuggestions.Add(sg);
                }
            }
            // The change-neighbourhood confirmation is a COUNT test and nothing else (PricingManagerPlanUI
            // :100 `if (_currentPlan.originalStorePrices.Count > 0)`), so the display copy carries that many
            // EMPTY entries: no entry of it is ever read on this machine.
            if (pl.originalStorePrices != null)
            {
                pl.originalStorePrices.Clear();
                for (int i = 0; i < d.OriginalStorePriceCount; i++) pl.originalStorePrices.Add(new OriginalStorePrice());
            }
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
            // HQ-PARITY-1 P1.  WITHOUT the product lines the native LoadProducts
            // (PurchasingAgentProductsScrollerController.cs:14-35) calls AddMissingImportProducts()
            // (ImportPartnership.cs:400-406), which mints a zero-amount, NULL-warehouse row per item: target
            // 0, warehouse "Unassigned", stock 0 (PurchasingAgentProductModel.UpdateWarehouse :66 answers 0
            // for a null warehouse) and a $0 next-delivery total (ImportPartnership.cs:71).  The owner's
            // lines are on the wire already (PwImportPartnership.Products), so the display copy carries them
            // - warehouse included, resolved through AddrOf, the ONE key->Address resolver this file uses.
            if (ip.products != null)
            {
                ip.products.Clear();
                foreach (var ln in d.Products ?? new List<PwItemOrderLine>())
                {
                    if (ln == null || string.IsNullOrEmpty(ln.ItemName)) continue;
                    ip.products.Add(new ImportProduct
                    {
                        itemName = ln.ItemName,
                        amount = ln.Amount,
                        amountOrderedLastWeek = ln.AmountOrderedLastWeek,
                        amountOrderedThisWeek = ln.AmountOrderedThisWeek,
                        // A partner's warehouse IS resolvable here: the flip makes its registration
                        // RentedByPlayer (MergerFlip.cs:138/:277), which is what the cell's warehouse
                        // dropdown lists (PurchasingAgentProductCellView.cs:19) and matches its selection
                        // against (:209-211).  An unresolvable key leaves null, exactly as no warehouse does.
                        assignedWarehouse = AddrOf(ln.AssignedWarehouseKey),
                    });
                }
            }
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
            // CROSS-HR-1 S1: the agreement travels with the plan, so the DISPLAY copy shows the same
            // insurance the owner sees (and HasActiveHealthInsurance answers the same, HrManagerPlan.cs:197-207).
            if (d.HealthInsurancePlanType >= 0)
                pl.healthInsurancePlan = new HealthInsurancePlan
                {
                    planType = (Entities.HealthInsurancePlanType)d.HealthInsurancePlanType,
                    pricePerDayAndEmployee = d.PricePerDayAndEmployee,
                };
            // HQ-PARITY-1 P4.  HrManagerPlan.EmployeeInstances (decompile HrManagerPlan.cs:38) maps every
            // assigned id through EmployeeHelper.GetEmployeeById and does NOT null-filter, so an id that was
            // never injected onto this machine throws inside HrManagerPlanUI.SetUpBasicData :137
            // (employees.Average(...)) - and SwallowPaneOpen turns that into a pane that silently refuses to
            // open.  The display copy therefore lists only the people this machine can actually resolve.
            if (pl.assignedEmployees != null)
            {
                pl.assignedEmployees.Clear();
                int missing = 0;
                foreach (var s in d.AssignedEmployees ?? new List<string>())
                {
                    if (string.IsNullOrEmpty(s)) continue;
                    if (!ResolvesHere(s)) { missing++; continue; }
                    pl.assignedEmployees.Add(s);
                }
                if (missing > 0)
                    Plugin.Logger.LogInfo($"[CompanyPlans] hr plan '{d.Id}': {missing} assignee(s) not present here - row shown without them.");
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
            // c4: nextRecruit is NOT carried.  No screen the display path can reach reads it, so the three
            // timer fields were dead on the wire; the copy leaves it null, as a never-recruited plan has it.
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

        /// <summary>HQ-PARITY-1 P4: does this employee id name a record THIS machine can resolve?  The one
        /// question every "drop what is not here" filter asks; `showError: false` because a miss is the
        /// normal case for a partner's staff and must not raise the game's own error toast.</summary>
        private static bool ResolvesHere(string employeeId)
        {
            try { return !string.IsNullOrEmpty(employeeId) && EmployeeHelper.GetEmployeeById(employeeId, showError: false) != null; }
            catch { return false; }
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

        /// <summary>U7 (HQ-UNION-2): how many PARTNER rows this family actually DREW on the page in its last
        /// draw.  It replaces HasOverlayRows, which read the row CACHE and answered a different question:
        /// _rowOwner is emptied only on suspend, a stale rebuild or Clear, so a partner headquarters that
        /// stopped resolving left it full (SwallowPaneOpen would swallow a pane throw with no partner row on
        /// screen) and a PRICING-only partner armed the HR assign list's fail-closed branch.  Set by the two
        /// union drivers per family (ShowPartnerRows, ShowLogisticsUnionRows) and zeroed at the start of
        /// every draw.  CROSS-HR-1 S5, exact: ClearAll zeroes it only when it actually clears (its early
        /// return, taken when nothing is held, leaves it) and ClearOwner never touches it - so a count can
        /// outlive the feed that produced it until that family draws again, which is why every reader asks
        /// only about the family whose draw it is inside.</summary>
        public static int PartnerRowsDrawn(string family)
            => family != null && _drawn.TryGetValue(family, out var n) ? n : 0;

        /// <summary>U7: family -> the partner rows that family drew in its last draw.</summary>
        private static readonly Dictionary<string, int> _drawn = new Dictionary<string, int>(StringComparer.Ordinal);

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
            void Take(string fam, string id, string dtoName, string detail = "")
            {
                if (string.IsNullOrEmpty(id)) return;
                if (family.Length > 0 && !string.Equals(fam, family, StringComparison.Ordinal)) return;
                string name = null;
                if (_rows.TryGetValue(ownerPid + "|" + fam + "|" + id, out var row) && row != null) name = RowName(row, fam);
                if (string.IsNullOrEmpty(name)) name = dtoName;
                parts.Add($"{fam}:{id}:{RowFieldSafe(name)}{detail}");
            }
            foreach (var d in p.PricingManagerPlans ?? new List<PwPricingPlan>())
                // HQ-PARITY-1 P8: the suggestion count IS the pricing pane's product table
                // (PricingManagerProductsScrollerController.LoadPlan :70-79), so a zero here is the empty table.
                Take("pricing", d?.Id, EmpName(d?.AssignedEmployeeId ?? ""), $"#suggestions={d?.CachedSuggestions?.Count ?? 0}");
            foreach (var d in p.ImportPartnerships ?? new List<PwImportPartnership>())
            {
                string who = EmpName(d?.EmployeeInstanceId ?? "");
                Take("purchasing", d?.Id, who.Length > 0 ? who : (d?.ImportAddressKey ?? ""), ProductLines(d));
            }
            foreach (var d in p.HrManagerPlans ?? new List<PwHrPlan>()) Take("hr", d?.Id, EmpName(d?.AssignedEmployeeId ?? ""));
            foreach (var d in p.HeadhunterPlans ?? new List<PwHeadhunterPlan>()) Take("headhunter", d?.Id, EmpName(d?.AssignedEmployeeId ?? ""));
            return $"OK plans '{ownerPid}' rows=[{string.Join(",", parts)}]";
        }

        /// <summary>P8: one purchasing plan's product lines as the OWNER published them, plus the stock this
        /// machine can see in the named warehouse - the four values a partner row was drawing empty before
        /// P1 (target 0, "Unassigned", 0 stock).  The row format's three reserved characters are stripped
        /// from every field, so a warehouse key cannot split a row.</summary>
        private static string ProductLines(PwImportPartnership d)
        {
            try
            {
                var lines = d?.Products;
                if (lines == null || lines.Count == 0) return "#lines=[]";
                var sb = new System.Text.StringBuilder("#lines=[");
                for (int i = 0; i < lines.Count; i++)
                {
                    var ln = lines[i];
                    if (ln == null) continue;
                    var wh = AddrOf(ln.AssignedWarehouseKey);
                    int stock = 0;
                    if (wh != null) { try { stock = BuildingHelper.CountResourcesInPallets(wh, ln.ItemName); } catch { stock = 0; } }
                    if (sb.Length > 8) sb.Append(';');
                    sb.Append($"item={RowFieldSafe(ln.ItemName)} amount={ln.Amount} ")
                      .Append($"wh={(wh == null ? "none" : RowFieldSafe(ln.AssignedWarehouseKey))} stock={stock}");
                }
                return sb.Append(']').ToString();
            }
            catch { return "#lines=[]"; }
        }

        /// <summary>P8, THE OWNER-SIDE LEVER: `hqtarget <planId> <item> <n>` changes a target on one of
        /// THIS machine's OWN purchasing plans, exactly where the pane's own ChangeTarget lands
        /// (PurchasingAgentProductCellView.cs:234-240 -> UpdateAmount, which writes ImportProduct.amount -
        /// the rest of that body is label refresh).  It exists because every other plan lever routes to
        /// somebody ELSE's machine, and P5's immediacy is about the owner's own edit.</summary>
        public static string TestDriveHqTarget(string arg)
        {
            try
            {
                var a = (arg ?? "").Trim().Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
                if (a.Length < 3 || !int.TryParse(a[2], out var want)) return "ERR usage: hqtarget <planId> <itemName> <amount>";
                var gi = SaveGameManager.Current;
                if (gi == null || gi.importPartnerships == null) return "ERR no game instance here";
                foreach (var ip in gi.importPartnerships)
                {
                    if (ip == null || ip.id != a[0]) continue;
                    if (CompanyLists.IsDisplayPlan(ip)) return $"ERR '{a[0]}' is a partner's display copy here - use `planedit purchasing {a[0]} target ...`";
                    if (ip.products != null)
                        foreach (var pr in ip.products)
                            if (pr != null && (pr.itemName == a[1] || RowFieldSafe(pr.itemName) == a[1]))   // fold b: the rig
                            {                                                                                   // captures the row-safe spelling
                                pr.amount = want;
                                SaveGameManager.MarkChange();
                                OwnEditCommitted("hqtarget lever");
                                return $"OK hqtarget {a[0]} {a[1]} amount={want} (own plan; published at the urgent cadence)";
                            }
                    return $"ERR item '{a[1]}' is not on partnership '{a[0]}' here";
                }
                return $"ERR no purchasing plan '{a[0]}' of my own here";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
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

        /// <summary>CROSS-HR-1 S4: owner -> that owner's SHADOW plan objects (the real elements of
        /// gi.hrManagerPlans that CompanyLists installed under "display:&lt;pid&gt;").  They are registered in
        /// _rowOwner/_rowInfo so IsOverlayPlan is true for them and EVERY HR pane prefix routes the edit
        /// unchanged - the smaller of the two changes the brief offered, because it needs no per-prefix
        /// predicate.  They are deliberately NOT put in _rows: that cache is what the overlay DRAWS and a
        /// shadow is drawn by the game's own list.  This list is what lets the drop be exact.</summary>
        private static readonly Dictionary<string, List<object>> _shadowRows = new(StringComparer.Ordinal);

        /// <summary>CROSS-HR-1 S4.  Replace-on-each-feed, exactly like the install itself: called from
        /// CompanyLists.Apply immediately after InstallListsForDisplay and before the registry's redraw.</summary>
        public static void RegisterShadowRows(string ownerPid)
        {
            if (string.IsNullOrEmpty(ownerPid)) return;
            DropShadowRows(ownerPid);
            var held = new List<object>();
            try
            {
                string tag = MergerAbsence.DisplayOwnerTag(ownerPid);
                foreach (var e in MergerAbsence.InstalledListItems)
                {
                    if (e.Item == null || !string.Equals(e.List, "hrManagerPlans", StringComparison.Ordinal)
                        || !string.Equals(e.Owner, tag, StringComparison.Ordinal)) continue;
                    string id = (e.Item as HrManagerPlan)?.id ?? "";
                    if (id.Length == 0) continue;
                    _rowOwner[e.Item] = ownerPid;
                    _rowInfo[e.Item] = new RowInfo { Owner = ownerPid, Family = "hr", PlanId = id, Hq = KeyOf(HqOfPlan(e.Item)) };
                    held.Add(e.Item);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] shadow rows of '{ownerPid}': {ex.Message}"); }
            if (held.Count > 0)
            {
                _shadowRows[ownerPid] = held;
                Plugin.Logger.LogInfo($"[Plans] {held.Count} shadow HR row(s) of '{ownerPid}' route their edits.");
            }
        }

        /// <summary>CROSS-HR-1 S4: forget one owner's shadow registrations (the objects themselves are the
        /// installer's to lift - RemoveInstalledForOwner, which CompanyLists calls on the same paths).</summary>
        private static void DropShadowRows(string ownerPid)
        {
            if (string.IsNullOrEmpty(ownerPid) || !_shadowRows.TryGetValue(ownerPid, out var held)) return;
            foreach (var o in held) { if (o == null) continue; _rowOwner.Remove(o); _rowInfo.Remove(o); }
            _shadowRows.Remove(ownerPid);
        }

        /// <summary>CROSS-HR-1 S4: one owner's INSTALLED shadow plans of `family` at `hqKey`, or NULL when
        /// that pair is not shadowed here (the caller then draws the detached rows as before).  Only HR is
        /// installed as a shadow; the other three screen-layer families never are.  Read live off the
        /// installer's own record, so an install lifted between feeds is seen immediately.</summary>
        private static List<object> ShadowPlansOf(string owner, string hqKey, string family)
        {
            if (family != "hr" || string.IsNullOrEmpty(owner)) return null;
            List<object> found = null;
            try
            {
                string tag = MergerAbsence.DisplayOwnerTag(owner);
                foreach (var e in MergerAbsence.InstalledListItems)
                {
                    if (e.Item == null || !string.Equals(e.List, "hrManagerPlans", StringComparison.Ordinal)
                        || !string.Equals(e.Owner, tag, StringComparison.Ordinal)) continue;
                    if (!Same(KeyOf(HqOfPlan(e.Item)), hqKey)) continue;
                    (found ??= new List<object>()).Add(e.Item);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] shadow rows of '{owner}': {ex.Message}"); return null; }
            return found;
        }

        /// <summary>The plan id of a row object: the registry's own record first (exact for a display copy
        /// and for a registered shadow alike), then the object's own `id`.</summary>
        private static string PlanIdOf(object row)
        {
            if (row == null) return "";
            if (_rowInfo.TryGetValue(row, out var ri) && ri != null && !string.IsNullOrEmpty(ri.PlanId)) return ri.PlanId;
            try { return (row as HrManagerPlan)?.id ?? ""; } catch { return ""; }
        }

        /// <summary>CROSS-HR-1b K3: on the shadow's OWN headquarters page the NATIVE list draws it
        /// (HrManagersPlanList.RefreshManagersList :88-91 -> SetUpPlanEntry :96-105, which records every row
        /// it builds in _entriesByPlanIds).  Those rows were getting NO drag strip, NO owner tint and no
        /// place in the count, so PartnerRowsDrawn("hr") read 0 on the one page where all of that owner's HR
        /// rows are present - and the fail-closed branch and the pane-throw swallow that ask it stood down
        /// there.  The union ADOPTS them: same strip, same tint, same count, so a shadow looks and behaves
        /// like the partner row it is.  Families whose caller hands over nothing are untouched.</summary>
        private static int AdoptNativeShadowRows(string family, List<KeyValuePair<string, Transform>> nativeRows)
        {
            if (nativeRows == null) return 0;
            int n = 0;
            try
            {
                foreach (var kv in nativeRows)
                {
                    if (kv.Value == null) continue;
                    string owner = ShadowOwnerOfPlanId(kv.Key);
                    if (owner.Length == 0) continue;
                    StripDragHandle(kv.Value);
                    if (PlayerColours.TryColourFor(owner, out var tint)) TintRow(kv.Value, tint);
                    n++;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] {family} shadow rows on their own page: {ex.Message}"); }
            return n;
        }

        /// <summary>The owner of a REGISTERED shadow plan id, or "" when that id is not a shadow here.</summary>
        private static string ShadowOwnerOfPlanId(string planId)
        {
            if (string.IsNullOrEmpty(planId)) return "";
            foreach (var kv in _shadowRows)
            {
                if (kv.Value == null) continue;
                foreach (var o in kv.Value)
                    if (o != null && _rowInfo.TryGetValue(o, out var ri) && ri != null
                        && string.Equals(ri.PlanId, planId, StringComparison.Ordinal)) return kv.Key;
            }
            return "";
        }

        /// <summary>CROSS-HR-1b: does this HR plan id name a SHADOW on this machine?  Read live off the
        /// game's own list and decided by the TAG alone (MergerAbsence.IsDisplayInstall), so an install
        /// lifted between feeds is seen at once and an absence stand-in's REAL items answer false.</summary>
        public static bool IsShadowHrPlanId(string planId)
        {
            if (string.IsNullOrEmpty(planId)) return false;
            try
            {
                var list = SaveGameManager.Current?.hrManagerPlans;
                if (list == null) return false;
                foreach (var p in list)
                {
                    if (p == null || !string.Equals(p.id, planId, StringComparison.Ordinal)) continue;
                    try { return MergerAbsence.IsDisplayInstall(p); } catch { return false; }
                }
            }
            catch { }
            return false;
        }

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
                if (!IsOverlayPlan(plan)) { OwnEditCommitted("pane edit"); return false; }   // my own plan: nothing changes here, but the company is watching
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

        /// <summary>HQ-PARITY-1 P5, THE ONE DECISION POINT for "the owner just changed something on a
        /// headquarters plan of their own".  Every such commit used to reach the mod and then do nothing:
        /// the bundle was marked dirty only by Order.Pay (PaperworkSync.cs:847) and GameManager.NewDay
        /// (:862), so a co-member saw the change up to thirty seconds later - or not until the next day.
        /// SaveGameManager.MarkChange is deliberately NOT patched (it fires on everything); the four
        /// families' own-plan returns and the logistics plan-load postfix call HERE instead, and the
        /// publish then rides the existing 2 s urgent cadence (:38) into MPServer.StorePaperwork, whose
        /// FanOutCompanyLists (MPServer.cs:619-620) is the co-members' redraw.  Off a merger this costs a
        /// single bool test.</summary>
        public static void OwnEditCommitted(string why)
        {
            try { if (MergerSync.IAmMember) PaperworkSync.MarkUrgent(); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyPlans] own edit '{why}': {ex.Message}"); }
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
                // HQ-PARITY-1 P5a: `end` and `urgent` on a purchasing partnership are the two pane commits
                // that reach the routing layer HERE and not through RoutePaneEdit, so an OWN plan's confirm
                // would otherwise publish at the 30 s cadence.  Same one hook.
                if (!IsOverlayPlan(plan) || !_rowInfo.ContainsKey(plan)) { OwnEditCommitted("confirmed pane edit"); return false; }
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
                // HQ-PARITY-1 P5a: 'Add plan' on MY OWN headquarters is the third own-plan commit that does
                // not pass through RoutePaneEdit.
                if (!PartnerHqOpen(out var hq, out var owner)) { OwnEditCommitted("plan created"); return false; }
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
        /// partner's page"; only when NO partner row is drawn is a throw the game's own and re-thrown.  U7:
        /// "drawn" is now THIS family's last draw (PartnerRowsDrawn), not the family-agnostic row cache.</summary>
        public static Exception SwallowPaneOpen(string family, Exception ex)
        {
            try
            {
                if (ex == null) return null;
                if (!CompanyHqPageOpen(out var hq, out var owner) || PartnerRowsDrawn(family) == 0) return ex;   // no partner row of THIS family here: the game's own bug
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

        /// <summary>CROSS-HR-3 A2: an injected copy of a CO-MEMBER's person - the one kind of foreign id an HR
        /// plan may now carry.  An injected copy of somebody who is merely a business GRANTEE is not a company
        /// member and stays refused, exactly as r2 MAJOR-3 refused every copy.</summary>
        internal static bool CoMemberCopyHere(string eid)
        {
            try { return !string.IsNullOrEmpty(eid) && MPRegisterSync.IsInjectedStaff(eid) && MPRegisterSync.IsInjectedFromMergedPartner(eid); }
            catch { return false; }
        }

        /// <summary>CROSS-HR-3 A2: ONE tag leg for a co-member's person on this machine's HR plan.  The write
        /// itself belongs on the machine that holds the REAL record, so the leg travels the employee-edit
        /// carrier the training legs use (Action "hrtag", routed by MPServer.HostRouteHrTrain: the registry
        /// names the record's owner, members only, never back to the sender, and RouteTargetFor sends it to a
        /// stand-in when that owner is away).  An empty plan id CLEARS.  The owner writes a VALUE, not a delta,
        /// so the same leg twice is the same tag - no stamp needed.</summary>
        private static void SendHrTag(string eid, string planId, bool clear, string why)
        {
            try
            {
                EmployeeInstance? e = null; try { e = EmployeeHelper.GetEmployeeById(eid); } catch { }
                string addr = ""; try { if (e != null) addr = GameStateReader.AddressKey(e.assignedAddress) ?? ""; } catch { }
                string owner = ""; try { owner = MPRegisterSync.OwnerOfInjected(eid) ?? ""; } catch { }
                MergerEmployeeSync.SendHrTag(new EmployeeEditPayload
                {
                    PlayerId   = MPConfig.PlayerId,
                    Action     = "hrtag",
                    AddressKey = addr,
                    EmployeeId = eid ?? "",
                    OwnerPid   = owner,
                    AssignedHrManagerPlanId = clear ? "" : (planId ?? ""),
                });
                Plugin.Logger.LogInfo($"[CrossHR] hr tag {(clear ? "CLEAR" : "SET")} leg for employee '{eid}' (plan '{planId}', {why}) -> owner "
                                    + $"'{(owner.Length > 0 ? owner : "?")}' @ '{(addr.Length > 0 ? addr : "-")}'. The copy here keeps the tag the plan gave it; a refusal is answered and undone here.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CrossHR] hr tag leg for employee '{eid}' (plan '{planId}'): {ex.Message}"); }
        }

        /// <summary>CROSS-HR-3b B1: MY OWN HR plan's pane (or the `planown` rig lever) has just run NATIVE's own
        /// pair on a CO-MEMBER's copy - the list changed here and the COPY took the tag, but nothing left this
        /// machine, so the owner's REAL record never heard of it.  This is the leg that path was missing.  The
        /// copy's tag is whatever native just wrote and it stays: it matches the list.  Native's Fill and
        /// Clear-all loop SetEmployeeAssigned, so a Clear-all sends one CLEAR per co-member copy.</summary>
        internal static void OwnPlanAssignedLocally(HrManagerPlan plan, string eid, bool assigned)
        {
            try
            {
                if (plan == null || string.IsNullOrEmpty(eid) || !CoMemberCopyHere(eid)) return;
                if (assigned)
                {
                    // Native adds to the list before this runs; no entry means the click did not stand.
                    if (plan.assignedEmployees == null || !plan.assignedEmployees.Contains(eid)) return;
                    SendHrTag(eid, plan.id, false, "joined this HR plan");
                }
                else SendHrTag(eid, plan.id, true, "left this HR plan");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CrossHR] own-plan hr assign of '{eid}': {ex.Message}"); }
        }

        /// <summary>CROSS-HR-3 A3: plan ids whose delete has ALREADY fanned its tag clears out.  The routed
        /// applier below fans out and then calls HrManagerHelper.DeletePlan, which is native's own
        /// HrManagerPlan.Delete (decompile HrManagerHelper.cs:35-38) - the very method the pane's delete button
        /// reaches and the one the A3 prefix hangs on.  The id sits here for the length of that call so the
        /// clears go out once.</summary>
        private static readonly HashSet<string> _hrDeleteFanOut = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>CROSS-HR-3 A3: has this plan's delete already sent its tag clears?</summary>
        internal static bool HrDeleteFanOutDone(string planId)
        { try { return !string.IsNullOrEmpty(planId) && _hrDeleteFanOut.Contains(planId); } catch { return false; } }

        /// <summary>CROSS-HR-3 A3: clear the tag on every CO-MEMBER's worker this plan holds, on the machine
        /// that holds each real record.  Native's Delete (decompile HrManagerPlan.cs:223-228) nulls
        /// assignedHrManagerPlanId on LOCAL records only, so without this a partner's worker keeps a tag
        /// naming a plan that exists nowhere - and the shadow behind that id is gone with it.</summary>
        internal static void HrTagFanOutClear(HrManagerPlan pl, string why)
        {
            try
            {
                if (pl == null || pl.assignedEmployees == null) return;
                int sent = 0;
                foreach (var one in new List<string>(pl.assignedEmployees))
                    if (CoMemberCopyHere(one)) { SendHrTag(one, pl.id, true, why); sent++; }
                if (sent > 0)
                    Plugin.Logger.LogInfo($"[CrossHR] hr plan '{pl.id}' ({why}): {sent} co-member tag clear(s) sent before the plan goes.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CrossHR] hr tag fan-out on plan '{(pl != null ? pl.id : "?")}': {ex.Message}"); }
        }

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
                case "delete":
                    // CROSS-HR-3 A3: the tags first, while the list still says who carries them; native then
                    // clears the LOCAL records as it always has.  The prefix on HrManagerPlan.Delete sees this
                    // id in _hrDeleteFanOut and leaves the sending to us.
                    _hrDeleteFanOut.Add(id);
                    try { HrTagFanOutClear(pl, "the plan was deleted"); HrManagerHelper.DeletePlan(id); }
                    finally { _hrDeleteFanOut.Remove(id); }
                    return true;
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
                        // employee id resolves to the COPY, and writing the tag here writes it on a copy.
                        //
                        // CROSS-HR-3 A2: that is no longer a refusal for a CO-MEMBER's person.  The company may put
                        // any of its people on any of its HR plans while each stays at the shop that employs them:
                        // the id joins this plan's list, the copy takes the tag and KEEPS it (no roster push ever
                        // overwrites it - MPRegisterSync.StaffInfoOf carries id/name/gender/available/wage/
                        // satisfaction/age/skills and no plan id at all), and the one write that matters travels to
                        // the machine holding the REAL record, which sets it only if THIS plan resolves there
                        // through the shadow - and answers `hrtag-refused` when it does not, which undoes the pair
                        // here (CROSS-HR-3b B2), so a phantom entry is no longer left for nothing to reconcile.
                        // An injected copy of a NON-member is still refused, with the same wording as before.
                        if (InjectedHere(eid))
                        {
                            if (!CoMemberCopyHere(eid)) return RefuseInjected("hr", op, id, eid);
                            if (!pl.assignedEmployees.Contains(eid)) pl.assignedEmployees.Add(eid);
                            emp.assignedHrManagerPlanId = pl.id;
                            Plugin.Logger.LogInfo($"[CrossHR] hr assign on plan '{id}': employee '{eid}' is a co-member's person here - on the list, tag write sent to its owner.");
                            SendHrTag(eid, pl.id, false, "joined this HR plan");
                            return true;
                        }
                        if (!pl.assignedEmployees.Contains(eid)) pl.assignedEmployees.Add(eid);
                        emp.assignedHrManagerPlanId = pl.id;
                    }
                    else
                    {
                        pl.assignedEmployees.Remove(eid);
                        if (emp != null && emp.assignedHrManagerPlanId == pl.id) emp.assignedHrManagerPlanId = null;
                        // CROSS-HR-3 A2: an unassign of a co-member's person clears the tag where the real record is.
                        // CROSS-HR-2 T3b: when that worker LEFT its owner's save, the owner's machine routed this very
                        // unassign here - so the clear goes back for a record that is gone there.  That is correct, and
                        // the owner's applier answers it quietly.
                        if (CoMemberCopyHere(eid)) SendHrTag(eid, pl.id, true, "left this HR plan");
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
                    int cleared = 0, tagsSent = 0;
                    foreach (var one in new List<string>(pl.assignedEmployees))
                    {
                        pl.assignedEmployees.Remove(one);
                        EmployeeInstance e = null; try { e = EmployeeHelper.GetEmployeeById(one); } catch { }
                        if (e != null && e.assignedHrManagerPlanId == pl.id) e.assignedHrManagerPlanId = null;
                        // CROSS-HR-3 A2: "unassign all" clears a co-member's worker where its real record lives too.
                        if (CoMemberCopyHere(one)) { SendHrTag(one, pl.id, true, "the plan's list was cleared"); tagsSent++; }
                        cleared++;
                    }
                    Plugin.Logger.LogInfo($"[Merger] hr clear on plan '{id}': {cleared} assignment(s) released, {tagsSent} co-member tag clear(s) sent.");
                    return true;
                }
                case "fill":
                {
                    // HO-1a H1: HrManagerPlanUI.Fill (decompile :224-240) takes the first (MaxEmployees -
                    // NumberOfAssignedEmployees) unassigned people off its own assignable list, again one
                    // SetEmployeeAssigned per person.  One leg now, IntValue carrying the free count the sender
                    // saw; the runner fills from its own roster, capped by its own plan's real free count.
                    //
                    // FILL-PREFER (user ruling 2026-09-12): the H3 rule that a fill took THIS save's own people
                    // only is RETIRED here.  A fill takes the plan owner's OWN eligible people FIRST, in the
                    // roster's order, and when free slots REMAIN it takes a CO-MEMBER's copies (CROSS-HR-3 A2
                    // already lets one sit on this plan) until the plan is full.  Each co-member add lists the id
                    // and tags the copy here, then sends the one write that matters to the machine holding the
                    // REAL record, exactly as `case "assign"` does.  An injected copy of a NON-member is still no
                    // pick of ours.  Off a merger nothing is injected, so the second pass finds nobody.
                    int free = pl.MaxEmployees - pl.NumberOfAssignedEmployees;
                    if (p.IntValue > 0 && p.IntValue < free) free = p.IntValue;
                    if (free <= 0) { Plugin.Logger.LogInfo($"[Merger] hr fill on plan '{id}': no free slot here."); return true; }
                    int ownAdded = 0, coAdded = 0;
                    for (int pass = 0; pass < 2 && ownAdded + coAdded < free; pass++)
                        foreach (var e in EmployeeHelper.GetEmployeeInstances())
                        {
                            if (ownAdded + coAdded >= free) break;
                            if (e == null || !string.IsNullOrEmpty(e.assignedHrManagerPlanId)) continue;   // native :233
                            if (e.id == pl.assignedEmployeeId) continue;                                   // native :233
                            bool co = CoMemberCopyHere(e.id);
                            // pass 0 = my own real records; pass 1 = a co-member's copies, for the slots left over.
                            if (pass == 0 ? InjectedHere(e.id) : !co) continue;
                            // HO-1c L4.5: the no-argument GetEmployeeInstances() is the RAW roster (decompile
                            // EmployeeHelper.cs:575-578; the mod filters the QueryInfo overload only), so the synthetic
                            // duty stand-ins are in it - and they are not IsInjectedStaff.  A stand-in must never be
                            // tagged with a plan id.
                            if (MPRegisterSync.IsSyntheticDuty(e.id)) continue;
                            if (pl.assignedEmployees.Contains(e.id)) continue;
                            pl.assignedEmployees.Add(e.id);
                            e.assignedHrManagerPlanId = pl.id;
                            if (co) { coAdded++; SendHrTag(e.id, pl.id, false, "joined this HR plan by fill"); }
                            else ownAdded++;
                        }
                    Plugin.Logger.LogInfo($"[Merger] hr fill on plan '{id}': {ownAdded} own + {coAdded} co-member of {free} free slot(s) taken.");
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
                string tail = sp > 0 ? rest.Substring(sp + 1) : "";
                if (sp > 0 && float.TryParse(tail, System.Globalization.NumberStyles.Float,
                                             System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    num = parsed; str = rest.Substring(0, sp);
                    // HQ-PARITY-1 P8, found while wiring the scenario: a WHOLE-number tail filled Estimate
                    // only, so every op that reads IntValue - `planedit purchasing <id> target <item> <n>`
                    // above all - was handed a target of 0 from the rig.  The same tail now fills both.
                    int.TryParse(tail, out iv);
                }
                // CROSS-HR-3 A5: an id AND a bool - `planedit hr <planId> assign <employeeId> true`.  The number
                // case already split a trailing value off the string; a trailing true/false was not split, so
                // bool.TryParse ran on the WHOLE rest, answered false, and the HR assign applier was handed an
                // UNASSIGN carrying "<employeeId> true" as its employee id.  Same split, for the bool tail.
                else if (sp > 0 && bool.TryParse(tail, out var parsedBool))
                { bv = parsedBool; str = rest.Substring(0, sp).Trim(); }
                else { bool.TryParse(rest, out bv); int.TryParse(rest, out iv); }
                // Fold b (HQ-PARITY-1 rig run 3): the rig captures item names off `plans ... rows`, which prints them
                // through RowFieldSafe (':' -> '_'); translate that spelling back to the REAL name off the display
                // copy this sender holds, so the runner's exact match receives what the owner's plan holds.
                if (fam == "purchasing" && row is ImportPartnership tip && tip.products != null && (op == "target" || op == "warehouse"))
                {
                    string want = str.Trim();
                    foreach (var pr in tip.products)
                        if (pr != null && pr.itemName != want && RowFieldSafe(pr.itemName) == want) { str = pr.itemName; break; }
                }
                bool routed = op == "add" ? RoutePlanCreateAt(fam, str.Trim())   // r2b: the HQ key is the argument (no pane on the rig)
                                          : RoutePaneEdit(fam, "planedit verb", row, op, str, iv, num, bv);
                return $"OK planedit {fam} {id} {op} routed={routed}";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        // -- CROSS-HR-3 A5: the `hrtag` / `hrplanof` rig levers ----------------

        /// <summary>`hrtag <employeeId> <planId|->` - sends exactly the tag leg A2 sends when a co-member's
        /// person joins this machine's HR plan, "-" (or nothing) for the CLEAR. It writes nothing here: the
        /// point is to watch the OWNER's real record take the tag, or drop it.</summary>
        internal static string TestDriveHrTag(string arg)
        {
            try
            {
                var a = (arg ?? "").Trim().Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (a.Length < 1) return "ERR usage: hrtag <employeeId> [planId|-]";
                string eid = a[0].Trim(), plan = a.Length > 1 ? a[1].Trim() : "";   // r1 MINOR-2: the plan id is
                                                                                   // optional - no id CLEARS too
                bool clear = plan.Length == 0 || plan == "-";
                if (!CoMemberCopyHere(eid))
                    return $"ERR '{eid}' is not an injected copy of a co-member's person here - nothing to route";
                SendHrTag(eid, clear ? "-" : plan, clear, "rig lever");
                string owner = ""; try { owner = MPRegisterSync.OwnerOfInjected(eid) ?? ""; } catch { }
                return $"OK hrtag employee='{eid}' plan='{(clear ? "-" : plan)}' clear={clear} owner='{(owner.Length > 0 ? owner : "?")}'";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        /// <summary>`hrplanof <employeeId>` - the record's assignedHrManagerPlanId as it stands on THIS machine
        /// and whether that id resolves here: "real" (one of my own plans), "shadow" (a partner's, installed by
        /// CROSS-HR-1 for the feed), "none" (a dangling tag - the A2 refusal exists to prevent exactly that) or
        /// "-" for no tag at all.</summary>
        internal static string TestDriveHrPlanOf(string arg)
        {
            try
            {
                string eid = (arg ?? "").Trim();
                if (eid.Length == 0) return "ERR usage: hrplanof <employeeId>";
                EmployeeInstance? e = null; try { e = EmployeeHelper.GetEmployeeById(eid); } catch { }
                if (e == null) return $"ERR no employee '{eid}' on this machine";
                string tag = e.assignedHrManagerPlanId ?? "";
                string resolves = "-";
                if (tag.Length > 0)
                {
                    HrManagerPlan? tp = null; try { tp = HrManagerHelper.GetPlanFromId(tag); } catch { }
                    bool shadow = false; try { shadow = tp != null && MergerAbsence.IsDisplayInstall(tp); } catch { }
                    resolves = tp == null ? "none" : (shadow ? "shadow" : "real");
                }
                string kind = MPRegisterSync.IsInjectedStaff(eid) ? "copy" : "real";
                string owner = ""; try { owner = MPRegisterSync.OwnerOfInjected(eid) ?? ""; } catch { }
                return $"OK hrplanof employee='{eid}' record={kind} owner='{(owner.Length > 0 ? owner : "-")}' "
                     + $"plan='{(tag.Length > 0 ? tag : "-")}' resolves={resolves}";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        /// <summary>CROSS-HR-3b B6 rig lever: `planown hr &lt;planId&gt; assign &lt;employeeId&gt; true|false` on the plan's
        /// OWN machine.  It performs native's own pair (decompile HrManagerPlanUI.cs:256-268: the plan's list,
        /// then assignedHrManagerPlanId on the record GetEmployeeById finds) and then calls the SAME helper the
        /// B1 pane postfix calls - so the own-plan tag leg is exercised without a pane.  A plan that is not a
        /// REAL plan here (a shadow or a display install) is refused: its list is a partner's to write.</summary>
        internal static string TestDrivePlanOwn(string arg)
        {
            try
            {
                var a = (arg ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (a.Length < 5 || !string.Equals(a[0], "hr", StringComparison.OrdinalIgnoreCase)
                                 || !string.Equals(a[2], "assign", StringComparison.OrdinalIgnoreCase))
                    return "ERR usage: planown hr <planId> assign <employeeId> true|false";
                string planId = a[1].Trim(), eid = a[3].Trim();
                bool want;
                if (!bool.TryParse(a[4].Trim(), out want))
                    return "ERR usage: planown hr <planId> assign <employeeId> true|false";
                HrManagerPlan? pl = null; try { pl = HrManagerHelper.GetPlanFromId(planId); } catch { }
                if (pl == null) return $"ERR no HR plan '{planId}' on this machine";
                bool display = false; try { display = MergerAbsence.IsDisplayInstall(pl) || IsOverlayPlan(pl); } catch { }   // r2 MINOR-3: a drawn partner row is not ours either
                if (display) return $"ERR HR plan '{planId}' is a partner's row here (display install or drawn row), not one of this machine's own plans";
                if (pl.assignedEmployees == null) return $"ERR HR plan '{planId}' has no assignment list here";
                if (want) { if (!pl.assignedEmployees.Contains(eid)) pl.assignedEmployees.Add(eid); }
                else pl.assignedEmployees.Remove(eid);
                EmployeeInstance? rec = null; try { rec = EmployeeHelper.GetEmployeeById(eid); } catch { }
                if (rec != null) { if (want) rec.assignedHrManagerPlanId = pl.id; else if (rec.assignedHrManagerPlanId == pl.id) rec.assignedHrManagerPlanId = null; }   // native's own pair: a clear only when the tag names this plan
                OwnPlanAssignedLocally(pl, eid, want);
                return $"OK planown hr {planId} assign {eid} {want} listed={pl.assignedEmployees.Count} coMember={CoMemberCopyHere(eid)}";
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
