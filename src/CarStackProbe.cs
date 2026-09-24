// PROBE-START: P-CARSTACK
using System.Collections.Generic;
using GleyTrafficSystem;   // TrafficManager - the game's own traffic pool, the same handle TrafficSync's census uses
using HarmonyLib;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>P-CARSTACK (user-approved 2026-09-21) - a LOG-ONLY probe for cars sitting inside one
    /// another. It CHANGES NOTHING: it walks tables the mod and the game already keep, measures
    /// bounds overlap, and prints what it found. Compiled into ALL configurations (a field report is
    /// where this is most likely to be seen), budgeted to 20 pair lines per session plus one summary
    /// line per pass that found any (and, within its own budget, per handover pass - see below).
    ///
    /// TRIGGERS ARE EDGES, never a frame - plus ONE bounded cadence:
    ///   - the world settling (MPWorldReady.IsSettled false -> true), i.e. just after a load/join;
    ///   - a consensus skip ending (MPRestSync's own skip edge);
    ///   - a traffic-mode change taking force (TrafficSync.ApplyTrafficMode);
    ///   - once per GAME HOUR while the local player is outdoors (the native hourly business tick);
    ///   - WHILE A TRAFFIC HANDOVER RUNS on a client (either direction; 2026-09-23 extension): a pass on the
    ///     handover's first tick and every 2 s after, naming the direction, the seconds since it began, and how
    ///     many local Gley cars and host ghosts are present. It ends with the handover (the 90 s in-view ceiling);
    ///   - WHILE A TIME SKIP RUNS and for 20 s after it ends, on BOTH machines (H-CARSTACK-1, 2026-09-24: every
    ///     old local-car x ghost pair was caught around a skip): a pass on the skip's first frame and every 2 s
    ///     after, with the traffic mode, the Gley cars alive (ambient / service / routed), the ghosts present and
    ///     the worst frame since the previous pass.
    ///   The two timed kinds share one session budget of summary lines (with or without pairs); once it AND the
    ///   pair budget are spent they skip their whole pass.
    ///
    /// REGISTRIES, existing ones only - no scene sweep:
    ///   - Helpers.VehicleHelper.AllPlayerVehicles (the game's own list of player cars, which on this
    ///     machine also holds the mod's materialised ghosts where they are real VehicleControllers);
    ///   - VehicleManager's remote player-vehicle ghost table;
    ///   - TrafficSync's traffic-ghost table;
    ///   - ParkedVehicleSync's parked tables (host-tracked and client ghosts);
    ///   - the game's own traffic pool, TrafficManager.Instance.trafficVehicles.GetVehicleList().</summary>
    internal static class CarStackProbe
    {
        private const float CellSize   = 8f;   // the bucket grid: a car is ~4-5 m, so a car can only touch its own cell and its neighbours
        private const int   PairBudget = 20;   // pair lines per session
        private static int  _pairsLogged;

        private static bool  _wasSettled;
        private static bool  _wasSkipping;
        private static float _skipEndedAt = -1f;

        // ── edges ────────────────────────────────────────────────────────────
        internal static void NoteSettled(bool now)
        {
            try
            {
                if (now == _wasSettled) return;   // compare-and-assign; a pass only on the rising edge
                _wasSettled = now;
                if (now) Pass("world-settled");
            }
            catch { }
        }

        internal static void NoteSkipActive(bool now)
        {
            try
            {
                if (now == _wasSkipping) return;
                _wasSkipping = now;
                if (!now) { _skipEndedAt = Time.unscaledTime; Pass("skip-ended"); }
            }
            catch { }
        }

        internal static void NoteTrafficMode(string mode)
        {
            try { Pass("traffic-mode=" + (mode ?? "")); } catch { }
        }

        // 2026-09-23 extension: every logged local-car x ghost pair so far had the local car HIGHER, and a
        // handover keeps in-view local cars beside kinematic ghosts for up to 90 s - watch that window.
        private const float CadencePassSeconds = 2f;
        private const int   CadenceLineBudget  = 150;   // review L1: summary lines of the TIMED passes (handover + skip), with or without pairs, per session
        private static int    _cadenceLines;
        private static string _handoverSeen = "none";
        private static float  _nextHandoverPass;

        /// <summary>Review L1: both budgets spent - a timed pass could print nothing, so it does no work at all.</summary>
        private static bool CadenceSpent => _cadenceLines >= CadenceLineBudget && _pairsLogged >= PairBudget;

        /// <summary>Client, every frame from TrafficSync.Tick (compare-and-return while no handover runs).</summary>
        internal static void NoteHandover(string handover, float beganAt)
        {
            try
            {
                if (handover == null || handover == TrafficSync.HandoverNone) { _handoverSeen = TrafficSync.HandoverNone; return; }
                float now = Time.unscaledTime;
                if (handover != _handoverSeen) { _handoverSeen = handover; _nextHandoverPass = now; }   // a new handover: pass now
                if (now < _nextHandoverPass) return;
                _nextHandoverPass = now + CadencePassSeconds;
                if (CadenceSpent) return;
                string trig = handover == TrafficSync.HandoverToGhost ? "handover-to-ghost" : "handover-to-local";
                int local = 0, ghosts = 0;
                try { local = TrafficSync.LocalAmbientCount(); } catch { }
                try { ghosts = TrafficSync.ClientTrafficGhostCount; } catch { }
                Pass(trig, $" handoverAge={now - beganAt:F1}s localAlive={local} ghosts={ghosts}", true);
            }
            catch { }
        }

        // H-CARSTACK-1 (2026-09-24): the skip cadence. Every old local-car x ghost pair (and the gley x gley
        // dY 1.42 one) was caught at trigger=skip-ended / hourly-outdoors, i.e. around TIME SKIPS - so watch the
        // skip itself and its first 20 s after, on both machines.
        private const float SkipTailSeconds = 20f;
        private static bool  _skipCadenceWasActive;
        private static float _skipTailUntil = -1f;
        private static float _nextSkipPass;
        private static float _worstFrameSincePass;
        private static int   _skipPasses;

        /// <summary>Every frame on BOTH machines, from TrafficSync.Tick. Outside a skip and its tail it is two
        /// compares; inside, a pass every 2 s (the skip's first frame included).</summary>
        internal static void NoteSkipCadence()
        {
            try
            {
                bool active = MPRestSync.SkipActive;
                float now = Time.unscaledTime;
                if (active && !_skipCadenceWasActive) { _nextSkipPass = now; _worstFrameSincePass = 0f; }   // a skip began: pass now
                if (!active && _skipCadenceWasActive) _skipTailUntil = now + SkipTailSeconds;
                _skipCadenceWasActive = active;
                if (!active && now >= _skipTailUntil) return;
                float dt = Time.unscaledDeltaTime;
                if (dt > _worstFrameSincePass) _worstFrameSincePass = dt;
                if (now < _nextSkipPass) return;
                _nextSkipPass = now + CadencePassSeconds;
                float worstMs = _worstFrameSincePass * 1000f;
                _worstFrameSincePass = 0f;
                if (CadenceSpent) return;
                _skipPasses++;
                string tmode = "?";
                try { tmode = MPServer.IsRunning ? "host" : (TrafficSync.ClientTrafficMode ?? ""); } catch { }
                int gley = 0, service = 0, routed = 0, ambient = 0;
                try
                {
                    var list = TrafficManager.Instance?.trafficVehicles?.GetVehicleList();
                    if (list != null)
                        for (int i = 0; i < list.Count; i++)
                        {
                            var v = list[i];
                            if (v == null || !v.gameObject.activeSelf) continue;
                            gley++;
                            if (ServiceCars.IsClientKept(v.gameObject)) service++;
                            else if (v.presetPath != null) routed++;
                            else ambient++;
                        }
                }
                catch { }
                int ghosts = 0;
                try { ghosts = TrafficSync.ClientTrafficGhostCount; } catch { }
                Pass(active ? "skip-active" : "skip-tail",
                     $" skipPass={_skipPasses} mode={tmode} gley={gley} ambient={ambient} service={service} routed={routed} ghosts={ghosts} worstFrameMs={worstMs:F0}", true);
            }
            catch { }
        }

        // ── the pass ─────────────────────────────────────────────────────────
        private struct Body
        {
            public string Kind, Model, Id;
            public int GoId;        // E (fold 3): the GAME OBJECT's instance id - the identity that decides "same car"
            public Bounds B;
        }

        private static readonly List<Body> _bodies = new List<Body>();
        private static readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
        /// <summary>E (fold 3) - THE SAME CAR IS IN MORE THAN ONE REGISTRY. A player vehicle is also a
        /// parked-table row; a materialised ghost is also a player vehicle. Every registry handed us the
        /// component's OWN instance id, so the ids differed and the probe cheerfully paired a car with
        /// itself - it sits inside its own bounds, so every such pair "overlapped". De-duplicate on the
        /// GameObject BEFORE any pairing: the first registry to name a body wins, the rest are skipped.</summary>
        private static readonly HashSet<int> _seenGos = new HashSet<int>();

        private static void Add(string kind, string model, string id, GameObject go)
        {
            try
            {
                if (go == null || !go.activeInHierarchy) return;
                int goId = go.GetInstanceID();
                if (!_seenGos.Add(goId)) return;        // already taken from an earlier registry
                Bounds b;
                if (!TryBounds(go, out b)) return;
                _bodies.Add(new Body { Kind = kind, Model = model ?? "", Id = id ?? "", GoId = goId, B = b });
            }
            catch { }
        }

        /// <summary>Solid collider bounds if the body has any, else renderer bounds. Triggers are
        /// skipped - a trigger volume overlapping a car is not a stacked car.</summary>
        private static bool TryBounds(GameObject go, out Bounds b)
        {
            b = new Bounds();
            bool any = false;
            try
            {
                var cols = go.GetComponentsInChildren<Collider>(false);
                for (int i = 0; i < cols.Length; i++)
                {
                    var c = cols[i];
                    if (c == null || c.isTrigger) continue;
                    if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
                }
            }
            catch { }
            if (any) return true;
            try
            {
                var rs = go.GetComponentsInChildren<Renderer>(false);
                for (int i = 0; i < rs.Length; i++)
                {
                    var r = rs[i];
                    if (r == null) continue;
                    if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
                }
            }
            catch { }
            return any;
        }

        private static long Key(int x, int z) { return ((long)x << 32) ^ (uint)z; }

        /// <summary>extra: appended right after the trigger on every line of this pass. cadence: a TIMED pass - its
        /// summary line is printed whether or not a pair was found (that is how it reports its counts), but only
        /// while the timed-pass budget lasts (review L1: pairs or not).</summary>
        internal static void Pass(string trigger, string extra = "", bool cadence = false)
        {
            try
            {
                _bodies.Clear();
                _cells.Clear();
                _seenGos.Clear();

                int reached = 0;
                try
                {
                    var pv = Helpers.VehicleHelper.AllPlayerVehicles;
                    if (pv != null)
                    {
                        reached++;
                        for (int i = 0; i < pv.Count; i++)
                        {
                            var v = pv[i];
                            if (v == null) continue;
                            Add("player-vehicle", v.gameObject.name, v.GetInstanceID().ToString(), v.gameObject);
                        }
                    }
                }
                catch { }
                try { VehicleManager.ProbeRemoteBodies(Add); reached++; } catch { }
                try { TrafficSync.ProbeGhostBodies(Add); reached++; } catch { }
                try { ParkedVehicleSync.ProbeParkedBodies(Add); reached++; } catch { }
                try
                {
                    var list = TrafficManager.Instance?.trafficVehicles?.GetVehicleList();
                    if (list != null)
                    {
                        reached++;
                        for (int i = 0; i < list.Count; i++)
                        {
                            var comp = list[i] as Component;
                            if (comp == null) continue;
                            Add("gley-traffic", comp.gameObject.name, comp.GetInstanceID().ToString(), comp.gameObject);
                        }
                    }
                }
                catch { }

                for (int i = 0; i < _bodies.Count; i++)
                {
                    var c = _bodies[i].B.center;
                    long k = Key(Mathf.FloorToInt(c.x / CellSize), Mathf.FloorToInt(c.z / CellSize));
                    List<int> bucket;
                    if (!_cells.TryGetValue(k, out bucket)) { bucket = new List<int>(); _cells[k] = bucket; }
                    bucket.Add(i);
                }

                int pairs = 0;
                string mode = "";
                try { mode = TrafficSync.ClientTrafficMode ?? ""; } catch { }
                float sinceSkip = _skipEndedAt < 0f ? -1f : Time.unscaledTime - _skipEndedAt;
                string perf = "";   // review H1: built lazily - only a pass that FOUND a pair pays for the string

                for (int i = 0; i < _bodies.Count; i++)
                {
                    var a = _bodies[i];
                    int cx = Mathf.FloorToInt(a.B.center.x / CellSize);
                    int cz = Mathf.FloorToInt(a.B.center.z / CellSize);
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            List<int> bucket;
                            if (!_cells.TryGetValue(Key(cx + dx, cz + dz), out bucket)) continue;
                            for (int n = 0; n < bucket.Count; n++)
                            {
                                int j = bucket[n];
                                if (j <= i) continue;              // each pair once
                                var b = _bodies[j];
                                if (a.GoId == b.GoId) continue;    // E: never pair an object with itself
                                if (!a.B.Intersects(b.B)) continue;
                                pairs++;
                                if (_pairsLogged >= PairBudget) continue;
                                _pairsLogged++;
                                float dy = a.B.center.y - b.B.center.y;
                                Plugin.Logger.LogWarning(
                                    $"[CarStack] PAIR {a.Kind}/'{a.Model}'#{a.Id} at {a.B.center} X {b.Kind}/'{b.Model}'#{b.Id} at {b.B.center} " +
                                    $"dY={dy:F2} trigger={trigger}{extra} traffic={mode} sinceSkipEnd={sinceSkip:F1}s perf[{perf}]");
                            }
                        }
                }

                if (pairs > 0 && perf.Length == 0) { try { perf = MPPerf.Snapshot(false); } catch { } }
                bool summary = cadence ? _cadenceLines < CadenceLineBudget : pairs > 0;
                if (summary && cadence) _cadenceLines++;
                if (summary)
                    Plugin.Logger.LogWarning($"[CarStack] pass trigger={trigger}{extra} bodies={_bodies.Count} registries={reached} " +
                                             $"pairs={pairs} logged={_pairsLogged}/{PairBudget} traffic={mode} sinceSkipEnd={sinceSkip:F1}s perf[{perf}]");
                _bodies.Clear();
                _cells.Clear();
                _seenGos.Clear();
            }
            catch (System.Exception ex) { try { Plugin.Logger.LogWarning($"[CarStack] pass: {ex.Message}"); } catch { } }
        }
    }

    /// <summary>P-CARSTACK's hourly trigger. BusinessSimulatorHelper.RunHourly is the game's own
    /// once-per-game-hour call (GameManager.RunMainGameTick, before Hour++), so it is an EVENT, not a
    /// timer. The pass runs only while the local player is OUTDOORS - a stacked car is an outdoor
    /// thing, and an interior is where the frame budget is tightest.</summary>
    [HarmonyPatch(typeof(BusinessSimulatorHelper), nameof(BusinessSimulatorHelper.RunHourly))]
    public static class Patch_RunHourly_CarStackProbe
    {
        private static int _lastHour = -1;
        static void Postfix()
        {
            try
            {
                if (BuildingManager.IsInsideBuilding) return;
                // Review H1: under the mod's own time skip an hour rolls about every 1.2 REAL seconds, so this
                // 'hourly' trigger became a ~0.8 Hz full pass (collider/renderer arrays per car) for an outdoor
                // player. No hourly pass while a skip runs - the skip-END edge already triggers one.
                if (MPRestSync.SkipActive) return;
                int h = SaveGameManager.Current.Hour;
                if (h == _lastHour) return;
                _lastHour = h;
                CarStackProbe.Pass("hourly-outdoors");
            }
            catch { }
        }
    }
}
// PROBE-END: P-CARSTACK
