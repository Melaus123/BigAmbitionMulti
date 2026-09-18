using System;
using HarmonyLib;
using Buildings;
using Helpers;

namespace BigAmbitionsMP
{
    /// <summary>CINEMA-1 (2026-09-17). USER RULING: a guest who holds business permission here — and a merger
    /// co-member — is NEVER charged and has the SAME access as the owner. The cinema/theater is the one business
    /// the game fences off with a physical barrier: TicketEntryBlocker.UpdateBlockers (decompile :28-41) raises
    /// every blocker's BoxCollider whenever <c>!BuildingManager.IsPlayerOwnedBusiness</c> (= the registration's
    /// <c>RentedByPlayer</c>, BuildingManager.cs:184) and the business is a cinema or a theater, so a permitted
    /// guest walked into an invisible wall in a shop they are allowed to run. This file answers the whole family
    /// the same way the rest of the mod does: reuse the existing "am I a permitted helper here" predicate
    /// (BusinessHelperRoute.HelperHere) plus the merger test, and either drop the barrier (the blocker) or open a
    /// SCOPED ownership flip around the one native statement that prices a ticket (the booth and the kiosk).
    ///
    /// NO NEW ON-SCREEN TEXT (standing rule): the barrier is simply not there and the ticket simply costs 0.</summary>
    internal static class CinemaGuestParity
    {
        private const string Tag = "[Cinema]";

        private static readonly System.Collections.Generic.HashSet<string> _liftedLogged =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        private static readonly System.Collections.Generic.HashSet<string> _freeLogged =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        /// <summary>The local player stands in a business they may run: a BUSINESS PERMISSION (helper grant —
        /// BusinessHelperRoute.HelperHere, the same predicate the warning-icon parity uses) or a merger
        /// co-member's shop (the AccessGates.PartnerShop test: the merger flip parks the runner stamp, and the
        /// absence simulation covers a shop this machine runs for an absent member).</summary>
        internal static bool PermittedHere(out string addr)
        {
            addr = "";
            try
            {
                if (BusinessHelperRoute.HelperHere(out addr)) return true;
                addr = "";
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return false;   // review L7
                var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                if (reg == null) return false;
                addr = GameStateReader.AddressKey(reg);
                if (string.IsNullOrEmpty(addr)) return false;
                if (GameStatePatcher.IsForeignPlayerBusiness(reg)) return true;
                return (MergerFlip.FlippedCount > 0 && MergerFlip.IsFlipped(addr)) || MergerAbsence.SimulatesHere(addr);
            }
            catch { addr = ""; return false; }
        }

        // ── Scoped ownership flip (the HousingFurniture pattern) ─────────────────────────────────────────────
        // The two ticket prices are single native statements — `IsPlayerOwnedBusiness ? 0f : GetPriceOnCurrentBusiness(...)`
        // (TicketBoothController.cs:58, TicketKioskController.cs:179). Mark the CURRENT building rented-by-us for
        // exactly the call that builds that CargoInstance and restore it the instant the call returns, so nothing
        // else ever reads the guest as the owner. Depth-counted, so nesting is safe.
        private static BuildingRegistration? _forced;
        private static bool _savedValue;
        private static int  _depth;

        internal static void Enter(string site)
        {
            try
            {
                if (_depth == 0)
                {
                    _forced = null;
                    if (PermittedHere(out var addr))
                    {
                        var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                        if (reg != null)
                        {
                            _forced = reg; _savedValue = reg.RentedByPlayer; reg.RentedByPlayer = true;
                            if (_freeLogged.Add(site + "|" + addr))
                                Plugin.Logger.LogInfo($"{Tag} {site}: a permitted guest is not charged for a ticket at {addr} (CINEMA-1).");
                        }
                    }
                }
                _depth++;
            }
            catch { }
        }

        internal static void Exit()
        {
            try
            {
                if (_depth > 0) _depth--;
                if (_depth == 0 && _forced != null) { _forced.RentedByPlayer = _savedValue; _forced = null; }
            }
            catch { _depth = 0; _forced = null; }
        }

        internal static void NoteBarrierLifted(string addr)
        {
            try
            {
                if (_liftedLogged.Add(addr))
                    Plugin.Logger.LogInfo($"{Tag} ticket barrier lifted for a permitted guest at {addr} (CINEMA-1).");
            }
            catch { }
        }
    }

