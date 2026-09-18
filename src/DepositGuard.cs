using System;
using HarmonyLib;
using BigAmbitions.Items;   // CargoInstance, ItemInstance
using Helpers;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Permanent field guard (round-67, promoted from the DepositMerge probe; ROUTING added by TILL-PUT-1):
    /// any NATIVE cargo merge
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
        // PUT-PERM-1 (A3, 2026-09-18): the old 5 s throttle hid the SHAPE of a leak — one line, then
        // silence while the rest of the drag leaked unrecorded. One line per (destination id + item
        // name) instead, carrying the running total and the reason every route above declined, capped
        // at 80 lines a session so a pathological loop still cannot flood the ring buffer.
        private static readonly System.Collections.Generic.HashSet<string> _unroutedSeen = new System.Collections.Generic.HashSet<string>();
        private static int _unroutedCount;
        private static int _unroutedLines;
        // TILL-PUT-1 (H3): ICargoHolder.TryToMergeAndMoveCargoBetweenHolders (asm ICargoHolder.cs:36,42) calls
        // MergeIntoCargo(c) and THEN TryToAddToCargo(c) for the SAME CargoInstance, so both prefixes below fire
        // on ONE deposit and we routed it twice (two puts; the helper's hands drained twice, StorageSync.cs:1611).
        // Remember which instance we routed and on which frame; the second prefix of the pair then stands down.
        private static CargoInstance? _routedCargo;
        private static int            _routedFrame = -1;
        // TILL-PUT-1: ids we have already announced a routed deposit for (one INFO line per station).
        private static readonly System.Collections.Generic.HashSet<string> _routedLogged = new System.Collections.Generic.HashSet<string>();

        /// <summary>TILL-PUT-1 — returns TRUE to let the native primitive run, FALSE when this deposit has
        /// been ROUTED to the owner instead (the caller's prefix then skips the local merge).</summary>
        internal static bool Check(string via, ItemInstance? dest, CargoInstance? inc)
        {
            try
            {
                if (dest == null) return true;
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return true;
                var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                if (reg == null) return true;
                string id = dest.id?.ToString() ?? "";
                bool interior = reg.itemInstances != null && id.Length > 0 && reg.itemInstances.ContainsKey(id);   // held boxes / vehicles are legit local targets

                // PUT-PERM-1 (user ruling 2026-09-18) — an UNGRANTED visitor's deposit must be refused
                // before the native primitive runs, and it has to be decided BEFORE the own-building
                // early-out below (a partner's shop replica can read RentedByPlayer on this machine, and
                // that line used to wave the deposit straight through). Skipping the native leaves the
                // SOURCE cargo UNTOUCHED, which is what makes "the goods stay in hand" true: the only
                // caller that deletes the source, ICargoHolder.TryToMergeAndMoveCargoBetweenHolders
                // (asm ICargoHolder.cs:36-47), removes it when the merge ZEROED the source or when
                // TryToAddToCargo answered true — a skipped MergeCargo leaves the amount unchanged and a
                // skipped TryToAddToCargo leaves __result at default(false), so neither branch fires.
                if (interior && MPPatches.Patch_ItemController_TryToGrabItem_ForeignShopGate.RefusedForeignGrab(out string putOwner))
                {
                    MPPatches.Patch_ItemController_TryToGrabItem_ForeignShopGate.PutRefusalToast(putOwner, $"'{dest.itemName}'");
                    return false;
                }

                if (reg.RentedByPlayer) return true;              // own building → native is legit
                if (!interior) return true;

                // TILL-PUT-1 (round-67 → routed): warning alone was never enough.  A helper's hand/box deposit
                // into a SINGLE-SLOT station (cash register or producer — one stock CargoInstance, same data
                // shape) landed on the local replica only; the owner's next cargo statement is ABSOLUTE
                // (GameStatePatcher's cargo apply) and overwrote the slot, so the goods evaporated.  The routed
                // refill the stock dropdown already uses covers exactly this shape, so send it there.
                if (dest.cargoInstances != null && dest.cargoInstances.Count == 1
                    && BusinessHelperRoute.HelperHere(out var addr))
                {
                    // H3: the other half of this deposit's pair already routed this very CargoInstance this
                    // frame — swallow it, but do NOT route (and so do not put) a second time.
                    if (inc != null && ReferenceEquals(inc, _routedCargo) && UnityEngine.Time.frameCount == _routedFrame)
                        return false;

                    // H4: RouteStationRefill never reads `inc` — it pours from the helper's HANDS or, empty-handed,
                    // the CURRENT VEHICLE (BusinessPatches.cs:53-57). Routing a deposit that came from anywhere
                    // else would send the WRONG goods, so route only when `inc` is by reference one of the
                    // instances that selection will read; otherwise keep the old behaviour (warn, native runs).
                    if (IsRoutableSource(inc))
                    {
                        ItemController? ctrl = null;
                        try { ctrl = ItemHelper.GetItemControllerByID(dest.id); } catch { }
                        // M1: only a REAL put (Routed) may stand in for the native merge — Handled is a toast and
                        // NotApplicable moved nothing, and skipping native on either loses the deposit outright.
                        if (ctrl != null
                            && BusinessHelperRoute.RouteStationRefill(ctrl, addr) == BusinessHelperRoute.RefillResult.Routed)
                        {
                            _routedCargo = inc; _routedFrame = UnityEngine.Time.frameCount;
                            if (_routedLogged.Add(id))
                                Plugin.Logger.LogInfo($"[DepositGuard] helper deposit into '{dest.itemName}' @{addr} routed to the owner (TILL-PUT-1)");
                            return false;   // skip the native LOCAL merge; the owner's OK is what consumes our held amount
                        }
                    }
                }

                _unroutedCount++;
                if (_unroutedSeen.Add(id + "|" + (inc?.itemName ?? "")) && _unroutedLines++ < 80)
                {
                    // A3: name WHY every route above declined, so the log distinguishes "no helper here"
                    // from "wrong shape" from "ungranted but not a foreign business" without a rebuild.
                    bool rented = false; try { rented = reg.RentedByPlayer; } catch { }
                    bool granted = false;
                    try
                    {
                        string shopOwner = MPRegisterSync.CurrentShopOwner;
                        granted = shopOwner.Length > 0 && GrantSync.IsGranted(GrantKind.Business, shopOwner, MPConfig.PlayerId);
                    }
                    catch { }
                    bool helper = false; try { helper = BusinessHelperRoute.HelperHere(out _); } catch { }
                    int slots = dest.cargoInstances != null ? dest.cargoInstances.Count : -1;
                    Plugin.Logger.LogWarning(
                        $"[DepositGuard] UNROUTED native {via} into interior item '{dest.itemName}' id={id} "
                        + $"@'{GameStateReader.AddressKey(reg)}' inc='{inc?.itemName}'x{inc?.amount} (#{_unroutedCount}) "
                        + $"— declined: rented={rented} granted={granted} helper={helper} slots={slots} — path: {ShortStack()}");
                }
            }
            catch (Exception ex) { try { Plugin.Logger.LogWarning($"[DepositGuard] Check ({via}): {ex.Message}"); } catch { } }
            return true;
        }

        /// <summary>TILL-PUT-1 (H4) — true when `inc` is BY REFERENCE one of the cargo instances
        /// RouteStationRefill will pour from: the helper's held item, or, empty-handed, the current
        /// vehicle. Same selection as BusinessPatches.cs:53-57.</summary>
        private static bool IsRoutableSource(CargoInstance? inc)
        {
            if (inc == null) return false;
            try
            {
                ICargoHolder? holder = !PlayerHelper.IsHoldingItem
                    ? VehicleHelper.GetCurrentVehicle()
                    : (ICargoHolder?)PlayerHelper.ItemInstanceInHands;
                var src = holder?.GetCargoInstances();
                if (src == null) return false;
                foreach (var c in src) if (ReferenceEquals(c, inc)) return true;
            }
            catch { }
            return false;
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
        // Skipping leaves __result at default(false) = "it did not go in locally" — deliberate: the routed
        // op's owner-side OK is what consumes the held amount, so the caller must not also drop it (TILL-PUT-1).
        static bool Prefix(ItemInstance __instance, CargoInstance cargoInstance)
            => DepositGuard.Check("TryToAddToCargo", __instance, cargoInstance);
    }

    // 3-param overload only: the 2-param overload delegates to it (ItemInstance.cs:263-266).
    [HarmonyPatch(typeof(ItemInstance), nameof(ItemInstance.MergeCargo),
                  typeof(CargoInstance), typeof(CargoInstance), typeof(int))]
    public static class Patch_DepositGuard_Merge
    {
        static bool Prefix(ItemInstance __instance, CargoInstance fromCargoInstance)
            => DepositGuard.Check("MergeCargo", __instance, fromCargoInstance);
    }
}
