using System;
using System.Collections.Generic;

namespace BigAmbitionsMP
{
    /// <summary>
    /// DISSOLVE CLEAN-UP (user ruling 2026-09-12: "the systems must correctly handle cancelling of a
    /// merger so that cross-merger services don't continue after the merger is cancelled").
    ///
    /// A cancelled merger un-does ITSELF today: MergerFlip's 1 Hz reconcile stops showing the partner's
    /// buildings as own and cascades CompanyLists.OnUnflipped -> PaperworkSync.ClearOwner, which takes the
    /// shadows and display copies back out. What nothing un-does is what a member MADE while merged - a
    /// logistics plan shipping into a partner's shop, a partner's worker listed on an HR plan, one of MY
    /// workers tagged to a plan that is about to vanish (which leaves them unassignable for ever and
    /// breaks their insurance and absence cover), bench copies of a partner's people (the roster copies
    /// that staff every player's shops in every world are not a merger arrangement and stay), an insurance
    /// offer naming a partner's plan. Those all survive the un-flip and keep running.
    ///
    /// THIS is the teardown. Every step is VALUE-BASED and IDEMPOTENT: it tests the state it fixes and
    /// does nothing when there is nothing to do, so the SAME routine runs at the MEMBERSHIP EDGE
    /// (MergerSync.ApplyState - while the flip still identifies the partner's buildings and the shadow
    /// still classifies an HR tag) and at the FIRST STATE of a session that says "you are in no group",
    /// which is the self-heal for a member who was OFFLINE when the company was cancelled and so ran no
    /// dissolve leg at all. Each step is wrapped on its own, so one failure never skips the rest, and
    /// each logs ONE line only when it actually changed something.
    ///
    /// `exPartners` names the pids that WERE co-members of mine and are not any more. An EMPTY set means
    /// "anyone who is not me" - the first-state reading, where there is no previous membership to diff.
    /// It is handed in ONLY where membership is KNOWN (the first-state hook, run through HealIfPending at
    /// whichever comes second of that state and world-ready, and the `dissolverun` lever):
    /// WORLD-READY does not call this at all, because membership is unknown there.
    /// </summary>
    internal static class MergerDissolve
    {
        /// <summary>What one sweep changed (or, in count-only mode, would change).</summary>
        internal struct Counts
        {
            public int Logistics, Contracts, HrList, HrTag, Offers, Copies;
        }

        /// <summary>MAIN THREAD. Tear down every cross-member arrangement the ex-partners left behind.</summary>
        /// <summary>FOLD d/e (re-checks r2/r3): the whole-world heal for a member who was away when the
        /// company was cancelled. ApplyState ARMS it (MergerSync.HealPending) on EVERY non-member state and
        /// calls this at once; the world-ready hook calls it too. Whichever call finds the state seen AND the
        /// world ready runs the empty-set sweep ONCE per world instance (MergerSync.HealedThisWorld) and
        /// disarms; every other call finds nothing to do. The state usually lands during the load (the
        /// host's load-time broadcast, a join replay) - the world-ready call runs it then; a state that
        /// lands after world-ready runs it here. Never while I am a member.</summary>
        public static void HealIfPending(string when)
        {
            try
            {
                if (!MergerSync.HealPending || !MergerSync.StateSeen) return;
                if (MPLifecycle.Phase < MPLifecycle.MPPhase.WorldReady)
                {
                    if (when != "world-ready") Plugin.Logger.LogInfo($"[Dissolve] heal armed at {when}: waiting for the world to be ready.");
                    return;   // the world-ready hook comes back
                }
                MergerSync.HealPending = false;
                if (MergerSync.HealedThisWorld) return;   // once per world instance
                if (MergerSync.IAmMember) return;         // a state arrived meanwhile that makes me a member: nothing to heal
                MergerSync.HealedThisWorld = true;
                Run("first-state (" + when + ")", Array.Empty<string>());
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Dissolve] heal at {when}: {ex.Message}"); }
        }