    /// <summary>CINEMA-1 C1 — the barrier itself. The native method decides ONE bool for every blocker in the
    /// scene and writes it to each one's collider (:39-41); a Postfix that clears the collider again for a
    /// permitted guest is the smallest possible answer and leaves the owner/customer paths untouched.</summary>
    [HarmonyPatch(typeof(global::Buildings.Retail.Businesses.CinemaTheater.TicketEntryBlocker),
                  nameof(global::Buildings.Retail.Businesses.CinemaTheater.TicketEntryBlocker.UpdateBlockers))]
    public static class Patch_TicketEntryBlocker_PermittedGuest
    {
        // AllInstances is a private static property on the blocker — read it by reflection so a rename in a game
        // update costs the feature, not a compile break, and says so once.
        private static readonly System.Reflection.PropertyInfo _pAllInstances =
            AccessTools.Property(typeof(global::Buildings.Retail.Businesses.CinemaTheater.TicketEntryBlocker), "AllInstances");
        private static bool _reflectionMissLogged;

        static void Postfix()
        {
            try
            {
                if (!CinemaGuestParity.PermittedHere(out var addr)) return;
                if (_pAllInstances == null)
                {
                    if (!_reflectionMissLogged)
                    {
                        _reflectionMissLogged = true;
                        Plugin.Logger.LogWarning("[Cinema] ticket barrier: TicketEntryBlocker.AllInstances was not found — a permitted guest may still be blocked (CINEMA-1).");
                    }
                    return;
                }
                var list = _pAllInstances.GetValue(null) as System.Collections.IEnumerable;
                if (list == null) return;
                int lifted = 0;
                foreach (var entry in list)
                {
                    var blocker = entry as UnityEngine.MonoBehaviour;
                    if (blocker == null) continue;
                    // Awake caches GetComponent<BoxCollider>() into the private _boxCollider — the same component
                    // this returns, so no reflection is needed for the collider itself.
                    var col = blocker.GetComponent<UnityEngine.BoxCollider>();
                    if (col != null && col.enabled) { col.enabled = false; lifted++; }
                }
                if (lifted > 0) CinemaGuestParity.NoteBarrierLifted(addr);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Cinema] ticket barrier (CINEMA-1): {ex.Message}"); }
        }
    }

    /// <summary>CINEMA-1 C2 — the STAFFED ticket booth's price. OpenOrderUI (decompile TicketBoothController.cs:55-60)
    /// builds the one CargoInstance at <c>IsPlayerOwnedBusiness ? 0f : GetPriceOnCurrentBusiness(...)</c>; with the
    /// scoped flip open the permitted guest gets the owner's 0. Everything after this (the purchase UI callback,
    /// Order.Pay, the ticket flag at Order.cs:56) then runs NATIVELY on a zero-priced order, so no money moves and
    /// the guest still holds a valid ticket.</summary>
    [HarmonyPatch(typeof(global::Controllers.TicketBoothController), "OpenOrderUI")]
    public static class Patch_TicketBoothController_OpenOrderUI_FreeForPermitted
    {
        static void Prefix()    { CinemaGuestParity.Enter("ticket booth"); }
        static void Finalizer() { CinemaGuestParity.Exit(); }
    }

    /// <summary>Review M2: the purchase UI is asynchronous - the confirm callback OnPlaceOrder (decompile
    /// TicketBoothController.cs:95-125) runs long after OpenOrderUI returned and branches on IsPlayerOwnedBusiness
    /// at :111. Without the flip a permitted guest at a booth whose employee is off shift fell into the customer
    /// line nobody serves, Order.Pay never ran and the ticket flag was never set. Same scoped flip around it.</summary>
    [HarmonyPatch(typeof(global::Controllers.TicketBoothController), "OnPlaceOrder")]
    public static class Patch_TicketBoothController_OnPlaceOrder_OwnerPath
    {
        static void Prefix()    { CinemaGuestParity.Enter("ticket booth order"); }
        static void Finalizer() { CinemaGuestParity.Exit(); }
    }

    /// <summary>CINEMA-1 C2 — the SELF-SERVICE ticket kiosk's price (TicketKioskController.cs:176-182), the same
    /// single statement and the same scoped flip.</summary>
    [HarmonyPatch(typeof(global::Controllers.TicketKioskController), "OpenOrderUI")]
    public static class Patch_TicketKioskController_OpenOrderUI_FreeForPermitted
    {
        static void Prefix()    { CinemaGuestParity.Enter("ticket kiosk"); }
        static void Finalizer() { CinemaGuestParity.Exit(); }
    }
}
