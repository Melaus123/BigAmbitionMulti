using System;
using HarmonyLib;
using Helpers;                       // VehicleHelper, PlayerHelper
using Player.HUD.ItemInfoOverlays;   // OverlayManager, CtaManager
using PlayerActivity;                // PlayerActivityUI

namespace BigAmbitionsMP
{
    // DIAG:INVESTIGATION(fridge-menu) — DIAGNOSTIC ONLY, REMOVE once the guest fridge menu is settled.
    //
    // Round-15: the guest fridge menu never opens after a deposit even though the pipeline verifiably
    // delivers the cargo (round-14 logs: coalesced push ✓, single clean destroy+respawn ✓, no exceptions ✓,
    // cargo present in the respawned instance's data). Static analysis of the click chain
    // (OnIoLeftClick → UpdateCta → ctaAction==null? → ShowDetailedOverlay → ShouldShowDetailedOverlay)
    // shows every gate covered by the guest wraps, so one of the links is failing in a way only runtime
    // state reveals (stale CTA action, overlay linked to the destroyed pre-respawn controller, dead
    // collider, …). These probes log each link for FRIDGES only; one guest test (deposit → click fridge)
    // pins the broken link:
    //   no [FridgeProbe] CLICK line   → the click never reaches the controller (hover/collider layer)
    //   CLICK with ctaActionNull=False → stale CTA action runs instead of the menu
    //   ShowDetailedOverlay → False    → the show-gate refused (cargo/type/wrap state in the same line)
    //   ShowDetailedOverlay → True     → menu was shown by the game but not visible (render/UI layer)

    [HarmonyPatch(typeof(EntityController), nameof(EntityController.OnIoLeftClick))]
    public static class Probe_Fridge_Click
    {
        static void Prefix(EntityController __instance)
        {
            if (!(__instance is FridgeController f)) return;
            try
            {
                var om = InstanceBehavior<OverlayManager>.Instance;
                Plugin.Logger.LogInfo($"[FridgeProbe] CLICK: cargo={f.ItemInstance?.cargoInstances?.Count ?? -1} " +
                    $"overlayOver={(om != null && om.IsShowingOverlayOverItem(f))} ctaKey='{CtaManager.ctaKey}' " +
                    $"ctaActionNull={CtaManager.ctaAction == null} enabled={f.primaryInteractionEnabled} visible={f.visible} " +
                    $"purch={f.playerItemPurchaserSettings?.enabled ?? false} activeVeh='{SaveGameManager.Current?.ActiveVehicleId ?? ""}'");
            }
            catch (Exception ex) { Plugin.Logger.LogInfo($"[FridgeProbe] CLICK probe: {ex.Message}"); }
        }
    }

    // The gate itself, with every input it reads — logs on ITEM entities so one guest click names the
    // exact failing condition (round-15: gate returned False with cargo=2 + the ownership wrap engaged;
    // remaining suspects: IsInsideMotorVehicle (stale ActiveVehicleId), activity panel, basket, or an
    // entity remap upstream in GetRelevantEntity).
    [HarmonyPatch(typeof(OverlayManager), "ShouldShowDetailedOverlay")]
    public static class Probe_Fridge_GateInputs
    {
        static void Postfix(EntityController entityController, bool __result)
        {
            try
            {
                if (!(entityController is ItemController)) return;
                if (__result && !(entityController is FridgeController)) return;   // keep the log tight
                bool motor = false; string curVeh = "";
                try { motor = VehicleHelper.IsInsideMotorVehicle(); curVeh = VehicleHelper.GetCurrentVehicle()?.id ?? ""; } catch { }
                Plugin.Logger.LogInfo($"[FridgeProbe] GATE({entityController.GetType().Name}) → {__result}: " +
                    $"motor={motor} curVeh='{curVeh}' map={CityMap.IsOpen} activity={PlayerActivityUI.IsPanelOpen} " +
                    $"basket={PlayerHelper.IsHoldingShoppingBasket} owned={InstanceBehavior<BuildingManager>.Instance?.IsPlayerOwnedBusiness} " +
                    $"activeVeh='{SaveGameManager.Current?.ActiveVehicleId ?? ""}'");
            }
            catch (Exception ex) { Plugin.Logger.LogInfo($"[FridgeProbe] GATE probe: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(CtaManager), nameof(CtaManager.UpdateCta))]
    public static class Probe_Fridge_UpdateCta
    {
        static void Postfix(EntityController entityController)
        {
            if (!(entityController is FridgeController f)) return;
            try
            {
                // rented=True here proves the guest wrap was engaged during this very call (the Postfix
                // runs inside our Prefix/Finalizer pair, so the forced flag is still visible).
                Plugin.Logger.LogInfo($"[FridgeProbe] UpdateCta → key='{CtaManager.ctaKey}' " +
                    $"actionNull={CtaManager.ctaAction == null} cargo={f.ItemInstance?.cargoInstances?.Count ?? -1} " +
                    $"rented={InstanceBehavior<BuildingManager>.Instance?.buildingRegistration?.RentedByPlayer}");
            }
            catch (Exception ex) { Plugin.Logger.LogInfo($"[FridgeProbe] UpdateCta probe: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(OverlayManager), nameof(OverlayManager.ShowDetailedOverlay))]
    public static class Probe_Fridge_ShowDetailed
    {
        static void Postfix(EntityController entityController, bool __result)
        {
            if (!(entityController is FridgeController f)) return;
            try
            {
                Plugin.Logger.LogInfo($"[FridgeProbe] ShowDetailedOverlay → {__result} " +
                    $"(cargo={f.ItemInstance?.cargoInstances?.Count ?? -1}, overlayType={(int)f.detailedOverlayType})");
            }
            catch (Exception ex) { Plugin.Logger.LogInfo($"[FridgeProbe] ShowDetailed probe: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(ItemController), nameof(ItemController.ShouldReactToIoEnter))]
    public static class Probe_Fridge_IoEnter
    {
        private static float _next;
        static void Postfix(ItemController __instance, bool __result)
        {
            if (!(__instance is FridgeController f)) return;
            try
            {
                if (UnityEngine.Time.unscaledTime < _next) return;   // hover can re-fire — keep the log readable
                _next = UnityEngine.Time.unscaledTime + 2f;
                Plugin.Logger.LogInfo($"[FridgeProbe] IoEnter → {__result} cargo={f.ItemInstance?.cargoInstances?.Count ?? -1}");
            }
            catch (Exception ex) { Plugin.Logger.LogInfo($"[FridgeProbe] IoEnter probe: {ex.Message}"); }
        }
    }
}