        public static void Run(string when, IReadOnlyCollection<string> exPartners)
        {
            Sweep(when, exPartners, apply: true);
        }

        /// <summary>MAIN THREAD (TestDrive `dissolverun`): as Run, and says what it changed.</summary>
        internal static Counts RunCounted(string when, IReadOnlyCollection<string> exPartners)
            => Sweep(when, exPartners, apply: true);

        /// <summary>The SAME predicates in COUNT-ONLY mode (TestDrive `dissolvecheck`): what Run would
        /// change right now, with nothing written and nothing logged.</summary>
        internal static Counts Check(IReadOnlyCollection<string> exPartners)
            => Sweep("check", exPartners, apply: false);

        private static Counts Sweep(string when, IReadOnlyCollection<string> exPartners, bool apply)
        {
            var c = new Counts();
            var ex = new HashSet<string>(StringComparer.Ordinal);
            if (exPartners != null)
                foreach (var p in exPartners)
                    if (!string.IsNullOrEmpty(p) && p != MPConfig.PlayerId) ex.Add(p);

            // "Anyone who is not me" is only TRUE once this machine KNOWS its own membership, so the empty
            // set now comes from exactly two places: the FIRST-STATE hook in MergerSync.ApplyState (the
            // instant membership becomes known, and it says "not a member") and the `dissolverun` lever.
            // WORLD-READY never calls this - membership is unknown there, on a client and on a host alike.
            // While I am still a member every cross-member arrangement here is LIVE, so that one case
            // changes nothing and logs nothing; a save that was never merged, and a solo load, run the
            // whole routine to zero.
            if (ex.Count == 0)
            {
                bool member = false;
                try { member = MergerSync.IAmMember; } catch { }
                if (member) return c;
            }

            // (1) LOGISTICS - first, while the flip still identifies which buildings were the partner's.
            try
            {
                int dests = 0, sources = 0, cargo = 0, flight = 0;
                var gi = SaveGameManager.Current;
                var plans = gi?.logisticsManagerPlans;
                if (plans != null)
                {
                    var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pl in plans)
                    {
                        if (pl == null) continue;
                        bool notMine = false;
                        try { notMine = MergerAbsence.IsDisplayInstall(pl) || CompanyPlans.IsOverlayPlan(pl); } catch { }
                        if (notMine) continue;                       // a partner's row, lifted by the un-flip itself
                        if (pl.targetAddress != null)
                        {
                            string sk = "";
                            try { sk = GameStateReader.AddressKey(pl.targetAddress); } catch { }
                            if (ExPartnerKey(sk, ex))
                            {
                                // A foreign SOURCE makes GetPlannedDeliveries null the address silently; drop it
                                // here so the plan says what it is instead of failing every hour in silence.
                                sources++;
                                if (!apply) Plugin.Logger.LogInfo($"[Dissolve] check names: source '{sk}' on plan {pl.id} (an ex-partner's building).");   // DIAG-1
                                if (apply) try { pl.UnAssignAddress(); } catch { }
                            }
                        }
                        if (pl.destinations == null) continue;
                        foreach (var d in pl.destinations)
                        {
                            if (d == null || d.deliveryTargetAddress == null) continue;
                            string dk = "";
                            try { dk = GameStateReader.AddressKey(d.deliveryTargetAddress); } catch { }
                            if (!ExPartnerKey(dk, ex) || !done.Add(dk)) continue;
                            // Nothing in the base game ever tests a DESTINATION (DeliverDestination checks only
                            // the warehouse), so without this the member's warehouse keeps shipping into the
                            // ex-partner's shop for ever. The native cancel resets every destination row in
                            // every plan that names the address, which is exactly the whole of the leak.
                            dests++;
                            if (!apply) Plugin.Logger.LogInfo($"[Dissolve] check names: destination '{dk}' on plan {pl.id} (an ex-partner's building).");   // DIAG-1
                            if (apply)
                                try { Buildings.Office.Headquarters.LogisticsManagerHelper.CancelAllDeliveriesForAddress(d.deliveryTargetAddress); }
                                catch { }
                        }
                    }
                }
                // In-flight ROUTED CARGO (a mixed leg the gate handed to CargoTransfer), split by whether the
                // goods have LEFT the warehouse yet - the source debits stock the moment the destination's
                // need answer comes back (CargoTransfer.OnNeedAnswer sets Withdrawn, :313). BEFORE that there
                // is something to stop: the row is closed, and nothing has moved across a boundary that no
                // longer exists. AFTER it there is not: the goods are out of the source and the host still
                // relays the delivery, while a close would raise a FALSE native "no stock" report (Close ->
                // RaiseReport when Withdrawn, CargoTransfer.cs:791) and forget the source's own record - so a
                // withdrawn row is LEFT TO COMPLETE and only counted. Close() is the existing local close path.
                var pending = CargoTransfer._pending;
                if (pending != null && pending.Count > 0)
                    foreach (var tid in new List<string>(pending.Keys))
                    {
                        if (!pending.TryGetValue(tid, out var row) || row == null) continue;
                        if (!ExPartnerKey(row.DestKey ?? "", ex)) continue;   // r1 MINOR-5: the row names its own destination
                        if (!apply) Plugin.Logger.LogInfo($"[Dissolve] check names: cargo row {tid} -> '{row.DestKey}' withdrawn={row.Withdrawn}.");   // DIAG-1
                        if (row.Withdrawn) { flight++; continue; }
                        cargo++;
                        if (!apply) continue;
                        try { CargoTransfer.Close(row, "the merger that authorised this leg was cancelled"); } catch { }
                    }
                c.Logistics = dests + sources + cargo;   // what CHANGED: a left-to-complete row is not a change
                if (apply && c.Logistics > 0)
                    Plugin.Logger.LogInfo($"[Dissolve] logistics at {when}: {dests} delivery destination(s) cancelled, "
                                        + $"{sources} plan source(s) un-assigned, {cargo} in-flight cargo transfer(s) closed "
                                        + $"before withdrawal, {flight} left to complete (goods already withdrawn).");
            }
            catch (Exception ex1) { Plugin.Logger.LogWarning($"[Dissolve] logistics at {when}: {ex1.Message}"); }

