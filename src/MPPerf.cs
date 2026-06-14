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
                _slots.Clear(); _frames = 0; _frameTotalMs = 0; _frameMaxMs = 0; _spikes = 0;
                _windowStartMs = _sw.Elapsed.TotalMilliseconds;
                return;
            }

            _frames++;
            double dtMs = unscaledDt * 1000.0;
            _frameTotalMs += dtMs;
            if (dtMs > _frameMaxMs) _frameMaxMs = dtMs;
            if (dtMs > 33.4) _spikes++;   // worse than 30 FPS for that frame

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
                sb.Append($" || modTicks={modTicks:F1}ms game+render={(avgFrame - modTicks):F1}ms");

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
            _slots.Clear(); _frames = 0; _frameTotalMs = 0; _frameMaxMs = 0; _spikes = 0;
            _windowStartMs = now;
        }
    }
}
