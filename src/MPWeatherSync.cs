using System;
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>Weather sync (user-approved 2026-07-14): rain is driven by
    /// per-save state (gi.seed + gi.nextRainStartTime), so host and client skies
    /// diverge — one player in a downpour, the other in sun.  The host's rain
    /// state (dry/raining) rides the existing GameTimeSync heartbeat (~3s) as an
    /// additive JSON field (old builds ignore it — no protocol impact); the
    /// client forces its local RainHelper to match on mismatch.
    ///
    /// HOST-ONLY WEATHER (user-approved 2026-08-18): on MP clients the local
    /// rain scheduler's autonomous start/stop decisions are suppressed by the
    /// nested Harmony patch below — the host heartbeat is the ONLY rain driver.
    /// Before, clients ran their own seed-schedule sim PLUS our divergence
    /// corrections on top of it (up to double the transitions of solo play).
    /// The host's intensity also rides the heartbeat now, applied when rain
    /// starts on the client.  RIG-PROVEN CONSTRAINT (2026-08-18 run 1): the
    /// rain-state COMMIT lives inside UpdateIsRaining — the identical
    /// Command_ToggleRain call latched on the host (native UpdateIsRaining) and
    /// never latched on the client under a full skip (38 corrections, 0
    /// latches).  Hence the CONDITIONAL skip: run the method while local≠host
    /// (it is the commit path for our own forces), skip it only once converged
    /// (the only thing it could then do is fire a natural transition we don't
    /// want).
    ///
    /// RainHelper is a DECOMPILE-DUMP GAP in the mono-0.11 tree — everything
    /// here is reflection.  Its full skeleton (fields + method names, bodies
    /// empty) exists in the older dump at
    /// C:\code\cpp2il\output-2022.1\DiffableCs\DayNightCycle\...\RainHelper.cs
    /// and matches the live 0.11 F7 API dump exactly.  The Dev-build F7 key
    /// forces a transition locally, so the full sync loop is testable on one
    /// rig without waiting for natural rain.  Failure mode is purely cosmetic:
    /// skies stay divergent exactly as they always were.</summary>
    public static class MPWeatherSync
    {
        private static Type? _rainHelper;
        private static System.Reflection.PropertyInfo? _isRainingProp;
        private static System.Reflection.FieldInfo?    _isRainingField;
        private static System.Reflection.FieldInfo?    _nextIntensityField;
        private static System.Reflection.PropertyInfo? _dropsProp;
        private static bool _resolved;

        /// <summary>Last host rain verdict seen by this client (-1 until the first
        /// heartbeat carries one).  Read by the scheduler patch each frame.  Not
        /// reset on leaving MP — the patch's role gate deactivates entirely there,
        /// and on a rejoin the first heartbeat (~3s) overwrites; worst case is
        /// three seconds of a stale converged-skip, during which rain cannot
        /// change anyway.</summary>
        public static int HostRainState = -1;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                // FindGameType needs the FULL name and RainHelper's namespace is a
                // dump gap (first F7 run confirmed the bare name misses) — scan all
                // loaded assemblies by simple name and log what resolved.
                _rainHelper = VehicleManager.FindGameType("RainHelper");
                if (_rainHelper == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type?[] types;
                        try { types = asm.GetTypes(); }
                        catch (System.Reflection.ReflectionTypeLoadException rtle) { types = rtle.Types; }
                        catch { continue; }
                        foreach (var t in types)
                            if (t != null && t.Name == "RainHelper") { _rainHelper = t; break; }
                        if (_rainHelper != null) break;
                    }
                }
                if (_rainHelper == null)
                {
                    // Host-only round: Resolve() now also runs at PATCH time (plugin
                    // init), before the game has lazily loaded the DayNightCycle
                    // assembly — force-load it so patch-time resolution is
                    // deterministic instead of load-order-dependent.
                    try
                    {
                        _rainHelper = System.Reflection.Assembly.Load("DayNightCycle")
                            ?.GetType("BigAmbitions.DayNightCycle.RainHelper");
                    }
                    catch { }
                }
                if (_rainHelper == null)
                { Plugin.Logger.LogWarning("[Weather] RainHelper type not found in any loaded assembly — weather sync off."); return; }
                Plugin.Logger.LogInfo($"[Weather] RainHelper resolved: '{_rainHelper.FullName}' in {_rainHelper.Assembly.GetName().Name}.");
                _isRainingProp  = AccessTools.Property(_rainHelper, "isRaining");
                _isRainingField = _isRainingProp == null ? AccessTools.Field(_rainHelper, "isRaining") : null;
                if (_isRainingProp == null && _isRainingField == null)
                    Plugin.Logger.LogWarning("[Weather] RainHelper.isRaining not found — weather sync off.");
                // Intensity source (may be null on a future game update — intensity
                // then just stays -1/absent; state sync is unaffected).
                _nextIntensityField = AccessTools.Field(_rainHelper, "NextRainMaxIntensity");
                // Visual-layer readout (drops actually falling vs the isRaining
                // event flag) — rig verification + TestDrive status.
                _dropsProp = AccessTools.Property(_rainHelper, "AreRainDropsFalling");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Weather] resolve: {ex.Message}"); }
        }

        /// <summary>-1 unknown, 0 dry, 1 raining.</summary>
        public static int CurrentRainState()
        {
            Resolve();
            try
            {
                if (_isRainingProp  != null) return (bool)_isRainingProp.GetValue(null)! ? 1 : 0;
                if (_isRainingField != null) return (bool)_isRainingField.GetValue(null)! ? 1 : 0;
            }
            catch { }
            return -1;
        }

        /// <summary>HOST stamp source: 0..1 while raining, -1 dry/unknown.  Reads
        /// RainHelper.NextRainMaxIntensity.  Why that names the CURRENT rain while
        /// raining [structural inference — bodies unreadable]: the schedule is one
        /// bundle of static fields (start/end/intensity) and the scheduler still
        /// needs NextRainEndTime to stop the rain that is falling, so it cannot
        /// re-arm the bundle for the NEXT rain until this one ends.  Caveat:
        /// console/F7-forced rain bypasses the schedule (the toggle passes its own
        /// intensity straight through), so a forced host rain stamps the scheduled
        /// value rather than the forced 1.0 — cosmetic; natural rain is the case
        /// that matters in the field.</summary>
        public static float CurrentRainIntensity()
        {
            Resolve();
            try
            {
                if (_nextIntensityField == null || CurrentRainState() != 1) return -1f;
                float v = (float)_nextIntensityField.GetValue(null)!;
                return (v >= 0f && v <= 1f) ? v : -1f;
            }
            catch { }
            return -1f;
        }

        /// <summary>0/1 whether raindrops are visually falling, -1 unreadable.
        /// Distinct from the isRaining event flag (pre-rain phase, indoors and
        /// enclosed vehicles keep drops off while the event is active).</summary>
        public static int DropsFalling()
        {
            Resolve();
            try { if (_dropsProp != null) return (bool)_dropsProp.GetValue(null)! ? 1 : 0; }
            catch { }
            return -1;
        }

        /// <summary>CLIENT, main thread: align local rain with the host's state
        /// (and, when starting rain, the host's intensity).</summary>
        public static void ApplyRainState(int hostState, float hostIntensity = -1f)
        {
            if (hostState < 0) return;
            HostRainState = hostState;   // recorded before any early return — the scheduler patch keys off it
            int local = CurrentRainState();
            if (local < 0 || local == hostState) return;
            Plugin.Logger.LogInfo($"[Weather] host={(hostState == 1 ? "raining" : "dry")} local={(local == 1 ? "raining" : "dry")} — forcing local to match.");
            TryForceRain(hostState == 1, hostIntensity);
        }

        /// <summary>Invoke a RainHelper transition.  API named by the 2026-07-14
        /// F7 discovery dump ('BigAmbitions.DayNightCycle.RainHelper' in the
        /// DayNightCycle assembly): StopRaining/0 = deterministic stop;
        /// Command_ToggleRain/0 = the game's own console toggle (smooth
        /// transition) — safe for START because we only call it after checking
        /// the current state, so the toggle direction is guaranteed.  Exact
        /// Type.EmptyTypes lookups avoid the Command_ToggleRain/2 overload.</summary>
        public static void TryForceRain(bool on, float intensity = -1f)
        {
            Resolve();
            if (_rainHelper == null) return;
            try
            {
                int cur = CurrentRainState();
                if (cur == (on ? 1 : 0)) return;   // already matching
                if (!on)
                {
                    var stop = AccessTools.Method(_rainHelper, "StopRaining", Type.EmptyTypes);
                    if (stop != null)
                    { stop.Invoke(null, null); Plugin.Logger.LogInfo("[Weather] RainHelper.StopRaining() invoked."); return; }
                }
                // Starting with a known host intensity: the /2 overload takes it
                // directly (intensity 0..1, instant=false = the same smooth
                // transition as the /0 toggle).  The gate above guarantees we are
                // dry here, so "toggle" direction is still deterministic.
                if (on && intensity > 0f && intensity <= 1f)
                {
                    var toggle2 = AccessTools.Method(_rainHelper, "Command_ToggleRain", new[] { typeof(float), typeof(bool) });
                    if (toggle2 != null)
                    { toggle2.Invoke(null, new object[] { intensity, false }); Plugin.Logger.LogInfo($"[Weather] RainHelper.Command_ToggleRain({intensity:F2}) invoked (→ rain, host intensity)."); return; }
                }
                var toggle = AccessTools.Method(_rainHelper, "Command_ToggleRain", Type.EmptyTypes);
                if (toggle != null)
                { toggle.Invoke(null, null); Plugin.Logger.LogInfo($"[Weather] RainHelper.Command_ToggleRain() invoked (→ {(on ? "rain" : "dry")})."); return; }
                Plugin.Logger.LogWarning("[Weather] transition methods missing (StopRaining/Command_ToggleRain) — weather sync inert.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Weather] force: {ex.InnerException?.Message ?? ex.Message}"); }
        }

        /// <summary>HOST-ONLY WEATHER (user-approved 2026-08-18): on MP clients,
        /// suppress the rain scheduler's AUTONOMOUS start/stop decisions so the host
        /// heartbeat is the only rain driver.  The 2022.1 skeleton splits UpdateRain
        /// into UpdateIsRaining (decision + state commit, re-arming the schedule) and
        /// UpdateTransitionsProgresses (the visuals).
        ///
        /// CONDITIONAL skip, not a full skip — rig run 1 (2026-08-18) proved the
        /// state COMMIT for ALL transitions, our forces included, lives inside
        /// UpdateIsRaining: under a full skip the identical Command_ToggleRain call
        /// latched on the host and never latched on the client (38 heartbeat
        /// corrections, 0 latches).  So: while local≠host the method runs NATIVE (it
        /// is the commit path our just-injected force needs); once local==host it is
        /// skipped (converged — the only thing it could then do is fire a natural
        /// transition we don't want).  Convergence windows are a few frames around
        /// each host weather change; a natural transition sneaking into one either
        /// pushes in the host's direction or is re-corrected by the next heartbeat.
        ///
        /// Role and states are read LIVE on every call: host, solo, and a
        /// handoff-promoted host all run native, and the InMpGame gate is sticky
        /// through reconnect windows (a briefly-dropped client stays suppressed).
        ///
        /// 5-point checklist (2026-08-18): (1) no other mod patch class targets
        /// RainHelper (grep); (2) our only RainHelper calls are StopRaining /
        /// Command_ToggleRain — never UpdateIsRaining; (3) loader annotation
        /// verified on rig load (run 1: no shared-target note, 1 method bound);
        /// (4) MP-gated, client-only; (5) skip-prefix, no exception absorption.
        /// If a game update renames the method the loader's BOUND-NOTHING warn plus
        /// the warn below fire, and behavior degrades to the pre-round
        /// correction-only sync (cosmetic).</summary>
        [HarmonyPatch]
        public static class Patch_RainScheduler_HostOnly
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                Resolve();
                var m = _rainHelper == null ? null : AccessTools.Method(_rainHelper, "UpdateIsRaining", Type.EmptyTypes);
                if (m == null)
                {
                    Plugin.Logger.LogWarning("[Weather] RainHelper.UpdateIsRaining not found — client scheduler suppression OFF (correction-only sync remains).");
                    yield break;
                }
                yield return m;
            }

            private static bool _suppressLogged;   // once per launch: the line is the field evidence the round is active
            static bool Prefix()
            {
                if (MPServer.IsRunning || !MPClient.InMpGame) return true;   // host/solo/promoted: native scheduler
                int host = HostRainState;
                if (host < 0) return true;              // no host verdict yet (or old-version host): native = pre-round behavior
                if (CurrentRainState() != host) return true;   // CONVERGING: native — this is the commit path (rig run 1)
                if (!_suppressLogged)
                {
                    _suppressLogged = true;
                    Plugin.Logger.LogInfo("[Weather] client rain scheduler suppressed — host is weather-authoritative.");
                }
                return false;                           // CONVERGED: no autonomous transitions
            }
        }
    }
}
