using System;
using BigAmbitions.Items;   // ItemInstance
using BigAmbitions.Tags;    // TagRef.Itemtag.ismop
using HarmonyLib;
using Helpers;              // PlayerHelper
using UI.ItemPanel;         // ItemPanelUI

namespace BigAmbitionsMP
{
    /// <summary>H-MOP-1 r1 (bundle 20260905-225610). After every COMPLETED furniture placement the game re-spawns the held item
    /// into the hand (PlacementHelper.Run → ItemPanelUI.SetItemInstance(ItemInstanceInHands), PlacementHelper.cs:160-167) WITHOUT
    /// removing the hand object already there (the mop branch just AddHandObject + AssignToPlayer, ItemPanelUI.cs:277-284), and
    /// BaseHuman.RemoveHandObject destroys only the FIRST attached object before clearing its list (BaseHuman.cs:315-327). A player
    /// who moved furniture while holding a mop therefore carried two mop models; the later discard removed one and orphaned the
    /// other — a mop nobody can take away. Reachable through the mod (a guest in the interior designer of a friend's building).
    /// Fix: when a mop is about to be spawned into a hand that already holds one, tear the existing one down the way the game's
    /// own RemoveItemsFromHands does (UnAssignFromPlayer + RemoveHandObject), so the re-spawn REPLACES instead of stacking. Mop
    /// only; every other item keeps the game's behaviour. The doomed mop is deactivated before the game re-spawns so the
    /// game's own lookup binds the new one. Attempt 1 of 2 (r2 = review fold).</summary>
    [HarmonyPatch(typeof(ItemPanelUI), nameof(ItemPanelUI.SetItemInstance))]
    internal static class Patch_ItemPanelUI_SetItemInstance_MopSingleSlot
    {
        static void Prefix(ItemInstance itemInstance, bool spawnIntoPlayerHands)
        {
            try
            {
                if (!spawnIntoPlayerHands || itemInstance == null) return;
                if (!itemInstance.ItemCached.HasTag(TagRef.Itemtag.ismop)) return;
                var hand = PlayerHelper.PlayerController.Character.rightHand;
                var old = hand != null ? hand.GetComponentInChildren<MopController>() : null;
                if (old == null) return;                                        // first spawn: nothing to replace
                old.UnAssignFromPlayer();                                       // listener, animator bools, cursor — the game's own teardown order (PlayerHelper.cs:312-334)
                old.gameObject.SetActive(false);                                // r2: Destroy is deferred to end of frame; an ACTIVE doomed mop would still be the first child the game's GetComponentInChildren<MopController>() finds after AddHandObject (ItemPanelUI.cs:282), and AssignToPlayer would land on it (review F-2026-09-06-Y MAJOR-1). Inactive objects are skipped; RemoveHandObject still destroys it by reference.
                PlayerHelper.PlayerController.Character.RemoveHandObject();     // destroys the existing hand object and clears the slot bookkeeping
                Plugin.Logger.LogInfo("[Mop] a mop was already in hand when the game re-spawned one — replaced instead of stacked (H-MOP-1).");
            }
            catch (Exception ex) { try { Plugin.Logger.LogWarning($"[Mop] single-slot guard: {ex.Message}"); } catch { } }
        }
    }
}