            // (2) CONTRACTS. VERIFIED against the decompile (Entities/DeliveryContract.cs:16-20): a contract
            // has exactly one wholesaleAddress and one businessAddress, and the wholesale end is always the
            // dialog's own NPC wholesale store (WholesaleStoreManagerDialog.cs:65 is the game's only creation
            // point) - so NO contract can span two PLAYERS' buildings and there is nothing cross-owner to
            // retire. What this step does find is residue: a contract on THIS save whose business end is an
            // ex-partner's building, which an older build could mint before the creation gate was routed.
            try
            {
                int gone = 0;
                var contracts = SaveGameManager.Current?.DeliveryContracts;
                if (contracts != null)
                    for (int i = contracts.Count - 1; i >= 0; i--)
                    {
                        var ct = contracts[i];
                        if (ct == null) continue;
                        bool copy = false;
                        try { copy = MergerAbsence.IsDisplayInstall(ct); } catch { }
                        if (copy) continue;                          // a partner's display copy - the un-flip lifts it
                        string bk = "";
                        try { bk = GameStateReader.AddressKey(ct.businessAddress); } catch { }
                        if (!ExPartnerKey(bk, ex)) continue;
                        gone++;
                        if (apply) try { ct.Remove(); } catch { }
                    }
                c.Contracts = gone;
                if (apply && gone > 0)
                    Plugin.Logger.LogInfo($"[Dissolve] contracts at {when}: {gone} wholesale contract(s) delivering into an ex-partner's building removed.");
            }
            catch (Exception ex2) { Plugin.Logger.LogWarning($"[Dissolve] contracts at {when}: {ex2.Message}"); }

