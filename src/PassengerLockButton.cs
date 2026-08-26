using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UI.ItemPanel;
using UnityEngine;
using UnityEngine.UI;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Lock / Unlock toggle on the in-car action menu (ItemPanelUI), placed to the LEFT of the
    /// native Park / Sell / Sleep buttons. SetVehicle fires for ANY car the local player enters —
    /// their own real vehicle OR a granted owner's GHOST (field 20260823-223742 disproved the
    /// original "inherently owner-only" premise) — so the toggle serves both: the OWNER and any
    /// KEY-HOLDER (ruling 2026-08-26: a granted key works like real car keys — the holder may
    /// lock/unlock too). State is read and written under the REAL vehicle id (a ghost's proxy
    /// prefix is stripped); the host authorizes the SENDER against its live owner/grant tables
    /// (HostSetLock on the host, SendVehicleLock on a client). Replaces the dev F8 stand-in.
    /// Default state is LOCKED (privacy-first); see docs/PASSENGER-SYSTEM.md.
    /// </summary>
    [HarmonyPatch(typeof(ItemPanelUI), nameof(ItemPanelUI.SetVehicle))]
    public static class Patch_ItemPanelUI_SetVehicle_Lock
    {
        static void Postfix(ItemPanelUI __instance, VehicleController vehicle)
        {
            try
            {
                // Harmony runs postfixes even when a prefix skipped the original: HousingPatches'
                // SetVehicle NullGuard bails on vehicleType == null and the panel keeps showing the
                // PREVIOUS car — rebinding here would point the button at a car the panel isn't
                // showing (review 2026-08-26 MAJOR-4). Mirror the guard.
                if (vehicle == null || vehicle.vehicleType == null) return;
                PassengerLockButton.Show(__instance, vehicle);
            }
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
                    // Parity (user field 2026-08-25): a pushed cargo cart has NO passenger seat, so
                    // the board request below came back "vehicle full" — a dead end. The owner's
                    // parity result on their own remotely-pushed cart is its STORAGE: the native
                    // manage-cargo screen on the REAL instance (their own authority; the borrower's
                    // replica converges through the fleet manifest like any other cargo change).
                    bool cargoCart = false;
                    try { cargoCart = inst.VehicleType != null && inst.VehicleType.spawnInPlayerObject && inst.VehicleType.maxCargoCapacity > 0; } catch { }
                    if (cargoCart)
                    {
                        Plugin.Logger.LogInfo($"[Drive] owner click on remotely-pushed cart '{inst.id}' → native manage cargo (parity; was a 'vehicle full' board refusal).");
                        try { InstanceBehavior<UI.UIs>.Instance.playerHUD.manageCargoUI.Show(inst); }
                        catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Drive] manage-cargo open: {ex.Message}"); }
                        return false;   // never a board request for a seatless cart
                    }
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
        private static readonly Dictionary<ItemPanelUI, string> _boundVid = new();   // panel → REAL vid the label/click act on

        public static void Show(ItemPanelUI panel, VehicleController vehicle)
        {
            if (panel == null || vehicle == null || vehicle.vehicleInstance == null) return;
            if (!MPServer.IsRunning && !MPClient.IsConnected) { Hide(panel); return; }   // no lock concept in SP
            string vid = vehicle.vehicleInstance.id;
            if (string.IsNullOrEmpty(vid)) { Hide(panel); return; }
            // A key-holder who entered a GRANTED vehicle is sitting in its GHOST — that instance id
            // carries the proxy prefix, but the lock table (and every gate that reads it) is keyed
            // by the REAL id. Strip it so the label shows the mirrored truth and the toggle acts on
            // the real lock. (Field 20260823-223742: the prefixed lookup made every borrower's label
            // read "Unlock" regardless of state, and the click wrote a key no gate ever read.)
            // BAMP_TESTRIG ids are REAL owner-side vehicles on pre-release rig saves — the six
            // sibling prefix tests all carry this exemption (PassengerRide.cs:42's MAJOR-2 rule).
            bool ghost = vid.StartsWith("BAMP_", System.StringComparison.Ordinal)
                         && !vid.StartsWith("BAMP_TESTRIG", System.StringComparison.Ordinal);
            string realVid = ghost ? vid.Substring(5) : vid;

            // A scooter has no passenger seats AND no shareable storage, so the lock/unlock toggle only
            // confuses. (Flatbed/hand-truck are also 0-seat but KEEP the toggle — it gates shared storage.)
            string tn = vehicle.vehicleInstance.vehicleTypeName ?? "";
            if (tn.IndexOf("scooter", System.StringComparison.OrdinalIgnoreCase) >= 0) { Hide(panel); return; }

            var btn = GetOrCreate(panel);
            if (btn == null) { Hide(panel); return; }   // clear the previous car's binding, not just bail
            btn.gameObject.SetActive(true);
            // The clone inherits parkButton's interactable state — and Park is greyed/non-interactable when you
            // can't park where you are, which left our toggle greyed AND click-dead (a non-interactable Button
            // suppresses onClick). Lock/unlock is always valid, so force it on (+ clear any inherited CanvasGroup
            // gating) every time we show it.
            btn.interactable = true;
            try { var cg = btn.GetComponent<CanvasGroup>(); if (cg != null) { cg.interactable = true; cg.blocksRaycasts = true; cg.alpha = 1f; } } catch { }
            _boundVid[panel] = realVid;
            SetLabel(btn, PassengerSync.IsLocked(realVid));
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() =>
            {
                // Live read at commitment (never the Show-time snapshot): a key revoked while
                // sitting inside must no-op with the truthful label, not lie optimistically.
                // The host's authorization is the backstop; this only spares the wire.
                if (ghost && !GrantSync.IsGranted(VehicleManager.OwnerIdFor(realVid), MPConfig.PlayerId))
                {
                    Plugin.Logger.LogInfo($"[LockBtn] '{realVid}' toggle refused locally — key no longer granted.");
                    SetLabel(btn, PassengerSync.IsLocked(realVid));
                    return;
                }
                bool setLocked = !PassengerSync.IsLocked(realVid);
                if (MPServer.IsRunning)
                {
                    bool applied = MPServer.HostSetLock(realVid, setLocked);
                    SetLabel(btn, applied ? setLocked : PassengerSync.IsLocked(realVid));   // synchronous truth
                }
                else
                {
                    MPClient.SendVehicleLock(realVid, setLocked);
                    // Optimistic; on apply the host's broadcast confirms, and on REFUSAL the host
                    // unicasts the current true state back (HandleVehicleLockSet), which lands in
                    // HandleVehicleLockMsg → RefreshFor and snaps this label to truth.
                    SetLabel(btn, setLocked);
                }
                Plugin.Logger.LogInfo($"[LockBtn] '{realVid}' -> {(setLocked ? "LOCKED" : "UNLOCKED")}{(ghost ? " (key-holder)" : "")}");
            });
        }

        /// <summary>A lock change landed (either side may toggle now). If a live panel's button is
        /// bound to this vehicle, snap its label to the mirrored truth. MAIN THREAD.</summary>
        public static void RefreshFor(string realVid)
        {
            if (string.IsNullOrEmpty(realVid)) return;
            foreach (var kv in _boundVid)
            {
                if (kv.Key == null || kv.Value != realVid) continue;
                if (_btn.TryGetValue(kv.Key, out var b) && b != null && b.gameObject.activeInHierarchy)
                    SetLabel(b, PassengerSync.IsLocked(realVid));
            }
            // Prune destroyed panels (one pair per scene load). Swept over _btn — the SUPERSET: a
            // panel Hidden (dropped from _boundVid) and THEN destroyed is only findable here.
            // kv.Key == null is Unity's fake-null overload probing the native side — LOAD-BEARING:
            // the managed reference stays real (that's what makes the Remove below find its bucket),
            // so `is null` or a null-forgiveness "fix" here would silently break the prune.
            List<ItemPanelUI>? dead = null;
            foreach (var kv in _btn)
                if (kv.Key == null) (dead ??= new List<ItemPanelUI>()).Add(kv.Key!);
            if (dead != null)
                foreach (var k in dead) { _boundVid.Remove(k); _btn.Remove(k); }
        }

        public static void Hide(ItemPanelUI panel)
        {
            if (panel == null) return;
            _boundVid.Remove(panel);
            if (_btn.TryGetValue(panel, out var b) && b != null)
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
