using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Shared time-speed management for the multiplayer session.
    ///
    /// Responsible for two things:
    ///   1. Speed/pause sync — monitors Time.timeScale every frame and flags local
    ///      changes so MPCanvasUI can broadcast them immediately to all peers.
    ///      Applies incoming network speed changes without causing a re-broadcast loop.
    ///
    ///   2. Drift thresholds — constants used by clock-alignment logic so the policy
    ///      lives in one place.
    ///
    /// The guarantee: when every peer has the same Time.timeScale they advance game time
    /// at the same wall-clock rate, so clocks stay naturally in sync.  System 1 handles
    /// pause/speed changes and System 2 (periodic 3-second broadcast) mops up any
    /// floating-point accumulation.
    /// </summary>
    public static class TimeSync
    {
        // ── timeScale application ─────────────────────────────────────────────

        /// <summary>Applies a Time.timeScale value (network sync / startup hold).</summary>
        private static float _lastLoggedScale = -999f;
        public static void ApplyNetwork(float scale)
        {
            Time.timeScale = scale;
            // Log only on an ACTUAL speed change. The unchanged 1.00× reprint every ~3s was ~2,100
            // lines/session of pure noise; the diagnostic value is the transitions (pause/skip/catch-up).
            if (scale != _lastLoggedScale)
            {
                _lastLoggedScale = scale;
                string label = scale == 0f ? " (PAUSED)" : $" ({scale:F2}×)";
                Plugin.Logger.LogInfo($"[TimeSync] Network speed applied:{label}");
            }
        }

        // ── Drift alignment thresholds ────────────────────────────────────────

        /// <summary>Drifts below this (game-hours) are within tolerance — no correction.</summary>
        public const float DRIFT_IGNORE_HOURS = 0.05f;   // 3 game-minutes (user-set 2026-06-18)

        // (DRIFT_SNAP_HOURS removed 2026-06-18: no hard clock-snap anymore. Beyond the dead-band we correct by
        //  RUNNING time — speed up when behind, freeze when ahead — never by writing the clock. The continuous
        //  correction caps drift, so it never accumulates to a size that would have needed a snap.)


        // ── Startup pause hold ────────────────────────────────────────────────
        //
        // When a multiplayer game starts, every player is held at timeScale 0
        // until ALL players have confirmed their scene finished loading.  This
        // prevents the faster-loading player's game clock from advancing while
        // the others are still on the loading screen.  Released automatically
        // by the host once everyone is in-game — no player interaction needed.

        private static bool _startupHold;

        // Round-18 ("client stuck paused after menu→reload"): the client's hold begins only when ITS loading
        // overlay clears (TickOverlayFreezeGate), but the host's release can arrive EARLIER — e.g. the fence
        // excused this client off a stale "Menu" phase report during the menu detour, or the host simply
        // finished first on a fast reload. EndStartupHold's not-held early-return silently swallowed that
        // release; the hold then began and froze forever (nothing left to release it). Remember an early
        // release and skip the pending hold.
        // Round-36 RE-OCCURRENCE (log-proven order): release arrives (marker set) → SCENE-READY reset
        // CLEARS the marker (the round-18 "per load cycle" clear) → hold engages anyway. The bool can't
        // survive that ordering, so it's now a TIMESTAMP with a validity window: resets don't clear it,
        // staleness self-expires. A spurious skip (duplicate release inside the window) just means no
        // freeze — the release that follows corrects any drift; a WRONGLY-ENGAGED hold is the worse bug.
        // DateTime.UtcNow (not Time.*): the early release arrives on the NETWORK thread (net48: no TickCount64).
        private static System.DateTime _releasedBeforeHoldAtUtc = System.DateTime.MinValue;
        private static bool _releasedBeforeHold
        {
            get => (System.DateTime.UtcNow - _releasedBeforeHoldAtUtc).TotalSeconds < 90.0;
            set => _releasedBeforeHoldAtUtc = value ? System.DateTime.UtcNow : System.DateTime.MinValue;
        }

        /// <summary>True while the game is frozen waiting for all players to load.</summary>
        public static bool IsStartupHeld => _startupHold;

        /// <summary>Begin the startup hold — freezes the game at timeScale 0 and
        /// engages the game's REAL pause so players can't walk during the wait.</summary>
        public static void BeginStartupHold()
        {
            if (_startupHold) return;
            if (_releasedBeforeHold)
            {
                _releasedBeforeHold = false;
                // Round-36c: skipping the hold must STILL end unpaused — the load/menu set the native
                // pause and no release will follow to clear it (the one that arrived already ran its
                // early path). Without this, the skip path left the pause FLAG stuck on the client.
                GameStatePatcher.EnqueueOnMainThread(() => GameStateReader.SetNativePause(false));
                Plugin.Logger.LogInfo("[TimeSync] Startup hold SKIPPED — the release for this load already arrived (fast host / menu-reload ordering).");
                return;
            }
            _startupHold = true;
            ApplyNetwork(0f);
            GameStatePatcher.EnqueueOnMainThread(() => GameStateReader.SetNativePause(true));
            MPLoadProfiler.Mark($"FREEZE begin — timeScale now {Time.timeScale}");
            Plugin.Logger.LogInfo("[TimeSync] Startup hold — game paused until all players have loaded.");
        }

        // ── World-sync release gate (2026-07-20 severity review) ─────────────
        // The hot-join path could hand the CLIENT control before the business
        // snapshot applied ("playing blind" — the divergence incubator of the
        // silent-loss class).  A release arriving while the world sync hasn't
        // applied is DEFERRED: the hold stands until ApplyBusinessSnapshot
        // completes (gate-heal + fragmentation make that short), with a 180s
        // force-release fail-safe so a dead host can't freeze the client forever.
        private static System.DateTime _deferredReleaseUtc = System.DateTime.MinValue;
        private static bool _releaseDeferred;
        private static bool _forceNextRelease;   // 180s fail-safe bypasses the gate exactly once

        /// <summary>Called when the world sync has applied (ApplyBusinessSnapshot)
        /// — fires a deferred release if one is waiting.</summary>
        public static void NotifyWorldSyncApplied()
        {
            if (!_releaseDeferred) return;
            _releaseDeferred = false;
            Plugin.Logger.LogInfo("[TimeSync] deferred release firing — world sync has applied.");
            EndStartupHold();
        }

        /// <summary>Release the startup hold — resumes the game at normal speed.</summary>
        public static void EndStartupHold()
        {
            // CLIENT world-sync gate: never unfreeze onto an unsynced world.
            if (_forceNextRelease) { _forceNextRelease = false; }
            else if (!MPServer.IsRunning && MPClient.InMpGame && !MPClient.WorldSyncApplied)
            {
                if (!_releaseDeferred)
                {
                    _releaseDeferred = true;
                    _deferredReleaseUtc = System.DateTime.UtcNow;
                    Plugin.Logger.LogWarning("[TimeSync] release DEFERRED — world sync not yet applied (holding so the player can't act on an incomplete world).");
                }
                return;
            }
            if (!_startupHold)
            {
                _releasedBeforeHold = true;   // arrived before our hold began — remember it (round-18)
                // Round-36c: the release's INTENT is "run unpaused" regardless of hold state — the game's
                // own load pause may still be standing; clear it now, not only on the full-release path.
                GameStatePatcher.EnqueueOnMainThread(() => GameStateReader.SetNativePause(false));
                Plugin.Logger.LogInfo("[TimeSync] Startup release received BEFORE the hold began — remembered; the pending hold will be skipped.");
                return;
            }
            _startupHold = false;
            ApplyNetwork(1f);
            GameStatePatcher.EnqueueOnMainThread(() => GameStateReader.SetNativePause(false));
            MPCanvasUI.ReleaseHostSoftHold("startup hold released");   // round-205: host movement unlocks with everyone else
            MPLoadProfiler.Mark("FREEZE end — game running");
            Plugin.Logger.LogInfo("[TimeSync] Startup hold released — game running.");
        }

        /// <summary>
        /// Call every LateUpdate while the hold is active — re-clamps timeScale to
        /// 0 so nothing the game does during load can let time advance.
        /// </summary>
        public static void TickStartupHold()
        {
            // Deferred-release fail-safe: if the world sync never lands (dead host,
            // pre-fix peer), force the release after 180s — a playable-but-partial
            // world beats a frozen game, and the audit self-heal keeps converging it.
            if (_releaseDeferred && (System.DateTime.UtcNow - _deferredReleaseUtc).TotalSeconds > 180.0)
            {
                _releaseDeferred = false;
                _forceNextRelease = true;
                Plugin.Logger.LogWarning("[TimeSync] deferred release FORCED after 180s without world sync — playing on a partial world (audit self-heal will converge it).");
                EndStartupHold();
                return;
            }
            if (!_startupHold) return;
            if (Time.timeScale != 0f)
                Time.timeScale = 0f;
        }

        // ── Manual pause ──────────────────────────────────────────────────────
        //
        // The ONLY player-driven pause in the multiplayer time model.  Triggered
        // by the game's pause button (intercepted via a Harmony patch) and shared
        // across all players, so the world pauses/resumes for everyone together.
        // Menu / bench / bed pauses are NOT manual pauses — they are overridden.

        public static bool ManualPaused { get; private set; }

        /// <summary>Sets the shared manual-pause state (from a button press or the
        /// network).  Also drives the game's REAL pause on this machine so a
        /// network-applied pause shows the red border / stops movement exactly
        /// like a local pause press (callable from the poll thread — the IL2CPP
        /// part is marshalled).</summary>
        public static void SetManualPause(bool paused)
        {
            if (ManualPaused == paused) return;
            ManualPaused = paused;
            GameStatePatcher.EnqueueOnMainThread(() => GameStateReader.SetNativePause(paused));
            Plugin.Logger.LogInfo($"[TimeSync] Manual pause = {paused}");
        }

        // ── Round-284/F2: heartbeat pause convergence (client) ───────────────
        // The host's pause INTENT rides every GameTimeSync (PauseState 1/2; 0 = older
        // host, no convergence).  The ManualPause edge messages remain the fast path —
        // this is the recurrence-covered floor under them: a lost or no-op'd edge heals
        // within one heartbeat instead of leaving this client desynced until the next
        // press.  Echo suppression: when THIS client presses pause, the edge is in
        // flight and the next heartbeat can still carry the host's PRE-press intent —
        // converging on that would visibly revert the press just before the relay
        // lands.  The desired state is recorded at send; a matching heartbeat consumes
        // it, a mismatching one is ignored until the bound expires (~2 heartbeat
        // intervals) — then the host's word wins, and the WARN below is the visible
        // trace of a lost/rejected local pause request.
        // 284b (verifier F-4): written on the main thread, read on the poll thread.  An int,
        // not bool? — Nullable<bool> is a two-field struct with no atomicity guarantee across
        // that pairing.  0 = none, 1 = press wants PAUSED, 2 = press wants UNPAUSED.  The
        // timestamp is a paired long read/written via Volatile so a torn 64-bit read on a
        // 32-bit runtime can't fabricate a bound expiry; state is written LAST so a reader
        // that sees a pending press also sees its fresh timestamp.
        private static volatile int _pendingLocalPause;
        private static long _pendingLocalPauseAtUtcTicks;
        private const double PendingLocalPauseBoundSec = 8.0;   // ~2 heartbeat intervals (3s cadence)

        /// <summary>Round-284/F2: a local pause press is on the wire (MPClient.SendManualPause) —
        /// suppress heartbeat convergence back to the old state until it round-trips or the
        /// bound expires.</summary>
        public static void NotePendingLocalPause(bool desired)
        {
            System.Threading.Volatile.Write(ref _pendingLocalPauseAtUtcTicks, System.DateTime.UtcNow.Ticks);
            _pendingLocalPause = desired ? 1 : 2;
        }

        /// <summary>Round-284/F2: converge the shared pause onto the host's heartbeat intent
        /// (1 = paused, 2 = unpaused; callers must not pass 0 — legacy hosts don't stamp).
        /// Client-only by construction: the heartbeat is a host→client message, and the host's
        /// own intent IS the authority.  Recurrence-covered by construction — every subsequent
        /// heartbeat re-evaluates, so nothing here consumes a trigger it failed to act on.</summary>
        public static void ConvergePauseFromHeartbeat(int pauseState)
        {
            if (pauseState != 1 && pauseState != 2) return;
            // The startup hold OWNS pause during a load: Begin/EndStartupHold drive the
            // native pause directly and GameStateReader.TickPendingNativePause re-asserts
            // their intent every frame — converging to "unpaused" here would start a
            // SetNativePause tug-of-war against the hold (the 2026-06-10 freeze class).
            // Skipping is safe: the F1 join inform covers the join case, and the first
            // post-release heartbeat converges anything missed within ~3s.
            if (IsStartupHeld) return;
            bool hostPaused = pauseState == 1;
            int pending = _pendingLocalPause;
            if (pending != 0)
            {
                bool desired = pending == 1;
                var pressedAt = new System.DateTime(System.Threading.Volatile.Read(ref _pendingLocalPauseAtUtcTicks), System.DateTimeKind.Utc);
                if (hostPaused == desired)
                    _pendingLocalPause = 0;   // press round-tripped (or host already agreed) — converge normally (no-op)
                else if ((System.DateTime.UtcNow - pressedAt).TotalSeconds < PendingLocalPauseBoundSec)
                    return;   // press still in flight — ignore this heartbeat's pause bit; the next one re-evaluates
                else
                {
                    _pendingLocalPause = 0;
                    Plugin.Logger.LogWarning($"[TimeSync] local pause press ({desired}) never round-tripped within {PendingLocalPauseBoundSec:0}s — host intent is {(hostPaused ? "paused" : "unpaused")}; converging to the host (lost/rejected pause request).");
                }
            }
            SetManualPause(hostPaused);   // no-op when already matching (SetManualPause early-returns)
        }

        // ── Drift state ───────────────────────────────────────────────────────

        // Remaining game-hours of forward catch-up to RUN (we're behind the host). Drained by
        // TickClockCorrection, which drives the game tick rather than writing the clock. AHEAD is handled by the
        // freeze flag below, not here.
        private static float _correctionHours;
        /// <summary>True while we're AHEAD of the host: the RunMainGameTick prefix (MPPatches) zeroes the tick
        /// delta so the clock + economy HOLD until the host catches up — the player and the visible world keep
        /// moving (timeScale untouched). Never rewind. Released by a later sync once we're back in tolerance.</summary>
        public static volatile bool AheadHeld;

        // ── Authorized-write handshake with the anti-skip watchdog ───────────
        // TickWorldClock (MPCanvasUI) reverts any fast clock advance — which is
        // exactly what a TimeSync snap/drip looks like.  Without this flag the
        // two fight: sync writes host time, watchdog reverts it, repeat (the
        // client's world flickered night↔day every packet — user, 2026-06-12).
        // The watchdog consumes the flag and re-bases its sampling window
        // instead of rejecting.  Its OWN revert writes don't set the flag.
        private static volatile bool _wroteClock;
        public static bool ConsumeClockWrite() { var v = _wroteClock; _wroteClock = false; return v; }

        // One-time JOIN snap (user 2026-07-19, "on connect you match"): connecting
        // days behind the host used to schedule a run-forward catch-up that
        // SIMULATED every skipped day (wages, rent, RunDaily…) at fast-forward.
        // The no-snap rule (2026-06-18) was designed for small IN-SESSION drift,
        // where simulating is correct; a join gap is different — the host's world
        // already lived that time and its state arrives via sync anyway.  So the
        // FIRST clock sync of an MP game load WRITES the clock straight to host
        // time (exactly what loading a save does), unconditionally beyond the
        // dead-band.  All later in-session drift keeps the run-only rule.
        // (Client-AHEAD at join DOES happen — corrected 2026-09-04: an accepted disconnect save can sit up
        // to DisconnectDayWindow days ahead of the host's own stored save (MPSaveCoordinator), the host may
        // restart or roll back, and the legacy "no MP session yet" start loads unrelated single-player saves —
        // so the backward snap is real, rare and usually 1-2 days. ShiftLocalTimeline handles both directions.)
        private static bool _firstSyncSeen;

        /// <summary>H-EMP-1 / H-SNAP-1 (bug class SKIPPED-PASS DAY DIVERGENCE): the JOIN SNAP writes this client's clock to the host's
        /// day/hour in ONE jump without the game's daily/hourly passes for the hours in between ("no time passed for you"). The game keeps
        /// future events as absolute day numbers (some with an hour) in the player's own save and consumes them in those passes, so after a
        /// jump everything inside the gap fires at once (sick days, deliveries, the headhunter's catch-up loop, a tax due day) or is missed
        /// forever (recruitment campaigns and insurance offers test for the exact day and hour). ONE rule for every schedule the LOCAL
        /// player owns (round 4, 2026-09-04 — the day-only shift and its three special-case predicates were wrong by up to a day): an item
        /// due at hour H keeps its place in the sequence of H-o'clock passes — it moves by the number of H-o'clock passes the jump skipped
        /// (forward) or will re-live (backward). A day-only field is an item at the hour its pass runs (the daily pass at 00:00; the
        /// wholesale/import pass at 08:00; an insurance offer at HourToSendOffer). Forward, an item at or before the jump start is overdue
        /// or already consumed and is left alone (it fires at the next pass, as vanilla would); backward, every item moves (one left where
        /// it was would turn "future" and fire days late — user ruling 2026-09-04). The headhunter's nextRecruit is a distance-from-now and
        /// moves by the exact jump in minutes. Taxes move by the midnight crossings (`Day - day` counts them). Injected/synthetic staff
        /// records are display copies — never touched. Then, forward only, if a tax anniversary fell inside the gap the game's own annual
        /// assessment runs once (user ruling 2026-09-04). Main thread; once per load, right after the clock write.</summary>
        private static void ShiftLocalTimeline(int fromDay, float fromHourF, int toDay, float toHourF)
        {
            try
            {
                int fromHour = (int)fromHourF, toHour = (int)toHourF;
                int fromMoment = fromDay * 24 + fromHour, toMoment = toDay * 24 + toHour;   // hour-granular positions on the game clock
                float deltaMinutes = ((toDay * 24f + toHourF) - (fromDay * 24f + fromHourF)) * 60f;
                if (fromMoment == toMoment && System.Math.Abs(deltaMinutes) < 0.5f) return;   // review r4 #2: a sub-hour jump still moves the minute-granular nextRecruit (every hour-granular shift is 0 then)
                bool backward = toMoment < fromMoment;
                int lo = System.Math.Min(fromMoment, toMoment), hi = System.Math.Max(fromMoment, toMoment);
                int crossings = toDay - fromDay;                                             // midnight passes skipped (+) or re-lived (−)
                var gi = SaveGameManager.Current;
                if (gi == null) return;
                if (backward) Plugin.Logger.LogWarning($"[TimeSync] JOIN SNAP: clock moved BACK {(fromMoment - toMoment)} h (day {fromDay} {fromHour:00}:00 → day {toDay} {toHour:00}:00) — schedules move back with it{(crossings != 0 ? "; the game will OVERWRITE the re-lived days' financial summaries and order history and would bill a tax anniversary inside that span again (known divergence, user ruling 2026-09-04)" : "")} (H-SNAP-1).");

                // The shift for an item due at `hour`: how many `hour`-o'clock passes lie strictly inside the jump (lo, hi].
                int Skipped(int hour)
                {
                    int firstDay = lo / 24 + (hour <= lo % 24 ? 1 : 0);
                    int lastDay  = hi / 24 - (hour >  hi % 24 ? 1 : 0);
                    int n = lastDay - firstDay + 1;
                    return n <= 0 ? 0 : (backward ? -n : n);
                }
                // Forward: only what was still AHEAD of the jump start moves (an item at or before it is overdue/consumed). Backward: everything.
                bool Moves(int day, int hour) => backward || day * 24 + hour > fromMoment;
                // Apply the shift to a day field; true only if it moved (review r4 #3: the counters are the oracle — count moves, not visits).
                // Never below day 1 (review r4 #6: TimeHelper.GetDayOfWeek throws on a negative day; a consumed item pushed back stays past anyway).
                bool Shift(ref int dayField, int hour)
                {
                    int d = Skipped(hour);
                    if (d == 0) return false;
                    int moved = System.Math.Max(1, dayField + d);   // day 1 is the game's first day; TimeHelper.GetDayOfWeek throws on 0 and negatives (review r5 #3)
                    if (moved == dayField) return false;
                    dayField = moved;
                    return true;
                }

                int staff = 0, campaigns = 0, offers = 0, headhunters = 0, taxes = 0, vehicles = 0, furniture = 0, food = 0, wholesale = 0, imports = 0, moves = 0, installs = 0;
                try
                {
                    var list = gi.EmployeeInstances;
                    if (list != null)
                        foreach (var e in list)
                        {
                            if (e == null) continue;
                            string id = e.id ?? "";
                            if (id.StartsWith(MPRegisterSync.SyntheticDutyEmployeeIdPrefix) || MPRegisterSync.IsInjectedStaff(id)) continue;
                            if (!Moves(e.nextSickDay, 0)) continue;   // consumed by the daily pass at 00:00 (EmployeeHelper.RunDaily: nextSickDay <= Day)
                            if (Shift(ref e.nextSickDay, 0)) staff++;
                        }
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[TimeSync] timeline shift (staff): {ex.Message}"); }
                try
                {
                    var list = gi.RecruitmentCampaigns;
                    if (list != null)
                        foreach (var c in list)
                        {
                            if (c == null || c.finished || c.candidateFindTimes == null) continue;
                            foreach (var t in c.candidateFindTimes)
                            {
                                if (t == null || !Moves(t.Day, t.Hour)) continue;   // exact Day && Hour match (RecruitmentCampaign.CheckForCandidates)
                                if (Shift(ref t.Day, t.Hour)) campaigns++;
                            }
                        }
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[TimeSync] timeline shift (campaigns): {ex.Message}"); }
                try
                {
                    var list = gi.healthInsurancePlanOffers;
                    if (list != null && list.Count > 0)
                    {
                        int sendHour = Helpers.HealthInsuranceHelper.HourToSendOffer;   // the game's fixed offer hour (EmployeeHelper.RunHourly: dayToSendOffer == Day at this hour)
                        foreach (var o in list)
                        {
                            if (o == null || o.negotiationFinished || !Moves(o.dayToSendOffer, sendHour)) continue;
                            if (Shift(ref o.dayToSendOffer, sendHour)) offers++;
                        }
                    }
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[TimeSync] timeline shift (offers): {ex.Message}"); }
                try
                {
                    var list = gi.headhunterPlans;
                    if (list != null)
                        foreach (var h in list)
                        {
                            if (h == null || !h.isRecruiting || h.nextRecruit == null) continue;
                            // Unconditional, exact: nextRecruit is a distance-from-now the headhunter catches up on during shifts (often
                            // already past at the jump); moved by the exact jump it stays past by the same minutes — the vanilla catch-up —
                            // instead of replaying the whole gap one candidate per 0.25-4.25 h.
                            h.nextRecruit.AddMinutes(deltaMinutes); if (h.nextRecruit.Day < 1) { h.nextRecruit.Day = 1; h.nextRecruit.Hour = 0; h.nextRecruit.Minute = 0f; } headhunters++;   // review r4 #6: never below day 1
                        }
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[TimeSync] timeline shift (headhunters): {ex.Message}"); }
                try
                {
                    var t = gi.currentUnpaidTaxes;
                    if (t != null && crossings != 0)
                    {
                        // Both move together: `day` is the master (EnsureCurrentTaxesDueDay rewrites dueDay from it); the days elapsed on
                        // the bill (`Day - day`, a count of midnight passes) stay what they were, so the grace window and the day-21 warning survive.
                        t.day += crossings; if (t.dueDay > 0) t.dueDay += crossings; taxes = 1;
                    }
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[TimeSync] timeline shift (taxes): {ex.Message}"); }
                // Delivery contracts the local player placed (round 2, user ruling 2026-09-04). All five lists hold this machine's own
                // contracts only (the mod never replicates them; the shared-shop work tabs borrow a remote list only inside one synchronous
                // native call, never here). Vehicle/food fire on IsInThePast(day, hour); furniture on day >= && hour >=; wholesale/import
                // on the exact day at the 08:00 pass.
                try
                {
                    var list = gi.vehicleDeliveryContracts;
                    if (list != null)
                        foreach (var c in list)
                        {
                            if (c == null || !Moves(c.deliveryDay, c.deliveryHour)) continue;
                            if (Shift(ref c.deliveryDay, c.deliveryHour)) vehicles++;
                        }
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[TimeSync] timeline shift (vehicle deliveries): {ex.Message}"); }
                try
                {
                    var list = gi.FurnitureDeliveryContracts;
                    if (list != null)
                        foreach (var c in list)
                        {
                            if (c == null || !Moves(c.dayOfDelivery, c.hourOfDelivery)) continue;
                            if (Shift(ref c.dayOfDelivery, c.hourOfDelivery)) furniture++;
                        }
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[TimeSync] timeline shift (furniture deliveries): {ex.Message}"); }
                try
                {
                    var list = gi.FoodDeliveryContracts;   // null on older saves — nothing to shift then
                    if (list != null)
                        foreach (var c in list)
                        {
                            if (c == null || !Moves(c.dayOfDelivery, c.hourOfDelivery)) continue;
                            if (Shift(ref c.dayOfDelivery, c.hourOfDelivery)) food++;
                        }
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[TimeSync] timeline shift (food deliveries): {ex.Message}"); }
                try
                {
                    var list = gi.DeliveryContracts;
                    if (list != null)
                        foreach (var c in list)
                        {
                            if (c == null || !c.enabled || !Moves(c.nextDeliveryDay, 8)) continue;
                            if (Shift(ref c.nextDeliveryDay, 8)) wholesale++;   // keeps its place among the 08:00 passes; the weekday label follows (one off-Monday delivery, then the game's next-Monday rule resumes)
                        }
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[TimeSync] timeline shift (wholesale deliveries): {ex.Message}"); }
                try
                {
                    var list = gi.importPartnerships;
                    if (list != null)
                        foreach (var p in list)
                        {
                            if (p == null || !p.isActive || !Moves(p.nextDeliveryDay, 8)) continue;
                            if (Shift(ref p.nextDeliveryDay, 8)) imports++;
                        }
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[TimeSync] timeline shift (import partnerships): {ex.Message}"); }

                // Review r4 #1: two more schedules the local player owns — a booked business move (hourly: movingDay <= Day && movingHour <= Hour)
                // and an interior-installation order (daily pass: dayOfInstallation <= Day). Both `<=`, so nothing is lost or doubled — but left
                // unshifted, a move booked for days ahead would execute within an hour of joining.
                try
                {
                    var list = gi.movingServiceContracts;
                    if (list != null)
                        foreach (var c in list)
                        {
                            if (c == null || !Moves(c.movingDay, c.movingHour)) continue;
                            if (Shift(ref c.movingDay, c.movingHour)) moves++;
                        }
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[TimeSync] timeline shift (moving contracts): {ex.Message}"); }
                try
                {
                    var list = gi.interiorInstallationFirmContracts;
                    if (list != null)
                        foreach (var c in list)
                        {
                            if (c == null || !Moves(c.dayOfInstallation, 0)) continue;
                            if (Shift(ref c.dayOfInstallation, 0)) installs++;
                        }
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[TimeSync] timeline shift (installation contracts): {ex.Message}"); }

                Plugin.Logger.LogInfo($"[TimeSync] JOIN SNAP: timeline shifted — jump {(toMoment - fromMoment):+0;-0} h = {deltaMinutes:+0.#;-0.#} min ({crossings:+0;-0} midnight pass(es)): staff={staff} campaigns={campaigns} offers={offers} headhunters={headhunters} taxBill={taxes} vehicles={vehicles} furniture={furniture} food={food} wholesale={wholesale} imports={imports} moves={moves} installs={installs} — no time passed for you (H-EMP-1/H-SNAP-1).");

                if (!backward) RunSkippedAnnualAssessment(gi, fromDay, toDay);   // forward only: a rewind across an anniversary is the game's own re-bill (left, ruling 2026-09-04)
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[TimeSync] timeline shift: {ex.Message}"); }
        }

        /// <summary>H-SNAP-1 (user ruling 2026-09-04): the game bills the year's taxes only on the exact anniversary day
        /// (TaxHelper.PlayerShouldDoTaxes: Day % daysPerYear == 0), so a jump across it skipped the bill outright. When an anniversary
        /// fell inside the skipped days and no bill is outstanding, run the game's own assessment now (the same private routine the
        /// daily pass calls), gated by the game's own sales threshold. Its window is the year ending today, so the bill is never higher
        /// than the anniversary's would have been and never doubles the next one. Dated today, 20 days to pay — the game's usual.</summary>
        private static void RunSkippedAnnualAssessment(GameInstance gi, int fromDay, int toDay)
        {
            try
            {
                int dpy = gi.gameVariables?.daysPerYear ?? 0;
                if (dpy <= 0) return;
                if (toDay / dpy <= fromDay / dpy) return;                       // no anniversary inside (fromDay, toDay]
                int anniversary = (toDay / dpy) * dpy;
                if (gi.currentUnpaidTaxes != null)
                {
                    Plugin.Logger.LogInfo($"[TimeSync] JOIN SNAP: tax anniversary day {anniversary} fell inside the skipped days — assessment NOT run, a bill is still outstanding (the game skips it then too) (H-SNAP-1).");
                    return;
                }
                // The game's own threshold (TaxHelper.PlayerShouldDoTaxes): total sales over the last daysPerYear summaries, by list index.
                float sales = 0f;
                var sums = gi.financialSummaries;
                if (sums != null)
                {
                    int start = System.Math.Max(0, sums.Count - dpy);
                    for (int i = start; i < sums.Count; i++)
                    {
                        var st = sums[i]?.businessIncomeStatements;
                        if (st == null) continue;
                        foreach (var b in st) if (b != null) sales += b.TotalSales;
                    }
                }
                if (sales < 150000f)
                {
                    Plugin.Logger.LogInfo($"[TimeSync] JOIN SNAP: tax anniversary day {anniversary} fell inside the skipped days — assessment NOT run, sales ${sales:N0} below the game's $150,000 threshold (H-SNAP-1).");
                    return;
                }
                var m = HarmonyLib.AccessTools.Method(typeof(Helpers.TaxHelper), "ExecutePlayerTaxesEvent");
                if (m == null) { Plugin.Logger.LogWarning("[TimeSync] JOIN SNAP: TaxHelper.ExecutePlayerTaxesEvent not found — the skipped annual assessment could not be run (H-SNAP-1)."); return; }
                m.Invoke(null, null);
                Plugin.Logger.LogInfo($"[TimeSync] JOIN SNAP: tax anniversary day {anniversary} fell inside the skipped days — the game's annual assessment ran now: bill dated day {toDay}, 20 days to pay (H-SNAP-1).");
            }
            catch (System.Exception ex)
            {
                var inner = (ex as System.Reflection.TargetInvocationException)?.InnerException ?? ex;   // review r2/r3 #8: name the game's own failure, not the reflection wrapper
                Plugin.Logger.LogWarning($"[TimeSync] skipped annual assessment: {inner.GetType().Name}: {inner.Message}");
            }
        }

        /// <summary>
        /// Called when a clock-sync packet arrives.  Calculates drift and schedules correction.
        /// </summary>
        public static void ReceiveClockSync(int hostDay, float hostHour)
        {
            // Precedence (forward-compatible): while a consensus skip is active, the skip's fast-run is the SOLE
            // time authority on this machine — routine drift-correction stands down so it can't fight or jump
            // past the fast-run. Phase 2's rate-based drift inherits this same guard.
            if (MPRestSync.SkipActive) return;

            var (localDay, localHour) = GameStateReader.GetGameTime();

            float hostTotal  = hostDay  * 24f + hostHour;
            float localTotal = localDay * 24f + localHour;
            float drift      = hostTotal - localTotal;  // positive = we're behind host

            // One-time join snap: the FIRST sync of this load, on a client — match
            // the host outright (beyond the dead-band, in either direction).
            // Consume the arm ONLY when eligible: GameTimeSync packets also arrive
            // DURING loading (before InMpGame), and burning the one-shot there
            // would silently disable the snap for the load it exists for.
            bool firstSync = false;
            if (!_firstSyncSeen && MPClient.InMpGame && !MPServer.IsRunning)
            {
                _firstSyncSeen = true;
                firstSync = true;
            }
            if (firstSync && Mathf.Abs(drift) >= DRIFT_IGNORE_HOURS)
            {
                int snapDay = hostDay; float snapHour = hostHour;
                _correctionHours = 0f;
                AheadHeld        = false;
                GameStatePatcher.EnqueueOnMainThread(() =>
                {
                    var (dayBeforeWrite, hourBeforeWrite) = GameStateReader.GetGameTime();   // H-EMP-1 review r1 #1: live read at the moment of the write — this lambda can land frames after the packet (budgeted drain); a midnight crossing in between would otherwise over-shift by one
                    GameStateReader.SetGameTime(snapDay, snapHour);
                    ShiftLocalTimeline(dayBeforeWrite, hourBeforeWrite, snapDay, snapHour);   // H-EMP-1/H-SNAP-1: the local player's schedules survive the jump; a skipped tax anniversary is assessed
                    _wroteClock = true;   // authorized write — the anti-skip watchdog re-bases
                    Plugin.Logger.LogInfo($"[TimeSync] JOIN SNAP: clock set to day {snapDay}, {snapHour:0.00}h (drift was {drift:+0.#;-0.#}h) — the gap is NOT simulated (one-time per load).");
                });
                return;
            }

            float absDrift = Mathf.Abs(drift);

            if (absDrift < DRIFT_IGNORE_HOURS)
            {
                // Within tolerance — cancel any pending catch-up and release any freeze.
                _correctionHours = 0f;
                AheadHeld        = false;
                return;
            }

            if (drift > 0f)
            {
                // BEHIND the host — schedule a forward catch-up that RUNS the sim (TickClockCorrection drives
                // the game tick), so the catch-up simulates the economy instead of writing the clock past it.
                // Re-targeted on each sync; no hard snap regardless of size.
                _correctionHours = drift;
                AheadHeld        = false;
                Plugin.Logger.LogInfo($"[TimeSync] behind {drift:+0.###} h → run-forward catch-up.");
            }
            else
            {
                // AHEAD of the host — FREEZE our game-time tick (the RunMainGameTick prefix zeroes its delta)
                // until the host catches up; never rewind. Released by a later sync once back in tolerance.
                _correctionHours = 0f;
                AheadHeld        = true;
                Plugin.Logger.LogInfo($"[TimeSync] ahead {drift:+0.###;-0.###} h → hold clock until host catches up.");
            }
        }

        /// <summary>Clear pending clock-correction state at a session/scene boundary so a fresh
        /// game (single-player, or a new MP session) never inherits leftover catch-up / ahead-hold.</summary>
        public static void ResetClockState()
        {
            _correctionHours = 0f;
            AheadHeld        = false;
            _firstSyncSeen   = false;   // re-arm the one-time join snap for the next load
            _releaseDeferred = false; _forceNextRelease = false;   // world-sync release gate dies with the load
            _pendingLocalPause = 0;   // round-284/F2: an in-flight press dies with the load too
            // Round-81 (user-approved): a stale MANUAL pause dies with the load too. It was never
            // reset at session boundaries, so a pause from the PREVIOUS session survived the menu on
            // both roles and re-imposed itself on the fresh world (host informs joiners at
            // MPServer:1905; client re-relays during load) — world sat silently paused after the
            // startup hold released ("characters stuck until pause/unpause", log-proven 2026-07-24).
            // Flag-only on purpose: the startup hold owns NATIVE pause during a load, so we must not
            // drive an unpause here — just forget the stale intent.
            if (ManualPaused)
            {
                ManualPaused = false;
                Plugin.Logger.LogInfo("[TimeSync] Stale manual pause cleared at scene load (round-81).");
            }
            // Round-36: the early-release marker is deliberately NOT cleared here anymore — the scene-ready
            // reset ran BETWEEN the early release and the hold engage (log-proven), wiping the marker the
            // hold needed. Its 90s validity window handles staleness instead.
        }

        /// <summary>
        /// Call every Update frame (even when paused, but skip if timeScale == 0).
        /// Drips the scheduled clock correction into the game time.
        /// </summary>
        private static bool _tickCorrectionThrew;
        public static void TickClockCorrection()
        {
            if (!MPServer.IsRunning && !MPClient.InMpGame) return;   // never drain MP catch-up outside an MP game (e.g. a disconnect dropped us to single-player) — mirrors the AheadFreeze gate
            if (_correctionHours <= 0f) return;   // only the BEHIND catch-up runs here; AHEAD = the freeze flag
            if (Time.timeScale   == 0f) return;   // paused — hold

            // Close the gap by RUNNING time forward this frame (same engine as the skip), capped per frame, so
            // the catch-up simulates the economy instead of writing the clock past it.
            float advanceMin = Mathf.Min(MPRestSync.SkipMinutesPerRealSecond * Time.unscaledDeltaTime,
                                         MPRestSync.MaxSkipMinutesPerFrame);
            advanceMin = Mathf.Min(advanceMin, _correctionHours * 60f);   // don't overshoot the remaining gap
            if (advanceMin <= 0f) return;

            var gm = InstanceBehavior<GameManager>.Instance;
            // Throw isolation (RED ROC field NRE 2026-07-13): the native tick can NRE on
            // transient world state during catch-up; unguarded, that aborted the whole
            // MPCanvasUI.Update chain for the frame.  Log once per session, keep draining —
            // the gap still closes on subsequent frames.
            try { if (gm != null) gm.RunMainGameTick(advanceMin); }
            catch (Exception ex)
            {
                if (!_tickCorrectionThrew)
                { _tickCorrectionThrew = true; Plugin.Logger.LogWarning($"[TimeSync] catch-up tick threw (logged once): {ex.GetType().Name}: {ex.Message}"); }
            }
            _wroteClock       = true;            // authorized fast-advance — the watchdog re-bases, doesn't pin
            _correctionHours -= advanceMin / 60f;
            if (_correctionHours < 0.0001f) _correctionHours = 0f;
        }
    }
}
