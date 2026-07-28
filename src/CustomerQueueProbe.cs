using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>Round-130 PROBE — why do customers spawn and walk straight back out?
    ///
    /// The user reports arrivals leaving immediately with two staffed, non-faulty, same-type registers in the
    /// shop.  A 10s [TillDiag] snapshot cannot answer it: it samples the shop, not the MOMENT a customer
    /// decides.  This captures that moment.
    ///
    /// CustomerJoinQueue.OnStart (decompiled) does exactly this:
    ///     if (GetAvailableWaitingLines(tag).Any())
    ///     { if (!IsThereAWaitingLineWithSpotsAvailable(tag)) Complain(noEmployeeStationsWithFreeSpots); }
    ///     else Complain(noAvailableEmployeeStations);
    /// and a complaint means: show an emoji, then the objective returns Success — i.e. give up and leave.
    /// So the customer's fate is decided by TWO queries against ITS OWN tag set, and the whole question is what
    /// those queries return for THAT customer at THAT instant.  We re-run the identical queries in a postfix
    /// and print the verdict plus every candidate line, so the answer is in the log rather than inferred.
    ///
    /// Reading the tag off the task instance matters: `employeeStationTag` is the customer's own filter
    /// (SharedItemTag.AllWithTag), and if a register is missing from FindWaitingLines(tag) then no amount of
    /// staffing or free spots would ever have put it in play — that is a different defect from an unstaffed or
    /// full till, and this line distinguishes them.
    ///
    /// Log-only, throttled, MP-gated.  Remove once the cause is named (registered in .modding/04-probes.md).</summary>
    internal static class CustomerQueueProbe
    {
        private const float MinSecondsBetweenLines = 1.5f;   // arrivals cluster; one line per beat is enough
        private static float _nextLogAt;
        private static int   _left, _joined;

        internal static void Report(CustomerJoinQueue task)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                if (task == null || task.employeeStationTag == null) return;

                string[] tag;
                try { tag = task.employeeStationTag.AllWithTag; } catch { return; }
                if (tag == null) return;

                var all       = WaitingLinesHelper.FindWaitingLines(tag)?.ToList();
                var available = WaitingLinesHelper.GetAvailableWaitingLines(tag)?.ToList();
                bool anyFree  = WaitingLinesHelper.IsThereAWaitingLineWithSpotsAvailable(tag);

                int found = all?.Count ?? -1;
                int avail = available?.Count ?? -1;
                bool willLeave = avail <= 0 || !anyFree;
                if (willLeave) _left++; else _joined++;

                if (Time.unscaledTime < _nextLogAt) return;
                _nextLogAt = Time.unscaledTime + MinSecondsBetweenLines;

                var sb = new System.Text.StringBuilder();
                if (all != null)
                    foreach (var wl in all)
                    {
                        if (wl == null) continue;
                        string key = "?"; int spots = -1; string emp = "none"; bool availHere = false;
                        try { key = $"{Mathf.RoundToInt(wl.EmployeeStationController.transform.position.x)}:{Mathf.RoundToInt(wl.EmployeeStationController.transform.position.y)}:{Mathf.RoundToInt(wl.EmployeeStationController.transform.position.z)}"; } catch { }
                        try { spots = wl.data.GetAmountOfSpotsAvailable(); } catch { }
                        try
                        {
                            var e = wl.EmployeeStationController?.employee;
                            emp = e == null ? "NONE" : (e.IsAway ? "AWAY" : "ok");
                        }
                        catch { }
                        try { availHere = available != null && available.Contains(wl); } catch { }
                        sb.Append($" [{key} emp={emp} spots={spots} available={availHere}]");
                    }

                Plugin.Logger.LogWarning(
                    $"[QueueProbe] customer decision: tagMatches={found} available={avail} anyWithFreeSpots={anyFree} "
                    + $"→ {(willLeave ? "COMPLAIN + LEAVE" : "join")} (session totals left={_left} joined={_joined}) —{sb}");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[QueueProbe] {ex.Message}"); }
        }
    }

    /// <summary>Postfix, so the native OnStart has already made its own decision — we only observe.</summary>
    [HarmonyPatch(typeof(CustomerJoinQueue), "OnStart")]
    public static class Patch_CustomerJoinQueue_Probe
    {
        static void Postfix(CustomerJoinQueue __instance) => CustomerQueueProbe.Report(__instance);
    }

    /// <summary>ROUND-130b — THE DEPARTURE FUNNEL.  Every customer that leaves, for ANY reason, goes through
    /// CustomerLeave.OnStart → Customer.Leave() (CustomerLeave.cs is four lines and does nothing else).  The
    /// queue probe above only sees customers that reach the QUEUE step; anyone who gives up earlier — during
    /// shopping, on a demand they cannot satisfy, on a 45-minute entry timeout — never reaches it and would
    /// have produced no evidence at all.  This closes that hole: it fires on every departure and prints HOW FAR
    /// the customer actually got.
    ///
    /// CustomerState is the whole answer: Spawning means they left before even heading for a till (so nothing
    /// about registers is implicated); GoingToWaitingLine / InWaitingLine means they picked a till and then
    /// abandoned it; BeingServed means the serve itself broke; Served means a NORMAL, correct departure and is
    /// not a defect at all.  Order-entry counts separate "had nothing to buy" from "had a basket and gave up".</summary>
    internal static class CustomerLeaveProbe
    {
        private static readonly System.Collections.Generic.Dictionary<string, int> _byState = new();
        private static float _nextSummaryAt;

        private static readonly System.Collections.Generic.Dictionary<int, object?> _lastReportedOrder = new();
        internal static int _instantLeaveDepth;   // round-138: set across InstantlyLeave so the purge is tagged

        internal static void Report(Customer c, string via)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                if (c == null) return;
                // ROUND-137 dedup fix: bodies are POOLED (round-45), so an instance-id-only dedup silently
                // swallowed every recycled customer after a body's first departure - this run: 23 joins,
                // 4 recorded departures.  Dedup per (body, order) instead: the order object changes with each
                // new occupant, so successors count, while Leave-then-Release of the SAME customer still
                // reports once.
                try
                {
                    int iid = c.GetInstanceID();
                    object? ord = c.order;
                    if (_lastReportedOrder.TryGetValue(iid, out var prevOrd) && ReferenceEquals(prevOrd, ord)) return;
                    _lastReportedOrder[iid] = ord;
                }
                catch { }

                bool shopOpen = false;
                try { shopOpen = InstanceBehavior<BuildingManager>.Instance?.isOpen ?? false; } catch { }

                // ROUND-138 (user correction accepted: amenity complainers do NOT leave; the leavers JOIN the
                // queue and abandon it before paying).  The behaviour TREE that decides abandonment is asset
                // data we cannot decompile, so the departure record itself must name the class.  Four fields
                // discriminate everything: inLine/spot (were they queued, and where), minsSinceSpawn (the
                // native time thresholds are 5/15/45 game-minutes — a cluster just past one of those numbers
                // is a timeout), timeState (the game's own reading of the same clock), and via=InstantLeave
                // (the hourly closed-shop purge, tagged by the prefix below, the only path that yanks a
                // QUEUED customer out directly).
                bool inLine = false; int lineSpot = -1; float minsSinceSpawn = -1f; string timeState = "?";
                try { inLine = c.assignedWaitingLine != null; } catch { }
                try { lineSpot = c.currentWaitingLineSpot; } catch { }
                try
                {
                    if (c.customerEntry != null)
                        minsSinceSpawn = TimeHelper.NowInMinutes() - c.customerEntry.spawnTime.GetTotalMinutes();
                }
                catch { }
                try { timeState = c.customerTimeState.ToString(); } catch { }
                if (_instantLeaveDepth > 0) via = "InstantLeave(closed-purge)";

                string state = "?";
                try { state = c.state.ToString(); } catch { }
                int entries = 0, processed = 0, paid = 0;
                try
                {
                    if (c.order?.entries != null)
                        foreach (var e in c.order.entries)
                        {
                            if (e == null) continue;
                            entries++;
                            if (e.processed) processed++;
                            if (e.paid) paid++;
                        }
                }
                catch { }

                // Served departures are the normal case — count them, but do not shout about them.
                bool normal = state == "Served";
                string key = $"{state}/{via}";
                _byState.TryGetValue(key, out var n);
                _byState[key] = n + 1;

                if (!normal)
                    Plugin.Logger.LogWarning($"[LeaveProbe] customer GONE via {via} in state={state} shopIsOpen={shopOpen} "
                        + $"inLine={inLine} spot={lineSpot} minsSinceSpawn={minsSinceSpawn:F0} timeState={timeState} — "
                        + $"order: {entries} entr(ies), {processed} processed, {paid} paid. "
                        + "(minsSinceSpawn clustering just past 45 = queue-wait timeout; via=InstantLeave = hourly "
                        + "closed-shop purge; via=ReleaseCustomer + state=Spawning = time-machine wipe.)");

                if (Time.unscaledTime >= _nextSummaryAt)
                {
                    _nextSummaryAt = Time.unscaledTime + 30f;
                    var sb = new System.Text.StringBuilder();
                    foreach (var kv in _byState) sb.Append($" {kv.Key}={kv.Value}");
                    Plugin.Logger.LogInfo($"[LeaveProbe] departures so far:{sb} (Served = normal; anything else is a customer giving up).");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[LeaveProbe] {ex.Message}"); }
        }
    }

    /// <summary>ROUND-131 CORRECTION — I patched the wrong thing, and the user caught it.  CustomerLeave is the
    /// behaviour-tree TASK; it is only one of three ways a customer disappears, and it recorded ONE departure
    /// while the user watched many.  I then argued from that single line that the customers had been SERVED.
    /// That was unsupported: one Served line says one customer was served, nothing about the others.
    ///
    /// The three real exits, all read this time:
    ///   1. CustomerLeave.OnStart → Customer.Leave()          — the ordinary walk-out (what I had).
    ///   2. Customer.OnNewHour → InstantlyLeave() → Leave()   — fires for EVERY customer, every game hour,
    ///      whenever BuildingManager.isOpen is false.  Bypasses the task entirely.
    ///   3. Customer.OnTimeMachineStarted → ReleaseCustomer() — does not even call Leave().  A time machine
    ///      starts on BUILDING ENTRY, taxi rides and time skips, so in MP — where players enter and exit
    ///      constantly — this can wipe the shop's customers over and over.
    ///
    /// So the probes now sit on Customer.Leave and Customer.ReleaseCustomer, which together cover all three,
    /// and each records the shop's isOpen state so an hourly closed-shop purge is distinguishable from a
    /// time-machine wipe on sight.</summary>
    [HarmonyPatch(typeof(Customer), nameof(Customer.Leave))]
    public static class Patch_Customer_Leave_Probe
    {
        static void Prefix(Customer __instance) => CustomerLeaveProbe.Report(__instance, "Leave");
    }

    /// <summary>Round-138: tag the hourly closed-shop purge.  Customer.OnNewHour → InstantlyLeave() is the ONE
    /// path that removes a QUEUED customer directly (it unhooks them from the waiting line, then calls Leave),
    /// and it fires for every customer at every game hour whenever BuildingManager.isOpen reads false at that
    /// instant.  A cluster of InstantLeave departures at hour boundaries = the open flag flickering, which
    /// after round-122/124's toggle work would be OUR prime suspect — this tag is what would convict or clear
    /// it.</summary>
    [HarmonyPatch(typeof(Customer), "InstantlyLeave")]
    public static class Patch_Customer_InstantlyLeave_Tag
    {
        static void Prefix()    { CustomerLeaveProbe._instantLeaveDepth++; }
        static void Finalizer() { if (CustomerLeaveProbe._instantLeaveDepth > 0) CustomerLeaveProbe._instantLeaveDepth--; }
    }

    /// <summary>Round-142 [QueueInit] — the poison is MEASURED (till 952's head anchor sits at the
    /// world origin while the item sits at x~950: the anchor was captured before the object was
    /// positioned) but the ENTRY POINT is not: reading says items are positioned before Start(), yet
    /// the poison regenerated this run.  EmployeeStationController.Start is where InitWaitingLine
    /// runs, so this logs, for every cash register at that exact moment: where the OBJECT actually is,
    /// where its DATA says it should be, and what queue data it starts with.  transform at origin
    /// while data says the shop = the capture moment is here on this machine; a0 already poisoned in
    /// the incoming data = the poison arrived via wire or save and the writer is upstream.</summary>
    [HarmonyPatch(typeof(EmployeeStationController), "Start")]
    public static class Patch_ESC_Start_QueueInit
    {
        static void Prefix(EmployeeStationController __instance)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                var ii = __instance.ItemInstance;
                if (ii?.itemName == null || !ii.itemName.Contains("cashregister")) return;
                var t = __instance.transform.position;
                Vector3 dataPos = ii.position;
                int n = -1; string a0 = "none";
                var cp = ii.customPositions;
                if (cp != null)
                {
                    n = cp.Count;
                    if (cp.Count > 0) { Vector3 v = cp[0]; a0 = v.ToString(); }
                }
                bool objAtOrigin = t.sqrMagnitude < 4f && dataPos.sqrMagnitude > 100f;
                Plugin.Logger.LogWarning($"[QueueInit] register Start: transform={t} dataPos={dataPos} "
                    + $"customPositions={n} a0={a0}{(objAtOrigin ? "  ← OBJECT AT ORIGIN AT INIT TIME — this is the capture moment" : "")}");
            }
            catch { }
        }
    }

    /// <summary>Round-141 [EjectProbe] — MoveCustomerToAnotherWaitingLine is the ONLY bounce path: a
    /// customer already bound to a line gets pushed out (the line was full when they arrived, or their
    /// spot move failed) and either transfers to another line or, when none qualifies at that INSTANT,
    /// leaves the shop.  The 10s [TillDiag] snapshot cannot see that instant; this fires inside it and
    /// answers, per ejection: which line bounced them and the exact state of every alternative.
    /// Vanilla spreads arrivals across registers (the chooser counts walkers as occupants), so bounces
    /// that end in Leave() need a moment where no other till qualifies — this names that moment.</summary>
    [HarmonyPatch(typeof(EmployeeStations.WaitingLineCustomersManagement),
                  nameof(EmployeeStations.WaitingLineCustomersManagement.MoveCustomerToAnotherWaitingLine))]
    public static class Patch_MoveToAnotherLine_Probe
    {
        static void Prefix(EmployeeStations.WaitingLineCustomersManagement __instance, Customer customer)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                var data = HarmonyLib.AccessTools.Field(typeof(EmployeeStations.WaitingLineCustomersManagement), "_data")
                               ?.GetValue(__instance) as EmployeeStations.WaitingLineData;
                string from = "?"; int standing = -1, spotsHere = -1; string[] names = null;
                if (data != null)
                {
                    try { standing = data.GetCustomersInWaitingLine(); } catch { }
                    try { spotsHere = data.spots?.Count ?? -1; } catch { }
                    try { names = data.controllerNames; } catch { }
                    try
                    {
                        if (data.spots != null && data.spots.Count > 0)
                        {
                            var s0 = data.spots[0];
                            from = $"{Mathf.RoundToInt(s0.x)}:{Mathf.RoundToInt(s0.y)}:{Mathf.RoundToInt(s0.z)}";
                        }
                    }
                    catch { }
                }
                var sb = new System.Text.StringBuilder();
                try
                {
                    var all   = names != null ? WaitingLinesHelper.FindWaitingLines(names)?.ToList() : null;
                    var avail = names != null ? WaitingLinesHelper.GetAvailableWaitingLines(names)?.ToList() : null;
                    if (all != null)
                        foreach (var wl in all)
                        {
                            if (wl == null) continue;
                            string key = "?"; int free = -1; string emp = "none"; bool availHere = false;
                            try { var t = wl.EmployeeStationController.transform.position; key = $"{Mathf.RoundToInt(t.x)}:{Mathf.RoundToInt(t.y)}:{Mathf.RoundToInt(t.z)}"; } catch { }
                            try { free = wl.data.GetAmountOfSpotsAvailable(); } catch { }
                            try { var e = wl.EmployeeStationController?.employee; emp = e == null ? "NONE" : (e.IsAway ? "AWAY" : "ok"); } catch { }
                            try { availHere = avail != null && avail.Contains(wl); } catch { }
                            sb.Append($" [{key} emp={emp} freeSpots={free} available={availHere}]");
                        }
                }
                catch { }
                string cust = "?";
                try { cust = $"state={customer.state} spot={customer.currentWaitingLineSpot}"; } catch { }
                Plugin.Logger.LogWarning($"[EjectProbe] customer BOUNCED off the line whose head is at {from} "
                    + $"(standing={standing}/spots={spotsHere}) {cust} — alternatives at this instant:{sb} — "
                    + "no alternative with available=True and freeSpots>0 means this bounce becomes a Leave().");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[EjectProbe] {ex.Message}"); }
        }
    }

    /// <summary>The pooling release — reached by the time-machine handler WITHOUT going through Leave().</summary>
    [HarmonyPatch(typeof(Customer), nameof(Customer.ReleaseCustomer))]
    public static class Patch_Customer_Release_Probe
    {
        static void Prefix(Customer __instance) => CustomerLeaveProbe.Report(__instance, "ReleaseCustomer");
    }

    /// <summary>The instant-leave variant: same decision, different task.</summary>
    [HarmonyPatch(typeof(CustomerJoinQueueInstantly), "OnStart")]
    public static class Patch_CustomerJoinQueueInstantly_Probe
    {
        static void Postfix()
        {
            if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;   // round-140: never print in single player
            try { Plugin.Logger.LogInfo("[QueueProbe] a customer used the INSTANT join path."); }
            catch { }
        }
    }
}
