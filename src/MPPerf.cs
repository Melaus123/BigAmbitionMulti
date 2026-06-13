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
        // Diagnostic scaffolding — excluded from the shipped build.  ON only in a
        // `-c Dev` build (BAMP_DEV); the per-system table costs a Stopwatch read
        // per bracket and is for local performance tuning, not end users.
        public static bool Enabled =
#if BAMP_DEV
            true;
#else
            false;
#endif
        private const float ReportSeconds = 10f;

        private sealed class Slot { public double Total; public double Max; public int Calls; }
        private static readonly Dictionary<string, Slot> _slots = new();
        private static readonly Stopwatch _sw = Stopwatch.StartNew();
        private static double _windowStartMs;
        private static int    _frames;
        private static double _frameTotalMs, _frameMaxMs;
        private static int    _spikes;
        private static int    _gc0, _gc1, _gc2, _gcIl;

        public static long Begin() => Enabled ? _sw.ElapsedTicks : 0L;

        public static void End(string name, long t0)
        {
            if (!Enabled || t0 == 0) return;
            double ms = (_sw.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
            if (!_slots.TryGetValue(name, out var s)) { s = new Slot(); _slots[name] = s; }
            s.Total += ms; s.Calls++; if (ms > s.Max) s.Max = ms;
        }

        /// <summary>Once per frame (end of Update).  Collects frame stats and
        /// emits the summary when the window elapses.  Quiet unless MP active.</summary>
        public static void FrameTick(float unscaledDt)
        {
            if (!Enabled) return;
            if (!MPServer.IsRunning && !MPClient.IsConnected)
            {   // not in MP — don't accumulate stale windows
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
                string role = MPServer.IsRunning ? "HOST" : "CLIENT";
                double avgFrame = _frames > 0 ? _frameTotalMs / _frames : 0;
                // GC activity this window: our CoreCLR side (gen0/1/2 deltas) +
                // the game's IL2CPP side if exposed — collector pauses are the
                // classic source of RHYTHMIC hitches.
                int g0 = System.GC.CollectionCount(0), g1 = System.GC.CollectionCount(1), g2 = System.GC.CollectionCount(2);
                string gc = $" gc {g0 - _gc0}/{g1 - _gc1}/{g2 - _gc2}";
                _gc0 = g0; _gc1 = g1; _gc2 = g2;
                try
                {
                    int gi = System.GC.CollectionCount(0);
                    gc += $" il2cpp {gi - _gcIl}";
                    _gcIl = gi;
                }
                catch { }
                var sb = new System.Text.StringBuilder();
                sb.Append($"[Perf/{role}] {(now - _windowStartMs) / 1000.0:F1}s: {_frames}f avg {avgFrame:F1}ms ({(avgFrame > 0 ? 1000.0 / avgFrame : 0):F0}fps) worst {_frameMaxMs:F0}ms spikes {_spikes}{gc} |");
                foreach (var kv in _slots)
                {
                    var s = kv.Value;
                    if (s.Calls == 0) continue;
                    // avg ms attributable PER FRAME (not per call) + worst single call
                    sb.Append($" {kv.Key}={s.Total / _frames:F2}/{s.Max:F1}");
                }
                Plugin.Logger.LogInfo(sb.ToString());
            }
            catch { }
            _slots.Clear(); _frames = 0; _frameTotalMs = 0; _frameMaxMs = 0; _spikes = 0;
            _windowStartMs = now;
        }
    }
}
