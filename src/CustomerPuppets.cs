using System;
using System.Collections.Generic;
using Buildings;
using Helpers;
using UnityEngine;
using UnityEngine.AI;
using DG.Tweening;                 // DOLookAt — round-119 serve performance
using BigAmbitions.Characters;    // AnimationType
using BigAmbitions.SoundSystem;   // SoundType

namespace BigAmbitionsMP
{
    /// <summary>Phase 3 slice 3 (round-41) — SHARED CUSTOMER BODIES when two+ session players are inside
    /// the same player building. One machine (the "simulator") runs native customers; every other player
    /// inside suppresses its local spawner and renders kinematic PUPPETS from the simulator's ~4 Hz
    /// stream — both players watch the same shopper at the same shelf.
    ///
    /// AUTHORITY (host-arbitrated, user-agreed design): the register worker first (serving requires
    /// simulating — the serve loop needs real customer AI locally), else the earliest-arrived player
    /// still inside (host-clock arrival order; no race). Transfers happen on exactly two human-legible
    /// events — someone starts working the register, or the simulator leaves — and render as walk-out/
    /// walk-in churn (v1): the outgoing crowd paths to the exit while the new simulator's spawner
    /// repopulates from the same shared schedule. "Authority persists after leaving" is impossible by
    /// engine design: interiors only exist around the local player.
    ///
    /// ECONOMY interplay (why this is safe): while a helper simulates, the owner-as-follower's spawner
    /// is suppressed, so the owner's entry table stays unclaimed and every helper-served order forwards
    /// cleanly (round-39f); while the owner simulates, sales record natively and the follower-helper's
    /// machine has no real customers to double-anything with.</summary>
    internal static class CustomerPuppets
    {
        // ── Authority state (all machines; host is the writer) ──────────────────────────────────────
        private static readonly Dictionary<string, string> _authority = new();   // addressKey → simulator pid

        // ── My local mode ────────────────────────────────────────────────────────────────────────────
        private static string _myBldg = "";
        private static bool _followerHere;

        // ── Host election bookkeeping ────────────────────────────────────────────────────────────────
        private static readonly Dictionary<string, (string bldg, float since)> _arrival = new();
        private static float _nextElectAt, _nextResendAt;

        // ── Simulator streaming ──────────────────────────────────────────────────────────────────────
        private static float _nextStreamAt;
        private static float _nextStreamStatAt;   // round-132: live-customers vs distinct-ids audit

        // ── Puppets (follower side) ──────────────────────────────────────────────────────────────────
        private class Puppet
        {
            public GameObject go = null!;
            public ThirdPersonCharacter? tpc;
            public Animator? anim;
            public Vector3 target;
            public float yaw;
            public float lastSeen;
            public bool leaving;
            public float leaveAt;
            public float holdUntil;   // H-HANDOFF-1: named in the old simulator's Final - kept in place (no stale walk-out) until the take-over
            public string held = "";
            public int fill = -1;
            public GameObject? heldGo;
            public CustomerPuppetLookPayload? look;   // round-44: the customer's appearance — survives handoffs
            // 1.0 port: MotionTime support, cached per controller exactly like the native
            // _motionTimeParamCheckedOn/_hasMotionTimeParam pair (ThirdPersonCharacter.cs:420-423) —
            // a look apply may swap the controller, which re-triggers the check.
            public RuntimeAnimatorController? motionTimeCheckedOn;
            public bool hasMotionTime;
            // H-PUPPETSTUTTER-1: the last 4 stream samples (fold P1 2026-09-27: was 3), stamped in the SIMULATOR's
            // clock (bsim) - the copy is drawn at a render time `delay` behind the sender's clock, interpolated between
            // the two samples around it (no chase). bn = 0: nothing buffered - hold where the body stands.
            public readonly float[] bt = new float[BufSamples];
            public readonly Vector3[] bp = new Vector3[BufSamples];
            public readonly float[] byaw = new float[BufSamples];
            public readonly float[] bfwd = new float[BufSamples];
            public int bn;
            public float delay;   // fold P3: the eased render delay, s (0 = not set yet)
            // fold A1 (review 2026-09-27): each buffered sample's activity (PuppetRowInfo Loops/Dance/ActItem) - applied
            // when the render time reaches that sample, not when the batch arrives. actT = the sample time last applied.
            public readonly long[] bloops = new long[BufSamples];
            public readonly float[] bdance = new float[BufSamples];
            public readonly string[] bact = new string[BufSamples];
            public float actT = float.MinValue;
            public float rt = float.MinValue;   // this copy's render time (sender clock) this frame - the one-shot queue reads it
            public string bsim = "";
            public bool snapNext;
            // H-PUPPETANIM-1: what this copy SHOWS - looping activity bits (PuppetRowInfo.Loops), dance type, workout machine.
            public long loops;
            public float dance;
            public string act = "";
            public bool actsChecked;   // the K-of-N parameter line was evaluated for this copy's controller
        }
        private static readonly Dictionary<string, Puppet> _puppets = new();

        // H-PUPPETSTUTTER-1: one clock-offset estimate per simulator (receiver unscaledTime - sender stamp).
        // Fold P1 (review 2026-09-27): taken from its LOW end - a row's offset is the true offset plus that row's transit
        // delay, so the smallest recent one is the truest. A smaller offset is taken at once; a larger one blends in only
        // ClockOffsetRise per batch, so the burst of late rows after a stall no longer lifts it (before: the first late
        // row re-seeded it ~1 s high and it took ~10 s to blend back - copies pinned to the oldest sample, stepping).
        // A step UP of more than 1 s (the sender's clock restarted) still re-seeds, but only once it has held
        // ClockReseedAfter seconds (a stall's burst drains well inside that), and to the smallest offset seen meanwhile.
        // jit: how far rows arrive ABOVE that floor, averaged (JitterBlend per batch, each row counted at most JitterCap
        // so a stall's burst barely moves it) - the render time sits that much further back, so a row of ordinary
        // lateness does not leave the copy holding its newest sample.
        private sealed class ClockEst { public float off; public float highSince = -1f; public float highMin; public float jit; }
        private const float JitterBlend = 0.05f;
        private const float JitterCap = 0.5f;
        private static readonly Dictionary<string, ClockEst> _clockOffsetBySim = new();
        private const float ClockOffsetRise = 0.02f;
        private const float ClockReseedAfter = 2f;
        private const int BufSamples = 4;
        // Fold P3: the render delay is 1.5 x the spacing of the samples actually received (the smallest gap in the
        // buffer), not the watcher's own StreamInterval, and it moves at most RenderDelayEase s per real second - at a
        // skip end (pace 5 -> 1) it no longer jumps 0.075 -> 0.375 s behind a buffer that spans ~0.1 s.
        private const float RenderDelayGaps = 1.5f;
        private const float RenderDelayEase = 0.5f;
        // Fold P2: a leaving copy's walk-out limit = distance to the exit / walking speed + margin, within [6, cap] s.
        private const float LeaveMarginSeconds = 3f;
        private const float LeaveCapSeconds = 30f;
        // puppetflips readout (fold P1/P3): the offset estimate, how far rows arrive above it, re-seeds, the render
        // delay, and where the render time falls in the buffer (frames, copies with >= 2 samples).
        private static float _offLast, _offExcessSum, _delayLast, _jitLast;
        private static int _offBatches, _offReseeds, _rbBetween, _rbOldest, _rbNewest;
        private const float SnapMetres = 3f;
        private const float CatchUpSpeed = 8f;   // m/s cap for the body reaching its render point after a hold (steady state it IS the render point)
        // H-PUPPETSTUTTER-1 flip counters (DEV lever `puppetflips`): IsMoving true->false changes, per copy on the
        // watcher (DriveLocomotion) and per native at each stream sample on the simulator.
        private static int _flipsWatch, _flipsSim, _simSamples, _simMovingSamples, _watchFrames, _watchMovingFrames;
        private static float _flipsSince = -1f;
        private static Dictionary<string, bool> _simMoving = new(), _simMovingNext = new();

        // Simulator-side per-customer HandContent cache (round-42 hand-prop mirroring).
        private static readonly Dictionary<int, Transform?> _handNodes = new();

        // Simulator-side per-customer entry-id cache (round-43 cross-machine identity).
        // Round-45: validated against the ORDER reference — customer bodies are POOLED (ReleaseCustomer →
        // reuse), so an instance id alone maps a recycled body to the PREVIOUS customer's identity: the
        // follower kept the old puppet+look for a brand-new person ("looks drifted"), and the new
        // person's look never shipped (the stale id sat in the sent-set).
        private static readonly Dictionary<int, (Order order, string id)> _custEntryIds = new();

        // Round-43: emotes that arrived before their puppet existed (complaints fire at the customer's
        // SPAWN instant — one state-tick before the follower's body appears). Applied at puppet spawn.
        private static readonly Dictionary<string, (int emoji, float seconds, float expires)> _pendingEmotes = new();

        // Round-44c: SESSION-LIFETIME look cache by customer identity — a look ships once, but puppets
        // get REBUILT (room re-entry, handoffs, stream gaps) and the consume-once pending queue lost the
        // look after first use, so rebuilt bodies fell back to random faces (field: "matched at first,
        // diverged by the end"). Never consumed, only overwritten; cleared with the session.
        private static readonly Dictionary<string, CustomerPuppetLookPayload> _looksById = new();
        private static readonly HashSet<string> _looksSent = new();

        // Round-44: adopted natives get their position HELD for a beat — the native spawn-init
        // repositions the body and assigns objectives a frame or two later, which raced our single warp
        // (field: adopted customers teleported and bolted in odd directions).
        private static readonly List<(Customer c, Vector3 pos, float until)> _warpHolds = new();

        // H-HANDOFF-1 (batch 27, 2026-09-26): hand-off bookkeeping (CustomerHandoff carries the visit rows).
        //   _prevSim        - the simulator this machine last FOLLOWED in _myBldg: the one a take-over takes over FROM;
        //   _lastReactSim   - the authority ReactToAuthority saw on its previous call (was it me? only then is
        //                     there a crowd of mine to hand over);
        //   _awaitFinalFrom - while not "", this machine IS the simulator but waits for that player's Final
        //                     snapshot: it stays in follower mode (spawner off, rows still applied) and streams
        //                     nothing, because an empty batch would walk the other machine's copies out.
        private static string _prevSim = "", _lastReactSim = "", _awaitFinalFrom = "";
        private static string _staleHoldLoggedFor = "";   // fold D1: the building whose stale-copy hold was already logged (once)
        private static string _heldForGone = "";   // the followed simulator whose DISCONNECT already froze my copies (once)
        // Fold R0 (run T-HANDOFF1-20260926-204839, leg 2): a take-over that is due while THIS interior is still
        // loading (the owner walked in: ShopCtx, the follow and the Final all land before EnterBuildingCoroutine
        // has moved the player in, so every SpawnCustomer met no navmesh - "0 adopted, 4 walking out"). The via
        // it will run with; HandoffWaitTick runs it once IndoorReady() holds, copies held meanwhile.
        private static string _readyPendingVia = "";
        private static float _awaitSince;
        private const float AwaitFinalTimeout = 5f;   // floor of the floor: never leave a shop unsimulated longer
        private const float AwaitLeftGrace = 1.5f;    // an EXITING simulator's Final is sent from ResetIndoors and may still be in flight
        internal static int LastAdopted, LastWithState, LastLeaving, LastWalked, LastMatched;   // custstate lever
        internal static string LastAdoptFrom = "", LastAdoptVia = "";
        internal static string MyBuilding => _myBldg;
        internal static string AwaitingFinalFrom => _awaitFinalFrom;
        internal static string PreviousSimulator => _prevSim;
        internal static int PuppetCount => _puppets.Count;
        internal static List<string> PuppetIds() => new List<string>(_puppets.Keys);

        public static void Reset()
        {
            try
            {
                DestroyAllPuppets();
                _authority.Clear();
                _arrival.Clear();
                _outOnce.Clear();
                _looksById.Clear();
                _looksSent.Clear();
                _myBldg = "";
                _prevSim = ""; _lastReactSim = ""; _awaitFinalFrom = ""; _heldForGone = ""; _staleHoldLoggedFor = ""; _readyPendingVia = "";   // H-HANDOFF-1 step 11
                try { CustomerHandoff.Reset(); } catch { }   // also clears the BookOnce registry
                try { ResetActs(); } catch { }               // H-PUPPETANIM-1
                try { SkipPaceBodies.RestoreAll(); } catch { }   // D-SKIPPACE-1: leaving the building / session end - no body keeps a scaled speed
                if (_followerHere) { try { IndoorCustomerSpawner.EnableCustomersSpawn(); } catch { } }
                _followerHere = false;
            }
            catch { }
        }

