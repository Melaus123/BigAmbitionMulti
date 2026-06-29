using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UI.ItemPanel;
using UnityEngine;
using UnityEngine.UI;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Owner-facing Lock / Unlock toggle on the in-car action menu (ItemPanelUI), placed to the
    /// LEFT of the native Park / Sell / Sleep buttons. The game only calls SetVehicle for a car
    /// the local player has entered — i.e. their OWN car — so showing the toggle there is
    /// inherently owner-only. Clicking flips PassengerSync's lock through the host-authoritative
    /// path (HostSetLock on the host, SendVehicleLock on a client owner). Replaces the dev F8
    /// stand-in. Default state is LOCKED (privacy-first); see docs/PASSENGER-SYSTEM.md.
    /// </summary>
    [HarmonyPatch(typeof(ItemPanelUI), nameof(ItemPanelUI.SetVehicle))]
    public static class Patch_ItemPanelUI_SetVehicle_Lock
    {
        static void Postfix(ItemPanelUI __instance, VehicleController vehicle)
        {
            try { PassengerLockButton.Show(__instance, vehicle); }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[LockBtn] SetVehicle: {ex.Message}"); }
        }
    }

    /// <summary>Phase 2 C: when the OWNER tries to enter their own car while a granted borrower is driving it,
    /// route them into the PASSENGER seat (ride along) instead of letting them hijack the wheel. The board runs
    /// through the host-authoritative path (HostCanBoard permits the owner only while the car is driven); the
    /// passenger pin targets the owner's real followed car via VehicleManager.GhostTransform's fallback.</summary>
    [HarmonyPatch(typeof(CarController), nameof(CarController.EnterVehicle))]
    public static class Patch_CarController_EnterVehicle_BlockHijack
    {
        static bool Prefix(CarController __instance)
        {
            try
            {
                var inst = __instance != null ? __instance.vehicleInstance : null;
                if (inst != null && VehicleManager.IsDrivenRemotely(inst.id))
                {
                    Plugin.Logger.LogInfo($"[Drive] owner enter on driven car '{inst.id}' → boarding as a passenger (ride along).");
                    PassengerRide.RequestBoard(inst.id);   // ride along as a passenger instead of hijacking the wheel
                    return false;   // skip native drive
                }
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Drive] hijack-guard: {ex.Message}"); }
            return true;
        }
    }

    /// <summary>Phase 2 C (owner ride): intercept the owner's "drive my car" click BEFORE the native
    /// walk-to-DRIVER-door (VehicleController.DriveVehicle → SetGoal(drivingEntrance, EnterVehicle)). While a
    /// borrower is driving it, board the owner as a PASSENGER instead, so they walk straight to the passenger
    /// door (not the driver door, then redirect). The EnterVehicle guard above is the safety net for any
    /// direct-enter path that bypasses DriveVehicle.</summary>
    [HarmonyPatch(typeof(VehicleController), nameof(VehicleController.DriveVehicle))]
    public static class Patch_VehicleController_DriveVehicle_OwnerRide
    {
        static bool Prefix(VehicleController __instance)
        {
            try
            {
                var inst = __instance != null ? __instance.vehicleInstance : null;
                if (inst != null && VehicleManager.IsDrivenRemotely(inst.id))
                {
                    Plugin.Logger.LogInfo($"[Drive] owner DriveVehicle on driven car '{inst.id}' → passenger board (walk to passenger door).");
                    PassengerRide.RequestBoard(inst.id);
                    return false;   // skip the native walk-to-driver-door + enter
                }
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Drive] owner-ride guard: {ex.Message}"); }
            return true;
        }
    }

    // Cache the live ItemPanelUI so the passenger HUD can clone its Park/Sleep buttons for the
    // native look (the passenger rides a ghost, so it never gets its own SetVehicle call).
    [HarmonyPatch(typeof(ItemPanelUI), "Start")]
    public static class Patch_ItemPanelUI_Start_Cache
    {
        static void Postfix(ItemPanelUI __instance) { PassengerHud.NativePanel = __instance; }
    }

    // The same panel is reused for placed ITEMS — hide our vehicle-only button then.
    [HarmonyPatch(typeof(ItemPanelUI), nameof(ItemPanelUI.SetItemInstance))]
    public static class Patch_ItemPanelUI_SetItemInstance_Lock
    {
        static void Postfix(ItemPanelUI __instance)
        {
            try { PassengerLockButton.Hide(__instance); }
            catch { }
        }
    }

    internal static class PassengerLockButton
    {
        private static readonly Dictionary<ItemPanelUI, Button> _btn = new();   // one per panel

        public static void Show(ItemPanelUI panel, VehicleController vehicle)
        {
            if (panel == null || vehicle == null || vehicle.vehicleInstance == null) return;
            if (!MPServer.IsRunning && !MPClient.IsConnected) { Hide(panel); return; }   // no lock concept in SP
            string vid = vehicle.vehicleInstance.id;
            if (string.IsNullOrEmpty(vid)) { Hide(panel); return; }

            // A scooter has no passenger seats AND no shareable storage, so the lock/unlock toggle only
            // confuses. (Flatbed/hand-truck are also 0-seat but KEEP the toggle — it gates shared storage.)
            string tn = vehicle.vehicleInstance.vehicleTypeName ?? "";
            if (tn.IndexOf("scooter", System.StringComparison.OrdinalIgnoreCase) >= 0) { Hide(panel); return; }

            var btn = GetOrCreate(panel);
            if (btn == null) return;
            btn.gameObject.SetActive(true);
            // The clone inherits parkButton's interactable state — and Park is greyed/non-interactable when you
            // can't park where you are, which left our toggle greyed AND click-dead (a non-interactable Button
            // suppresses onClick). Lock/unlock is always valid, so force it on (+ clear any inherited CanvasGroup
            // gating) every time we show it.
            btn.interactable = true;
            try { var cg = btn.GetComponent<CanvasGroup>(); if (cg != null) { cg.interactable = true; cg.blocksRaycasts = true; cg.alpha = 1f; } } catch { }
            SetLabel(btn, PassengerSync.IsLocked(vid));
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() =>
            {
                bool setLocked = !PassengerSync.IsLocked(vid);
                if (MPServer.IsRunning) MPServer.HostSetLock(vid, setLocked);
                else                    MPClient.SendVehicleLock(vid, setLocked);
                SetLabel(btn, setLocked);   // immediate feedback (host applies synchronously; client is optimistic)
                Plugin.Logger.LogInfo($"[LockBtn] '{vid}' -> {(setLocked ? "LOCKED" : "UNLOCKED")}");
            });
        }

        public static void Hide(ItemPanelUI panel)
        {
            if (panel != null && _btn.TryGetValue(panel, out var b) && b != null)
                b.gameObject.SetActive(false);
        }

        private static Button? GetOrCreate(ItemPanelUI panel)
        {
            if (_btn.TryGetValue(panel, out var existing) && existing != null) return existing;

            var src = panel.parkButton;
            if (src == null) return null;
            var clone = UnityEngine.Object.Instantiate(src.gameObject, src.transform.parent);
            clone.name = "BAMP_LockButton";
            clone.transform.SetAsFirstSibling();        // leftmost in the button row
            var btn = clone.GetComponent<Button>();

            // CRITICAL: replace the whole click event. The clone inherited Park's PERSISTENT
            // (serialized) onClick -> ClickPark -> ExitVehicle; RemoveAllListeners() only clears
            // RUNTIME listeners, not persistent ones, so the clone was both unlocking AND exiting
            // the car. A fresh event is the established fix (see MPPhoneButton, ANTIPATTERNS class 6).
            if (btn != null) btn.onClick = new Button.ButtonClickedEvent();

            // Drop any localization driver on the clone so it can't overwrite our label on the
            // next language/binding event (reflection by type name — no namespace dependency).
            foreach (var comp in clone.GetComponentsInChildren(typeof(Component), true))
                if (comp != null && comp.GetType().Name == "TextLocalizationComponent")
                    UnityEngine.Object.Destroy(comp);

            _btn[panel] = btn;
            Plugin.Logger.LogInfo("[LockBtn] toggle created (cloned parkButton; onClick + interactable reset).");
            return btn;
        }

        private static void SetLabel(Button btn, bool locked)
        {
            if (btn == null) return;
            var tmp = btn.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null) tmp.text = locked ? "Unlock" : "Lock";
        }
    }
}