            // (3a) HR LISTS - a partner's person listed on one of MY real plans. Local only, no legs: by the
            // time a member learns of the dissolve the host has already closed the merger route, and the
            // partner's own machine is running this very routine for its own lists.
            try
            {
                int drops = 0;
                var hrPlans = SaveGameManager.Current?.hrManagerPlans;
                if (hrPlans != null)
                    foreach (var hp in hrPlans)
                    {
                        if (hp == null || hp.assignedEmployees == null) continue;
                        bool notMine = false;
                        try { notMine = MergerAbsence.IsDisplayInstall(hp) || CompanyPlans.IsOverlayPlan(hp); } catch { }
                        if (notMine) continue;
                        for (int i = hp.assignedEmployees.Count - 1; i >= 0; i--)
                        {
                            string eid = hp.assignedEmployees[i] ?? "";
                            if (eid.Length == 0) continue;
                            string owner = "";
                            try { owner = MPRegisterSync.OwnerOfInjected(eid) ?? ""; } catch { }
                            bool drop = owner.Length > 0 && (ex.Count == 0 || ex.Contains(owner));
                            if (!drop && ex.Count == 0)
                            {
                                // The empty set: an id on my plan that resolves to no record here is a partner's
                                // person left over from the cancelled company. (TrainEmployees drops a NULL
                                // instance on its next pass - a live copy never.) r1 MINOR-2: read the dictionary
                                // DIRECTLY - EmployeeHelper.GetEmployeeById logs a Unity error per id it misses.
                                try { drop = !Helpers.EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(eid, out _); }
                                catch { drop = false; }
                            }
                            if (!drop) continue;
                            drops++;
                            if (!apply) continue;
                            hp.assignedEmployees.RemoveAt(i);
                            // ... and the copy's own tag with it, so a copy that outlives this pass is not left
                            // pointing at a plan it is no longer on.
                            try
                            {
                                Helpers.EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(eid, out var copy);
                                if (copy != null && string.Equals(copy.assignedHrManagerPlanId, hp.id, StringComparison.Ordinal))
                                    copy.assignedHrManagerPlanId = null;
                            }
                            catch { }
                        }
                    }
                c.HrList = drops;
                if (apply && drops > 0)
                    Plugin.Logger.LogInfo($"[Dissolve] hr lists at {when}: {drops} ex-partner worker(s) removed from this machine's own HR plans.");
            }
            catch (Exception ex3) { Plugin.Logger.LogWarning($"[Dissolve] hr lists at {when}: {ex3.Message}"); }

            // (3b) HR TAGS - MY OWN worker still tagged to an ex-partner's plan. Once the shadow is gone
            // HrManagerHelper.GetPlanFromId returns null, and then the worker's insurance fails, absence
            // replacement never applies, and the worker can NEVER join any HR plan again (every assignable
            // filter is IsNullOrEmpty(assignedHrManagerPlanId)) - and the tag is SAVED.
            try
            {
                int cleared = 0;
                var emps = SaveGameManager.Current?.EmployeeInstances;
                if (emps != null)
                    foreach (var e in emps)
                    {
                        if (e == null) continue;
                        string eid = e.id ?? "";
                        if (eid.Length == 0) continue;
                        bool injected = false;
                        try { injected = MPRegisterSync.IsInjectedStaff(eid); } catch { }
                        if (injected) continue;                      // a copy, not a real record of mine
                        string tag = e.assignedHrManagerPlanId ?? "";
                        if (tag.Length == 0 || !BadPlanId(tag, ex)) continue;
                        cleared++;
                        if (apply) e.assignedHrManagerPlanId = null;
                    }
                c.HrTag = cleared;
                if (apply && cleared > 0)
                    Plugin.Logger.LogInfo($"[Dissolve] hr tags at {when}: {cleared} own worker(s) un-tagged from an ex-partner's HR plan.");
            }
            catch (Exception ex4) { Plugin.Logger.LogWarning($"[Dissolve] hr tags at {when}: {ex4.Message}"); }

