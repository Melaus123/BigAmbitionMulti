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

            var btn = GetOrCreate(panel);
            if (btn == null) return;
            btn.gameObject.SetActive(true);
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

            // Drop any localization driver on the clone so it can't overwrite our label on the
            // next language/binding event (reflection by type name — no namespace dependency).
            foreach (var comp in clone.GetComponentsInChildren(typeof(Component), true))
                if (comp != null && comp.GetType().Name == "TextLocalizationComponent")
                    UnityEngine.Object.Destroy(comp);

            _btn[panel] = btn;
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
