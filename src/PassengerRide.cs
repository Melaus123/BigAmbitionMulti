using System.Collections.Generic;
using UnityEngine;
using Helpers;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Passenger ("ride shotgun") gameplay layer. Consumes the host-authoritative occupancy +
    /// locks in <see cref="PassengerSync"/> and turns them into the visible ride: the LOCAL
    /// player walking to a ghost car's passenger door, pinning into the seat (own model hidden,
    /// the camera keeps following the pinned character), and stepping out beside the car; plus
    /// rendering OTHER players who are riding (their avatar pinned to the seat, nametag floating).
    /// The feature ships — only the F7 board/exit TEST trigger is dev-gated, a stand-in until the
    /// in-car "Ride" CTA lands. Driven once per frame from MPCanvasUI.Update. See
    /// docs/PASSENGER-SYSTEM.md.
    /// </summary>
    internal static class PassengerRide
    {
        private const float BoardRange = 7f;   // metres: how close to a ghost F7 will board

        // ── local ride ────────────────────────────────────────────────────────
        private static string _localVeh  = "";    // what we're currently rendering as ridden ("" = on foot)
        private static int    _localSeat = -1;
        private static bool   _pinned;            // arrived at the seat and locked to it
        private static float  _pinFallback;       // unscaled-time deadline to pin if the walk never arrives
        private static GameObject? _hiddenModel;  // our "Model" child we hid (restore on exit)

        // ── remote riders we've pinned (pid → vehicleId) ────────────────────
        private static readonly Dictionary<string, string> _remotePinned = new();

        public static void Update()
        {
            if (!MPServer.IsRunning && !MPClient.IsConnected)
            {
                if (_localVeh != "") EndLocalRide(beside: false);
                if (_remotePinned.Count > 0) ClearRemotePins();
                return;
            }

            try
            {
#if BAMP_DEV
                // DIAG:INVESTIGATION(passenger-ride) — F7 boards the nearest unlocked ghost in
                //   range (or exits if already aboard). Stand-in until the in-car "Ride" CTA lands.
                if (Input.GetKeyDown(KeyCode.F7)) ToggleBoardNearest();
#endif
                TickLocalRide();
                TickRemoteRiders();
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Ride] Update: {ex.Message}"); }
        }

        // ── board / exit initiation ──────────────────────────────────────────
#if BAMP_DEV
        private static void ToggleBoardNearest()
        {
            if (PassengerSync.IsRiding(MPConfig.PlayerId)) { RequestExit(); return; }

            Vector3 me = LocalPos();
            string best = ""; float bestSq = BoardRange * BoardRange;
            foreach (var (vid, t) in VehicleManager.AllGhosts())
            {
                if (t == null) continue;
                float d = (t.position - me).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = vid; }
            }
            if (best == "") { Plugin.Logger.LogInfo($"[Ride] F7: no ghost car within {BoardRange}m."); return; }
            Plugin.Logger.LogInfo($"[Ride] F7: requesting board of '{best}'.");
            RequestBoard(best);
        }
