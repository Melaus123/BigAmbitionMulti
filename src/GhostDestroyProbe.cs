using UnityEngine;

namespace BigAmbitionsMP
{
    // PROBE-START: P-GHOST-DESTROY (this whole file is the probe; also delete the four
    // sentinel-bracketed blocks in VehicleManager — the two marker attaches in SpawnRemoteVehicle
    // (player body + A2 look-alike) and the Expected.Add lines in DespawnByVehicleId / DespawnAll)
    /// <summary>Field 20260830-170317 (user-approved 2026-08-31): the CLIENT's ghost vehicles
    /// were destroyed and fleet-respawned several times (Micinox's van 3x, a handtruck 4x)
    /// with only ONE grant-change line in that log — so something OTHER than the mod's two
    /// despawn paths destroys ghost GameObjects on the client, and it is silent (no exception,
    /// no mod line). Candidates: native world streaming, ParkingLaneGenerator regeneration,
    /// the delivery-job lifecycle. This names the event so the next bundle carries it.
    ///
    /// HOW: every fleet ghost gets this witness component at spawn. The mod's OWN despawn
    /// paths add the id to <see cref="Expected"/> right before they Destroy (those paths log
    /// themselves) — so an OnDestroy that arrives UNMARKED is a foreign destruction, and one
    /// WARN line records the context. Unity defers Destroy to end of frame, so a stack trace
    /// here would name the destruction pump, not the caller — context correlation (what was
    /// happening this frame: current building, loading state, position) is the honest signal.
    ///
    /// Log-only; no behavior. Capped at 20 unexpected lines per launch (a scene unload that
    /// bypasses DespawnAll would otherwise print one line per live ghost). MP-only by
    /// construction (ghosts exist only in MP sessions).</summary>
    public sealed class GhostDestroyMarker : MonoBehaviour
    {
        public string VehicleId = "";
        public string TypeName = "";
        public string OwnerId = "";

        /// <summary>Ids the mod's own despawn paths are about to destroy — consumed on OnDestroy.</summary>
        public static readonly System.Collections.Generic.HashSet<string> Expected = new();

        private static int _logged;

        private void OnDestroy()
        {
            try
            {
                if (Expected.Remove(VehicleId)) return;   // mod-initiated — that path logged already
                if (_logged >= 20) return;
                _logged++;
                string inside = ""; bool inBuilding = false;
                try { inBuilding = BuildingManager.IsInsideBuilding; } catch { }
                try
                {
                    var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                    if (reg != null && inBuilding) inside = GameStateReader.AddressKey(reg);
                }
                catch { }
                Vector3 p = Vector3.zero; try { p = transform.position; } catch { }
                Plugin.Logger.LogWarning(
                    $"[PROBE] GhostDestroy: ghost '{TypeName}' of '{OwnerId}' ({VehicleId}) destroyed OUTSIDE the mod's despawn paths "
                    + $"— pos=({p.x:F0},{p.y:F0},{p.z:F0}) inside='{inside}' IsInsideBuilding={inBuilding} frame={Time.frameCount} "
                    + $"t={Time.unscaledTime:F1}s. The fleet will respawn it within ~1s; the correlation target is whatever this frame "
                    + $"was doing (streaming, lane regen, job lifecycle). #{_logged}/20");
            }
            catch { }
        }
    }
    // PROBE-END: P-GHOST-DESTROY
}
