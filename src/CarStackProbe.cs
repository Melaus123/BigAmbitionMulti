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
    ///   - H-CARSTACK-1 confirming test (2026-09-24): on the switch to ghost mode and on every handover pass, one
    ///     HCARS line (local cars alive / in view / keepable = in view AND within the retire distance / gone since
    ///     the last pass / the longest a car has survived / the arrival gate) and up to 12 HCAR lines, nearest first,
    ///     one per local car (model, distance to the handover anchor, in-view verdict, keep/retire verdict, how long
    ///     it has survived the handover, nearest ghost + dY). Own budget; handover passes also get their own pair
    ///     budget for pairs that involve a traffic car (the session pair budget is spent by parked cars at load).
    ///   The two timed kinds share one session budget of summary lines (with or without pairs); once it AND the
    ///   pair budget are spent they skip their whole pass.
    ///   - LIFT/TILT signal (2026-09-26, pre-approved, log-only): the pair metric is weak - dY compares transform
    ///     PIVOTS, which differ by model (trucks ~2.1, cars ~0.8), and bounds pairs include touching neighbours. So
    ///     EVERY pass also measures, for each body within 120 m of the local player (at most 120 per pass - moving
    ///     cars first (Gley traffic, traffic ghosts, remote player cars), then parked and own cars, each nearest
    ///     first: run 1 of t-carhandover2 had ~160 bodies in range and registry order spent the rays on parked cars), how high
    ///     its pivot sits above the ground straight under it (one downward ray from above the car; the car's own
    ///     and every other listed car's colliders and triggers are skipped) minus that MODEL's usual height (the
    ///     median of its last 21 samples this session, this pass included; needs 3 before a lift is judged), and
    ///     its pitch/roll plus its tilt against the ground normal. `LIFTED` = more than 0.3 m above the usual height
    ///     or tilted more than 10 degrees against the ground, naming the nearest other car. One `LIFT` summary per
    ///     pass + at most 10 `LIFTED` lines, worst first; own session budgets.
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
            try
            {
                if (mode == TrafficSync.ModeGhost) { ResetCarTrack(); LocalCars("switch-to-ghost", 0f); }
            }
            catch { }
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
                if (handover != _handoverSeen)   // a new handover: pass now
                {
                    _handoverSeen = handover; _nextHandoverPass = now;
                    if (handover != TrafficSync.HandoverToGhost) ResetCarTrack();   // to-ghost was reset on the switch itself
                }
                if (now < _nextHandoverPass) return;
                _nextHandoverPass = now + CadencePassSeconds;
                string trig = handover == TrafficSync.HandoverToGhost ? "handover-to-ghost" : "handover-to-local";
                try { LocalCars(trig, now - beganAt); } catch { }
                if (CadenceSpent && _hoPairsLogged >= HandoverPairBudget) return;
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

        // ── H-CARSTACK-1 confirming test (2026-09-24): the per-car handover lines ──
        private const int CarLinesPerPass    = 12;
        private const int CarLineBudget      = 700;   // HCAR + HCARS lines per session
        private const int HandoverPairBudget = 30;    // PAIR lines of handover passes that involve a traffic car
        private static int _carLines, _hoPairsLogged, _carPass;
        private static readonly Dictionary<int, float> _carFirstSeen = new Dictionary<int, float>();
        private static HashSet<int> _carPrev = new HashSet<int>();
        private static HashSet<int> _carNow  = new HashSet<int>();
        private static readonly List<KeyValuePair<float, VehicleComponent>> _carRows = new List<KeyValuePair<float, VehicleComponent>>();

        private static void ResetCarTrack() { _carFirstSeen.Clear(); _carPrev.Clear(); _carNow.Clear(); _carPass = 0; }

        /// <summary>Walks the SAME local-car set TickGhostHandover judges (active, not routed, not a kept service car)
        /// and prints what the handover sees. Read-only: nothing is removed, moved or re-flagged.</summary>
        private static void LocalCars(string trig, float age)
        {
            if (_carLines >= CarLineBudget) return;
            float now = Time.unscaledTime;
            Vector3 me;
            bool haveMe = TrafficSync.ProbeHandoverAnchor(out me);
            float retire = TrafficSync.ProbeRetireDistance;
            _carRows.Clear();
            _carNow.Clear();
            int alive = 0, inView = 0, keepable = 0;
            float heldMax = 0f;
            var list = TrafficManager.Instance?.trafficVehicles?.GetVehicleList();
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var v = list[i];
                    if (v == null || !v.gameObject.activeSelf) continue;
                    if (v.presetPath != null) continue;
                    if (ServiceCars.IsClientKept(v.gameObject)) continue;
                    alive++;
                    int id = v.gameObject.GetInstanceID();
                    _carNow.Add(id);
                    float first;
                    if (!_carFirstSeen.TryGetValue(id, out first)) { first = now; _carFirstSeen[id] = now; }
                    if (now - first > heldMax) heldMax = now - first;
                    float d = haveMe ? Vector3.Distance(v.gameObject.transform.position, me) : -1f;
                    bool iv = TrafficSync.ProbeLocalCarInView(v);
                    if (iv) inView++;
                    if (iv && d >= 0f && d <= retire) keepable++;
                    _carRows.Add(new KeyValuePair<float, VehicleComponent>(d, v));
                }
            int gone = 0;
            foreach (var id in _carPrev) if (!_carNow.Contains(id)) gone++;
            var swap = _carPrev; _carPrev = _carNow; _carNow = swap;
            _carPass++;
            int ghosts = 0;
            try { ghosts = TrafficSync.ClientTrafficGhostCount; } catch { }
            _carLines++;
            Plugin.Logger.LogWarning($"[CarStack] HCARS trig={trig} age={age:F1}s pass={_carPass} local={alive} inView={inView} keepable={keepable} " +
                                     $"goneSincePrev={gone} heldMaxS={heldMax:F1} ghosts={ghosts} {TrafficSync.ProbeHandoverGate()} retireAt={retire:F0}m " +
                                     $"anchor=({me.x:F0},{me.y:F1},{me.z:F0})");
            _carRows.Sort((x, y) => x.Key.CompareTo(y.Key));
            for (int i = 0; i < _carRows.Count && i < CarLinesPerPass && _carLines < CarLineBudget; i++)
            {
                var v = _carRows[i].Value;
                float d = _carRows[i].Key;
                try
                {
                    var p = v.gameObject.transform.position;
                    int id = v.gameObject.GetInstanceID();
                    float first;
                    float held = _carFirstSeen.TryGetValue(id, out first) ? now - first : 0f;
                    bool iv = TrafficSync.ProbeLocalCarInView(v);
                    string gm;
                    float gdy;
                    float gd = TrafficSync.ProbeNearestGhost(p, out gm, out gdy);
                    _carLines++;
                    Plugin.Logger.LogWarning($"[CarStack] HCAR trig={trig} age={age:F1}s pass={_carPass} car#{id} '{v.gameObject.name}' at ({p.x:F1},{p.y:F2},{p.z:F1}) " +
                                             $"d={d:F1}m inView={iv} verdict={(iv && d >= 0f && d <= retire ? "keep" : "retire")} heldS={held:F1} " +
                                             $"nearestGhost={(gd < 0f ? "none" : gd.ToString("F1") + "m")} '{gm}' dYghost={gdy:F2}");
                }
                catch { }
            }
            _carRows.Clear();
        }

        // ── the pass ─────────────────────────────────────────────────────────
        private struct Body
        {
            public string Kind, Model, Id;
            public int GoId;        // E (fold 3): the GAME OBJECT's instance id - the identity that decides "same car"
            public Bounds B;
            public GameObject Go;   // LIFT/TILT: the body itself (transform + the ray's own-collider skip)
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
                _bodies.Add(new Body { Kind = kind, Model = model ?? "", Id = id ?? "", GoId = goId, B = b, Go = go });
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
                bool hoPass = trigger.StartsWith("handover-") || trigger.StartsWith("traffic-mode=");
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
                                bool trafficPair = a.Kind == "gley-traffic" || a.Kind == "traffic-ghost" || b.Kind == "gley-traffic" || b.Kind == "traffic-ghost";
                                if (hoPass && trafficPair && _hoPairsLogged < HandoverPairBudget) _hoPairsLogged++;   // H-CARSTACK-1: the handover's own pair budget
                                else if (_pairsLogged >= PairBudget) continue;
                                else _pairsLogged++;
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
                try { LiftPass(trigger, extra); } catch (System.Exception lex) { try { Plugin.Logger.LogWarning($"[CarStack] lift: {lex.Message}"); } catch { } }
                _bodies.Clear();
                _cells.Clear();
                _seenGos.Clear();
            }
            catch (System.Exception ex) { try { Plugin.Logger.LogWarning($"[CarStack] pass: {ex.Message}"); } catch { } }
        }

        // ── LIFT/TILT signal (2026-09-26, log-only; see the class comment) ──
        private const float LiftRange         = 120f;   // metres from the local player (horizontal)
        private const float LiftFlagMetres    = 0.3f;   // above the model's usual height
        private const float TiltFlagDegrees   = 10f;    // against the ground normal
        private const int   LiftCarsPerPass   = 120;    // rays per pass (moving cars first, then nearest)
        private const int   LiftDetailPerPass = 10;
        private const int   LiftSummaryBudget = 400;    // LIFT lines per session
        private const int   LiftDetailBudget  = 500;    // LIFTED lines per session
        private const int   ModelWindow       = 21;     // samples kept per model
        private const int   ModelMinSamples   = 3;      // before a lift is judged
        private static int _liftSummaries, _liftDetails;
        private static readonly Dictionary<string, List<float>> _modelHeights = new Dictionary<string, List<float>>();
        private static readonly List<float> _medianScratch = new List<float>();
        private static readonly RaycastHit[] _liftHits = new RaycastHit[24];
        private static readonly HashSet<int> _carGos = new HashSet<int>();
        private static readonly List<KeyValuePair<float, int>> _liftOrder = new List<KeyValuePair<float, int>>();   // (priority key, body index)

        private struct LiftRow
        {
            public int Body, Samples;
            public float H, Usual, Lift, Clear, Pitch, Roll, TiltG;
            public bool Lifted, Tilted;
            public string Ground;
        }
        private static readonly List<LiftRow> _liftRows = new List<LiftRow>();

        private static string ModelKey(string model)
        {
            string m = model ?? "";
            int k = m.IndexOf("(Clone)", System.StringComparison.Ordinal);
            if (k >= 0) m = m.Substring(0, k);
            return m.Trim();
        }

        /// <summary>Is this collider part of any body the pass listed (walks up to 12 parents)?</summary>
        private static bool OnListedCar(Transform t)
        {
            for (int i = 0; t != null && i < 12; i++, t = t.parent)
                if (_carGos.Contains(t.gameObject.GetInstanceID())) return true;
            return false;
        }

        private static float MedianOf(List<float> src)
        {
            _medianScratch.Clear();
            _medianScratch.AddRange(src);
            _medianScratch.Sort();
            int n = _medianScratch.Count;
            if (n == 0) return 0f;
            return (n & 1) == 1 ? _medianScratch[n / 2] : 0.5f * (_medianScratch[n / 2 - 1] + _medianScratch[n / 2]);
        }

        private static void LiftPass(string trigger, string extra)
        {
            if (_liftSummaries >= LiftSummaryBudget || _bodies.Count == 0) return;
            Vector3 me;
            if (!TrafficSync.ProbeHandoverAnchor(out me)) return;
            _liftRows.Clear();
            _carGos.Clear();
            for (int i = 0; i < _bodies.Count; i++) _carGos.Add(_bodies[i].GoId);
            int inRange = 0, probed = 0, noGround = 0;
            float r2 = LiftRange * LiftRange;
            // Order: moving cars (anything not parked and not a player-vehicle row) before the rest, each nearest first;
            // the key is the squared distance, plus a big offset for the second group.
            _liftOrder.Clear();
            for (int i = 0; i < _bodies.Count; i++)
            {
                var bd = _bodies[i];
                if (bd.Go == null) continue;
                Vector3 c = bd.B.center;
                float dx = c.x - me.x, dz = c.z - me.z;
                float d2 = dx * dx + dz * dz;
                if (d2 > r2) continue;
                bool still = bd.Kind == "player-vehicle" || bd.Kind.StartsWith("parked");
                _liftOrder.Add(new KeyValuePair<float, int>(d2 + (still ? 1e7f : 0f), i));
            }
            inRange = _liftOrder.Count;
            _liftOrder.Sort((x, y) => x.Key.CompareTo(y.Key));
            for (int oi = 0; oi < _liftOrder.Count && probed < LiftCarsPerPass; oi++)
            {
                int i = _liftOrder[oi].Value;
                var bd = _bodies[i];
                try
                {
                    probed++;
                    var tr = bd.Go.transform;
                    Vector3 pos = tr.position;
                    var origin = new Vector3(pos.x, bd.B.max.y + 2f, pos.z);
                    int n = Physics.RaycastNonAlloc(origin, Vector3.down, _liftHits, 80f, ~(1 << 2), QueryTriggerInteraction.Ignore);   // review LOW: not the 'Ignore Raycast' layer
                    int best = -1;
                    float bestD = float.MaxValue;
                    for (int k = 0; k < n && k < _liftHits.Length; k++)
                    {
                        var col = _liftHits[k].collider;
                        if (col == null || OnListedCar(col.transform)) continue;
                        if (_liftHits[k].distance < bestD) { bestD = _liftHits[k].distance; best = k; }
                    }
                    if (best < 0) { noGround++; continue; }
                    var hit = _liftHits[best];
                    float h = pos.y - hit.point.y;
                    string mk = ModelKey(bd.Model);
                    List<float> win;
                    if (!_modelHeights.TryGetValue(mk, out win)) { win = new List<float>(); _modelHeights[mk] = win; }
                    win.Add(h);
                    if (win.Count > ModelWindow) win.RemoveAt(0);
                    Vector3 f = tr.forward, rt = tr.right;
                    string gname = "";
                    try { gname = LayerMask.LayerToName(hit.collider.gameObject.layer) + ":" + hit.collider.gameObject.name; } catch { }
                    _liftRows.Add(new LiftRow
                    {
                        Body  = i,
                        H     = h,
                        Clear = bd.B.min.y - hit.point.y,
                        Pitch = Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg,
                        Roll  = Mathf.Asin(Mathf.Clamp(rt.y, -1f, 1f)) * Mathf.Rad2Deg,
                        TiltG = Vector3.Angle(tr.up, hit.normal),
                        Ground = gname,
                    });
                }
                catch { }
            }
            _liftOrder.Clear();
            // judge after every sample of this pass is in its model's window
            int lifted = 0, tilted = 0, flagged = 0;
            float worstLift = 0f, worstTilt = 0f;
            for (int i = 0; i < _liftRows.Count; i++)
            {
                var row = _liftRows[i];
                List<float> win;
                if (_modelHeights.TryGetValue(ModelKey(_bodies[row.Body].Model), out win) && win.Count >= ModelMinSamples)
                {
                    row.Samples = win.Count;
                    row.Usual = MedianOf(win);
                    row.Lift = row.H - row.Usual;
                    row.Lifted = row.Lift > LiftFlagMetres;
                }
                else { row.Samples = win != null ? win.Count : 0; row.Usual = float.NaN; row.Lift = float.NaN; }
                row.Tilted = row.TiltG > TiltFlagDegrees;
                if (row.Lifted) lifted++;
                if (row.Tilted) tilted++;
                if (row.Lifted || row.Tilted) flagged++;
                if (!float.IsNaN(row.Lift) && row.Lift > worstLift) worstLift = row.Lift;
                if (row.TiltG > worstTilt) worstTilt = row.TiltG;
                _liftRows[i] = row;
            }
            _liftSummaries++;
            Plugin.Logger.LogWarning($"[CarStack] LIFT trigger={trigger}{extra} inRange={inRange} probed={probed} noGround={noGround} " +
                                     $"flagged={flagged} lifted={lifted} tilted={tilted} worstLift={worstLift:F2}m worstTiltG={worstTilt:F1} " +
                                     $"models={_modelHeights.Count} budget={_liftSummaries}/{LiftSummaryBudget}");
            if (flagged == 0 || _liftDetails >= LiftDetailBudget) { _liftRows.Clear(); return; }
            // worst first: the larger of (lift in metres x 10) and (tilt in degrees) - both read as 'x times the flag'
            _liftRows.Sort((a, b) =>
            {
                float sa = Mathf.Max(float.IsNaN(a.Lift) ? 0f : a.Lift / LiftFlagMetres, a.TiltG / TiltFlagDegrees);
                float sb = Mathf.Max(float.IsNaN(b.Lift) ? 0f : b.Lift / LiftFlagMetres, b.TiltG / TiltFlagDegrees);
                return sb.CompareTo(sa);
            });
            int shown = 0;
            for (int i = 0; i < _liftRows.Count && shown < LiftDetailPerPass && _liftDetails < LiftDetailBudget; i++)
            {
                var row = _liftRows[i];
                if (!row.Lifted && !row.Tilted) continue;
                try
                {
                    var a = _bodies[row.Body];
                    Vector3 p = a.Go != null ? a.Go.transform.position : a.B.center;
                    int nb = -1;
                    float nd = float.MaxValue;
                    for (int j = 0; j < _bodies.Count; j++)
                    {
                        if (j == row.Body || _bodies[j].GoId == a.GoId) continue;
                        float d = Vector3.Distance(a.B.center, _bodies[j].B.center);
                        if (d < nd) { nd = d; nb = j; }
                    }
                    string near = nb < 0 ? "none"
                        : $"{_bodies[nb].Kind}/'{_bodies[nb].Model}'#{_bodies[nb].Id} d={nd:F1}m dYcentre={a.B.center.y - _bodies[nb].B.center.y:F2}";
                    string why = row.Lifted && row.Tilted ? "lift+tilt" : (row.Lifted ? "lift" : "tilt");
                    string lift = float.IsNaN(row.Lift) ? $"n/a(samples={row.Samples})" : $"{row.Lift:+0.00;-0.00}m";
                    string usual = float.IsNaN(row.Usual) ? "n/a" : row.Usual.ToString("F2");
                    shown++;
                    _liftDetails++;
                    Plugin.Logger.LogWarning($"[CarStack] LIFTED why={why} {a.Kind}/'{a.Model}'#{a.Id} at ({p.x:F1},{p.y:F2},{p.z:F1}) lift={lift} " +
                                             $"h={row.H:F2} usual={usual} n={row.Samples} clear={row.Clear:F2} pitch={row.Pitch:F1} roll={row.Roll:F1} " +
                                             $"tiltG={row.TiltG:F1} ground='{row.Ground}' nearest={near} trigger={trigger}{extra}");
                }
                catch { }
            }
            _liftRows.Clear();
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