        /// <summary>Once per frame from MPCanvasUI.Update. MAIN THREAD.</summary>
        public static void Tick()
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected)
                {
                    if (_puppets.Count > 0 || _followerHere) Reset();
                    return;
                }
                if (MPServer.IsRunning) HostElectionTick();
                TrackMyBuilding();
                HandoffWaitTick();       // H-HANDOFF-1 step 5: a take-over waiting for the old simulator's Final
                SimulatorStreamTick();
                HandoffStreamTick();     // H-HANDOFF-1 (ruling a): visit rows on change, <= 1/s
                BookOnce.Tick();         // book once: the till watcher (inline till adds of the employee checkout / kiosk)
#if BAMP_DEV
                try { if (_myBldg.Length > 0) CustomerHandoff.ArmTick(_followerHere ? 0 : LiveCustomerCount, _puppets.Count); } catch { }
#endif
                UpdatePuppets();
                TickWarpHolds();
                ChurnTick();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] puppet tick: {ex.Message}"); }
        }

        // ── Host: simulator election ─────────────────────────────────────────────────────────────────
        private static void HostElectionTick()
        {
            if (Time.unscaledTime < _nextElectAt) return;
            _nextElectAt = Time.unscaledTime + 1f;
            bool resend = Time.unscaledTime >= _nextResendAt;
            if (resend) _nextResendAt = Time.unscaledTime + 5f;

            // Presence: every session player's current building (host clock stamps arrivals).
            var inside = new Dictionary<string, List<(string pid, float since)>>();
            var seenPids = new HashSet<string>();
            foreach (var pid in MPRestSync.AllPlayers())
            {
                seenPids.Add(pid);
                string b = pid == MPConfig.PlayerId ? (MPRegisterSync.CurrentShopAddress ?? "")
                                                    : RemotePlayerManager.BuildingOf(pid);
                // Review HIGH-1 (batch 16c): a read of "" is PROVISIONAL for one pass. The booking-player rule
                // un-sticks the incumbent, so an owner whose building blips to "" and back would otherwise cost two
                // full transfers per blip. He keeps his previous building until "" has been read on two passes
                // in a row; a move straight into another building is immediate; a player who left the session is
                // not visited by this loop at all.
                if (string.IsNullOrEmpty(b) && _arrival.TryGetValue(pid, out var was) && !string.IsNullOrEmpty(was.bldg))
                { if (_outOnce.Add(pid)) b = was.bldg; }
                else _outOnce.Remove(pid);
                if (!_arrival.TryGetValue(pid, out var a) || a.bldg != b)
                    _arrival[pid] = (b, Time.unscaledTime);
                if (string.IsNullOrEmpty(b)) continue;
                if (!MPServer.BuildingOwners.ContainsKey(b)) continue;   // only session-player buildings
                if (!inside.TryGetValue(b, out var list)) inside[b] = list = new List<(string, float)>();
                list.Add((pid, _arrival[pid].since));
            }
            // Re-check MEDIUM (batch 16c): a player who LEFT the session must not keep an arrival row - on rejoin
            // the one-pass grace above would count him inside the building he disconnected in.
            if (_arrival.Count > seenPids.Count)
                foreach (var gone in new List<string>(_arrival.Keys))
                    if (!seenPids.Contains(gone)) { _arrival.Remove(gone); _outOnce.Remove(gone); }

            // Elect per occupied building: the BOOKING PLAYER when he is inside (2026-09-19 ruling, at the
            // foreach below), else INCUMBENT (round-118), else register-duty holder, else earliest arrival.
            //
            // ROUND-118 STICKY AUTHORITY — field report bamp-bug-20260727-204640 ('Misterxk9x'): the host
            // could not work his own restaurant while his partner was in it.  Cause: she arrived first so she
            // held authority; every time he sat at the register he took duty and authority flipped to him,
            // and every time he stood it flipped straight back.  The log shows ten duty transitions in 28
            // seconds and at least five full round trips of authority — and a transfer is EXPENSIVE by
            // design ("the outgoing crowd paths to the exit while the new simulator's spawner repopulates"),
            // so his customers were torn down and respawned over and over and nobody was ever served.
            //
            // The fix is not a debounce, it is not transferring at all while nothing relevant has changed.
            // A player sitting at a SECOND till does not need authority for that till to work: every other
            // machine injects a real staffed stand-in at it (MPRegisterSync :789-790), so customers queue and
            // check out there on the simulator's machine, and those customers stream back as puppets — the
            // second player sees the queue form at their own counter.  So authority only has to move when the
            // current simulator genuinely CANNOT do the job any more, i.e. they are no longer in the building
            // (leaving/disconnecting drops them from `inside`, and an emptied building is cleared below) — or,
            // since the 2026-09-19 ruling below, when the player whose BOOKS the shop is walks into it.
            //
            // Duty-first still decides the FIRST election for a building — when nobody is simulating yet, the
            // person working the register is the right choice, because serving does require simulating.
            // 2026-09-19 RULING ('Option 2') — THE MACHINE THAT BOOKS A SHOP RUNS ITS LIVE CUSTOMERS
            // WHENEVER ITS PLAYER IS INSIDE.  Native skips the abstract hourly simulation for the building
            // the local player is standing in (BusinessSimulatorHelper.cs:32) and a follower's spawner is
            // disabled, so an OWNER standing in his own shop as a FOLLOWER books NOTHING for shop types
            // whose NPC sales never pass Order.Pay (a gym completes through Customer.CompleteOrder), and for
            // till shops only by way of the forward.  With the booking machine simulating, every sale of
            // every shop type books natively where the books are.
            //
            // The booking player is the LEDGER owner (MPServer.BuildingOwners, pid space: the host's own
            // entries read "host" or its own pid, and an ABSENT owner's entry is still a stableId, which
            // simply matches nobody inside) — or, when that owner is absent and a merged STAND-IN simulates
            // his addresses, the stand-in.  The stand-in lookup is built ONCE per election pass from the
            // live mark table (never HostSnapshot(), which deep-copies), and only when a mark exists at all;
            // a session with no absent owner pays one int comparison for it.
            Dictionary<string, string>? standIn = null;
            if (MergerAbsence.MarkCount > 0)
            {
                standIn = new Dictionary<string, string>();
                try
                {
                    foreach (var mk in MergerAbsence.Marks)
                    {
                        var m = mk.Value;
                        if (m == null || string.IsNullOrEmpty(m.SimulatorPid) || m.Addresses == null) continue;
                        foreach (var a in m.Addresses) if (!string.IsNullOrEmpty(a)) standIn[a] = m.SimulatorPid;
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] stand-in read: {ex.Message}"); }
            }
            foreach (var kv in inside)
            {
                string sim = "";
                // THE BOOKING PLAYER FIRST — ahead of the incumbent.  Owner-presence is a STABLE fact: it
                // changes only when he walks in or out of the building, never on a per-action basis the way
                // register duty does, so this cannot re-create the round-118 flip-flop.  While he is inside,
                // nothing below can move authority off him; when he leaves he drops out of `inside` like any
                // other simulator and the ordinary rules resume.
                string booker = "";
                bool bookerIsStandIn = false;
                string ledger = MPServer.BuildingOwners.TryGetValue(kv.Key, out var led) ? (led ?? "") : "";
                if (GameStatePatcher.IsHostLedgerId(ledger)) ledger = MPConfig.PlayerId;
                if (ledger.Length > 0 && InList(kv.Value, ledger)) booker = ledger;
                else if (standIn != null && standIn.TryGetValue(kv.Key, out var sp)
                         && !string.IsNullOrEmpty(sp) && InList(kv.Value, sp ?? "")) { booker = sp ?? ""; bookerIsStandIn = true; }
                if (booker.Length > 0) sim = booker;
                if (sim.Length == 0 && _authority.TryGetValue(kv.Key, out var incumbent) && !string.IsNullOrEmpty(incumbent))
                    foreach (var (pid, _) in kv.Value) if (pid == incumbent) { sim = incumbent; break; }
                if (sim.Length == 0)
                {
                    string duty = MPRegisterSync.PersonalDutyHolderAt(kv.Key);
                    if (!string.IsNullOrEmpty(duty))
                        foreach (var (pid, _) in kv.Value) if (pid == duty) { sim = duty; break; }
                }
                if (sim.Length == 0)
                {
                    float best = float.MaxValue;
                    foreach (var (pid, since) in kv.Value)
                        if (since < best) { best = since; sim = pid; }
                }
                bool changed = !_authority.TryGetValue(kv.Key, out var cur) || cur != sim;
                if (changed || resend)
                {
                    _authority[kv.Key] = sim;
                    MPServer.BroadcastCustomerAuthority(new CustomerSimAuthorityPayload { AddressKey = kv.Key, SimulatorPid = sim });
                    if (changed)
                    {
                        // Round-118: transfers are still RARE by construction (the incumbent keeps it while
                        // inside unless the booking player walks in, which happens at most twice per visit),
                        // so say WHY one happened — a run of these in a field log means the occupancy reading
                        // is flickering, which is a different bug from the one this stickiness fixed.
                        // The reason must be TRUE: an incumbent who is STILL inside can only have been
                        // displaced by the booking-player rule above, so say that instead of the old
                        // "no longer inside" line, which would now be a lie.  Every other reason keeps
                        // its wording.
                        bool curStillInside = !string.IsNullOrEmpty(cur) && InList(kv.Value, cur ?? "");
                        string why = string.IsNullOrEmpty(cur) ? "first election for this building."
                                   : curStillInside ? (bookerIsStandIn ? "its stand-in is inside and books it." : "the shop's owner is inside and books it.")
                                   : $"'{cur}' is no longer inside.";
                        Plugin.Logger.LogInfo($"[Customers] simulator for '{kv.Key}' \u2192 '{sim}' ({kv.Value.Count} inside) \u2014 " + why);
                        if (kv.Key == _myBldg) ReactToAuthority();
                    }
                }
            }

            // Emptied buildings: clear the assignment so everyone reverts to native.
            var stale = new List<string>();
            foreach (var kv in _authority)
                if (!inside.ContainsKey(kv.Key)) stale.Add(kv.Key);
            foreach (var k in stale)
            {
                _authority.Remove(k);
                MPServer.BroadcastCustomerAuthority(new CustomerSimAuthorityPayload { AddressKey = k, SimulatorPid = "" });
            }
        }

        /// <summary>Host election helper: is this player id among one building's occupants?</summary>
        private static bool InList(List<(string pid, float since)> occupants, string pid)
        {
            for (int i = 0; i < occupants.Count; i++) if (occupants[i].pid == pid) return true;
            return false;
        }

        // ── All machines: react to authority + my own movement ──────────────────────────────────────
        public static void ApplyAuthority(CustomerSimAuthorityPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey)) return;
                if (string.IsNullOrEmpty(p.SimulatorPid)) _authority.Remove(p.AddressKey);
                else _authority[p.AddressKey] = p.SimulatorPid;
                if (p.AddressKey == _myBldg) ReactToAuthority();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] apply authority: {ex.Message}"); }
        }

        private static void TrackMyBuilding()
        {
            string cur = MPRegisterSync.CurrentShopAddress ?? "";
            if (cur == _myBldg) return;
            // Leaving a building: the interior (and any puppets in it) is gone for me — restore native
            // spawning unconditionally and drop puppet objects immediately (they died with the interior).
            if (_followerHere)
            {
                try { IndoorCustomerSpawner.EnableCustomersSpawn(); } catch { }
                _followerHere = false;
            }
            DestroyAllPuppets();
            // T8 review MAJOR-1: the ship-once look set is per OCCUPANCY EPISODE, not per session —
            // the host's look cache clears when a building empties (authority ""), and a
            // session-lifetime set here meant those looks could never re-ship: followers met
            // permanently undressed puppets. Clearing on every building change makes looks
            // re-ship exactly once per episode, mirroring the cache's lifecycle.
            _looksSent.Clear();
            _myBldg = cur;
            // H-HANDOFF-1 step 11: the hand-off state belongs to the building I was in.
            _prevSim = ""; _lastReactSim = ""; _awaitFinalFrom = ""; _heldForGone = ""; _staleHoldLoggedFor = ""; _readyPendingVia = "";
            try { CustomerHandoff.OnBuildingChanged(cur); } catch { }
            ReactToAuthority();
        }

        private static void ReactToAuthority()
        {
            if (string.IsNullOrEmpty(_myBldg)) return;
            _authority.TryGetValue(_myBldg, out var sim);
            bool follow = !string.IsNullOrEmpty(sim) && sim != MPConfig.PlayerId;
            // H-HANDOFF-1: was I this building's simulator until now? Only then is there a crowd of mine to hand
            // over (a player who just walked in and follows has only his own entry spawns, not the shop's crowd).
            bool wasSim = _lastReactSim == MPConfig.PlayerId;
            _lastReactSim = sim ?? "";
            if (follow) _prevSim = sim ?? "";
            if (follow && !_followerHere)
            {
                _followerHere = true;
                _awaitFinalFrom = "";
                _rigDiagLogged = false;   // one rig-diagnostic line per follow session (P-PUPPET-RIG)
                // H-HANDOFF-1 step 1 (batch 27): DisableCustomersSpawn used to run HERE, before the swap - and its
                // CleanCustomers (IndoorCustomerSpawner.cs:309-314 -> :122-133) released every live customer, so the
                // loop below always met an empty list ("0 native(s) swapped") and no body was ever handed over in
                // place. It now runs AFTER the loop, and each body's visit row is captured before its release.
                // Round-43 SMOOTH HANDOFF (losing side): swap each native customer for a puppet AT ITS
                // POSITION — the new simulator adopts the same entries in place, so its stream picks
                // these bodies up where they stand instead of despawn/respawn churn.
                int swapped = 0, dropped = 0;
                var finalRows = new List<CustomerVisitRow>();
                BuildingRegistration? reg = null;
                try
                {
                    reg = FindReg(_myBldg);
                    var mine = new List<Customer>(IndoorCustomerSpawner.Customers);
                    foreach (var c in mine)
                    {
                        if (c == null) continue;
                        string id = RowIdOf(c, reg);
                        Vector3 pos = c.transform.position;
                        float yaw = c.transform.eulerAngles.y;
                        var (held, heldFill) = HeldStateOf(c);
                        var look = CaptureLook(c.tpc);
                        if (!c.isPlayer && !id.StartsWith("i", StringComparison.Ordinal))
                        {
                            try { var vr = CustomerHandoff.Capture(c, id); if (vr != null) finalRows.Add(vr); } catch { }
                        }
                        // H-HANDOFF-1 part B step C: the pooled release frees none of the seat, queue place, slot chair,
                        // casino table spot or dance spot this body holds - they stayed "taken" on this machine.
                        if (!c.isPlayer) { try { CustomerSeatPins.FreeHeld(c); } catch { } }
                        try { c.ReleaseCustomer(); } catch { }
                        if (!id.StartsWith("i", StringComparison.Ordinal) && !_puppets.ContainsKey(id))
                        {
                            var pup = SpawnPuppet(pos, yaw);
                            if (pup != null)
                            {
                                _puppets[id] = pup;
                                if (!string.IsNullOrEmpty(held)) { UpdateHeld(pup, held); ApplyFill(pup, heldFill); }
                                // Round-44: the puppet WEARS the native's exact look (local capture — no wire).
                                if (look != null) { pup.look = look; _looksById[id] = look; ApplyLookTo(pup.tpc, look); }
                                swapped++;
                                continue;
                            }
                        }
                        dropped++;   // no entry identity → the rare churn fallback
                    }
                }
                catch { }
                try { IndoorCustomerSpawner.DisableCustomersSpawn(); } catch { }   // step 1: after the swap (see above)
                Plugin.Logger.LogInfo($"[Customers] following '{sim}' in '{_myBldg}' — spawner suppressed; {swapped} native(s) swapped to puppets in place{(dropped > 0 ? $", {dropped} without identity dropped" : "")}.");
                if (wasSim) CustomerHandoff.SendFinal(_myBldg, reg, finalRows, "authority");
            }
            else if (!follow && _followerHere)
            {
                // H-HANDOFF-1 step 5: the old simulator's Final snapshot is what makes the adoption resume each
                // visit. When it has not arrived yet and that player is still connected (the booking player
                // walked in: the old simulator only learns of the change from this same broadcast; or it is
                // walking out and its exit Final is still in flight), stay a follower - spawner off, rows still
                // applied, nothing streamed - until it arrives (OnFinalReceived), that player disconnects or has
                // been outside for AwaitLeftGrace, authority moves elsewhere, or AwaitFinalTimeout passes
                // (HandoffWaitTick). A disconnected simulator is not waited for: its stream rows are all there is.
                if (_awaitFinalFrom.Length > 0 || _readyPendingVia.Length > 0) return;   // already waiting (an authority resend)
                if (_prevSim.Length > 0 && _prevSim != MPConfig.PlayerId && !CustomerHandoff.HasFreshFinal(_prevSim)
                    && CustomerHandoff.InLobby(_prevSim))
                {
                    _awaitFinalFrom = _prevSim;
                    _awaitSince = Time.unscaledTime;
                    Plugin.Logger.LogInfo($"[Handoff] simulator for '{_myBldg}' is me now - waiting for the final snapshot from '{_prevSim}' before adopting {_puppets.Count} cop(ies).");
                    return;
                }
                TakeOver(CustomerHandoff.HasFreshFinal(_prevSim) ? "final" : "no-final");
            }
        }

        /// <summary>H-HANDOFF-1 step 5, every frame: the fallback floor of a waiting take-over.</summary>
        private static void HandoffWaitTick()
        {
            // The simulator I follow DISCONNECTED: no Final can come, and the host's election reacts only after its
            // disconnect bookkeeping (a save, T-HANDOFF2-20260926-203637: every copy had timed out and walked out
            // first, "0 puppet(s) adopted"). Freeze the copies once, so the take-over adopts them from the stream rows.
            if (_followerHere && _prevSim.Length > 0 && _heldForGone != _prevSim && !CustomerHandoff.InLobby(_prevSim))
            {
                _heldForGone = _prevSim;
                HoldForHandoff(null, _prevSim, "its simulator disconnected");
            }
            // Fold R0: a take-over waiting for this interior to finish loading - polled here, every frame, on the
            // game's own state (IndoorReady), never a timer. The copies stay held until it runs.
            if (_readyPendingVia.Length > 0)
            {
                try
                {
                    if (!_followerHere || !IAmSimulatorFor(_myBldg))
                    {
                        Plugin.Logger.LogInfo($"[Handoff] dropped the take-over waiting for the interior of '{_myBldg}' - authority moved on.");
                        _readyPendingVia = "";
                        return;
                    }
                    if (IndoorReady())
                    {
                        string pv = _readyPendingVia;
                        if (pv != "final" && CustomerHandoff.HasFreshFinal(_prevSim)) pv = "final";
                        TakeOver(pv);
                        return;
                    }
                    float hnow = Time.unscaledTime;
                    foreach (var hp in _puppets.Values)
                        if (hp != null && hp.holdUntil < hnow + 2f) hp.holdUntil = hnow + 2f;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Handoff] wait tick: {ex.Message}"); _readyPendingVia = ""; }
                return;
            }
            if (_awaitFinalFrom.Length == 0) return;
            try
            {
                if (!_followerHere || !IAmSimulatorFor(_myBldg))
                {
                    Plugin.Logger.LogInfo($"[Handoff] stopped waiting for '{_awaitFinalFrom}' in '{_myBldg}' - authority moved on.");
                    _awaitFinalFrom = "";
                    return;
                }
                float waited = Time.unscaledTime - _awaitSince;
                if (CustomerHandoff.HasFreshFinal(_awaitFinalFrom)) TakeOver("final");
                else if (!CustomerHandoff.InLobby(_awaitFinalFrom)) TakeOver("left");
                else if (waited > AwaitLeftGrace && !CustomerHandoff.PlayerInside(_awaitFinalFrom, _myBldg)) TakeOver("left");
                else if (waited > AwaitFinalTimeout) TakeOver("timeout");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Handoff] wait tick: {ex.Message}"); _awaitFinalFrom = ""; }
        }

        /// <summary>H-HANDOFF-1: the simulator I follow sent its Final - it streams nothing more, and the election
        /// that makes a new simulator can take several seconds (T-HANDOFF1-20260926-202315: a vehicle exit plus an
        /// autosave; every copy had timed out and walked out, "0 puppet(s) adopted"). The copies it names stay
        /// where they are (a walk-out already started is stopped) for HandoffHoldSeconds, so the take-over
        /// adopts them in place.</summary>
        private const float HandoffHoldSeconds = 20f;
        internal static void HoldForHandoff(List<CustomerVisitRow>? rows, string from, string why = "final received")
        {
            try
            {
                if (!_followerHere || string.IsNullOrEmpty(from) || from != _prevSim) return;
                float now = Time.unscaledTime;
                int held = 0, stopped = 0;
                // rows == null: every copy (a disconnect names none).
                var ids = new List<string>();
                if (rows != null) { foreach (var r in rows) if (r != null && !string.IsNullOrEmpty(r.Id)) ids.Add(r.Id); }
                else ids.AddRange(_puppets.Keys);
                foreach (var id in ids)
                {
                    if (!_puppets.TryGetValue(id, out var pup) || pup.go == null) continue;
                    pup.holdUntil = now + HandoffHoldSeconds;
                    pup.lastSeen = now;
                    if (pup.leaving) { pup.leaving = false; pup.target = pup.go.transform.position; pup.bn = 0; stopped++; }   // H-PUPPETSTUTTER-1: bn = 0 holds it where it stands
                    held++;
                }
                Plugin.Logger.LogInfo($"[Handoff] holding {held} cop(ies) in place for the take-over ({stopped} walk-out(s) stopped) - {why}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Handoff] hold: {ex.Message}"); }
        }

        /// <summary>H-HANDOFF-1 step 4: a Final arrived (CustomerHandoff.Apply) - complete a take-over waiting for it.</summary>
        internal static void OnFinalReceived(string pid)
        {
            try
            {
                if (_awaitFinalFrom.Length > 0 && pid == _awaitFinalFrom && _followerHere && IAmSimulatorFor(_myBldg))
                    TakeOver("final");
                else if (_readyPendingVia.Length > 0 && pid == _prevSim) _readyPendingVia = "final";   // fold R0: it runs at ready
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Handoff] final received: {ex.Message}"); }
        }

        /// <summary>The gaining side of a hand-off (was the inline branch of ReactToAuthority).</summary>
        private static void TakeOver(string via)
        {
            // Fold R0: never adopt into an interior that is still loading - SpawnCustomer places each body at an exit
            // zone and warps its NavMeshAgent, which fails with no navmesh under it. Wait for the game's own
            // "entered" state; HandoffWaitTick completes this take-over.
            if (!IndoorReady())
            {
                if (_readyPendingVia.Length == 0)
                    Plugin.Logger.LogInfo($"[Handoff] take-over of '{_myBldg}' ({via}) waits for this interior to finish loading - {_puppets.Count} cop(ies) held.");
                _readyPendingVia = via;
                _awaitFinalFrom = "";
                return;
            }
            _readyPendingVia = "";
            string from = _prevSim;
            _awaitFinalFrom = "";
            _followerHere = false;
            // Round-43 SMOOTH HANDOFF (gaining side): adopt each puppet's schedule entry as a REAL
            // customer AT the puppet's position — bodies stay put, the AI resumes its routine from
            // there (a mid-checkout customer re-approaches; the agreed settle behavior). Puppets
            // whose entry can't be found locally walk out (the churn fallback).
            // H-HANDOFF-1: each copy with a visit row from the old simulator resumes that visit (step 6), and
            // the copies are adopted in the source's QUEUE ORDER (spot 0 first, not queued last) so re-queueing
            // bodies take their places in the same order.
            int adopted = 0, walked = 0, withState = 0, leaving = 0, matched = 0, bodyOnly = 0, noCopy = 0, reopened = 0;
            int fee0 = CustomerHandoff.FeeSuppressed, comp0 = CustomerHandoff.ComplaintSuppressed;
            // Fold K6 (2026-09-26): what became of every id this take-over looked at - adopted / bodyOnly / spawned /
            // leaving / refused:<reason> - for the accounting line below (every Final row must be accounted for).
            var outcome = new Dictionary<string, string>();
            try
            {
                var regNow = FindReg(_myBldg);
                // Fold K4 (book once): a refused customer whose visit is registered and NOT booked goes back to the
                // native spawner.
                var refusedOpen = new List<string>();
                var todo = new List<(string key, CustomerVisitRow? v)>();
                foreach (var k in _puppets.Keys) todo.Add((k, CustomerHandoff.RowFrom(k, from)));
                todo.Sort((a, b) => SpotKey(a.v).CompareTo(SpotKey(b.v)));
                foreach (var (key, v) in todo)
                {
                    if (!_puppets.TryGetValue(key, out var pup)) continue;
                    if (AdoptPuppetAsNative(regNow, key, pup, v, out bool st, out bool lv, out bool mt, out bool bo))
                    {
                        if (bo) bodyOnly++;
                        try { ApplyActivity(pup, 0L, 0f, ""); } catch { }   // H-PUPPETANIM-1: the copy's machine flag off (the native sets its own)
                        try { if (pup.heldGo != null) UnityEngine.Object.Destroy(pup.heldGo); } catch { }
                        try { if (pup.go != null) UnityEngine.Object.Destroy(pup.go); } catch { }
                        _puppets.Remove(key);
                        adopted++;
                        if (st) withState++;
                        if (lv) leaving++;
                        if (st && mt) matched++;
                        outcome[key] = bo ? "bodyOnly" : (lv ? "leaving" : "adopted");
                    }
                    else
                    {
                        StartLeaving(pup); walked++;
                        if (!_adoptBooked) refusedOpen.Add(key);
                        outcome[key] = "refused:" + _adoptWhy;
                    }
                }
                var handled = new HashSet<string>();
                foreach (var t in todo) handled.Add(t.key);
                // Fold F5: a Final row with NO copy here (its copy timed out, or never streamed to this machine) used
                // to be dropped - the visit lost, the entry later respawned fresh. It is spawned at the entrance
                // WITH its state, when the entry is still this machine's to spawn (not consumed here, or a visit the
                // book-once registry holds - booked ones body-only, on a detached copy).
                // Fold K6 (rig run T-HANDOFF2-20260926-213941 leg 2: 12 rows, 8 vanished): a visit is over when the
                // customer is LEAVING, not when the order is completed - a gym order is completed at the door
                // (GymCustomer.Init -> PayEntranceFee -> CompleteOrder) while the workout goes on. Only leaving rows
                // are skipped; the money side stays exactly-once inside AdoptPuppetAsNative.
                if (from.Length > 0)
                    foreach (var r in CustomerHandoff.FinalRowsFrom(from))
                    {
                        if (r == null || string.IsNullOrEmpty(r.Id) || handled.Contains(r.Id)) continue;
                        if (r.Leaving) { outcome[r.Id] = "leaving"; continue; }
                        handled.Add(r.Id);
                        if (AdoptPuppetAsNative(regNow, r.Id, null, r, out bool st2, out bool lv2, out bool mt2, out bool bo2))
                        {
                            adopted++; noCopy++;
                            if (bo2) bodyOnly++;
                            if (st2) withState++;
                            if (lv2) leaving++;
                            if (st2 && mt2) matched++;
                            outcome[r.Id] = bo2 ? "bodyOnly" : (lv2 ? "leaving" : "spawned");
                        }
                        else
                        {
                            if (!_adoptBooked) refusedOpen.Add(r.Id);
                            outcome[r.Id] = "refused:" + _adoptWhy;
                        }
                    }
                // Every customer this take-over looked at goes into the till ledger (the rig oracle) on the machine that
                // keeps the books. (Fold F4's mark clearing is gone: the book-once registry keeps a visit's booking state
                // across any number of hand-offs, so a partner forward after a take-back is booked or suppressed there.)
                bool booksNow = false;
                try { booksNow = regNow != null && MergerFlip.BooksHere(regNow); } catch { }
                if (booksNow)
                    foreach (var id in handled) CustomerHandoff.LedgerAdd(id, regNow);
                // Fold K4 (book once): a refused visit that is registered and NOT booked: its entry was consumed here and
                // no body carries it now - opened again, so the native spawner brings it back (it books once, like any).
                foreach (var id in refusedOpen)
                {
                    try
                    {
                        if (!BookOnce.IsRegistered(id) || BookOnce.IsBooked(id)) continue;
                        var e = CustomerEntrySync.TryFindEntry(regNow, id);
                        if (e == null || !e.completed) continue;
                        e.completed = false;
                        reopened++;
                        outcome.TryGetValue(id, out var oc);
                        Plugin.Logger.LogInfo($"[Handoff] {id}: {oc} and not booked - its entry is open again, the native spawner brings that customer back.");
                    }
                    catch (Exception rx) { Plugin.Logger.LogWarning($"[Handoff] reopen {id}: {rx.Message}"); }
                }
                // Fold K6: every row of the Final accounted for: adopted + bodyOnly + spawned + leaving + refused = rows.
                if (from.Length > 0)
                {
                    try
                    {
                        var finalRows = CustomerHandoff.FinalRowsFrom(from);
                        if (finalRows.Count > 0)
                        {
                            int nA = 0, nB = 0, nS = 0, nL = 0, nR = 0, noWhy = 0, unacc = 0, rows = 0;
                            var whys = new Dictionary<string, int>();
                            foreach (var r in finalRows)
                            {
                                if (r == null || string.IsNullOrEmpty(r.Id)) continue;
                                rows++;
                                if (!outcome.TryGetValue(r.Id, out var oc)) { unacc++; continue; }
                                if (oc == "adopted") nA++;
                                else if (oc == "bodyOnly") nB++;
                                else if (oc == "spawned") nS++;
                                else if (oc == "leaving") nL++;
                                else
                                {
                                    nR++;
                                    string why = oc.StartsWith("refused:", StringComparison.Ordinal) ? oc.Substring(8) : "";
                                    if (why.Length == 0) noWhy++;
                                    else { whys.TryGetValue(why, out var wn); whys[why] = wn + 1; }
                                }
                            }
                            bool ok = unacc == 0 && noWhy == 0 && nA + nB + nS + nL + nR == rows;
                            var wl = new List<string>();
                            foreach (var kv in whys) wl.Add($"{kv.Key} x{kv.Value}");
                            Plugin.Logger.LogInfo($"[Handoff] final rows accounted @{_myBldg} from '{from}': rows={rows} adopted={nA} bodyOnly={nB} spawned={nS} leaving={nL} refused={nR} noReason={noWhy} unaccounted={unacc} accounted={(ok ? "ok" : "BAD")}{(wl.Count > 0 ? " (refused: " + string.Join("; ", wl) + ")" : "")}.");
                        }
                    }
                    catch (Exception ax) { Plugin.Logger.LogWarning($"[Handoff] accounting: {ax.Message}"); }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Handoff] take-over: {ex.Message}"); }
            try { IndoorCustomerSpawner.EnableCustomersSpawn(); } catch { }   // step 5: spawning back on AFTER the adoption
            CustomerHandoff.ConsumeFinal(from);
            LastAdopted = adopted; LastWithState = withState; LastLeaving = leaving; LastWalked = walked; LastMatched = matched;
            LastAdoptFrom = from; LastAdoptVia = via;
            Plugin.Logger.LogInfo($"[Customers] native mode in '{_myBldg}' — spawner restored; {adopted} puppet(s) adopted in place ({withState} with state, {leaving} leaving, {walked} walking out) from '{from}' via {via}; {bodyOnly} body-only (order already booked), {noCopy} spawned without a copy, {BookOnce.Count} visit(s) in the book-once registry, {reopened} refused entr(ies) reopened.");
            if (withState > 0 || via != "no-final")
                Plugin.Logger.LogInfo($"[Handoff] adopt: fee suppressed={CustomerHandoff.FeeSuppressed - fee0} complaint suppressed={CustomerHandoff.ComplaintSuppressed - comp0} progress matched={matched}/{withState}.");
        }

        /// <summary>Fold R0: the game's own "this interior is in": BuildingManager.EnterBuildingCoroutine
        /// (:549-621) sets enteringBuilding at its start and clears it only after LoadBuilding, LoadItems, the move
        /// to the spawn point, onEnterBuilding, DelayedEnterBuildingActions and the fade-in - and it is the
        /// building this machine tracks.</summary>
        private static bool IndoorReady()
        {
            try
            {
                if (!BuildingManager.IsInsideBuilding) return false;
                var bm = InstanceBehavior<BuildingManager>.Instance;
                if (bm == null || bm.enteringBuilding) return false;
                var r = bm.buildingRegistration;
                return r != null && GameStateReader.AddressKey(r) == _myBldg;
            }
            catch { return false; }
        }

        private static int SpotKey(CustomerVisitRow? v) => v == null || v.Spot < 0 ? int.MaxValue : v.Spot;

        /// <summary>H-HANDOFF-1 (ruling a): the continuous visit-row stream, only while I simulate here.</summary>
        private static void HandoffStreamTick()
        {
            if (string.IsNullOrEmpty(_myBldg) || _awaitFinalFrom.Length > 0 || _followerHere) return;
            if (!IAmSimulatorFor(_myBldg) || _myBldg == CustomerHandoff.StoppedStreamingFor) return;
            CustomerHandoff.StreamTick(_myBldg, _captureForStream);
        }
        private static readonly Func<(BuildingRegistration? reg, List<CustomerVisitRow> rows)> _captureForStream = () =>
        {
            var reg = FindReg(_myBldg);
            return (reg, CaptureLiveRows(reg));
        };

        /// <summary>H-HANDOFF-1: a visit row for every live customer with a cross-machine id (the exit snapshot
        /// and the stream; the authority swap captures inline, just before each release).</summary>
        internal static List<CustomerVisitRow> CaptureLiveRows(BuildingRegistration? reg)
        {
            var rows = new List<CustomerVisitRow>();
            try
            {
                foreach (var c in IndoorCustomerSpawner.Customers)
                {
                    if (c == null || c.isPlayer) continue;
                    string id = RowIdOf(c, reg);
                    if (id.StartsWith("i", StringComparison.Ordinal)) continue;
                    var r = CustomerHandoff.Capture(c, id);
                    if (r != null) rows.Add(r);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Handoff] capture live: {ex.Message}"); }
            return rows;
        }

        /// <summary>Round-43: spawn the puppet's schedule entry as a real customer and move the body to
        /// the puppet's spot. Uses the game's own (private) SpawnCustomer(CustomerEntry).</summary>
        private static readonly HashSet<string> _outOnce = new();   // pids whose building read "" on the last election pass (HIGH-1)

        /// <summary>Folds K4/K6: why the last AdoptPuppetAsNative refused, and whether that customer's order is
        /// already booked on this machine (the book-once registry; a refused unbooked visit is reopened).</summary>
        private static string _adoptWhy = "";
        private static bool _adoptBooked;

        /// <summary>Fold K6: an order that holds nothing but entrance-fee lines - a gym visit, completed at the door
        /// while the workout goes on (the native hourly pass can append a second fee line: still fee-only).</summary>
        private static bool FeeOnly(List<Entities.OrderEntry>? l, string fee)
        {
            if (string.IsNullOrEmpty(fee) || l == null || l.Count == 0) return false;
            foreach (var e in l) if (e == null || e.itemName != fee) return false;
            return true;
        }

        /// <summary>Fold K1: a detached copy of a booked order for the adopted body - Customer.Init (:197-200 the demand
        /// score) and SelfServiceCustomer.Init (:12 a basket-less shop REPLACES the item list with one random item)
        /// write the order they are handed, and a booked order sits in the till (IndoorCustomerSpawner.cs:266-272
        /// hands the entry's own Order to the body).</summary>
        private static Order CloneOrder(Order o)
        {
            var c = new Order
            {
                timestamp = o.timestamp, completed = o.completed, customerServiceSkill = o.customerServiceSkill,
                cleanliness = o.cleanliness, customerDemandScore = o.customerDemandScore,
            };
            c.entries = new List<Entities.OrderEntry>();
            if (o.entries != null)
                foreach (var e in o.entries)
                    if (e != null)
                        c.entries.Add(new Entities.OrderEntry
                        {
                            itemName = e.itemName, price = e.price, wholesalePrice = e.wholesalePrice, available = e.available,
                            priceAccceptable = e.priceAccceptable, paid = e.paid, processed = e.processed,
                        });
            c.customerDemandTypes = o.customerDemandTypes != null ? new List<string>(o.customerDemandTypes) : new List<string>();
            return c;
        }

        /// <summary>Book once: a detached Order built from a visit row (items with their done/paid flags, completed), the
        /// rest (timestamp, scores, demand types) from <paramref name="basis"/> when there is one.</summary>
        private static Order OrderFromRow(CustomerVisitRow v, Order? basis)
        {
            var c = new Order
            {
                timestamp = basis?.timestamp, completed = v.Completed, customerServiceSkill = basis?.customerServiceSkill ?? 0f,
                cleanliness = basis?.cleanliness ?? 0f, customerDemandScore = basis?.customerDemandScore ?? 0f,
            };
            c.entries = new List<Entities.OrderEntry>();
            if (v.Entries != null)
                foreach (var ve in v.Entries)
                    if (ve != null)
                        c.entries.Add(new Entities.OrderEntry
                        {
                            itemName = ve.ItemName ?? "", price = ve.Price, wholesalePrice = ve.WholesalePrice,
                            available = ve.Available, priceAccceptable = ve.Acceptable, paid = ve.Paid, processed = ve.Processed,
                        });
            CustomerHandoff.MarkPickedFromRow(c.entries, v.Entries);   // stock once: grabbed lines stay grabbed
            c.customerDemandTypes = basis?.customerDemandTypes != null ? new List<string>(basis.customerDemandTypes) : new List<string>();
            return c;
        }

        private static string OrderSig(Order? o)
        {
            if (o == null) return "none";
            int n = 0, paid = 0; float total = 0f;
            if (o.entries != null) foreach (var e in o.entries) if (e != null) { n++; total += e.price; if (e.paid) paid++; }
            return $"items={n} paid={paid} total=${total:F2} completed={o.completed} score={o.customerDemandScore:F2}";
        }

        /// <summary>Fold H2(a) (2026-09-27): the visit <paramref name="id"/> was just booked on this machine by another Order
        /// (a forward, or any other funnel) while this machine's own body of that visit is alive with an open order: it
        /// would queue again and the employee would serve it a second time (the paper bag FullServiceEmployee.cs:95, the
        /// retail lines :59/:166-196) - stock taken twice. Its order is finished the way the native exit clean-up does it
        /// (Customer.ForceFinishOrder :363-371 drops the unprocessed lines) but WITHOUT a till add, completed first so
        /// Customer.Leave (:309-313) puts nothing back on a shelf, and the body leaves.</summary>
        internal static int FinishLiveBodiesOf(string id, Order? booking)
        {
            int n = 0;
            try
            {
                if (string.IsNullOrEmpty(id)) return 0;
                List<Customer>? hit = null;
                foreach (var lc in IndoorCustomerSpawner.Customers)
                {
                    if (lc == null || lc.isPlayer || lc.order == null || lc.order.completed) continue;
                    if (booking != null && ReferenceEquals(lc.order, booking)) continue;
                    if (LiveIdOf(lc) != id) continue;
                    (hit ??= new List<Customer>()).Add(lc);
                }
                if (hit == null) return 0;
                foreach (var lc in hit)
                {
                    try
                    {
                        CustomerHandoff.ReturnUnbooked(lc.order, booking);   // H-HANDOFF-1 stock once: held units the booking does not sell go back on a shelf
                        var oes = lc.order.entries;
                        int before = oes != null ? oes.Count : 0;
                        if (oes != null) oes.RemoveAll(x => x == null || !x.processed);
                        int after = oes != null ? oes.Count : 0;
                        lc.order.completed = true;
                        lc.Leave();
                        n++;
                        Plugin.Logger.LogInfo($"[BookOnce] {id} booked while its body lived here - its order finished ({before - after} open line(s) dropped, {after} kept, no till add); it leaves without being served again.");
                    }
                    catch (Exception lx) { Plugin.Logger.LogWarning($"[BookOnce] finish body {id}: {lx.Message}"); }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BookOnce] finish bodies {id}: {ex.Message}"); }
            return n;
        }

        /// <summary>H-HANDOFF-1 stock once: the units a live body of visit <paramref name="id"/> holds in this shop (open order,
        /// not a booked copy) - a forward booking that visit credits them instead of deducting again.</summary>
        internal static List<KeyValuePair<string, float>> LiveTakenOf(string id, BuildingRegistration? reg)
        {
            var l = new List<KeyValuePair<string, float>>();
            try
            {
                if (string.IsNullOrEmpty(id) || reg == null) return l;
                var bm = InstanceBehavior<BuildingManager>.Instance;
                if (bm == null || !ReferenceEquals(bm.buildingRegistration, reg)) return l;
                foreach (var lc in IndoorCustomerSpawner.Customers)
                {
                    if (lc == null || lc.isPlayer || lc.order?.entries == null || lc.order.completed || BookOnce.IsBookedCopy(lc.order)) continue;
                    if (LiveIdOf(lc) != id) continue;
                    foreach (var e in lc.order.entries)
                    {
                        if (e == null || string.IsNullOrEmpty(e.itemName)) continue;
                        bool bag = CustomerHandoff.IsPaperBag(e.itemName);
                        if (bag ? e.available : CustomerHandoff.TakenOe(e))
                            l.Add(new KeyValuePair<string, float>(e.itemName, e.wholesalePrice));
                    }
                }
            }
            catch { }
            return l;
        }

        /// <summary>A live body's visit id: its Order in the book-once map, else this machine's own stream identity.</summary>
        private static string? LiveIdOf(Customer? lc)
        {
            try
            {
                if (lc == null) return null;
                var id = BookOnce.IdOf(lc.order);
                if (id != null) return id;
                if (_custEntryIds.TryGetValue(lc.GetInstanceID(), out var t) && ReferenceEquals(t.order, lc.order)) return t.id;
            }
            catch { }
            return null;
        }

        private static bool AdoptPuppetAsNative(BuildingRegistration? reg, string entryId, Puppet? pup, CustomerVisitRow? v,
                                                out bool withState, out bool leftNow, out bool matched, out bool bodyOnly)
        {
            withState = false; leftNow = false; matched = false; bodyOnly = false;
            _adoptWhy = ""; _adoptBooked = false;
            try
            {
                if (pup != null && pup.go == null) { _adoptWhy = "copy has no body"; return false; }
                if (entryId.StartsWith("i", StringComparison.Ordinal)) { _adoptWhy = "local-only id"; return false; }
                bool books = false;
                try { books = reg != null && MergerFlip.BooksHere(reg); } catch { }
                var entry = CustomerEntrySync.TryFindEntry(reg, entryId);
                // Book once: on the machine that keeps the books, every adopted visit is registered (initial state: booked
                // when its Order already sits in the till, or a forward for it was already adopted).
                if (books) BookOnce.Register(reg, entryId, entry, "adopted");
                bool synthetic = false;
                if (entry == null && books && v != null && BookOnce.IsRegistered(entryId))
                {
                    // A registered visit whose entry left this machine's table (an older forward claimed and retired it, or
                    // the schedule rotated): the visit goes on from a stand-alone entry built from the row (never put in the
                    // table). Its Order is known to the registry: a booked visit is body-only (any later payment for it is
                    // suppressed), an unbooked one books once like any other.
                    try
                    {
                        var se = new AI.Customers.CustomerEntries.CustomerEntry
                        {
                            spawnTime = v.SpawnMin >= 0f ? new BigAmbitions.DayNightCycle.Timestamp(v.SpawnMin) : TimeHelper.Now(),
                            completed = true,
                        };
                        se.order = OrderFromRow(v, null);
                        se.order.timestamp = se.spawnTime;
                        BookOnce.MapOrder(se.order, entryId);
                        entry = se; synthetic = true;
                    }
                    catch (Exception sx) { Plugin.Logger.LogWarning($"[Handoff] stand-alone entry {entryId}: {sx.Message}"); entry = null; }
                }
                if (entry == null) { _adoptWhy = "entry not in this machine's table (claimed by a forward, or rotated)"; return false; }
                if (entry.order == null) { _adoptWhy = "entry has no order"; return false; }
                // Fold F5: no copy (pup == null) - spawned only from a row, and only when the entry is still this
                // machine's to spawn: not consumed here, or a visit the book-once registry holds (booked or not).
                if (pup == null && v == null) { _adoptWhy = "no visit row"; return false; }
                if (pup == null && entry.completed && !synthetic && !BookOnce.IsRegistered(entryId)) { _adoptWhy = "entry consumed on this machine"; return false; }
                if (pup == null)
                {
                    bool live = false;
                    // Fold L: a row-built stand-in entry is a NEW object - a live body of the same visit is found by its id.
                    try { foreach (var lc in IndoorCustomerSpawner.Customers) if (lc != null && !lc.isPlayer && (ReferenceEquals(lc.customerEntry, entry) || LiveIdOf(lc) == entryId)) { live = true; break; } } catch { }
                    if (live) { _adoptWhy = "already live on this machine"; return false; }
                }
                string fee = "";
                try { fee = BusinessTypeHelper.GetEntranceFeeNameForBusinessType(InstanceBehavior<BuildingManager>.Instance.businessType) ?? ""; } catch { }
                // Book once (replaces fold F2's 'done or in the till'): on the machine that keeps the books, a visit the
                // registry holds as BOOKED is never overwritten: the row's items and completed flag are not applied and no
                // fee line is added. The body alone is adopted, on a detached copy (fold K1), and it is NOT walked out -
                // it keeps its visit (eats, works out, sits); any later payment for it is suppressed by the registry.
                bool booked = books && BookOnce.IsBooked(entryId);
                _adoptBooked = booked;
                bool feeOnlyBooked = booked && FeeOnly(entry.order.entries, fee);
                bodyOnly = booked;
                // NOT guarded on entry.completed, deliberately: that flag means 'consumed by the spawner on this
                // machine' (native sets it at spawn, paid or not), so it is true for every live shopper this machine
                // ever spawned. A sale this machine already BOOKED through a forward reaches here only through the
                // stand-alone entry: the owner's forward-adopt removes the claimed entry from the table (fold H1).
                //
                // H-HANDOFF-1 step 6: load the old simulator's visit row BEFORE the spawn, so the native Init reads
                // it: the visit clock (Customer.SetCurrentTimeState :593-611 and the subclasses read
                // customerEntry.spawnTime - the "already in action" branches resume the visit), the order's items
                // with their done/paid flags, and completed. The item list is changed IN PLACE and the Order is
                // never replaced: EntryIdForOrder finds an entry by the order REFERENCE (CustomerEntrySync.cs:141).
                // SelfServiceCustomer.Init (:9-14) then only reorders a basket shop's list, or keeps one item of a
                // basket-less shop's single-item list - which is the source's own item. No patch needed.
                bool feeInSnapshot = false, recharge = false;
                if (v != null)
                {
                    try
                    {
                        if (v.SpawnMin >= 0f) entry.spawnTime = new BigAmbitions.DayNightCycle.Timestamp(v.SpawnMin);
                        var ord = entry.order;
                        if (ord != null && !booked)   // fold F2: a booked order is left exactly as it is
                        {
                            if (v.Entries != null && v.Entries.Count > 0 && ord.entries != null)
                            {
                                ord.entries.Clear();
                                foreach (var ve in v.Entries)
                                    if (ve != null)
                                        ord.entries.Add(new Entities.OrderEntry
                                        {
                                            itemName = ve.ItemName ?? "", price = ve.Price, wholesalePrice = ve.WholesalePrice,
                                            available = ve.Available, priceAccceptable = ve.Acceptable, paid = ve.Paid, processed = ve.Processed,
                                        });
                            }
                            if (v.Entries != null && v.Entries.Count > 0) CustomerHandoff.MarkPickedFromRow(ord.entries, v.Entries);   // stock once
                            ord.completed = v.Completed;
                            if (fee.Length > 0 && ord.entries != null)
                                foreach (var oe in ord.entries) if (oe != null && oe.itemName == fee) { feeInSnapshot = true; break; }
                            // Fold F3: the fee line in a row does not mean the fee was CHARGED - a merged member's
                            // machine adds it without keeping the books. When this machine books and the row's
                            // source did not, the fee is charged HERE once.
                            // Fold K5 (2026-09-26): not by re-running the native check - that judges the fee with a NEW
                            // random citizen (IndoorCustomerSpawner.cs:293-306) and can refuse a shopper already inside.
                            // The snapshot's own fee line stays, paid as the snapshot says; it is booked with the order
                            // (a gym's PayEntranceFee still runs and completes a fee-only order into the till here).
                            if (feeInSnapshot && books && !CustomerHandoff.RowSourceBooks(entryId))
                            {
                                recharge = true;
                                CustomerHandoff.FeeRecharged++;
                            }
                        }
                        withState = !booked;
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Handoff] apply visit {entryId}: {ex.Message}"); }
                }
                // H-HANDOFF-1 part B step B1: the held spot this body should get, recorded BEFORE the spawn (keyed by
                // its CustomerEntry - the tree may tick inside Init).
                if (v != null) CustomerSeatPins.WantBefore(entry, entryId, v);
                int before = IndoorCustomerSpawner.Customers.Count;
                _spawnCustomerM ??= HarmonyLib.AccessTools.Method(typeof(IndoorCustomerSpawner), "SpawnCustomer",
                    new[] { typeof(AI.Customers.CustomerEntries.CustomerEntry) });
                if (_spawnCustomerM == null) { CustomerSeatPins.DropEntry(entry, "no spawner"); withState = false; bodyOnly = false; _adoptWhy = "SpawnCustomer not found"; return false; }
                // H-HANDOFF-1 step 7: Init runs synchronously inside SpawnCustomer (IndoorCustomerSpawner.cs:272), so
                // this scope covers the entrance fee, PayEntranceFee and the arrival complaint (CustomerHandoff).
                // Book once: the fee CHECK (K5 - it appends a fee line and a new random citizen may refuse the spawn) is
                // suppressed for a booked visit and whenever the snapshot holds the fee line; the gym's fee PAYMENT only
                // when the source charged it or no money is kept here (F3). A booked visit's PayEntranceFee is no longer
                // skipped here: it completes the detached copy and the registry suppresses that till add.
                bool suppressCheck = booked || (withState && feeInSnapshot);
                bool suppressPay = withState && feeInSnapshot && !recharge;
                // Fold K1 (2026-09-26): a body-only adoption hands the body a DETACHED COPY of the booked order, so the
                // native Init cannot re-value the Order object that sits in the till; the entry keeps its own Order.
                Order? tillOrder = null;
                string tillBefore = "";
                if (booked)
                {
                    tillOrder = entry.order;
                    tillBefore = OrderSig(tillOrder);
                    // Book once: the copy carries the ROW's progress when there is a row (the partner's live order - a diner
                    // keeps the food it is eating); this machine's own Order can be stale (the native exit clean-up
                    // ForceFinishOrder strips its unprocessed items, rig run T-HANDOFFSEAT-20260927-011851: items=0 and the
                    // body left at once).
                    entry.order = v?.Entries != null && v.Entries.Count > 0 ? OrderFromRow(v, tillOrder) : CloneOrder(tillOrder);
                    // Fold H2(b): no new shopping on a booked visit - the copy keeps only its DONE lines (the food a diner
                    // eats, a paid fee), and it is marked so Customer.Leave -> ReturnItemsToShelf (Customer.cs:307-313)
                    // never puts the row's items (picked on the partner's machine) onto this machine's shelves.
                    try { entry.order.entries?.RemoveAll(x => x == null || !x.processed); } catch { }
                    BookOnce.MarkBookedCopy(entry.order);
                    BookOnce.MapOrder(entry.order, entryId);   // the copy's payments are this visit's: suppressed
                }
                // H-HANDOFF-1 STOCK ONCE: on the machine that keeps the books the body's taken lines are settled BEFORE the
                // spawn (Init and the in-action catch-up run inside it): units it carried from here are credited, units
                // picked on the partner's machine come off a shelf now, and all are marked so no native re-take takes them
                // again (CustomerHandoff.StockOnAdopt). A refused spawn undoes it.
                CustomerHandoff.StockUndo? stockUndo = null;
                if (books) stockUndo = CustomerHandoff.StockOnAdopt(reg, entryId, entry.order, booked, v != null && v.Leaving);
                CustomerHandoff.BeginAdopt(withState || booked, suppressCheck, suppressPay);
                bool spawnThrew = false;
                try { _spawnCustomerM.Invoke(null, new object[] { entry }); }
                catch { spawnThrew = true; throw; }
                finally
                {
                    CustomerHandoff.EndAdopt();
                    if (tillOrder != null) entry.order = tillOrder;
                    // Fold S2 (2026-09-27): a throw skips the refused-spawn check below - the stock settlement is undone here,
                    // unless a body was added before the throw (it keeps what it was settled with).
                    if (spawnThrew)
                    {
                        try
                        {
                            if (IndoorCustomerSpawner.Customers.Count <= before) CustomerHandoff.StockAdoptUndo(reg, entryId, stockUndo);
                            else Plugin.Logger.LogWarning($"[Stock] adopt {entryId}: the spawn threw after adding a body - its stock settlement is kept.");
                        }
                        catch { }
                    }
                }
                if (tillOrder != null)
                {
                    string tillAfter = OrderSig(tillOrder);
                    Plugin.Logger.LogInfo($"[Handoff] body-only {entryId}: booked order before {tillBefore} -> after {tillAfter} ({(tillAfter == tillBefore ? "untouched" : "CHANGED")}); the body carries a detached copy{(feeOnlyBooked ? " - fee-only visit" : "")}{(synthetic ? " - stand-alone entry" : "")}, it keeps its visit.");
                }
                var list = IndoorCustomerSpawner.Customers;
                if (list.Count <= before) { CustomerHandoff.StockAdoptUndo(reg, entryId, stockUndo); CustomerSeatPins.DropEntry(entry, "spawn refused"); withState = false; bodyOnly = false; _adoptWhy = "spawn refused (capacity or fee)"; return false; }   // the entry stays unconsumed
                // Consumed — the regular spawner must not spawn it again. H-HANDOFF-1 (C3): set only AFTER the
                // success check; before, a refused spawn still consumed the entry and that shopper never came.
                entry.completed = true;
                if (!booked && books) BookOnce.MapOrder(entry.order, entryId);
                var c = list[list.Count - 1];
                if (c == null) { CustomerSeatPins.DropEntry(entry, "no body"); return true; }
                // Book once (replaces fold F2's walk-out, rig run T-HANDOFF1-20260926-212003): a booked body stays; the
                // second till add its checkout would make (Customer.CompleteOrder :385-397 has no completed check) is
                // suppressed by the registry. Only a body that was already leaving on the source walks out.
                bool leave = v != null && v.Leaving && (withState || booked);
                // Part B step B2: reserve the wanted seat / machine right after the spawn, before another adopted body's
                // random pick can take it; a body that walks straight out wants nothing.
                if (leave) CustomerSeatPins.DropEntry(entry, "walks out"); else CustomerSeatPins.ReserveAfter(entry, c);
                if (pup != null && pup.go != null)   // fold F5: a body without a copy stays where SpawnCustomer put it (the entrance)
                {
                Vector3 pos = pup.go.transform.position;
                // Round-44: HOLD the position — the native spawn-init repositions the body + assigns
                // objectives over the next frames; a single warp raced it (field: teleport + bolting).
                // (H-HANDOFF-1: not for a body that walks straight out - the hold would drag it back.)
                // Part B step B5: not for a seat / machine want either - UseWorkoutMachine switches the agent off and
                // snaps the body to the machine, and the hold's transform fallback would pull it off again.
                if (!leave && !CustomerSeatPins.HasSpotWant(entry)) _warpHolds.Add((c, pos, Time.unscaledTime + 0.75f));
                try
                {
                    var ag = c.tpc != null ? c.tpc.navmeshAgent : null;
                    if (ag != null && ag.enabled) ag.Warp(pos);
                    else c.transform.position = pos;
                }
                catch { c.transform.position = pos; }
                // Round-44: the adopted native KEEPS the puppet's look (looks survive handoffs).
                if (pup.look != null) ApplyLookTo(c.tpc, pup.look);
                }
                _custEntryIds[c.GetInstanceID()] = (c.order, entryId);   // identity continuity for my own stream
                if (withState && v != null)
                {
                    // H-HANDOFF-1 step 6: the SAME person - SpawnCustomer drew a random citizen
                    // (IndoorCustomerSpawner.cs:255) and the Init Postfix recorded it for the price check
                    // (BusinessPatches.cs Patch_Customer_Init_HelperComplaints), so both are overwritten.
                    // SetAppearance is left alone (GymCustomer keeps characterDataAtStart from it); the look
                    // above already dresses the body.
                    try
                    {
                        if (!string.IsNullOrEmpty(v.NationalID) || !string.IsNullOrEmpty(v.Name))
                        {
                            c.citizenData = new AI.Citizens.CitizenData
                            {
                                NationalID = v.NationalID ?? "", Name = v.Name ?? "", Age = v.Age, Neighbourhood = v.Neighbourhood ?? "",
                                SocialClass = (AI.Citizens.SocialClass)v.SocialClass, Gender = (BigAmbitions.Characters.Gender)v.Gender,
                            };
                            if (c.order != null) CustomerEntrySync.RecordCitizen(c.order, c.citizenData);
                        }
                    }
                    catch (Exception cx) { Plugin.Logger.LogWarning($"[Handoff] citizen {entryId}: {cx.Message}"); }
                    try { matched = SameProgress(c.order, v); } catch { }
                }
                if (leave)
                {
                    {
                        // Already walking out on the source: its order was finished there, so nothing goes back on
                        // a shelf and nothing is billed (Customer.Leave :309 and ForceFinishOrder :366 both skip a
                        // completed order) - completed is forced for a source that could not set it.
                        try { if (c.order != null) c.order.completed = true; c.Leave(); leftNow = true; }
                        catch (Exception lx) { Plugin.Logger.LogWarning($"[Handoff] leave {entryId}: {lx.Message}"); }
                    }
                }
                return true;   // ([AdoptProbe] retired 2026-07-07 — adoption field-confirmed healthy)
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] adopt puppet: {ex.Message}"); _adoptWhy = "error: " + ex.Message; return false; }
        }

        /// <summary>H-HANDOFF-1 readout: the adopted body's order reached exactly the visit row's progress
        /// (item count, items done, completed).</summary>
        private static bool SameProgress(Order? o, CustomerVisitRow v)
        {
            if (o == null || o.entries == null || v == null) return false;
            int n = 0, k = 0, vn = 0, vk = 0;
            foreach (var e in o.entries) if (e != null) { n++; if (e.processed) k++; }
            foreach (var e in v.Entries) if (e != null) { vn++; if (e.Processed) vk++; }
            return n == vn && k == vk && o.completed == v.Completed;
        }

        /// <summary>Round-44: keep adopted bodies pinned while the native spawn-init settles.</summary>
        private static void TickWarpHolds()
        {
            if (_warpHolds.Count == 0) return;
            float now = Time.unscaledTime;
            for (int i = _warpHolds.Count - 1; i >= 0; i--)
            {
                var (c, pos, until) = _warpHolds[i];
                if (c == null || now > until) { _warpHolds.RemoveAt(i); continue; }
                try
                {
                    if ((c.transform.position - pos).sqrMagnitude > 0.09f)
                    {
                        var ag = c.tpc != null ? c.tpc.navmeshAgent : null;
                        if (ag != null && ag.enabled) ag.Warp(pos);
                        else c.transform.position = pos;
                    }
                }
                catch { _warpHolds.RemoveAt(i); }
            }
        }

        /// <summary>Round-44b: dress a body from the structured look via native SetAppearance.
        /// (look-drift probes retired 2026-07-07 — pool-reuse identity fix field-confirmed.)</summary>
        private static void ApplyLookTo(ThirdPersonCharacter? tpc, CustomerPuppetLookPayload p)
        {
            try
            {
                if (tpc == null || p == null || p.Elements.Count == 0) return;
                var data = new CharacterData
                {
                    gender    = (BigAmbitions.Characters.Gender)p.Gender,
                    strength  = p.Strength,
                    fatness   = p.Fatness,
                    color     = new Color32((byte)((p.ColorPacked >> 24) & 0xFF), (byte)((p.ColorPacked >> 16) & 0xFF), (byte)((p.ColorPacked >> 8) & 0xFF), (byte)(p.ColorPacked & 0xFF)),
                    eyesColor = new Color32((byte)((p.EyesPacked >> 24) & 0xFF), (byte)((p.EyesPacked >> 16) & 0xFF), (byte)((p.EyesPacked >> 8) & 0xFF), (byte)(p.EyesPacked & 0xFF)),
                };
                foreach (var e in p.Elements)
                    if (e != null)
                        data.elements.Add(new BigAmbitions.Characters.Appearance.AppearanceElementData
                        { type = (BigAmbitions.Characters.Appearance.AppearanceElementType)e.Type, variantId = e.VariantId, colorId = e.ColorId });
                foreach (var b in p.Blends)
                    if (b != null)
                        data.blendshapes.Add(new BigAmbitions.Characters.FacialBlendshape { name = b.Name, value = b.Value });
                tpc.appearanceSetter.SetAppearance(data);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] apply look: {ex.Message}"); }
        }

        private static System.Reflection.MethodInfo? _spawnCustomerM;

        private static BuildingRegistration? FindReg(string addressKey)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null || string.IsNullOrEmpty(addressKey)) return null;
                foreach (var r in gi.BuildingRegistrations)
                    if (r != null && GameStateReader.AddressKey(r) == addressKey) return r;
            }
            catch { }
            return null;
        }

        /// <summary>Round-43: a live customer's cross-machine row id — the schedule entry id when known,
        /// else a machine-local fallback ("i&lt;instanceId&gt;", churns on handoff). Round-45: the cache
        /// hit requires the SAME Order reference — a pooled body reused for a new customer carries a new
        /// order, which invalidates the stale mapping.</summary>
        private static string RowIdOf(Customer c, BuildingRegistration? reg)
        {
            int iid = c.GetInstanceID();
            var order = c.order;
            if (_custEntryIds.TryGetValue(iid, out var cached) && ReferenceEquals(cached.order, order))
                return cached.id;
            string id = "";
            try { id = CustomerEntrySync.EntryIdForOrder(reg, order); } catch { }
            bool fallback = string.IsNullOrEmpty(id);
            if (fallback) id = "i" + iid + "-" + (order?.GetHashCode() ?? 0);
            // ROUND-131: a row id CHANGING for a live customer is invisible here but catastrophic on the
            // follower — the old id stops appearing in the batch, so its puppet walks to the exit and is
            // destroyed, while the new id spawns a fresh body.  On screen that is a customer appearing and
            // immediately leaving, over and over, while the simulator's own customers are perfectly healthy
            // (this run: 9 joins, 0 complaints, 1 Served departure).  The cache is keyed by instance id and
            // invalidated when the ORDER OBJECT changes, and the id falls back to an order hash when no entry
            // mapping resolves — so either a re-assigned order or a flapping entry lookup renames a customer
            // mid-life.  Say so, once per customer, with which branch produced each id.
            // (Probe retired round-158: renames here are pooled-body reuse — a recycled body's new
            // occupant — not identity corruption; the follower-side destroy/respawn handles it.)
            _custEntryIds[iid] = (order!, id);
            return id;
        }

        /// <summary>D-SKIPPACE-1 (2026-09-21): THE row-stream period, in real seconds - the ONE
        /// definition. 0.25 s normally; while a skip runs the shop's bodies move at MPRestSync.SkipPace,
        /// so a follower's puppet would be chasing a position a whole sped-up stride out of date - the
        /// stream speeds up with them, to a 20 Hz ceiling (pace clamped to 5) so a 50x skip cannot turn
        /// the wire into a firehose. Every reader that used to assume 0.25 s reads THIS.
        /// The 2.5 s stale timeout is NOT derived from it and does not change (a follower must still
        /// tolerate a couple of lost batches before walking a puppet out).</summary>
        internal static float StreamInterval
        {
            get { return 0.25f / Mathf.Clamp(MPRestSync.SkipPace, 1f, 5f); }
        }

        // ── Simulator: stream my live customers ─────────────────────────────────────────────────────
        private static void SimulatorStreamTick()
        {
            // H-PUPPETANIM-1: every "not simulating" exit empties the animator->row map (NoteSimTrigger's one compare).
            if (string.IsNullOrEmpty(_myBldg)) { ClearSimAnim(); return; }
            if (!_authority.TryGetValue(_myBldg, out var sim) || sim != MPConfig.PlayerId) { ClearSimAnim(); return; }
            // H-HANDOFF-1 step 5: while waiting for the old simulator's Final this machine has no customers of its
            // own yet, and an EMPTY batch would walk every copy on the other machine out. Same after an EXIT Final
            // (CustomerHandoff.StoppedStreamingFor): the interior is being torn down but the election has not
            // moved yet.
            if (_awaitFinalFrom.Length > 0) { ClearSimAnim(); return; }
            if (_readyPendingVia.Length > 0) { ClearSimAnim(); return; }   // fold R0: same - a take-over is due once the interior is ready
            if (_myBldg == CustomerHandoff.StoppedStreamingFor) { ClearSimAnim(); return; }
            if (Time.unscaledTime < _nextStreamAt) return;
            _nextStreamAt = Time.unscaledTime + StreamInterval;

            var p = new CustomerPuppetStatePayload { AddressKey = _myBldg, SimulatorPid = MPConfig.PlayerId, T = Time.unscaledTime };
            int tickLoopers = 0, tickSitEat = 0;   // H-PUPPETANIM-1: puppetacts arm
            try { _simAnimRow.Clear(); } catch { }  // H-PUPPETANIM-1: rebuilt from this tick's natives
            try
            {
                var reg = FindReg(_myBldg);
                foreach (var c in IndoorCustomerSpawner.Customers)
                {
                    if (c == null) continue;
                    var t = c.transform;
                    string rid = RowIdOf(c, reg);
                    var (heldName, heldFill) = HeldStateOf(c);
                    // H-PUPPETSTUTTER-1: a live read of what the real body is animating - Forward while IsMoving, else 0.
                    float rowFwd = 0f;
                    try
                    {
                        var an = c.tpc != null ? c.tpc.animator : null;
                        if (an != null)
                        {
                            bool mv = an.GetBool(BaseHuman.IsMoving);
                            if (mv) rowFwd = Mathf.Max(0f, an.GetFloat(BaseHuman.Forward));
                            NoteSimMoving(rid, mv);
                        }
                    }
                    catch { }
                    // H-PUPPETANIM-1: a live read of the looping activity states that are on, the dance type while
                    // dancing, and the workout machine this body is on (only while a loop is on - pooled bodies).
                    long rowLoops = 0L; float rowDance = 0f; string? rowAct = null;
                    try
                    {
                        var an2 = c.tpc != null ? c.tpc.animator : null;
                        if (an2 != null)
                        {
                            _simAnimRow[an2] = rid;
                            rowLoops = LoopsOf(an2);
                            if ((rowLoops & (1L << DanceBit)) != 0L && HasParam(an2, TypeOfDanceHash, AnimatorControllerParameterType.Float))
                                rowDance = an2.GetFloat(TypeOfDanceHash);
                            if (rowLoops != 0L && _bodyMachine.TryGetValue(an2.GetInstanceID(), out var mid) && !string.IsNullOrEmpty(mid)) rowAct = mid;
                        }
                        if (rowLoops != 0L) tickLoopers++;
                        if (IsSitEat(rowLoops)) tickSitEat++;
                    }
                    catch { }
                    p.Rows.Add(new PuppetRowInfo
                    {
                        Id   = rid,
                        X    = t.position.x, Y = t.position.y, Z = t.position.z,
                        Yaw  = t.eulerAngles.y,
                        Held = heldName,
                        Fill = heldFill,
                        Fwd  = rowFwd,
                        Loops = rowLoops,
                        Dance = rowDance,
                        ActItem = rowAct,
                    });
                    // Round-44: ship each customer's look ONCE so followers dress the same person.
                    if (!_looksSent.Contains(rid))
                    {
                        _looksSent.Add(rid);
                        var lp = CaptureLook(c.tpc);
                        if (lp != null)
                        {
                            lp.AddressKey = _myBldg; lp.SimulatorPid = MPConfig.PlayerId; lp.CustomerId = rid;
                            if (MPServer.IsRunning) MPServer.BroadcastCustomerLook(lp);
                            else MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.CustomerPuppetLook, MPConfig.PlayerId, lp));
                        }
                        else Plugin.Logger.LogInfo($"[Customers] look capture EMPTY for {rid} (no appearance data).");
                    }
                }
            }
            catch { }
            try
            {
                // H-PUPPETANIM-1: the one-shots queued since the previous batch ride this one (a new list only when any).
                if (_simEv.Count > 0) { p.Ev = _simEv; _simEv = new List<PuppetEventInfo>(); }
                ActsArmCheck(tickLoopers, tickSitEat);
            }
            catch { }
            try { var sw = _simMoving; _simMoving = _simMovingNext; _simMovingNext = sw; _simMovingNext.Clear(); } catch { }   // H-PUPPETSTUTTER-1: only this tick's natives carry over
            // ROUND-132: the follower was rendering 1-3 bodies while the simulator had a dozen customers
            // queued (QueueProbe: joined=14, one departure).  Rows are keyed by id on the receiver
            // (_puppets[r.Id]), so ANY two customers resolving to the SAME id collapse into one body — which
            // would look exactly like "most customers never appear, and the ones that do keep being replaced".
            // Count live customers against DISTINCT ids actually sent; if they disagree, identity is the bug.
            if (Time.unscaledTime >= _nextStreamStatAt)
            {
                _nextStreamStatAt = Time.unscaledTime + 5f;
                int live = 0; try { live = IndoorCustomerSpawner.Customers.Count; } catch { }
                var distinct = new HashSet<string>();
                foreach (var r in p.Rows) if (r != null) distinct.Add(r.Id);
                if (distinct.Count != p.Rows.Count || p.Rows.Count != live)
                    Plugin.Logger.LogWarning($"[PuppetChurn] STREAM MISMATCH: {live} live customer(s) → {p.Rows.Count} row(s) → "
                        + $"{distinct.Count} DISTINCT id(s). Duplicate ids collapse into one body on every follower.");
                // ("stream ok" success-path line retired round-158 — the mismatch warning stays as a tripwire.)
            }

            if (MPServer.IsRunning) MPServer.BroadcastCustomerPuppets(p);
            else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.CustomerPuppetState, MPConfig.PlayerId, p));
        }

        /// <summary>The prop in a simulated customer's hand ("" = empty) — first active child of the
        /// TPC's HandContent bone, name-normalized — plus its FILL (active direct children: shopping
        /// baskets visually fill with child item meshes as the customer shops, round-45).</summary>
        private static (string name, int fill) HeldStateOf(Customer c)
        {
            try
            {
                int id = c.GetInstanceID();
                if (!_handNodes.TryGetValue(id, out var hand))
                {
                    hand = null;
                    var root = (c.tpc != null ? c.tpc.transform : c.transform);
                    foreach (var tr in root.GetComponentsInChildren<Transform>(true))
                        if (tr != null && tr.name == "HandContent") { hand = tr; break; }
                    _handNodes[id] = hand;
                }
                if (hand == null) return ("", 0);
                for (int i = 0; i < hand.childCount; i++)
                {
                    var ch = hand.GetChild(i);
                    if (ch == null || !ch.gameObject.activeSelf) continue;
                    // Round-45b ([PropProbe]-taught): fill = the ACTIVE "Products*" children ONLY — the
                    // basket's other children (IK attach point, shadow caster) are always-on and counting
                    // them made puppets look full at pickup.
                    int fill = 0;
                    for (int j = 0; j < ch.childCount; j++)
                    {
                        var k = ch.GetChild(j);
                        if (k != null && k.name.StartsWith("Products", StringComparison.Ordinal) && k.gameObject.activeSelf) fill++;
                    }
                    return (RemotePlayerManager.CleanProp(ch.name), fill);
                }
            }
            catch { }
            return ("", 0);
        }

        // (prop-mirror probes retired 2026-07-07 — findings folded into the code: basket fill =
        // Products*-only children; templates require an ItemController; bags mint via GetRandomBag.)

        /// <summary>Round-42 — replay a simulated customer's emoji (complaints etc.) on the puppet.</summary>
        public static void ApplyEmote(CustomerPuppetEmotePayload p)
        {
            try
            {
                if (p == null || p.SimulatorPid == MPConfig.PlayerId) return;
                if (p.AddressKey != _myBldg || !_followerHere) return;
                if (string.IsNullOrEmpty(p.CustomerId)) return;
                if (!_puppets.TryGetValue(p.CustomerId, out var pup) || pup.tpc == null)
                {
                    // Round-43: the body may not exist yet (complaints fire at spawn instant, one state
                    // tick before the puppet appears) — hold the emote briefly.
                    _pendingEmotes[p.CustomerId] = (p.Emoji, p.Seconds <= 0f ? 3f : p.Seconds, Time.unscaledTime + 4f);
                    return;
                }
                PlayEmoteOn(pup, p.Emoji, p.Seconds);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] apply emote: {ex.Message}"); }
        }

        /// <summary>Round-119: am I the elected simulator for this building?  Used by the serve mirror to emit
        /// beats only from the machine actually running the customers.</summary>
        internal static bool IAmSimulatorFor(string addr)
            => !string.IsNullOrEmpty(addr) && _authority.TryGetValue(addr, out var s) && s == MPConfig.PlayerId;

        /// <summary>H-SALEHOLE-1 rig read-only: WHICH machine runs this address's live customers ("" = nobody
        /// inside, or no election yet). The `salestats` lever's `sim=` field.</summary>
        internal static string SimulatorFor(string addr)
            => !string.IsNullOrEmpty(addr) && _authority.TryGetValue(addr, out var sf) ? (sf ?? "") : "";

        /// <summary>H-SALEHOLE-1 rig read-only: is the native spawner suppressed HERE because this machine
        /// follows someone else's simulator in the building it stands in?</summary>
        internal static bool SpawnerSuppressedHere => _followerHere;

        /// <summary>H-SALEHOLE-1 rig read-only: live native customers in the interior this machine is in.</summary>
        internal static int LiveCustomerCount
        {
            get { try { return IndoorCustomerSpawner.Customers.Count; } catch { return -1; } }
        }

        /// <summary>Round-119: the wire id for a live customer — the SAME id the position stream uses, so a
        /// serve beat names the body the follower already has on screen.</summary>
        internal static string RowIdForCustomer(Customer c)
        {
            try { return c == null ? "" : RowIdOf(c, FindReg(_myBldg)); }
            catch { return ""; }
        }

        // ── Round-119: serve performance on a FOLLOWER's till ────────────────────────────────────────────
        // A player working a register is made the station's EMPLOYEE by the game (WorkActivity.StartWorking →
        // AssignEmployee(playerController.Character, ...)), so the ordinary employee serve loop runs on their
        // own body: fetch each item with a 2.5s animation, return, the customer animates, a gendered sound
        // plays.  On a follower none of that can happen — there are no real Customers, so no serve loop — and
        // they stand motionless behind a queue that is visibly being served (their till IS staffed by a
        // stand-in on the simulator's machine, and those customers stream back as puppets).  So we mirror the
        // real serve's START and FINISH and perform the middle locally.
        private static string _servingCustomerId = "";
        private static int    _servedThisSession;

        /// <summary>The rounded-position key of the till THIS player is personally working ("" if none).</summary>
        private static string MyDutyStationKey()
        {
            try { return MPRegisterSync.LocalPersonalDutyKey(); } catch { return ""; }
        }

        // ── Round-146 serve actor ─────────────────────────────────────────────────────────────
        // The simulator streams the stand-in's REAL serve events (RegisterServeMirror); this actor
        // performs them on the duty player's own body, strictly in order: each fetch walks to the
        // actual item position the stand-in grabbed, the end beat walks home and rings up, a
        // self-service start beat rings up in place for the native duration.  Replaces round-120's
        // synthesized single fetch trip (field 2026-07-28: the guess never fired — two ring-ups and
        // a wait — and a guess cannot match per-shop behaviour anyway).  Queue + single runner
        // because beats arrive on the wire while a previous walk is still playing; the native serve
        // is sequential and so is this.
        private enum ServeAct { Fetch, ReturnAndRingUp, RingUpOnly, ReturnOnly }
        private static readonly System.Collections.Generic.Queue<(ServeAct act, Vector3 pos, float dur)> _serveQueue = new();
        private static bool _serveActorRunning;
        private static Vector3 _serveHome;
        private static Vector3 _serveHomeFace;
        private static int _fetchesLeft = -1;     // round-151: expected grabs this serve (-1 = unknown)
        private static bool _returnQueued;        // round-151: home-return already enqueued this serve

        /// <summary>Round-150 — the nearest live item to a streamed position (the wire carries the item's
        /// CENTRE; the approach spot is resolved locally via the item's own native methods).</summary>
        private static ItemController NearestItem(Vector3 pos, float maxR)
        {
            ItemController best = null; float bestD2 = maxR * maxR;
            try
            {
                foreach (var ic in UnityEngine.Object.FindObjectsOfType<ItemController>())
                {
                    if (ic == null) continue;
                    float d2 = (ic.transform.position - pos).sqrMagnitude;
                    if (d2 < bestD2) { bestD2 = d2; best = ic; }
                }
            }
            catch { }
            return best;
        }

        /// <summary>Round-150 — home is the duty REGISTER's own employee position (the native
        /// GetEmployeePosition the real serve returns to), never "wherever I stood when the serve
        /// started" — that drifted and could even point at the wrong register (field 2026-07-28).</summary>
        private static bool TryGetMyDutyHome(out Vector3 home, out Vector3 face)
        {
            home = default; face = default;
            try
            {
                string mine = MyDutyStationKey();
                if (string.IsNullOrEmpty(mine)) return false;
                var parts = mine.Split(':');
                var approx = new Vector3(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
                EmployeeStationController best = null; float bestD2 = 4f;
                foreach (var o in UnityEngine.Object.FindObjectsOfType<EmployeeStationController>())
                {
                    if (o == null) continue;
                    float d2 = (o.transform.position - approx).sqrMagnitude;
                    if (d2 < bestD2) { bestD2 = d2; best = o; }
                }
                if (best == null) return false;
                home = best.GetEmployeePosition();
                face = best.transform.position;   // native return-walk looks at the station itself
                return true;
            }
            catch { return false; }
        }

        private static UnityEngine.Coroutine _serveActorHandle;
        private static float _actorHardDeadline;

        private static void EnqueueServeAct(ThirdPersonCharacter me, ServeAct act, Vector3 pos, float dur)
        {
            _serveQueue.Enqueue((act, pos, dur));
            // Round-149 watchdog: no single act may hold the runner beyond its hard deadline — a stuck
            // runner is stopped and a fresh one started (StopCoroutine skips finally, so the flag is
            // cleared by hand).  The round-146/147 failure mode — one frozen walk silencing the whole
            // performance forever — is structurally impossible now.
            if (_serveActorRunning && Time.unscaledTime > _actorHardDeadline)
            {
                try { if (_serveActorHandle != null) me.StopCoroutine(_serveActorHandle); } catch { }
                _serveActorRunning = false;
                Plugin.Logger.LogWarning("[ServeActor] watchdog: runner exceeded its deadline — restarted.");
            }
            if (!_serveActorRunning)
            {
                _serveActorRunning = true;
                _actorHardDeadline = Time.unscaledTime + 15f / MPRestSync.SkipPace;   // D-SKIPPACE-1
                _serveActorHandle = me.StartCoroutine(ServeActor(me));
            }
        }

        /// <summary>Round-147: every act is HANG-PROOF and EXCEPTION-PROOF.  Field 2026-07-28: routing all
        /// acts through walk-first coroutines silenced the whole performance ("client does nothing at all") —
        /// consistent with the walk never completing on a duty-locked body, which ALSO explains why round-120's
        /// fetch trip never fired while its directly-played animations did.  So: walks are driven manually with
        /// a deadline and a swallow, a failed walk degrades to performing the animation in place (the pre-146
        /// behaviour that field-proved visible), the run flag resets in finally, and the FIRST walk logs the
        /// agent's state so the blocker is named instead of guessed.</summary>

        private static System.Collections.IEnumerator BoundedWalk(ThirdPersonCharacter me, Vector3 pos, float arrive)
        {
            // Round-149: driving the NATIVE MoveToPosition proved UN-boundable — it yields a WaitUntil
            // whose predicate can never fire (its stuck-abort is parameter-gated), my coroutine blocked
            // on that single yield forever, and the deadline between yields never sampled again
            // (breadcrumbs: act BEGIN Fetch, healthy agent, no act END, no timeout — the frozen runner
            // silenced every animation queued behind it since round 146).  So the walk is now OURS:
            // set the destination, poll PER FRAME, deadline enforced every frame; if the body has not
            // displaced after ~1s the diag prints the agent's path truth (hasPath/status/velocity),
            // naming the mover-blocker instead of guessing.  A walk that cannot happen simply lapses
            // and the following animation plays in place — visible either way.
            var a = me.navmeshAgent;
            if (a == null || !a.isActiveAndEnabled) yield break;
            bool destOk = false;
            try { destOk = a.SetDestination(pos); } catch { yield break; }
            float pace     = MPRestSync.SkipPace;   // D-SKIPPACE-1: read ONCE for the whole walk, so its windows cannot drift mid-walk
            float deadline = Time.unscaledTime + 6f / pace;
            float diagAt   = Time.unscaledTime + 1f / pace;
            Vector3 startPos = me.transform.position;
            bool diagDone = false;
            bool reached = false;
            while (Time.unscaledTime < deadline)
            {
                bool arrived;
                try
                {
                    if (!a.isActiveAndEnabled) break;
                    arrived = !a.pathPending && a.hasPath && a.remainingDistance <= arrive + 0.05f;
                    if (!diagDone && Time.unscaledTime >= diagAt)
                    {
                        diagDone = true;
                        if ((me.transform.position - startPos).sqrMagnitude < 0.01f)
                            Plugin.Logger.LogInfo($"[ServeActor] walk diag (1s in, no displacement): setDest={destOk} "
                                + $"hasPath={a.hasPath} pathPending={a.pathPending} status={a.pathStatus} "
                                + $"remaining={(a.hasPath ? a.remainingDistance : -1f):F2} vel={a.velocity.magnitude:F2} "
                                + "— whatever is false/zero here is the mover-blocker.");
                    }
                }
                catch { break; }
                if (arrived) { reached = true; break; }
                yield return null;
            }
            // Round-152: the arrival radius is native (MoveToPosition's maxDistance) but natively it only
            // releases the WAIT — the agent keeps its path and glides to the exact spot.  Cancelling the
            // path here turned the wait-radius into a stop-radius, which is why the acting stopped short
            // of where the host stands.  So: leave an arrived agent's path alone (it finishes the last
            // 0.3m by itself); only a timed-out/aborted walk gets its path cleared.
            if (!reached)
                try { if (a.isActiveAndEnabled && a.hasPath) a.ResetPath(); } catch { }
        }

        private static System.Collections.IEnumerator PlayAnim(ThirdPersonCharacter me, AnimationType anim, float dur)
        {
            System.Collections.IEnumerator a = null;
            try { a = me.animator.RunAnimation(anim, dur); } catch { yield break; }
            if (a == null) yield break;
            while (true)
            {
                bool step;
                try { step = a.MoveNext(); } catch { yield break; }
                if (!step) yield break;
                yield return a.Current;
            }
        }

        /// <summary>Round-153: wait for the arrival glide to settle, THEN turn.  A tween fired while the
        /// agent is still steering fights its rotation and lands on a wrong facing — native rotates only
        /// after velocity reaches zero (MoveToPosition's rotate branch).</summary>
        private static System.Collections.IEnumerator SettleThenFace(ThirdPersonCharacter me, Vector3 face)
        {
            float end = Time.unscaledTime + 0.8f / MPRestSync.SkipPace;   // D-SKIPPACE-1: a faster body settles sooner
            while (Time.unscaledTime < end)
            {
                bool moving = false;
                try { var a = me.navmeshAgent; moving = a != null && a.isActiveAndEnabled && a.velocity.sqrMagnitude > 0.0025f; } catch { }
                if (!moving) break;
                yield return null;
            }
            try { me.transform.DOLookAt(new Vector3(face.x, me.transform.position.y, face.z), 0.25f); } catch { }
        }

        private static System.Collections.IEnumerator ServeActor(ThirdPersonCharacter me)
        {
            try
            {
                while (_serveQueue.Count > 0)
                {
                    var (act, pos, dur) = _serveQueue.Dequeue();
                    // Round-148: the user reports NOTHING visible while 'served 1' still logs — so either the
                    // actor stalls between acts or every act runs invisibly.  Breadcrumbs for the first serve
                    // separate those two worlds; retire with the serve-mirror probes.
                    _actorHardDeadline = Time.unscaledTime + 15f / MPRestSync.SkipPace;   // round-149: per-act watchdog window (D-SKIPPACE-1: scales with the performance)
                    switch (act)
                    {
                        case ServeAct.Fetch:
                        {
                            // Round-150: the wire carries the item's CENTRE; walking at a centre lands on
                            // whatever side pathing reaches first (field: "accesses the rear of the
                            // machine").  Resolve the native approach spot on OUR copy of the item — the
                            // same two calls the native GrabItem makes — and face the item on arrival.
                            Vector3 walkTo = pos, face = pos;
                            try
                            {
                                var ic = NearestItem(pos, 2.5f);
                                if (ic != null)
                                {
                                    face = ic.transform.position;
                                    var v = ic.GetClosestNavMeshTargetPosition(me.transform.position);
                                    if (v == Vector3.zero) v = ic.GetNavMeshTargetPosition();
                                    if (v != Vector3.zero) walkTo = v;
                                }
                            }
                            catch { }
                            yield return BoundedWalk(me, walkTo, 0.3f);
                            yield return SettleThenFace(me, face);
                            yield return PlayAnim(me, AnimationType.UsingProducer, 2.5f);
                            // Round-151: that was the last expected grab → head home NOW (the native serve
                            // returns before the customer pays; waiting for the finish beat was one full
                            // return-walk late and the customer left first).
                            if (_fetchesLeft > 0 && --_fetchesLeft == 0 && !_returnQueued)
                            {
                                _returnQueued = true;
                                _serveQueue.Enqueue((ServeAct.ReturnAndRingUp, Vector3.zero, 0f));
                            }
                            // Round-153: the entry count is only the FAST path — the native serve SKIPS
                            // unfindable items, so grabs can run short of the count and the return then
                            // waited for the finish beat again (field: "delay reintroduced").  If no
                            // further beat arrives within a grace second, this grab was the last: go home.
                            if (_serveQueue.Count == 0 && !_returnQueued)
                            {
                                float graceEnd = Time.unscaledTime + 1.0f / MPRestSync.SkipPace;   // D-SKIPPACE-1
                                while (Time.unscaledTime < graceEnd && _serveQueue.Count == 0) yield return null;
                                if (_serveQueue.Count == 0 && !_returnQueued)
                                {
                                    _returnQueued = true;
                                    _serveQueue.Enqueue((ServeAct.ReturnAndRingUp, Vector3.zero, 0f));
                                }
                            }
                            break;
                        }
                        case ServeAct.ReturnAndRingUp:
                            yield return BoundedWalk(me, _serveHome, 0.25f);
                            yield return SettleThenFace(me, _serveHomeFace);
                            yield return PlayAnim(me, AnimationType.UsingCashRegister, 2f);
                            break;
                        case ServeAct.ReturnOnly:
                            yield return BoundedWalk(me, _serveHome, 0.25f);
                            yield return SettleThenFace(me, _serveHomeFace);
                            break;
                        case ServeAct.RingUpOnly:
                            yield return PlayAnim(me, AnimationType.UsingCashRegister, dur);
                            break;
                    }
                }
            }
            finally { _serveActorRunning = false; }
        }

        /// <summary>Round-119/146: a serve event at some till in my building.  If it is the till I am
        /// working, perform it.  Beats for any other till are ignored — the simulator broadcasts per
        /// building, and only the player standing at that specific counter should act it out.</summary>
        public static void ApplyServe(RegisterServePayload p)
        {
            try
            {
                if (p == null || p.SimulatorPid == MPConfig.PlayerId) return;
                if (p.AddressKey != _myBldg || !_followerHere) return;

                string mine = MyDutyStationKey();
                if (string.IsNullOrEmpty(mine) || mine != p.StationKey) return;   // not my counter

                var me = Helpers.PlayerHelper.PlayerController?.Character;
                if (me == null) return;

                switch (p.Kind)
                {
                    case 0:   // serve start
                        _servingCustomerId = p.CustomerId ?? "";
                        // Round-150: home = MY register's native employee position (never current position).
                        if (!TryGetMyDutyHome(out _serveHome, out _serveHomeFace))
                        { _serveHome = me.transform.position; _serveHomeFace = _serveHome; }
                        // Round-151: return home on our own after this many grabs — native cadence.
                        _fetchesLeft = (!p.SelfService && p.Entries > 0) ? p.Entries : -1;
                        _returnQueued = false;
                        try
                        {
                            if (!string.IsNullOrEmpty(_servingCustomerId)
                                && _puppets.TryGetValue(_servingCustomerId, out var cp) && cp.go != null)
                            {
                                var look = cp.go.transform.position; look.y = me.transform.position.y;
                                me.transform.DOLookAt(look, 0.2f);
                            }
                        }
                        catch { }
                        // A self-service till rings up in place for the native duration — that IS the serve.
                        if (p.SelfService) EnqueueServeAct(me, ServeAct.RingUpOnly, Vector3.zero, p.Dur > 0f ? p.Dur : 3f);
                        break;

                    case 1:   // fetch one item — the REAL position the stand-in walked to
                        EnqueueServeAct(me, ServeAct.Fetch, new Vector3(p.FX, p.FY, p.FZ), 0f);
                        break;

                    case 2:   // serve end (or cancel)
                        if (p.Cancelled)
                        {
                            _serveQueue.Clear();
                            EnqueueServeAct(me, ServeAct.ReturnOnly, Vector3.zero, 0f);
                            _servingCustomerId = "";
                            return;
                        }
                        // Round-151: the return normally already happened after the last grab; this is
                        // only the safety net for serves whose grab count never arrived or fell short.
                        if (!_returnQueued) EnqueueServeAct(me, ServeAct.ReturnAndRingUp, Vector3.zero, 0f);
                        _returnQueued = true; _fetchesLeft = -1;
                        // The customer's own beat + the gendered interaction sound the simulator played.
                        if (!string.IsNullOrEmpty(p.CustomerId) && _puppets.TryGetValue(p.CustomerId, out var pup) && pup.tpc != null)
                        {
                            // H-PUPPETANIM-1: the customer's own UsingProducer beat is no longer started here - the simulator's
                            // RunAnimationLength reaches the copy through the one-shot event funnel (one source); the sound stays.
                            try
                            {
                                InstanceBehavior<SfxManager>.Instance.PlayAudio(
                                    p.Male ? SoundType.NpcMaleProducerInteraction : SoundType.NpcFemaleProducerInteraction,
                                    pup.go != null ? pup.go.transform.position : me.transform.position);
                            }
                            catch { }
                        }
                        _servingCustomerId = "";
                        _servedThisSession++;
                        if (_servedThisSession == 1 || _servedThisSession % 10 == 0)
                            Plugin.Logger.LogInfo($"[Customers] served {_servedThisSession} customer(s) at my till in '{p.AddressKey}' (mirrored performance).");
                        break;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] apply serve: {ex.Message}"); }
        }

        /// <summary>Simulator side (called by the ShowExpression patch): stream a customer's emoji.
        /// (emote-chain probes retired 2026-07-07 — pipeline + visuals field-confirmed.)</summary>
        internal static void OnLocalCustomerEmote(ThirdPersonCharacter tpc, int emoji, float seconds)
        {
            try
            {
                if (string.IsNullOrEmpty(_myBldg)) return;
                if (!_authority.TryGetValue(_myBldg, out var sim) || sim != MPConfig.PlayerId) return;
                var cust = tpc != null ? tpc.GetComponentInParent<Customer>() : null;
                if (cust == null) return;
                var p = new CustomerPuppetEmotePayload
                {
                    AddressKey = _myBldg, SimulatorPid = MPConfig.PlayerId,
                    CustomerId = RowIdOf(cust, FindReg(_myBldg)), Emoji = emoji, Seconds = seconds,
                };
                if (MPServer.IsRunning) MPServer.BroadcastCustomerEmote(p);
                else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.CustomerPuppetEmote, MPConfig.PlayerId, p));
            }
            catch { }
        }

        /// <summary>Round-44 — a customer's look arrived: dress the puppet (or hold until it spawns).</summary>
        public static void ApplyLook(CustomerPuppetLookPayload p)
        {
            try
            {
                if (p == null || p.SimulatorPid == MPConfig.PlayerId || string.IsNullOrEmpty(p.CustomerId)) return;
                _looksById[p.CustomerId] = p;   // round-44c: cache always — rebuilt puppets re-dress from here
                if (p.AddressKey != _myBldg || !_followerHere) return;
                if (_puppets.TryGetValue(p.CustomerId, out var pup))
                {
                    pup.look = p;
                    ApplyLookTo(pup.tpc, p);
                }
            }
            catch { }
        }

        /// <summary>Round-44b — play an emoji on a puppet DIRECTLY via the emoji system. The native
        /// ShowExpression wrapper NREs on puppets (its internal coroutine-runner is Start-assigned and
        /// Start never runs on the disabled TPC — field-proven stack at ShowExpression MoveNext 0xa9);
        /// the head bone is prefab-serialized, so the static ShowEmoji path has everything it needs.</summary>
        private static void PlayEmoteOn(Puppet pup, int emoji, float seconds)
        {
            try
            {
                if (pup.tpc == null || pup.tpc.head == null) return;
                pup.tpc.StartCoroutine(Characters.EmojiSystem.CharacterEmojiSystem.ShowEmoji(
                    pup.tpc.head, (CharacterEmojiName)emoji, showText: true, seconds <= 0f ? 3f : seconds));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] emote play: {ex.Message}"); }
        }

        /// <summary>Round-44b — structured appearance capture (raw CharacterData JSON died silently on
        /// its embedded ItemInstance graph).</summary>
        private static CustomerPuppetLookPayload? CaptureLook(ThirdPersonCharacter? tpc)
        {
            try
            {
                var d = tpc?.appearanceSetter?.data;
                if (d == null) return null;
                var p = new CustomerPuppetLookPayload
                {
                    Gender      = (int)d.gender,
                    Strength    = d.strength,
                    Fatness     = d.fatness,
                    ColorPacked = (d.color.r << 24) | (d.color.g << 16) | (d.color.b << 8) | d.color.a,
                    EyesPacked  = (d.eyesColor.r << 24) | (d.eyesColor.g << 16) | (d.eyesColor.b << 8) | d.eyesColor.a,
                };
                if (d.elements != null)
                    foreach (var e in d.elements)
                        if (e != null) p.Elements.Add(new LookElementInfo { Type = (int)e.type, VariantId = e.variantId ?? "", ColorId = e.colorId ?? "" });
                if (d.blendshapes != null)
                    foreach (var b in d.blendshapes)
                        if (b != null) p.Blends.Add(new LookBlendInfo { Name = b.name ?? "", Value = b.value });
                return p.Elements.Count > 0 ? p : null;   // no elements = nothing to dress with
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] look capture: {ex.Message}"); return null; }
        }

        // ── Follower: render the stream ──────────────────────────────────────────────────────────────
        public static void ApplyState(CustomerPuppetStatePayload p)
        {
            try
            {
                if (p == null || p.SimulatorPid == MPConfig.PlayerId) return;   // my own echo
                if (p.AddressKey != _myBldg || !_followerHere) return;          // not my room / I'm native
                // H-PUPPETSTUTTER-1: the sender's clock stamp (an old sender sends none - stamp it on arrival).
                float arrive = Time.unscaledTime;
                float senderT = p.T > 0f ? p.T : arrive;
                string simKey = p.SimulatorPid ?? "";
                try
                {
                    float off = arrive - senderT;
                    if (!_clockOffsetBySim.TryGetValue(simKey, out var ce) || ce == null) { ce = new ClockEst { off = off }; _clockOffsetBySim[simKey] = ce; }
                    else if (off <= ce.off) { ce.off = off; ce.highSince = -1f; }                  // fold P1: the low end, at once
                    else if (off - ce.off > 1f)                                                    // a big step up: re-seed only if it holds
                    {
                        if (ce.highSince < 0f) { ce.highSince = arrive; ce.highMin = off; }
                        else
                        {
                            ce.highMin = Mathf.Min(ce.highMin, off);
                            if (arrive - ce.highSince >= ClockReseedAfter) { ce.off = ce.highMin; ce.highSince = -1f; _offReseeds++; }
                        }
                    }
                    else { ce.off = Mathf.Lerp(ce.off, off, ClockOffsetRise); ce.highSince = -1f; }  // slowly up
                    ce.jit = Mathf.Lerp(ce.jit, Mathf.Clamp(off - ce.off, 0f, JitterCap), JitterBlend);
                    _offLast = ce.off; _jitLast = ce.jit; _offExcessSum += off - ce.off; _offBatches++;
                }
                catch { }
                var seen = new HashSet<string>();
                foreach (var r in p.Rows)
                {
                    if (r == null || string.IsNullOrEmpty(r.Id)) continue;
                    seen.Add(r.Id);
                    if (!_puppets.TryGetValue(r.Id, out var pup))
                    {
                        pup = SpawnPuppet(new Vector3(r.X, r.Y, r.Z), r.Yaw);
                        if (pup == null) continue;
                        _puppetSpawns++;
                        _puppets[r.Id] = pup;
                        // Round-43: replay an emote that arrived before this body existed (complaints
                        // fire at the customer's spawn instant).
                        if (_pendingEmotes.TryGetValue(r.Id, out var pe))
                        {
                            _pendingEmotes.Remove(r.Id);
                            if (Time.unscaledTime < pe.expires) PlayEmoteOn(pup, pe.emoji, pe.seconds);
                        }
                        // Round-44c: dress the body from the session look cache (survives rebuilds).
                        if (_looksById.TryGetValue(r.Id, out var lk))
                        {
                            pup.look = lk;
                            ApplyLookTo(pup.tpc, lk);
                        }
                    }
                    var newTarget = new Vector3(r.X, r.Y, r.Z);
                    // D (fold 3): PUPPET LAG - how far this body still is from the target that has just
                    // arrived for it. One vector subtract on a row we were already applying.
                    try
                    {
                        if (pup.go != null)
                        {
                            var lagV = newTarget - pup.go.transform.position;
                            lagV.y = 0f;
                            ShopDayMeter.NoteLag(lagV.magnitude);
                        }
                    }
                    catch { }
                    pup.target   = newTarget;   // the NEWEST sample - only the walk-out reads it (and swaps in the exit); the lag meter uses newTarget, an adoption the DRAWN body position
                    pup.yaw      = r.Yaw;
                    pup.lastSeen = Time.unscaledTime;
                    PushSample(pup, simKey, senderT, newTarget, r.Yaw, r.Fwd, r.Loops, r.Dance, r.ActItem ?? "");   // fold A1: activity too
                    if (pup.leaving) pup.leaving = false;   // simulator says they're still here
                    if (pup.held != (r.Held ?? "")) UpdateHeld(pup, r.Held ?? "");
                    if (pup.fill != r.Fill) ApplyFill(pup, r.Fill);
                }
                // Rows that vanished = customers who left/were served away → walk out.
                foreach (var kv in _puppets)
                    if (!seen.Contains(kv.Key) && !kv.Value.leaving) { _leaveMissing++; _leaveMissingTotal++; StartLeaving(kv.Value); }
                // H-PUPPETANIM-1: the customers' one-shots since the previous batch, in send order.
                if (p.Ev != null && p.Ev.Count > 0) QueueEvents(p.Ev, senderT, arrive);   // fold A1: played when the copy's render time reaches T
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] apply puppets: {ex.Message}"); }
        }

        private static Puppet? SpawnPuppet(Vector3 pos, float yaw)
        {
            try
            {
                if (Helpers.PlayerHelper.PlayerController == null) return null;   // round-188: never materialize into an unready scene (stream re-delivers)
                var tpc = PrefabHelper.CreatePrefab<ThirdPersonCharacter>("Characters/HumanDefinitionLow", null);
                if (tpc == null) return null;
                var go = tpc.gameObject;
                go.name = "BAMP_CustomerPuppet";
                go.SetActive(true);
                go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
                try
                {
                    var gender = UnityEngine.Random.value < 0.5f
                        ? BigAmbitions.Characters.Gender.Male
                        : BigAmbitions.Characters.Gender.Female;
                    tpc.appearanceSetter.SetRandomAppearance(gender,
                        new[] { BigAmbitions.Characters.Appearance.AppearanceTag.Casual });
                }
                catch { }
                // Kinematic mannequin: no pathfinding, no physics pushback (round-13 avatar-shove class),
                // no click surface — the stream is the only thing that moves it.
                try { foreach (var a in go.GetComponentsInChildren<NavMeshAgent>(true)) a.enabled = false; } catch { }
                try { foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) rb.isKinematic = true; } catch { }
                try { foreach (var col in go.GetComponentsInChildren<Collider>(true)) col.enabled = false; } catch { }
                // Round-42 (field: puppets slid instead of walking): ThirdPersonCharacter.Update drives
                // the locomotion float itself — animator.SetFloat(BaseHuman.Forward, 0) every frame when
                // ITS movement logic sees no input — overwriting ours. The component's logic is dead
                // weight on a stream-driven body; disable it so our Forward value sticks. (Plain method
                // calls on the disabled component — SetHandContent, ShowExpression — still work.)
                tpc.enabled = false;
                return new Puppet { go = go, tpc = tpc, anim = tpc.animator, lastSeen = Time.unscaledTime, target = pos, yaw = yaw };
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] puppet spawn: {ex.Message}"); return null; }
        }

        private static int _puppetSpawns, _puppetLeaves, _leaveMissing, _leaveStale;
        private static float _nextChurnAt;

        // BATCH-26 FOLD 3 (D): the four counters above are zeroed by ChurnTick every 10 s and were
        // never printed anywhere, so nothing could ever be read off them. These are the same events
        // counted CUMULATIVELY for the measured window - zeroed only by `shopday reset` - and the
        // `shopday` lever prints them. Nothing new is computed: they are incremented beside the
        // existing bumps.
        private static int _leaveStaleTotal, _puppetLeavesTotal, _leaveMissingTotal, _puppetSpawnsTotal;
        internal static int LeaveStaleTotal    { get { return _leaveStaleTotal; } }
        internal static int PuppetLeavesTotal  { get { return _puppetLeavesTotal; } }
        internal static int LeaveMissingTotal  { get { return _leaveMissingTotal; } }
        internal static int PuppetSpawnsTotal  { get { return _puppetSpawnsTotal; } }
        internal static void ResetChurnTotals()
        {
            _leaveStaleTotal = _puppetLeavesTotal = _leaveMissingTotal = _puppetSpawnsTotal = 0;
        }

        /// <summary>Round-131: one line every 10s on the FOLLOWER comparing bodies created against bodies sent
        /// away, split by cause.  A healthy shop churns very little; spawns ≈ leaves every few seconds means the
        /// simulator's row ids are not stable, not that customers are misbehaving.</summary>
        private static void ChurnTick()
        {
            if (Time.unscaledTime < _nextChurnAt) return;
            _nextChurnAt = Time.unscaledTime + 10f;
            if (_puppetSpawns == 0 && _puppetLeaves == 0) return;
            // (10s churn summary retired round-158 — churn was field-explained: pooled bodies + normal turnover.)
            _puppetSpawns = _puppetLeaves = _leaveMissing = _leaveStale = 0;
        }

        private static void StartLeaving(Puppet pup)
        {
            _puppetLeaves++; _puppetLeavesTotal++;
            pup.leaving = true;
            pup.bn = 0;   // fold P4: if its rows resume, it starts a fresh buffer - not a straight line from a stale sample
            pup.actT = float.MinValue;
            float now = Time.unscaledTime;
            pup.leaveAt = now + 6f;   // hard stop even if no exit is reachable
            try { ApplyActivity(pup, 0L, 0f, ""); } catch { }   // H-PUPPETANIM-1: a copy walking out stands up and lets go of its machine
            try
            {
                var zones = InstanceBehavior<BuildingManager>.Instance?.exitZones;
                if (zones != null && zones.Count > 0 && zones[0] != null)
                {
                    pup.target = zones[0].transform.position;
                    // fold P2: long enough to reach the exit at plain walking speed (a skip only shortens it), capped
                    if (pup.go != null)
                    {
                        var to = pup.target - pup.go.transform.position; to.y = 0f;
                        float walk = Mathf.Max(0.5f, LeaveWalkSpeed() / Mathf.Max(1f, MPRestSync.SkipPace));
                        pup.leaveAt = now + Mathf.Clamp(to.magnitude / walk + LeaveMarginSeconds, 6f, LeaveCapSeconds);
                    }
                }
            }
            catch { }
        }

        /// <summary>Fold D1 (manager decision 2026-09-26, run T-HANDOFF2-20260926-221137 leg 3): a simulator whose game
        /// is closing stops streaming positions many seconds before its disconnect is seen (15 s there), and every copy
        /// walked out on staleness first - "0 adopted via no-final". A copy whose simulator is STILL CONNECTED and sent a
        /// visit row (stream or Final) for it within StaleHoldSeconds stays in place. The hold ends when (a) that
        /// simulator disconnects - HandoffWaitTick holds every copy and the take-over adopts from the stream rows;
        /// (b) its position rows resume (lastSeen fresh again); (c) authority moves elsewhere; or (d) StaleHoldSeconds
        /// pass without a row - the ordinary stale walk-out.</summary>
        private const float StaleHoldSeconds = 30f;
        private static bool StaleHoldApplies(string id, float now)
        {
            try
            {
                if (!_followerHere || string.IsNullOrEmpty(_myBldg) || string.IsNullOrEmpty(id)) return false;
                if (!_authority.TryGetValue(_myBldg, out var sim) || string.IsNullOrEmpty(sim) || sim == MPConfig.PlayerId) return false;   // (c)
                if (!CustomerHandoff.InLobby(sim)) return false;                                                                             // (a)
                float age = CustomerHandoff.RowAgeFrom(id, sim);
                if (age > StaleHoldSeconds) return false;                                                                                     // (d)
                if (_staleHoldLoggedFor != _myBldg)
                {
                    _staleHoldLoggedFor = _myBldg;
                    Plugin.Logger.LogInfo($"[Handoff] holding stale cop(ies) in place in '{_myBldg}' - simulator '{sim}' still connected, visit row {age:0.0} s old (until it disconnects, its rows resume, authority moves, or {StaleHoldSeconds:0} s without a row).");
                }
                return true;
            }
            catch { return false; }
        }

        private static void UpdatePuppets()
        {
            if (_puppets.Count == 0) { if (_pendingEv.Count > 0) _pendingEv.Clear(); return; }
            var dead = new List<string>();
            float now = Time.unscaledTime;
            foreach (var kv in _puppets)
            {
                var pup = kv.Value;
                if (pup.go == null) { dead.Add(kv.Key); continue; }
                // Stale stream (simulator disconnect / hitch) → walk out rather than freeze mid-stride.
                // Fold D1: not while the simulator is still connected and sent a visit row for this copy lately.
                if (!pup.leaving && now - pup.lastSeen > 2.5f && now > pup.holdUntil && !StaleHoldApplies(kv.Key, now)) { _leaveStale++; _leaveStaleTotal++; StartLeaving(pup); }

                var tr = pup.go.transform;
                if (!pup.leaving)
                {
                    // H-PUPPETSTUTTER-1: drawn from the sample buffer at a fixed delay - no chase, so no speed pulse.
                    float bf = 0f;
                    try { bf = RenderBuffered(pup, tr, now); } catch { }
                    try { DriveLocomotion(pup, bf); } catch { }
                    continue;
                }
                // Leaving copies walk to the exit at a constant walking speed (the pre-fix chase, minus its pulse).
                Vector3 to = pup.target - tr.position;
                to.y = 0f;
                float dist = to.magnitude;
                float speed = LeaveWalkSpeed();
                if (dist > 0.02f)
                {
                    tr.position = Vector3.MoveTowards(tr.position, pup.target, speed * Time.deltaTime);
                    tr.rotation = Quaternion.Slerp(tr.rotation, Quaternion.LookRotation(to.normalized), 10f * Time.deltaTime);
                }
                else
                {
                    tr.rotation = Quaternion.Slerp(tr.rotation, Quaternion.Euler(0f, pup.yaw, 0f), 10f * Time.deltaTime);
                    speed = 0f;
                }
                try { DriveLocomotion(pup, speed); } catch { }

                if (dist < 0.6f || now > pup.leaveAt) dead.Add(kv.Key);
            }
            try { PlayDueEvents(now); } catch { }   // fold A1: after every copy's render time is set this frame
            foreach (var k in dead)
            {
                if (_puppets.TryGetValue(k, out var pup))
                {
                    try { ApplyActivity(pup, 0L, 0f, ""); } catch { }   // H-PUPPETANIM-1
                    try { if (pup.heldGo != null) UnityEngine.Object.Destroy(pup.heldGo); } catch { }
                    try { if (pup.go != null) UnityEngine.Object.Destroy(pup.go); } catch { }
                    _puppets.Remove(k);
                }
            }
        }

        /// <summary>H-PUPPETSTUTTER-1: add one stream sample to a copy's BufSamples-sample buffer. A new simulator, a clock that
        /// went back more than 1 s, or a jump over SnapMetres restarts the buffer (a jump also snaps the body).</summary>
        private static void PushSample(Puppet pup, string sim, float t, Vector3 pos, float yaw, float fwd, long loops, float dance, string act)
        {
            try
            {
                if (pup.bsim != sim) { pup.bn = 0; pup.bsim = sim; }
                if (pup.bn > 0)
                {
                    int last = pup.bn - 1;
                    if (t < pup.bt[last] - 1f) pup.bn = 0;                 // the sender's clock restarted
                    else if (t <= pup.bt[last] + 0.0001f) return;          // duplicate / out of order
                    else
                    {
                        var j = pos - pup.bp[last]; j.y = 0f;
                        if (j.magnitude > SnapMetres) { pup.bn = 0; pup.snapNext = true; }
                    }
                }
                if (pup.bn == BufSamples)
                {
                    for (int i = 0; i < BufSamples - 1; i++)
                    {
                        pup.bt[i] = pup.bt[i + 1]; pup.bp[i] = pup.bp[i + 1]; pup.byaw[i] = pup.byaw[i + 1]; pup.bfwd[i] = pup.bfwd[i + 1];
                        pup.bloops[i] = pup.bloops[i + 1]; pup.bdance[i] = pup.bdance[i + 1]; pup.bact[i] = pup.bact[i + 1];
                    }
                    pup.bn = BufSamples - 1;
                }
                pup.bt[pup.bn] = t; pup.bp[pup.bn] = pos; pup.byaw[pup.bn] = yaw; pup.bfwd[pup.bn] = fwd;
                pup.bloops[pup.bn] = loops; pup.bdance[pup.bn] = dance; pup.bact[pup.bn] = act ?? "";
                pup.bn++;
            }
            catch { }
        }

        /// <summary>H-PUPPETSTUTTER-1: place a (not leaving) copy at render time rt = now - offset - delay (fold P3: delay eases
        /// toward 1.5 x the smallest gap between the buffered samples):
        /// straight-line between the two samples around rt, held at the newest past it (no extrapolation), at the
        /// oldest before it. Faces the motion while the reported Forward is above 0.01, else turns to the reported
        /// yaw. Returns the Forward to animate with.</summary>
        private static float RenderBuffered(Puppet pup, Transform tr, float now)
        {
            int n = pup.bn;
            if (n <= 0)
            {
                tr.rotation = Quaternion.Slerp(tr.rotation, Quaternion.Euler(0f, pup.yaw, 0f), 10f * Time.deltaTime);
                return 0f;
            }
            float off = 0f;
            if (_clockOffsetBySim.TryGetValue(pup.bsim ?? "", out var ce) && ce != null) off = ce.off + ce.jit;
            // fold P3: the spacing of the samples actually received (the smallest gap - one late batch does not stretch it)
            float gap = StreamInterval;
            if (n >= 2)
            {
                gap = float.MaxValue;
                for (int k = 1; k < n; k++) { float g = pup.bt[k] - pup.bt[k - 1]; if (g < gap) gap = g; }
                gap = Mathf.Clamp(gap, 0.02f, 0.5f);
            }
            float wantDelay = RenderDelayGaps * gap;
            pup.delay = pup.delay <= 0f ? wantDelay : Mathf.MoveTowards(pup.delay, wantDelay, RenderDelayEase * Time.unscaledDeltaTime);
            _delayLast = pup.delay;
            float rt = now - off - pup.delay;
            pup.rt = rt;
            int last = n - 1;
            // fold A1: the activity of the newest sample at or before rt (the oldest while rt is before them all), applied
            // once per sample - a copy sits, dances or mounts a machine when it is DRAWN at that spot.
            int ai = 0;
            for (int k = last; k > 0; k--) if (pup.bt[k] <= rt) { ai = k; break; }
            if (pup.bt[ai] != pup.actT)
            {
                pup.actT = pup.bt[ai];
                try { ApplyActivity(pup, pup.bloops[ai], pup.bdance[ai], pup.bact[ai] ?? ""); } catch { }
            }
            Vector3 pos, seg = Vector3.zero;
            float yaw, fwd;
            if (rt >= pup.bt[last])
            {
                if (n >= 2) _rbNewest++;
                pos = pup.bp[last]; yaw = pup.byaw[last];
                // a late batch: keep the stride for up to two gaps rather than stop the walk animation
                fwd = (rt - pup.bt[last] <= 2f * gap) ? pup.bfwd[last] : 0f;
                if (last > 0) seg = pup.bp[last] - pup.bp[last - 1];
            }
            else if (rt <= pup.bt[0])
            {
                if (n >= 2) _rbOldest++;
                pos = pup.bp[0]; yaw = pup.byaw[0]; fwd = pup.bfwd[0];
                if (n > 1) seg = pup.bp[1] - pup.bp[0];
            }
            else
            {
                _rbBetween++;
                int i = 0;
                while (i < last - 1 && rt >= pup.bt[i + 1]) i++;
                float span = pup.bt[i + 1] - pup.bt[i];
                float u = span > 0.0001f ? Mathf.Clamp01((rt - pup.bt[i]) / span) : 1f;
                pos = Vector3.Lerp(pup.bp[i], pup.bp[i + 1], u);
                yaw = Mathf.LerpAngle(pup.byaw[i], pup.byaw[i + 1], u);
                fwd = Mathf.Lerp(pup.bfwd[i], pup.bfwd[i + 1], u);
                seg = pup.bp[i + 1] - pup.bp[i];
            }
            Vector3 d = pos - tr.position;
            if (pup.snapNext || d.magnitude > SnapMetres) { tr.position = pos; pup.snapNext = false; }
            else if (d.sqrMagnitude > 1e-8f)
                tr.position = Vector3.MoveTowards(tr.position, pos, CatchUpSpeed * Mathf.Max(1f, MPRestSync.SkipPace) * Time.deltaTime);
            seg.y = 0f;
            Quaternion want = (fwd > 0.01f && seg.sqrMagnitude > 0.0001f)
                ? Quaternion.LookRotation(seg.normalized)
                : Quaternion.Euler(0f, yaw, 0f);
            tr.rotation = Quaternion.Slerp(tr.rotation, want, 10f * Time.deltaTime);
            return fwd;
        }

        /// <summary>H-PUPPETSTUTTER-1: a leaving copy's constant walk - GlobalReferences.walkingSpeedWalk (x the shop's
        /// skip pace while a skip runs, as the natives are paced).</summary>
        private static float LeaveWalkSpeed()
        {
            float w = 1.5f;
            try { var gr = InstanceBehavior<GlobalReferences>.Instance; if (gr != null && gr.walkingSpeedWalk > 0.05f) w = gr.walkingSpeedWalk; } catch { }
            return w * Mathf.Max(1f, MPRestSync.SkipPace);
        }

        /// <summary>H-PUPPETSTUTTER-1: simulator side of the flip counter - one native's IsMoving at this stream sample.</summary>
        private static void NoteSimMoving(string rid, bool moving)
        {
            try
            {
                if (_flipsSince < 0f) _flipsSince = Time.unscaledTime;
                _simSamples++;
                if (moving) _simMovingSamples++;
                if (_simMoving.TryGetValue(rid, out var was) && was && !moving) _flipsSim++;
                _simMovingNext[rid] = moving;
            }
            catch { }
        }

        internal static void ResetFlips()
        {
            _flipsWatch = _flipsSim = _simSamples = _simMovingSamples = _watchFrames = _watchMovingFrames = 0;
            _offBatches = _offReseeds = _rbBetween = _rbOldest = _rbNewest = 0;
            _offExcessSum = 0f;
            _flipsSince = Time.unscaledTime;
        }

        /// <summary>DEV lever `puppetflips`: both counters per minute since the last reset. `vs` = the other machine's
        /// simulator rate: within = this watcher's rate is inside 1.5x of it either way.</summary>
        internal static string FlipsLine(float vsSimPerMin = -1f)
        {
            float secs = _flipsSince < 0f ? 0f : Time.unscaledTime - _flipsSince;
            float mins = Mathf.Max(secs / 60f, 0.001f);
            float simPm = _flipsSim / mins, watchPm = _flipsWatch / mins;
            string line = $"secs={secs:0.0} sim={_flipsSim} simPerMin={simPm:0.0} simSamples={_simSamples} simMoving={_simMovingSamples} "
                + $"watch={_flipsWatch} watchPerMin={watchPm:0.0} watchFrames={_watchFrames} watchMovingFrames={_watchMovingFrames} "
                + $"copies={_puppets.Count} follower={_followerHere} bldg='{_myBldg}' "
                + $"off={_offLast:0.000} jit={_jitLast:0.000} offExcessAvg={(_offBatches > 0 ? _offExcessSum / _offBatches : 0f):0.000} offBatches={_offBatches} offReseeds={_offReseeds} delay={_delayLast:0.000} "
                + $"renderBetween={_rbBetween} pinnedOldest={_rbOldest} pinnedOldestPerMin={_rbOldest / mins:0.0} heldNewest={_rbNewest} heldNewestPerMin={_rbNewest / mins:0.0}";
            if (vsSimPerMin >= 0f)
            {
                float ratio = vsSimPerMin > 0.001f ? watchPm / vsSimPerMin : (watchPm > 0.001f ? 999f : 1f);
                bool within = ratio <= 1.5f && ratio >= 1f / 1.5f;
                line += $" vs={vsSimPerMin:0.0} ratio={ratio:0.00} within={within}";
            }
            return line;
        }

        private static bool _rigDiagLogged;

        /// <summary>1.0 port fix (2026-08-29): drive the puppet's animator the way 1.0 drives its own
        /// characters. 0.11 walked a humanoid off ONE signal — the Forward float — and that is all this
        /// code used to send. 1.0 rebuilt the drive around THREE (ThirdPersonCharacter.cs:387-445):
        /// an IsMoving bool that gates the idle→walk transition (NEW — BaseHuman.cs:42), the surviving
        /// Forward float smoothed at ForwardTransitionSpeed, and a MotionTime walk-cycle clock the
        /// SCRIPT winds forward every frame (skipped for controllers without the parameter — native
        /// caches HasParameter per controller; mirrored per puppet). Setting only Forward still
        /// compiled and still ran — the controller just never left idle, which is exactly the
        /// frozen-glider field report (F-2026-08-29-BQ). Walking/animation speeds are read LIVE from
        /// GlobalReferences, the same source the native properties wrap.</summary>
        private static void DriveLocomotion(Puppet pup, float speed)
        {
            var anim = pup.anim;
            if (anim == null) return;
            // Review S2 (2026-08-29): the THIRD native signal — BoredAnimations (idle fidgets) is
            // toggled by the locomotion itself (ThirdPersonCharacter.cs:389-399: on while standing,
            // off while walking; Start() disables it initially). Puppets never run Start (tpc is
            // disabled before its first frame), so the component keeps its unknown PREFAB state
            // forever unless driven. State-driven every frame here → the prefab's baked state is
            // irrelevant, and idle puppets fidget exactly like native customers. Purely local —
            // nothing travels.
            var bored = pup.tpc != null ? pup.tpc.boredAnimations : null;
            try
            {
                if (_flipsSince < 0f) _flipsSince = Time.unscaledTime;
                _watchFrames++;
                if (speed > 0.01f) _watchMovingFrames++;
                else if (anim.GetBool(BaseHuman.IsMoving)) _flipsWatch++;   // H-PUPPETSTUTTER-1: this copy stops walking
            }
            catch { }
            if (speed <= 0.01f)
            {
                anim.SetBool(BaseHuman.IsMoving, false);
                bool wantBored = pup.loops == 0L;   // H-PUPPETANIM-1: no idle fidgets while a looping activity shows
                if (bored != null && bored.enabled != wantBored) bored.enabled = wantBored;
                return;
            }
            if (bored != null && bored.enabled) bored.enabled = false;

            float fwd = Mathf.Min(speed, 6f);
            if (anim.GetBool(BaseHuman.IsMoving))
                fwd = Mathf.MoveTowards(anim.GetFloat(BaseHuman.Forward), fwd, BaseHuman.ForwardTransitionSpeed * Time.deltaTime);
            anim.SetFloat(BaseHuman.Forward, fwd);
            anim.SetBool(BaseHuman.IsMoving, true);

            if (!ReferenceEquals(pup.motionTimeCheckedOn, anim.runtimeAnimatorController))
            {
                pup.motionTimeCheckedOn = anim.runtimeAnimatorController;
                pup.hasMotionTime = false;
                try
                {
                    foreach (var prm in anim.parameters)
                        if (prm.nameHash == BaseHuman.MotionTime) { pup.hasMotionTime = true; break; }
                }
                catch { }
                // P-PUPPET-RIG: one line per follow session. If puppets ever freeze again, this says
                // whether the rig even carries the script-wound walk clock — evidence with the report.
                if (!_rigDiagLogged)
                {
                    _rigDiagLogged = true;
                    // Review S2: also report the fidget component — its prefab-baked state was
                    // undecidable from the decompile; this line settles it per rig, in the field.
                    string boredNote = "no BoredAnimations";
                    try
                    {
                        var b = pup.tpc != null ? pup.tpc.boredAnimations : null;
                        if (b != null) boredNote = $"BoredAnimations prefab-state={(b.enabled ? "ENABLED" : "disabled")} (now driven)";
                    }
                    catch { }
                    Plugin.Logger.LogInfo($"[Customers] puppet rig: MotionTime "
                        + (pup.hasMotionTime ? "PRESENT (walk cycle is script-wound)" : "ABSENT (controller runs its own cycle)")
                        + $" — controller '{pup.motionTimeCheckedOn?.name}'; {boredNote}.");
                }
            }
            if (pup.hasMotionTime)
            {
                var gr = InstanceBehavior<GlobalReferences>.Instance;
                if (gr == null) return;
                float animSpeed;
                if (fwd < gr.walkingSpeedWalkFast)
                    animSpeed = Mathf.Lerp(gr.animationSpeedWalk, gr.animationSpeedWalkFast,
                                           Mathf.InverseLerp(gr.walkingSpeedWalk, gr.walkingSpeedWalkFast, fwd));
                else if (fwd < gr.walkingSpeedJog)
                    animSpeed = Mathf.Lerp(gr.animationSpeedWalkFast, gr.animationSpeedJog,
                                           Mathf.InverseLerp(gr.walkingSpeedWalkFast, gr.walkingSpeedJog, fwd));
                else
                    animSpeed = Mathf.Lerp(gr.animationSpeedJog, gr.animationSpeedRun,
                                           Mathf.InverseLerp(gr.walkingSpeedJog, gr.walkingSpeedRun, fwd));
                anim.SetFloat(BaseHuman.MotionTime, anim.GetFloat(BaseHuman.MotionTime) + Time.deltaTime * animSpeed);
            }
        }

        /// <summary>Round-42 — mirror the customer's hand prop (basket/box) via the carry-template pool.</summary>
        private static void UpdateHeld(Puppet pup, string held)
        {
            try
            {
                pup.held = held;
                pup.fill = -1;   // fresh prop — fill re-applies on the next row
                if (pup.heldGo != null) { try { UnityEngine.Object.Destroy(pup.heldGo); } catch { } pup.heldGo = null; }
                if (pup.tpc == null) return;
                if (string.IsNullOrEmpty(held)) { try { pup.tpc.SetHandContent(null); } catch { } return; }
                var clone = RemotePlayerManager.CloneHeldPropFor(held);
                if (clone == null) return;
                pup.heldGo = clone;
                // Round-214: native colors a pulled basket by copying the RACK's custom
                // colors (Customer.SetBasket → SetCustomColors(rack.customColors), one
                // frame deferred); our template clone skipped that line, so mirrored
                // shoppers carried the default blue in a shop with a recolored rack
                // (rig 2026-08-01: owner and the puller saw brown, only puppets blue).
                // Same recipe, same source: the rack in THIS shop.
                if (held.IndexOf("ShoppingBasket", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try
                    {
                        var rack = InstanceBehavior<BuildingManager>.Instance?.allItemControllers?
                            .FirstOrDefault(x => x != null && x.itemName == "ba:itemname_stackofshoppingbaskets");
                        var ic = clone.GetComponent<ItemController>();
                        // (Native defers this a frame because ITS basket is freshly built;
                        // our clone comes from a fully-instantiated template — direct is safe.)
                        if (rack != null && ic != null && rack.customColors != null && rack.customColors.Count > 0)
                            ic.SetCustomColors(rack.customColors);
                    }
                    catch { }
                }
                // SetHandContent requires an ItemController on the prop and derefs its members
                // (TogglePhysics, Item.playerMountPosition, IK points) — keep the LOUD catch permanently:
                // a silent throw here cost three diagnosis rounds (2026-07-07).
                try
                {
                    pup.tpc.SetHandContent(clone.transform);   // native parenting + mount offsets + holding anim
                }
                catch (Exception shx)
                {
                    Plugin.Logger.LogWarning($"[Customers] SetHandContent('{held}') threw {shx.GetType().Name}: {shx.Message} — manual hand-parent fallback.");
                    ManualHandParent(pup, clone);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] UpdateHeld('{held}'): {ex.Message}"); }
        }

        /// <summary>Round-45e — fallback attach: parent the prop to the puppet's HandContent bone
        /// directly (identity local pose) when the native SetHandContent path refuses.</summary>
        private static void ManualHandParent(Puppet pup, GameObject clone)
        {
            try
            {
                if (pup.go == null) return;
                Transform? hand = null;
                foreach (var tr in pup.go.GetComponentsInChildren<Transform>(true))
                    if (tr != null && tr.name == "HandContent") { hand = tr; break; }
                if (hand == null) return;
                clone.transform.SetParent(hand, false);
                // Round-45f: the item's own mount offsets (what native SetHandContent applies) — identity
                // pose left the bag floating in front of the body.
                Vector3 mountPos = Vector3.zero; Vector3 mountRot = Vector3.zero;
                try
                {
                    var ic = clone.GetComponent<ItemController>();
                    var item = ic != null ? BigAmbitions.Items.ItemsGetter.GetByName(ic.itemName) : null;
                    if (item != null) { mountPos = item.playerMountPosition; mountRot = item.playerMountRotation; }
                }
                catch { }
                clone.transform.localPosition = mountPos;
                clone.transform.localEulerAngles = mountRot;
                try { pup.anim?.SetBool(BaseHuman.HoldingBox, true); } catch { }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] manual hand-parent: {ex.Message}"); }
        }

        /// <summary>Round-45b — mirror basket fill ([PropProbe]-taught structure): toggle ONLY the
        /// "Products*" children; everything else on the prop (IK attach, shadow caster) stays untouched.</summary>
        private static void ApplyFill(Puppet pup, int fill)
        {
            try
            {
                pup.fill = fill;
                if (pup.heldGo == null) return;
                var t = pup.heldGo.transform;
                int seen = 0;
                for (int i = 0; i < t.childCount; i++)
                {
                    var ch = t.GetChild(i);
                    if (ch == null || !ch.name.StartsWith("Products", StringComparison.Ordinal)) continue;
                    ch.gameObject.SetActive(seen < fill);
                    seen++;
                }
            }
            catch { }
        }

        // ── H-PUPPETANIM-1 (batch 27): customers' activities on the watcher ───────────────────────────────
        // A watcher's copy used to get IsMoving / Forward / MotionTime / HoldingBox only. Now:
        //   * LOOPING states - every PermanentAnimationType SetBool (CharacterAnimations.cs:93-97: seats, eating, gym
        //     machines ...) plus Dancing / TypeOfDance and isHoldingAJacket (BaseHuman.cs:52-56) - travel as a bit mask
        //     per row (PuppetRowInfo.Loops/Dance), read LIVE off the real body's animator on the simulator;
        //   * ONE-SHOTS - every Animator.SetTrigger(int) on a simulated customer (RunAnimationLength ends in it too,
        //     :52-58) - travel as events (payload Ev) with the body's AnimationSpeed;
        //   * the workout MACHINE's own animator flag (treadmill belt ...) follows PuppetRowInfo.ActItem on the watcher.
        private const int DanceBit = 62, JacketBit = 61, MaxEnumBit = 60, MaxEvPerBatch = 32;
        private static readonly int DancingHash     = Animator.StringToHash("Dancing");
        private static readonly int TypeOfDanceHash = Animator.StringToHash("TypeOfDance");
        private static readonly int JacketHash      = Animator.StringToHash("isHoldingAJacket");
        private static readonly int AnimSpeedHash   = Animator.StringToHash("AnimationSpeed");
        private static readonly int BoredHash       = Animator.StringToHash("Bored");   // BoredAnimations.cs:7 - copies fidget locally (DriveLocomotion), never twice
        private static readonly int SittingBit = (int)PermanentAnimationType.Sitting;
        private static readonly int EatSitBit  = (int)PermanentAnimationType.ConsumingFoodSitting;
        private static (int bit, int hash, string name)[]? _loopDefs;
        private static int _loopEnumCount, _loopUnmapped;
        // per animator CONTROLLER: its parameters (hash -> type), and which loop bits it carries as bools
        private static readonly Dictionary<RuntimeAnimatorController, Dictionary<int, AnimatorControllerParameterType>> _ctrlParams = new();
        private static readonly Dictionary<RuntimeAnimatorController, (int bit, int hash)[]> _ctrlLoops = new();
        // simulator: native customer animator -> row id, rebuilt every stream tick, EMPTY when this machine simulates nothing
        private static readonly Dictionary<Animator, string> _simAnimRow = new();
        private static List<PuppetEventInfo> _simEv = new();
        // any machine: body animator instance id -> the workout machine's ItemInstance.id (Start/StopWorkoutAnimation postfixes)
        private static readonly Dictionary<int, string> _bodyMachine = new();
        private static HarmonyLib.AccessTools.FieldRef<global::PlayerActivity.WorkoutAnimatorController, BaseHuman>? _wacHuman;
        private static HarmonyLib.AccessTools.FieldRef<global::PlayerActivity.WorkoutAnimatorController, global::Controllers.IWorkoutMachine>? _wacMachine;
        private static bool _wacTried;
        private static int _simEvQueued, _simEvDropped, _evPlayed, _evNoParam, _missingHits;
        private static readonly HashSet<string> _missingNames = new();
        private static string _actsLoggedKey = "", _actsCtrl = "";
        private static int _actsK = -1;
        private static int _actsArm = int.MinValue;   // puppetacts arm: n > 0 = loopers >= n; -1 = a seated eater; MinValue = off

        private static void ResetActs()
        {
            _simAnimRow.Clear();
            _simEv.Clear();
            _bodyMachine.Clear();
            _missingNames.Clear();
            _simEvQueued = _simEvDropped = _evPlayed = _evNoParam = _missingHits = 0;
            _actsLoggedKey = ""; _actsCtrl = ""; _actsK = -1;
            _actsArm = int.MinValue;
        }

        private static void ClearSimAnim()
        {
            if (_simAnimRow.Count > 0) _simAnimRow.Clear();
            if (_simEv.Count > 0) _simEv.Clear();
        }

        /// <summary>Every PermanentAnimationType (bit = its value; CharacterAnimations hashes value.ToString()), then Dancing
        /// and isHoldingAJacket. Built once.</summary>
        private static (int bit, int hash, string name)[] LoopDefs()
        {
            if (_loopDefs != null) return _loopDefs;
            var l = new List<(int bit, int hash, string name)>();
            int n = 0, un = 0;
            try
            {
                foreach (PermanentAnimationType v in Enum.GetValues(typeof(PermanentAnimationType)))
                {
                    int bit = Convert.ToInt32(v);
                    n++;
                    if (bit < 0 || bit > MaxEnumBit) { un++; continue; }
                    string nm = v.ToString();
                    l.Add((bit, Animator.StringToHash(nm), nm));
                }
            }
            catch { }
            l.Add((DanceBit, DancingHash, "Dancing"));
            l.Add((JacketBit, JacketHash, "HoldingJacket"));
            _loopEnumCount = n; _loopUnmapped = un;
            _loopDefs = l.ToArray();
            return _loopDefs;
        }

        /// <summary>The controller's parameters, cached per controller (an animator that is not initialised yet reports
        /// none - that answer is not cached).</summary>
        private static Dictionary<int, AnimatorControllerParameterType>? ParamsOf(Animator an)
        {
            var ctl = an.runtimeAnimatorController;
            if (ctl == null) return null;
            if (_ctrlParams.TryGetValue(ctl, out var d)) return d;
            d = new Dictionary<int, AnimatorControllerParameterType>();
            try { foreach (var prm in an.parameters) d[prm.nameHash] = prm.type; } catch { }
            if (d.Count > 0) _ctrlParams[ctl] = d;
            return d;
        }

        private static bool HasParam(Animator an, int hash, AnimatorControllerParameterType type)
        {
            var d = ParamsOf(an);
            return d != null && d.TryGetValue(hash, out var ty) && ty == type;
        }

        private static (int bit, int hash)[] LoopParamsOf(Animator an)
        {
            var ctl = an.runtimeAnimatorController;
            if (ctl == null) return Array.Empty<(int, int)>();
            if (_ctrlLoops.TryGetValue(ctl, out var arr)) return arr;
            var d = ParamsOf(an);
            if (d == null || d.Count == 0) return Array.Empty<(int, int)>();
            var l = new List<(int bit, int hash)>();
            foreach (var def in LoopDefs())
                if (d.TryGetValue(def.hash, out var ty) && ty == AnimatorControllerParameterType.Bool) l.Add((def.bit, def.hash));
            arr = l.ToArray();
            _ctrlLoops[ctl] = arr;
            return arr;
        }

        /// <summary>The looping activity bits that are ON on this animator right now (live read).</summary>
        private static long LoopsOf(Animator an)
        {
            long m = 0L;
            var arr = LoopParamsOf(an);
            for (int i = 0; i < arr.Length; i++)
                if (an.GetBool(arr[i].hash)) m |= 1L << arr[i].bit;
            return m;
        }

        private static bool IsSitEat(long m)
        {
            if (SittingBit < 0 || SittingBit > MaxEnumBit || EatSitBit < 0 || EatSitBit > MaxEnumBit) return false;
            long want = (1L << SittingBit) | (1L << EatSitBit);
            return (m & want) == want;
        }

        private static string LoopNames(long m)
        {
            if (m == 0L) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var def in LoopDefs())
                if ((m & (1L << def.bit)) != 0L) { if (sb.Length > 0) sb.Append('+'); sb.Append(def.name); }
            return sb.ToString();
        }

        /// <summary>Simulator (the SetTrigger(int) postfix): queue a simulated customer's one-shot for the next batch.
        /// O(1); a single Count compare when this machine simulates no customers.</summary>
        internal static void NoteSimTrigger(Animator anim, int hash)
        {
            try
            {
                if (_simAnimRow.Count == 0) return;
                if (hash == BoredHash) return;   // idle fidgets: every copy runs its own (DriveLocomotion)
                if (!_simAnimRow.TryGetValue(anim, out var rid)) return;
                if (_simEv.Count >= MaxEvPerBatch) { _simEvDropped++; return; }
                float s = 1f;
                if (HasParam(anim, AnimSpeedHash, AnimatorControllerParameterType.Float)) s = anim.GetFloat(AnimSpeedHash);
                _simEv.Add(new PuppetEventInfo { Id = rid, H = hash, S = s });
                _simEvQueued++;
            }
            catch { }
        }

        /// <summary>Start/StopWorkoutAnimation postfixes: which workout machine (ItemInstance.id) a body is on.</summary>
        internal static void NoteWorkout(global::PlayerActivity.WorkoutAnimatorController w, bool on)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                if (!_wacTried)
                {
                    _wacTried = true;
                    try { _wacHuman = HarmonyLib.AccessTools.FieldRefAccess<global::PlayerActivity.WorkoutAnimatorController, BaseHuman>("_baseHuman"); } catch { }
                    try { _wacMachine = HarmonyLib.AccessTools.FieldRefAccess<global::PlayerActivity.WorkoutAnimatorController, global::Controllers.IWorkoutMachine>("_workoutMachine"); } catch { }
                }
                if (_wacHuman == null || w == null) return;
                var bh = _wacHuman(w);
                if (bh == null || bh.animator == null) return;
                int key = bh.animator.GetInstanceID();
                if (!on) { _bodyMachine.Remove(key); return; }
                var ic = _wacMachine != null ? _wacMachine(w) as ItemController : null;
                string id = ic != null && ic.ItemInstance != null ? (ic.ItemInstance.id ?? "") : "";
                if (id.Length > 0) _bodyMachine[key] = id; else _bodyMachine.Remove(key);
            }
            catch { }
        }

        /// <summary>Watcher: make a copy SHOW the given loops / dance / machine. Only changed bits are written; a loop
        /// whose parameter the copy's controller lacks is counted (puppetacts missing=). Loops off = fidgets back
        /// (DriveLocomotion) and the paced speed restored; ("", 0) is also the release on leave / removal.</summary>
        private static void ApplyActivity(Puppet pup, long loops, float dance, string act)
        {
            // fold A4 (review 2026-09-27): the machine first - a copy without an animator still lets go of its machine
            if (act != pup.act)
            {
                if (pup.act.Length > 0) MachineUse(pup.act, false);
                pup.act = act;
                if (act.Length > 0) MachineUse(act, true);
            }
            var an = pup.anim;
            if (an == null) return;
            if (!pup.actsChecked) pup.actsChecked = LogActsParams(an);
            if (loops != pup.loops)
            {
                long diff = loops ^ pup.loops;
                var d = ParamsOf(an);
                // fold A2: an animator that reports no parameters yet commits nothing - pup.loops stays, the next sample retries
                if (d != null && d.Count > 0)
                {
                    foreach (var def in LoopDefs())
                    {
                        long bit = 1L << def.bit;
                        if ((diff & bit) == 0L) continue;
                        bool on = (loops & bit) != 0L;
                        if (!d.TryGetValue(def.hash, out var ty) || ty != AnimatorControllerParameterType.Bool)
                        {
                            if (on) { _missingHits++; _missingNames.Add(def.name); }
                            continue;
                        }
                        an.SetBool(def.hash, on);
                    }
                    pup.loops = loops;
                }
            }
            if ((loops & (1L << DanceBit)) != 0L && Mathf.Abs(dance - pup.dance) > 0.0001f)
            {
                pup.dance = dance;
                if (HasParam(an, TypeOfDanceHash, AnimatorControllerParameterType.Float)) an.SetFloat(TypeOfDanceHash, dance);
            }
            // User-approved 2026-09-22: looping activity animations follow the skip pace - the same Animator.speed knob
            // SkipPaceBodies puts on the simulator's natives (idempotent; RestoreAll puts it back when the skip ends).
            if (loops != 0L) { if (MPRestSync.SkipPace > 1.001f) SkipPaceBodies.ApplyAnimator(an); }
            else if (SkipPaceBodies.IsAnimatorScaled(an)) SkipPaceBodies.RestoreAnimatorOne(an);
        }

        /// <summary>Fold A3 (review 2026-09-27): copies using each workout machine - its flag goes on with the first user and
        /// off with the last, so one copy stepping off and another stepping on (any order, any frame) leave it on.</summary>
        private static readonly Dictionary<string, int> _machineUsers = new();
        private static void MachineUse(string itemId, bool on)
        {
            _machineUsers.TryGetValue(itemId, out int n);
            if (on) { _machineUsers[itemId] = n + 1; if (n == 0) SetMachineFlag(itemId, true); }
            else if (n <= 1) { _machineUsers.Remove(itemId); SetMachineFlag(itemId, false); }
            else _machineUsers[itemId] = n - 1;
        }

        /// <summary>The workout machine's OWN animator flag (WorkoutAnimatorController.StartWorkoutAnimation sets it on the
        /// simulator): found by ItemInstance.id among this interior's item controllers.</summary>
        private static void SetMachineFlag(string itemId, bool on)
        {
            try
            {
                var bm = InstanceBehavior<BuildingManager>.Instance;
                if (bm == null || bm.allItemControllers == null || string.IsNullOrEmpty(itemId)) return;
                foreach (var ic in bm.allItemControllers)
                {
                    if (ic == null || ic.ItemInstance == null || ic.ItemInstance.id != itemId) continue;
                    if (ic is global::Controllers.IWorkoutMachine wm)
                    {
                        var ma = wm.GetAnimator();
                        var ex = wm.GetWorkoutExercise();
                        if (ma != null && ex != null && !string.IsNullOrEmpty(ex.animationOnTheWorkoutMachineBoolName))
                            ma.SetBool(ex.animationOnTheWorkoutMachineBoolName, on);
                    }
                    return;
                }
            }
            catch { }
        }

        /// <summary>Fold A1 (review 2026-09-27): a batch's one-shots wait, stamped with its sender time T, until the copy's
        /// render time reaches T (PlayDueEvents, each frame) - they fire where the copy is DRAWN, not ~1.5 intervals
        /// early. A copy that is gone, leaving or not buffering, or a wait over EvMaxWait real seconds, plays/drops at
        /// once as before. Send order is kept (a copy's later events carry later T).</summary>
        private struct PendingEv { public PuppetEventInfo e; public float t; public float arrive; }
        private static readonly List<PendingEv> _pendingEv = new();
        private const int MaxPendingEv = 256;
        private const float EvMaxWait = 2f;
        private static void QueueEvents(List<PuppetEventInfo> evs, float t, float arrive)
        {
            for (int i = 0; i < evs.Count; i++)
            {
                if (evs[i] == null) continue;
                if (_pendingEv.Count >= MaxPendingEv) { PlayOne(evs[i]); continue; }
                _pendingEv.Add(new PendingEv { e = evs[i], t = t, arrive = arrive });
            }
        }

        private static void PlayDueEvents(float now)
        {
            if (_pendingEv.Count == 0) return;
            int w = 0;
            for (int i = 0; i < _pendingEv.Count; i++)
            {
                var pe = _pendingEv[i];
                bool wait = pe.e != null && !string.IsNullOrEmpty(pe.e.Id) && now - pe.arrive < EvMaxWait
                    && _puppets.TryGetValue(pe.e.Id, out var pup) && !pup.leaving && pup.bn > 0 && pup.rt < pe.t;
                if (wait) _pendingEv[w++] = pe;
                else PlayOne(pe.e);
            }
            if (w < _pendingEv.Count) _pendingEv.RemoveRange(w, _pendingEv.Count - w);
        }

        /// <summary>Watcher: replay one of the simulator's one-shots on its copy - AnimationSpeed first, then the trigger,
        /// only when the copy's controller has that trigger.</summary>
        private static void PlayOne(PuppetEventInfo? e)
        {
            try
            {
                if (e == null || string.IsNullOrEmpty(e.Id)) return;
                if (!_puppets.TryGetValue(e.Id, out var pup) || pup.leaving || pup.anim == null) return;
                var an = pup.anim;
                if (!HasParam(an, e.H, AnimatorControllerParameterType.Trigger)) { _evNoParam++; return; }
                if (HasParam(an, AnimSpeedHash, AnimatorControllerParameterType.Float)) an.SetFloat(AnimSpeedHash, e.S > 0f ? e.S : 1f);
                an.SetTrigger(e.H);
                _evPlayed++;
            }
            catch { }
        }

        /// <summary>One line per follow session: how many looping activity parameters the copy's controller carries.
        /// False = the animator reported no parameters yet (try again on the next row).</summary>
        private static bool LogActsParams(Animator an)
        {
            try
            {
                var d = ParamsOf(an);
                if (d == null || d.Count == 0) return false;
                string cn = an.runtimeAnimatorController != null ? an.runtimeAnimatorController.name : "(none)";
                string key = _myBldg + "|" + cn;
                if (key == _actsLoggedKey) return true;
                int k = 0;
                var miss = new List<string>();
                foreach (var def in LoopDefs())
                {
                    if (def.bit > MaxEnumBit) continue;
                    if (d.TryGetValue(def.hash, out var ty) && ty == AnimatorControllerParameterType.Bool) k++;
                    else miss.Add(def.name);
                }
                _actsLoggedKey = key; _actsCtrl = cn; _actsK = k;
                Plugin.Logger.LogInfo($"[Customers] puppet activity params: {k} of {_loopEnumCount} present on '{cn}'"
                    + $" (Dancing={HasParam(an, DancingHash, AnimatorControllerParameterType.Bool)}, isHoldingAJacket={HasParam(an, JacketHash, AnimatorControllerParameterType.Bool)},"
                    + $" AnimationSpeed={HasParam(an, AnimSpeedHash, AnimatorControllerParameterType.Float)}, unmapped={_loopUnmapped}; missing: {(miss.Count == 0 ? "none" : string.Join(",", miss))}).");
                return true;
            }
            catch { return true; }
        }

        private static void ActsArmCheck(int loopers, int sitEat)
        {
            if (_actsArm == int.MinValue) return;
            if (_actsArm > 0 && loopers >= _actsArm)
                Plugin.Logger.LogInfo($"[PuppetActs] armed count reached: {loopers} >= {_actsArm} customer(s) with a looping activity in '{_myBldg}'.");
            else if (_actsArm == -1 && sitEat >= 1)
                Plugin.Logger.LogInfo($"[PuppetActs] armed seated eater reached: {sitEat} in '{_myBldg}'.");
            else return;
            _actsArm = int.MinValue;
        }

        /// <summary>DEV lever `puppetacts arm n|eat`.</summary>
        internal static string ArmActs(string a)
        {
            if (a == "eat") { _actsArm = -1; return $"OK puppetacts armed eat bldg='{_myBldg}'"; }
            if (int.TryParse(a, out int n) && n > 0) { _actsArm = n; return $"OK puppetacts armed n={n} bldg='{_myBldg}'"; }
            if (a == "off") { _actsArm = int.MinValue; return "OK puppetacts disarmed"; }
            return "ERR usage: puppetacts arm <n>|eat|off";
        }

        /// <summary>DEV lever `puppetacts [vs sig]`: per id the loop names ON right now - the simulator reads its natives'
        /// animators, the watcher its copies' animators (live, so it shows what is really displayed).</summary>
        internal static string ActsLine(string? vs)
        {
            string mode = _followerHere ? "follower" : (IAmSimulatorFor(_myBldg) ? "native" : "none");
            int rows = 0, loopers = 0, sitEat = 0, onMachine = 0, k = -1;
            string ctrl = "";
            var names = new System.Text.StringBuilder();
            var sig = new System.Text.StringBuilder();
            var shown = new Dictionary<string, long>();
            void Add(string id, long m)
            {
                shown[id] = m;
                if (m == 0L) return;
                loopers++;
                if (IsSitEat(m)) sitEat++;
                if (names.Length > 0) names.Append(';');
                names.Append(id).Append(':').Append(LoopNames(m));
                if (sig.Length > 0) sig.Append(',');
                sig.Append(id).Append(':').Append(m.ToString("x"));
            }
            int Kof(Animator an)
            {
                int kk = 0;
                var arr = LoopParamsOf(an);
                for (int i = 0; i < arr.Length; i++) if (arr[i].bit <= MaxEnumBit) kk++;
                return kk;
            }
            if (mode == "native")
            {
                var reg = FindReg(_myBldg);
                foreach (var c in IndoorCustomerSpawner.Customers)
                {
                    if (c == null || c.tpc == null || c.tpc.animator == null) continue;
                    var an = c.tpc.animator;
                    rows++;
                    if (ctrl.Length == 0 && an.runtimeAnimatorController != null) { ctrl = an.runtimeAnimatorController.name; k = Kof(an); }
                    long m = LoopsOf(an);
                    if (m != 0L && _bodyMachine.ContainsKey(an.GetInstanceID())) onMachine++;
                    Add(RowIdOf(c, reg), m);
                }
            }
            else if (mode == "follower")
            {
                foreach (var kv in _puppets)
                {
                    var pup = kv.Value;
                    if (pup == null || pup.leaving || pup.anim == null) continue;
                    rows++;
                    if (ctrl.Length == 0 && pup.anim.runtimeAnimatorController != null) { ctrl = pup.anim.runtimeAnimatorController.name; k = Kof(pup.anim); }
                    if (pup.act.Length > 0) onMachine++;
                    Add(kv.Key, LoopsOf(pup.anim));
                }
            }
            LoopDefs();
            string head = $"bldg='{_myBldg}' mode={mode} rows={rows} loopers={loopers} sitEat={sitEat} onMachine={onMachine} "
                        + $"params={k}/{_loopEnumCount} ctrl='{ctrl}' loggedK={_actsK} missing={_missingNames.Count} missingHits={_missingHits} "
                        + $"missingNames=[{string.Join(",", _missingNames)}] evQueued={_simEvQueued} evDropped={_simEvDropped} evPlayed={_evPlayed} evNoParam={_evNoParam}";
            if (vs != null)
            {
                int total = 0, matched = 0, absent = 0, simSE = 0, seMatched = 0;
                var mism = new System.Text.StringBuilder();
                foreach (var part in vs.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int colon = part.LastIndexOf(':');
                    if (colon <= 0) continue;
                    string id = part.Substring(0, colon);
                    if (!long.TryParse(part.Substring(colon + 1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out long sm) || sm == 0L) continue;
                    total++;
                    bool se = IsSitEat(sm);
                    if (se) simSE++;
                    if (!shown.TryGetValue(id, out long wm)) { absent++; mism.Append(id).Append(":absent;"); continue; }
                    if ((wm & sm) == sm) { matched++; if (se) seMatched++; }
                    else mism.Append(id).Append(":lacks=").Append(LoopNames(sm & ~wm)).Append(';');
                }
                float ratio = total > 0 ? (float)matched / total : 0f;
                head += $" simLoopers={total} matched={matched} absent={absent} ratio={ratio.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} "
                      + $"within={(total > 0 && ratio >= 0.8f)} simSitEat={simSE} sitEatMatched={seMatched} mismatch=[{mism}]";
            }
            return head + $" names=[{names}] sig={sig}";
        }


        private static void DestroyAllPuppets()
        {
            foreach (var kv in _puppets)
            {
                try { ApplyActivity(kv.Value, 0L, 0f, ""); } catch { }   // H-PUPPETANIM-1
                try { if (kv.Value.heldGo != null) UnityEngine.Object.Destroy(kv.Value.heldGo); } catch { }
                try { if (kv.Value.go != null) UnityEngine.Object.Destroy(kv.Value.go); } catch { }
            }
            _puppets.Clear();
            _machineUsers.Clear();   // fold A3
            _pendingEv.Clear();      // fold A1
            _handNodes.Clear();
            _custEntryIds.Clear();
            _pendingEmotes.Clear();
            _warpHolds.Clear();
            _actsLoggedKey = "";   // H-PUPPETANIM-1: the K-of-N parameter line is once per follow session
            // _looksById deliberately NOT cleared here — this runs on every building exit, and the whole
            // point of the cache is dressing REBUILT puppets on re-entry. Session Reset clears it.
        }
    }

    /// <summary>Round-42 — emoji/complaint parity for followers: every customer expression on the
    /// SIMULATING machine streams to the puppets (Customer.Init complaints, no-paperbag reactions, …).
    /// ShowExpression is the single native funnel for customer emoji bubbles.</summary>
    [HarmonyLib.HarmonyPatch(typeof(ThirdPersonCharacter), nameof(ThirdPersonCharacter.ShowExpression))]
    public static class Patch_ShowExpression_PuppetMirror
    {
        static void Postfix(ThirdPersonCharacter __instance, CharacterEmojiName characterEmojiName, float secondsToShow)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                // ROUND-136 (verify "give-ups are native demand misses"): the emoji IS the reason a customer
                // gives up — CustomerCantFindItem / CustomerTooHighPrice / CustomerNoPaperbags etc. — and this
                // is the single funnel it passes through.  One log line per expression pairs each [LeaveProbe]
                // give-up with its named cause; if give-ups appear WITHOUT a matching complaint here, they are
                // NOT demand misses and the theory is refuted.
                CustomerPuppets.OnLocalCustomerEmote(__instance, (int)characterEmojiName, secondsToShow);
            }
            catch { }
        }
    }

    /// <summary>H-PUPPETANIM-1: which workout machine a body is on (PuppetRowInfo.ActItem) - the simulator reads it per
    /// row; WorkoutAnimatorController.StartWorkoutAnimation / StopWorkoutAnimation are the two native edges.</summary>
    [HarmonyLib.HarmonyPatch(typeof(global::PlayerActivity.WorkoutAnimatorController), nameof(global::PlayerActivity.WorkoutAnimatorController.StartWorkoutAnimation))]
    public static class Patch_WorkoutStart_PuppetAct
    {
        static void Postfix(global::PlayerActivity.WorkoutAnimatorController __instance)
        {
            try { CustomerPuppets.NoteWorkout(__instance, true); } catch { }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(global::PlayerActivity.WorkoutAnimatorController), nameof(global::PlayerActivity.WorkoutAnimatorController.StopWorkoutAnimation))]
    public static class Patch_WorkoutStop_PuppetAct
    {
        static void Postfix(global::PlayerActivity.WorkoutAnimatorController __instance)
        {
            try { CustomerPuppets.NoteWorkout(__instance, false); } catch { }
        }
    }
}
