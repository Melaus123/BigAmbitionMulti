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
    /// OWNER ACKS IT (r2 F4 - the send only flags it); their own
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
            { have.OwnerPid = ownerPid ?? have.OwnerPid; have.OwnerBack = false; have.ReturnSent = false;   // r2 F4: absent again
              have.LastSimulatorPid = simulatorPid; return false; }

            _marks[ownerStable] = new AbsenceMark
            {
                OwnerStable  = ownerStable,
                OwnerPid     = ownerPid ?? "",
                SimulatorPid = simulatorPid,
                Addresses    = new List<string>(addresses),
                SinceDay     = have != null ? have.SinceDay : sinceDay,
                LastSimulatorPid = simulatorPid,   // P3-C: who ran them, kept past r5's clear
            };
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
        /// zero is a refusal and says why.</summary>
        public static int HostClearMarkOnReturnAck(string ownerPid, string ownerStable)
        {
            int n = 0;
            try
            {
                var kill = new List<string>();
                foreach (var kv in _marks)
                {
                    var m = kv.Value;
                    if (m == null || !m.OwnerBack) continue;
                    bool isOwner = (!string.IsNullOrEmpty(ownerPid)    && m.OwnerPid    == ownerPid)
                                || (!string.IsNullOrEmpty(ownerStable) && m.OwnerStable == ownerStable);
                    if (isOwner) kill.Add(kv.Key);
                }
                foreach (var s in kill) { _marks.Remove(s); n++; }
                if (n > 0)
                {
                    _returnLogged.Remove(ownerPid ?? "");
                    Plugin.Logger.LogInfo($"[Absence] return of '{ownerPid}' acknowledged - mark cleared.");
                }
                else
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
        { if (_marks.TryGetValue(ownerStable ?? "", out var m) && m != null) m.ReturnSent = false; }

        /// <summary>HOST: put one mark back from the manifest (clear-then-apply restore, MPServer).
        /// SimulatorPid is deliberately NOT restored - a player id from the previous session names
        /// nobody here - so the mark comes back SUSPENDED and the next reconcile designates a simulator
        /// and sends the hand-over. SinceDay is the whole point of persisting it and is kept exactly.</summary>
        public static void HostRestoreMark(string ownerStable, string ownerPid, List<string> addresses, int sinceDay,
                                          string lastSimulatorPid = "")
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
        public static void HostReset() { _marks.Clear(); _returnLogged.Clear(); _snapQueue.Clear(); _lastReturnLine = ""; }

        // ── B2 hand-over send (host) ──────────────────────────────────────────
        // r2 F1a: the third element says whether this pair belongs to the RETURN LEG. A return's snapshot
        // is vouched even when the host copy is empty (the host copy IS the truth for a marked address);
        // a hand-over's is not, and queues false.
        private static readonly List<(string addr, string pid, bool returnLeg)> _snapQueue = new();   // paced: one per tick

        /// <summary>HOST: hand the designated simulator its payload, then PACE one interior snapshot
        /// per tick to it. The host itself applies locally instead of sending to itself.</summary>
        public static void SendHandover(AbsenceMark m)
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
                };
                int bytes = string.IsNullOrEmpty(json) ? 0 : System.Text.Encoding.UTF8.GetByteCount(json);
                Plugin.Logger.LogInfo($"[Absence] hand-over of '{m.OwnerPid}' ({m.Addresses.Count} addresses, "
                                    + $"{bytes} bytes paperwork) -> '{m.SimulatorPid}'.");

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
                if (owner == MPConfig.PlayerId) ApplyReturn(p);          // the host is the owner: no wire, no snapshots
                else
                {
                    MPServer.SendToPlayer(owner, MessageEnvelope.Create(MessageType.MergerHandover, "host", p));
                    foreach (var a in addresses)
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
                if (_heldReturn != null)
                {
                    bool ready = false;
                    try { ready = SaveGameManager.Current?.BuildingRegistrations != null; } catch { }
                    if (ready) { var held = _heldReturn; _heldReturn = null; ApplyReturn(held); }
                }
                if (_snapQueue.Count == 0 || !MPServer.IsRunning) return;
                var (addr, pid, returnLeg) = _snapQueue[0];
                _snapQueue.RemoveAt(0);
                // r2 F1a: vouchEmpty for a RETURN only - the host's copy of a marked address is the truth
                // even when it holds zero items, so the returned owner's apply must not skip it as
                // non-authoritative. A hand-over keeps the old "never vouch an empty list" rule.
                InteriorSync.SendSnapshotToPlayer(addr, pid, forceItemAuthority: true, vouchEmpty: returnLeg);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] paced snapshot: {ex.Message}"); }
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
        /// <summary>P3-C's removal surface: what B3(d) put into this machine's GameInstance lists (and
        /// into the one Address-keyed map, tagged as an InstalledDictEntry).</summary>
        public static IReadOnlyList<(string Owner, string List, object Item)> InstalledListItems => _installed;
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

        private static readonly HashSet<string> _resendAsked = new();   // ownerPid, F3: log once until served

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
                    bool lost = false;
                    foreach (var addr in a.Addresses ?? new List<string>())
                        if (!string.IsNullOrEmpty(addr) && !SimulatesHere(addr)) { lost = true; break; }
                    if (!lost) { _resendAsked.Remove(a.OwnerPid ?? ""); continue; }
                    if (_resendAsked.Add(a.OwnerPid ?? ""))
                        Plugin.Logger.LogInfo($"[Absence] installs for '{a.OwnerPid}' are gone here (scene churn) - "
                                            + "asking the host to re-send the hand-over.");
                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.MergerHandover, MPConfig.PlayerId,
                        new MergerHandoverPayload
                        {
                            OwnerPid = a.OwnerPid, OwnerStable = a.OwnerStable,
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
                if (p.Drop) { UndoLocal(p.OwnerPid, $"host dropped the mark for '{p.OwnerPid}'"); return; }
                if (!string.IsNullOrEmpty(p.SimulatorPid) && p.SimulatorPid != MPConfig.PlayerId)
                { Plugin.Logger.LogWarning($"[Absence] hand-over addressed to '{p.SimulatorPid}' arrived here - ignored."); return; }

                BusinessPaperworkPayload bundle = null;
                if (!string.IsNullOrEmpty(p.PaperworkJson))
                {
                    try { bundle = Newtonsoft.Json.JsonConvert.DeserializeObject<BusinessPaperworkPayload>(p.PaperworkJson); }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] paperwork parse: {ex.Message}"); }
                }

                // The owner's WHOLE address set: a moving contract names two addresses, so the
                // installer has to know which of them are the owner's to install it exactly once.
                var owned = new HashSet<string>(p.Addresses ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                string owner = p.OwnerPid ?? "";
                UndoLocal(owner, $"re-applying the hand-over for '{owner}'");   // MAJOR-2: undo THEN install
                foreach (var addr in p.Addresses ?? new List<string>())
                {
                    if (string.IsNullOrEmpty(addr)) continue;
                    _simHere[addr] = owner;                  // (a)+(b)+(e): the veil exception, the interior
                                                              // publisher and the paperwork publish all read this
                    int staff = PromoteStaffFor(addr, bundle, owner);        // (c)
                    int items = InstallListsFor(addr, bundle, owned, owner);  // (d)
                    Plugin.Logger.LogInfo($"[Absence] simulating '{addr}' for '{p.OwnerPid}' "
                                        + $"(staff promoted: {staff}, list items installed: {items}).");
                }

                _resendAsked.Remove(owner);   // r4 F3: served - a later loss may ask (and log) again

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

        /// <summary>B5 / re-apply / re-designation: stop simulating for ONE absent owner (r1 MAJOR-6 -
        /// this used to wipe EVERY owner, and a simulator running two of them lost both). Only that
        /// owner's addresses leave the exception set, only that owner's promoted records are demoted
        /// (removed here - the owner's next roster publish re-injects them as display copies) and only
        /// that owner's installed list items are lifted back out.</summary>
        public static void UndoLocal(string ownerPid, string why)
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
            try { UndoLocalAll("session/scene reset"); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] reset undo: {ex.Message}"); }
            _simHere.Clear(); _known.Clear(); _promotedStaff.Clear(); _installed.Clear();
            _snapQueue.Clear(); _idWarned.Clear(); _fieldWarned.Clear(); _resendAsked.Clear();
            // P3-C (C5): a held return payload dies with the connection - the host clears a mark only
            // after a SEND, so the next return re-sends the whole thing.
            _heldReturn = null; _heldLogged = false; _returnAddrs.Clear(); _replaced.Clear(); _lastToastKey = "";
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
                        list.RemoveAt(i);
                        n++;
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
        /// through the exception onto a by-then developed interior (r1 m4).</summary>
        public static bool ConsumeReturnInterior(string addressKey)
        {
            try
            {
                if (string.IsNullOrEmpty(addressKey) || _returnAddrs.Count == 0) return false;
                if (!_returnAddrs.Remove(addressKey)) return false;
                if (!_replaced.Contains(addressKey)) _replaced.Add(addressKey);
                Plugin.Logger.LogInfo($"[Absence] replaced my '{addressKey}' with the simulated copy.");
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

                // (a) the interiors follow this message on the same lane, one per host tick.
                _returnAddrs.Clear();
                foreach (var a in addrs) _returnAddrs.Add(a);

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
                foreach (var owner in orphans) UndoLocal(owner, "no mark names this machine any more");
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
            return $"OK absence marks=[{string.Join("; ", marks)}] simulating_here=[{string.Join(",", SimulatedAddresses())}]" + extra;
        }
    }
}
