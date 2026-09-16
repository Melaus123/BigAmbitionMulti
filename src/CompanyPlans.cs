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
                // FOLD b B6 (review F8): THE PER-PLAN TABLES GO WITH THE OWNER.  ClearAll wiped them, but a
                // single dissolve left every one of them holding that company's plan ids - the last
                // capacity (_lastMax) would still answer for a plan nobody publishes any more, and the
                // once-per-plan log sets would stay armed, so a company re-formed with the same ids said
                // nothing the second time round.  Collected BEFORE DropRowsOf, which is what destroys the
                // keys they are read out of.
                var ids = new List<string>();
                string pfx = ownerPid + "|";
                foreach (var kv in _rows)
                {
                    if (!kv.Key.StartsWith(pfx, StringComparison.Ordinal)) continue;
                    int cut = kv.Key.IndexOf('|', pfx.Length);
                    if (cut < 0) continue;
                    string fam = kv.Key.Substring(pfx.Length, cut - pfx.Length), pid = kv.Key.Substring(cut + 1);
                    if (pid.Length == 0) continue;
                    ids.Add(pid);
                    _lastMax.Remove(pid);
                    _loggedNothingToSend.Remove(pid);
                    _loggedOverCap.Remove(pid);
                    _refreshLogged.Remove(fam + "|" + pid);
                    _lastRefreshedShape.Remove(fam + "|" + pid);
                }
                if (ids.Count > 0)
                {
                    try { CompanyLists.ForgetPlans(ids); } catch { }
                    try { MPPatches.Patch_LogisticsReorder_DisplayRefuse.Forget(ids); } catch { }
                    Plugin.Logger.LogInfo($"[Plans] forgot {ids.Count} plan id(s) of '{ownerPid}' - the per-plan tables go with the owner.");
                }
                foreach (var f in PaneFamilies) _lastRowSetShape.Remove(ownerPid + "|" + f);   // FOLD b G1
                DropRowsOf(ownerPid);
                DropShadowRows(ownerPid);                    // CROSS-HR-1 S4: the shadows go with them
                if (had) Plugin.Logger.LogInfo($"[Plans] cleared ({why}: '{ownerPid}')");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] clear '{ownerPid}': {ex.Message}"); }
        }

        public static void ClearAll(string why)
        {
            // FOLD b H3: the "stock not substituted" line is once per plan-and-address PER MERGER, so its
            // record dies with the merger state - before the early exit, because a dissolve with nothing
            // drawn still ends the merger. The set itself lives beside the pallet-count patch that writes it.
            try { MPPatches.ClearStockMissLog(); } catch { }
            if (_byOwner.Count == 0 && _rows.Count == 0) { _suspended.Clear(); return; }
            _byOwner.Clear(); _suspended.Clear(); _rows.Clear(); _rowOwner.Clear(); _refusalLogged.Clear(); _stale.Clear(); _drawn.Clear();
            _rowInfo.Clear(); _seq.Clear(); _applied.Clear(); _shadowRows.Clear();   // CROSS-HR-1 S4
            _lastMax.Clear(); _refreshLogged.Clear(); _loggedNothingToSend.Clear();   // HQ-PARITY-3 A6/A2/B1
            _lastRefreshedShape.Clear(); _loggedOverCap.Clear();                      // FOLD b B1/B3
            _lastRowSetShape.Clear();                                                 // FOLD b G1
            HrSliderHeld = false; _sliderReleaseLogged = false;                       // FOLD d E1: no hold survives a
            // reload - but HrSliderGo STANDS: the slider object outlives the session, and the guard on it
            // is what ends a hold the pointer-up never did.
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
        /// is Receive, which CompanyLists already enqueues.
        /// FOLD b G1 (review F1): BOTH HALVES ARE NOW GATED ON WHAT A CONTROL WRITES.  The list rebuild
        /// below runs for a family only when that owner's published ROW SET for it changed (RebuildIfChanged
        /// / RowSetShapeOf), and the pane loop under it only when the open plan's own published shape changed
        /// (DtoShapeOf) - so a partner merely TRADING no longer destroys and rebuilds every row every two
        /// seconds, with the reselect firing the game's SelectPlan -&gt; LoadPlan on the pane being read.
        /// HQ-PARITY-5 A1: this loop MIRRORS OTHER OWNERS ONLY - Receive drops this machine's own pid (:85)
        /// and _byOwner never holds it, so the OWNER'S OWN tabs after a partner's routed edit are redrawn by
        /// RefreshOwnTabsAfterRoutedEdit off the apply itself, not from here.</summary>
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
                // HQ-PARITY-4 P1: the list is the one the GAME last refreshed (registered by that family's
                // own refresh postfix); the hierarchy sweep is only the fallback.
                var reselected = new HashSet<string>(StringComparer.Ordinal);
                // FOLD b G2: the plan each pane had open BEFORE its list rebuild hid it - filled by Rebuild,
                // so it names only the families that actually rebuilt on this feed.
                var openBefore = new Dictionary<string, string>(StringComparer.Ordinal);
                n += RebuildIfChanged(ownerPid, ListOf("pricing"),    "RefreshPlansList",    "pricing",    ref kept, reselected, openBefore);
                n += RebuildIfChanged(ownerPid, ListOf("purchasing"), "RefreshManagersList", "purchasing", ref kept, reselected, openBefore);
                n += RebuildIfChanged(ownerPid, ListOf("hr"),         "RefreshManagersList", "hr",         ref kept, reselected, openBefore);
                n += RebuildIfChanged(ownerPid, ListOf("headhunter"), "RefreshManagersList", "headhunter", ref kept, reselected, openBefore);
                n += RebuildIfChanged(ownerPid, ListOf("logistics"),  "RefreshManagersList", "logistics",  ref kept, reselected, openBefore);   // U4: logistics joined the union
                if (n > 0) Plugin.Logger.LogInfo($"[Plans] redrew {n} open tab(s) for '{ownerPid}' - a newer feed arrived"
                                               + (kept > 0 ? $"; {kept} open pane(s) reselected." : "."));
                // HQ-PARITY-3 B1: the list rebuild above restores the SELECTION; this restores the PANE.
                // Every family's own load re-reads every control from the plan it is handed (purchasing
                // :115-117/:164, HR :86-98, headhunter :36-39, pricing and logistics the same), so the
                // settings that "did not match" were never a drawing bug - the load simply never ran.  ONE
                // method, all five families, and the same one the runner's own pane goes through after a
                // partner's routed edit (ApplyRouted).
                _refreshLogged.Clear();
                // FOLD b B1 (review F6): a pane is reloaded ONLY when this bundle CHANGED that plan.  The
                // game's own LoadPlan is not a repaint - it re-fires the unmanaged-plan popup, re-seats the
                // training slider mid-drag, drops the scroll position and recomputes the pricing suggestions
                // - so running it on every bundle (2 s apart at the urgent cadence) was destructive on its
                // own.  The test is the OWNER'S PUBLISHED SHAPE for that plan; a plan never seen before
                // counts as changed, and the shape is recorded only where the refresh actually RUNS.
                // FOLD b G1 (review F1): the LIST rebuild above is gated the same way now, on the family's
                // whole row set - both halves of the redraw follow what a CONTROL writes, and neither one
                // moves for the counters the simulation turns on its own.
                foreach (var fam in PaneFamilies)
                {
                    string openId = OpenPanePlanId(fam);
                    // FOLD b G2 (review F2): THE HEADHUNTER PANE COMES BACK.  Every list refresh opens with
                    // planUI.Hide() (decompile HeadhuntersPlanList.RefreshManagersList:75) and the headhunter
                    // list carries NO id on its rows - only a private _selectedEntry (:32), "(Clone)" names
                    // and no Plan property - so Reselect cannot find the row, and the id read here is "" for
                    // the now-INACTIVE pane (OpenPanePlanId's activeInHierarchy guard).  The pane is reloaded
                    // directly instead, from the id captured before the invoke.  The list HIGHLIGHT is NOT
                    // restored: the list offers nothing to match a row on, and a position is not an identity.
                    // Only families the rebuild RAN for are in openBefore, so this fires only where G1 let
                    // the rebuild hide the pane.
                    if (openId.Length == 0 && fam == "headhunter"
                        && openBefore.TryGetValue(fam, out var hidden) && hidden.Length > 0)
                    {
                        ReopenHeadhunterPane(hidden);
                        _lastRefreshedShape[fam + "|" + hidden] = DtoShapeOf(fam, hidden);
                        continue;
                    }
                    if (openId.Length == 0) continue;
                    string shapeKey = fam + "|" + openId, shape = DtoShapeOf(fam, openId);
                    // HQ-PARITY-4 P4, ONE OBJECT PER PLAN, ONE LOAD PER BUNDLE.  A family the rebuild above
                    // RESELECTED has already been loaded by the list's OWN SelectPlan, with the row object
                    // that refresh just built; loading it again here would hand the pane a second object for
                    // the same plan and re-fire everything a load re-fires.  The shape is still recorded, so
                    // the change gate stays honest on the next bundle.
                    if (reselected.Contains(fam)) { _lastRefreshedShape[shapeKey] = shape; continue; }
                    if (_lastRefreshedShape.TryGetValue(shapeKey, out var seenShape) && seenShape == shape) continue;
                    RefreshOpenPaneInPlace(fam, openId);
                    _lastRefreshedShape[shapeKey] = shape;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] redraw for '{ownerPid}': {ex.Message}"); }
        }

        /// <summary>HQ-PARITY-5 A1, THE OWNER'S OWN PAGE.  A routed edit lands on the owner's REAL plan, and
        /// the game redraws only on its OWN events - so the owner, sitting on the very page the partner just
        /// changed, saw nothing.  The mirror loop above cannot help: it is keyed on _byOwner, and Receive
        /// skips this machine's own pid (:85), so the owner is never in it.  Owner-&gt;partner already worked,
        /// because the publish that follows the apply feeds the partner's rebuild.  This is the same rebuild
        /// for the owner's own tab: the family's own list refresh (with its reselect), then the one pane
        /// refresh every family goes through.  MAIN THREAD - the caller is ApplyRouted.</summary>
        private static void RefreshOwnTabsAfterRoutedEdit(string fam, string planId, string fromPid, string op)
        {
            try
            {
                if (string.IsNullOrEmpty(fam) || string.IsNullOrEmpty(planId)) return;
                if (!CompanyHqPageOpen(out _, out _))
                {
                    Plugin.Logger.LogInfo($"[Plans] own {fam} redraw after a routed {op} from '{fromPid}' (plan {planId}): page closed, nothing to draw.");
                    return;
                }
                string why = EditWindowOpen();
                // FOLD b G5 (review): THE LOGISTICS FAMILY NEVER DEFERS.  An open dropdown is itself one of
                // EditWindowOpen's reasons (:306), so a logistics edit would wait behind the very control it
                // has to correct - and a destset that SHORTENS pl.destinations while this pane still holds
                // the longer _destinationEntries lets the game's own handler index a destination that is
                // gone (decompile LogisticsManagerPlanUI.UpdateSelectedBusiness:567 indexes
                // _currentPlan.destinations[destinationIndex] with no guard).  Reloading at once closes the
                // dropdown and rebuilds the entries, which is what 750a001's bare in-place refresh did.
                if (why.Length > 0 && fam != "logistics")
                {
                    // HO-1a H5's rule, for the owner's own tab: a rebuild under a half-typed edit throws the
                    // player's own input away, so it waits on the UI state re-read in TickDeferredRedraw.
                    // FOLD b G4 (review): ONE PENDING SLOT PER FAMILY.  A single slot meant a routed hr edit
                    // arriving after a routed logistics edit, both under one open field, threw the first
                    // family's redraw away.  Last writer wins WITHIN a family - a redraw is a whole-family
                    // rebuild either way - and the line is logged once per changed (family, plan, op, from)
                    // rather than per feed.
                    if (!_pendingOwn.TryGetValue(fam, out var prev)
                        || prev.plan != planId || prev.op != op || prev.from != fromPid)
                        Plugin.Logger.LogInfo($"[Plans] own {fam} redraw after a routed {op} from '{fromPid}' (plan {planId}) DEFERRED - {why}.");
                    _pendingOwn[fam] = (planId, op, fromPid);
                    return;
                }
                int kept = 0;
                var reselected = new HashSet<string>(StringComparer.Ordinal);
                var openBefore = new Dictionary<string, string>(StringComparer.Ordinal);
                int n = Rebuild(ListOf(fam), fam == "pricing" ? "RefreshPlansList" : "RefreshManagersList",
                                fam, ref kept, reselected, openBefore);
                // FOLD b G1 (review): THE HEADHUNTER PANE COMES BACK HERE TOO - the same branch the mirror
                // loop runs (:258-271).  RefreshManagersList opens with planUI.Hide() (decompile
                // HeadhuntersPlanList.cs:75) and headhunter rows carry no id, so Reselect cannot re-seat the
                // pane and OpenPanePlanId answers "" for the now-inactive one; without this the OWNER's own
                // headhunter pane stayed shut after a partner's edit.  openBefore exists for this branch
                // alone - it is the id captured before the invoke, and only families the rebuild RAN for are
                // in it.  The pane is reloaded from that id instead of through RefreshOpenPaneInPlace, which
                // would find nothing open to refresh.  The list HIGHLIGHT is not restored: the list offers
                // nothing to match a row on.
                string reopenedId = "";
                if (OpenPanePlanId(fam).Length == 0 && fam == "headhunter"
                    && openBefore.TryGetValue(fam, out var hidden) && hidden.Length > 0)
                {
                    ReopenHeadhunterPane(hidden);
                    reopenedId = hidden;
                }
                // HQ-PARITY-4 P4, ONE OBJECT PER PLAN, ONE LOAD: a family the rebuild RESELECTED has already
                // been loaded by the list's own SelectPlan, and a second load would hand the pane a second
                // object for the one plan and re-fire everything a load re-fires.
                else if (!reselected.Contains(fam)) RefreshOpenPaneInPlace(fam, planId);
                string openId = reopenedId.Length > 0 ? reopenedId : OpenPanePlanId(fam);
                string pane = openId.Length == 0 ? "none"
                            : (string.Equals(openId, planId, StringComparison.Ordinal) ? "open" : "other");
                Plugin.Logger.LogInfo($"[Plans] own {fam} tab redrawn after a routed {op} from '{fromPid}' (plan {planId}): list={n} pane={pane}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] own {fam} redraw: {ex.Message}"); }
        }

        /// <summary>A1, per family since FOLD b G4.  The owner's OWN redraws waiting for the player to finish
        /// an edit, keyed on family; an absent family = nothing pending for it.  Last writer wins WITHIN a
        /// family (a redraw is a whole-family rebuild either way), but one family no longer throws away
        /// another's pending redraw.  The logistics family is never in here - it redraws at once.</summary>
        private static readonly Dictionary<string, (string plan, string op, string from)> _pendingOwn =
            new Dictionary<string, (string plan, string op, string from)>(StringComparer.Ordinal);

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
        /// owner is cleared only when the rebuild actually ran, so no redraw is lost to a window that reopened.
        /// HQ-PARITY-5 A1: the owner's OWN deferred redraw waits on the same re-read and is replayed here too.</summary>
        public static void TickDeferredRedraw()
        {
            try
            {
                if (_pendingRedraw.Length == 0 && _pendingOwn.Count == 0) return;
                if (UnityEngine.Time.unscaledTime < _nextDeferredCheck) return;
                _nextDeferredCheck = UnityEngine.Time.unscaledTime + 1f;
                if (!CompanyHqPageOpen(out _, out _))
                {
                    _pendingRedraw = ""; _pendingReason = "";
                    _pendingOwn.Clear();
                    return;
                }
                if (EditWindowOpen().Length > 0) return;
                if (_pendingRedraw.Length > 0)
                {
                    string owner = _pendingRedraw;
                    _pendingRedraw = "";
                    Plugin.Logger.LogInfo($"[Plans] deferred redraw for '{owner}' RUNS - the edit window closed.");
                    RefreshOpenTabsFor(owner);
                }
                // A1: the owner's own tabs are replayed AFTER the mirror redraw - a feed that arrived while
                // the window was open has then already rebuilt the partner rows, so the own rebuild is the
                // last word on those tabs.  FOLD b G4: EVERY pending family is redrawn, not just the last
                // one; the set is taken and cleared first, so a redraw that defers again (it cannot here -
                // the window is shut - but a throw must not strand the rest) starts from an empty slot.
                if (_pendingOwn.Count > 0)
                {
                    var due = new List<KeyValuePair<string, (string plan, string op, string from)>>(_pendingOwn);
                    _pendingOwn.Clear();
                    foreach (var kv in due)
                    {
                        Plugin.Logger.LogInfo($"[Plans] deferred own {kv.Key} redraw (plan {kv.Value.plan}) RUNS - the edit window closed.");
                        RefreshOwnTabsAfterRoutedEdit(kv.Key, kv.Value.plan, kv.Value.from, kv.Value.op);
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] deferred redraw: {ex.Message}"); }
        }

        /// <summary>FOLD c I2 (review H2): DROP ONE OWNER'S CHANGE GATES.  A refusal changes nothing in
        /// _byOwner, so that owner's row-set shape still equals _lastRowSetShape (no list rebuild) and every
        /// open plan's shape still equals _lastRefreshedShape (no pane load) - the refusal redraw was a no-op
        /// and the screen went on showing an edit that never happened.  Forgetting the recorded shapes is what
        /// makes the very next RefreshOpenTabsFor run for real.  Nothing is logged here: the refusal itself is
        /// already one WARNING.</summary>
        public static void InvalidateShapes(string ownerPid)
        {
            try
            {
                if (string.IsNullOrEmpty(ownerPid)) return;
                foreach (var f in PaneFamilies) _lastRowSetShape.Remove(ownerPid + "|" + f);
                string pfx = ownerPid + "|";
                foreach (var kv in _rows)                       // keys are owner|family|planId
                {
                    if (!kv.Key.StartsWith(pfx, StringComparison.Ordinal)) continue;
                    int cut = kv.Key.IndexOf('|', pfx.Length);
                    if (cut < 0) continue;
                    string fam = kv.Key.Substring(pfx.Length, cut - pfx.Length), pid = kv.Key.Substring(cut + 1);
                    if (pid.Length == 0) continue;
                    _lastRefreshedShape.Remove(fam + "|" + pid);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] invalidate shapes for '{ownerPid}': {ex.Message}"); }
        }

        /// <summary>B1: `family|planId` -> the owner's published shape of that plan AS OF THE LAST REFRESH
        /// THAT RAN.  A plan absent from here has never been refreshed on this machine and counts as
        /// changed.</summary>
        private static readonly Dictionary<string, string> _lastRefreshedShape = new(StringComparer.Ordinal);

        /// <summary>HQ-PARITY-3 B2, THE GATE NARROWED TO ONE REAL CONFLICT.  This used to be a single
        /// GLOBAL reason that deferred the redraw of EVERY family, and two of its clauses held while a player
        /// merely LOOKED: "a headhunter plan is open" was true for the whole time a headhunter plan was
        /// selected (proven in the hands-on host log, `[Plans] redraw for 'Client1' DEFERRED - a headhunter
        /// plan is open`), and the purchasing "expanded product row" test LATCHED, because the recycled
        /// scroller cell's SetData never resets `_layoutElement.minHeight` (decompile
        /// PurchasingAgentProductCellView.cs:169-179) - so once a row had been expanded and scrolled the
        /// reason held for the rest of the session and nothing anywhere ever refreshed again.  BOTH CLAUSES
        /// ARE DELETED: a rebuild now RE-LOADS the open pane in place (RefreshOpenPaneInPlace), which
        /// re-reads every control from the new copy.
        /// FOLD b B2, THE CARRY-ACROSS IS GONE AND THE GATES ARE THE GAME'S OWN STATE.  The half-typed text
        /// used to be captured by PATH and written back after the load; review F2 killed that: the purchasing
        /// pane's LoadPlan defers its product rows by a frame (`CoroutineUtility.RunAfterOneFrame(RefreshItems)`,
        /// decompile PurchasingAgentPlanUI.cs:103), so a synchronous restore was overwritten a frame later,
        /// and the path runs over RECYCLED scroller cells, so it could land on a different product's row.  A
        /// field being edited is therefore a DEFERRAL like every other reason, and TickDeferredRedraw lands
        /// the redraw the moment it clears.  The four reasons, first match wins, each read off the state the
        /// game itself keeps:
        /// (a) an OPEN DROPDOWN (UI.Elements/Dropdown.cs:116 `public static Dropdown currentDropdown`, set
        ///     :229 and nulled :276) whose options panel is still showing (:200);
        /// (b) a TEXT FIELD being edited - the EventSystem's selected object carries a focused TMP_InputField
        ///     or legacy InputField and sits under the same bizMan root the redraw rebuilds;
        /// (c) the HR ASSIGN-EMPLOYEES LIST open - `HrManagerPlanUI.IsAssignEmployeesListOpen` (decompile
        ///     :74), the game's own property, because that pane's LoadPlan calls CloseAssignEmployeesList()
        ///     (:99) and would slam the picker shut under the player's hand (review F1);
        /// (d) the TRAINING SLIDER held - the mod's own EventTrigger pointer events (HrSliderHeld), not a
        ///     timer, because LoadPlan re-seats `trainingTargetSlider.value` from the plan (:98) and the
        ///     PointerUp commit would then publish the value the reload put there.</summary>
        private static string EditWindowOpen()
        {
            try
            {
                var dd = UI.Elements.Dropdown.currentDropdown;
                if (dd != null && dd.gameObject.activeInHierarchy)
                {
                    var f = typeof(UI.Elements.Dropdown).GetField("optionsPanelParentRect",
                                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var rect = f != null ? f.GetValue(dd) as RectTransform : null;
                    if (rect != null && rect.gameObject.activeSelf) return "a dropdown is open";
                }
                if (TextFieldBeingEdited()) return "a text field is being edited";
                var hr = PaneOf("hr") as UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI;
                if (hr != null && hr.gameObject.activeInHierarchy && hr.IsAssignEmployeesListOpen)
                    return "the HR assign list is open";
                if (HrSliderHeldNow()) return "the training slider is held";
                return "";
            }
            catch { return ""; }
        }

        /// <summary>B2 (b): is the player typing into a field of the headquarters screen right now?  The
        /// focus is the EventSystem's, the "being edited" is the input field's OWN `isFocused`, and the
        /// descendant test uses the very root RefreshOpenTabsFor rebuilds - a field anywhere else on the
        /// phone is not this redraw's business.</summary>
        private static bool TextFieldBeingEdited()
        {
            try
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                var go = es != null ? es.currentSelectedGameObject : null;
                if (go == null) return false;
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var bm = ui != null && ui.fullMenu != null ? ui.fullMenu.bizMan : null;
                if (bm == null || !go.transform.IsChildOf(bm.transform)) return false;
                var tmp = go.GetComponentInParent<TMPro.TMP_InputField>();
                if (tmp != null) return tmp.isFocused;
                var leg = go.GetComponentInParent<UnityEngine.UI.InputField>();
                return leg != null && leg.isFocused;
            }
            catch { return false; }
        }

        /// <summary>B2 (d): true between the training slider's PointerDown/BeginDrag and its PointerUp/EndDrag,
        /// set from the EventTrigger the HR pane patch arms (Patch_HrPlanLoad_ControlsRoute.ArmSliderCommit).
        /// The game's own pointer events, so nothing here waits on a clock.  FOLD d E1: the hold is ARMED by
        /// those pointer events and ENDED three ways, every one of them an event of the game's own - the
        /// matching pointer-up, the pane's next load (a re-arm IS a fresh load, never mid-drag), and Unity's
        /// OnDisable on the HrSliderHoldGuard below when the slider leaves the screen under the player's
        /// hand.  So it cannot latch.  Never read it directly; the gate is HrSliderHeldNow, and ClearAll
        /// clears it.</summary>
        public static bool HrSliderHeld;

        /// <summary>FOLD c C1: the slider GameObject the EventTrigger was armed on, stored by
        /// ArmSliderCommit.  FOLD d E1: the hold is believed only about a slider that is still on screen -
        /// and about NOTHING ELSE.  The EventSystem's selection is deliberately not consulted: a Selectable
        /// is selected on pointer-down only when its navigation mode allows it (unknowable for this prefab),
        /// and a right- or middle-click during the drag moves the selection away while the drag is still
        /// live - either would report "not held" in the middle of a real drag.  The object outlives a
        /// session, so ClearAll leaves it standing.</summary>
        public static GameObject? HrSliderGo;

        /// <summary>FOLD c C1: one line per session for a hold that ended without its pointer-up.</summary>
        private static bool _sliderReleaseLogged;

        /// <summary>FOLD d E1: the hold ended without its pointer-up - the slider was deactivated or
        /// destroyed under the player's hand, and ExecuteEvents skips an inactive component.  Clears the
        /// flag and says so once per session.  Called by HrSliderHoldGuard.OnDisable (the moment it
        /// happens) and by the gate below (the backstop, for a slider that went away some other way).</summary>
        internal static void SliderHoldEndedWithoutPointerUp()
        {
            if (!HrSliderHeld) return;
            HrSliderHeld = false;
            if (_sliderReleaseLogged) return;
            _sliderReleaseLogged = true;
            Plugin.Logger.LogInfo("[Plans] training slider hold released - the slider left the screen mid-drag.");
        }

        /// <summary>B2 (d), THE LIVE READ (FOLD d E1).  True only when the flag is set AND the armed slider
        /// still exists and is `activeInHierarchy`.  A slider that is not on screen cannot be under a
        /// pointer, so the hold is over and the flag is CLEARED here rather than left to defer every
        /// family's redraw for the rest of the session.</summary>
        private static bool HrSliderHeldNow()
        {
            try
            {
                if (!HrSliderHeld) return false;
                var go = HrSliderGo;
                if (go != null && go.activeInHierarchy) return true;
                SliderHoldEndedWithoutPointerUp();          // nothing else would clear it
                return false;
            }
            catch { HrSliderHeld = false; return false; }
        }

        /// <summary>The five plan panes, in tab order.  Unlike `Families` this one HOLDS logistics: the
        /// in-place refresh is about the pane, and the logistics pane is a pane like any other.</summary>
        private static readonly string[] PaneFamilies = { "pricing", "purchasing", "hr", "headhunter", "logistics" };

        /// <summary>B1: one line per (family, plan) per FEED, not per call.</summary>
        private static readonly HashSet<string> _refreshLogged = new(StringComparer.Ordinal);

        /// <summary>HQ-PARITY-3 B1, THE ONE REFRESH.  Hand the pane that is open on `planId` the NEW copy of
        /// that plan and let the pane's OWN load redraw it.  Used by the receive path (all five families,
        /// after the lists rebuild) and by ApplyRouted (the RUNNER's own pane, after a partner's op landed on
        /// the runner's real plan - A4), so a member and an owner watching the same plan both see a change
        /// the moment it is made.  Nothing here draws anything itself: it calls the game's own LoadPlan.
        /// HQ-PARITY-4 P4: on the receive path this is now SKIPPED for any family the list rebuild already
        /// reselected - that reselect went through the list's own SelectPlan, which loaded the pane with the
        /// fresh row object, and a second load would be a second object for the one plan.</summary>
        public static void RefreshOpenPaneInPlace(string family, string planId)
        {
            try
            {
                if (string.IsNullOrEmpty(family) || string.IsNullOrEmpty(planId)) return;
                var pane = PaneOf(family);
                if (pane == null || !pane.gameObject.activeInHierarchy) return;
                if (!string.Equals(OpenPanePlanId(family, pane), planId, StringComparison.Ordinal)) return;
                object? plan = PlanObjectOf(family, planId);
                if (plan == null) return;
                switch (family)
                {
                    case "pricing":
                    {
                        var ui = pane as UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagerPlanUI;
                        var pl = plan as PricingManagerPlan;
                        if (ui == null || pl == null) return;
                        ui.LoadPlan(pl); break;
                    }
                    case "purchasing":
                    {
                        var ui = pane as UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentPlanUI;
                        var pl = plan as ImportPartnership;
                        if (ui == null || pl == null) return;
                        ui.LoadPlan(pl); break;
                    }
                    case "hr":
                    {
                        var ui = pane as UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI;
                        var pl = plan as HrManagerPlan;
                        if (ui == null || pl == null) return;
                        ui.LoadPlan(pl); break;
                    }
                    case "headhunter":
                    {
                        var ui = pane as HeadhunterPlanUI;
                        var pl = plan as HeadhunterPlan;
                        if (ui == null || pl == null) return;
                        ui.LoadPlan(pl); break;
                    }
                    case "logistics":
                    {
                        var ui = pane as UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagerPlanUI;
                        var pl = plan as Buildings.Office.Headquarters.LogisticsManagerPlan;
                        if (ui == null || pl == null) return;
                        ui.LoadPlan(pl);
                        CompanyLists.CaptureLogisticsBaseline(pl);   // A1: the redrawn copy IS the new baseline
                        break;
                    }
                    default: return;
                }
                if (_refreshLogged.Add(family + "|" + planId))
                    Plugin.Logger.LogInfo($"[Plans] refreshed the open {family} pane in place for plan {planId} - it changed.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] in-place {family} refresh: {ex.Message}"); }
        }

        /// <summary>FOLD b G2 (review F2): put the headhunter pane back on the plan its list rebuild just
        /// hid.  HeadhunterPlanUI.LoadPlan ENDS in gameObject.SetActive(true) itself (decompile
        /// HeadhunterPlanUI.cs:48), so the load IS the show - the pane has no Show() to call, only a Hide()
        /// (:63-67), and the list's own SelectPlan (HeadhuntersPlanList.cs:213-228) shows it the same way,
        /// through LoadPlan.  What SelectPlan also does - recolour the row, swap its name label for its
        /// dropdown, re-point the no-manager popup - belongs to a ROW, and there is no row to find here.
        /// FOLD c I1 (review H1): AND THAT IS WHY A PLAN WITH NO HEADHUNTER IS NOT REOPENED AT ALL.  LoadPlan
        /// calls noManagerAssignedPopUp.Show() when the plan's own assignedEmployeeId is null (decompile
        /// HeadhunterPlanUI.cs:40-42), and that popup's onChooseEmployee is still the closure
        /// HeadhuntersPlanList.SelectPlan built over the ROW the player clicked (decompile
        /// HeadhuntersPlanList.cs:213-228); its tail reaches into entry.Find("ManagerName/UnassignedManagerIcon"),
        /// and the rebuild has DESTROYED that row - so choosing an employee in the reopened popup throws a
        /// MissingReferenceException inside the game's own dropdown listener, AFTER the plan write already
        /// happened, with no mod try/catch anywhere on that path.  The pane is left hidden instead and the
        /// player is told to click the row, which is what builds the closure over a row that exists.
        /// One INFO per plan per feed: _refreshLogged is cleared at the top of every pane loop.</summary>
        private static void ReopenHeadhunterPane(string planId)
        {
            try
            {
                if (string.IsNullOrEmpty(planId)) return;
                var pane = PaneOf("headhunter") as HeadhunterPlanUI;
                if (pane == null) return;
                var pl = PlanObjectOf("headhunter", planId) as HeadhunterPlan;
                if (pl == null) return;
                // I1: the GAME'S OWN gate, read off the game's own field before the load - LoadPlan fires the
                // stale-closure popup on exactly this test (`plan.assignedEmployeeId == null`), so the pane is
                // left hidden rather than armed.
                if (pl.assignedEmployeeId == null)
                {
                    if (_refreshLogged.Add("headhunter-unassigned|" + planId))
                        Plugin.Logger.LogInfo($"[Plans] headhunter plan {planId} has no headhunter assigned - the pane is not reopened after the rebuild; click the row to reopen it.");
                    return;
                }
                pane.LoadPlan(pl);
                if (_refreshLogged.Add("headhunter|" + planId))
                    Plugin.Logger.LogInfo($"[Plans] reopened the headhunter pane for plan {planId} after the list rebuild.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] reopen the headhunter pane: {ex.Message}"); }
        }

        // -- HQ-PARITY-4 P1: REGISTRIES, NOT SWEEPS ---------------------------

        /// <summary>P1.  The PANE object the game itself last loaded for each family, recorded by a postfix
        /// on that family's own LoadPlan.  The hierarchy sweep below never found the PURCHASING pane in
        /// either hands-on session, so that pane never refreshed and the owner's warehouse count was never
        /// substituted into it: the game holds a direct handle to it (decompile BizManBusiness.cs:98
        /// `bizMan.business.purchasingAgentsPlanList.purchasingAgentPlanUISettings`) and never parents it
        /// under the object the sweep starts from.  NOT cleared by ClearAll - these are Unity objects that
        /// outlive a session, and every read of them is Unity-null-checked, which covers destruction.</summary>
        private static readonly Dictionary<string, Component> _paneOf = new(StringComparer.Ordinal);

        /// <summary>P1: the LIST object the game itself last refreshed for each family, recorded by the five
        /// union postfixes on RefreshManagersList / RefreshPlansList.</summary>
        private static readonly Dictionary<string, Component> _listOf = new(StringComparer.Ordinal);

        /// <summary>P1, FOLD b G3 (review F3): the (family|registry) pairs whose registered object has
        /// ALREADY been compared with the sweep's - written whether the two agreed or not, so the comparison
        /// sweep runs once per family per registry per session either way.  Writing it only on a MISMATCH
        /// left the agreeing families sweeping the hierarchy on every single call, for a line that was never
        /// going to be logged.  The INFO itself is still one per pair per session.</summary>
        private static readonly HashSet<string> _regCompared = new(StringComparer.Ordinal);

        /// <summary>P1: called from the five pane LoadPlan postfixes with that patch's `__instance`.</summary>
        public static void RegisterPane(string family, Component pane)
        { try { if (!string.IsNullOrEmpty(family) && pane != null) _paneOf[family] = pane; } catch { } }

        /// <summary>P1: called from the five list refresh postfixes with that patch's `__instance`.</summary>
        public static void RegisterList(string family, Component list)
        { try { if (!string.IsNullOrEmpty(family) && list != null) _listOf[family] = list; } catch { } }

        /// <summary>P1: the REGISTERED pane while it is alive, else the sweep.  G3: the comparison costs
        /// ONE sweep per family per session - the first call records that the pair has been compared,
        /// agreement or not, and every call after it skips the sweep.
        /// INTERNAL since HQ-PARITY-6 P2: the three EDIT gates in MPPatches (hr, pricing, purchasing) ask
        /// this too, because their own hierarchy search was finding a DIFFERENT, inactive instance of the
        /// pane and their edits then took the own-edit path instead of being routed.</summary>
        internal static Component? PaneOf(string family)
        {
            try
            {
                if (_paneOf.TryGetValue(family, out var reg) && reg != null)
                {
                    if (_regCompared.Add(family + "|pane") && !ReferenceEquals(reg, SweptPaneOf(family)))
                        Plugin.Logger.LogInfo($"[Plans] {family}: the pane on screen is not the one the hierarchy search finds - using the registered one.");
                    return reg;
                }
            }
            catch { }
            return SweptPaneOf(family);
        }

        /// <summary>FOLD b H4(a): the registry and NOTHING ELSE - null when this family has no live pane
        /// recorded yet.  PaneOf answers with its own hierarchy sweep in that case, which is fine for finding
        /// a pane but useless to a caller that wants to know whether the pane it holds CAME from the registry
        /// (MergerGatePane, before it reports a disagreement with its own search).</summary>
        internal static Component? RegisteredPaneOf(string family)
        {
            try { return _paneOf.TryGetValue(family, out var reg) && reg != null ? reg : null; }
            catch { return null; }
        }

        /// <summary>P1: the REGISTERED list while it is alive, else the sweep.</summary>
        private static Component? ListOf(string family)
        {
            try
            {
                if (_listOf.TryGetValue(family, out var reg) && reg != null)
                {
                    if (_regCompared.Add(family + "|list") && !ReferenceEquals(reg, SweptListOf(family)))
                        Plugin.Logger.LogInfo($"[Plans] {family}: the list on screen is not the one the hierarchy search finds - using the registered one.");
                    return reg;
                }
            }
            catch { }
            return SweptListOf(family);
        }

        private static Component? SweptPaneOf(string family)
        {
            try
            {
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var bm = ui != null && ui.fullMenu != null ? ui.fullMenu.bizMan : null;
                if (bm == null) return null;
                switch (family)
                {
                    case "pricing":    return bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagerPlanUI>(true);
                    case "purchasing": return bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentPlanUI>(true);
                    case "hr":         return bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI>(true);
                    case "headhunter": return bm.GetComponentInChildren<HeadhunterPlanUI>(true);
                    case "logistics":  return bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagerPlanUI>(true);
                }
            }
            catch { }
            return null;
        }

        private static Component? SweptListOf(string family)
        {
            try
            {
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var bm = ui != null && ui.fullMenu != null ? ui.fullMenu.bizMan : null;
                if (bm == null) return null;
                switch (family)
                {
                    case "pricing":    return bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagersPlanList>(true);
                    case "purchasing": return bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.PurchasingAgentsPlanList>(true);
                    case "hr":         return bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.HRManagers.HrManagersPlanList>(true);
                    case "headhunter": return bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.Headhunters.HeadhuntersPlanList>(true);
                    case "logistics":  return bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagersPlanList>(true);
                }
            }
            catch { }
            return null;
        }

        public static string OpenPanePlanId(string family) => OpenPanePlanId(family, PaneOf(family));

        /// <summary>HQ-PARITY-3 B3: WHICH plan each pane is showing, read off the pane itself.  The headhunter
        /// family carries no id on its ROWS at all (its entries are bare clones and its selection is a private
        /// Transform), which is why it used to defer instead of refreshing - but the PANE holds the plan in the
        /// open: `public HeadhunterPlan currentPlan` (decompile HeadhunterPlanUI.cs:26, set in LoadPlan :38).
        /// There is no sibling-index or name guess anywhere here: a position is not an identity.</summary>
        private static string OpenPanePlanId(string family, Component? pane)
        {
            try
            {
                if (pane == null || !pane.gameObject.activeInHierarchy) return "";
                if (family == "headhunter")
                {
                    var hh = pane as HeadhunterPlanUI;
                    return hh != null && hh.currentPlan != null ? (hh.currentPlan.id ?? "") : "";
                }
                string fn = family == "purchasing" ? "_currentImportPartnership" : "_currentPlan";
                var f = pane.GetType().GetField(fn, BindingFlags.Instance | BindingFlags.NonPublic);
                object? pl = f != null ? f.GetValue(pane) : null;
                if (pl == null) return "";
                var idf = pl.GetType().GetField("id", BindingFlags.Instance | BindingFlags.Public);
                return idf != null ? (idf.GetValue(pl) as string ?? "") : "";
            }
            catch { return ""; }
        }

        /// <summary>The plan object with that id AS IT STANDS NOW.  HQ-PARITY-4 P4 reversed the order: the
        /// screen-layer registry's materialised row comes FIRST, because that is the PARTNER's copy and the
        /// copy the pane on a partner's headquarters is showing - the game's own lists could answer with a
        /// same-id plan of this machine's own and hand the pane the wrong object.  For LOGISTICS the two are
        /// the same object anyway (ShowLogisticsUnionRows draws the installed copies out of
        /// `gi.logisticsManagerPlans`), so the change is harmless there and decisive for the other four.</summary>
        private static object? PlanObjectOf(string family, string planId)
        {
            try
            {
                string tail = "|" + family + "|" + planId;
                foreach (var kv in _rows)
                    if (kv.Key.EndsWith(tail, StringComparison.Ordinal) && kv.Value != null) return kv.Value;
                var gi = SaveGameManager.Current;
                if (gi != null)
                    switch (family)
                    {
                        case "pricing":
                            if (gi.pricingManagerPlans != null)
                                foreach (var x in gi.pricingManagerPlans) if (x != null && x.id == planId) return x;
                            break;
                        case "purchasing":
                            if (gi.importPartnerships != null)
                                foreach (var x in gi.importPartnerships) if (x != null && x.id == planId) return x;
                            break;
                        case "hr":
                            if (gi.hrManagerPlans != null)
                                foreach (var x in gi.hrManagerPlans) if (x != null && x.id == planId) return x;
                            break;
                        case "headhunter":
                            if (gi.headhunterPlans != null)
                                foreach (var x in gi.headhunterPlans) if (x != null && x.id == planId) return x;
                            break;
                        case "logistics":
                            if (gi.logisticsManagerPlans != null)
                                foreach (var x in gi.logisticsManagerPlans) if (x != null && x.id == planId) return x;
                            break;
                    }
            }
            catch { }
            return null;
        }

        /// <summary>P2: one line per family per session.</summary>
        private static readonly HashSet<string> _destroyedRowLogged = new(StringComparer.Ordinal);

        /// <summary>HQ-PARITY-4 P2 / R1.  The list still POINTS at a row, but the row behind the reference is
        /// destroyed - the C# field is set and the Unity object is gone.  That is exactly what the previous
        /// bundle left behind when its reselect landed on a row `RefreshManagersList` had already doomed
        /// (`entryTemplate.transform.ResetTemplate()` destroys at end of frame, decompile
        /// LogisticsManagersPlanList.cs:97), which is why the logistics pane vanished on every second
        /// bundle.</summary>
        private static bool SelectedEntryDestroyed(Component list)
        {
            try
            {
                var f = list.GetType().GetField("_selectedEntry", BindingFlags.Instance | BindingFlags.NonPublic);
                object? sel = f != null ? f.GetValue(list) : null;
                return !ReferenceEquals(sel, null) && TransformOf(sel) == null;
            }
            catch { return false; }
        }

        /// <summary>FOLD b G1: `owner|family` -&gt; the ROW-SET shape that family's list was last REBUILT
        /// from.  A pair absent from here has never been rebuilt on this machine and counts as changed.</summary>
        private static readonly Dictionary<string, string> _lastRowSetShape = new(StringComparer.Ordinal);

        /// <summary>FOLD b G1 (review F1), THE LIST REBUILD IS CHANGE-GATED TOO.  Rebuild was gated only on
        /// the list being active, and Receive calls the redraw on EVERY bundle - so with a tab open a partner
        /// merely trading destroyed and rebuilt every row every two seconds and the reselect fired the game's
        /// own SelectPlan -&gt; LoadPlan on the pane being read: the reload-while-trading churn the pane loop's
        /// change gate was built to remove.  The test is the owner's published ROW SET for the family
        /// (RowSetShapeOf), so a row added, removed, reordered or edited redraws the list and a stock count
        /// moving does not.  The shape is recorded only when the rebuild actually RAN - a list that is not on
        /// screen, or a refresh method that could not be reached, leaves the record alone.</summary>
        private static int RebuildIfChanged(string ownerPid, Component? list, string method, string fam,
                                            ref int reselected, HashSet<string> reselectedFams,
                                            Dictionary<string, string> openBefore)
        {
            string key = ownerPid + "|" + fam, shape = RowSetShapeOf(ownerPid, fam);
            if (_lastRowSetShape.TryGetValue(key, out var seen) && seen == shape) return 0;
            int ran = Rebuild(list, method, fam, ref reselected, reselectedFams, openBefore);
            if (ran > 0) _lastRowSetShape[key] = shape;
            return ran;
        }

        private static int Rebuild(Component? list, string method, string fam, ref int reselected, HashSet<string> reselectedFams,
                                   Dictionary<string, string> openBefore)
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
                // HQ-PARITY-4 P2: THE OPEN PLAN COMES FROM THE PANE FIRST.  The pane is what the player is
                // actually reading and it answers off its own `_currentPlan`, which no list rebuild can
                // destroy; the list's `_selectedEntry` is only the fallback, and it is the thing that goes
                // Unity-null when the row under it was doomed by the previous refresh.
                var open = OpenPlanOf(list);
                if (open.Id.Length == 0 && SelectedEntryDestroyed(list) && _destroyedRowLogged.Add(fam))
                    Plugin.Logger.LogInfo($"[Plans] {fam}: the list's selected row was destroyed - reselecting from the pane.");
                string paneId = OpenPanePlanId(fam);
                // FOLD b G2: the pre-invoke open id goes out to the caller - the headhunter refresh HIDES its
                // pane and leaves nothing to read it off afterwards.
                if (paneId.Length > 0) { open.Id = paneId; openBefore[fam] = paneId; }
                // HQ-PARITY-4 P3: A RESELECT CAN ONLY LAND ON A LIVE ROW.  The rows standing here are the
                // ones the refresh is about to destroy; they stay present and activeSelf for the rest of
                // this frame and answer an id check perfectly, so the only safe rows are the ones NOT in
                // this set - the rows born in the refresh below.
                var before = new HashSet<Transform>();
                var parentBefore = EntryParentOf(list);
                if (parentBefore != null)
                    for (int i = 0; i < parentBefore.childCount; i++) before.Add(parentBefore.GetChild(i));
                m.Invoke(list, null);
                if (open.Id.Length > 0)
                    if (Reselect(list, open, before)) { reselected++; reselectedFams.Add(fam); }
                return 1;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] {method}: {ex.Message}"); return 0; }
        }

        /// <summary>P6: which plan one of the five lists has OPEN, in the one form all five can answer.  Id
        /// is the plan id wherever the list makes it readable - the entry component's public Plan (pricing
        /// and logistics entries), the id-to-row map (HR's `_entriesByPlanIds` :105 or logistics'
        /// `_entriesById` :47) or the entry object's
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
                object? sel = f != null ? f.GetValue(list) : null;
                var t = TransformOf(sel);
                if (t == null) return r;
                r.Id = PlanIdOfEntry(list, sel, t);
            }
            catch { }
            return r;
        }

        private static Transform? TransformOf(object? entry)
        {
            var t = entry as Transform; if (t != null) return t;
            var c = entry as Component;  return c != null ? c.transform : null;
        }

        /// <summary>P3: the two names the five lists give their id-to-row map.</summary>
        private static readonly string[] EntryMapNames = { "_entriesByPlanIds", "_entriesById" };

        /// <summary>P6, the ONE entry-to-plan-id resolver for all five families, in order of certainty.
        /// HQ-PARITY-4 P3: the MAP branch tries BOTH names.  HR's map is `_entriesByPlanIds`
        /// (HrManagersPlanList.cs:105) but LOGISTICS names its own `_entriesById` (decompile
        /// LogisticsManagersPlanList.cs:47, `Dictionary&lt;string, LogisticsManagersPlanListEntry&gt;`), so
        /// looking for one name only meant the one map that is REFILLED WITH LIVE ROWS ONLY was never
        /// consulted for the family whose pane was vanishing.</summary>
        private static string PlanIdOfEntry(Component list, object entry, Transform t)
        {
            try
            {
                if (entry != null && !(entry is Transform))
                {
                    var pp = entry.GetType().GetProperty("Plan", BindingFlags.Instance | BindingFlags.Public);
                    object? plan = pp != null ? pp.GetValue(entry) : null;
                    if (plan != null)
                    {
                        var idf = plan.GetType().GetField("id", BindingFlags.Instance | BindingFlags.Public);
                        var id = idf != null ? idf.GetValue(plan) as string : null;
                        if (id != null && id.Length > 0) return id;
                    }
                }
                foreach (var mapName in EntryMapNames)
                {
                    var mf = list.GetType().GetField(mapName, BindingFlags.Instance | BindingFlags.NonPublic);
                    if (mf == null || t == null) continue;
                    if (!(mf.GetValue(list) is System.Collections.IDictionary map)) continue;
                    foreach (System.Collections.DictionaryEntry kv in map)
                        if (ReferenceEquals(TransformOf(kv.Value), t) && kv.Key is string k && k.Length > 0) return k;
                }
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
        /// c1: the path is PER FAMILY - only three of the five wire the click to the entry ROOT.
        /// HQ-PARITY-4 P3: `before` is the set of rows that existed BEFORE the refresh ran.  Those rows are
        /// already destroyed - `ResetTemplate()` defers the destruction to the end of the frame - but they
        /// are still present, still activeSelf and still answer their plan id, at LOWER sibling indices than
        /// the new rows, so a first-match walk handed the game a doomed row and the pane died at the end of
        /// the frame.  Only rows NOT in that set are considered here.</summary>
        private static bool Reselect(Component list, OpenPlan open, HashSet<Transform> before)
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

                Transform? hit = null; Component? hitEntry = null;

                // HQ-PARITY-4 P3: LOGISTICS RESOLVES THROUGH THE LIST'S OWN MAP FIRST.  `_entriesById`
                // (decompile LogisticsManagersPlanList.cs:47) is cleared at :96 and refilled by
                // SetUpPlanEntry (:144) with the rows THIS refresh just built, so it cannot answer with a
                // doomed row - and it is the same map the game's own SelectPlan falls back to (:215-222).
                if (fam == "LogisticsManagersPlanList")
                {
                    var mapped = LogisticsEntryById(list, open.Id);
                    if (mapped != null) { hitEntry = mapped; hit = mapped.transform; }
                }

                if (hit == null)
                {
                    var parent = EntryParentOf(list);
                    if (parent == null) return false;
                    for (int i = 0; i < parent.childCount; i++)
                    {
                        var c = parent.GetChild(i);
                        if (before.Contains(c)) continue;          // P3: a row that predates the refresh is doomed
                        if (!c.gameObject.activeSelf) continue;
                        var ec = EntryComponentOf(c);
                        if (PlanIdOfEntry(list, ec, c) != open.Id) continue;
                        hit = c; hitEntry = ec; break;
                    }
                }
                if (hit == null) return false;

                // LOGISTICS wires its click to the same serialized CHILD (LogisticsManagersPlanListEntry.cs:44-48).
                // Its public selection takes the row AND the plan: `public void SelectPlan(Transform
                // entryTransform, LogisticsManagerPlan plan)` (LogisticsManagersPlanList.cs:207), and the plan is
                // the rebuilt row's own `public LogisticsManagerPlan Plan { get; private set; }` (entry :36).
                if (fam == "LogisticsManagersPlanList")
                {
                    var pp = hitEntry != null ? hitEntry.GetType().GetProperty("Plan", BindingFlags.Instance | BindingFlags.Public) : null;
                    object? plan = pp != null ? pp.GetValue(hitEntry) : null;
                    if (plan == null) return false;
                    var sel = list.GetType().GetMethod("SelectPlan", BindingFlags.Instance | BindingFlags.Public,
                                                       null, new[] { typeof(Transform), pp!.PropertyType }, null);
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

        /// <summary>HQ-PARITY-4 P3: the LIVE row for a plan id, straight out of LogisticsManagersPlanList's
        /// own `private readonly Dictionary&lt;string, LogisticsManagersPlanListEntry&gt; _entriesById`
        /// (decompile :47).  Null = this refresh drew no row for that plan.</summary>
        private static Component? LogisticsEntryById(Component list, string planId)
        {
            try
            {
                if (string.IsNullOrEmpty(planId)) return null;
                var f = list.GetType().GetField("_entriesById", BindingFlags.Instance | BindingFlags.NonPublic);
                var map = f != null ? f.GetValue(list) as System.Collections.IDictionary : null;
                if (map == null) return null;
                foreach (System.Collections.DictionaryEntry kv in map)
                    if (kv.Key is string k && string.Equals(k, planId, StringComparison.Ordinal))
                    {
                        var c = kv.Value as Component;
                        return c != null ? c : null;              // a destroyed row compares equal to null
                    }
            }
            catch { }
            return null;
        }

        /// <summary>Every entry is instantiated under the TEMPLATE's parent (all five SetUpPlanEntry bodies
        /// do exactly that), so the template field is the handle on the row container.</summary>
        private static Transform? EntryParentOf(Component list)
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
        private static Component? EntryComponentOf(Transform t)
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
        public static void StripDragHandle(Transform entry)
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
                    case "logistics":
                    {
                        // G2 (HQ-PARITY-8): the logistics drop handler indexes GetFilteredPlans()
                        // (LogisticsManagersPlanList.cs:77-79 / :107-120) - the headquarters' plans of the
                        // OPEN TAB only - so the warehouse/factory filter is part of the count.  The tab is
                        // the list component's own public `currentTab`, read the way the create gate reads it.
                        bool factory = false;
                        try
                        {
                            var lst = ListOf("logistics") as UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagersPlanList;
                            factory = lst != null && (lst.currentTab ?? "").Equals("factory", StringComparison.OrdinalIgnoreCase);
                        }
                        catch { }
                        int n = 0;
                        foreach (var pl in LogisticsManagerHelper.GetAssignedPlansForHeadquarters(a))
                            if (pl != null && pl.isFactory == factory) n++;
                        return n;
                    }
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
                if (d == null) continue;
                string who = EmpName(d.EmployeeInstanceId ?? "");
                // HQ-PARITY-3 B6: the two purchasing toggles the hqtoggle lever drives.
                Take("purchasing", d.Id ?? "", who.Length > 0 ? who : (d.ImportAddressKey ?? ""),
                     ProductLines(d) + $" rep={d.IsRepeatingOrder} auto={d.IsTarget}");
            }
            // HQ-PARITY-2 P4: the logistics rows were never listed here.  Each carries the two numbers a
            // co-member cannot compute, so the rig can assert the capacity and the stock it is drawing with.
            foreach (var d in p.LogisticsManagerPlans ?? new List<PwLogisticsPlan>())
            {
                if (d == null) continue;
                Take("logistics", d.Id ?? "", EmpName(d.AssignedEmployeeId ?? ""), LogisticsDetail(d));
            }
            // HQ-PARITY-3 B6: the HR pair (B5) and the headhunter deal-breaker count, so the rig can assert
            // the owner-side publish per CONTROL rather than per plan.
            foreach (var d in p.HrManagerPlans ?? new List<PwHrPlan>())
                Take("hr", d?.Id, EmpName(d?.AssignedEmployeeId ?? ""),
                     $"#replace={d?.ReplaceAbsentEmployees ?? false} train={d?.TrainingTarget ?? 0}");
            foreach (var d in p.HeadhunterPlans ?? new List<PwHeadhunterPlan>())
                Take("headhunter", d?.Id, EmpName(d?.AssignedEmployeeId ?? ""),
                     $"#dealbreakers={d?.DealBreakerTypes?.Count ?? 0}");
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
                    // FOLD b B8: and the number the PANE actually draws - the owner's own count for that
                    // warehouse and item, carried on the bundle.  `stock=` is the LOCAL replica's count, so
                    // without this the rig could not see whether the carry was happening at all.
                    string carried = "none";
                    foreach (var st in d?.Stock ?? new List<PwStockLine>())
                        if (st != null && Same(st.AddressKey, ln.AssignedWarehouseKey)
                            && string.Equals(st.ItemName, ln.ItemName ?? "", StringComparison.Ordinal))
                        { carried = st.Count.ToString(System.Globalization.CultureInfo.InvariantCulture); break; }
                    if (sb.Length > 8) sb.Append(';');
                    sb.Append($"item={RowFieldSafe(ln.ItemName)} amount={ln.Amount} ")
                      .Append($"wh={(wh == null ? "none" : RowFieldSafe(ln.AssignedWarehouseKey))} stock={stock} carried={carried}");
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

        /// <summary>P4: one logistics plan's carried numbers - the capacity the destination rows are greyed
        /// against (LogisticsManagerPlanUI.cs:265) and the per-item stock the product rows draw (:296-298).
        /// The row format's own three characters cannot appear in a field, so the stock list is `;`-joined
        /// and `=`-paired exactly as the purchasing lines are.</summary>
        private static string LogisticsDetail(PwLogisticsPlan d)
        {
            try
            {
                if (d == null) return "";
                var sb = new System.Text.StringBuilder($"#max={d.MaxDestinations} dests={d.Destinations?.Count ?? 0} stock=[");
                int n = 0;
                foreach (var ln in d.Stock ?? new List<PwStockLine>())
                {
                    if (ln == null || string.IsNullOrEmpty(ln.ItemName)) continue;
                    if (n++ > 0) sb.Append(';');
                    // FOLD b B8: `<item>=<count>/<soldPerWeek>` - the second number is the denominator the
                    // pane's "runs out in" column is computed from (ScopedRunsOutIn), and it was invisible.
                    sb.Append($"{RowFieldSafe(ln.ItemName)}={ln.Count}/{ln.SoldPerWeek}");
                }
                return sb.Append(']').ToString();
            }
            catch { return "#max=0 dests=0 stock=[]"; }
        }

        /// <summary>P4, THE LOGISTICS LEVER: `hqlog &lt;planId&gt; &lt;op&gt; [args]` drives exactly the paths the
        /// pane's controls drive.  On a PARTNER's display copy it sends the op leg the diff would have sent
        /// (RouteLogisticsOps -> Send); on one of THIS machine's own plans it runs the same runner body the
        /// op would have run there and marks the bundle urgent, which is what the pane's own commit does.
        /// HQ-PARITY-5 B4: the four DESTINATION ops keep their usage - an index is still how the lever names
        /// a row - but the index is now applied to a LOCAL copy of the plan's own destination list, and what
        /// leaves (or is applied) is ONE `destset` carrying the whole desired list, exactly as the pane's
        /// diff now sends it.  No index crosses the wire any more.
        /// Ops: manager &lt;employeeId|->, warehouse &lt;addressKey|->, destadd [addressKey],
        /// destremove &lt;index&gt;, destchange &lt;index&gt; &lt;addressKey&gt;, target &lt;index&gt; &lt;item&gt; &lt;amount&gt;.</summary>
        public static string TestDriveHqLog(string arg)
        {
            try
            {
                var a = (arg ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (a.Length < 2) return "ERR usage: hqlog <planId> <shape|load|manager|warehouse|destadd|destremove|destchange|target> [args]";
                string id = a[0], op = a[1].ToLowerInvariant();
                if (op == "shape") return HqLogShape(id);
                if (op == "load")  return HqLogLoad(id);
                string s = ""; int iv = 0, dest = -1;
                switch (op)
                {
                    case "manager":
                    case "warehouse":  s = a.Length > 2 && a[2] != "-" ? a[2] : ""; break;
                    case "destadd":    s = a.Length > 2 ? a[2] : ""; break;
                    case "destremove": if (a.Length < 3 || !int.TryParse(a[2], out iv)) return "ERR usage: hqlog <planId> destremove <index>"; break;
                    case "destchange": if (a.Length < 4 || !int.TryParse(a[2], out iv)) return "ERR usage: hqlog <planId> destchange <index> <addressKey>"; s = a[3]; break;
                    case "target":
                        if (a.Length < 5 || !int.TryParse(a[2], out dest) || !int.TryParse(a[4], out iv))
                            return "ERR usage: hqlog <planId> target <destIndex> <itemName> <amount>";
                        s = a[3]; break;
                    default: return $"ERR no logistics op '{op}'";
                }
                var gi = SaveGameManager.Current;
                if (gi == null || gi.logisticsManagerPlans == null) return "ERR no game instance here";
                Buildings.Office.Headquarters.LogisticsManagerPlan? pl = null;
                foreach (var x in gi.logisticsManagerPlans) if (x != null && x.id == id) { pl = x; break; }
                if (pl == null) return $"ERR no logistics plan '{id}' here";
                string item = s;
                if (op == "target")
                {
                    // The rig captures the ROW-SAFE spelling of an item name (fold b); accept either.
                    foreach (var dst in pl.destinations ?? new List<Entities.LogisticsManagerPlanDestination>())
                        foreach (var t in dst?.stockTargets ?? new List<BigAmbitions.Items.ItemAmountTarget>())
                            if (t != null && RowFieldSafe(t.itemName) == s) item = t.itemName;
                }
                // FOLD b4: the ROUTED branch reads two values (the op's string and its destination index)
                // and Send builds the payload itself; only the OWN-plan branch needs a whole one, and it is
                // built there.
                string strValue = op == "target" ? item : s;
                string station = dest >= 0 ? dest.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
                // B4: a destination op becomes the DESIRED LIST.  The index names a row in this machine's own
                // copy of the plan's destinations, the edit is made there, and the whole list travels.
                List<PwLogisticsDestination>? destinations = null;
                string sendOp = op;
                if (op == "destadd" || op == "destremove" || op == "destchange" || op == "target")
                {
                    destinations = DestinationsOf(pl);
                    int at = op == "target" ? dest : iv;
                    if (op != "destadd" && (at < 0 || at >= destinations.Count))
                        return $"ERR destination {at} is not on plan '{id}' here ({destinations.Count} of them)";
                    switch (op)
                    {
                        case "destadd":
                            destinations.Add(new PwLogisticsDestination { DeliveryTargetAddressKey = s });
                            break;
                        case "destremove":
                            destinations.RemoveAt(at);
                            break;
                        case "destchange":
                            // UpdateSelectedBusiness Reset()s the row it re-points (LogisticsManagerPlanUI
                            // .cs:568-571), so a re-pointed row loses its targets here too.
                            destinations[at].DeliveryTargetAddressKey = s;
                            destinations[at].StockTargets.Clear();
                            break;
                        default:
                        {
                            if (item.Length == 0) return "ERR usage: hqlog <planId> target <destIndex> <itemName> <amount>";
                            var rows = destinations[at].StockTargets;
                            PwItemOrderLine? row = null;
                            foreach (var t in rows) if (t != null && t.ItemName == item) { row = t; break; }
                            if (iv <= 0) { if (row != null) rows.Remove(row); }
                            else if (row != null) row.Amount = iv;
                            else rows.Add(new PwItemOrderLine { ItemName = item, Amount = iv });
                            break;
                        }
                    }
                    sendOp = "destset"; strValue = ""; iv = 0; station = "";
                }
                if (CompanyLists.IsDisplayPlan(pl))
                {
                    string hq = KeyOf(pl.headquartersAddress);
                    if (hq.Length == 0) return $"ERR plan '{id}' names no headquarters here";
                    CompanyLists.TryOwnerOfAddress(hq, out var owner);
                    Send("logistics", id, hq, owner ?? "", sendOp, "hqlog lever", null, strValue, iv, 0f, false, station, destinations);
                    return $"OK hqlog {id} {op} routed to '{(string.IsNullOrEmpty(owner) ? "?" : owner)}' for '{hq}'";
                }
                var pay = new SharedWorkEditPayload
                {
                    PlayerId = MPConfig.PlayerId, Op = "mergerplanedit", Family = "logistics", PlanId = id,
                    PlanOp = sendOp, StrValue = strValue, IntValue = iv, StationId = station,
                    AddressKey = KeyOf(pl.headquartersAddress), Destinations = destinations,
                };
                _refusal = "";
                if (!ApplyLogistics(gi, pl.headquartersAddress, pay, sendOp, id))
                    return $"ERR hqlog {id} {op} refused: {(_refusal.Length > 0 ? _refusal : "the runner refused the edit")}";
                SaveGameManager.MarkChange();
                OwnEditCommitted("hqlog lever");
                return $"OK hqlog {id} {op} applied (own plan; published at the urgent cadence)";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        /// <summary>B4.  One plan's destinations in the shape the wire carries them - what `destset` takes,
        /// and the starting point the lever applies its index edit to.</summary>
        private static List<PwLogisticsDestination> DestinationsOf(Buildings.Office.Headquarters.LogisticsManagerPlan pl)
        {
            var list = new List<PwLogisticsDestination>();
            foreach (var d in pl?.destinations ?? new List<Entities.LogisticsManagerPlanDestination>())
            {
                var pd = new PwLogisticsDestination { DeliveryTargetAddressKey = d == null ? "" : KeyOf(d.deliveryTargetAddress) };
                foreach (var t in (d == null ? null : d.stockTargets) ?? new List<BigAmbitions.Items.ItemAmountTarget>())
                    if (t != null && !string.IsNullOrEmpty(t.itemName))
                        pd.StockTargets.Add(new PwItemOrderLine { ItemName = t.itemName, Amount = t.targetAmount });
                list.Add(pd);
            }
            return list;
        }

        /// <summary>HQ-PARITY-3 DIAGNOSTIC: the two strings whose asymmetry turned an OPEN into an EDIT.
        /// `SerializeObject(PlanToDto(copy))` is what every route compares; the baseline is what this machine
        /// seeded.  Before A1 the two were built by different paths (PlanToDto against Bare(receivedDto), on a
        /// copy assembled by a third, MergerAbsence.FillDestinations/SetAddr) and any single differing byte
        /// read as the player having changed something.  A1 makes the OPEN itself the capture, so this should
        /// now say `identical` on a plan that has just been opened.</summary>
        private static string HqLogShape(string id)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null || gi.logisticsManagerPlans == null) return "ERR no game instance here";
                Buildings.Office.Headquarters.LogisticsManagerPlan? pl = null;
                foreach (var x in gi.logisticsManagerPlans) if (x != null && x.id == id) { pl = x; break; }
                if (pl == null) return $"ERR no logistics plan '{id}' here";
                string now = Newtonsoft.Json.JsonConvert.SerializeObject(CompanyLists.PlanToDto(pl));
                string was = CompanyLists.BaselineShapeOf(id);
                if (was.Length == 0) return $"OK hqlog {id} shape no-baseline copy={now.Length}";
                if (was == now) return $"OK hqlog {id} shape identical len={now.Length}";
                int i = 0;
                while (i < now.Length && i < was.Length && now[i] == was[i]) i++;
                return $"OK hqlog {id} shape differs at {i} copy={now.Length} baseline={was.Length}";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        /// <summary>HQ-PARITY-3 A1's ASSERTION: run the REAL load of a display copy and prove it sends
        /// NOTHING.  The list's own `SelectPlan(Transform, LogisticsManagerPlan)` is the path a click takes
        /// (LogisticsManagersPlanList.cs:207 -> LoadPlan), so when the list is on screen that is what runs.
        /// With no list drawn - the rig never opens a screen - the SEAM is driven instead: the copy is
        /// captured exactly as the LoadPlan postfix captures it and then offered to the route, which is the
        /// pair the open performs.  Either way the count of legs that LEFT this machine is the answer.</summary>
        private static string HqLogLoad(string id)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null || gi.logisticsManagerPlans == null) return "ERR no game instance here";
                Buildings.Office.Headquarters.LogisticsManagerPlan? pl = null;
                foreach (var x in gi.logisticsManagerPlans) if (x != null && x.id == id) { pl = x; break; }
                if (pl == null) return $"ERR no logistics plan '{id}' here";
                if (!CompanyLists.IsDisplayPlan(pl)) return $"ERR '{id}' is one of this machine's own plans - a load of it is not routed at all";
                long before = SharedShopWorkTabs.LegsSent;
                string how = "seam";
                var pane = PaneOf("logistics");
                var lists = pane == null ? null : pane.GetComponentInParent<UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagersPlanList>();
                bool ran = false;
                if (lists != null && lists.gameObject.activeInHierarchy)
                {
                    var parent = EntryParentOf(lists);
                    if (parent != null)
                        for (int i = 0; i < parent.childCount && !ran; i++)
                        {
                            var c = parent.GetChild(i);
                            if (!c.gameObject.activeSelf) continue;
                            var ec = EntryComponentOf(c);
                            if (PlanIdOfEntry(lists, ec!, c) != id) continue;
                            lists.SelectPlan(c, pl);
                            ran = true; how = "SelectPlan";
                        }
                }
                if (!ran)
                {
                    CompanyLists.CaptureLogisticsBaseline(pl);
                    CompanyLists.RouteDisplayPlanIfChanged(pl, "hqlog load lever");
                }
                return $"OK hqlog load sends={SharedShopWorkTabs.LegsSent - before} via={how}";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        /// <summary>HQ-PARITY-4 P5 - TEST LEVER, READ-ONLY.  What the mod believes about each of the five
        /// headquarters pages RIGHT NOW: which object it would use for that family's list and for its pane
        /// (the one the game registered when it last drew it, the one the hierarchy sweep finds, or none),
        /// whether each of those is on screen, and which plan the pane has open.  Nothing is drawn, nothing
        /// is written and nothing is sent - the rig draws no page, so this is the hands-on check.</summary>
        public static string TestDriveHqPane(string arg)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var fam in PaneFamilies)
                {
                    bool lreg = _listOf.TryGetValue(fam, out var lr) && lr != null;
                    Component? list = lreg ? lr : SweptListOf(fam);
                    bool preg = _paneOf.TryGetValue(fam, out var pr) && pr != null;
                    Component? pane = preg ? pr : SweptPaneOf(fam);
                    string openId = OpenPanePlanId(fam, pane);
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(fam).Append(": list=").Append(list == null ? "none" : (lreg ? "registered" : "swept"))
                      .Append(" active=").Append(list != null && list.gameObject.activeInHierarchy)
                      .Append(" pane=").Append(pane == null ? "none" : (preg ? "registered" : "swept"))
                      .Append(" active=").Append(pane != null && pane.gameObject.activeInHierarchy)
                      .Append(" open=").Append(openId.Length > 0 ? openId : "-");
                }
                return sb.ToString();
            }
            catch (Exception ex) { return "hqpane: " + ex.Message; }
        }

        /// <summary>HQ-PARITY-3 B6, THE PER-CONTROL OWNER LEVER: `hqtoggle &lt;family&gt; &lt;planId&gt; &lt;field&gt;
        /// [&lt;arg&gt;] &lt;value&gt;` makes the write the GAME's own handler makes on one of THIS machine's OWN
        /// plans and then takes the very seam every patched own-plan return now takes (RoutePaneEdit answers
        /// false for an own plan and calls OwnEditCommitted on the way), so the rig proves the owner-side
        /// publish CONTROL BY CONTROL instead of plan by plan.  The handlers themselves cannot be invoked
        /// headless - each needs a loaded pane whose label and scroller refreshes would throw with no screen -
        /// so what is driven is the write, at the same commitment point.
        /// Fields: purchasing repeating|autostock &lt;bool&gt;; hr replaceabsent &lt;bool&gt; | trainingtarget &lt;n&gt;;
        /// headhunter dealbreaker &lt;type&gt; &lt;bool&gt;.</summary>
        public static string TestDriveHqToggle(string arg)
        {
            try
            {
                var a = (arg ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (a.Length < 4) return "ERR usage: hqtoggle <purchasing|hr|headhunter> <planId> <field> [<arg>] <value>";
                string fam = a[0].ToLowerInvariant(), id = a[1], field = a[2].ToLowerInvariant();
                string last = a[a.Length - 1];
                bool bv = string.Equals(last, "true", StringComparison.OrdinalIgnoreCase);
                var gi = SaveGameManager.Current;
                if (gi == null) return "ERR no game instance here";
                switch (fam)
                {
                    case "purchasing":
                    {
                        if (gi.importPartnerships == null) return "ERR no purchasing plans here";
                        foreach (var ip in gi.importPartnerships)
                        {
                            if (ip == null || ip.id != id) continue;
                            if (CompanyLists.IsDisplayPlan(ip) || IsOverlayPlan(ip)) return $"ERR '{id}' is a partner's copy here";
                            // decompile PurchasingAgentPlanUI.cs:276-286, the two toggle handlers' own writes
                            if (field == "repeating") ip.isRepeatingOrder = bv;
                            else if (field == "autostock") ip.isTarget = bv;
                            else return $"ERR no purchasing field '{field}'";
                            SaveGameManager.MarkChange();
                            RoutePaneEdit("purchasing", "hqtoggle lever", ip, field);
                            return $"OK hqtoggle purchasing {id} {field}={bv} (own plan; published at the urgent cadence)";
                        }
                        return $"ERR no purchasing plan '{id}' of my own here";
                    }
                    case "hr":
                    {
                        if (gi.hrManagerPlans == null) return "ERR no hr plans here";
                        foreach (var pl in gi.hrManagerPlans)
                        {
                            if (pl == null || pl.id != id) continue;
                            if (IsOverlayPlan(pl)) return $"ERR '{id}' is a partner's copy here";
                            // decompile HrManagerPlanUI.cs:86-89 and :92-97, the two lambdas' own writes
                            // FOLD b B8: `echo` is the value AS THE PLAN NOW HOLDS IT, not the raw token -
                            // purchasing prints bool.ToString() ("True") and this printed "true", so the rig
                            // needed two spellings for one thing; the int is printed CLAMPED, which is what
                            // was actually written.
                            string echo;
                            if (field == "replaceabsent") { pl.replaceAbsentEmployees = bv; echo = bv.ToString(); }
                            else if (field == "trainingtarget")
                            {
                                if (!int.TryParse(last, out var n)) return "ERR usage: hqtoggle hr <planId> trainingtarget <0-100>";
                                pl.trainingTarget = n < 0 ? 0 : (n > 100 ? 100 : n);
                                echo = pl.trainingTarget.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            }
                            else return $"ERR no hr field '{field}'";
                            SaveGameManager.MarkChange();
                            RoutePaneEdit("hr", "hqtoggle lever", pl, field);
                            return $"OK hqtoggle hr {id} {field}={echo} (own plan; published at the urgent cadence)";
                        }
                        return $"ERR no hr plan '{id}' of my own here";
                    }
                    case "headhunter":
                    {
                        if (gi.headhunterPlans == null) return "ERR no headhunter plans here";
                        if (field != "dealbreaker") return $"ERR no headhunter field '{field}'";
                        if (a.Length < 5) return "ERR usage: hqtoggle headhunter <planId> dealbreaker <type> <bool>";
                        string type = a[3];
                        foreach (var pl in gi.headhunterPlans)
                        {
                            if (pl == null || pl.id != id) continue;
                            if (IsOverlayPlan(pl)) return $"ERR '{id}' is a partner's copy here";
                            if (pl.dealBreakerTypes == null) return $"ERR plan '{id}' holds no deal-breaker list";
                            // decompile HeadhuntersRecruitingTab.cs:328-337, ToggleDealBreaker's own writes
                            if (bv) { if (!pl.dealBreakerTypes.Contains(type)) pl.dealBreakerTypes.Add(type); }
                            else pl.dealBreakerTypes.Remove(type);
                            SaveGameManager.MarkChange();
                            RoutePaneEdit("headhunter", "hqtoggle lever", pl, "settings");
                            return $"OK hqtoggle headhunter {id} dealbreaker {type}={bv} count={pl.dealBreakerTypes.Count} (own plan; published at the urgent cadence)";
                        }
                        return $"ERR no headhunter plan '{id}' of my own here";
                    }
                }
                return $"ERR no family '{fam}'";
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

        // ── HQ-PARITY-2 P2: THE DISPLAY SCOPE ────────────────────────────────────

        /// <summary>HQ-PARITY-2 FOLD b3 — THE PANE REGISTER.  The call-window scope object is gone.  It
        /// was opened by a prefix on LogisticsManagerPlanUI.LoadPlan and closed in that patch's finalizer, so
        /// it answered for the FIRST draw of a plan and for nothing after it: AddDestination (:227-237) and
        /// UpdateSelectedBusiness (:566-575) redraw rows from a CLICK handler with no LoadPlan around them,
        /// and both read the two patched game methods - which is why a destination added on a partner's plan
        /// drew at half alpha with the add button dead, and a re-picked destination drew stale local stock
        /// (review MAJOR-4/5).  What is remembered now is only WHICH PANE OBJECT is drawing; the answers below
        /// follow the PLAN.  A destroyed pane compares equal to null through UnityEngine.Object's operator.</summary>
        private static Component? _paneUI;
        private static FieldInfo? _paneCurrentPlanField;

        /// <summary>Called from the LoadPlan postfix with that patch's `__instance`.</summary>
        public static void NoteLogisticsPane(object ui) { _paneUI = ui as Component; }

        /// <summary>The cheap first test the pallet-count prefix runs before it resolves an address key:
        /// false off a merger and false until a logistics pane has drawn at least once on this machine.</summary>
        public static bool LogisticsPaneTracked => MergerFlip.FlippedCount != 0 && _paneUI != null;

        /// <summary>The plan the logistics pane is showing RIGHT NOW, if it is a PARTNER's copy.  A
        /// LIVE read of the pane's own `_currentPlan` (LogisticsManagerPlanUI.cs:71) - not a copy taken at
        /// draw time - so the answer is the plan as it stands at the moment of the question, which is what
        /// the MarkChange commit seam needs.  Null = no pane, a hidden pane, or one of this machine's own
        /// plans.
        /// HQ-PARITY-7 R1 - RECOGNISED BY ITS ID, NOT ONLY BY OBJECT IDENTITY.  The identity tag that
        /// CompanyLists.IsDisplayPlan tests is carried by the plan OBJECT, so a copy LIFTED by a re-install
        /// keeps its id but loses the tag, and the hands-on run had every stock figure drop to 0 until the
        /// plan was re-selected - silently, because the miss diagnostic is gated on the very test that
        /// failed.  THE WINDOW: the game's destination remove handler calls `LoadPlan(_currentPlan)` at once
        /// (decompile LogisticsManagerDestinationUI.cs:57-62) while the owner's urgent bundle re-installs the
        /// copies about two seconds later, so for those seconds the pane holds an object the installer has
        /// already replaced.  A published DTO for the same id is therefore the second way in, and it can
        /// never admit one of this machine's OWN plans: Receive drops its own pid before anything reaches
        /// `_byOwner` (:85), so an own plan has no DTO here at all.</summary>
        public static Buildings.Office.Headquarters.LogisticsManagerPlan? PaneDisplayPlan()
        {
            try
            {
                var ui = _paneUI;
                if (ui == null || !ui.gameObject.activeInHierarchy) return null;
                if (_paneCurrentPlanField == null)
                    _paneCurrentPlanField = ui.GetType().GetField("_currentPlan", BindingFlags.Instance | BindingFlags.NonPublic);
                var pl = _paneCurrentPlanField == null
                       ? null : _paneCurrentPlanField.GetValue(ui) as Buildings.Office.Headquarters.LogisticsManagerPlan;
                if (pl == null) return null;
                // R1 (fold b/c, review HIGH + re-check): the id fallback must never admit a plan this
                // machine RUNS.  While it stands in for an absent partner (MergerAbsence), that partner's
                // REAL plans are installed here under the same ids - their live pallets are the truth, not
                // the absent owner's frozen published figures, which _byOwner still holds.  The authoritative
                // state is MergerAbsence's own stand-in registry, keyed by the headquarters address: the
                // _suspended set is filled only when the owner's bundle arrived BEFORE the absence mark
                // (SuspendOwner returns early otherwise), so it alone cannot be the gate.
                if (!CompanyLists.IsDisplayPlan(pl))
                {
                    if (string.IsNullOrEmpty(pl.id)) return null;
                    string hq = ""; try { hq = KeyOf(pl.headquartersAddress); } catch { }
                    if (hq.Length > 0 && MergerAbsence.SimulatesHere(hq)) return null;   // run here on the absent owner's behalf
                    if (LogisticsDtoOfActive(pl.id) == null) return null;
                }
                return pl;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] logistics pane read: {ex.Message}"); return null; }
        }

        /// <summary>The OWNER's published DTO for one plan id, out of the registry the fan-out fills.
        /// INTERNAL since HQ-PARITY-5 C3: the product-list postfix in MPPatches reads SourceProducts off it.</summary>
        internal static PwLogisticsPlan? LogisticsDtoOf(string planId)
        {
            foreach (var kv in _byOwner)
                foreach (var g in kv.Value?.LogisticsManagerPlans ?? new List<PwLogisticsPlan>())
                    if (g != null && string.Equals(g.Id, planId, StringComparison.Ordinal)) return g;
            return null;
        }

        /// <summary>HQ-PARITY-7 fold b: the same lookup, but an owner whose overlay is SUSPENDED (this
        /// machine stands in for them and runs their real plans) is skipped - the pane's plan is then this
        /// machine's own to run, never a partner's copy.</summary>
        internal static PwLogisticsPlan? LogisticsDtoOfActive(string planId)
        {
            foreach (var kv in _byOwner)
            {
                if (_suspended.Contains(kv.Key)) continue;
                foreach (var g in kv.Value?.LogisticsManagerPlans ?? new List<PwLogisticsPlan>())
                    if (g != null && string.Equals(g.Id, planId, StringComparison.Ordinal)) return g;
            }
            return null;
        }

        /// <summary>PREFIX (a)'s answer: the owner's capacity for ANY tagged display copy whose owner has
        /// published numbers - asked from LoadPlan's draw, from AddDestination's click handler (:235) and
        /// from AddDestinationEntry (:265), and all three get the same answer now.  False = off a merger, not
        /// a display copy, or nothing published for it yet: the native getter runs.</summary>
        public static bool ScopedMaxDestinations(object plan, out int max)
        {
            max = 0;
            if (MergerFlip.FlippedCount == 0) return false;
            var lg = plan as Buildings.Office.Headquarters.LogisticsManagerPlan;
            if (lg == null || string.IsNullOrEmpty(lg.id) || !CompanyLists.IsDisplayPlan(lg)) return false;
            var dto = LogisticsDtoOf(lg.id);
            if (dto != null) { max = dto.MaxDestinations; _lastMax[lg.id] = max; return true; }
            // HQ-PARITY-3 A6: a plan whose owner's DTO is momentarily absent - between a dissolve-and-rejoin,
            // or in the gap while a bundle is being rebuilt - used to fall through to the NATIVE getter, and
            // the native answer on a co-member is always 0 because a partner's VehicleInstances are not in
            // this machine's save list: every destination row greyed and the add button died.  The last number
            // the owner published answers instead.  Only a plan this machine has NEVER seen a number for
            // reaches native.
            if (_lastMax.TryGetValue(lg.id, out var seen)) { max = seen; return true; }
            return false;
        }

        /// <summary>A6: plan id -> the last capacity its owner published.  Cleared with the registry itself,
        /// so a dissolved company leaves nothing behind.</summary>
        private static readonly Dictionary<string, int> _lastMax = new(StringComparer.Ordinal);

        /// <summary>PREFIX (b)'s answer: the owner's pallet count for the SOURCE WAREHOUSE OF THE PLAN THE
        /// PANE IS SHOWING and for no other address; every other address falls through to the native count.
        /// An item missing from the list is answered ZERO, because the owner's list holds every item that
        /// warehouse actually holds - falling through there would draw this machine's stale replica of a
        /// partner's building.</summary>
        public static bool ScopedStock(string addressKey, string itemName, out int count)
        {
            count = 0;
            var pl = PaneDisplayPlan();
            if (pl == null || string.IsNullOrEmpty(pl.id)) return false;
            var dto = LogisticsDtoOf(pl.id);
            if (dto == null || string.IsNullOrEmpty(dto.TargetAddressKey) || !Same(addressKey, dto.TargetAddressKey)) return false;
            if (!string.IsNullOrEmpty(itemName))
                foreach (var ln in dto.Stock ?? new List<PwStockLine>())
                    if (ln != null && string.Equals(ln.ItemName, itemName, StringComparison.Ordinal)) { count = ln.Count; break; }
            return true;
        }

        /// <summary>HQ-PARITY-6 P3, DIAGNOSTIC ONLY.  Why ScopedStock did NOT answer for the plan the pane is
        /// showing - the field runs had the factory tab drawing 0 for every product and the warehouse tab
        /// dropping to 0 after a destination removal, with no log line for either. NULL means the
        /// substitution was in order (the caller then says nothing). Called only on the miss, never on the
        /// happy path, so the strings it builds cost nothing while the numbers are right.</summary>
        internal static string? ScopedStockReason(string planId, string addressKey, string itemName)
        {
            try
            {
                if (!LogisticsPaneTracked) return "no logistics pane tracked";
                var dto = LogisticsDtoOf(planId ?? "");
                if (dto == null) return "dto missing";
                if (string.IsNullOrEmpty(dto.TargetAddressKey) || !Same(addressKey, dto.TargetAddressKey))
                    return $"dto target {dto.TargetAddressKey} != {addressKey}";
                if (dto.Stock == null || dto.Stock.Count == 0) return "dto has 0 stock lines";
                foreach (var ln in dto.Stock)
                    if (ln != null && string.Equals(ln.ItemName, itemName ?? "", StringComparison.Ordinal)) return null;
                return $"item {itemName} has no stock line";
            }
            catch (Exception ex) { return "reason unavailable: " + ex.Message; }
        }

        /// <summary>HQ-PARITY-3 A5, RUNS OUT IN.  `LogisticsManagerPlan.GetRunsOutIn` (decompile :180-195)
        /// sums the LOCAL seven-day `orderHistory` of every BuildingRegistration - a partner's sales are not in
        /// this machine's registrations at all, so the pane drew "never" (-2) beside a stock figure that IS the
        /// owner's.  Gated exactly as ScopedStock is: the plan asked about must be the very display copy the
        /// pane is showing.  The returns are the decompile's own: -1 for no stock, -2 for nothing sold, else
        /// ceil(stock / (sold / 7)).</summary>
        public static bool ScopedRunsOutIn(object plan, string product, int currentStock, out int days)
        {
            days = 0;
            try
            {
                var pane = PaneDisplayPlan();
                if (pane == null || !ReferenceEquals(pane, plan)) return false;
                var dto = LogisticsDtoOf(pane.id ?? "");
                if (dto == null) return false;
                if (currentStock == 0) { days = -1; return true; }        // decompile :182-185
                int sold = 0;
                foreach (var ln in dto.Stock ?? new List<PwStockLine>())
                    if (ln != null && string.Equals(ln.ItemName, product, StringComparison.Ordinal)) { sold = ln.SoldPerWeek; break; }
                if (sold <= 0) { days = -2; return true; }                // decompile :191-194
                days = (int)Mathf.Ceil(currentStock / ((float)sold / 7f));
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] logistics runs-out-in: {ex.Message}"); return false; }
        }

        /// <summary>HQ-PARITY-3 A7, PURCHASING STOCK.  `PurchasingAgentProductModel.UpdateWarehouse` (decompile
        /// :66-70) counts THIS machine's pallets in the product's assigned warehouse, which for a partner's
        /// warehouse is a replica seeded once at world-live.  The gate is the PURCHASING pane: it is active,
        /// its `_currentImportPartnership` (read live, decompile :55) is one of the registry's display rows,
        /// and the address asked about is one of that partnership's own assigned warehouses.  An item absent
        /// from the owner's list is zero at the owner, so it is answered zero here rather than falling through
        /// to the replica.</summary>
        private static FieldInfo? _purchPlanField;

        /// <summary>FOLD b B4 (review F5): true only while `PurchasingAgentProductModel.UpdateWarehouse`
        /// (decompile :63-67) is on the stack - the ONE caller of CountResourcesInPallets that draws the
        /// purchasing pane's own stock figure.  Before this, the purchasing answer was consulted for EVERY
        /// caller while a partner's purchasing plan happened to be showing, including the simulation's
        /// Entities/ImportProduct.cs:45 and Entities/Warehouse.cs:121, so local ordering maths could be done
        /// with the OWNER's pallet count.  Set by a prefix and cleared by a FINALIZER, which Harmony 2.3.3
        /// runs on the throwing path too (a postfix does not).</summary>
        public static bool PurchasingModelScope;

        public static bool ScopedPurchasingStock(string addressKey, string itemName, out int count)
        {
            count = 0;
            try
            {
                if (_byOwner.Count == 0 || string.IsNullOrEmpty(addressKey)) return false;
                var pane = PaneOf("purchasing");
                if (pane == null || !pane.gameObject.activeInHierarchy) return false;
                if (_purchPlanField == null)
                    _purchPlanField = pane.GetType().GetField("_currentImportPartnership", BindingFlags.Instance | BindingFlags.NonPublic);
                var ip = _purchPlanField != null ? _purchPlanField.GetValue(pane) as ImportPartnership : null;
                if (ip == null || !IsOverlayPlan(ip)) return false;
                var dto = PurchasingDtoOf(ip.id ?? "");
                if (dto == null) return false;
                bool mine = false;
                foreach (var ln in dto.Products ?? new List<PwItemOrderLine>())
                    if (ln != null && Same(ln.AssignedWarehouseKey, addressKey)) { mine = true; break; }
                if (!mine) return false;
                foreach (var st in dto.Stock ?? new List<PwStockLine>())
                    if (st != null && Same(st.AddressKey, addressKey)
                        && string.Equals(st.ItemName, itemName ?? "", StringComparison.Ordinal)) { count = st.Count; return true; }
                return true;   // the owner holds none of it there
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] purchasing stock: {ex.Message}"); return false; }
        }

        private static PwImportPartnership? PurchasingDtoOf(string planId)
        {
            if (string.IsNullOrEmpty(planId)) return null;
            foreach (var kv in _byOwner)
                foreach (var g in kv.Value?.ImportPartnerships ?? new List<PwImportPartnership>())
                    if (g != null && string.Equals(g.Id, planId, StringComparison.Ordinal)) return g;
            return null;
        }

        /// <summary>FOLD b G1: the owner's published ROW SET for one family, serialised - the test the
        /// change-gated LIST rebuild runs.  The concatenation, in the owner's own list order, of the
        /// EDITABLE shape (DtoShapeOf) of every row of that family, so it moves when a row is added, removed,
        /// reordered or edited and stays still while the simulation turns the counters those shapes blank.
        /// "" = that owner publishes no row of this family; "?" - which is never equal to a real shape - =
        /// the shape could not be read, and an unreadable shape REDRAWS rather than skipping.</summary>
        private static string RowSetShapeOf(string ownerPid, string family)
        {
            try
            {
                if (string.IsNullOrEmpty(ownerPid) || string.IsNullOrEmpty(family)) return "?";
                if (!_byOwner.TryGetValue(ownerPid, out var p) || p == null) return "";
                var sb = new System.Text.StringBuilder();
                switch (family)
                {
                    case "pricing":
                        foreach (var g in p.PricingManagerPlans ?? new List<PwPricingPlan>()) AppendRowShape(sb, g?.Id, g);
                        break;
                    case "purchasing":
                        foreach (var g in p.ImportPartnerships ?? new List<PwImportPartnership>()) AppendRowShape(sb, g?.Id, g);
                        break;
                    case "hr":
                        foreach (var g in p.HrManagerPlans ?? new List<PwHrPlan>()) AppendRowShape(sb, g?.Id, g);
                        break;
                    case "headhunter":
                        foreach (var g in p.HeadhunterPlans ?? new List<PwHeadhunterPlan>()) AppendRowShape(sb, g?.Id, g);
                        break;
                    case "logistics":
                        foreach (var g in p.LogisticsManagerPlans ?? new List<PwLogisticsPlan>()) AppendRowShape(sb, g?.Id, g);
                        break;
                    default: return "?";
                }
                return sb.ToString();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] {family} row-set shape for '{ownerPid}': {ex.Message}"); return "?"; }
        }

        /// <summary>G1: one row of the row-set shape - the id (so a REORDER moves the shape) and the row's
        /// own editable shape.  A row with no id is not drawable and is skipped.  FOLD c I4 (review H5): the
        /// ROW ITSELF is passed, so the shape is taken off the object already in hand instead of rescanning
        /// every owner's list of this family for its id, once per row.</summary>
        private static void AppendRowShape(System.Text.StringBuilder sb, string? id, object? row)
        {
            if (string.IsNullOrEmpty(id)) return;
            sb.Append(id).Append('=').Append(DtoShapeOf(row)).Append('\u0001');
        }

        /// <summary>FOLD c I4: the editable shape of a row ALREADY IN HAND.  The id-based overload below is
        /// this preceded by a lookup, and a caller holding the row must not pay for that lookup.  null (no
        /// such row) serialises as "", exactly as the lookup's own miss does.</summary>
        private static string DtoShapeOf(object? row)
        {
            try
            {
                if (row == null) return "";
                return Newtonsoft.Json.JsonConvert.SerializeObject(EditableShapeOf(row));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] row shape: {ex.Message}"); return ""; }
        }

        /// <summary>FOLD b B1: the owner's published row for one plan, serialised - the test the change-gated
        /// refresh runs.  "" = the registry holds no such plan (one of this machine's own, or an owner whose
        /// bundle has not arrived), and "" compares equal to "" so such a pane is refreshed once and then
        /// left alone until the feed really carries it.
        /// FOLD c C2 (review F2): the shape is the EDITABLE shape - "what a CONTROL writes changed", not
        /// "the row changed".  The rows carry live simulation counters (warehouse stock and its
        /// sold-per-week, the pricing clock and its cached suggestions, the headhunter's remaining
        /// candidates), and keying on those redrew an open pane - losing its scroll, re-collapsing its rows
        /// and recomputing its suggestions - while the business merely traded.  With them blanked an open
        /// page still follows every edit the moment it lands, and the stock figures it shows are those of its
        /// LAST LOAD - exactly what the owner sees on their own page, whose native load draws them once.</summary>
        private static string DtoShapeOf(string family, string planId)
        {
            try
            {
                if (string.IsNullOrEmpty(family) || string.IsNullOrEmpty(planId)) return "";
                foreach (var kv in _byOwner)
                {
                    var p = kv.Value;
                    if (p == null) continue;
                    object? row = null;
                    switch (family)
                    {
                        case "pricing":
                            foreach (var g in p.PricingManagerPlans ?? new List<PwPricingPlan>())
                                if (g != null && string.Equals(g.Id, planId, StringComparison.Ordinal)) { row = g; break; }
                            break;
                        case "purchasing":
                            foreach (var g in p.ImportPartnerships ?? new List<PwImportPartnership>())
                                if (g != null && string.Equals(g.Id, planId, StringComparison.Ordinal)) { row = g; break; }
                            break;
                        case "hr":
                            foreach (var g in p.HrManagerPlans ?? new List<PwHrPlan>())
                                if (g != null && string.Equals(g.Id, planId, StringComparison.Ordinal)) { row = g; break; }
                            break;
                        case "headhunter":
                            foreach (var g in p.HeadhunterPlans ?? new List<PwHeadhunterPlan>())
                                if (g != null && string.Equals(g.Id, planId, StringComparison.Ordinal)) { row = g; break; }
                            break;
                        case "logistics":
                            foreach (var g in p.LogisticsManagerPlans ?? new List<PwLogisticsPlan>())
                                if (g != null && string.Equals(g.Id, planId, StringComparison.Ordinal)) { row = g; break; }
                            break;
                    }
                    if (row != null) return DtoShapeOf(row);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] {family} shape for {planId}: {ex.Message}"); }
            return "";
        }

        /// <summary>FOLD c C2: `MemberwiseClone` reached by reflection, so a row can be blanked for the shape
        /// test without a hand-written copy of every field - and so a field ADDED to a DTO later is copied
        /// without anybody remembering to come back here.  Null (the method could not be reached) means the
        /// caller serialises the original, which is the fold-b behaviour, never a wrong shape.</summary>
        private static readonly MethodInfo? _memberwiseClone =
            typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);

        private static T? ShallowCopy<T>(T row) where T : class
            => _memberwiseClone == null ? null : _memberwiseClone.Invoke(row, null) as T;

        /// <summary>FOLD c C2 (review F2): a SHALLOW COPY of the registry row with the fields no control
        /// writes - the ones the simulation moves on its own - blanked.  The row in `_byOwner` is never
        /// touched; only the copy is serialised.
        /// logistics `Stock` (pallets moving and the week's sales) and `SourceProducts` (FOLD b G2: the
        /// source warehouse's published product list, which gains and loses names with every delivery in and
        /// out, and whose churn rebuilt a partner's logistics rows and re-fired LoadPlan on the pane they
        /// were reading - exactly the churn blanking `Stock` was meant to stop) - `MaxDestinations` is KEPT, a capacity
        /// change is rare and is worth the redraw; purchasing `Stock` (the same counts, per partnership),
        /// `NextDeliveryDay` (every delivery that arrives moves it) and the two per-product week counters
        /// `AmountOrderedLastWeek`/`AmountOrderedThisWeek` (every order the owner's importer places moves
        /// them) - FOLD d E2, without which an open purchasing page of a partner reloaded while the owner
        /// merely traded.  That partnership's control-written fields (`DaysUntilRepeat`, `IsActive`,
        /// `IsRepeatingOrder`, `IsTarget`, `IsUrgentOrder`) all STAY: a run starting or ending is a state
        /// change worth the redraw.  A page follows what a CONTROL writes; an order placed or a delivery
        /// arriving is drawn at the page's next load, exactly as on the owner's own page;
        /// pricing `CachedSuggestions` and `NextUpdateDay`/`NextUpdateHour` (the pricing clock rolls an hour
        /// with nobody editing anything); headhunter `RemainingCandidatesToRecruit`, which the recruiting
        /// run decrements by itself - the control writes `AmountOfCandidatesToRecruitPreference`, and a
        /// start/stop moves `IsRecruiting`, so no edit loses its redraw.  `PwHrPlan` has NOTHING to blank:
        /// every one of its fields (the assigned list, the absent-replacement flag, the training target, the
        /// two insurance fields) is written by a control and by nothing else.</summary>
        private static object EditableShapeOf(object row)
        {
            try
            {
                switch (row)
                {
                    case PwLogisticsPlan lp:
                    {
                        var c = ShallowCopy(lp);
                        if (c != null) { c.Stock = null!; c.SourceProducts = null!; return c; }
                        break;
                    }
                    case PwImportPartnership ip:
                    {
                        var c = ShallowCopy(ip);
                        if (c != null)
                        {
                            c.Stock = null!;
                            c.NextDeliveryDay = 0;
                            // FOLD d E2: the shallow copy SHARES the registry row's product list, so the two
                            // week counters have to be blanked on COPIES of the lines - the original row and
                            // its lines are never touched.  Everything a control writes (the item, its box
                            // count, its target amount, its assigned warehouse) is carried across.
                            var lines = new List<PwItemOrderLine>();
                            foreach (var p in ip.Products ?? new List<PwItemOrderLine>())
                            {
                                if (p == null) continue;
                                lines.Add(new PwItemOrderLine
                                {
                                    ItemName             = p.ItemName,
                                    Boxes                = p.Boxes,
                                    Amount               = p.Amount,
                                    AssignedWarehouseKey = p.AssignedWarehouseKey,
                                });
                            }
                            c.Products = lines;
                            return c;
                        }
                        break;
                    }
                    case PwPricingPlan pp:
                    {
                        var c = ShallowCopy(pp);
                        if (c != null) { c.CachedSuggestions = null!; c.NextUpdateDay = 0; c.NextUpdateHour = 0; return c; }
                        break;
                    }
                    case PwHeadhunterPlan hh:
                    {
                        var c = ShallowCopy(hh);
                        if (c != null) { c.RemainingCandidatesToRecruit = 0; return c; }
                        break;
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] editable shape: {ex.Message}"); }
            return row;
        }

        // ── HQ-PARITY-2 P3: LOGISTICS EDITS AS SINGLE OPS ──────────────────────────────────

        /// <summary>FOLD b B3: the cap on ONE plan's diff.  Eight was small enough that an ordinary mass edit
        /// - a target typed on every item of a destination - fell off it and was silently dropped; the ops are
        /// tiny and idempotent at the runner, so the ceiling is now 64 and going over it is logged.</summary>
        private const int MaxLogisticsOps = 64;

        /// <summary>FOLD b B3 (review F3): what one diff DID.  `Nothing` and `Inexpressible` used to share
        /// `false`, and the caller treated both as "sent", marking a baseline pending that no echo could ever
        /// match - so re-seeding from the owner was blocked for three of the owner's bundles after a diff that
        /// had sent nothing at all.  Three answers, three different things for the caller to do.</summary>
        public enum LogisticsRoute { Nothing, Sent, Inexpressible }

        /// <summary>THE DIFF.  `was` is the owner's last-known shape, `now` is the display copy as the player
        /// just left it; what changed between them becomes ONE op per control, sent on the same
        /// `mergerplanedit` carrier the other four families use.  FOLD b B3, THREE ANSWERS: `Sent` = every
        /// difference was expressed and the ops are on the wire; `Nothing` = the two shapes agree on every
        /// control this diff carries, so nothing was sent and nothing is in flight; `Inexpressible` = the
        /// change cannot travel as ops and the caller leaves the owner's plan alone.  HQ-PARITY-5 B2 leaves
        /// exactly ONE inexpressible case: a diff of more than MaxLogisticsOps ops, which is logged.  The
        /// destinations no longer diff into index ops at all - any difference in count, order, address key or
        /// target amount becomes ONE `destset` carrying the whole desired list, so a reorder CAN travel in
        /// one.  FOLD b G6 (review): none is produced today, though - the display copy's drag is still
        /// refused at the source (src/MPPatches.cs:4653 Patch_LogisticsReorder_DisplayRefuse, and :4598
        /// StripDestinationDrag takes the handles off), so a reordered list never reaches this diff on a
        /// co-member.  The carrier is ready; the control is not open yet.
        /// The controls are NOT patched one by one: three of the six (the destination remove button, the
        /// add-destination button, the per-item target field) are anonymous delegates built inside
        /// LogisticsManagerDestinationUI.SetUp and LogisticsManagerPlanUI.LoadProducts and have no method to
        /// patch.  Only the remove button comes back through LoadPlan; the target field (:387/:406/:424) and
        /// AddDestination (:227-237) end at SaveGameManager.MarkChange, and BOTH seams call
        /// CompanyLists.RouteDisplayPlanIfChanged, which is where this diff sits.</summary>
        public static LogisticsRoute RouteLogisticsOps(PwLogisticsPlan? was, PwLogisticsPlan? now, string owner, string why)
        {
            try
            {
                if (was == null || now == null) return LogisticsRoute.Inexpressible;
                string id = now.Id ?? "", hq = now.HeadquartersAddressKey ?? "";
                if (id.Length == 0 || hq.Length == 0) return LogisticsRoute.Inexpressible;
                var ops = new List<LogOp>();
                if (!Same(was.AssignedEmployeeId, now.AssignedEmployeeId))
                    ops.Add(new LogOp { Op = "manager", S = now.AssignedEmployeeId ?? "" });
                if (!Same(was.TargetAddressKey, now.TargetAddressKey))
                    ops.Add(new LogOp { Op = "warehouse", S = now.TargetAddressKey ?? "" });
                var wd = was.Destinations ?? new List<PwLogisticsDestination>();
                var nd = now.Destinations ?? new List<PwLogisticsDestination>();
                // HQ-PARITY-5 B2: THE DESTINATIONS TRAVEL AS ONE LIST.  What stood here diffed the two
                // snapshots into destchange / destremove / destadd / target ops, each naming a row by its
                // INDEX in the sender's copy (`Iv = a - removed`), and the runner applied them with
                // RemoveAt(i) / [i] against its own LIVE list.  Any gap between the two - a fan-out in
                // flight, an edit the owner made in the same breath, a refusal already rolled back - put the
                // change on the wrong destination, silently.  One `destset` carries the whole desired list
                // instead; the runner matches it against its own BY ADDRESS KEY, so a row that is still
                // wanted keeps its object and its runtime state, and a position is never an identity.
                if (DestinationsDiffer(wd, nd))
                    ops.Add(new LogOp { Op = "destset", D = nd });
                // HQ-PARITY-3 A2: "NOTHING TO EXPRESS" IS NOT "CANNOT EXPRESS".  A zero-op diff used to
                // answer false, and false was the whole-plan `mergerplan` leg - which is how merely OPENING a
                // partner's plan replaced the owner's real plan object from the sender's copy.  Nothing
                // changed means nothing is sent, and the caller is told the diff succeeded.
                if (ops.Count == 0)
                {
                    _loggedOverCap.Remove(id);   // FOLD c C4 (review F5): a plan that diffs to nothing is no
                                                 // longer over the cap, so its warning is re-armed for the
                                                 // next diff that really does go over.
                    if (_loggedNothingToSend.Add(id))
                        Plugin.Logger.LogInfo($"[Plans] logistics plan {id}: nothing to send - the copy matches the baseline ({why}).");
                    return LogisticsRoute.Nothing;
                }
                if (ops.Count > MaxLogisticsOps)
                {
                    if (_loggedOverCap.Add(id))
                        Plugin.Logger.LogWarning($"[Plans] logistics plan {id}: {ops.Count} ops is over the {MaxLogisticsOps} cap ({why}) - "
                                               + "nothing sent; the owner's plan is left alone.");
                    return LogisticsRoute.Inexpressible;
                }
                _loggedNothingToSend.Remove(id);
                _loggedOverCap.Remove(id);
                foreach (var o in ops) Send("logistics", id, hq, owner ?? "", o.Op, why ?? "", null, o.S, 0, 0f, false, "", o.D);
                return LogisticsRoute.Sent;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] logistics op diff: {ex.Message}"); return LogisticsRoute.Inexpressible; }
        }

        private sealed class LogOp
        {
            public string Op = "";
            public string S = "";
            /// <summary>B2: `destset`'s whole desired destination list; null on every other op.</summary>
            public List<PwLogisticsDestination>? D;
            // The `Iv` index and the `St` destination-index-as-string that used to sit here went with the
            // index ops (HQ-PARITY-5 B2): manager and warehouse carry a string, destset carries a list, and
            // nothing a logistics diff emits names a position any more.
        }

        /// <summary>B2.  Do the two destination lists differ in COUNT, in ORDER, in any address key, or in
        /// any target amount?  The amount test is TargetChanges, the same comparison the per-row `target` op
        /// used to be derived from.  True = send one `destset` with the new list.</summary>
        private static bool DestinationsDiffer(List<PwLogisticsDestination> wd, List<PwLogisticsDestination> nd)
        {
            if (wd.Count != nd.Count) return true;
            for (int i = 0; i < nd.Count; i++)
            {
                if (!Same(wd[i] == null ? "" : wd[i].DeliveryTargetAddressKey,
                          nd[i] == null ? "" : nd[i].DeliveryTargetAddressKey)) return true;
                if (TargetChanges(wd[i], nd[i]).Count > 0) return true;
            }
            return false;
        }

        /// <summary>A2: one "nothing to send" line per plan id, cleared the moment that plan really does send
        /// something, so the next quiet stretch says so again.</summary>
        private static readonly HashSet<string> _loggedNothingToSend = new(StringComparer.Ordinal);

        /// <summary>B3: one WARNING per plan whose diff went over the op cap, cleared the moment that plan
        /// does send something.</summary>
        private static readonly HashSet<string> _loggedOverCap = new(StringComparer.Ordinal);

        // FOLD b's IsReorder / SameDest are GONE (HQ-PARITY-5 B2): both existed only to answer "this is a
        // pure re-ordering, refuse it, because destchange's Reset() would wipe every moved row's runtime
        // state".  There is no destchange any more - a reorder is just a different list inside one `destset`,
        // and the runner re-uses each row's existing object by address key - so there is nothing left to
        // refuse and nothing left to compare positionally.  FOLD b G6 (review): that is the CARRIER's
        // answer.  No reorder actually arrives today, because the display copy's drag is refused at the
        // source (src/MPPatches.cs:4653 Patch_LogisticsReorder_DisplayRefuse, with :4598 StripDestinationDrag
        // removing the handles); a destset simply carries one already if that refusal is ever lifted.

        /// <summary>item -> its NEW amount, for every stock target that differs between the two destinations.
        /// A target removed is amount 0, which is exactly what the pane writes (LogisticsManagerPlanUI
        /// :376-379 removes the entry when the field reaches zero).</summary>
        private static Dictionary<string, int> TargetChanges(PwLogisticsDestination? a, PwLogisticsDestination? b)
        {
            var had = new Dictionary<string, int>(StringComparer.Ordinal);
            var cur = new Dictionary<string, int>(StringComparer.Ordinal);
            if (a != null)
                foreach (var t in a.StockTargets ?? new List<PwItemOrderLine>())
                    if (t != null && !string.IsNullOrEmpty(t.ItemName)) had[t.ItemName] = t.Amount;
            if (b != null)
                foreach (var t in b.StockTargets ?? new List<PwItemOrderLine>())
                    if (t != null && !string.IsNullOrEmpty(t.ItemName)) cur[t.ItemName] = t.Amount;
            var diff = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kv in cur) { int w; had.TryGetValue(kv.Key, out w); if (w != kv.Value) diff[kv.Key] = kv.Value; }
            foreach (var kv in had) if (!cur.ContainsKey(kv.Key) && kv.Value != 0) diff[kv.Key] = 0;
            return diff;
        }

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
                // G1 (HQ-PARITY-8).  A LOGISTICS row is NOT a detached overlay row: it is a tagged display
                // INSTALL sitting in this machine's own gi.logisticsManagerPlans (CompanyLists.IsDisplayPlan),
                // so it is never in _rowOwner/_rowInfo and the overlay test below would call a partner's plan
                // "my own" and let the native body run on it.  The owner is read off the plan's own
                // headquarters exactly as the hqlog lever does (:2444-2450), and no plan DTO rides along - a
                // logistics leg carries its fields in StrValue / Destinations.
                if (string.Equals(family, "logistics", StringComparison.Ordinal))
                {
                    var lg = plan as Buildings.Office.Headquarters.LogisticsManagerPlan;
                    if (lg == null || !CompanyLists.IsDisplayPlan(lg)) { OwnEditCommitted("pane edit"); return false; }
                    string lhq = KeyOf(lg.headquartersAddress);
                    if (lhq.Length == 0) { Refused(family, op, lg.id ?? "?", "the plan names no headquarters here"); return true; }
                    CompanyLists.TryOwnerOfAddress(lhq, out var lowner);
                    if (mutate != null) { try { mutate(plan); } catch (Exception mx) { Plugin.Logger.LogWarning($"[Plans] {family} {op} display: {mx.Message}"); } }
                    return Send(family, lg.id ?? "", lhq, lowner ?? "", op, what, null, strValue, intValue, number, flag, station);
                }
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
        private static string _confirmFamily = "", _confirmOp = "", _confirmWhat = "", _confirmValue = "";
        // FOLD b M3: the DISPLAY write that goes with the armed edit.  A no-op by default, so the purchasing
        // pane's own commits (which have no display leg) need no delegate and nothing here can be null.
        private static Action<object> _confirmMutate = _ => { };

        /// <summary>True = armed, the native body must run.  False = not a partner's row (nothing changes).
        /// G3 (HQ-PARITY-8): `value` is what the routed leg has to carry in StrValue - the chosen employee id
        /// for a manager/agent dropdown whose change the GAME itself puts behind a confirmation.  It stays
        /// empty for the purchasing pane commits, whose ops carry nothing.
        /// FOLD b M3: `mutate` is the same DISPLAY write the direct route passes to RoutePaneEdit.  Without it
        /// the confirmed leg dropped the optimistic copy write and the dropdown reverted at the next rebuild
        /// until the owner published.</summary>
        public static bool ArmConfirmRoute(string family, object plan, string op, string what, string value = "",
                                           Action<object>? mutate = null)
        {
            try
            {
                // HQ-PARITY-1 P5a: `end` and `urgent` on a purchasing partnership are the two pane commits
                // that reach the routing layer HERE and not through RoutePaneEdit, so an OWN plan's confirm
                // would otherwise publish at the 30 s cadence.  Same one hook.
                if (!IsOverlayPlan(plan) || !_rowInfo.ContainsKey(plan)) { OwnEditCommitted("confirmed pane edit"); return false; }
                _confirmRow = plan; _confirmFamily = family ?? ""; _confirmOp = op ?? ""; _confirmWhat = what ?? ""; _confirmValue = value ?? "";
                _confirmMutate = mutate ?? (_ => { });
                return true;
            }
            catch { DisarmConfirmRoute(); return false; }
        }

        public static void DisarmConfirmRoute()
        { _confirmRow = null; _confirmFamily = ""; _confirmOp = ""; _confirmWhat = ""; _confirmValue = ""; _confirmMutate = _ => { }; }

        /// <summary>THE WRAPPER'S SIDE.  Null = nothing armed, leave the dialog's own callback alone.  A
        /// delegate = use this INSTEAD: the routed send, taken once so a nested dialog cannot take it twice.</summary>
        public static Action TakeConfirmRoute()
        {
            object row = _confirmRow;
            if (row == null) return null;
            string fam = _confirmFamily, op = _confirmOp, what = _confirmWhat, val = _confirmValue;
            var mut = _confirmMutate;                                   // FOLD b M3: taken with the rest
            DisarmConfirmRoute();
            return () =>
            {
                try
                {
                    // G3: one line per family the first time a confirmed edit really leaves - the player has
                    // just answered the GAME's own dialog on a partner's row, and the log says where it went.
                    if (_refusalLogged.Add("confirmroute|" + fam))
                        Plugin.Logger.LogInfo($"[Merger] {fam}: the game's own confirmation was answered on a partner's row - the edit routes to that plan's owner (once per family).");
                    RoutePaneEdit(fam, what, row, op, val, 0, 0f, false, mut);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] {fam} {op} confirmed route: {ex.Message}"); }
            };
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
                                 object? dto, string strValue, int intValue, float number, bool flag,
                                 string stationId = "", List<PwLogisticsDestination>? destinations = null)
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
                Destinations = destinations,   // HQ-PARITY-5 B1/B2: `destset`'s whole list; null on every other op
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
        /// owner's truth rather than sitting on an edit that never happened.  FOLD c I2 / FOLD d: the refused
        /// PLAN'S owner's recorded shapes are DROPPED first - a refusal moves nothing in _byOwner, so without it the redraw found both
        /// change gates closed and did nothing at all.</summary>
        private static void Refused(string family, string op, string planId, string why)
        {
            Plugin.Logger.LogWarning($"[Merger] plan edit refused ({family} {op} {planId}): {why}.");
            try { if (CompanyHqPageOpen(out _, out var owner)) { InvalidateShapes(OwnerOfPlanRow(family, planId, owner)); RefreshOpenTabsFor(owner); } } catch { }
        }

        /// <summary>The member's answer leg: the runner could not apply the edit.  The rows on screen are
        /// redrawn from the registry, which still holds the owner's truth - FOLD c I2 / FOLD d: after the refused
        /// plan's owner's recorded shapes are dropped, since a refusal changes nothing in _byOwner and the change gates would
        /// otherwise skip both the list rebuild and the pane load.</summary>
        public static void ReceiveRefusal(string family, string planId, string reason)
        {
            Plugin.Logger.LogWarning($"[Merger] plan-edit-refused ({family} {planId}): {reason}.");
            // HQ-PARITY-7 R3 - A REFUSED ORDER IS SHOWN WITH THE GAME'S OWN NOTICE.  The two refusals the
            // owner's purchasing-start check can return (:4199 / :4204) are the same two cases the game
            // itself puts on screen when Order is pressed locally (decompile PurchasingAgentPlanUI.cs:238
            // and :243), so the sender sees the game's own localized notice instead of a WARNING line nobody
            // reads.  ONLY these two: every other reason stays log-only, its wording not yet settled.
            //
            // WHY THE MOD'S PREFIX CANNOT RUN THE GAME'S TWO CHECKS LOCALLY: the display copy's order lines
            // DO carry the owner's warehouse key (Protocol.cs:3721 PwItemOrderLine.AssignedWarehouseKey),
            // but it is resolved against THIS machine's registrations on install (:1844 AddrOf) and a key
            // that does not resolve leaves exactly the null a genuinely unassigned product leaves.  A local
            // check could not tell those apart and would refuse orders that are perfectly good at the owner,
            // so the owner's check is the only one that can answer.
            if (reason != null && reason.StartsWith("start: an ordered item has no warehouse assigned", StringComparison.Ordinal))
            {
                try { UI.Notification.Notifications.ShowError("bizman_purchasingagents_contact_notification_no_warehouse_assigned"); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] refusal notice: {ex.Message}"); }
            }
            else if (reason != null && reason.StartsWith("start: there are no items to deliver", StringComparison.Ordinal))
            {
                try { UI.Notification.Notifications.ShowError("bizman_delivery_notification_no_items_to_deliver"); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] refusal notice: {ex.Message}"); }
            }
            else if (reason != null && reason.Contains("too close to its delivery day"))
            {
                // The game's own notice for this rule (PurchasingAgentPlanUI.CancelOrder -> DeliveryHelper).
                try { DeliveryHelper.ShowCantModifyContractNotification(); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] refusal notice: {ex.Message}"); }
            }
            // FOLD b2: the routed logistics op advanced the OPTIMISTIC baseline on the way out.  A refusal
            // means the runner never applied it, so that baseline is now a shape the owner never held and the
            // next diff would be taken against a lie.  Re-seed it from the last RECEIVED DTO, so the next
            // edit on this plan sends the whole truth instead.
            if (string.Equals(family, "logistics", StringComparison.Ordinal))
                CompanyLists.ReseedLogisticsBaseline(planId ?? "");
            try { if (CompanyHqPageOpen(out _, out var owner)) { InvalidateShapes(OwnerOfPlanRow(family, planId ?? "", owner)); RefreshOpenTabsFor(owner); } } catch { }
        }

        /// <summary>FOLD d (re-check c, minor): the shapes to drop are the REFUSED PLAN'S owner's, not the page's -
        /// a refusal arrives asynchronously and the player may have switched to another partner's page in
        /// between.  The owner is read off the row registry (keys `owner|family|planId`); the page owner is the
        /// fallback when the row is no longer held.</summary>
        private static string OwnerOfPlanRow(string family, string planId, string fallback)
        {
            try
            {
                string tail = "|" + family + "|" + (planId ?? "");
                foreach (var kv in _rows)
                    if (kv.Key.EndsWith(tail, StringComparison.Ordinal))
                        return kv.Key.Substring(0, kv.Key.Length - tail.Length);
            }
            catch { }
            return fallback;
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
                    case "logistics":  ok = ApplyLogistics(gi, addr, p, op, id);  break;   // HQ-PARITY-2 P3
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
                // HQ-PARITY-3 A4: the RUNNER is excluded from its own fan-out (Receive :81), so nothing used
                // to redraw the owner's own open pane after a partner pressed a button on it - and for
                // logistics the pane went on holding the plan object the apply had replaced.  It follows the
                // edit now, through the one refresh every family uses (B1).
                // HQ-PARITY-5 A1: and so does the owner's own LIST.  The bare RefreshOpenPaneInPlace that
                // stood here redrew the pane alone, ran under a half-typed edit, and left the row set behind;
                // A1 does the family's own list refresh first and then that same pane refresh, or defers the
                // whole thing when a control is open.
                RefreshOwnTabsAfterRoutedEdit(fam, id, p.PlayerId ?? "", op);
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

        /// <summary>RUNNER, HQ-PARITY-2 P3.  One logistics op onto MY OWN plan, each with the write the
        /// game's own control makes: `manager` is ChangeLogisticsManager's single assignment
        /// (LogisticsManagersPlanList.cs:195), `warehouse` is OnChangedWarehouse's (LogisticsManagerPlanUI
        /// .cs:214/:220, and index 0 there is UnAssignAddress), and - HQ-PARITY-5 B3 - `destset` REPLACES
        /// THE WHOLE DESTINATION LIST from the leg, under the plan's own capacity test (:235).  The four
        /// index ops it replaces (destadd/destremove/destchange/target) are gone: each named a row by its
        /// position in the SENDER's snapshot and was applied here with RemoveAt(i) / [i] against this
        /// machine's LIVE list, so any gap between the two wrote the edit onto the wrong destination.
        /// `destset` matches the wanted list against the live one BY ADDRESS KEY, so a destination that is
        /// still wanted keeps its OWN object - and with it every bit of runtime state the game hangs off it -
        /// instead of being Reset() or rebuilt; only a key that is new builds a fresh row.  The stock targets
        /// are then re-seated row by row exactly as the pane's own field write does (:361-383, where zero
        /// REMOVES the entry).  ApplyRouted marks the save changed and publishes urgently after this returns,
        /// which is the LoadPlan-equivalent bookkeeping; the owner's own open tabs follow the edit through
        /// RefreshOwnTabsAfterRoutedEdit (HQ-PARITY-5 A1).</summary>
        private static bool ApplyLogistics(GameInstance gi, Address addr, SharedWorkEditPayload p, string op, string id)
        {
            if (gi.logisticsManagerPlans == null) return Gone("logistics", op, id);
            Buildings.Office.Headquarters.LogisticsManagerPlan? pl = null;
            foreach (var x in gi.logisticsManagerPlans) if (x != null && x.id == id) { pl = x; break; }
            if (pl == null) return Gone("logistics", op, id);
            if (CompanyLists.IsDisplayPlan(pl))
                return Refuse($"{op}: plan '{id}' is a display copy here - this machine does not run it");
            switch (op)
            {
                // G1 (HQ-PARITY-8): the pane's delete button (LogisticsManagerPlanUI.cs:239-247) deletes
                // through the helper and mutates no plan, so the change seam never saw it - the co-member's
                // delete of a partner's plan is this leg instead.  Mirrors purchasing's `delete` (:4326).
                // FOLD b M4 (record only): DeletePlan FORCE-COMPLETES every in-flight delivery on this machine (LogisticsManagerHelper.cs:69-71) - the game's own behaviour when the OWNER deletes, and a routed delete is the owner deleting.
                case "delete": LogisticsManagerHelper.DeletePlan(id); return true;
                case "manager":
                {
                    string eid = p.StrValue ?? "";
                    if (eid.Length == 0) { pl.UnAssignEmployee(); return true; }
                    if (InjectedHere(eid) && !CoMemberCopyHere(eid))
                        return Refuse($"manager: '{eid}' is an injected copy of somebody else's person here");
                    EmployeeInstance? e = null; try { e = EmployeeHelper.GetEmployeeById(eid); } catch { }
                    if (e == null) return Refuse($"manager: no employee '{eid}' on this machine");
                    pl.assignedEmployeeId = eid;
                    return true;
                }
                case "warehouse":
                {
                    string key = p.StrValue ?? "";
                    if (key.Length == 0) { pl.UnAssignAddress(); return true; }
                    var a = AddrOf(key);
                    if (a == null) return Refuse($"warehouse: '{key}' is not a building known here");
                    pl.targetAddress = a;
                    return true;
                }
                case "destset":
                {
                    // B3.  The whole desired list arrives at once.  Everything is CHECKED before anything is
                    // written, so a refusal leaves the plan exactly as it was.
                    if (p.Destinations == null) return Refuse("destset: the leg carried no destination list");
                    if (pl.destinations == null) return Refuse("destset: the plan holds no destination list");
                    // FOLD b G3 (review): CAPACITY IS REFUSED ONLY ON GROWTH.  MaxDestinations is LIVE - it
                    // is CalculateMaxDestinations(targetAddress, assignedEmployeeId) and answers 0 whenever
                    // the plan has no manager or no warehouse (decompile LogisticsManagerPlan.cs:43/:145) -
                    // so the flat test refused a pure AMOUNT change on a manager-less plan, whose list was
                    // not growing at all.  A list that is not getting longer cannot break a capacity that
                    // already holds it.
                    if (p.Destinations.Count > pl.destinations.Count && p.Destinations.Count > pl.MaxDestinations)
                        return Refuse($"destset: growing to {p.Destinations.Count} destination(s) from {pl.destinations.Count} is over the plan's capacity of {pl.MaxDestinations} here");
                    var keys = new List<string>();
                    var addrs = new List<Address?>();
                    foreach (var d in p.Destinations)
                    {
                        string key = d == null ? "" : (d.DeliveryTargetAddressKey ?? "");
                        Address? a = null;
                        if (key.Length > 0)
                        {
                            a = AddrOf(key);
                            if (a == null) return Refuse($"destset: '{key}' is not a building known here");
                        }
                        keys.Add(key); addrs.Add(a);
                    }
                    // MATCH BY ADDRESS KEY, first unused match in order: a row that is still wanted keeps its
                    // existing object (and its runtime state) wherever it has moved to in the list.
                    var spare = new List<Entities.LogisticsManagerPlanDestination>(pl.destinations);
                    var made = new List<Entities.LogisticsManagerPlanDestination>();
                    for (int i = 0; i < p.Destinations.Count; i++)
                    {
                        Entities.LogisticsManagerPlanDestination? dst = null;
                        for (int k = 0; k < spare.Count; k++)
                        {
                            if (spare[k] == null || !Same(KeyOf(spare[k].deliveryTargetAddress), keys[i])) continue;
                            dst = spare[k]; spare.RemoveAt(k); break;
                        }
                        if (dst == null)
                            dst = new Entities.LogisticsManagerPlanDestination { isUiCollapsed = false, deliveryTargetAddress = addrs[i] };
                        if (dst.stockTargets == null) dst.stockTargets = new List<BigAmbitions.Items.ItemAmountTarget>();
                        SetStockTargets(dst, p.Destinations[i]);
                        made.Add(dst);
                    }
                    // The SAME list instance: the pane, the plan's own delivery run and the save all hold it.
                    pl.destinations.Clear();
                    pl.destinations.AddRange(made);
                    return true;
                }
            }
            Plugin.Logger.LogWarning($"[Merger] plan edit REFUSED (logistics {op}): no such op.");
            return Refuse($"logistics: no op '{op}'");
        }

        /// <summary>B3.  Re-seat one destination's stock targets from the leg's copy of that row, with the
        /// product row's own rules: an empty item name is not a target, the amount is clamped to the pane's
        /// ceiling, and ZERO IS THE ABSENCE OF A TARGET (the pane removes the entry when the field reaches
        /// zero, LogisticsManagerPlanUI.cs:376-379).  An ItemAmountTarget already on the row for the same
        /// item is RE-USED rather than replaced, exactly as the old `target` op did.</summary>
        private static void SetStockTargets(Entities.LogisticsManagerPlanDestination dst, PwLogisticsDestination? want)
        {
            var keep = new List<BigAmbitions.Items.ItemAmountTarget>();
            foreach (var t in (want == null ? null : want.StockTargets) ?? new List<PwItemOrderLine>())
            {
                if (t == null || string.IsNullOrEmpty(t.ItemName)) continue;
                int amount = t.Amount < 0 ? 0 : (t.Amount > MaxTargetAmount ? MaxTargetAmount : t.Amount);
                if (amount == 0) continue;
                bool already = false;
                foreach (var k in keep) if (k != null && k.itemName == t.ItemName) { already = true; break; }
                if (already) continue;                       // a duplicated item name is one target, not two
                BigAmbitions.Items.ItemAmountTarget? row = null;
                foreach (var e in dst.stockTargets) if (e != null && e.itemName == t.ItemName) { row = e; break; }
                if (row == null) row = new BigAmbitions.Items.ItemAmountTarget(t.ItemName);
                row.targetAmount = amount;
                keep.Add(row);
            }
            dst.stockTargets.Clear();
            dst.stockTargets.AddRange(keep);
        }

        /// <summary>The pane's own ceiling on a stock target (LogisticsManagerPlanUI.cs:38/:367-369).</summary>
        private const int MaxTargetAmount = 9999999;

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
                    // FOLD b H2: the game changes a pricing manager through the PLAN's own calls
                    // (PricingManagersPlanList.ChangePricingManager :237-246).  UnAssignEmployee
                    // (PricingManagerPlan.cs:65-70) RESTORES THE SHOPS' ORIGINAL PRICES - the very thing the
                    // unassign dialog warns about - and clears the suggestions; AssignEmployee (:57-63)
                    // snapshots the current prices, reapplies and recomputes.  The raw field write did none
                    // of that, so the owner did not do what the confirmed dialog promised.
                    if (string.IsNullOrEmpty(p.StrValue)) pl.UnAssignEmployee();
                    else pl.AssignEmployee(p.StrValue);
                    return true;
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
                    // FOLD b H2: ChangePurchasingAgent (PurchasingAgentsPlanList.cs:150-158) does more on the
                    // UNASSIGN leg - ImportPartnership.UnAssignEmployee (:415-421) also stops the contract and
                    // drops the urgent flag.  Assigning is the raw write the game itself makes.
                    if (string.IsNullOrEmpty(p.StrValue)) ip.UnAssignEmployee();
                    else ip.employeeInstanceId = p.StrValue;
                    return true;
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
                {
                    if (InjectedHere(p.StrValue)) return RefuseInjected("hr", op, id, p.StrValue);   // r2 MAJOR-3
                    // FOLD b H2: the game's own change path (HrManagersPlanList.cs:186-256 plus
                    // ChangeHrManager :260-267).  Every confirmed branch TRIMS BEFORE ASSIGNING: employees
                    // past the new manager's capacity lose the plan (and their own assignedHrManagerPlanId),
                    // and the health insurance is cancelled when the new manager's skill cannot carry it.
                    // The member has already answered that dialog; when neither condition held both steps are
                    // no-ops, so they run unconditionally here rather than being guessed from the wire.
                    string? eid = string.IsNullOrEmpty(p.StrValue) ? null : p.StrValue;
                    if (eid == null) { pl.UnAssignEmployee(); return true; }
                    EmployeeInstance? e = null; try { e = EmployeeHelper.GetEmployeeById(eid); } catch { }
                    if (e == null) return Refuse($"manager: no employee '{eid}' on this machine");
                    float sk = e.GetSkillValue("ba:skill_hrmanager");
                    int max = HrManagerHelper.CalculateMaxAssignableEmployees(sk);
                    while (pl.assignedEmployees != null && pl.assignedEmployees.Count > 0 && pl.assignedEmployees.Count > max)
                    {
                        string last = pl.assignedEmployees[pl.assignedEmployees.Count - 1];
                        try { var lost = EmployeeHelper.GetEmployeeById(last); if (lost != null) lost.assignedHrManagerPlanId = null; } catch { }
                        pl.assignedEmployees.RemoveAt(pl.assignedEmployees.Count - 1);
                    }
                    if (pl.healthInsurancePlan != null
                        && sk < HealthInsuranceHelper.GetMinSkillForPlan(pl.healthInsurancePlan.planType))
                        pl.CancelHealthInsurancePlan();
                    pl.AssignEmployee(eid);
                    // ChangeHrManager :262-266: somebody who was one of the plan's OWN employees stops being
                    // one the moment they take the plan over.
                    if (e.assignedHrManagerPlanId == pl.id)
                    { if (pl.assignedEmployees != null) pl.assignedEmployees.Remove(eid); e.assignedHrManagerPlanId = null; }
                    return true;
                }
                case "delete":
                    // CROSS-HR-3 A3: the tags first, while the list still says who carries them; native then
                    // clears the LOCAL records as it always has.  The prefix on HrManagerPlan.Delete sees this
                    // id in _hrDeleteFanOut and leaves the sending to us.
                    _hrDeleteFanOut.Add(id);
                    try { HrTagFanOutClear(pl, "the plan was deleted"); HrManagerHelper.DeletePlan(id); }
                    finally { _hrDeleteFanOut.Remove(id); }
                    return true;
                // HQ-PARITY-3 B5: the two HR controls that had no patch at all.  Their listeners are
                // anonymous lambdas built inside HrManagerPlanUI.LoadPlan (decompile :86-89 and :92-97) and a
                // partner's press wrote the detached copy and was dropped.  The writes here are the game's own.
                case "replaceabsent": pl.replaceAbsentEmployees = p.BoolValue; return true;
                case "trainingtarget": pl.trainingTarget = p.IntValue < 0 ? 0 : (p.IntValue > 100 ? 100 : p.IntValue); return true;
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
                {
                    if (InjectedHere(p.StrValue)) return RefuseInjected("headhunter", op, id, p.StrValue);   // r2 MAJOR-3
                    // FOLD b H2: HeadhuntersPlanList.cs:131-195 plus ChangeHeadhunter :203.  Every confirmed
                    // branch STRIPS BEFORE ASSIGNING: deal-breakers the new headhunter's recruitment points
                    // cannot pay for are cleared, and the HR plans past its capacity are nulled out.  Both are
                    // no-ops when the new headhunter covers them, so they run unconditionally.  Unassigning
                    // never prompts and is the plain write the game itself makes.
                    string? eid = string.IsNullOrEmpty(p.StrValue) ? null : p.StrValue;
                    if (eid != null)
                    {
                        EmployeeInstance? e = null; try { e = EmployeeHelper.GetEmployeeById(eid); } catch { }
                        if (e == null) return Refuse($"manager: no employee '{eid}' on this machine");
                        float sk = e.GetSkillValue("ba:skill_headhunter");
                        int cost = 0;
                        if (pl.dealBreakerTypes != null)
                            foreach (var t in pl.dealBreakerTypes)
                            { try { var d = HeadhunterHelper.GetData(t); if (d != null) cost += d.recruitmentPointCost; } catch { } }
                        if (pl.dealBreakerTypes != null && HeadhunterHelper.CalculateMaxDealBreakersPoints(sk) < cost)
                            pl.dealBreakerTypes.Clear();
                        int max = HeadhunterHelper.CalculateMaxHrPlans(sk);
                        if (max < 0) max = 0;
                        if (pl.assignedHrPlans != null)
                        {
                            // Fold c (re-check): the game trims the HR-plan slots ONLY when the new headhunter cannot
                            // handle the plans currently held (HeadhuntersPlanList.cs:136 counts the non-empty slots;
                            // the trims sit inside the two failed-capacity branches :143-146 / :181-184). The array is
                            // index-addressed and may be sparse, so an unconditional trim could null a plan parked in a
                            // high slot that the game would keep.
                            int held = 0; foreach (var h in pl.assignedHrPlans) if (!string.IsNullOrEmpty(h)) held++;
                            if (max < held)
                                for (int i = max; i < pl.assignedHrPlans.Length; i++) pl.assignedHrPlans[i] = null;
                        }
                    }
                    pl.assignedEmployeeId = eid;
                    return true;
                }
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

    /// <summary>FOLD d E1: UNITY'S OWN END FOR THE TRAINING-SLIDER HOLD.  Added to the slider's GameObject
    /// by ArmSliderCommit, alongside the EventTrigger that arms the hold.  Unity calls OnDisable on a
    /// component the moment its GameObject or ANY ancestor is deactivated or destroyed - which is exactly
    /// the case where the pointer-up never reaches the slider (a tab switch, the phone closing, the plan
    /// deselected mid-drag), because ExecuteEvents skips an inactive component.  One component per slider,
    /// added once and left there for the object's life.</summary>
    public sealed class HrSliderHoldGuard : MonoBehaviour
    {
        private void OnDisable()
        {
            try { CompanyPlans.SliderHoldEndedWithoutPointerUp(); }
            catch { }
        }
    }
}
