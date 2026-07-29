using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace BigAmbitionsMP
{
    /// <summary>[MoveFreeze] SYMPTOM-level probe (approved 2026-07-16): detects
    /// "player is trying to move but isn't moving" directly, independent of the
    /// H1 stuck-selection diagnosis — if H1 is wrong, the snapshot names the real
    /// blocker.  Trigger: ≥3s of sustained move input (or ≥6 ground clicks in 5s)
    /// while total displacement over the same window stays under 0.5m, outside
    /// every legitimate lock we can identify (loading, in-vehicle, hospital trip,
    /// a focused input field, pause).  One rich snapshot per freeze episode
    /// (selection state incl. destroyed-leftover test, overlay/pointer state,
    /// navmesh agent state, dialog, building context) + a "resolved after Xs"
    /// line when movement returns.  Log-only; capped per session.
    ///
    /// PlayerAction (the game's input wrapper) is a DUMP GAP — resolved by
    /// reflection at first use; raw WASD/arrow keys are the fallback, so the
    /// probe degrades gracefully rather than dying with the dump.</summary>
    public static class MPFreezeProbe
    {
        private const float WindowSec   = 3f;     // input must persist this long
        private const float MinMoveM    = 0.5f;   // less than this over the window = frozen
        // Round-185 (user ruling: a LIFETIME cap starves the endgame — twice now a field session
        // spent its whole budget on early false positives and was blind for the episode the report
        // was about): volume is bounded by RATE (one episode start per minute — a still-stuck
        // player just opens the episode late) and the cap survives only as a runaway backstop
        // (~3.3h of CONTINUOUS misfiring to reach it), still announcing itself loudly.
        private const int   MaxEpisodes      = 200;
        private const float MinEpisodeGapSec = 60f;

        // Position ring: one sample per 0.5s, 8 slots = 4s of history.
        private static readonly Vector3[] _ring = new Vector3[8];
        private static readonly float[]   _ringT = new float[8];
        private static int _ringN;                 // total samples ever (head = (_ringN-1)%8)
        private static float _nextSampleAt;

        private static float _wasdHeldSince = -1f;
        private static readonly System.Collections.Generic.Queue<(float t, bool overUi)> _clicks = new();

        private static bool  _inEpisode;
        private static float _episodeStart;
        private static int   _episodesLogged;
        private static float _lastEpisodeStartAt = -999f;   // round-185: rate-limit anchor

        // PlayerAction.Move reflection (dump gap).
        private static bool _inputResolved;
        private static object? _moveAction;
        private static System.Reflection.MethodInfo? _vectorMi;

        public static void Reset()
        {
            _ringN = 0; _wasdHeldSince = -1f; _clicks.Clear();
            _inEpisode = false; _episodesLogged = 0; _lastEpisodeStartAt = -999f;
        }

        public static void Tick()
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                if (_episodesLogged >= MaxEpisodes && !_inEpisode)
                {
                    // Round-175: the cap must announce itself ONCE — an unlogged freeze after a silent
                    // cap is indistinguishable from a healthy session in a report.
                    if (!_capAnnounced)
                    {
                        _capAnnounced = true;
                        Plugin.Logger.LogWarning($"[MoveFreeze] episode cap ({MaxEpisodes}) reached — further freeze episodes this session will NOT be logged.");
                    }
                    return;
                }

                // Legitimate locks: any of these and the player is EXPECTED to be
                // still — clear all state so a freeze can't be misattributed.
                // Round-107: name WHICH condition stood us down. A field report burned several
                // exchanges on "were they in a vehicle?" that this string answers outright — the
                // real reason there was a focused UI element (a stuck player clicking menus),
                // not a vehicle at all.
                string legitWhy = "";
                try { if (UI.Load.LoadScene.isLoading) legitWhy = "loading"; } catch { }
                try { if (legitWhy.Length == 0 && Helpers.VehicleHelper.IsInsideVehicle()) legitWhy = "in-vehicle"; } catch { }
                try { if (legitWhy.Length == 0 && Helpers.EnergyHelper.goingToHospital) legitWhy = "hospital"; } catch { }
                try { if (legitWhy.Length == 0 && Time.timeScale == 0f) legitWhy = "paused"; } catch { }
                try
                {
                    if (legitWhy.Length == 0 && EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null)
                        legitWhy = "focused-ui:" + EventSystem.current.currentSelectedGameObject.name;
                }
                catch { }
                if (legitWhy.Length > 0) { EndEpisode("legit-lock: " + legitWhy); _ringN = 0; _wasdHeldSince = -1f; _clicks.Clear(); return; }

                Vector3 pos;
                try { pos = Helpers.PlayerHelper.GetPosition(); } catch { return; }
                float now = Time.unscaledTime;

                if (now >= _nextSampleAt)
                {
                    _ring[_ringN % _ring.Length] = pos; _ringT[_ringN % _ring.Length] = now;
                    _ringN++; _nextSampleAt = now + 0.5f;
                }

                // Movement intent (a): held move input, game's own wrapper first.
                float mag = ReadMoveMagnitude();
                if (mag >= 0.05f) { if (_wasdHeldSince < 0f) _wasdHeldSince = now; }
                else _wasdHeldSince = -1f;

                // Movement intent (b): ground-click spam (click-to-move players).
                if (Input.GetMouseButtonDown(0))
                {
                    bool overUi = false;
                    try { overUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(); } catch { }
                    _clicks.Enqueue((now, overUi));
                }
                while (_clicks.Count > 0 && now - _clicks.Peek().t > 5f) _clicks.Dequeue();

                float disp = DisplacementOverWindow(now);
                if (disp < 0f) return;   // history not deep enough yet

                bool wasdFrozen  = _wasdHeldSince >= 0f && now - _wasdHeldSince >= WindowSec && disp < MinMoveM;
                // Round-107: only WORLD clicks are movement intent. Clicking UI is the OPPOSITE of
                // trying to walk somewhere, and counting it manufactured "STUCK" episodes for anyone
                // browsing their phone while standing still. Field case: episode 1 was genuine
                // (6 clicks, only 1 on UI → 5 real click-to-move attempts that produced no path),
                // while episodes 2-3 were all-UI and told us nothing.
                // Threshold stays at SIX (unchanged from before) — dropping it to 4 was my error:
                // world clicks are not all movement intent either (shelves, items, NPCs, vehicles
                // are all clicked while standing still), so a lower bar trades one false-positive
                // source for another. Excluding UI clicks at the SAME threshold is strictly more
                // conservative than the original behaviour.
                int worldClicks = 0;
                foreach (var c in _clicks) if (!c.overUi) worldClicks++;
                bool clickFrozen = worldClicks >= 6 && disp < MinMoveM;

                // Round-185: rate-limited, not lifetime-starved — a start inside the gap is simply
                // deferred (a genuinely stuck player is still stuck next tick and opens late).
                if (!_inEpisode && (wasdFrozen || clickFrozen) && now - _lastEpisodeStartAt >= MinEpisodeGapSec)
                {
                    _inEpisode = true; _episodeStart = now; _episodesLogged++; _lastEpisodeStartAt = now;
                    LogSnapshot(wasdFrozen ? $"move-input held {now - _wasdHeldSince:F1}s" : "click-spam", pos, mag);
                }
                else if (_inEpisode && disp >= MinMoveM)
                {
                    EndEpisode("moving again");
                }
                else if (_inEpisode && now >= _nextStuckBeatAt && now - _episodeStart >= 2f)
                {
                    // Round-175: DURING-EPISODE trail (every 2s, max 6 beats).  The 11s field episode
                    // left no evidence of what held the player between STUCK and its closer — this
                    // names the wider candidate states while the freeze is live.  (A legit lock can
                    // never be active here: it would have ended the episode above.)
                    _nextStuckBeatAt = now + 2f;
                    if (_beatsThisEpisode < 6)
                    {
                        _beatsThisEpisode++;
                        string act = ""; try { act = MPRestSync.CurrentActivityName() ?? ""; } catch { }
                        string agent = "?";
                        try
                        {
                            var a = Helpers.PlayerHelper.PlayerController?.Character?.navmeshAgent;
                            agent = a == null ? "null"
                                : $"vel={a.velocity.magnitude:F2} hasPath={a.hasPath} stopped={a.isStopped}";
                        }
                        catch { }
                        Plugin.Logger.LogWarning($"[MoveFreeze] still stuck {now - _episodeStart:F0}s: activity='{act}' agent[{agent}] timeScale={Time.timeScale:F2} inputMag={ReadMoveMagnitude():F2}");
                    }
                }
            }
            catch { }
        }

        private static bool  _capAnnounced;
        private static float _nextStuckBeatAt;
        private static int   _beatsThisEpisode;

        private static void EndEpisode(string why)
        {
            if (!_inEpisode) return;
            _inEpisode = false;
            _beatsThisEpisode = 0; _nextStuckBeatAt = 0f;
            Plugin.Logger.LogWarning($"[MoveFreeze] resolved after {Time.unscaledTime - _episodeStart:F0}s ({why}).");
        }

        /// <summary>Distance moved over the last ~3s; -1 while history is short.</summary>
        private static float DisplacementOverWindow(float now)
        {
            if (_ringN < _ring.Length) return -1f;
            int head = (_ringN - 1) % _ring.Length;
            // Oldest sample at least WindowSec old.
            for (int back = _ring.Length - 1; back >= 1; back--)
            {
                int i = (head - back + _ring.Length * 2) % _ring.Length;
                if (now - _ringT[i] >= WindowSec) return Vector3.Distance(_ring[head], _ring[i]);
            }
            return -1f;
        }

        private static float ReadMoveMagnitude()
        {
            try
            {
                if (!_inputResolved)
                {
                    _inputResolved = true;
                    var t = VehicleManager.FindGameType("PlayerAction");
                    if (t == null)
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            try { foreach (var x in asm.GetTypes()) if (x != null && x.Name == "PlayerAction") { t = x; break; } } catch { }
                            if (t != null) break;
                        }
                    if (t != null)
                    {
                        var m = HarmonyLib.AccessTools.Property(t, "Move")?.GetValue(null)
                             ?? HarmonyLib.AccessTools.Field(t, "Move")?.GetValue(null);
                        if (m != null)
                        {
                            _vectorMi = m.GetType().GetMethod("Vector", Type.EmptyTypes);
                            if (_vectorMi != null) _moveAction = m;
                        }
                    }
                    Plugin.Logger.LogInfo($"[MoveFreeze] input source: {(_moveAction != null ? "PlayerAction.Move" : "raw keys (PlayerAction unresolved — dump gap)")}.");
                }
                if (_moveAction != null && _vectorMi != null)
                {
                    var v = _vectorMi.Invoke(_moveAction, null);
                    if (v is Vector2 v2) return v2.magnitude;
                }
            }
            catch { }
            // Fallback: raw keys — misses rebinds/gamepad but never lies positive.
            return Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.D)
                || Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.RightArrow) ? 1f : 0f;
        }

        private static void LogSnapshot(string trigger, Vector3 pos, float inputMag)
        {
            // Every read individually guarded — the snapshot must never be the crash.
            string sel = "null";
            try
            {
                var cte = MouseController.currentTargetEntity;
                if ((object?)cte != null) sel = cte == null ? $"DESTROYED-LEFTOVER({cte.GetType().Name})" : cte.GetType().Name;   // Unity fake-null = H1 residue
            }
            catch { sel = "?"; }
            bool overUi = false; string selectedGo = "none";
            try { overUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(); } catch { }
            try { var go = EventSystem.current?.currentSelectedGameObject; if (go != null) selectedGo = go.name; } catch { }
            int clicksOverUi = 0; foreach (var c in _clicks) if (c.overUi) clicksOverUi++;
            string nav = "?";
            try
            {
                var pc = InstanceBehavior<GameManager>.Instance?.playerController;
                var ch = pc == null ? null : HarmonyLib.AccessTools.Property(pc.GetType(), "Character")?.GetValue(pc)
                                          ?? HarmonyLib.AccessTools.Field(pc.GetType(), "Character")?.GetValue(pc);
                var agent = ch == null ? null : HarmonyLib.AccessTools.Field(ch.GetType(), "navmeshAgent")?.GetValue(ch) as UnityEngine.AI.NavMeshAgent;
                if (agent != null) nav = $"enabled={agent.enabled} onNavMesh={agent.isOnNavMesh} stopped={agent.isStopped} hasPath={agent.hasPath}";
            }
            catch { }
            bool dialog = false; try { dialog = DialogController.current != null; } catch { }
            bool inside = false; try { inside = BuildingManager.IsInsideBuilding; } catch { }
            Plugin.Logger.LogWarning(
                $"[MoveFreeze] STUCK ({trigger}) pos={pos.x:F1},{pos.y:F1},{pos.z:F1} inputMag={inputMag:F2} " +
                $"clicks5s={_clicks.Count}(overUI={clicksOverUi}) pointerOverUI={overUi} selectedGO='{selectedGo}' " +
                $"selection={sel} nav[{nav}] dialog={dialog} inside={inside} shop='{MPRegisterSync.CurrentShopAddress}' timeScale={Time.timeScale:F2}");
        }
    }
}
