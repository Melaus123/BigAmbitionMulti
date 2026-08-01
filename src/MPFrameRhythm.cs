using System;
using System.Collections.Generic;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>Round-207 — frame-RHYTHM telemetry (user-escalated standing symptom:
    /// constant fps oscillation, e.g. ~60↔~7, through entire MP sessions).
    ///
    /// Why it exists: the [Perf] lines are 10-second AVERAGES — a metronomic sawtooth
    /// and a steady slog produce the same line — and [PatchCost] SPIKE attribution is
    /// throttled (biased sample) and brackets only OUR ticks, so native work the sync
    /// triggers lands unattributed in "game+render". Result: repeated field reports of
    /// rhythmic throttling kept reading as "machine struggling". This module captures
    /// the SHAPE (percentiles), the CADENCE (period of slow frames), and the CULPRIT
    /// (slow frames phase-matched against a registry of periodic beats), so the next
    /// oscillating session names its rhythm on first contact.
    ///
    /// Cost: one float write per frame + an O(window) summary once per 10s. Beats are
    /// marked by one timestamp write at each periodic system's fire point.</summary>
    public static class MPFrameRhythm
    {
        private const float SlowFrameMs   = 50f;    // a frame visible as a hitch
        private const float WindowSecs    = 10f;    // summary cadence
        private const int   OscSlowCount  = 5;      // window is "oscillating" at ≥ this many slow frames
        private const int   DetailFirstN  = 20;     // per-slow-frame detail lines: first N ...
        private const int   DetailEveryN  = 10;     // ... then every Nth (ring stays informative, log stays sane)

        // ── beat registry: name → last fire time (unscaled) ─────────────────────
        private static readonly Dictionary<string, float> _beatLast = new();

        /// <summary>Called by a periodic system at its fire moment. One dictionary
        /// write — safe at any frequency, but only register beats with period ≥ ~0.5s
        /// (faster beats would phase-match every frame and carry no information).</summary>
        public static void MarkBeat(string name)
        {
            try { _beatLast[name] = Time.unscaledTime; } catch { }
        }

        // ── per-window state ────────────────────────────────────────────────────
        private static readonly List<float> _frameMs   = new(1024);
        private static readonly List<float> _slowAt    = new(64);
        private static readonly Dictionary<string, int> _slowBeatHits = new();
        private static float _windowStart;
        private static int   _slowDetailCount;
        private static int   _gc0, _gc1, _gc2;

        public static void Reset()
        {
            _frameMs.Clear(); _slowAt.Clear(); _slowBeatHits.Clear();
            _beatLast.Clear();
            _windowStart = 0f; _slowDetailCount = 0;
        }

        // Round-207b: per-hitch attribution counters (reported in the window summary).
        private static int _slowGcHits, _slowModHeavy;
        private static int _prevFrameGc0 = -1, _prevFrameGc2;

        /// <summary>Per-frame capture — call once per Update while in an MP session.
        /// Dev builds also capture SINGLE-PLAYER, so the rig can produce a vanilla
        /// baseline on the same machine — the mod-influence discriminator.</summary>
        public static void Tick()
        {
            try
            {
#if BAMP_DEV
                // dev rig: capture SP too (vanilla baseline; the mod is otherwise quiet there)
#else
                if (!MPServer.IsRunning && !MPClient.InMpGame) return;
#endif
                float now = Time.unscaledTime;
                if (_windowStart == 0f)
                {
                    _windowStart = now;
                    _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
                }

                float ms = Time.unscaledDeltaTime * 1000f;
                _frameMs.Add(ms);

                // Round-207b: did a GC run DURING the frame this dt describes?
                int g0 = GC.CollectionCount(0), g2 = GC.CollectionCount(2);
                bool gcThisFrame = _prevFrameGc0 >= 0 && (g0 != _prevFrameGc0 || g2 != _prevFrameGc2);
                _prevFrameGc0 = g0; _prevFrameGc2 = g2;

                if (ms >= SlowFrameMs)
                {
                    _slowAt.Add(now);
                    // Phase match: which beats fired DURING this frame (or within a
                    // 5ms margin before it)? Those are the rhythm suspects.
                    string matched = "";
                    float frameStart = now - Time.unscaledDeltaTime;
                    foreach (var kv in _beatLast)
                    {
                        if (kv.Value >= frameStart - 0.005f && kv.Value <= now)
                        {
                            _slowBeatHits.TryGetValue(kv.Key, out int c);
                            _slowBeatHits[kv.Key] = c + 1;
                            matched += (matched.Length > 0 ? "," : "") + kv.Key;
                        }
                    }
                    // Round-207b verdict per hitch: mod-heavy / GC / native.
                    var (modMs, modTop) = MPPerf.PrevFrameModShare();
                    if (gcThisFrame) _slowGcHits++;
                    if (modMs >= ms * 0.5f) _slowModHeavy++;
                    _slowDetailCount++;
                    if (_slowDetailCount <= DetailFirstN || _slowDetailCount % DetailEveryN == 0)
                        Plugin.Logger.LogInfo(
                            $"[Rhythm] slow frame #{_slowDetailCount}: {ms:F0}ms — mod={modMs:F1}ms{(modTop.Length > 0 ? $" ({modTop})" : "")}"
                            + $"{(gcThisFrame ? " +GC" : "")} — beats: {(matched.Length > 0 ? matched : "<none>")}");
                }

                if (now - _windowStart >= WindowSecs) EmitWindow(now);
            }
            catch { }
        }

        private static void EmitWindow(float now)
        {
            try
            {
                int n = _frameMs.Count;
                int slow = _slowAt.Count;
                bool oscillating = slow >= OscSlowCount;
                if (oscillating && n > 0)
                {
                    _frameMs.Sort();
                    float p50 = _frameMs[n / 2], p95 = _frameMs[(int)(n * 0.95f)], p99 = _frameMs[(int)(n * 0.99f)], max = _frameMs[n - 1];

                    // Cadence: median interval between consecutive slow frames.
                    float period = 0f;
                    if (slow >= 3)
                    {
                        var iv = new List<float>(slow - 1);
                        for (int i = 1; i < slow; i++) iv.Add(_slowAt[i] - _slowAt[i - 1]);
                        iv.Sort();
                        period = iv[iv.Count / 2];
                    }

                    string beats = "";
                    foreach (var kv in _slowBeatHits)
                        beats += $" {kv.Key}={kv.Value}/{slow}";
                    int g0 = GC.CollectionCount(0) - _gc0, g1 = GC.CollectionCount(1) - _gc1, g2 = GC.CollectionCount(2) - _gc2;

                    Plugin.Logger.LogWarning(
                        $"[Rhythm] OSCILLATING {WindowSecs:F0}s: frames={n} p50={p50:F0}ms p95={p95:F0}ms p99={p99:F0}ms max={max:F0}ms "
                        + $"slow(≥{SlowFrameMs:F0}ms)={slow} period≈{period:F2}s gc={g0}/{g1}/{g2} "
                        + $"| hitch verdicts: gc={_slowGcHits}/{slow} modHeavy={_slowModHeavy}/{slow} "
                        + $"| beat hits:{(beats.Length > 0 ? beats : " <none — cadence is not a registered beat>")}");
                }
            }
            catch { }
            finally
            {
                _frameMs.Clear(); _slowAt.Clear(); _slowBeatHits.Clear();
                _slowGcHits = 0; _slowModHeavy = 0;
                _windowStart = now;
                _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
            }
        }
    }
}
