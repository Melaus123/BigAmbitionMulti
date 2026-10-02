using System;
using System.Collections;
using System.Collections.Generic;

namespace BigAmbitionsMP
{
    /// <summary>
    /// MERGER PHASE 3-B — DESIGNATION + HAND-OVER (plan §9; rulings D1, D13, D15).
    ///
    /// When a company member drops, the HOST designates one online member of that company as the
    /// SIMULATOR and hands it that member's businesses: the interiors (direct snapshots), the
    /// paperwork P3-A stored, and the staff records behind the roster publish.  The simulator then
    /// runs exactly those addresses as an owner would — the authority veil leaves them flipped on
    /// THAT machine only, its interior publisher treats them as its own, its paperwork publish
    /// includes them, and the promoted staff leave the injected registry so the native employee
    /// passes work them, pay them from the pooled wallet, train them and make them sick.
    ///
    /// D1: only while a member is ONLINE, merger-only.  D13: the host copy is the source, one-shot.
    /// D15: nothing depends on the simulator STAYING — the host holds its pushes, so a simulator
    /// that itself drops is simply replaced and the new one is seeded from the host exactly like
    /// the first.
    ///
    /// THE RETURN LEG IS P3-C - BUILT (2026-09-11, D2/D13/D15): when the owner is back AND their own
    /// world has loaded, the host sends them MergerHandover with Return=true - the host's paperwork for
    /// exactly the marked addresses - then paces those interiors to them and clears the mark WHEN THE
    /// OWNER ACKS IT (r2 F4 - the send only flags it; ABSENCE-HANDBACK-1 F7: the paperwork ack AND one
    /// "hand-back applied" ack per interior); their own
    /// machine applies it to its OWN registrations, lists and staff (never a native save field written
    /// for another player) and shows the one approved toast.  r4 (m1) is what finally
    /// makes the mark SURVIVE the owner's return: HostNoteReturn flags it OwnerBack and the host's
    /// reconcile spares it from the dead sweep that used to drop it in the very same pass, so P3-C
    /// still has something to consume.  Every installed list item is tagged so P3-C can lift it out
    /// again.  r5: while OwnerBack the simulator STOPS - HostNoteReturn drops it the moment the owner is
    /// back, so exactly ONE machine ever runs those shops.  The return leg's source is therefore the HOST's
    /// stored paperwork entry for the owner (the simulator's publishes were filed under the owner's stable
    /// while it was away), which StorePaperwork's OwnerBack guard (r6 F1) holds against the returned
    /// owner's own republish until P3-C copies back and the returned owner's ACK clears the mark (r2 F4).
    ///
    /// INERTNESS: no marks → SimulatesHere is one empty-dictionary read and every tick is a count
    /// check.  A direct-grant session (no merger) never produces a mark at all.
    /// </summary>
    public static class MergerAbsence
    {
        // ══ HOST: the mark table ═══════════════════════════════════════════════
        /// <summary>One absent owner's businesses and who simulates them.</summary>
        public class AbsenceMark
        {
            public string OwnerStable = "";
            public string OwnerPid = "";
            public string SimulatorPid = "";
            public List<string> Addresses = new();
            public int SinceDay;
            /// <summary>r4 m1: the owner is back ONLINE and the return leg has not run yet. The mark is
            /// kept (and spared the reconcile's dead sweep) until P3-C consumes it.</summary>
            public bool OwnerBack;
            /// <summary>P3-C (return toast): the last machine that actually simulated these addresses.
            /// r5 CLEARS SimulatorPid the moment the owner is back, so by the time the return leg builds
            /// its payload this is the only record of who ran the shops. Never cleared by a return;
            /// persisted additively in the manifest (a mark restored without it simply shows no toast).</summary>
            public string LastSimulatorPid = "";
            /// <summary>r2 F4: a return leg has been SENT for this mark and the owner has not acked it yet.
            /// The mark now lives until that ack, so an owner who drops during the paced interior send has
            /// something to re-fire from. IN-MEMORY ONLY - deliberately never persisted to the manifest, so
            /// a host restart re-fires the return, which is the intended outcome.</summary>
            public bool ReturnSent;
            /// <summary>ABSENCE-HANDBACK-1 F7: the addresses whose hand-back interior the returned owner has not acked as
            /// APPLIED yet. While an address is in here the host holds the owner's own uploads of it (InteriorSync.
            /// HostUploadVerdict) and answers routed storage ops on it 'busy' (StorageSync F9). IN-MEMORY ONLY, like
            /// ReturnSent: a restored mark re-fires the whole return.</summary>
            public HashSet<string> PendingInteriors = new(StringComparer.OrdinalIgnoreCase);
            /// <summary>ABSENCE-HANDBACK-1 F7: the owner's "return-applied" (paperwork) ack has landed. It ends
            /// StorePaperwork's OwnerBack guard exactly where the old mark removal did; the mark itself stays until
            /// PendingInteriors is empty too. In-memory only.</summary>
            public bool PaperworkAcked;
            /// <summary>ABSENCE-HANDBACK-1 fold 2 (D1 b): the addresses whose HOST WORLD copy is the absence truth - an accepted
            /// stand-in upload (or cargo graft) applied to the host's world, the F2 freeze, the host standing in with a truth
            /// (F1/F3). PERSISTED with the mark (the world copy is in the same save), so after a host restart - which empties the
            /// in-memory stored copies - the host still knows its world copy is the truth (InteriorSync.HostHoldsTruth). An
            /// address leaves it when the mark's hold for it ends (hand-back acked / refused, no hand-back) or with the mark.</summary>
            public HashSet<string> TruthInWorld = new(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly Dictionary<string, AbsenceMark> _marks = new();   // ownerStable → mark
        /// <summary>HOST: the live mark table (P3-C's read surface).</summary>
        public static IReadOnlyDictionary<string, AbsenceMark> Marks => _marks;
        public static int MarkCount => _marks.Count;

        private static readonly HashSet<string> _returnLogged = new();            // ownerPid, B4 once-per-return

        /// <summary>HOST: record (or re-point) one owner's mark. Returns true when something changed,
        /// which is the caller's signal to send the hand-over.</summary>
        public static bool HostSetMark(string ownerStable, string ownerPid, string simulatorPid,
                                       List<string> addresses, int sinceDay)
        {
            if (string.IsNullOrEmpty(ownerStable) || string.IsNullOrEmpty(simulatorPid)) return false;
            addresses ??= new List<string>();
            if (_marks.TryGetValue(ownerStable, out var have)
                && have.SimulatorPid == simulatorPid && SameSet(have.Addresses, addresses))
            { have.OwnerPid = string.IsNullOrEmpty(ownerPid) ? have.OwnerPid : ownerPid;   // ABSENCE-RESTART-DROP-1 (C3): "" never blanks a known pid
              have.OwnerBack = false; have.ReturnSent = false;   // r2 F4: absent again
              have.PendingInteriors.Clear(); have.PaperworkAcked = false;                                  // F7: a void return holds nothing
              have.LastSimulatorPid = simulatorPid; return false; }

            _marks[ownerStable] = new AbsenceMark
            {
                OwnerStable  = ownerStable,
                OwnerPid     = !string.IsNullOrEmpty(ownerPid) ? ownerPid : (have?.OwnerPid ?? ""),   // C3: a re-pointed mark keeps its known pid
                SimulatorPid = simulatorPid,
                Addresses    = new List<string>(addresses),
                SinceDay     = have != null ? have.SinceDay : sinceDay,
                LastSimulatorPid = simulatorPid,   // P3-C: who ran them, kept past r5's clear
            };
            // fold 2 (D1 b): a re-pointed mark keeps the world-truth record of the addresses it still names.
            try
            {
                if (have?.TruthInWorld != null && have.TruthInWorld.Count > 0)
                {
                    var nm = _marks[ownerStable];
                    foreach (var a in have.TruthInWorld)
                        if (nm.Addresses.Exists(x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase))) nm.TruthInWorld.Add(a);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] world-truth carry-over '{ownerStable}': {ex.Message}"); }
            return true;
        }

        /// <summary>HOST: this owner is no longer absent-with-a-simulator (dissolve, removal, nobody
        /// online). Tells the CURRENT simulator to undo B3 — never the owner's own machine (B4).</summary>
        public static void HostDropMark(string ownerStable, string why)
        {
            if (string.IsNullOrEmpty(ownerStable) || !_marks.TryGetValue(ownerStable, out var m)) return;
            _marks.Remove(ownerStable);
            Plugin.Logger.LogInfo($"[Absence] mark for '{m.OwnerPid}' dropped ({why}); simulator was '{m.SimulatorPid}'.");
            HostSendDrop(m.SimulatorPid, m.OwnerStable, m.OwnerPid, m.Addresses, why);
        }

        /// <summary>HOST, B4: the absent owner reconnected. r4 m1 - the mark really does STAY now: it
        /// is flagged OwnerBack and the TRUE return here is the caller's signal to put the stable into
        /// its 'wanted' set, which is the only thing that keeps the reconcile's dead sweep off it (the
        /// old code logged the return and then let the same pass drop the mark, so P3-C would have found
        /// nothing). The owner's own machine is still told nothing. The LOG is once per return.</summary>
        public static bool HostNoteReturn(string ownerStable, string ownerPid, int day)
        {
            if (!_marks.TryGetValue(ownerStable ?? "", out var m)) return false;
            m.OwnerPid  = ownerPid ?? m.OwnerPid;
            m.OwnerBack = true;
            // r5 (re-review r4 note): ONE machine per address, always. The moment the owner is back their own
            // machine runs those shops again, so the simulator must stop NOW - its last 2 s pushes are in the
            // host's copies and its paperwork for these addresses is already filed under the owner (F2). The
            // mark itself STAYS (OwnerBack) so the return leg can copy the simulated state back to the owner.
            if (!string.IsNullOrEmpty(m.SimulatorPid))
            {
                // r6 F4 (nit): clear FIRST, send after - the Marks snapshot the drop payload carries is then
                // already the post-return truth (nobody simulating these addresses).
                string wasSim = m.SimulatorPid;
                m.SimulatorPid = "";
                HostSendDrop(wasSim, ownerStable, m.OwnerPid, new List<string>(m.Addresses), "owner is back");
            }
            if (_returnLogged.Add(ownerPid ?? ""))
                Plugin.Logger.LogInfo($"[Absence] owner '{ownerPid}' returned - {m.Addresses.Count} addresses simulated "
                                    + $"since day {m.SinceDay} (simulator stopped; mark KEPT for the return leg).");
            return true;
        }

        /// <summary>HOST: forget a return log so a later absence logs its return again.</summary>
        public static void HostClearReturnLog(string ownerPid) { if (!string.IsNullOrEmpty(ownerPid)) _returnLogged.Remove(ownerPid); }

        /// <summary>HOST, MAIN THREAD (P3-C r2, F4): the RETURNED OWNER acked the return leg. THIS - never
        /// the send - is what clears the mark, so a return lost to a drop during the paced interior send
        /// re-fires at the owner's next load instead of vanishing while their stale publish overwrites the
        /// host's simulated record (r1 m5). Only the mark's OWN owner can clear it: the caller passes the
        /// SENDER's player id and stable id, never anything the payload claims. Returns how many cleared -
        /// zero is a refusal and says why, or (ABSENCE-HANDBACK-1 F7) a mark KEPT because hand-back interiors are
        /// still pending: this ack is then the PAPERWORK half only (PaperworkAcked ends StorePaperwork's guard) and
        /// the last HostInteriorAck clears the mark.</summary>
        public static int HostClearMarkOnReturnAck(string ownerPid, string ownerStable)
        {
            int n = 0, held = 0;
            try
            {
                var kill = new List<string>();
                foreach (var kv in _marks)
                {
                    var m = kv.Value;
                    if (m == null || !m.OwnerBack) continue;
                    bool isOwner = (!string.IsNullOrEmpty(ownerPid)    && m.OwnerPid    == ownerPid)
                                || (!string.IsNullOrEmpty(ownerStable) && m.OwnerStable == ownerStable);
                    if (!isOwner) continue;
                    m.PaperworkAcked = true;
                    if (m.PendingInteriors.Count == 0) kill.Add(kv.Key);
                    else
                    {
                        held++;
                        Plugin.Logger.LogInfo($"[Absence] return of '{ownerPid}' acknowledged (paperwork) - mark kept until its "
                                            + $"{m.PendingInteriors.Count} hand-back interior(s) are applied.");
                    }
                }
                foreach (var s in kill) { _marks.Remove(s); n++; }
                if (n > 0)
                {
                    _returnLogged.Remove(ownerPid ?? "");
                    Plugin.Logger.LogInfo($"[Absence] return of '{ownerPid}' acknowledged - mark cleared.");
                }
                else if (held == 0)
                    Plugin.Logger.LogInfo($"[Absence] return ack from '{ownerPid}' names no mark of theirs that is back - "
                                        + "ignored (already cleared, or the sender is not that mark's owner).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] return ack: {ex.Message}"); }
            return n;
        }

        /// <summary>HOST (r2 F4): a return has been sent for this stable and is still waiting for the ack.
        /// While that is true the reconcile neither re-sends it nor re-designates a simulator.</summary>
        public static bool IsReturnSent(string ownerStable)
            => _marks.TryGetValue(ownerStable ?? "", out var m) && m != null && m.OwnerBack && m.ReturnSent;

        /// <summary>HOST (r2 F4): the owner went absent again, so the sent-but-unacked return is void and
        /// the normal designation takes over.</summary>
        public static void HostClearReturnSent(string ownerStable)
        {
            if (_marks.TryGetValue(ownerStable ?? "", out var m) && m != null)
            { m.ReturnSent = false; m.PendingInteriors.Clear(); m.PaperworkAcked = false; }   // F7: nothing is held for an absent owner
        }

        /// <summary>HOST: put one mark back from the manifest (clear-then-apply restore, MPServer).
        /// SimulatorPid is deliberately NOT restored - a player id from the previous session names
        /// nobody here - so the mark comes back SUSPENDED and the next reconcile designates a simulator
        /// and sends the hand-over. SinceDay is the whole point of persisting it and is kept exactly.</summary>
        public static void HostRestoreMark(string ownerStable, string ownerPid, List<string> addresses, int sinceDay,
                                          string lastSimulatorPid = "", List<string>? truthInWorld = null)
        {
            if (string.IsNullOrEmpty(ownerStable)) return;
            _marks[ownerStable] = new AbsenceMark
            {
                OwnerStable  = ownerStable,
                OwnerPid     = ownerPid ?? "",
                SimulatorPid = "",
                Addresses    = addresses == null ? new List<string>() : new List<string>(addresses),
                SinceDay     = sinceDay,
                // P3-C: the previous session's simulator is nobody HERE, but the NAME it resolves to is
                // still the truthful answer to "who ran my shops" - the toast is the only reader.
                LastSimulatorPid = lastSimulatorPid ?? "",
            };
            // fold 2 (D1 b): which of these addresses' host WORLD copy (restored with this save) is the absence truth.
            try
            {
                var m = _marks[ownerStable];
                foreach (var a in truthInWorld ?? new List<string>())
                    if (!string.IsNullOrEmpty(a) && m.Addresses.Exists(x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase)))
                        m.TruthInWorld.Add(a);
                if (m.TruthInWorld.Count > 0)
                    Plugin.Logger.LogInfo($"[Absence] restored mark of '{m.OwnerPid}': the host's world copy is the absence truth for "
                                        + $"{m.TruthInWorld.Count} address(es) [{string.Join(", ", m.TruthInWorld)}].");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] world-truth restore '{ownerStable}': {ex.Message}"); }
        }

        /// <summary>HOST: nobody in this owner's company is online, so nothing simulates (D1) and no NEW
        /// mark is made - but an existing one is SUSPENDED (SimulatorPid cleared), never dropped: its
        /// SinceDay is the only record of when the absence began and the return leg needs it. Returns
        /// true when a mark is present, which is the caller's signal to spare it from the dead sweep.</summary>
        public static bool HostSuspendMark(string ownerStable)
        {
            if (string.IsNullOrEmpty(ownerStable) || !_marks.TryGetValue(ownerStable, out var m)) return false;
            if (!string.IsNullOrEmpty(m.SimulatorPid))
            {
                Plugin.Logger.LogInfo($"[Absence] mark for '{m.OwnerPid}' suspended (nobody in the company online); "
                                    + $"simulator was '{m.SimulatorPid}', absent since day {m.SinceDay}.");
                // r1 MAJOR-1: a suspend is a drop for whoever WAS simulating - it must give the state back.
                HostSendDrop(m.SimulatorPid, m.OwnerStable, m.OwnerPid, m.Addresses, "mark suspended, nobody in the company online");
                m.SimulatorPid = "";
            }
            return true;
        }

        /// <summary>Every machine's view of who simulates what (MergerState, additive).</summary>
        public static List<AbsenceInfo> HostSnapshot()
        {
            var list = new List<AbsenceInfo>();
            foreach (var kv in _marks)
                list.Add(new AbsenceInfo
                {
                    OwnerPid = kv.Value.OwnerPid, OwnerStable = kv.Value.OwnerStable,
                    SimulatorPid = kv.Value.SimulatorPid, SinceDay = kv.Value.SinceDay,
                    Addresses = new List<string>(kv.Value.Addresses),
                });
            list.Sort((a, b) => string.CompareOrdinal(a.OwnerStable, b.OwnerStable));
            return list;
        }

        /// <summary>HOST: reset with the world (new world / manifest restore).</summary>
        public static void HostReset()
        {
            _marks.Clear(); _returnLogged.Clear(); _snapQueue.Clear(); _lastReturnLine = ""; _handbacks.Clear();
            _awaitFlush.Clear(); _hostStint.Clear();   // ABSENCE-HANDBACK-1 F6 / F3
        }

        // ── B2 hand-over send (host) ──────────────────────────────────────────
        // r2 F1a: the third element says whether this pair belongs to the RETURN LEG. A return's snapshot
        // is vouched even when the host copy is empty (the host copy IS the truth for a marked address);
        // a hand-over's is not, and queues false.
        private static readonly List<(string addr, string pid, bool returnLeg)> _snapQueue = new();   // paced: one per tick

