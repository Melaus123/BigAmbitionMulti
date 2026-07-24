using System;
using HarmonyLib;
using BigAmbitions.Items;   // CargoInstance, ItemInstance
using Helpers;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Permanent field guard (round-67, promoted from the DepositMerge probe): any NATIVE cargo merge
    /// INTO an interior item of a building the local player doesn't rent is an UNROUTED deposit — the
    /// class of silent replica divergence behind savvyfish 20260722-200341 (goods leave the owner's
    /// storage via routed takes, deposits land only on the guest's replica, the owner's cargo shield
    /// then discards them → inventory evaporates). Every legit helper deposit routes via BStore ops
    /// and never runs these primitives against a foreign interior item, so a single WARN here names
    /// the leaking call path (short stack) in the log AND in every field report's ring buffer.
    /// Cost: two cheap gate checks per call; the expensive work (stack walk, string build) runs only
    /// on a hit, which healthy code never produces. 2026-07-24 two-instance verification: zero hits
    /// across flatbed/hand-truck/hands deposits, takes, and dropdown picks.
    /// </summary>
    internal static class DepositGuard
    {
        private static float _nextLog;   // 5s throttle — a leaking loop shouldn't flood the ring buffer

        internal static void Check(string via, ItemInstance? dest, CargoInstance? inc)
        {
            try
            {
                if (dest == null) return;
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                if (reg == null || reg.RentedByPlayer) return;                    // own building → native is legit
                string id = dest.id?.ToString() ?? "";
                if (reg.itemInstances == null || id.Length == 0 || !reg.itemInstances.ContainsKey(id)) return;   // held boxes / vehicles are legit local targets
                if (UnityEngine.Time.unscaledTime < _nextLog) return;
                _nextLog = UnityEngine.Time.unscaledTime + 5f;
                Plugin.Logger.LogWarning(
                    $"[DepositGuard] UNROUTED native {via} into interior item '{dest.itemName}' id={id} "
                    + $"@'{GameStateReader.AddressKey(reg)}' inc='{inc?.itemName}'x{inc?.amount} — path: {ShortStack()}");
            }
            catch { }
        }

        private static string ShortStack()
        {
            try
            {
                var st = new System.Diagnostics.StackTrace(2, fNeedFileInfo: false);
                var sb = new System.Text.StringBuilder();
                int shown = 0;
                for (int i = 0; i < st.FrameCount && shown < 7; i++)
                {
                    var m = st.GetFrame(i)?.GetMethod();
                    if (m == null) continue;
                    string dt = m.DeclaringType?.Name ?? "?";
                    if (dt.Contains("DepositGuard")) continue;
                    sb.Append($"{dt}.{m.Name} ← ");
                    shown++;
                }
                return sb.ToString();
            }
            catch { return "stack-unavailable"; }
        }
    }

    [HarmonyPatch(typeof(ItemInstance), nameof(ItemInstance.TryToAddToCargo))]
    public static class Patch_DepositGuard_TryToAdd
    {
        static void Prefix(ItemInstance __instance, CargoInstance cargoInstance)
            => DepositGuard.Check("TryToAddToCargo", __instance, cargoInstance);
    }

    // 3-param overload only: the 2-param overload delegates to it (ItemInstance.cs:263-266).
    [HarmonyPatch(typeof(ItemInstance), nameof(ItemInstance.MergeCargo),
                  typeof(CargoInstance), typeof(CargoInstance), typeof(int))]
    public static class Patch_DepositGuard_Merge
    {
        static void Prefix(ItemInstance __instance, CargoInstance fromCargoInstance)
            => DepositGuard.Check("MergeCargo", __instance, fromCargoInstance);
    }
}
