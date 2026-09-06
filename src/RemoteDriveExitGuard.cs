using System;
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>H-VEH-DORMANT-1 r1 (bundle 20260905-234706). The game's VehicleController.OnExitBuilding
    /// (VehicleController.cs:188-201) destroys every vehicle whose saved Address equals the building the LOCAL player
    /// just walked out of — its model is "a car addressed to a building is inside it". A car a friend is driving right
    /// now is NOT inside it: the owner drove it in (address stamped, VehicleController.cs:392-397), the borrower drove
    /// it out (the game clears the address only for the car YOU drive out, BuildingManager.cs:1439/:1451/:1510), and
    /// the mod keeps the borrower's building in follow state only. So the owner walking out destroyed the live object
    /// and the car fell to the data-only path (Player.log:2377→2397 of the bundle). Skipping the cleanup for a car that
    /// is being driven remotely keeps the owner's live object; the release path (VehicleManager.ApplyStreetData at
    /// release) already records where the borrower left it. Attempt 1 of 2 for this symptom.</summary>
    [HarmonyPatch(typeof(VehicleController), "OnExitBuilding")]
    internal static class Patch_VehicleController_OnExitBuilding_RemoteDriveGuard
    {
        static bool Prefix(VehicleController __instance)
        {
            try
            {
                string? vid = __instance?.vehicleInstance?.id;
                if (!VehicleManager.IsDrivenRemotely(vid ?? "")) return true;   // not my car in a borrower's hands → the game's own cleanup runs
                Plugin.Logger.LogInfo($"[Drive] my car '{vid}' is being driven remotely — the exit-building cleanup is skipped for it (H-VEH-DORMANT-1).");
                return false;
            }
            catch { return true; }
        }
    }
}