        /// <summary>HOST: hand the designated simulator its payload, then PACE one interior snapshot
        /// per tick to it. The host itself applies locally instead of sending to itself.</summary>
        public static void SendHandover(AbsenceMark m, bool sameSimulator = false)
        {
            if (m == null) return;
            try
            {
                // r4 C2/F2: the host now FILES a simulator's publish of these shops under the OWNER's
                // stable, so this reads the freshest record of them instead of the copy frozen at the
                // moment the owner dropped (which re-installed rewound delivery/plan/licensing days).
                string json = MPServer.PaperworkJsonForStable(m.OwnerStable);
                var p = new MergerHandoverPayload
                {
                    OwnerPid      = m.OwnerPid,
                    OwnerStable   = m.OwnerStable,
                    SimulatorPid  = m.SimulatorPid,
                    SinceDay      = m.SinceDay,
                    Addresses     = new List<string>(m.Addresses),
                    PaperworkJson = json,
                    Drop          = false,
                    Marks         = HostSnapshot(),
                    SameSimulator = sameSimulator,   // H-STANDINTILL-2 T1
                };
                int bytes = string.IsNullOrEmpty(json) ? 0 : System.Text.Encoding.UTF8.GetByteCount(json);
                Plugin.Logger.LogInfo($"[Absence] hand-over of '{m.OwnerPid}' ({m.Addresses.Count} addresses, "
                                    + $"{bytes} bytes paperwork) -> '{m.SimulatorPid}'{(sameSimulator ? " (same stand-in again: its own till is kept)" : "")}.");

                if (m.SimulatorPid == MPConfig.PlayerId) ApplyHandover(p);            // the host is the simulator
                else MPServer.SendToPlayer(m.SimulatorPid, MessageEnvelope.Create(MessageType.MergerHandover, "host", p));

                // The interiors follow, PACED — one per host tick (a company can hold many shops and a
                // snapshot is the megabyte class; the direct send is a heal path, never a burst).
                foreach (var a in m.Addresses)
                {
                    if (m.SimulatorPid == MPConfig.PlayerId || string.IsNullOrEmpty(a)) continue;
                    // r6 F3 (m-b): set-like. A re-send loop used to re-queue EVERY address while Tick drains
                    // one per second, so an owner with more than 10 shops grew the queue without bound. A
                    // pair already waiting is already going to be sent.
                    bool queued = false;
                    foreach (var q in _snapQueue)
                        if (q.pid == m.SimulatorPid && string.Equals(q.addr, a, StringComparison.OrdinalIgnoreCase))
                        { queued = true; break; }
                    if (!queued) _snapQueue.Add((a, m.SimulatorPid, false));
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] hand-over send: {ex.Message}"); }
        }

        /// <summary>HOST (r1 MAJOR-1): tell ONE machine to STOP simulating ONE owner. It goes to the OLD
        /// simulator BEFORE a re-designation hands that same owner to somebody else - without it the old
        /// simulator keeps the veil exception, the promoted staff and the installed paperwork, and TWO
        /// machines book and publish the same shops. The host undoes locally instead of messaging itself,
        /// and that owner's queued interior snapshots to that machine are purged first (r1 m1).</summary>
        public static void HostSendDrop(string simPid, string ownerStable, string ownerPid,
                                        List<string> addresses, string why)
        {
            if (string.IsNullOrEmpty(simPid)) return;
            try
            {
                var addrs = new HashSet<string>(addresses ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                _snapQueue.RemoveAll(q => q.pid == simPid && addrs.Contains(q.addr ?? ""));
                // ABSENCE-HANDBACK-1 F6: a CLIENT stand-in's last work reaches the host only through its final flush (F5),
                // so no snapshot of these addresses leaves the host (Tick) until that machine acks "standin-flushed" or
                // disconnects. Only for a machine that is online now - an offline one flushes nothing.
                if (simPid != MPConfig.PlayerId && MPServer.IsRunning && MPServer.IsOnlinePid(simPid) && addrs.Count > 0)
                {
                    foreach (var a in addrs) if (!string.IsNullOrEmpty(a)) _awaitFlush[a] = (simPid, ownerStable);   // (no ?? here: it would mark the parameter maybe-null for the flow analysis below)
                    Plugin.Logger.LogInfo($"[Absence] waiting for '{simPid}''s final interior flush of {addrs.Count} address(es) of "
                                        + $"'{ownerPid}' before any snapshot of them is sent.");
                }
                else ForgetStandInSeeded(simPid, addrs);   // R4: no final flush comes, so its seed confirmations end here
                HostNoteHandback(simPid, ownerStable, ownerPid, addresses);   // H-STANDINTILL-2 T5: BEFORE the local apply below
                Plugin.Logger.LogInfo($"[Absence] '{simPid}' stops simulating '{ownerPid}' ({why}).");
                var p = new MergerHandoverPayload
                {
                    OwnerPid     = ownerPid ?? "",
                    OwnerStable  = ownerStable ?? "",
                    SimulatorPid = simPid,
                    Addresses    = new List<string>(addresses ?? new List<string>()),
                    Drop         = true,
                    Marks        = HostSnapshot(),
                };
                if (simPid == MPConfig.PlayerId) ApplyHandover(p);
                else MPServer.SendToPlayer(simPid, MessageEnvelope.Create(MessageType.MergerHandover, "host", p));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] drop send: {ex.Message}"); }
        }

        // == H-STANDINTILL-2 T5 - THE HAND-BACK WINDOW (host) ====================
        // A stand-in publishes its paperwork once more right before it stops (HandBackFlush). By the time that
        // publish reaches the host, the mark no longer names the sender (HostNoteReturn clears SimulatorPid first,
        // a re-designation re-points it, HostDropMark removes it), so MPServer.HostFileSimulatedPaperwork would
        // no longer file those addresses under their OWNER. Every HostSendDrop therefore opens a short window in
        // which the dropped machine's publish of exactly those addresses is still filed under the owner.
        private static readonly List<(string sim, AbsenceMark mark, int at)> _handbacks = new();
        private const int HandbackWindowMs = 60000;

        private static void HostNoteHandback(string simPid, string ownerStable, string ownerPid, List<string>? addresses)
        {
            try
            {
                if (string.IsNullOrEmpty(simPid) || string.IsNullOrEmpty(ownerStable)) return;
                int now = Environment.TickCount;
                _handbacks.RemoveAll(h => unchecked(now - h.at) > HandbackWindowMs
                                          || (h.sim == simPid && h.mark.OwnerStable == ownerStable));
                _handbacks.Add((simPid, new AbsenceMark
                {
                    OwnerStable = ownerStable, OwnerPid = ownerPid ?? "", SimulatorPid = simPid,
                    Addresses = new List<string>(addresses ?? new List<string>()), LastSimulatorPid = simPid,
                }, now));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] hand-back window: {ex.Message}"); }
        }

        /// <summary>HOST: true while any hand-back window is open (MPServer's filing gate).</summary>
        public static bool HostHasHandbacks => _handbacks.Count > 0;

        /// <summary>HOST: the owners this sender was told to stop standing in for in the last 60 s, as
        /// mark-shaped records (owner stable/pid + the addresses) for MPServer.HostFileSimulatedPaperwork.</summary>
        public static List<AbsenceMark> HostHandbackMarksFor(string senderPid)
        {
            var l = new List<AbsenceMark>();
            try
            {
                if (string.IsNullOrEmpty(senderPid) || _handbacks.Count == 0) return l;
                int now = Environment.TickCount;
                _handbacks.RemoveAll(h => unchecked(now - h.at) > HandbackWindowMs);
                foreach (var h in _handbacks)
                    if (h.sim == senderPid && h.mark != null && h.mark.Addresses.Count > 0) l.Add(h.mark);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] hand-back window read: {ex.Message}"); }
            return l;
        }

        // == MERGER PHASE 3-C - THE RETURN LEG, HOST SIDE (C1) =================
        /// <summary>A pre-P3-C build reads SimulatorPid and ignores any hand-over not addressed to it,
        /// so the return leg stamps THIS - never a real player id - into that field.</summary>
        public const string ReturnSentinel = "return-leg";

        private static string _lastReturnLine = "";
        /// <summary>HOST, TestDrive (C6): the last return this session ("pid: N addresses").</summary>
        public static string LastReturnLine => _lastReturnLine;

        /// <summary>HOST, MAIN THREAD (C1): the RETURN. The owner is back and their own save has loaded,
        /// so they are given the state of exactly the businesses that were simulated in their absence:
        ///   - the PAPERWORK the host holds for those addresses (D13 - the host copy is the source: the
        ///     simulator's publishes were filed under the owner's stable at every publish, and the r6 F1
        ///     guard has been holding them against the returned owner's own republish). It is split out
        ///     of a DESERIALISED COPY of the stored entry, so the stored TEXT is never mutated;
        ///   - the INTERIORS, paced exactly like the hand-over's (one per tick through the same queue),
        ///     and VOUCHED even when the host copy is empty (r2 F1a);
        /// and the mark is then flagged ReturnSent - r2 F4: it is the OWNER'S ACK that clears it, never
        /// this send, so a delivery that dies mid-flight re-fires instead of vanishing. No filed paperwork
        /// is not a failure: the interiors still go and the owner's own state stands for everything else
        /// (D2). D15: the simulator is not consulted and need not be online - nothing here reads it.</summary>
        public static bool HostSendReturn(AbsenceMark m, string ranByName, string ownerPid)
        {
            if (m == null || !m.OwnerBack) return false;
            string owner = string.IsNullOrEmpty(ownerPid) ? (m.OwnerPid ?? "") : ownerPid;
            var addresses = new List<string>(m.Addresses ?? new List<string>());
            addresses.RemoveAll(string.IsNullOrEmpty);
            try
            {
                string json = "";
                int parts = 0, biz = 0;
                var marked = new HashSet<string>(addresses, StringComparer.OrdinalIgnoreCase);
                string stored = MPServer.PaperworkJsonForStable(m.OwnerStable);
                if (!string.IsNullOrEmpty(stored) && marked.Count > 0)
                {
                    BusinessPaperworkPayload? copy = null;
                    try { copy = Newtonsoft.Json.JsonConvert.DeserializeObject<BusinessPaperworkPayload>(stored); }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] return: owner entry parse '{m.OwnerStable}': {ex.Message}"); }
                    if (copy != null)
                    {
                        // `copy` is a throwaway deserialisation - the surgery empties it, never the store.
                        var mine = PaperworkSync.SplitOutAddresses(copy, marked, out parts);
                        biz = mine.Businesses?.Count ?? 0;   // HELPER CONTRACT: counted before anything moves it
                        if (parts > 0)
                        {
                            mine.PlayerId = owner;
                            mine.StableId = m.OwnerStable;
                            try { json = Newtonsoft.Json.JsonConvert.SerializeObject(mine); }
                            catch (Exception ex)
                            { Plugin.Logger.LogWarning($"[Absence] return: serialise '{m.OwnerStable}': {ex.Message}"); json = ""; parts = 0; }
                        }
                    }
                }
                int bytes = string.IsNullOrEmpty(json) ? 0 : System.Text.Encoding.UTF8.GetByteCount(json);
                var p = new MergerHandoverPayload
                {
                    OwnerPid      = owner,
                    OwnerStable   = m.OwnerStable,
                    SimulatorPid  = ReturnSentinel,
                    SinceDay      = m.SinceDay,
                    Addresses     = new List<string>(addresses),
                    PaperworkJson = json,
                    Return        = true,
                    RanByName     = ranByName ?? "",
                    Marks         = HostSnapshot(),
                };
                int snaps = 0;
                // ABSENCE-HANDBACK-1 F7: every address stays HELD (owner uploads refused, storage ops 'busy') until the owner
                // acks its interior as applied; the host owner gets no snapshots, so nothing is pending for it.
                m.PendingInteriors.Clear();
                m.PaperworkAcked = false;
                // ABSENCE-HANDBACK-1 R1 (a): a hand-back exists only where the host holds a TRUTH for the address - its F2
                // freeze or a stored owner / stand-in upload. Where it holds none, all it could send is its own replica
                // (blank or stale, stamped return-authoritative): nothing is sent, nothing is held (F4 / F9), and the owner
                // is told so its return exception for that address closes - the owner's own copy stands.
                var handBack = new List<string>();
                var noCopy   = new List<string>();
                if (owner != MPConfig.PlayerId)
                    foreach (var a in addresses)
                    {
                        if (InteriorSync.HostHoldsTruth(a)) { handBack.Add(a); continue; }
                        noCopy.Add(a);
                        m.TruthInWorld.Remove(a);   // fold 2 (D1 b): no hand-back, no hold - its record ends here
                        _snapQueue.RemoveAll(q => q.returnLeg && q.pid == owner && string.Equals(q.addr, a, StringComparison.OrdinalIgnoreCase));
                        Plugin.Logger.LogInfo($"[Absence] no hand-back for '{a}': the host holds no copy - the owner's own copy stands.");
                    }
                foreach (var a in handBack) m.PendingInteriors.Add(a);
                if (owner == MPConfig.PlayerId) ApplyReturn(p);          // the host is the owner: no wire, no snapshots
                else
                {
                    MPServer.SendToPlayer(owner, MessageEnvelope.Create(MessageType.MergerHandover, "host", p));
                    if (noCopy.Count > 0)   // after the return payload, on the same lane (the owner has opened its return set)
                        MPServer.SendToPlayer(owner, MessageEnvelope.Create(MessageType.MergerHandover, "host", new MergerHandoverPayload
                        {
                            OwnerPid = owner, OwnerStable = m.OwnerStable ?? "", SimulatorPid = ReturnSentinel,
                            Ack = NoHandbackAck, Addresses = new List<string>(noCopy),
                        }));
                    foreach (var a in handBack)
                    {
                        bool queued = false;
                        foreach (var q in _snapQueue)
                            if (q.pid == owner && string.Equals(q.addr, a, StringComparison.OrdinalIgnoreCase))
                            { queued = true; break; }
                        if (!queued) { _snapQueue.Add((a, owner, true)); snaps++; }
                    }
                }
                if (parts == 0)
                    Plugin.Logger.LogInfo($"[Absence] return to '{owner}': no filed paperwork for {addresses.Count} addresses "
                                        + $"(their own state stands; {snaps} snapshots queued).");
                else
                    Plugin.Logger.LogInfo($"[Absence] return to '{owner}' ({addresses.Count} addresses, {bytes} bytes paperwork "
                                        + $"covering {biz} businesses, {snaps} snapshots) - simulated since day {m.SinceDay}.");
                _lastReturnLine = $"{owner}: {addresses.Count} addresses";
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] return send: {ex.Message}"); }
            // r2 F4: the mark is NOT cleared here. Delivery is only PROVEN by the owner's "return-applied"
            // ack (HostClearMarkOnReturnAck), so an owner who drops during the paced interior send - and
            // reconnects before the 10 s reconcile re-marks them - still has a mark to re-fire from
            // instead of a stale publish overwriting the host's simulated record (r1 m5). The flag is
            // in-memory only and deliberately NOT persisted: a host restart re-fires the return. While it
            // is set the reconcile neither re-sends nor re-designates, so C5's "a second return sends
            // nothing" still holds. StorePaperwork's OwnerBack guard therefore ends at the ACK, one hop
            // later - harmless: the owner sends the ack at the end of ApplyReturn, before its own next
            // paperwork publish, so the guard is already gone when that publish reaches the host.
            // ABSENCE-HANDBACK-1 F7: that ack now sets PaperworkAcked (which is what ends the guard); the MARK
            // lives on until every address in PendingInteriors has been acked as applied (HostInteriorAck).
            m.ReturnSent = true;
            return true;
        }

        /// <summary>HOST, 1 Hz (chained off MergerFlip.Tick): one queued interior snapshot per tick.
        /// P3-C: ALSO the owner-side recurrence for a return payload that arrived before this machine's
        /// world was ready (this tick runs on every machine, not just the host).</summary>
        public static void Tick()
        {
            try
            {
                if (_heldHandover.Count > 0) ReleaseHeldHandovers();   // ABSENT-OWNER-GATES-1 (C4): the world-settled edge
                if (_heldReturn != null)
                {
                    bool ready = false;
                    try { ready = SaveGameManager.Current?.BuildingRegistrations != null; } catch { }
                    if (ready) { var held = _heldReturn; _heldReturn = null; ApplyReturn(held); }
                }
                if (_snapQueue.Count == 0 || !MPServer.IsRunning) return;
                // ABSENCE-HANDBACK-1 F6: the FIRST entry whose address is not waiting for a client stand-in's final flush
                // (a hand-back / re-designation must carry that machine's last work, not the host's older copy).
                int pick = -1;
                for (int i = 0; i < _snapQueue.Count; i++)
                {
                    var qe = _snapQueue[i];
#if BAMP_DEV
                    if (qe.returnLeg && DevHoldReturn) continue;   // rig lever `absence holdreturn on`: the return-leg drain only
#endif
                    if (_awaitFlush.Count > 0 && _awaitFlush.ContainsKey(qe.addr ?? "")) continue;
                    pick = i; break;
                }
                if (pick < 0) return;
                var (addr, pid, returnLeg) = _snapQueue[pick];
                _snapQueue.RemoveAt(pick);
                // fold 2 (D1 c): after a host restart the persisted world truth becomes the stored copy HERE, so a stand-in's
                // seed is served from it (authoritative) instead of the host's replica.
                InteriorSync.HostHoldsTruth(addr);
                if (returnLeg)
                {
                    // B5: what the owner is handed, and the host's live copy beside it (they differ only if the host's world
                    // moved after the freeze).
                    string sentFp = InteriorSync.FingerprintHostSend(addr);
                    _fpSent[addr] = sentFp;
                    Plugin.Logger.LogInfo($"[Absence] hand-back '{addr}' -> '{pid}' sent=[{sentFp}] hostLive=[{InteriorSync.Fingerprint(addr)}].");
                }
                // r2 F1a: vouchEmpty for a RETURN only - the host's copy of a marked address is the truth
                // even when it holds zero items, so the returned owner's apply must not skip it as
                // non-authoritative. A hand-over keeps the old "never vouch an empty list" rule.
                InteriorSync.SendSnapshotToPlayer(addr, pid, forceItemAuthority: true, vouchEmpty: returnLeg);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] paced snapshot: {ex.Message}"); }
        }

        // ══ ABSENCE-HANDBACK-1 (owner-approved 2026-09-29, decision 42) - HOST side ══════════════════
        // F6: address -> (the client stand-in whose final flush is awaited, its owner's stable). Main thread.
        private static readonly Dictionary<string, (string sim, string ownerStable)> _awaitFlush = new(StringComparer.OrdinalIgnoreCase);
        // F3: addresses the HOST has started standing in for since its last freeze (the start check runs once per stint).
        private static readonly HashSet<string> _hostStint = new(StringComparer.OrdinalIgnoreCase);
        // B5: the last fingerprints per address (log + the `absence fp` rig verb only).
        private static readonly Dictionary<string, string> _fpFrozen  = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _fpSent    = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _fpApplied = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, (string host, string live, bool mismatch)> _fpAck = new(StringComparer.OrdinalIgnoreCase);
        private static string _returnStable = "";   // OWNER: the stable the return named (rides the interior ack)
#if BAMP_DEV
        /// <summary>Rig lever (`absence holdreturn on|off`): hold ONLY the return-leg snapshot drain.</summary>
        internal static bool DevHoldReturn;
#endif

        /// <summary>fold 2 (D1 c), HOST: does a mark name this address AND record its host WORLD copy as the absence truth?</summary>
        public static bool HostTruthInWorld(string addressKey)
        {
            var m = HostMarkFor(addressKey);
            return m != null && m.TruthInWorld.Contains(addressKey);
        }

        /// <summary>fold 2 (D1 b), HOST, MAIN THREAD: the host's world copy of this marked address is now the absence truth -
        /// recorded with the mark (persisted). Not once the mark's hold for it has ended (handed back and acked / refused).</summary>
        public static void HostNoteTruthInWorld(string addressKey, string why)
        {
            try
            {
                var m = HostMarkFor(addressKey);
                if (m == null) return;
                if (m.ReturnSent && !m.PendingInteriors.Contains(addressKey)) return;
                if (m.TruthInWorld.Add(addressKey))
                    Plugin.Logger.LogInfo($"[Absence] '{addressKey}': the host's world copy is the absence truth for '{m.OwnerPid}' ({why}) - "
                                        + "recorded with the mark.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] world-truth note '{addressKey}': {ex.Message}"); }
        }

#if BAMP_DEV
        /// <summary>Rig lever `absence forgetcopy` (host, DEV): the host holds no truth for this address - the world record too.</summary>
        internal static void DevForgetTruthInWorld(string addressKey)
        {
            try { HostMarkFor(addressKey)?.TruthInWorld.Remove(addressKey); } catch { }
        }
#endif

        /// <summary>HOST: the mark whose Addresses name this address, or null. One scan of a small table.</summary>
        public static AbsenceMark? HostMarkFor(string addressKey)
        {
            if (string.IsNullOrEmpty(addressKey) || _marks.Count == 0) return null;
            foreach (var kv in _marks)
            {
                var m = kv.Value;
                if (m?.Addresses == null) continue;
                foreach (var a in m.Addresses)
                    if (string.Equals(a, addressKey, StringComparison.OrdinalIgnoreCase)) return m;
            }
            return null;
        }

        /// <summary>F9, HOST, MAIN THREAD: is this ONLINE owner's shop held for the absence hand-back - the return not sent
        /// yet, or sent and this address's interior not yet acked as applied? Then their machine still holds the stale
        /// pre-absence copy, and an op routed there would be undone by the hand-back (goods duplicated).</summary>
        public static bool HostHandbackHeld(string addressKey, string ownerPid)
        {
            var m = HostMarkFor(addressKey);
            if (m == null || string.IsNullOrEmpty(ownerPid) || m.OwnerPid != ownerPid) return false;
            return !m.ReturnSent || m.PendingInteriors.Contains(addressKey);
        }

        /// <summary>F4, HOST: the returned owner uploaded a held address after its hand-back was sent - their apply of it
        /// has not been acked, so it goes again (set-like: an entry already waiting is not doubled).</summary>
        internal static void HostRequeueHandback(AbsenceMark m, string addressKey)
        {
            try
            {
                if (m == null || string.IsNullOrEmpty(addressKey) || !m.ReturnSent || !m.PendingInteriors.Contains(addressKey)) return;
                string owner = m.OwnerPid ?? "";
                if (owner.Length == 0 || owner == MPConfig.PlayerId) return;
                foreach (var q in _snapQueue)
                    if (q.pid == owner && string.Equals(q.addr, addressKey, StringComparison.OrdinalIgnoreCase)) return;
                _snapQueue.Add((addressKey, owner, true));
                Plugin.Logger.LogInfo($"[Absence] hand-back of '{addressKey}' re-queued for '{owner}': their own upload arrived while the "
                                    + "hold is open (their apply of it has not been acknowledged).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] hand-back re-queue '{addressKey}': {ex.Message}"); }
        }

        /// <summary>F6, HOST: is `senderPid` the dropped client stand-in whose final flush of this address is awaited?</summary>
        public static bool HostAwaitsFlushFrom(string senderPid, string addressKey)
            => _awaitFlush.Count > 0 && !string.IsNullOrEmpty(addressKey) && !string.IsNullOrEmpty(senderPid)
               && _awaitFlush.TryGetValue(addressKey, out var w) && w.sim == senderPid;

        /// <summary>F6, HOST, MAIN THREAD: the dropped stand-in acked "standin-flushed" - every address of that owner it was
        /// holding is released for its snapshots (the flush uploads precede this ack on the same lane).</summary>
        public static void HostStandInFlushed(string senderPid, string ownerStable, List<string>? delivered)
        {
            try
            {
                int n = 0;
                if (_awaitFlush.Count > 0)
                {
                    var gone = new List<string>();
                    foreach (var kv in _awaitFlush)
                        if (kv.Value.sim == senderPid && (string.IsNullOrEmpty(ownerStable) || kv.Value.ownerStable == ownerStable)) gone.Add(kv.Key);
                    foreach (var a in gone) _awaitFlush.Remove(a);
                    n = gone.Count;
                }
                ForgetStandInSeeded(senderPid, null);   // R4: its last upload is in; a later stint re-confirms
                Plugin.Logger.LogInfo($"[Absence] stand-in '{senderPid}' flushed {delivered?.Count ?? 0} interior(s) - {n} address(es) "
                                    + "released for their snapshots.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] stand-in flush ack from '{senderPid}': {ex.Message}"); }
        }

        /// <summary>F6, HOST, MAIN THREAD: a disconnect ends any wait for that machine's flush (its last accepted upload stands).</summary>
        public static void HostPeerGone(string pid)
        {
            try
            {
                ForgetStandInSeeded(pid, null);   // R4: a reconnected stand-in re-confirms from its new hand-over
                if (_awaitFlush.Count == 0 || string.IsNullOrEmpty(pid)) return;
                var gone = new List<string>();
                foreach (var kv in _awaitFlush) if (kv.Value.sim == pid) gone.Add(kv.Key);
                foreach (var a in gone) _awaitFlush.Remove(a);
                if (gone.Count > 0)
                    Plugin.Logger.LogInfo($"[Absence] stand-in '{pid}' disconnected before its final interior flush - {gone.Count} address(es) "
                                        + "released; the host's last accepted upload of each stands.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] stand-in disconnect '{pid}': {ex.Message}"); }
        }

        /// <summary>F7, HOST, MAIN THREAD: the RETURNED OWNER applied these hand-back interiors. The sender must be the mark's
        /// owner (by player id or stable - never anything the payload claims). Each acked address leaves PendingInteriors
        /// (its hold is released); a mark whose paperwork is acked and whose interiors are all applied is removed. Returns
        /// how many marks were cleared (the caller broadcasts on > 0).</summary>
        public static int HostInteriorAck(string senderPid, string senderStable, List<string>? addresses, string? refusedWhy = null)
        {
            int cleared = 0;
            try
            {
                foreach (var addr in addresses ?? new List<string>())
                {
                    if (string.IsNullOrEmpty(addr)) continue;
                    AbsenceMark? hit = null;
                    foreach (var kv in _marks)
                    {
                        var m = kv.Value;
                        if (m == null || !m.PendingInteriors.Contains(addr)) continue;
                        bool isOwner = (!string.IsNullOrEmpty(senderPid)    && m.OwnerPid    == senderPid)
                                    || (!string.IsNullOrEmpty(senderStable) && m.OwnerStable == senderStable);
                        if (isOwner) { hit = m; break; }
                    }
                    if (hit == null)
                    {
                        Plugin.Logger.LogInfo($"[Absence] hand-back ack for '{addr}' from '{senderPid}' names no pending hand-back of theirs - ignored.");
                        continue;
                    }
                    hit.PendingInteriors.Remove(addr);
                    hit.TruthInWorld.Remove(addr);   // fold 2 (D1 b): the hold for it has ended (applied or refused)
                    // ABSENCE-RESTART-DROP-1 E2b: a hand-back RE-QUEUED by the owner's own upload (HostRequeueHandback) is void
                    // once this address is acked or refused - left queued it went out again after the mark had cleared.
                    try
                    {
                        string hbOwner = hit.OwnerPid ?? "";
                        string hbAddr = addr;
                        int dq = _snapQueue.RemoveAll(q => q.returnLeg && string.Equals(q.addr, hbAddr, StringComparison.OrdinalIgnoreCase)
                                                           && ((hbOwner.Length > 0 && q.pid == hbOwner) || q.pid == senderPid));
                        if (dq > 0)
                            Plugin.Logger.LogInfo($"[Absence] queued hand-back of '{addr}' for '{senderPid}' dropped: that address is "
                                                + $"{(refusedWhy != null ? "refused" : "acknowledged")} ({dq} queued).");
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] hand-back queue clean-up '{addr}': {ex.Message}"); }
                    if (refusedWhy != null)
                    {
                        // R2 (b): the owner REFUSED this hand-back before its commit - released exactly like an applied ack
                        // (no re-queue: the owner has closed its return exception for it), and never silent.
                        _fpAck[addr] = (InteriorSync.FingerprintHostSend(addr), "refused", true);
                        Plugin.Logger.LogWarning($"[Absence] hand-back of '{addr}' REFUSED by '{senderPid}' ({refusedWhy}) - hold released; "
                                               + "the owner's copy stands. MISMATCH");
                        continue;
                    }
                    string hostFp = InteriorSync.FingerprintHostSend(addr);
                    string liveFp = InteriorSync.Fingerprint(addr);
                    bool mismatch = hostFp != liveFp;
                    _fpAck[addr] = (hostFp, liveFp, mismatch);
                    Plugin.Logger.LogInfo($"[Absence] hand-back of '{addr}' acknowledged by '{senderPid}' - hold released; host fp=[{hostFp}] "
                                        + $"live=[{liveFp}]{(mismatch ? " MISMATCH" : "")} ({hit.PendingInteriors.Count} still pending).");
                }
                var kill = new List<string>();
                foreach (var kv in _marks)
                {
                    var m = kv.Value;
                    if (m == null || !m.OwnerBack || !m.ReturnSent || !m.PaperworkAcked || m.PendingInteriors.Count > 0) continue;
                    bool isOwner = (!string.IsNullOrEmpty(senderPid)    && m.OwnerPid    == senderPid)
                                || (!string.IsNullOrEmpty(senderStable) && m.OwnerStable == senderStable);
                    if (isOwner) kill.Add(kv.Key);
                }
                foreach (var s in kill) { _marks.Remove(s); cleared++; }
                if (cleared > 0)
                {
                    _returnLogged.Remove(senderPid ?? "");
                    Plugin.Logger.LogInfo($"[Absence] return of '{senderPid}' acknowledged - mark cleared (paperwork and every hand-back interior applied).");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] hand-back ack from '{senderPid}': {ex.Message}"); }
            return cleared;
        }

        /// <summary>B5 rig read-out (`absence fp &lt;addr&gt;`): the last fingerprints this machine recorded for the address.</summary>
        public static string FingerprintLine(string addressKey)
        {
            string a = addressKey ?? "";
            string G(Dictionary<string, string> d) => d.TryGetValue(a, out var v) ? v : "";
            _fpAck.TryGetValue(a, out var ack);
            int held = 0;
            try { if (MPServer.IsRunning) { var m = HostMarkFor(a); if (m != null && m.PendingInteriors.Contains(a)) held = 1; } } catch { }
            return $"OK absence fp addr='{a}' frozen=[{G(_fpFrozen)}] sent=[{G(_fpSent)}] applied=[{G(_fpApplied)}] "
                 + $"ackHost=[{ack.host ?? ""}] ackLive=[{ack.live ?? ""}] mismatch={(ack.mismatch ? 1 : 0)} held={held}";
        }

        /// <summary>F2, HOST, MAIN THREAD: freeze every address this host stood in for, for that owner.</summary>
        private static void HostFreezeStoodIn(string ownerPid)
        {
            try
            {
                string owner = ownerPid ?? "";
                var addrs = new List<string>();
                foreach (var kv in _simHere) if (kv.Value == owner) addrs.Add(kv.Key);
                foreach (var a in addrs)
                {
                    string fp = InteriorSync.HostFreezeStandIn(a, owner, "the host stops standing in");
                    if (fp.Length > 0) _fpFrozen[a] = fp;
                    _hostStint.Remove(a);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] stand-in freeze for '{ownerPid}': {ex.Message}"); }
        }

        /// <summary>F5, CLIENT stand-in, MAIN THREAD: one last forced upload of every address it stood in for, for that owner,
        /// then the "standin-flushed" ack the host waits for (sent even when nothing was stood in here, so the host never
        /// waits on this machine for nothing). A mid-placement address is left out - its last accepted upload stands.</summary>
        private static void FlushStoodIn(MergerHandoverPayload p)
        {
            try
            {
                if (!MPClient.IsConnected) return;
                string owner = p.OwnerPid ?? "";
                var addrs = new List<string>();
                foreach (var kv in _simHere) if (kv.Value == owner) addrs.Add(kv.Key);
                var delivered = new List<string>();
                int nothing = 0;
                foreach (var a in addrs)
                {
                    if (InteriorSync.ForceOwnerPush(a, "stand-in hand-back", seedOrHeal: true, out bool sentA))
                    {
                        if (sentA) delivered.Add(a);
                        else
                        {
                            nothing++;   // R9: SendLocalOwnerSnapshot's "done" also covers nothing sent (no snapshot / all-zero)
                            Plugin.Logger.LogInfo($"[Absence] stand-in hand-back of '{a}' for '{owner}': nothing to send (no readable copy, or it "
                                                + "reads all-zero here) - the host keeps my last accepted upload of it.");
                        }
                    }
                    else Plugin.Logger.LogInfo($"[Absence] stand-in hand-back of '{a}' for '{owner}': left out (an item is mid-placement "
                                             + "here, or the send failed) - the host keeps my last accepted upload of it.");
                }
                MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.MergerHandover, MPConfig.PlayerId,
                    new MergerHandoverPayload
                    {
                        OwnerPid = owner, OwnerStable = p.OwnerStable ?? "", SimulatorPid = MPConfig.PlayerId,
                        Ack = "standin-flushed", Addresses = delivered,
                    }));
                Plugin.Logger.LogInfo($"[Absence] stand-in hand-back of '{owner}': {delivered.Count}/{addrs.Count} interior(s) flushed to the "
                                    + $"host ({nothing} with nothing to send) - told the host (standin-flushed).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] stand-in flush for '{p?.OwnerPid}': {ex.Message}"); }
        }

        /// <summary>F7, OWNER, MAIN THREAD: one hand-back interior is on my copy - fingerprint it and ack it to the host.</summary>
        private static void AckReturnInterior(string addressKey)
        {
            try
            {
                string fp = InteriorSync.Fingerprint(addressKey);
                _fpApplied[addressKey] = fp;
                Plugin.Logger.LogInfo($"[Absence] hand-back applied '{addressKey}' fp=[{fp}] - acknowledging it to the host.");
                if (MPServer.IsRunning) { MPServer.HostInteriorAck(MPConfig.PlayerId, new List<string> { addressKey }); return; }
                if (MPClient.IsConnected)
                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.MergerHandover, MPConfig.PlayerId,
                        new MergerHandoverPayload
                        {
                            OwnerPid = MPConfig.PlayerId, OwnerStable = _returnStable, SimulatorPid = ReturnSentinel,
                            Ack = "return-interior-applied", Addresses = new List<string> { addressKey },
                        }));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] hand-back ack '{addressKey}': {ex.Message}"); }
        }

