using System;
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>GUEST-PARITY-2 (2026-09-17). USER RULING, same as CINEMA-1: a permitted guest (a business
    /// helper grant, a merger co-member, or the offline fork of either) is NEVER charged in the business they
    /// are permitted in, and has the owner's access. The game prices those services from a single inline
    /// statement — <c>IsPlayerOwnedBusiness ? 0f : price</c> — so the answer is the same scoped ownership flip
    /// the cinema file uses: mark the CURRENT building rented-by-us for exactly the call that computes the
    /// price, and restore it the instant the call returns. Sites (decompile):
    ///   Controllers/HairdresserChairController.cs:191 in OnChangeHairButtonUpdate — the fee shown on the
    ///     "change hair" button (<c>_playerPrice</c>).
    ///   Controllers/HairdresserChairController.cs:246 in OnPlaceOrder — the OrderEntry prices for the
    ///     haircut / hair-chemical fees.
    ///   Character/PlayerDances.cs:63-65 StartOwnNightclubBoost — the happiness boost for dancing in your OWN
    ///     nightclub; a permitted guest running the club should get the same boost.
    /// No on-screen text — the price is simply 0 and the boost simply applies.
    ///
    /// NOTE (reported, not changed here): in an MP session with a player-owned shop the hairdresser's native
    /// OnPlaceOrder is SWALLOWED by MPPatches.Patch_MPOrderFinalizer, which computes the fees itself from
    /// ItemHelper.GetPriceOnCurrentBusiness and never reads IsPlayerOwnedBusiness — so the wrap below covers
    /// the native path only. Zeroing the guest's fee on the routed MP path is a MPPatches change, out of this
    /// file's scope.</summary>
    internal static class GuestParity2
    {
        private const string Tag = "[GuestParity2]";

        private static System.Reflection.MethodBase? Declared(Type t, string name)
        {
            try
            {
                foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                                             | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
                    if (m.Name == name && m.DeclaringType == t) return m;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} target {t.Name}.{name}: {ex.Message}"); }
            Plugin.Logger.LogWarning($"{Tag} {t.Name}.{name} NOT FOUND — that guest-parity site is inert (game update?).");
            return null;
        }

        internal static System.Reflection.MethodBase? HairPriceButton()
            => Declared(typeof(Controllers.HairdresserChairController), "OnChangeHairButtonUpdate");

        internal static System.Reflection.MethodBase? HairPlaceOrder()
            => Declared(typeof(Controllers.HairdresserChairController), "OnPlaceOrder");

        internal static System.Reflection.MethodBase? NightclubBoost()
            => Declared(typeof(Character.PlayerDances), "StartOwnNightclubBoost");
    }

    /// <summary>GUEST-PARITY-2 — the hairdresser fee PREVIEW on the change-hair button (decompile :191).</summary>
    [HarmonyPatch]
    public static class Patch_Hairdresser_ButtonPrice_GuestParity
    {
        static System.Reflection.MethodBase? TargetMethod() => GuestParity2.HairPriceButton();
        static void Prefix()
        {
            try { CinemaGuestParity.Enter("hairdresser-price"); }
            catch (Exception ex) { try { Plugin.Logger.LogWarning($"[GuestParity2] Patch_Hairdresser_ButtonPrice_GuestParity: {ex.Message}"); } catch { } }
        }
        static void Finalizer()
        {
            try { CinemaGuestParity.Exit(); }
            catch (Exception ex) { try { Plugin.Logger.LogWarning($"[GuestParity2] Patch_Hairdresser_ButtonPrice_GuestParity exit: {ex.Message}"); } catch { } }
        }
    }

    /// <summary>GUEST-PARITY-2 — the hairdresser ORDER prices (decompile :246). Native path only; the MP
    /// routed sale is swallowed upstream by Patch_MPOrderFinalizer (see the class note).</summary>
    [HarmonyPatch]
    public static class Patch_Hairdresser_OrderPrice_GuestParity
    {
        static System.Reflection.MethodBase? TargetMethod() => GuestParity2.HairPlaceOrder();
        static void Prefix()
        {
            try { CinemaGuestParity.Enter("hairdresser-order"); }
            catch (Exception ex) { try { Plugin.Logger.LogWarning($"[GuestParity2] Patch_Hairdresser_OrderPrice_GuestParity: {ex.Message}"); } catch { } }
        }
        static void Finalizer()
        {
            try { CinemaGuestParity.Exit(); }
            catch (Exception ex) { try { Plugin.Logger.LogWarning($"[GuestParity2] Patch_Hairdresser_OrderPrice_GuestParity exit: {ex.Message}"); } catch { } }
        }
    }

    /// <summary>GUEST-PARITY-2 — dancing in a nightclub you are permitted in earns the owner's boost
    /// (decompile Character/PlayerDances.cs:63-65).</summary>
    [HarmonyPatch]
    public static class Patch_PlayerDances_OwnClubBoost_GuestParity
    {
        static System.Reflection.MethodBase? TargetMethod() => GuestParity2.NightclubBoost();
        static void Prefix()
        {
            try { CinemaGuestParity.Enter("nightclub-dance-boost"); }
            catch (Exception ex) { try { Plugin.Logger.LogWarning($"[GuestParity2] Patch_PlayerDances_OwnClubBoost_GuestParity: {ex.Message}"); } catch { } }
        }
        static void Finalizer()
        {
            try { CinemaGuestParity.Exit(); }
            catch (Exception ex) { try { Plugin.Logger.LogWarning($"[GuestParity2] Patch_PlayerDances_OwnClubBoost_GuestParity exit: {ex.Message}"); } catch { } }
        }
    }
}
