using System.Collections.Generic;
using System.Diagnostics;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Lightweight main-thread frame profiler for the mod's tick work.
    /// Begin/End brackets each system; every ReportSeconds a single summary
    /// line shows ms-per-frame (avg) and worst-call ms per system, plus overall
    /// frame stats (FPS, worst frame, spike count) — so host choppiness can be
    /// attributed to a specific system, or ruled out as mod-caused entirely.
    /// Costs ~nothing while enabled (a Stopwatch read per bracket).
    /// </summary>
    // DIAG:FIELD — the [Perf] summary + all per-subsystem brackets (incl. RemUpd/RemLate)
    //   ship in the player build so submitted bug-report logs carry perf data.
    //   Log-only, cheap. See docs/DIAGNOSTICS.md.
    public static class MPPerf
    {
        // Always on in every build (2026-06-14).  The per-bracket Stopwatch reads
        // cost ~nothing (~20ns each, a couple µs/sec total at 70fps), and shipping
        // the profiler in the PLAYER build means a stuttering field report carries
        // its own [Perf] lines inside the submitted log — no special diagnostic
        // build needs to be pre-armed.  A player submitting their log is their
        // opt-in to have it reviewed.
        public static bool Enabled = true;
        private const float ReportSeconds = 10f;

        private sealed class Slot { public double Total; public double Max; public int Calls; }
        private static readonly Dictionary<string, Slot> _slots = new();
        private static readonly Stopwatch _sw = Stopwatch.StartNew();
        private static double _windowStartMs;
        private static int    _frames;
        private static double _frameTotalMs, _frameMaxMs;
        private static int    _spikes;
        private static int    _gc0, _gc1, _gc2;

        public static long Begin() => Enabled ? _sw.ElapsedTicks : 0L;

        public static void End(string name, long t0)
        {
            if (!Enabled || t0 == 0) return;
            double ms = (_sw.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
            if (!_slots.TryGetValue(name, out var s)) { s = new Slot(); _slots[name] = s; }
            s.Total += ms; s.Calls++; if (ms > s.Max) s.Max = ms;
        }

        // ── Round-97 PATCH-COST attribution (user-mandated after the lag-report audit) ────────
        // The line below admits it: Harmony patch BODIES run inside native calls and land in
        // "game+render" — the exact bucket the lag triage kept clearing as "not ours". These
        // brackets carry a NAME per patch site, aggregate per window, and — the pointing part —
        // any frame worse than SpikeSnapshotMs logs WHICH instrumented sites ran in that exact
        // frame and what each cost. A field spike line then reads either "ours=0.1ms" (cleared,
        // with proof) or "SiteX=490ms" (the fix target, named). Frame alignment: Unity's
        // unscaledDt at FrameTick describes the PREVIOUS frame, so per-frame site costs rotate
        // through a prev/cur buffer pair before the spike check reads them.
        private const double SpikeSnapshotMs = 100.0;
        private static readonly Dictionary<string, Slot> _patchSlots = new();
        private static Dictionary<string, double> _framePatchPrev = new();
        private static Dictionary<string, double> _framePatchCur  = new();

        public static void PatchEnd(string site, long t0)
        {
            if (!Enabled || t0 == 0) return;
            double ms = (_sw.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
            if (!_patchSlots.TryGetValue(site, out var s)) { s = new Slot(); _patchSlots[site] = s; }
            s.Total += ms; s.Calls++; if (ms > s.Max) s.Max = ms;
            _framePatchCur.TryGetValue(site, out double f);
            _framePatchCur[site] = f + ms;
        }

        /// <summary>Once per frame (end of Update).  Collects frame stats and emits
        /// a summary when the window elapses.  Runs in SINGLE-PLAYER too — each
        /// window is tagged SP / MP-HOST / MP-CLIENT, so an SP-then-MP session in
        /// one launch yields directly-comparable baselines for diffing the cost.
        /// Only accumulates while actually in a game (menus/loading reset).</summary>
        public static void FrameTick(float unscaledDt)
        {
            if (!Enabled) return;

            bool inGame = false;
            try { inGame = SaveGameManager.Current != null; } catch { }
            if (!inGame)
            {   // menu/loading — don't pollute a window with non-gameplay frames
                _slots.Clear(); _patchSlots.Clear(); _framePatchPrev.Clear(); _framePatchCur.Clear();
                _frames = 0; _frameTotalMs = 0; _frameMaxMs = 0; _spikes = 0;
                _windowStartMs = _sw.Elapsed.TotalMilliseconds;
                return;
            }

            _frames++;
            double dtMs = unscaledDt * 1000.0;
            _frameTotalMs += dtMs;
            if (dtMs > _frameMaxMs) _frameMaxMs = dtMs;
            if (dtMs > 33.4) _spikes++;   // worse than 30 FPS for that frame

            // Round-97: spike snapshot — dt describes the PREVIOUS frame; _framePatchPrev holds
            // that frame's per-site patch costs. Names the culprit (or exonerates, with numbers).
            if (dtMs > SpikeSnapshotMs)
            {
                try
                {
                    double ours = 0; foreach (var kv in _framePatchPrev) ours += kv.Value;
                    var top = new System.Text.StringBuilder();
                    int listed = 0;
                    foreach (var kv in _framePatchPrev)
                        if (kv.Value >= 1.0 && listed++ < 4) top.Append($" {kv.Key}={kv.Value:F1}ms");
                    Plugin.Logger.LogInfo($"[PatchCost] SPIKE {dtMs:F0}ms frame — instrumented mod share {ours:F1}ms{(top.Length > 0 ? " |" + top.ToString() : "")}");
                }
                catch { }
            }
            // rotate: cur becomes prev for the NEXT frame's dt to describe
            var tmp = _framePatchPrev; _framePatchPrev = _framePatchCur; _framePatchCur = tmp; _framePatchCur.Clear();

            double now = _sw.Elapsed.TotalMilliseconds;
            if (_windowStartMs == 0) { _windowStartMs = now; return; }
            if (now - _windowStartMs < ReportSeconds * 1000.0) return;

            try
            {
                string role = MPServer.IsRunning ? "MP-HOST" : (MPClient.IsConnected ? "MP-CLIENT" : "SP");
                double avgFrame = _frames > 0 ? _frameTotalMs / _frames : 0;
                int g0 = System.GC.CollectionCount(0), g1 = System.GC.CollectionCount(1), g2 = System.GC.CollectionCount(2);
                string gc = $" gc {g0 - _gc0}/{g1 - _gc1}/{g2 - _gc2}";
                _gc0 = g0; _gc1 = g1; _gc2 = g2;

                var sb = new System.Text.StringBuilder();
                sb.Append($"[Perf/{role}] {(now - _windowStartMs) / 1000.0:F1}s: {_frames}f avg {avgFrame:F1}ms ({(avgFrame > 0 ? 1000.0 / avgFrame : 0):F0}fps) worst {_frameMaxMs:F0}ms spikes {_spikes}{gc} |");

                // Per-system detail.  NOTE: Parked/Traffic/Biz/etc. are SUBSETS of
                // PosSync* — so for the ours-vs-game split below, sum only the
                // TOP-LEVEL brackets (Drain + WorldSnap + PosSync*), never all.
                double modTicks = 0;
                foreach (var kv in _slots)
                {
                    var s = kv.Value;
                    if (s.Calls == 0) continue;
                    double perFrame = s.Total / _frames;
                    if (kv.Key == "Drain" || kv.Key == "WorldSnap" || kv.Key == "PosSync*") modTicks += perFrame;
                    double cpf = (double)s.Calls / _frames;   // calls/frame — exposes hot patches fired per-NPC
                    sb.Append(cpf > 1.5
                        ? $" {kv.Key}={perFrame:F2}/{s.Max:F1}/{cpf:F0}x"
                        : $" {kv.Key}={perFrame:F2}/{s.Max:F1}");
                }
                // modTicks = time inside OUR per-frame work; gameOther = the rest of
                // the frame (game logic + render + our Harmony patch bodies, which
                // aren't bracketed).  A large gameOther in MP vs SP = cost we INDUCED
                // in the game (extra NPCs/ghosts it now simulates), not our ticks.
                // Round-97: instrumented patch-body share (runs INSIDE native calls — invisible
                // to modTicks). Window summary + a top-offenders line when any site spiked.
                double patchPerFrame = 0, patchWorst = 0;
                foreach (var kv in _patchSlots)
                { patchPerFrame += kv.Value.Total / _frames; if (kv.Value.Max > patchWorst) patchWorst = kv.Value.Max; }
                sb.Append($" || modTicks={modTicks:F1}ms patch={patchPerFrame:F2}/{patchWorst:F1} game+render={(avgFrame - modTicks - patchPerFrame):F1}ms");
                if (patchWorst >= 10.0)
                {
                    var pt = new System.Text.StringBuilder("[PatchCost] top:");
                    foreach (var kv in _patchSlots)
                        if (kv.Value.Max >= 5.0) pt.Append($" {kv.Key}={kv.Value.Calls}x max {kv.Value.Max:F1}ms total {kv.Value.Total:F0}ms");
                    Plugin.Logger.LogWarning(pt.ToString());
                }

                // Entity load — correlate frame cost with what we've added to the scene.
                try
                {
                    int remotes = RemotePlayerManager.GetRemotePlayerIds().Count;
                    if (MPServer.IsRunning)
                        sb.Append($" | ent parked={ParkedVehicleSync.HostTrackedCount} traffic={TrafficSync.HostTrafficCount()} remotes={remotes} clients={MPServer.ConnectedCount}");
                    else if (MPClient.IsConnected)
                        sb.Append($" | ent parkedGhosts={ParkedVehicleSync.ClientGhostCount} trafficGhosts={TrafficSync.ClientTrafficGhostCount} remotes={remotes}");
                }
                catch { }

                Plugin.Logger.LogInfo(sb.ToString());
            }
            catch { }
            _slots.Clear(); _patchSlots.Clear(); _frames = 0; _frameTotalMs = 0; _frameMaxMs = 0; _spikes = 0;
            _windowStartMs = now;
        }
    }
}