        // ══ ABSENCE-HANDBACK-1 review folds (R1 a, R2 b, R4) ══════════════════════════════════════════
        /// <summary>R1 (a), host -> the returned owner (after the return payload): these addresses get NO hand-back - the host
        /// holds no copy of them - so the owner closes its return exception for them. Existing Addresses list.</summary>
        public const string NoHandbackAck = "no-handback";
        /// <summary>R2 (b), owner -> host: the hand-back of this address was refused before its commit (reason in RanByName,
        /// log only). The host releases the hold exactly like "return-interior-applied".</summary>
        public const string RefusedAck = "return-interior-refused";
        /// <summary>R4, client stand-in -> host: the host's authoritative copy of this address is applied on my machine.</summary>
        public const string SeededAck = "standin-seeded";

        // R4, HOST: address -> the client stand-in that has applied the host's copy (a truth) of it. Main thread.
        private static readonly Dictionary<string, string> _standInSeeded = new(StringComparer.OrdinalIgnoreCase);
        // R4, STAND-IN: addresses whose seed confirmation this machine has sent (re-armed by every hand-over / drop).
        private static readonly HashSet<string> _seedAckSent = new(StringComparer.OrdinalIgnoreCase);
        // R1 (a), OWNER: no-hand-back notices that arrived while the return itself was held for world-ready.
        private static readonly HashSet<string> _noHandbackHeld = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>R4, HOST: has this client stand-in applied the host's copy of the address?</summary>
        public static bool HostStandInSeeded(string simPid, string addressKey)
            => _standInSeeded.Count > 0 && !string.IsNullOrEmpty(simPid) && !string.IsNullOrEmpty(addressKey)
               && _standInSeeded.TryGetValue(addressKey, out var s) && s == simPid;

        private static void ForgetStandInSeeded(string simPid, HashSet<string>? addresses)
        {
            try
            {
                if (_standInSeeded.Count == 0 || string.IsNullOrEmpty(simPid)) return;
                var gone = new List<string>();
                foreach (var kv in _standInSeeded)
                    if (kv.Value == simPid && (addresses == null || addresses.Contains(kv.Key))) gone.Add(kv.Key);
                foreach (var a in gone) _standInSeeded.Remove(a);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] stand-in seed forget '{simPid}': {ex.Message}"); }
        }

