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
        private const int   MaxEpisodes = 5;      // snapshots per session

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

        // PlayerAction.Move reflection (dump gap).
        private static bool _inputResolved;
        private static object? _moveAction;
        private static System.Reflection.MethodInfo? _vectorMi;

        public static void Reset()
        {
            _ringN = 0; _wasdHeldSince = -1f; _clicks.Clear();
            _inEpisode = false; _episodesLogged = 0;
        }

        public static void Tick()
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                if (_episodesLogged >= MaxEpisodes && !_inEpisode) return;

                // Legitimate locks: any of these and the player is EXPECTED to be
                // still — clear all state so a freeze can't be misattributed.
                bool legit = false;
                try { legit |= UI.Load.LoadScene.isLoading; } catch { }
                try { legit |= Helpers.VehicleHelper.IsInsideVehicle(); } catch { }
                try { legit |= Helpers.EnergyHelper.goingToHospital; } catch { }
                try { legit |= Time.timeScale == 0f; } catch { }
                try { legit |= EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null; } catch { }   // any focused input field: chat, popups, rename…
                if (legit) { EndEpisode("legit-lock"); _ringN = 0; _wasdHeldSince = -1f; _clicks.Clear(); return; }

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
                bool clickFrozen = _clicks.Count >= 6 && disp < MinMoveM;

                if (!_inEpisode && (wasdFrozen || clickFrozen))
                {
                    _inEpisode = true; _episodeStart = now; _episodesLogged++;
                    LogSnapshot(wasdFrozen ? $"move-input held {now - _wasdHeldSince:F1}s" : "click-spam", pos, mag);
                }
                else if (_inEpisode && disp >= MinMoveM)
                {
                    EndEpisode("moving again");
                }
            }
            catch { }
        }

        private static void EndEpisode(string why)
        {
            if (!_inEpisode) return;
            _inEpisode = false;
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