            // (3c) The two SAVED side-records that name a partner's plan or a partner's person. Both use the
            // predicates their save-strip uses (MPRegisterSync K1(c) for the offers, M4 for the headhunter
            // replacements): while the shadow stands the strip keeps them out of the .hsg, but once it is
            // gone the predicate is false and they PERSIST.
            try
            {
                int gone = 0;
                var gi = SaveGameManager.Current;
                var offers = gi?.healthInsurancePlanOffers;
                if (offers != null)
                    for (int i = offers.Count - 1; i >= 0; i--)
                    {
                        string oid = "";
                        try { oid = offers[i]?.hrManagerPlanId ?? ""; } catch { }
                        if (oid.Length == 0 || !BadPlanId(oid, ex)) continue;
                        gone++;
                        if (apply) offers.RemoveAt(i);
                    }
                if (gi?.headhunterPlans != null)
                    foreach (var plan in gi.headhunterPlans)
                    {
                        var rl = plan?.headhunterReplacementDataList;
                        if (rl == null) continue;
                        for (int i = rl.Count - 1; i >= 0; i--)
                        {
                            string rid = "";
                            try { rid = rl[i]?.employeeInstance?.id ?? ""; } catch { }
                            if (rid.Length == 0 || !CopyOfExPartner(rid, ex)) continue;
                            gone++;
                            if (apply) rl.RemoveAt(i);
                        }
                    }
                c.Offers = gone;
                if (apply && gone > 0)
                    Plugin.Logger.LogInfo($"[Dissolve] offers at {when}: {gone} insurance offer(s)/headhunter replacement(s) naming an ex-partner's plan or person removed.");
            }
            catch (Exception ex5) { Plugin.Logger.LogWarning($"[Dissolve] offers at {when}: {ex5.Message}"); }

            // (4) BENCH COPIES ONLY (rig T-DISSOLVE run 2 + probe, 2026-09-12). The ROSTER copies of another
            // player's shop staff are not a merger arrangement: every player's staff is injected into every
            // world so that player's shops run with bodies, merger or no merger - the feed keeps them alive and
            // dropping them here only made the ex-partner's shops stand empty until the next publish (196 gone
            // and back on the client; the host's 161 simply arrived after the edge). What the merger ADDED is
            // the BENCH: a partner's unassigned people reach co-members (and plain grantees, whose grants are
            // gone for good between ex-members), so only bench copies of an ex-partner leave. At world-ready
            // nothing is injected yet and this counts 0.
            try
            {
                c.Copies = MPRegisterSync.DropBenchForOwners(ex, apply, $"merger cancelled ({when})");
                if (apply && c.Copies > 0)
                    Plugin.Logger.LogInfo($"[Dissolve] bench copies at {when}: {c.Copies} bench {(c.Copies == 1 ? "copy" : "copies")} of an ex-partner's people dropped.");
            }
            catch (Exception ex6) { Plugin.Logger.LogWarning($"[Dissolve] bench copies at {when}: {ex6.Message}"); }

            // (5) TRANSFERS and (6) ABSENCE are HOST legs and run in MPServer's "leave" handler - they need
            // the host-held tables, and a member cannot drive either.
            return c;
        }

        /// <summary>Is this address key a building that WAS an ex-partner's and is not mine? At the
        /// membership edge the flip still holds the parked runner, so the owner is named exactly; at
        /// world-ready the flip is not on yet and "not mine" is the whole test. A building this machine
        /// SIMULATES for an absent owner is not a cross-member leg - it is run here, legitimately.</summary>
        private static bool ExPartnerKey(string key, HashSet<string> ex)
        {
            try
            {
                if (string.IsNullOrEmpty(key)) return false;
                if (MergerAbsence.SimulatesHere(key)) return false;
                var reg = MergerAbsence.RegOfKey(key);
                if (reg == null) return false;                       // not registered here - nothing is proven
                if (MergerFlip.TrulyMine(reg)) return false;
                if (ex.Count == 0) return true;
                string owner = "";
                try
                {
                    owner = MergerFlip.IsFlipped(key) ? (MergerFlip.ParkedRunner(key) ?? "")
                                                      : (reg.businessOwnerRivalId?.ToString() ?? "");
                }
                catch { }
                return owner.Length > 0 && ex.Contains(owner);
            }
            catch { return false; }
        }