        /// <summary>R4, HOST, MAIN THREAD: a client stand-in confirmed it applied the host's copy of these addresses. Counted
        /// only from the address's CURRENT stand-in and only where the host holds a truth - with none, its uploads stay
        /// refused and no hand-back follows (the owner's own copy stands).</summary>
        public static void HostNoteStandInSeeded(string senderPid, List<string>? addresses)
        {
            try
            {
                foreach (var a in addresses ?? new List<string>())
                {
                    if (string.IsNullOrEmpty(a)) continue;
                    var m = HostMarkFor(a);
                    if (m == null || string.IsNullOrEmpty(senderPid) || m.SimulatorPid != senderPid)
                    {
                        Plugin.Logger.LogInfo($"[Absence] seed confirmation for '{a}' from '{senderPid}' names no stand-in of it - ignored.");
                        continue;
                    }
                    if (!InteriorSync.HostHoldsTruth(a))
                    {
                        Plugin.Logger.LogInfo($"[Absence] stand-in '{senderPid}' applied a copy of '{a}', but the host holds no copy of it - its "
                                            + "uploads of it stay refused; the owner's own copy stands.");
                        continue;
                    }
                    _standInSeeded[a] = senderPid;
                    Plugin.Logger.LogInfo($"[Absence] stand-in '{senderPid}' applied the host's copy of '{a}' - its uploads of it are accepted from now on.");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] seed confirmation from '{senderPid}': {ex.Message}"); }
        }

        /// <summary>ABSENT-OWNER-GATES-1 (C4), HOST, MAIN THREAD, on a client stand-in's hand-over ack: every address of
        /// that mark the stand-in has not confirmed (R4 seed) gets its interior snapshot queued again (set-like, paced by
        /// Tick). A stand-in that held the hand-over until its world was ready received the first seeds before it simulated
        /// those shops, so it never confirmed them and its uploads stayed refused. One log line per ack that re-queues.</summary>
        public static void HostRequeueUnseeded(string simPid, string ownerStable)
        {
            try
            {
                if (!MPServer.IsRunning || string.IsNullOrEmpty(simPid) || simPid == MPConfig.PlayerId || string.IsNullOrEmpty(ownerStable)) return;
                if (!_marks.TryGetValue(ownerStable, out var m) || m == null || m.SimulatorPid != simPid || m.Addresses == null) return;
                int n = 0;
                foreach (var a in m.Addresses)
                {
                    if (string.IsNullOrEmpty(a) || HostStandInSeeded(simPid, a)) continue;
                    bool queued = false;
                    foreach (var q in _snapQueue)
                        if (q.pid == simPid && string.Equals(q.addr, a, StringComparison.OrdinalIgnoreCase)) { queued = true; break; }
                    if (queued) continue;
                    _snapQueue.Add((a, simPid, false));
                    n++;
                }
                if (n > 0)
                    Plugin.Logger.LogInfo($"[Absence] '{simPid}' applied the hand-over of '{m.OwnerPid}': {n} address(es) it has not confirmed "
                                        + "the host's copy of - their snapshots are queued again.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] re-queue unseeded: {ex.Message}"); }
        }

        /// <summary>R4, CLIENT STAND-IN, MAIN THREAD (the apply's commit point / identical skip, SeedOrHeal only): an
        /// authoritative host copy of an address I stand in for is on my machine - confirm it once per hand-over.</summary>
        public static void NoteSeedApplied(string addressKey, bool authoritative)
        {
            try
            {
                if (MPServer.IsRunning || !MPClient.IsConnected || string.IsNullOrEmpty(addressKey) || !authoritative) return;
                if (!_simHere.TryGetValue(addressKey, out var owner)) return;
                if (!_seedAckSent.Add(addressKey)) return;
                MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.MergerHandover, MPConfig.PlayerId,
                    new MergerHandoverPayload
                    {
                        OwnerPid = owner ?? "", SimulatorPid = MPConfig.PlayerId,
                        Ack = SeededAck, Addresses = new List<string> { addressKey },
                    }));
                Plugin.Logger.LogInfo($"[Absence] stand-in: applied the host's copy of '{addressKey}' (for '{owner}') - told the host (standin-seeded).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] seed confirmation '{addressKey}': {ex.Message}"); }
        }