#endif

        private static void RequestBoard(string vehicleId)
        {
            if (MPServer.IsRunning) MPServer.HostBoardRequest(vehicleId);
            else                    MPClient.SendBoardRequest(vehicleId);
        }

        private static void RequestExit()
        {
            string vid = PassengerSync.LocalRidingVehicleId;
            if (string.IsNullOrEmpty(vid)) return;
            if (MPServer.IsRunning) MPServer.HostExit(vid);
            else                    MPClient.SendPassengerExit(vid);
        }

        // ── local ride state machine (driven by PassengerSync.LocalRidingVehicleId) ──
        private static void TickLocalRide()
        {
            string target = PassengerSync.LocalRidingVehicleId;

            if (target != _localVeh)
            {
                if (string.IsNullOrEmpty(target)) EndLocalRide(beside: true);
                else BeginLocalRide(target, PassengerSync.LocalSeat);
            }

            if (_localVeh == "") return;

            var ghost = VehicleManager.GhostTransform(_localVeh);
            if (ghost == null) { EndLocalRide(beside: false); return; }   // car despawned under us

            if (!_pinned && Time.unscaledTime >= _pinFallback) StartPin();
            if (_pinned) PinLocalToSeat(ghost, _localSeat);
        }

        private static void BeginLocalRide(string vehicleId, int seat)
        {
            _localVeh = vehicleId; _localSeat = seat; _pinned = false;
            _pinFallback = Time.unscaledTime + 4f;   // pin anyway if the walk never arrives

            var pc = PlayerHelper.PlayerController;
            var ghost = VehicleManager.GhostTransform(vehicleId);
            if (pc == null || ghost == null) { StartPin(); return; }
            try
            {
                Vector3 door = ghost.TransformPoint(DoorLocal(seat));
                pc.SetGoal(door, new UnityEngine.Events.UnityAction(StartPin));   // walk; pin on arrival
            }
            catch { StartPin(); }
        }

        private static void StartPin()
        {
            if (_pinned || _localVeh == "") return;
            _pinned = true;
            try
            {
                var ch = PlayerHelper.PlayerController?.Character;
                if (ch == null) return;
                var agent = ch.navmeshAgent;
                if (agent != null)
                    try { agent.updatePosition = false; agent.updateRotation = false; agent.isStopped = true; } catch { }
                _hiddenModel = ch.transform.Find("Model")?.gameObject;   // don't render ourselves standing in the seat
                if (_hiddenModel != null) _hiddenModel.SetActive(false);
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Ride] StartPin: {ex.Message}"); }
        }

        private static void PinLocalToSeat(Transform ghost, int seat)
        {
            try
            {
                var ch = PlayerHelper.PlayerController?.Character;
                if (ch == null) return;
                ch.transform.position = ghost.TransformPoint(SeatLocal(seat));
                ch.transform.rotation = Quaternion.Euler(0f, ghost.eulerAngles.y, 0f);
            }
            catch { }
        }

        private static void EndLocalRide(bool beside)
        {
            if (_localVeh == "") return;
            try
            {
                var pc = PlayerHelper.PlayerController;
                var ch = pc?.Character;
                var ghost = VehicleManager.GhostTransform(_localVeh);

                if (_hiddenModel != null) { try { _hiddenModel.SetActive(true); } catch { } _hiddenModel = null; }

                if (ch != null)
                {
                    var agent = ch.navmeshAgent;
                    Vector3 outPos = (beside && ghost != null) ? ghost.TransformPoint(DoorLocal(_localSeat)) : ch.transform.position;
                    if (agent != null)
                    {
                        try { agent.updatePosition = true; agent.updateRotation = true; agent.isStopped = false; } catch { }
                        try { agent.Warp(outPos); } catch { ch.transform.position = outPos; }
                    }
                    else ch.transform.position = outPos;
                }
                try { pc?.ResetNavigation(); } catch { }
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Ride] EndLocalRide: {ex.Message}"); }
            _localVeh = ""; _localSeat = -1; _pinned = false;
        }

        // ── remote riders rendering ──────────────────────────────────────────
        private static void TickRemoteRiders()
        {
            var want = new Dictionary<string, string>();   // pid → vid (remote riders this frame)
            foreach (var vid in PassengerSync.OccupiedVehicleIds())
            {
                // Resolve the vehicle's transform on THIS machine: another player's car is a ghost,
                // but the OWNER's own car is the REAL native vehicle (not a ghost) — riders of it
                // must be pinned to it too, else the passenger never sits on the owner's screen
                // (and, pre-trigger-fix, kept shoving the car).
                Transform? veh = VehicleManager.GhostTransform(vid) ?? VehicleManager.LocalVehicleTransform(vid);
                if (veh == null) continue;
                var riders = PassengerSync.RidersOf(vid);
                if (riders == null) continue;
                foreach (var kv in riders)
                {
                    string pid = kv.Value;
                    if (string.IsNullOrEmpty(pid) || pid == MPConfig.PlayerId) continue;   // local ride handles us
                    want[pid] = vid;
                    try
                    {
                        RemotePlayerManager.SetDriving(pid, true);                // hide their walk model
                        RemotePlayerManager.SetRide(pid, veh, SeatLocal(kv.Key)); // pin (nametag) to the seat
                    }
                    catch { }
                }
            }
            // Release anyone who stopped riding since last frame.
            if (_remotePinned.Count > 0)
            {
                List<string>? stale = null;
                foreach (var kv in _remotePinned)
                    if (!want.ContainsKey(kv.Key)) (stale ??= new List<string>()).Add(kv.Key);
                if (stale != null)
                    foreach (var pid in stale)
                    {
                        try { RemotePlayerManager.SetRide(pid, null, Vector3.zero); RemotePlayerManager.SetDriving(pid, false); } catch { }
                        _remotePinned.Remove(pid);
                    }
            }
            foreach (var kv in want) _remotePinned[kv.Key] = kv.Value;
        }

        private static void ClearRemotePins()
        {
            foreach (var kv in _remotePinned)
                try { RemotePlayerManager.SetRide(kv.Key, null, Vector3.zero); RemotePlayerManager.SetDriving(kv.Key, false); } catch { }
            _remotePinned.Clear();
        }

        // ── seat / door geometry (generic local-space; refine per car later) ─
        // Car frame: +x right, +z forward. Seat 1 = front passenger (shotgun, right-front);
        // 2 = rear-left, 3 = rear-right. Doors sit just outside the body on the matching side.
        private static Vector3 SeatLocal(int seat) => seat switch
        {
            2 => new Vector3(-0.45f, 0.7f, -0.85f),
            3 => new Vector3( 0.45f, 0.7f, -0.85f),
            _ => new Vector3( 0.45f, 0.7f,  0.15f),   // 1 / default = shotgun
        };
        private static Vector3 DoorLocal(int seat) => seat switch
        {
            2 => new Vector3(-1.15f, 0f, -0.85f),
            3 => new Vector3( 1.15f, 0f, -0.85f),
            _ => new Vector3( 1.15f, 0f,  0.15f),
        };

        private static Vector3 LocalPos()
        {
            try { var ch = PlayerHelper.PlayerController?.Character; return ch != null ? ch.transform.position : Vector3.zero; }
            catch { return Vector3.zero; }
        }
    }
}