        /// <summary>Does this HR plan id name a plan that an ex-partner owned, or one that resolves to
        /// nothing at all? A REMAINING co-member's shadow answers FALSE - a 3+ company survives one member
        /// leaving and those tags are still live.</summary>
        private static bool BadPlanId(string planId, HashSet<string> ex)
        {
            if (string.IsNullOrEmpty(planId)) return false;
            object? pl = null;
            try
            {
                var list = SaveGameManager.Current?.hrManagerPlans;
                if (list != null)
                    foreach (var p in list)
                        if (p != null && string.Equals(p.id, planId, StringComparison.Ordinal)) { pl = p; break; }
            }
            catch { return false; }
            if (pl == null) return true;                             // a dead tag: the plan exists nowhere
            bool shadow = false;
            try { shadow = MergerAbsence.IsDisplayInstall(pl) || CompanyPlans.IsOverlayPlan(pl); } catch { }
            if (!shadow) return false;                               // my own plan, or one I stand in for
            if (ex.Count == 0) return true;
            string owner = OwnerOfInstall(pl);
            if (owner.Length == 0)
                try { owner = CompanyPlans.OwnerOfOverlayPlan(pl) ?? ""; } catch { }
            return owner.Length > 0 && ex.Contains(owner);
        }

        /// <summary>Whose display copy is this installed list item? MergerAbsence tags a wave-4 display
        /// copy "display:&lt;pid&gt;" and an absence install with a REAL pid; both answer the owner here.</summary>
        private static string OwnerOfInstall(object item)
        {
            try
            {
                foreach (var t in MergerAbsence.InstalledListItems)
                {
                    if (!ReferenceEquals(t.Item, item)) continue;
                    string o = t.Owner ?? "";
                    return o.StartsWith("display:", StringComparison.Ordinal) ? o.Substring("display:".Length) : o;
                }
            }
            catch { }
            return "";
        }

        /// <summary>TestDrive only (D4): every pid this machine currently treats as a partner - my
        /// co-members plus the owners of the injected copies standing here, EXCEPT an owner who currently
        /// holds a direct Business grant with me (r1 MINOR-1): a plain grantee's bench stands on that grant,
        /// not on a company, so the lever must never drop it. The levers need a NAMED set so they can be run
        /// while a merger is live; an empty one means "anyone who is not me", which the routine deliberately
        /// refuses to act on while I am still a member.</summary>
        internal static List<string> LeverPartners()
        {
            var set = new List<string>();
            try
            {
                foreach (var p in MergerSync.CoMembersOf(MPConfig.PlayerId))
                    if (!string.IsNullOrEmpty(p) && p != MPConfig.PlayerId && !set.Contains(p)) set.Add(p);
            }
            catch { }
            try
            {
                foreach (var o in MPRegisterSync.InjectedOwners())
                {
                    if (string.IsNullOrEmpty(o) || o == MPConfig.PlayerId || set.Contains(o)) continue;
                    bool grantee = false;
                    try { grantee = GrantSync.IsGrantedDirect(GrantKind.Business, o, MPConfig.PlayerId); } catch { }
                    if (!grantee) set.Add(o);            // a plain grant is not a merger arrangement
                }
            }
            catch { }
            return set;
        }

        /// <summary>Is this employee id a COPY of an ex-partner's person (staff or candidate)?</summary>
        private static bool CopyOfExPartner(string employeeId, HashSet<string> ex)
        {
            try
            {
                string owner = MPRegisterSync.OwnerOfInjected(employeeId) ?? "";
                if (owner.Length > 0) return ex.Count == 0 || ex.Contains(owner);
                if (ex.Count == 0 && CompanyCandidates.IsInjectedCandidate(employeeId)) return true;
            }
            catch { }
            return false;
        }
    }
}