        /// <summary>R2 (b), OWNER, MAIN THREAD: my apply refused or dropped this hand-back before its commit. The return
        /// exception for it closes (a later heal must not walk through it) and the host is told, so its hold ends; my own
        /// copy stands.</summary>
        public static void RefuseReturnInterior(string addressKey, string why)
        {
            try
            {
                if (string.IsNullOrEmpty(addressKey) || !_returnAddrs.Remove(addressKey)) return;
                Plugin.Logger.LogWarning($"[Absence] hand-back of '{addressKey}' refused here ({why}) - my own copy stands; telling the host "
                                       + "(return-interior-refused).");
                if (MPServer.IsRunning) { MPServer.HostInteriorAck(MPConfig.PlayerId, new List<string> { addressKey }, why); return; }
                if (MPClient.IsConnected)
                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.MergerHandover, MPConfig.PlayerId,
                        new MergerHandoverPayload
                        {
                            OwnerPid = MPConfig.PlayerId, OwnerStable = _returnStable, SimulatorPid = ReturnSentinel,
                            Ack = RefusedAck, Addresses = new List<string> { addressKey }, RanByName = why ?? "",
                        }));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] hand-back refusal '{addressKey}': {ex.Message}"); }
        }

        /// <summary>R1 (a), OWNER, MAIN THREAD: the host holds no copy of these returned addresses, so no hand-back follows -
        /// close their return exception now (kept for a return still held for world-ready).</summary>
        private static void OwnerNoHandback(MergerHandoverPayload p)
        {
            try
            {
                if (MPServer.IsRunning || p.OwnerPid != MPConfig.PlayerId) return;
                foreach (var a in p.Addresses ?? new List<string>())
                {
                    if (string.IsNullOrEmpty(a)) continue;
                    if (_returnAddrs.Remove(a))
                        Plugin.Logger.LogInfo($"[Absence] no hand-back for '{a}': the host holds no copy - my own copy stands.");
                    else if (_heldReturn != null) _noHandbackHeld.Add(a);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] no-hand-back notice: {ex.Message}"); }
        }

        // ══ SIMULATOR SIDE (any machine, main thread) ══════════════════════════
        private static readonly Dictionary<string, string> _simHere = new(StringComparer.OrdinalIgnoreCase);   // addressKey → owner pid
        private static readonly List<AbsenceInfo> _known = new();          // every machine's view (UI/P3-C)
        private static readonly Dictionary<string, string> _promotedStaff = new();   // employee id -> OWNER pid
        private static readonly List<(string Owner, string List, object Item)> _installed = new();
        private static readonly HashSet<string> _idWarned = new();          // Q9: one WARN per installed kind
        // The owner every Install()/InstallDict() tags its entry with. Set by InstallListsFor for the
        // duration of ONE address's install; main thread only, never re-entrant.
        private static string _installOwner = "";

        /// <summary>Does THIS machine simulate that address for an absent owner? The one read the
        /// veil exception, the interior publisher and the paperwork publish all go through.</summary>
        public static bool SimulatesHere(string addressKey)
            => !string.IsNullOrEmpty(addressKey) && _simHere.Count > 0 && _simHere.ContainsKey(addressKey);

        public static int SimulatedCount => _simHere.Count;
        public static IReadOnlyList<AbsenceInfo> Known => _known;

        /// <summary>H-MERGERHIRE-1: is that address one SOMEBODY is standing in for right now - i.e. does the
        /// host's absence table (fanned to every machine inside MergerState, ApplyStateAbsences above) name it?
        /// True on the ABSENT OWNER's own machines too, which is harmless: they cannot see a partner's picker
        /// while they are not in the session. The one read that answers "can this booking reach its real owner
        /// right now?" without asking the host - a marked address means the owner is away, so a commitment that
        /// only their own save can hold (a recruitment campaign) must not be offered here.</summary>
        public static bool AwayAnywhere(string addressKey)
        {
            if (string.IsNullOrEmpty(addressKey)) return false;
            // Review MEDIUM-4: _known is filled from the MergerState broadcast, which only CLIENTS apply; the host
            // holds the authoritative table itself.
            IEnumerable<AbsenceInfo> table = MPServer.IsRunning ? HostSnapshot() : _known;
            foreach (var a in table)
            {
                if (a?.Addresses == null) continue;
                foreach (var ad in a.Addresses)
                    if (string.Equals(ad, addressKey, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>H-MERGERTRAIN-1: the PID-keyed twin of AwayAnywhere, off the same table. AwayAnywhere asks
        /// "is that ADDRESS being stood in for"; a bench has no address, so the question becomes "is that PLAYER
        /// away right now" - i.e. does the host's absence table (fanned to every machine inside MergerState,
        /// ApplyStateAbsences) name them as an absent owner. The one presence read a MEMBER can make without
        /// asking the host. It is deliberately one-sided: a mark means away, no mark means only "nobody is
        /// standing in", which is why the host still re-checks the owner is CONNECTED before it routes.</summary>
        public static bool OwnerAwayPid(string ownerPid)
        {
            if (string.IsNullOrEmpty(ownerPid)) return false;
            IEnumerable<AbsenceInfo> table = MPServer.IsRunning ? HostSnapshot() : _known;
            foreach (var a in table)
                if (a != null && string.Equals(a.OwnerPid, ownerPid, StringComparison.Ordinal)) return true;
            return false;
        }
        /// <summary>P3-C's removal surface: what B3(d) put into this machine's GameInstance lists (and
        /// into the one Address-keyed map, tagged as an InstalledDictEntry).</summary>
        public static IReadOnlyList<(string Owner, string List, object Item)> InstalledListItems => _installed;

        // ── MERGER PHASE 2 WAVE 4 (D18): the DISPLAY-COPY surface ────────────
        // Wave 4 installs a partner's delivery contracts and logistics plans on a plain CO-MEMBER through
        // THIS installer, tagged under an owner string of its own ("display:<pid>", never a player id), so
        // the save strip (MPSaveCoordinator/OfflineForkSave read InstalledListItems), RemoveInstalled and
        // IsInstalledItem already cover them. An absence tags with a real pid and a display copy never does,
        // so the two are always distinguishable (IsDisplayInstall) - and r2 MAJOR-2 makes them MUTUALLY
        // EXCLUSIVE per owner as well: becoming that owner's stand-in lifts their display copies first
        // (CompanyLists.SuspendOwner, from ApplyHandover) and the end of the simulation puts them back
        // (CompanyLists.ReinstallOwner, from UndoLocal). Both sets in the lists at once would run every
        // agreement twice.
        public static string DisplayOwnerTag(string ownerPid) => "display:" + (ownerPid ?? "");

        /// <summary>WAVE 4: install one address's list items out of a bundle under `ownerTag`. The same
        /// call B3(d) makes, with the tag chosen by the caller.</summary>
        public static int InstallListsForDisplay(string addr, BusinessPaperworkPayload bundle, HashSet<string> owned, string ownerTag)
            => InstallListsFor(addr, bundle, owned, ownerTag);

        /// <summary>WAVE 4: lift every item installed under `ownerTag` back out (RemoveInstalled's own
        /// per-owner filter - an absence's items are tagged with a pid and are never touched).</summary>
        public static int RemoveInstalledForOwner(string ownerTag) => RemoveInstalled(ownerTag);

        /// <summary>THE EXECUTION-GUARD PREDICATE: is this list element one THIS machine installed (an
        /// absence hand-over's, or a wave-4 display copy)? A native pass must never run one it does not
        /// also simulate.</summary>
        public static bool IsTaggedInstall(object item) => IsInstalledItem(item);

        /// <summary>WAVE 4 r2 (review MAJOR-1): is this list element a DISPLAY COPY - an item installed
        /// under a "display:&lt;pid&gt;" tag, which must never run and never reach a save? An install tagged
        /// with a REAL pid is the opposite case: this machine stands in for that absent owner, the item is
        /// that owner's live agreement and the native passes are exactly what must run it. IsTaggedInstall
        /// cannot tell the two apart (it is a reference scan), so every wave-4 predicate uses THIS one.</summary>
        public static bool IsDisplayInstall(object item)
        {
            if (item == null || _installed.Count == 0) return false;
            foreach (var e in _installed)
                if (ReferenceEquals(e.Item, item))
                    return e.Owner != null && e.Owner.StartsWith("display:", StringComparison.Ordinal);
            return false;
        }

        /// <summary>WAVE 4 r2 (review MAJOR-3): whose absence is this machine standing in for at `addressKey`?
        /// "" when nobody - the building is mine, or not simulated here. The routed replays tag their installs
        /// with THIS pid so the save strip keeps them out of my .hsg and the return leg lifts them.</summary>
        public static string OwnerSimulatedFor(string addressKey)
            => !string.IsNullOrEmpty(addressKey) && _simHere.Count > 0 && _simHere.TryGetValue(addressKey, out var o) ? (o ?? "") : "";

        /// <summary>WAVE 4 r2 (review MAJOR-3): the STAND-IN's own replay of a routed edit. The same installer
        /// the hand-over uses, TAGGED with the ABSENT OWNER's pid - the items are that owner's agreements:
        /// stripped from this machine's save, published back under the owner by the filing, and lifted by the
        /// return leg. Upsert is on for the same reason the return path sets it: an incoming row REPLACES the
        /// row already under its key instead of being refused.</summary>
        public static int InstallListsTagged(string addr, BusinessPaperworkPayload bundle, HashSet<string> owned, string ownerPid)
        {
            bool wasTagged = _tagInstalls, wasUpsert = _returnUpsert;
            _tagInstalls = true; _returnUpsert = true;
            try { return InstallListsFor(addr, bundle, owned, ownerPid); }
            finally { _tagInstalls = wasTagged; _returnUpsert = wasUpsert; }
        }

        /// <summary>WAVE 4 r2 (review MAJOR-3): adopt an object THIS machine built natively into the tagged
        /// set, for a stand-in's routed creation - the item goes into the game list through the game's own
        /// code and only its BOOKKEEPING is added here. Same effect as an install: save strip, undo and the
        /// display predicate all see it.</summary>
        public static bool RegisterInstalledFor(string ownerPid, string listName, object item)
        {
            if (item == null || string.IsNullOrEmpty(listName)) return false;
            foreach (var e in _installed) if (ReferenceEquals(e.Item, item)) return false;
            _installed.Add((ownerPid ?? "", listName, item));
            return true;
        }

        /// <summary>WAVE 4 r2 (review MAJOR-3): drop the tag record of an item SOMEONE ELSE has already taken
        /// out of the game list (a replace-by-id). Leaving it would orphan the record, and the save strip's
        /// restore would put the dead object back - two plans with one id.</summary>
        public static int ForgetInstalled(object item)
        {
            if (item == null) return 0;
            int n = 0;
            for (int i = _installed.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_installed[i].Item, item)) { _installed.RemoveAt(i); n++; }
            return n;
        }

        /// <summary>WAVE 4 (V2c): the OPERATOR's own replay of a routed plan edit. The very same installer,
        /// UNTAGGED - these items are the operator's REAL agreements: they must sit in its save and must be
        /// run by its passes, which is exactly what the tag would prevent (the P3-C return path flips the
        /// same flag for the same reason).</summary>
        public static int InstallListsUntagged(string addr, BusinessPaperworkPayload bundle, HashSet<string> owned, string owner)
        {
            bool wasTagged = _tagInstalls, wasUpsert = _returnUpsert;
            _tagInstalls = false; _returnUpsert = true;
            try { return InstallListsFor(addr, bundle, owned, owner); }
            finally { _tagInstalls = wasTagged; _returnUpsert = wasUpsert; }
        }
        public static IReadOnlyCollection<string> PromotedStaff => _promotedStaff.Keys;

        public static List<string> SimulatedAddresses()
        { var l = new List<string>(_simHere.Keys); l.Sort(StringComparer.OrdinalIgnoreCase); return l; }

        /// <summary>Every machine: adopt the host's absence table off the MergerState broadcast, so
        /// P3-C and any later UI know who simulates what without a second message.</summary>
        public static void ApplyStateAbsences(MergerStatePayload s)
        {
            _known.Clear();
            if (s?.Absences != null) _known.AddRange(s.Absences);
            StopSimulatingWhatNoMarkNames();   // P3-C C3
            RequestResendIfInstallsLost();     // r4 F3
        }

        private static readonly HashSet<string> _resendAsked = new();   // "ownerPid|address" (G3): ONE re-ask per hand-over
        private static readonly HashSet<string> _resendStopped = new();   // G3: the 'stopped asking' line, once per key per hand-over

        // ── ABSENT-OWNER-GATES-1 (C4): a hand-over that arrives before this machine's world is ready ──
        // After a host restart the hand-over reached the stand-in in the LOBBY and was applied before its world existed:
        // no registrations, so no staff, lists or till were taken over, yet the shops were recorded as simulated and the
        // resend net never fired. The LATEST payload per owner is held here and applied when the world settles (the
        // MPWorldReady settled edge, read by Tick); a drop for that owner cancels it. MAIN THREAD (like every table here).
        private static readonly Dictionary<string, MergerHandoverPayload> _heldHandover = new(StringComparer.Ordinal);
        // ABSENT-OWNER-GATES-1 G1 (review fold): the load each held hand-over belongs to - this machine's served load ticket
        // (MPClient.ServedLoadGen; 0 before any load was served on this connection) when it was held. A held hand-over is
        // discarded when this machine settles in a DIFFERENT served load, and every held one is discarded when the host starts
        // a world from its lobby after it was sent (DiscardHeldHandovers, from MPClient.HandleStartGame). Before, a hand-over
        // from a world whose load failed back to the lobby was applied onto the same addresses of the next world. MAIN THREAD.
        private static readonly Dictionary<string, int> _heldLoadGen = new(StringComparer.Ordinal);
        /// <summary>G1: the host started a world from its lobby - every held hand-over predates that world. Main thread.
        /// If the host still names this machine as a stand-in there, the resend net asks for the hand-over again.</summary>
        public static void DiscardHeldHandovers(string why)
        {
            try
            {
                if (_heldHandover.Count == 0) return;
                foreach (var h in _heldHandover.Values)
                    Plugin.Logger.LogInfo($"[Absence] held hand-over for '{h?.OwnerPid}' discarded - {why}.");
                _heldHandover.Clear(); _heldLoadGen.Clear();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] discard held hand-overs: {ex.Message}"); }
        }
        private static string HeldKey(MergerHandoverPayload p)
            => !string.IsNullOrEmpty(p?.OwnerStable) ? p!.OwnerStable : (p?.OwnerPid ?? "");
        private static bool HeldFor(string ownerPid, string ownerStable)
        {
            foreach (var kv in _heldHandover)
            {
                var h = kv.Value;
                if (h == null) continue;
                if (!string.IsNullOrEmpty(ownerStable) && h.OwnerStable == ownerStable) return true;
                if (!string.IsNullOrEmpty(ownerPid) && h.OwnerPid == ownerPid) return true;
            }
            return false;
        }
        private static void CancelHeld(MergerHandoverPayload drop)
        {
            try
            {
                if (_heldHandover.Count == 0 || drop == null) return;
                var gone = new List<string>();
                foreach (var kv in _heldHandover)
                {
                    var h = kv.Value;
                    if (h == null
                        || (!string.IsNullOrEmpty(drop.OwnerStable) && h.OwnerStable == drop.OwnerStable)
                        || (!string.IsNullOrEmpty(drop.OwnerPid) && h.OwnerPid == drop.OwnerPid))
                        gone.Add(kv.Key);
                }
                foreach (var k in gone)
                {
                    _heldHandover.Remove(k); _heldLoadGen.Remove(k);
                    Plugin.Logger.LogInfo($"[Absence] held hand-over for '{drop.OwnerPid}' cancelled - the host dropped the mark before this world was ready.");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] cancel held hand-over: {ex.Message}"); }
        }
        /// <summary>The settled edge: apply every held hand-over once the world is ready. Called from Tick.</summary>
        private static void ReleaseHeldHandovers()
        {
            try
            {
                if (_heldHandover.Count == 0 || !MPWorldReady.IsSettled) return;
                var held = new List<KeyValuePair<string, MergerHandoverPayload>>(_heldHandover);
                _heldHandover.Clear();
                var gens = new Dictionary<string, int>(_heldLoadGen, StringComparer.Ordinal);
                _heldLoadGen.Clear();
                int nowGen = 0; try { nowGen = MPServer.IsRunning ? 0 : MPClient.ServedLoadGen; } catch { }
                foreach (var kv in held)
                {
                    var h = kv.Value;
                    if (h == null) continue;
                    // G1: held during one served load, settling in another - not this world's hand-over.
                    if (gens.TryGetValue(kv.Key, out int g) && g != 0 && g != nowGen)
                    {
                        Plugin.Logger.LogInfo($"[Absence] held hand-over for '{h.OwnerPid}' discarded - it arrived during load {g}, this world is load {nowGen}.");
                        continue;
                    }
                    Plugin.Logger.LogInfo($"[Absence] this world is ready - applying the held hand-over for '{h.OwnerPid}'.");
                    ApplyHandover(h);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] release held hand-overs: {ex.Message}"); }
        }

        /// <summary>r4 F3 (C3): a CLIENT simulator whose SCENE reloaded without disconnecting still holds
        /// the host's mark but has lost every local install, and the host's own re-seed only covers the
        /// case where the HOST is the simulator - so that machine ran nothing for the absent owner and
        /// nothing ever noticed. Detect it off the MergerState broadcast (this machine is named as the
        /// simulator, yet SimulatesHere is false) and ask the host to hand it over again. No new message
        /// type and no new field: this type ALREADY travels client -> host carrying nothing but Ack, so
        /// the request is Ack="resend". It recurs with the 10s broadcast until served (D15: the host
        /// holds every push, so a lost request costs nothing); the log line is once per owner.</summary>
        private static void RequestResendIfInstallsLost()
        {
            try
            {
                if (MPServer.IsRunning || !MPClient.IsConnected || _known.Count == 0) return;
                foreach (var a in _known)
                {
                    if (a == null || a.SimulatorPid != MPConfig.PlayerId) continue;
                    if (HeldFor(a.OwnerPid ?? "", a.OwnerStable ?? "")) continue;   // ABSENT-OWNER-GATES-1 (C4): applied at the settled edge
                    bool lost = false; string lostAddr = "";
                    // ABSENT-OWNER-GATES-1 (C4): 'simulated here' with NO building registration in a ready world is lost
                    // too (a hand-over applied before the world existed took nothing over).
                    bool settled = false; try { settled = MPWorldReady.IsSettled; } catch { }
                    foreach (var addr in a.Addresses ?? new List<string>())
                    {
                        if (string.IsNullOrEmpty(addr)) continue;
                        if (!SimulatesHere(addr)) { lost = true; lostAddr = addr; break; }
                        if (settled && GameStatePatcher.FindRegistration(addr) == null) { lost = true; lostAddr = addr; break; }
                    }
                    if (!lost) continue;
                    // ABSENT-OWNER-GATES-1 G3 (review fold): ONE re-ask per (owner, address) per hand-over - a stand-in that
                    // truly has no registration for a mark address re-asked every 10 s forever. The next applied hand-over
                    // (ApplyHandover) re-arms it; until then one line says the asking stopped.
                    string rk = (a.OwnerPid ?? "") + "|" + lostAddr;
                    if (!_resendAsked.Add(rk))
                    {
                        if (_resendStopped.Add(rk))
                            Plugin.Logger.LogWarning($"[Absence] '{lostAddr}' for '{a.OwnerPid}' is still not running here after one re-send request - "
                                                   + "not asking again until the next hand-over.");
                        continue;
                    }
                    Plugin.Logger.LogInfo($"[Absence] installs for '{a.OwnerPid}' are gone here (scene churn) - "
                                        + "asking the host to re-send the hand-over.");
                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.MergerHandover, MPConfig.PlayerId,
                        new MergerHandoverPayload
                        {
                            OwnerPid = a.OwnerPid, OwnerStable = a.OwnerStable ?? "",   // G4: the nullable warning ed9b3bd's '?? ""' test raised
                            SimulatorPid = MPConfig.PlayerId, Ack = "resend",
                        }));
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] re-send request: {ex.Message}"); }
        }

        /// <summary>B3 — the simulator applies a hand-over. TRULY idempotent, and this is HOW (r1
        /// MAJOR-2, which the old one-line "idempotent" claim got wrong): one payload names exactly ONE
        /// absent owner, and the first thing it does is UndoLocal(THAT owner) - that owner's addresses
        /// leave the exception set, its promoted records are demoted and its installed list items are
        /// lifted back out - so the install below always starts from nothing and a re-send can never
        /// double-install (the old bare list.Add spent the delivery-contract money a second time).
        /// A DIFFERENT absent owner simulated on this same machine is left completely alone (MAJOR-6).</summary>
        public static void ApplyHandover(MergerHandoverPayload p)
        {
            if (p == null) return;
            try
            {
                if (p.Ack == NoHandbackAck) { OwnerNoHandback(p); return; }   // R1 (a): host -> returned owner, not a hand-over
                // R4: every hand-over (and every drop) re-arms this machine's seed confirmations for its addresses.
                foreach (var sa in p.Addresses ?? new List<string>()) if (!string.IsNullOrEmpty(sa)) _seedAckSent.Remove(sa);
                if (p.Drop)
                {
                    CancelHeld(p);   // ABSENT-OWNER-GATES-1 (C4)
                    HandBackFlush(p.OwnerPid, "the host dropped the mark");   // H-STANDINTILL-2 T5: while the addresses are still ours
                    // ABSENCE-HANDBACK-1 F2 / F5: the INTERIORS go back too, while the addresses are still ours. The HOST
                    // freezes its live world into its stored copy (the hand-back source); a CLIENT stand-in uploads each one
                    // last time and acks "standin-flushed" (the host holds their snapshots until then).
                    if (MPServer.IsRunning) HostFreezeStoodIn(p.OwnerPid);
                    else FlushStoodIn(p);
                    ForgetHeld(p.OwnerPid);
                    UndoLocal(p.OwnerPid, $"host dropped the mark for '{p.OwnerPid}'");
                    return;
                }
                if (!string.IsNullOrEmpty(p.SimulatorPid) && p.SimulatorPid != MPConfig.PlayerId)
                { Plugin.Logger.LogWarning($"[Absence] hand-over addressed to '{p.SimulatorPid}' arrived here - ignored."); return; }
                // ABSENT-OWNER-GATES-1 (C4): before this machine's world is ready (a lobby, a load) nothing can be taken over -
                // keep the LATEST payload for this owner and apply it at the settled edge (Tick -> ReleaseHeldHandovers).
                if (!MPWorldReady.IsSettled)
                {
                    string hk = HeldKey(p);
                    bool first = !_heldHandover.ContainsKey(hk);
                    _heldHandover[hk] = p;
                    int hg = 0; try { hg = MPServer.IsRunning ? 0 : MPClient.ServedLoadGen; } catch { }
                    _heldLoadGen[hk] = hg;   // G1: the load this hand-over belongs to
                    if (first) Plugin.Logger.LogInfo($"[Absence] hand-over for '{p.OwnerPid}' held until this world is ready.");
                    return;
                }

                BusinessPaperworkPayload bundle = null;
                bool bundleUnreadable = false;   // H-STANDINTILL-2 T3: a PARSE FAILURE is not 'the owner has no till'
                if (!string.IsNullOrEmpty(p.PaperworkJson))
                {
                    try { bundle = Newtonsoft.Json.JsonConvert.DeserializeObject<BusinessPaperworkPayload>(p.PaperworkJson); }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] paperwork parse: {ex.Message}"); }
                    bundleUnreadable = bundle == null;
                }

                // The owner's WHOLE address set: a moving contract names two addresses, so the
                // installer has to know which of them are the owner's to install it exactly once.
                var owned = new HashSet<string>(p.Addresses ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                string owner = p.OwnerPid ?? "";
                // H-STANDINTILL-1: the addresses this machine ALREADY stood in for, for this owner, before this payload
                // (a re-apply / re-send). Only a FRESH stand-in takes the till over below - on a re-apply this machine has
                // been the single writer of that till all along, so its own till is newer than any bundle.
                var tillAlreadyHere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in _simHere) if (kv.Value == owner) tillAlreadyHere.Add(kv.Key);
                // H-STANDINTILL-2 T1: the host says this machine is the SAME stand-in as before (a re-send, or the
                // re-designation back after a disconnect blip - whose Reset emptied _simHere above but left the till
                // live). Its own till is then the newest record: keep it. Only for a till this process really held
                // in that stint - the SAME registration object it stood in with - so a restarted game or a reloaded
                // world (whose till came from a load) still takes the owner's till over.
                if (p.SameSimulator) KeepHeldTills(owner, p.Addresses, tillAlreadyHere);
                UndoLocal(owner, $"re-applying the hand-over for '{owner}'", restoreDisplay: false);   // MAJOR-2: undo THEN install
                // WAVE 4 r2 (review MAJOR-2): this machine is about to hold that owner's REAL items. Its
                // DISPLAY COPIES of the same agreements must go first, or both sets sit in the lists at once
                // and every wholesale pass would order the goods and charge the fee twice. The registry is
                // KEPT, so the return leg can put the display copies back.
                try { CompanyLists.SuspendOwner(owner, "now simulating that owner's businesses here"); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] display-copy suspend: {ex.Message}"); }
                foreach (var addr in p.Addresses ?? new List<string>())
                {
                    if (string.IsNullOrEmpty(addr)) continue;
                    // ABSENCE-HANDBACK-1 F3: the host's first start of a stint - its live world must equal the stored copy
                    // before F1 starts serving the live world as the truth. Once per stint (a re-apply keeps the host's work).
                    if (MPServer.IsRunning && !_hostStint.Contains(addr) && !tillAlreadyHere.Contains(addr))
                    { _hostStint.Add(addr); InteriorSync.HostAlignStandInStart(addr, owner); }
                    _simHere[addr] = owner;                  // (a)+(b)+(e): the veil exception, the interior
                                                              // publisher and the paperwork publish all read this
                    if (!tillAlreadyHere.Contains(addr))
                    {
                        if (bundleUnreadable)   // H-STANDINTILL-2 T3: never empty a till over a bundle we could not read
                            Plugin.Logger.LogWarning($"[Absence] till of '{addr}' for '{owner}' NOT taken over: the hand-over "
                                                   + "paperwork did not parse - the local till is left as it is.");
                        else TakeOverTill(addr, bundle, owner);   // H-STANDINTILL-1
                    }
                    int staff = PromoteStaffFor(addr, bundle, owner);        // (c)
                    int items = InstallListsFor(addr, bundle, owned, owner);  // (d)
                    Plugin.Logger.LogInfo($"[Absence] simulating '{addr}' for '{p.OwnerPid}' "
                                        + $"(staff promoted: {staff}, list items installed: {items}).");
                }
                // MIRROR-1 (re-check R4): the marks above are what the creation gate and the regeneration read,
                // so the game's own per-business alert pass for these addresses is asked only now.
                try { TaskMirror.RegenerateStandIn(owner, p.Addresses); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] alert regeneration: {ex.Message}"); }

                // r4 F3 / G3: served - a later loss may ask (and log) once again
                _resendAsked.RemoveWhere(k => k.StartsWith(owner + "|", StringComparison.Ordinal));
                _resendStopped.RemoveWhere(k => k.StartsWith(owner + "|", StringComparison.Ordinal));

                // The ack rides the SAME type back (no second message type): the host logs delivery.
                if (MPClient.IsConnected && !MPServer.IsRunning)
                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.MergerHandover, MPConfig.PlayerId,
                        new MergerHandoverPayload
                        {
                            OwnerPid = p.OwnerPid, OwnerStable = p.OwnerStable, SimulatorPid = MPConfig.PlayerId,
                            Ack = $"{_simHere.Count} addr, {_promotedStaff.Count} staff, {_installed.Count} items",
                        }));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] apply: {ex.Message}"); }
        }

        /// <summary>H-STANDINTILL-1 (user-approved 2026-09-27): a FRESH stand-in's till of the absent owner's shop
        /// (reg.unprocessedCompletedOrders) is REPLACED by the owner's till from the hand-over bundle - the same fill the
        /// return leg uses (PaperworkSync.FillTill) - or EMPTIED when the bundle carries no record of that address. While
        /// the shop was flipped here the till only gathered COPIES (the unchecked native adds, TicketKioskController.cs:144
        /// and SelfServiceEmployee.cs:150, while this player stood inside), which the veil kept off the day roll. Kept, they
        /// were paid AGAIN by this machine's next day roll once it books the shop (BusinessHelper.ProcessDailyOrders
        /// :223-232 has no date check), shipped to the owner in this machine's paperwork, and they replaced the owner's own
        /// unpaid orders in the host's stored record (PaperworkSync.MergeAddresses), which lost those. Taking the owner's
        /// till over carries the owner's unpaid orders through the stint and back exactly once. ONLY the till is touched
        /// (order history and campaigns stay as they were). Main thread.</summary>
        private static void TakeOverTill(string addr, BusinessPaperworkPayload? bundle, string owner)
        {
            try
            {
                var reg = GameStatePatcher.FindRegistration(addr);
                if (reg == null)
                {
                    Plugin.Logger.LogWarning($"[Absence] till of '{addr}' for '{owner}': no registration here - not taken over.");
                    return;
                }
                BusinessPaperwork? biz = null;
                if (bundle?.Businesses != null)
                    foreach (var b in bundle.Businesses)
                        if (b != null && string.Equals(b.AddressKey, addr, StringComparison.OrdinalIgnoreCase)) { biz = b; break; }
                int before = 0; try { before = reg.unprocessedCompletedOrders?.Count ?? 0; } catch { }
                double beforeVal = TillValue(reg);
                int after = PaperworkSync.FillTill(reg, biz?.UnprocessedCompletedOrders);
                double afterVal = TillValue(reg);
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                string src = biz != null
                    ? $"the owner's till from the hand-over bundle ({biz.UnprocessedCompletedOrders?.Count ?? 0} order(s))"
                    : "nothing - the bundle holds no record of this address, so it is emptied";
                Plugin.Logger.LogInfo($"[Absence] till of '{addr}' for '{owner}' taken over: {before} local order(s) worth "
                                    + $"{beforeVal.ToString("F2", inv)} replaced by {src} - now {after} order(s) worth {afterVal.ToString("F2", inv)}.");
                // H-STANDINTILL-2 T2: how old the stored record is. The bundle carries the publisher's game DAY only
                // (BusinessPaperworkPayload.Day - no time of day); within the day it is at most one publish (30 s) old.
                try
                {
                    if (bundle != null)
                    {
                        int today = 0; try { today = GameStateReader.GetGameTime().day; } catch { }
                        Plugin.Logger.LogInfo($"[Absence] till of '{addr}' for '{owner}': the hand-over bundle was published on "
                                            + $"game day {bundle.Day} (today: day {today}; the bundle carries no time of day).");
                        if (bundle.Day > 0 && today > 0 && bundle.Day < today)
                            Plugin.Logger.LogWarning($"[Absence] till of '{addr}' for '{owner}': the hand-over bundle PREDATES today "
                                                   + $"(published day {bundle.Day}, today day {today}) - orders the owner took after that "
                                                   + "publish are not in it.");
                    }
                }
                catch { }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] till take-over '{addr}': {ex.Message}"); }
        }

        /// <summary>What a till is worth at the day roll (TillDupes.ExtraReferenceValue per order - the one formula).</summary>
        private static double TillValue(BuildingRegistration reg)
        {
            double v = 0;
            try
            {
                var till = reg.unprocessedCompletedOrders;
                if (till != null) foreach (var o in till) v += TillDupes.ExtraReferenceValue(o);
            }
            catch { }
            return v;
        }

        // ── H-STANDINTILL-2 T5: the stand-in's hand-back publish ──────────────
        /// <summary>Right before this machine stops standing in for ONE owner (the host's Drop - which is what the owner's
        /// return, a re-designation, a suspend and the dead sweep all send - or the mark-gone sweep), publish its paperwork
        /// once more while the owner's addresses are still in _simHere, so the orders it took since its last 30 s publish
        /// reach the host's copy (filed under the owner through the host's hand-back window). Main thread.</summary>
        private static void HandBackFlush(string ownerPid, string why)
        {
            try
            {
                string owner = ownerPid ?? "";
                int n = 0;
                foreach (var kv in _simHere) if (kv.Value == owner) n++;
                if (n == 0) return;
                var pub = PaperworkSync.FlushNow($"hand-back of '{owner}'");
                Plugin.Logger.LogInfo($"[Absence] hand-back publish for '{owner}' ({n} stood-in address(es), {why}): "
                                    + (pub != null ? $"published day {pub.Day}." : "NOT published (world not settled, not a member, or no session)."));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] hand-back publish for '{ownerPid}': {ex.Message}"); }
        }

        // ── H-STANDINTILL-2 T1: the tills this process stood in with, kept past a disconnect's Reset ──
        // A client's involuntary drop runs Reset (MPClient.OnDisconnected) and THEN writes its disconnect save; the rejoin
        // RELOADS the world (MPSaveCoordinator.ProceedWithLoadData) from that save when the host commits it - else from the
        // host's older stored .hsg. So the registration object is always new after a rejoin, and what proves "the till I
        // stood in with" is its CONTENT: the order count and day-roll value remembered here at the Reset. A restarted
        // game remembers nothing and takes the owner's till over, exactly as before.
        private static readonly Dictionary<string, (string owner, int count, string value)> _heldAtReset = new(StringComparer.OrdinalIgnoreCase);

        private static (int count, string value) TillPrint(string addr)
        {
            var reg = GameStatePatcher.FindRegistration(addr);
            if (reg == null) return (-1, "");
            int c = 0; try { c = reg.unprocessedCompletedOrders?.Count ?? 0; } catch { }
            return (c, TillValue(reg).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        }

        private static void RememberHeld()
        {
            try
            {
                foreach (var kv in _simHere)
                {
                    (int count, string value) tp = (-1, "");
                    try { tp = TillPrint(kv.Key); } catch { }
                    if (tp.count >= 0) _heldAtReset[kv.Key] = (kv.Value ?? "", tp.count, tp.value);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] remember held tills: {ex.Message}"); }
        }

        private static void ForgetHeld(string ownerPid)
        {
            try
            {
                var gone = new List<string>();
                foreach (var kv in _heldAtReset) if (kv.Value.owner == (ownerPid ?? "")) gone.Add(kv.Key);
                foreach (var a in gone) _heldAtReset.Remove(a);
            }
            catch { }
        }

        private static void KeepHeldTills(string owner, List<string>? addresses, HashSet<string> tillAlreadyHere)
        {
            try
            {
                foreach (var addr in addresses ?? new List<string>())
                {
                    if (string.IsNullOrEmpty(addr) || tillAlreadyHere.Contains(addr)) continue;
                    (int count, string value) now = (-1, "");
                    try { now = TillPrint(addr); } catch { }
                    bool held = _heldAtReset.TryGetValue(addr, out var h) && h.owner == owner;
                    if (held && now.count == h.count && now.value == h.value)
                    {
                        tillAlreadyHere.Add(addr);
                        Plugin.Logger.LogInfo($"[Absence] till of '{addr}' for '{owner}' KEPT: the same stand-in again (SameSimulator) "
                                            + $"with the till it stood in with ({now.count} order(s) worth {now.value}) - no take-over from the stored copy.");
                    }
                    else
                        Plugin.Logger.LogInfo($"[Absence] till of '{addr}' for '{owner}': SameSimulator named, but this machine's till "
                                            + (held ? $"({now.count} order(s) worth {now.value}) is not the one it stood in with ({h.count} worth {h.value}) - "
                                                    : "was never stood in with in this game session (restart) - ")
                                            + "taking the owner's till over.");
                }
                // NOT forgotten here: a hand-over that lands while the rejoin's world load is still running finds no
                // registration yet, and the re-send after the load must still be able to match. A Drop or the
                // mark-gone sweep forgets it; the next Reset overwrites it.
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] same-stand-in till check: {ex.Message}"); }
        }

        /// <summary>B5 / re-apply / re-designation: stop simulating for ONE absent owner (r1 MAJOR-6 -
        /// this used to wipe EVERY owner, and a simulator running two of them lost both). Only that
        /// owner's addresses leave the exception set, only that owner's promoted records are demoted
        /// (removed here - the owner's next roster publish re-injects them as display copies) and only
        /// that owner's installed list items are lifted back out.</summary>
        public static void UndoLocal(string ownerPid, string why, bool restoreDisplay = true)
        {
            string owner = ownerPid ?? "";
            var addrs = new List<string>();
            foreach (var kv in _simHere) if (kv.Value == owner) addrs.Add(kv.Key);
            var staff = new List<string>();
            foreach (var kv in _promotedStaff) if (kv.Value == owner) staff.Add(kv.Key);
            bool anyItems = false;
            foreach (var e in _installed) if (e.Owner == owner) { anyItems = true; break; }
            if (addrs.Count == 0 && staff.Count == 0 && !anyItems) return;
            int items = 0, demoted = 0;
            try { items = RemoveInstalled(owner); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] undo lists: {ex.Message}"); }
            try { demoted = RemovePromoted(staff); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] undo staff: {ex.Message}"); }
            foreach (var a in addrs) _simHere.Remove(a);
            foreach (var id in staff) _promotedStaff.Remove(id);
            Plugin.Logger.LogInfo($"[Absence] stopped simulating {addrs.Count} address(es) for '{owner}' ({why}) - "
                                + $"{demoted}/{staff.Count} staff + {items} list item(s) released.");
            // WAVE 4 r2 (review MAJOR-2): the simulation is over, so this machine is a plain co-member for
            // that owner again - its display copies go back in (only for addresses still flipped here, which
            // is the installer's own test). Skipped when the caller is about to install the real items again.
            if (restoreDisplay)
            {
                try { CompanyLists.ReinstallOwner(owner, why); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] display-copy restore: {ex.Message}"); }
            }
        }

        /// <summary>Every owner at once - the scene/session boundary's undo.</summary>
        public static void UndoLocalAll(string why)
        {
            if (_simHere.Count == 0 && _installed.Count == 0 && _promotedStaff.Count == 0) return;
            int addrs = _simHere.Count, staff = _promotedStaff.Count, items = 0, demoted = 0;
            try { items = RemoveInstalled(null); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] undo lists: {ex.Message}"); }
            try { demoted = RemovePromoted(new List<string>(_promotedStaff.Keys)); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] undo staff: {ex.Message}"); }
            _simHere.Clear();
            _promotedStaff.Clear();
            Plugin.Logger.LogInfo($"[Absence] stopped simulating {addrs} address(es) for every owner ({why}) - "
                                + $"{demoted}/{staff} staff + {items} list item(s) released.");
        }

        /// <summary>Scene/session boundary — every machine. r1 MAJOR-4: this does NOT just drop the
        /// tables. MPClient calls it on DISCONNECT with the scene still live and the offline fork's very
        /// next save is the player's OWN .hsg, so the absent owner's contracts, plans, licensing rows
        /// and promoted staff have to be lifted back OUT of the live GameInstance first.</summary>
        public static void Reset()
        {
            RememberHeld();   // H-STANDINTILL-2 T1: BEFORE the undo empties _simHere
            try { UndoLocalAll("session/scene reset"); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] reset undo: {ex.Message}"); }
            _simHere.Clear(); _known.Clear(); _promotedStaff.Clear(); _installed.Clear();
            _snapQueue.Clear(); _idWarned.Clear(); _fieldWarned.Clear(); _resendAsked.Clear(); _resendStopped.Clear();
            // P3-C (C5): a held return payload dies with the connection - the host clears a mark only
            // after a SEND, so the next return re-sends the whole thing.
            _heldReturn = null; _heldLogged = false; _returnAddrs.Clear(); _replaced.Clear(); _lastToastKey = "";
            _standInSeeded.Clear(); _seedAckSent.Clear(); _noHandbackHeld.Clear();   // ABSENCE-HANDBACK-1 R1 (a) / R4
            _heldHandover.Clear(); _heldLoadGen.Clear();   // ABSENT-OWNER-GATES-1 (C4): dies with the session - the host re-sends on the next designation
            _tagInstalls = true; _returnUpsert = false;
        }

        // ── B3(c) staff promotion ─────────────────────────────────────────────
        private static int PromoteStaffFor(string addr, BusinessPaperworkPayload bundle, string owner)
        {
            if (bundle?.Employees == null) return 0;
            int n = 0;
            foreach (var rec in bundle.Employees)
            {
                if (rec == null || !string.Equals(rec.AddressKey, addr, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(rec.EmployeeId)) continue;
                try
                {
                    if (MergerEmployeeSync.PromoteRecord(rec)) { _promotedStaff[rec.EmployeeId] = owner ?? ""; n++; }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] promote '{rec.EmployeeId}': {ex.Message}"); }
            }
            return n;
        }

        // ── B3(d) owner-list installation (tagged, removable, never saved) ────
        private static int InstallListsFor(string addr, BusinessPaperworkPayload bundle, HashSet<string> owned, string owner)
        {
            var l = bundle?.Lists;
            if (l == null) return 0;
            var gi = SaveGameManager.Current;
            if (gi == null) return 0;
            int n = 0;
            _installOwner = owner ?? "";   // every Install()/InstallDict() below tags its entry with this
            try
            {
                if (l.DeliveryContracts != null)
                    foreach (var d in l.DeliveryContracts)
                    {
                        if (d == null || !Same(d.BusinessAddressKey, addr)) continue;
                        var item = NewElement(gi.DeliveryContracts as IList);
                        if (item == null) continue;
                        SetAddr(item, "businessAddress", d.BusinessAddressKey);
                        SetAddr(item, "wholesaleAddress", d.WholesaleAddressKey);
                        SetField(item, "enabled", d.Enabled);
                        SetField(item, "isUrgentOrder", d.IsUrgentOrder);
                        SetField(item, "nextDeliveryDay", d.NextDeliveryDay);
                        SetField(item, "repeatingOrder", d.RepeatingOrder);
                        SetField(item, "deliveryFee", d.DeliveryFee);
                        FillLines(item, "items", d.Items);
                        if (Install("DeliveryContracts", gi.DeliveryContracts as IList, item)) n++;
                    }

                if (l.ImportPartnerships != null)
                    foreach (var ip in l.ImportPartnerships)
                    {
                        if (ip == null || !Same(ip.HeadquartersAddressKey, addr)) continue;
                        var item = NewElement(gi.importPartnerships as IList);
                        if (item == null) continue;
                        SetField(item, "id", ip.Id);
                        SetAddr(item, "headquartersAddress", ip.HeadquartersAddressKey);
                        SetAddr(item, "importAddress", ip.ImportAddressKey);
                        SetField(item, "employeeInstanceId", ip.EmployeeInstanceId);
                        SetField(item, "nextDeliveryDay", ip.NextDeliveryDay);
                        SetField(item, "isRepeatingOrder", ip.IsRepeatingOrder);
                        SetField(item, "daysUntilRepeat", ip.DaysUntilRepeat);
                        SetField(item, "isActive", ip.IsActive);
                        SetField(item, "isUrgentOrder", ip.IsUrgentOrder);
                        SetField(item, "isTarget", ip.IsTarget);
                        FillLines(item, "products", ip.Products);
                        if (Install("importPartnerships", gi.importPartnerships as IList, item)) n++;
                    }

                // ── the four HEADQUARTERS plans: every one of them is keyed on headquartersAddress ──
                if (l.LogisticsManagerPlans != null)
                    foreach (var pl in l.LogisticsManagerPlans)
                    {
                        if (pl == null || !Same(pl.HeadquartersAddressKey, addr)) continue;
                        var item = NewElement(gi.logisticsManagerPlans as IList);
                        if (item == null) continue;
                        SetField(item, "id", pl.Id);                       // the game's 'id' is readonly; reflection sets it, and a refusal only leaves the fresh uuid
                        SetField(item, "assignedEmployeeId", pl.AssignedEmployeeId);
                        SetAddr(item, "headquartersAddress", pl.HeadquartersAddressKey);
                        SetAddr(item, "targetAddress", pl.TargetAddressKey);
                        SetField(item, "isFactory", pl.IsFactory);
                        FillDestinations(item, pl.Destinations);
                        CheckInstalledId("logisticsManagerPlans", item, pl.Id);   // Q9
                        if (Install("logisticsManagerPlans", gi.logisticsManagerPlans as IList, item)) n++;
                    }

                if (l.PricingManagerPlans != null)
                    foreach (var pl in l.PricingManagerPlans)
                    {
                        if (pl == null || !Same(pl.HeadquartersAddressKey, addr)) continue;
                        var item = NewElement(gi.pricingManagerPlans as IList);
                        if (item == null) continue;
                        SetField(item, "id", pl.Id);
                        SetField(item, "assignedEmployeeId", pl.AssignedEmployeeId);
                        SetAddr(item, "headquartersAddress", pl.HeadquartersAddressKey);
                        SetField(item, "supervisedNeighborhood", pl.SupervisedNeighborhood);
                        SetField(item, "nextUpdateDay", pl.NextUpdateDay);
                        SetField(item, "nextUpdateHour", pl.NextUpdateHour);
                        FillStrings(item, "manuallyPricedItems", pl.ManuallyPricedItems);
                        CheckInstalledId("pricingManagerPlans", item, pl.Id);   // Q9
                        if (Install("pricingManagerPlans", gi.pricingManagerPlans as IList, item)) n++;
                    }

                if (l.HrManagerPlans != null)
                    foreach (var pl in l.HrManagerPlans)
                    {
                        if (pl == null || !Same(pl.HeadquartersAddressKey, addr)) continue;
                        var item = NewElement(gi.hrManagerPlans as IList);
                        if (item == null) continue;
                        SetField(item, "id", pl.Id);
                        SetField(item, "assignedEmployeeId", pl.AssignedEmployeeId);
                        SetAddr(item, "headquartersAddress", pl.HeadquartersAddressKey);
                        FillStrings(item, "assignedEmployees", pl.AssignedEmployees);
                        SetField(item, "replaceAbsentEmployees", pl.ReplaceAbsentEmployees);
                        SetField(item, "trainingTarget", pl.TrainingTarget);
                        // CROSS-HR-1 S1/S2: the SHADOW carries the owner's insurance agreement, so a member's
                        // worker assigned to this partner plan reads insured through the game's own lookups
                        // (HasHealthInsurance.cs:27-34 -> HrManagerPlan.HasActiveHealthInsurance :197-207,
                        // which also needs the assignedEmployeeId above to resolve - the merger's injected
                        // manager copy is what resolves it).  Nothing here may CHARGE for it:
                        // HrManagerPlan.PayHealthInsurance runs only from HRManager.WorkDaily, and S3 skips
                        // that for a display install.  Typed assignment, not SetField: healthInsurancePlan is
                        // a public field of a CLASS type, not a value the reflective setter converts.
                        if (pl.HealthInsurancePlanType >= 0
                            && item is Buildings.Office.Headquarters.HrManagerPlan hrShadow)
                            hrShadow.healthInsurancePlan = new Entities.HealthInsurancePlan
                            {
                                planType = (Entities.HealthInsurancePlanType)pl.HealthInsurancePlanType,
                                pricePerDayAndEmployee = pl.PricePerDayAndEmployee,
                            };
                        CheckInstalledId("hrManagerPlans", item, pl.Id);   // Q9
                        if (Install("hrManagerPlans", gi.hrManagerPlans as IList, item)) n++;
                    }

                if (l.HeadhunterPlans != null)
                    foreach (var pl in l.HeadhunterPlans)
                    {
                        if (pl == null || !Same(pl.HeadquartersAddressKey, addr)) continue;
                        var item = NewElement(gi.headhunterPlans as IList);
                        if (item == null) continue;
                        SetField(item, "id", pl.Id);
                        SetField(item, "assignedEmployeeId", pl.AssignedEmployeeId);
                        SetAddr(item, "headquartersAddress", pl.HeadquartersAddressKey);
                        FillStrings(item, "assignedHrPlans", pl.AssignedHrPlans);   // string[2] in the game type
                        SetField(item, "isRecruiting", pl.IsRecruiting);
                        SetField(item, "skillRecruiting", pl.SkillRecruiting);
                        SetField(item, "skillValueTarget", pl.SkillValueTarget);
                        FillStrings(item, "dealBreakerTypes", pl.DealBreakerTypes);
                        SetField(item, "automaticallyReplaceOnRetire", pl.AutomaticallyReplaceOnRetire);
                        SetField(item, "automaticallyReplaceOnResign", pl.AutomaticallyReplaceOnResign);
                        SetField(item, "remainingCandidatesToRecruit", pl.RemainingCandidatesToRecruit);
                        SetField(item, "amountOfCandidatesToRecruitPreference", pl.AmountOfCandidatesToRecruitPreference);
                        CheckInstalledId("headhunterPlans", item, pl.Id);   // Q9
                        if (Install("headhunterPlans", gi.headhunterPlans as IList, item)) n++;
                    }

                if (l.InteriorInstallationFirmContracts != null)
                    foreach (var c in l.InteriorInstallationFirmContracts)
                    {
                        if (c == null || !Same(c.AddressKey, addr)) continue;   // addressToDoTheInstallation
                        var item = NewElement(gi.interiorInstallationFirmContracts as IList);
                        if (item == null) continue;
                        SetAddr(item, "interiorInstallationFirmAddress", c.FirmAddressKey);
                        SetAddr(item, "addressToDoTheInstallation", c.AddressKey);
                        SetField(item, "designName", c.DesignName);
                        SetField(item, "isBlueprint", c.IsBlueprint);
                        SetField(item, "isCompatBlueprint", c.IsCompatBlueprint);
                        SetField(item, "hasDiscontinuedItems", c.HasDiscontinuedItems);
                        SetField(item, "dayOfInstallation", c.DayOfInstallation);
                        SetField(item, "businessTypeName", c.BusinessTypeName);
                        if (Install("interiorInstallationFirmContracts", gi.interiorInstallationFirmContracts as IList, item)) n++;
                    }

                if (l.MovingServiceContracts != null)
                    foreach (var c in l.MovingServiceContracts)
                    {
                        if (c == null) continue;
                        // A move NAMES two addresses, and this runs once PER marked address - install it
                        // on the ORIGIN when the owner holds that, else on the destination, so a move
                        // INTO the company is still installed and neither is installed twice.
                        string org = c.OriginAddressKey ?? "", dst = c.DestinationAddressKey ?? "";
                        if (!(Same(org, addr) || (Same(dst, addr) && !owned.Contains(org)))) continue;
                        var item = NewElement(gi.movingServiceContracts as IList);
                        if (item == null) continue;
                        SetReg(item, "movingCompanyRegistration", c.MovingCompanyAddressKey);
                        SetAddr(item, "originMovingAddress", org);
                        SetAddr(item, "destinationMovingAddress", dst);
                        SetField(item, "movingDay", c.MovingDay);
                        SetField(item, "movingHour", c.MovingHour);
                        SetField(item, "transferBizManSettings", c.TransferBizManSettings);
                        if (Install("movingServiceContracts", gi.movingServiceContracts as IList, item)) n++;
                    }

                if (l.DisabledLicensingFees != null)
                    foreach (var f in l.DisabledLicensingFees)
                    {
                        if (f == null || !Same(f.AddressKey, addr) || AddressOfKey(f.AddressKey) == null) continue;
                        var item = NewElement(gi.disabledLicensingFees as IList);
                        if (item == null) continue;
                        SetAddr(item, "address", f.AddressKey);
                        SetField(item, "itemId", f.ItemId);
                        SetField(item, "day", f.Day);                      // DayOfWeekOrdered (enum from the int)
                        if (Install("disabledLicensingFees", gi.disabledLicensingFees as IList, item)) n++;
                    }

                if (l.PaidLicensingFeesToday != null)
                    foreach (var f in l.PaidLicensingFeesToday)
                    {
                        if (f == null || !Same(f.AddressKey, addr) || AddressOfKey(f.AddressKey) == null) continue;
                        var item = NewElement(gi.paidLicensingFeesToday as IList);   // (Address, string) tuple
                        if (item == null) continue;
                        SetAddr(item, "Item1", f.AddressKey);
                        SetField(item, "Item2", f.ItemId);
                        if (Install("paidLicensingFeesToday", gi.paidLicensingFeesToday as IList, item)) n++;
                    }

                // ── the one MAP: itemsOrderedThisWeekByImporter is keyed by the IMPORTER's address,
                // never by one of the owner's own buildings (P3-A review MAJOR-5) - so the rows to
                // install are the importAddresses of the partnerships THIS address is HQ for. ──
                if (l.ItemsOrderedThisWeekByImporter != null && l.ImportPartnerships != null)
                {
                    var myImportKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var ip in l.ImportPartnerships)
                        if (ip != null && Same(ip.HeadquartersAddressKey, addr) && !string.IsNullOrEmpty(ip.ImportAddressKey))
                            myImportKeys.Add(ip.ImportAddressKey);
                    if (myImportKeys.Count > 0)
                    {
                        var dict = gi.itemsOrderedThisWeekByImporter as IDictionary;
                        foreach (var row in l.ItemsOrderedThisWeekByImporter)
                        {
                            if (row == null || !myImportKeys.Contains(row.AddressKey ?? "")) continue;
                            if (InstallDict("itemsOrderedThisWeekByImporter", dict,
                                            AddressOfKey(row.AddressKey), NewTargetList(dict, row.Items))) n++;
                        }
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] install lists '{addr}': {ex.Message}"); }
            return n;
        }

        /// <summary>P3-C (C2c): FALSE while the RETURNED OWNER re-installs its own businesses' items.
        /// Those items are the owner's real ones - they must never be lifted by an undo and never sit
        /// out that machine's save, which is exactly what the tag causes.</summary>
        private static bool _tagInstalls = true;

        /// <summary>r2 F2: TRUE only while the RETURNED OWNER re-installs its own businesses' lists, and
        /// the one thing it changes is that a dictionary row the returned bundle carries REPLACES the row
        /// already under that key instead of being refused.</summary>
        private static bool _returnUpsert;

        private static bool Install(string listName, IList list, object item)
        {
            if (list == null || item == null) return false;
            list.Add(item);
            if (_tagInstalls) _installed.Add((_installOwner, listName, item));
            return true;
        }

        /// <summary>One installed MAP row - itemsOrderedThisWeekByImporter is an Address-keyed
        /// dictionary, not a list, so its tagged entry carries the key AND the value it put there.</summary>
        public class InstalledDictEntry
        {
            public object Key;
            public object Value;
        }

        private static bool InstallDict(string name, IDictionary dict, object key, object value)
        {
            if (dict == null || key == null || value == null) return false;
            bool have = dict.Contains(key);
            // r2 F2: a RETURN never DELETES the owner's importer rows (RemoveOwnItemsFor leaves that map
            // alone), so a row the returned bundle carries has to REPLACE the one already here - otherwise
            // the owner keeps their pre-absence row for that importer. Rows the bundle does NOT carry are
            // left exactly as they are, so an unmarked building's orders can never be collateral. A
            // HAND-OVER still refuses an existing key: that row belongs to the simulating machine itself.
            if (have && !_returnUpsert) return false;
            dict[key] = value;
            if (_tagInstalls && !have) _installed.Add((_installOwner, name, new InstalledDictEntry { Key = key, Value = value }));
            return true;
        }

        /// <summary>Lift installed items back out of this machine's lists. owner == null means EVERY
        /// owner; otherwise only that owner's entries move and everybody else's stay (r1 MAJOR-6).</summary>
        private static int RemoveInstalled(string? owner)
        {
            var gi = SaveGameManager.Current;
            int n = 0;
            for (int i = _installed.Count - 1; i >= 0; i--)
            {
                var e = _installed[i];
                if (owner != null && e.Owner != owner) continue;
                if (TakeOne(gi, e.List, e.Item)) n++;
                _installed.RemoveAt(i);
            }
            return n;
        }

        /// <summary>Demote promoted records: an absent owner's employee leaves this machine's save
        /// entirely (it was never ours - the promotion took it OUT of the injected registry, so nothing
        /// else strips it). The owner's next roster publish re-injects it as the display copy it was
        /// before B3(c). Returns how many records were actually there.</summary>
        private static int RemovePromoted(List<string> ids)
        {
            if (ids == null || ids.Count == 0) return 0;
            // MERGER PHASE 4b (PEOPLE) P4 r2 (MINOR-6): THIS is the one point where promoted records leave
            // this machine, so it is where the phone relay stops calling their messages MINE. Without it a
            // press arriving after the hand-back still runs a stored closure over records that left the save.
            try { CompanyMessages.ForgetMine(ids, "the stand-in handed those people back"); } catch { }
            int n = 0;
            var wanted = new HashSet<string>(ids, StringComparer.Ordinal);
            try
            {
                var list = SaveGameManager.Current?.EmployeeInstances;
                if (list != null)
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var emp = list[i];
                        if (emp == null || !wanted.Contains(emp.id ?? "")) continue;
                        string dId = emp.id ?? "", dNm = MergerEmployeeSync.StaffNameOf(emp);   // STAFF-EVIDENCE-1: read before the record goes
                        list.RemoveAt(i);
                        n++;
                        try { var ev = MergerEmployeeSync.CountShiftsNaming(SaveGameManager.Current, dId); MergerEmployeeSync.LogStaffRemoval("demote", dId, dNm, ev.shifts, ev.regs); } catch { }
                    }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] demote: {ex.Message}"); }
            foreach (var id in ids) { try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.Remove(id); } catch { } }
            return n;
        }

        /// <summary>Q9: the game's plan 'id' is READONLY - reflection sets it on Mono, but if a runtime
        /// ever refuses the write the plan silently keeps the fresh uuid Activator gave it and every
        /// assignment that names the plan by id misses. WARN once per kind; never abort the install.</summary>
        private static void CheckInstalledId(string kind, object item, string want)
        {
            try
            {
                if (item == null || string.IsNullOrEmpty(want)) return;
                string got = FieldOf(item, "id")?.GetValue(item) as string ?? "";
                if (string.Equals(got, want, StringComparison.Ordinal)) return;
                if (_idWarned.Add(kind))
                    Plugin.Logger.LogWarning($"[Absence] installed {kind} kept id '{got}' instead of '{want}' "
                                           + "(readonly id - assignments naming this plan will not match; once per kind).");
            }
            catch { }
        }

        private static IList ListByName(GameInstance gi, string name)
        {
            if (gi == null) return null;
            switch (name)
            {
                case "DeliveryContracts":                  return gi.DeliveryContracts as IList;
                case "importPartnerships":                 return gi.importPartnerships as IList;
                case "logisticsManagerPlans":              return gi.logisticsManagerPlans as IList;
                case "pricingManagerPlans":                return gi.pricingManagerPlans as IList;
                case "hrManagerPlans":                     return gi.hrManagerPlans as IList;
                case "headhunterPlans":                    return gi.headhunterPlans as IList;
                case "interiorInstallationFirmContracts":  return gi.interiorInstallationFirmContracts as IList;
                case "movingServiceContracts":             return gi.movingServiceContracts as IList;
                case "disabledLicensingFees":              return gi.disabledLicensingFees as IList;
                case "paidLicensingFeesToday":             return gi.paidLicensingFeesToday as IList;
                default: return null;
            }
        }

        /// <summary>The one installed kind that is a MAP, not a list.</summary>
        private static IDictionary DictByName(GameInstance gi, string name)
            => gi != null && name == "itemsOrderedThisWeekByImporter" ? gi.itemsOrderedThisWeekByImporter as IDictionary : null;

        /// <summary>Take one installed entry back out (list element or map row). True when it was there.</summary>
        private static bool TakeOne(GameInstance gi, string name, object item)
        {
            try
            {
                if (item is InstalledDictEntry de)
                {
                    var d = DictByName(gi, name);
                    if (d == null || de.Key == null || !d.Contains(de.Key)) return false;
                    d.Remove(de.Key); return true;
                }
                var list = ListByName(gi, name);
                if (list == null || !list.Contains(item)) return false;
                list.Remove(item); return true;
            }
            catch { return false; }
        }

        /// <summary>Put one taken entry back (the save strip's finally). Never duplicates.</summary>
        private static bool ReAddOne(GameInstance gi, string name, object item)
        {
            try
            {
                if (item is InstalledDictEntry de)
                {
                    var d = DictByName(gi, name);
                    if (d == null || de.Key == null || d.Contains(de.Key)) return false;
                    d[de.Key] = de.Value; return true;
                }
                var list = ListByName(gi, name);
                if (list == null || list.Contains(item)) return false;
                list.Add(item); return true;
            }
            catch { return false; }
        }

        /// <summary>SAVE STRIP — called at BOTH save sites (MPSaveCoordinator's choke point and the
        /// offline-fork save). Everything this machine installed to simulate an ABSENT OWNER is that
        /// owner's, never this save's: the installed list items AND (r1 MAJOR-3) the PROMOTED EMPLOYEE
        /// RECORDS. The promotion deliberately takes a record OUT of the injected registry, and
        /// MPRegisterSync's own save strip gates on exactly that registry (`_injectedStaff.ContainsKey`),
        /// so a promoted record was serialising into the simulator's .hsg as its own employee, assigned
        /// to an address the flip strip had just un-rented. Both come out for the serialization and the
        /// ONE returned action puts both back - call it only after JoinSaveGameThreads. (Every OTHER
        /// partner's injected copies still strip in MPRegisterSync exactly as before - untouched.)</summary>
        public static Action StripInstalledForSave()
        {
            if (_installed.Count == 0 && _promotedStaff.Count == 0) return () => { };
            var gi = SaveGameManager.Current;
            var taken = new List<(string Owner, string List, object Item)>(_installed);
            int removed = 0;
            foreach (var e in taken) if (TakeOne(gi, e.List, e.Item)) removed++;

            var takenStaff = new List<Entities.EmployeeInstance>();
            try
            {
                var list = gi?.EmployeeInstances;
                if (list != null && _promotedStaff.Count > 0)
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var emp = list[i];
                        string id = emp?.id ?? "";
                        if (emp == null || !_promotedStaff.ContainsKey(id)) continue;
                        takenStaff.Add(emp);
                        list.RemoveAt(i);
                        try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.Remove(id); } catch { }
                    }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] save strip (promoted staff): {ex.Message}"); }

            // STAFF-EVIDENCE-1: these records are RESTORED right after serialisation, so this is a
            // once-per-session census with totals, not a per-save line.
            try
            {
                if (takenStaff.Count > 0 && MergerEmployeeSync.StaffEvidenceWanted("save-strip-promoted"))   // review H1: once per session, decided BEFORE the walk
                {
                    int evS = 0, evR = 0;
                    foreach (var emp in takenStaff)
                    { var ev = MergerEmployeeSync.CountShiftsNaming(gi, emp?.id ?? ""); evS += ev.shifts; evR += ev.regs; }
                    MergerEmployeeSync.LogStaffRemoval("save-strip-promoted", $"{takenStaff.Count} record(s)",
                                                       "session total", evS, evR, "save-strip-promoted");
                }
            }
            catch { }

            if (removed > 0 || takenStaff.Count > 0)
                Plugin.Logger.LogInfo($"[Absence] {removed} simulated list item(s) + {takenStaff.Count} promoted staff "
                                    + "record(s) sit out the save (they belong to an absent owner).");
            return () =>
            {
                var g2 = SaveGameManager.Current;
                foreach (var e in taken) ReAddOne(g2, e.List, e.Item);
                try
                {
                    var l2 = g2?.EmployeeInstances;
                    foreach (var emp in takenStaff)
                    {
                        if (emp == null) continue;
                        if (l2 != null && !l2.Contains(emp)) l2.Add(emp);
                        try { Helpers.EmployeeHelper.EmployeeInstancesDictionary[emp.id] = emp; } catch { }
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] save restore (promoted staff): {ex.Message}"); }
            };
        }

        // ── Reflection helpers (the native list element types are not csproj-named) ──
        private const System.Reflection.BindingFlags FieldFlags = System.Reflection.BindingFlags.Public
                                                                | System.Reflection.BindingFlags.NonPublic
                                                                | System.Reflection.BindingFlags.Instance;
        private static readonly HashSet<string> _fieldWarned = new();   // r4 m3: one WARN per type+field

        /// <summary>r4 m3: GetField's DEFAULT flags are Public|Instance, so every helper below silently
        /// skipped a field the game declares private and left Activator's default sitting in the
        /// installed row. NonPublic is included, and the walk goes up the base types because GetField
        /// never sees a base type's private fields. A name that is nowhere on the type is a decompile
        /// drift, not a data problem, so it WARNs once per type+name instead of failing silently.</summary>
        private static System.Reflection.FieldInfo? FieldOf(object o, string name)
        {
            try
            {
                if (o == null || string.IsNullOrEmpty(name)) return null;
                for (var t = o.GetType(); t != null; t = t.BaseType)
                {
                    var f = t.GetField(name, FieldFlags);
                    if (f != null) return f;
                }
                if (_fieldWarned.Add(o.GetType().Name + "." + name))
                    Plugin.Logger.LogWarning($"[Absence] field '{name}' is not on '{o.GetType().Name}' - "
                                           + "that value is NOT installed (once per kind).");
            }
            catch { }
            return null;
        }

        private static object NewElement(IList list)
        {
            try
            {
                var t = list?.GetType();
                var args = t?.GetGenericArguments();
                if (args == null || args.Length != 1) return null;
                return Activator.CreateInstance(args[0]);
            }
            catch { return null; }
        }

        private static void SetField(object o, string name, object val)
        {
            try
            {
                var f = FieldOf(o, name);
                if (f == null || val == null) return;
                f.SetValue(o, f.FieldType.IsEnum ? Enum.ToObject(f.FieldType, val) : Convert.ChangeType(val, f.FieldType));
            }
            catch { }
        }

        private static void SetAddr(object o, string name, string addressKey)
        {
            try
            {
                var a = AddressOfKey(addressKey);
                if (a == null) return;
                FieldOf(o, name)?.SetValue(o, a);
            }
            catch { }
        }

        private static void SetReg(object o, string name, string addressKey)
        {
            try
            {
                var r = RegOfKey(addressKey);
                if (r == null) return;
                FieldOf(o, name)?.SetValue(o, r);
            }
            catch { }
        }

        private static void FillLines(object owner, string fieldName, List<PwItemOrderLine> lines)
        {
            try
            {
                if (lines == null || lines.Count == 0) return;
                var f = FieldOf(owner, fieldName);
                if (f == null) return;
                var list = f.GetValue(owner) as IList;
                if (list == null)
                {
                    list = Activator.CreateInstance(f.FieldType) as IList;
                    if (list == null) return;
                    f.SetValue(owner, list);
                }
                list.Clear();
                var et = f.FieldType.GetGenericArguments();
                if (et.Length != 1) return;
                foreach (var ln in lines)
                {
                    if (ln == null) continue;
                    var e = Activator.CreateInstance(et[0]);
                    SetField(e, "itemName", ln.ItemName);
                    SetField(e, "boxes", ln.Boxes);
                    SetField(e, "amount", ln.Amount);
                    SetField(e, "amountOrderedLastWeek", ln.AmountOrderedLastWeek);
                    SetField(e, "amountOrderedThisWeek", ln.AmountOrderedThisWeek);
                    // HQ-PARITY-1 P1: the DESIGNATED WAREHOUSE travels too.  Without it an installed
                    // partnership drew "Unassigned" with 0 stock, and ImportProduct.GetAmountToBuy
                    // (decompile ImportProduct.cs:44-53) fell back to the raw target because a null
                    // warehouse has nothing to subtract.  A line type that has no such field (the
                    // itemName/targetAmount shapes) is simply not touched - SetAddr no-ops on a missing
                    // field - and an unresolvable key leaves null, exactly as no warehouse does.
                    SetAddr(e, "assignedWarehouse", ln.AssignedWarehouseKey);
                    list.Add(e);
                }
            }
            catch { }
        }

        /// <summary>Fill an ItemAmountTarget list field (itemName + targetAmount) - the shape the
        /// importer map and every logistics destination use.</summary>
        private static void FillTargets(object owner, string fieldName, List<PwItemOrderLine> lines)
        {
            try
            {
                var f = FieldOf(owner, fieldName);
                var et = f?.FieldType.GetGenericArguments();
                if (f == null || et == null || et.Length != 1) return;
                var list = f.GetValue(owner) as IList;
                if (list == null)
                {
                    list = Activator.CreateInstance(f.FieldType) as IList;
                    if (list == null) return;
                    f.SetValue(owner, list);
                }
                list.Clear();
                foreach (var ln in lines ?? new List<PwItemOrderLine>())
                {
                    if (ln == null) continue;
                    var e = Activator.CreateInstance(et[0]);
                    SetField(e, "itemName", ln.ItemName);
                    SetField(e, "targetAmount", ln.Amount);
                    list.Add(e);
                }
            }
            catch { }
        }

        /// <summary>Fill a logistics plan's destinations (each one an address plus its stock targets).</summary>
        private static void FillDestinations(object plan, List<PwLogisticsDestination> dests)
        {
            try
            {
                var f = FieldOf(plan, "destinations");
                var et = f?.FieldType.GetGenericArguments();
                if (f == null || et == null || et.Length != 1) return;
                var list = f.GetValue(plan) as IList;
                if (list == null)
                {
                    list = Activator.CreateInstance(f.FieldType) as IList;
                    if (list == null) return;
                    f.SetValue(plan, list);
                }
                list.Clear();
                foreach (var d in dests ?? new List<PwLogisticsDestination>())
                {
                    if (d == null) continue;
                    var e = Activator.CreateInstance(et[0]);
                    SetAddr(e, "deliveryTargetAddress", d.DeliveryTargetAddressKey);
                    FillTargets(e, "stockTargets", d.StockTargets);
                    list.Add(e);
                }
            }
            catch { }
        }

        /// <summary>Fill a string COLLECTION field, whichever of the three shapes the game used:
        /// List&lt;string&gt; (assignedEmployees, dealBreakerTypes), HashSet&lt;string&gt;
        /// (manuallyPricedItems) or a FIXED string[] (assignedHrPlans is string[2] - a longer DTO list
        /// is truncated to the slots the game allows).</summary>
        private static void FillStrings(object owner, string fieldName, List<string> values)
        {
            try
            {
                if (values == null) return;
                var f = FieldOf(owner, fieldName);
                if (f == null) return;
                if (f.FieldType.IsArray)
                {
                    var arr = f.GetValue(owner) as Array;
                    if (arr == null || arr.Length == 0)
                    {
                        arr = Array.CreateInstance(f.FieldType.GetElementType(), values.Count);
                        f.SetValue(owner, arr);
                    }
                    for (int i = 0; i < values.Count && i < arr.Length; i++) arr.SetValue(values[i], i);
                    return;
                }
                var cur = f.GetValue(owner);
                if (cur == null)
                {
                    cur = Activator.CreateInstance(f.FieldType);
                    if (cur == null) return;
                    f.SetValue(owner, cur);
                }
                var t = cur.GetType();
                t.GetMethod("Clear", Type.EmptyTypes)?.Invoke(cur, null);
                var add = t.GetMethod("Add", new[] { typeof(string) });
                if (add == null) return;
                foreach (var s in values) add.Invoke(cur, new object[] { s ?? "" });
            }
            catch { }
        }

        /// <summary>A fresh VALUE list for the importer map (List&lt;ItemAmountTarget&gt;), built from
        /// the dictionary's own generic argument so the element type is never named here.</summary>
        private static object NewTargetList(IDictionary dict, List<PwItemOrderLine> lines)
        {
            try
            {
                var args = dict?.GetType().GetGenericArguments();
                if (args == null || args.Length != 2) return null;
                var list = Activator.CreateInstance(args[1]) as IList;
                var et = args[1].GetGenericArguments();
                if (list == null || et.Length != 1) return null;
                foreach (var ln in lines ?? new List<PwItemOrderLine>())
                {
                    if (ln == null) continue;
                    var e = Activator.CreateInstance(et[0]);
                    SetField(e, "itemName", ln.ItemName);
                    SetField(e, "targetAmount", ln.Amount);
                    list.Add(e);
                }
                return list;
            }
            catch { return null; }
        }

        /// <summary>An addressKey back to the live Address the game's own lists hold.</summary>
        /// <summary>An addressKey back to the live BuildingRegistration the game's own lists hold.</summary>
        public static BuildingRegistration RegOfKey(string addressKey)
        {
            try
            {
                if (string.IsNullOrEmpty(addressKey)) return null;
                var regs = SaveGameManager.Current?.BuildingRegistrations;
                if (regs == null) return null;
                foreach (var r in regs)
                    if (r != null && string.Equals(GameStateReader.AddressKey(r), addressKey, StringComparison.OrdinalIgnoreCase))
                        return r;
            }
            catch { }
            return null;
        }

        /// <summary>An addressKey back to the live Address the game's own lists hold.</summary>
        public static Address AddressOfKey(string addressKey)
        {
            try { var r = RegOfKey(addressKey); return r == null ? null : new Address(r.StreetName, r.StreetNumber); }
            catch { return null; }
        }

        private static bool Same(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

        private static bool SameSet(List<string> a, List<string> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            var set = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
            foreach (var s in b) if (!set.Contains(s)) return false;
            return true;
        }

        // == MERGER PHASE 3-C - THE RETURN LEG, OWNER SIDE (C2) ================
        // Nothing here is ever done to another player's data: this is the RETURNED OWNER's own machine
        // writing its OWN registrations, its OWN GameInstance lists and its OWN employee records - which
        // is their game (D2: the owner's state wins everywhere EXCEPT the marked addresses, and those are
        // exactly the businesses somebody else ran for them).

        private static MergerHandoverPayload? _heldReturn;   // arrived before this world was ready
        private static bool _heldLogged;
        /// <summary>The addresses of the return payload this machine is currently applying - the ONLY
        /// predicate the round-178 exception in GameStatePatcher reads. One apply each (consumed).</summary>
        private static readonly HashSet<string> _returnAddrs = new(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> _replaced = new();   // C6, this session
        private static string _lastToastKey = "";

        /// <summary>OWNER (C2a, r2 F1b): is this SeedOrHeal snapshot the simulated copy of one of the
        /// addresses the return just named? NON-CONSUMING - the round-178 exception (and the
        /// non-authoritative belt beside it) only ASK. Draining the set inside a guard was r1's M1: the
        /// entry was spent before the apply was final, so a snapshot refused or deferred FURTHER DOWN
        /// left the owner on a stale interior with nothing left in the set to let the re-send through.</summary>
        public static bool IsReturnInterior(string addressKey)
            => !string.IsNullOrEmpty(addressKey) && _returnAddrs.Count > 0 && _returnAddrs.Contains(addressKey);

        /// <summary>OWNER (r2 F1c): the apply has COMMITTED this address's items and designs to my copy,
        /// so the return's one-apply exception for it closes NOW and a later generic snapshot is refused
        /// exactly as before. Called once at the commit point for every SeedOrHeal apply that gets there;
        /// an address no return named is a silent no-op, and an EMPTY host copy drains the set just like
        /// a full one - an address left behind in the set is what would let a much later heal walk
        /// through the exception onto a by-then developed interior (r1 m4). ABSENCE-HANDBACK-1 F7: consuming is
        /// also the moment this machine acks that address to the host ("return-interior-applied"), which releases
        /// the host's hold on it.</summary>
        public static bool ConsumeReturnInterior(string addressKey)
        {
            try
            {
                if (string.IsNullOrEmpty(addressKey) || _returnAddrs.Count == 0) return false;
                if (!_returnAddrs.Remove(addressKey)) return false;
                if (!_replaced.Contains(addressKey)) _replaced.Add(addressKey);
                Plugin.Logger.LogInfo($"[Absence] replaced my '{addressKey}' with the simulated copy.");
                AckReturnInterior(addressKey);   // ABSENCE-HANDBACK-1 F7: the explicit "hand-back applied" edge
                return true;
            }
            catch { return false; }
        }

        /// <summary>OWNER, MAIN THREAD (C2): take back exactly the businesses that were simulated in my
        /// absence. (a) the interiors are the paced snapshots that FOLLOW this payload - this call opens
        /// the one-apply-each exception for them; (b) the books go back onto my registrations; (c) my
        /// stale list items for those addresses go and the returned ones take their place as MY OWN
        /// items (never tagged, never stripped at save); (d) the full staff records are written onto my
        /// real records by id; (e) my next publish carries the returned state. Nothing outside those
        /// addresses is touched. A payload that arrives before my world is ready is HELD and applied
        /// from the 1 Hz tick (recurrence, logged once).</summary>
        public static void ApplyReturn(MergerHandoverPayload p)
        {
            if (p == null) return;
            // r2 F5: a return NAMES ITS ADDRESSEE, exactly as the hand-over path checks its own. A payload
            // whose OwnerPid is not this machine is somebody else's return - a misroute, an older host, or
            // a forgery - and applying it would write another player's simulated businesses onto MY
            // registrations, lists and staff. Refuse it, and say so.
            if (p.OwnerPid != MPConfig.PlayerId)
            {
                Plugin.Logger.LogWarning($"[Absence] return REFUSED: addressed to '{p.OwnerPid}', not to this machine "
                                       + $"('{MPConfig.PlayerId}') - nothing applied.");
                return;
            }
            try
            {
                // fold 2 (D2): a NEWER return replaces the one held here for world-ready - the no-hand-back notices saved for
                // the old one are void (the new return's own notices follow it on the same lane).
                if (_heldReturn != null && !ReferenceEquals(_heldReturn, p) && _noHandbackHeld.Count > 0)
                {
                    Plugin.Logger.LogInfo($"[Absence] a newer return replaces the held one - {_noHandbackHeld.Count} no-hand-back notice(s) "
                                        + "saved for the old one are dropped.");
                    _noHandbackHeld.Clear();
                }
                bool ready = false;
                try { ready = SaveGameManager.Current?.BuildingRegistrations != null; } catch { }
                if (!ready)
                {
                    _heldReturn = p;
                    if (!_heldLogged)
                    {
                        _heldLogged = true;
                        Plugin.Logger.LogInfo("[Absence] a return payload arrived before this machine's world was ready - "
                                            + "held and applied at world-ready.");
                    }
                    return;
                }
                _heldReturn = null; _heldLogged = false;

                var addrs = new HashSet<string>(p.Addresses ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                addrs.Remove("");
                if (addrs.Count == 0)
                { Plugin.Logger.LogInfo($"[Absence] return from the host names no addresses - nothing to apply."); return; }

                // ABSENCE-HANDBACK-1 F8: the hand-back must replace my LIVE objects - forget the per-item / S4-lite baselines,
                // so the apply compares against what I hold instead of keeping "unchanged" live items (my stale stock).
                foreach (var a in addrs) { try { GameStatePatcher.ForgetInteriorBaseline(a); } catch { } }
                _returnStable = p.OwnerStable ?? "";
                // (a) the interiors follow this message on the same lane, one per host tick.
                _returnAddrs.Clear();
                foreach (var a in addrs) _returnAddrs.Add(a);
                if (_noHandbackHeld.Count > 0)   // R1 (a): the host's no-hand-back notice beat this (held) return here
                {
                    foreach (var a in _noHandbackHeld)
                        if (_returnAddrs.Remove(a))
                            Plugin.Logger.LogInfo($"[Absence] no hand-back for '{a}': the host holds no copy - my own copy stands.");
                    _noHandbackHeld.Clear();
                }

                BusinessPaperworkPayload? bundle = null;
                if (!string.IsNullOrEmpty(p.PaperworkJson))
                {
                    try { bundle = Newtonsoft.Json.JsonConvert.DeserializeObject<BusinessPaperworkPayload>(p.PaperworkJson); }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] return paperwork parse: {ex.Message}"); }
                }

                int books = 0, items = 0, staffUpd = 0, staffAdd = 0;
                if (bundle != null)
                {
                    try { books = PaperworkSync.ApplyReturnedBusinesses(bundle, addrs); }        // (b)
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] return books: {ex.Message}"); }
                    try { items = ReinstallOwnLists(bundle, addrs, p.OwnerPid ?? ""); }          // (c)
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] return lists: {ex.Message}"); }
                    try { ApplyReturnedStaff(bundle, addrs, out staffUpd, out staffAdd); }       // (d)
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] return staff: {ex.Message}"); }
                }
                else
                    Plugin.Logger.LogInfo($"[Absence] return for {addrs.Count} address(es) carried no paperwork - "
                                        + "my own records stand; the interiors still replace my copies.");

                try { PaperworkSync.MarkDirty(); } catch { }                                     // (e)
                Plugin.Logger.LogInfo($"[Absence] return applied: {addrs.Count} address(es), {books} business record(s), "   // (f)
                                    + $"{items} list item(s), {staffUpd} staff updated + {staffAdd} added "
                                    + $"(simulated since day {p.SinceDay}).");
                ShowReturnToast(p);                                                              // (g)
                // (h) r2 F4: THE ACK. The host keeps the mark - and with it StorePaperwork's OwnerBack
                // guard - until this lands, so a drop during the paced interior send can no longer leave
                // this machine half-returned with no mark to re-fire from (r1 m5). Sent HERE, at the end
                // of the apply and well before this machine's next paperwork publish (PaperworkSync's
                // cadence is 30 s), so the guard outliving the apply by one hop costs nothing. The
                // interiors are consumed independently as they arrive; a lost ack simply means the return
                // re-fires at the next load, which the _peerApplying one-shot already tolerates.
                // ABSENCE-HANDBACK-1 F7: this is the PAPERWORK ack only - each interior is acked on its own
                // as it commits (ConsumeReturnInterior), and the host keeps the mark until all of them are.
                if (MPServer.IsRunning)
                    MPServer.HostReturnAck(MPConfig.PlayerId);   // the host IS the owner: no wire
                else if (MPClient.IsConnected)
                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.MergerHandover, MPConfig.PlayerId,
                        new MergerHandoverPayload
                        {
                            OwnerPid     = MPConfig.PlayerId,
                            OwnerStable  = p.OwnerStable ?? "",
                            SimulatorPid = ReturnSentinel,
                            Ack          = "return-applied",
                            Addresses    = new List<string>(p.Addresses ?? new List<string>()),
                        }));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] return apply: {ex.Message}"); }
        }

        /// <summary>C2(g) - the ONE approved on-screen line of this leg (user approval 2026-09-11). Once
        /// per return; no name known means no toast at all rather than an invented one.</summary>
        private static void ShowReturnToast(MergerHandoverPayload p)
        {
            try
            {
                string key = (p.OwnerStable ?? "") + "|" + p.SinceDay;
                if (key == _lastToastKey) return;
                _lastToastKey = key;
                string name = p.RanByName ?? "";
                if (string.IsNullOrWhiteSpace(name))
                {
                    Plugin.Logger.LogInfo($"[Absence] return toast skipped for '{p.OwnerPid}' - no simulator name known.");
                    return;
                }
                PassengerHud.Toast($"While you were away, {name} ran your businesses. They are back in your hands.");
                Plugin.Logger.LogInfo($"[Absence] return toast shown to '{p.OwnerPid}' (ran by '{name}').");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] return toast: {ex.Message}"); }
        }

        /// <summary>C2(c): my stale list items for these addresses leave, the returned ones take their
        /// place. The SAME installer P3-B uses, with the tag OFF - these are my own items now, so an
        /// undo must never lift them and the save strip must never hold them back.</summary>
        private static int ReinstallOwnLists(BusinessPaperworkPayload bundle, HashSet<string> addrs, string owner)
        {
            var gi = SaveGameManager.Current;
            if (gi == null) return 0;
            int removed = RemoveOwnItemsFor(gi, addrs);
            int n = 0;
            bool wasTagged = _tagInstalls, wasUpsert = _returnUpsert;
            _tagInstalls = false; _returnUpsert = true;   // r2 F2: a returned dict row REPLACES mine
            try { foreach (var a in addrs) n += InstallListsFor(a, bundle, addrs, owner); }
            finally { _tagInstalls = wasTagged; _returnUpsert = wasUpsert; }
            Plugin.Logger.LogInfo($"[Absence] my own lists for {addrs.Count} address(es): {removed} stale item(s) out, "
                                + $"{n} returned item(s) in.");
            return n;
        }

        /// <summary>Which field of each installed list names the building. The removal below is the
        /// inverse of InstallListsFor and reads exactly the fields it writes.</summary>
        private static readonly (string List, string[] Fields)[] _addrFields =
        {
            ("DeliveryContracts",                 new[] { "businessAddress" }),
            ("importPartnerships",                new[] { "headquartersAddress" }),
            ("logisticsManagerPlans",             new[] { "headquartersAddress" }),
            ("pricingManagerPlans",               new[] { "headquartersAddress" }),
            ("hrManagerPlans",                    new[] { "headquartersAddress" }),
            ("headhunterPlans",                   new[] { "headquartersAddress" }),
            ("interiorInstallationFirmContracts", new[] { "addressToDoTheInstallation" }),
            ("movingServiceContracts",            new[] { "originMovingAddress", "destinationMovingAddress" }),
            ("disabledLicensingFees",             new[] { "address" }),
            ("paidLicensingFeesToday",            new[] { "Item1" }),
        };

        /// <summary>Take MY pre-absence items for exactly these addresses back out, so the returned ones
        /// replace them instead of doubling them. An item this machine INSTALLED for some OTHER absent
        /// owner is tagged and is never touched here (it is theirs; UndoLocal is what lifts it).</summary>
        private static int RemoveOwnItemsFor(GameInstance gi, HashSet<string> addrs)
        {
            int n = 0;
            // r2 F2: itemsOrderedThisWeekByImporter is deliberately NOT cleared here. Its rows are keyed by
            // the IMPORTER's address, never by one of the marked buildings, and the same importer can also
            // supply an UNMARKED headquarters - deleting the row because a MARKED HQ happens to have a
            // partnership with that importer would throw away the unmarked HQ's orders for the week, and
            // they would only come back if the returned bundle happened to carry that importer's row.
            // (Today every building of the owner is marked - AddressesOfStable is the whole of
            // BuildingOwners for the stable - so no unmarked HQ can share an importer; this is hardening
            // for the day that stops being true.) The returned bundle UPSERTS instead: InstallDict
            // replaces the row for every key the bundle carries and leaves every other row untouched, so
            // nothing is lost and nothing is doubled.
            foreach (var (name, fields) in _addrFields)
            {
                try
                {
                    var list = ListByName(gi, name);
                    if (list == null) continue;
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var it = list[i];
                        if (it == null) continue;
                        bool hit = false;
                        foreach (var f in fields) if (addrs.Contains(AddrKeyOf(it, f))) { hit = true; break; }
                        if (!hit || IsInstalledItem(it)) continue;
                        list.RemoveAt(i);
                        n++;
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] return: clearing '{name}': {ex.Message}"); }
            }

            return n;
        }

        private static string AddrKeyOf(object o, string field)
        {
            try
            {
                var v = FieldOf(o, field)?.GetValue(o);
                return v is Address a ? GameStateReader.AddressKey(a) : "";
            }
            catch { return ""; }
        }

        private static bool IsInstalledItem(object item)
        {
            if (_installed.Count == 0) return false;
            foreach (var e in _installed) if (ReferenceEquals(e.Item, item)) return true;
            return false;
        }

        /// <summary>C2(d): the returned full records onto MY real employee records, matched BY ID. An id
        /// I already hold is UPDATED in place (never duplicated, never re-hired); an id I do not hold is
        /// a hire the simulator made while I was away and comes in through the same reconstruction P3-B
        /// promotes with.</summary>
        private static void ApplyReturnedStaff(BusinessPaperworkPayload bundle, HashSet<string> addrs,
                                               out int updated, out int added)
        {
            updated = 0; added = 0;
            foreach (var rec in bundle?.Employees ?? new List<EmployeeEditPayload>())
            {
                if (rec == null || string.IsNullOrEmpty(rec.EmployeeId)) continue;
                if (!addrs.Contains(rec.AddressKey ?? "")) continue;
                try
                {
                    int r = MergerEmployeeSync.ApplyReturnedRecord(rec);
                    if (r == 1) updated++;
                    else if (r == 2) added++;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] return staff '{rec.EmployeeId}': {ex.Message}"); }
            }
        }

        /// <summary>C3 (derived safety, every machine): this machine still simulates an address that NO
        /// mark names any more - the host restarted, or its drop never arrived. The broadcast table is
        /// the truth, so give those addresses back exactly as a drop would. Never fires while a mark
        /// still names this machine, and never for an owner the table still lists.</summary>
        private static void StopSimulatingWhatNoMarkNames()
        {
            try
            {
                if (_simHere.Count == 0) return;
                var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in _known)
                {
                    if (a == null || a.SimulatorPid != MPConfig.PlayerId) continue;
                    foreach (var addr in a.Addresses ?? new List<string>())
                        if (!string.IsNullOrEmpty(addr)) named.Add(addr);
                }
                var orphans = new List<string>();
                foreach (var kv in _simHere)
                    if (!named.Contains(kv.Key))
                    {
                        Plugin.Logger.LogInfo($"[Absence] stopped simulating '{kv.Key}' for '{kv.Value}' (mark gone).");
                        if (!orphans.Contains(kv.Value)) orphans.Add(kv.Value);
                    }
                foreach (var owner in orphans)
                {
                    HandBackFlush(owner, "no mark names this machine any more");   // H-STANDINTILL-2 T5
                    ForgetHeld(owner);
                    UndoLocal(owner, "no mark names this machine any more");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] mark-gone sweep: {ex.Message}"); }
        }

        // == B6 TestDrive line =================================================
        public static string TestDriveLine()
        {
            var marks = new List<string>();
            if (MPServer.IsRunning)
            {
                // r4 F4: ownerBack is the HOST's flag - AbsenceInfo (the broadcast shape) has no room for
                // it, so the host verb reads the live table and a client verb simply omits the field.
                var keys = new List<string>(_marks.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (var k in keys)
                {
                    var m = _marks[k];
                    marks.Add($"{m.OwnerPid}->{m.SimulatorPid} ownerBack={(m.OwnerBack ? 1 : 0)}: "
                            + $"{string.Join(",", m.Addresses)}");
                }
            }
            else
                foreach (var a in _known)
                    marks.Add($"{a.OwnerPid}->{a.SimulatorPid}: {string.Join(",", a.Addresses ?? new List<string>())}");
            // P3-C (C6): the host adds the last return it sent this session; every other machine adds
            // the addresses a return actually replaced here.
            string extra = MPServer.IsRunning
                         ? $" returned=[{_lastReturnLine}]"
                         : $" replaced=[{string.Join(",", _replaced)}]";
            if (MPServer.IsRunning)
            {
                // ABSENCE-HANDBACK-1 F7: per sent return, how many hand-back interiors are still unacked (+pw = paperwork acked).
                var pend = new List<string>();
                foreach (var kv in _marks)
                    if (kv.Value != null && kv.Value.ReturnSent)
                        pend.Add($"{kv.Value.OwnerPid}:{kv.Value.PendingInteriors.Count}{(kv.Value.PaperworkAcked ? "+pw" : "")}");
                extra += $" pendingInteriors=[{string.Join(",", pend)}]";
            }
            return $"OK absence marks=[{string.Join("; ", marks)}] simulating_here=[{string.Join(",", SimulatedAddresses())}]" + extra;
        }
    }
}
