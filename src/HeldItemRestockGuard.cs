// HELD-ITEM RESTOCK GUARD - BOX-PLACEMENT-STUCK-1, F1 (owner-approved 2026-10-02, decision 72).
//
// THE BUG (reproduction T-BOXGRAB-20261002-061901, 29/29). While THIS machine's player moves a partly used
// stock box, the game's own restocking empties it and deletes its object in the player's hands:
//   ReStockingHelper.RefillSingleItemWithLimit (ReStockingHelper.cs:118) -> MoveStockToItem (:294)
//   -> MoveStockFromBuildingStorage (:310-347, takes via TryAddStockAmount :148-166) -> an emptied box with
//   'discardcontainerwhenempty' -> BuildingRegistration.RemoveItemInstanceFromBuilding -> object destroyed.
// The other source loop is ItemHelper.FillUpShowcaseShelfOrPointOfSale (ItemHelper.cs:823; storage loop
// :845-887 and vehicle loop :888-910, both via CargoInstance.MergeAmount). Single player never gets here:
// the game pauses during a move (PlacementHelper.cs:86-92); multiplayer suppresses that pause.
//
// WHAT THIS DOES. While the game says an item is being moved (PlacementSystem.CurrentPlaceableItemBeingPlaced,
// the game's own state - never a proxy), goods are never TAKEN OUT of that item or the items stacked on it
// (its childItemControllers, which move with it). Only taking goods out empties and deletes a held box;
// adding goods TO a held item never deletes it, so a held item is still a restock TARGET as in single player.
// That matters: StopPlacingItem calls OnItemPlacementFinished (PlacementSystem.cs:693, 0916 asm) BEFORE it
// clears the held field (:701), and OnItemPlacementFinished fills a newly placed single-product station, or a
// register in a paper-bag shop, from storage (ItemController.cs:1469-1474) - the item still counts as held.
// Everything else restocks as before. Nothing is changed and then restored: every hook only READS the game
// state and declines one transfer.
//
// THE CHOKE POINTS AND WHY (read in the decompile; the narrowest places that cover every entry point):
//   * SOURCE, ReStockingHelper: TryAddStockAmount. The only call that takes goods out of a storage item
//     (:324, :363 - its only callers in the game). Declining it returns 0, so the loop moves on to the next
//     storage item exactly as for an empty one. CanUseCargoForRestocking would be one hook for both
//     count and take, but it is a ~25-byte method the Mono JIT may inline, so a patch there may never run.
//   * SOURCE COUNT, ReStockingHelper: CountAvailableStock. Postfix subtracts what the held item contributed,
//     so the refill plan sizes itself from the storage the shop can really use, as if the box were not there
//     (and, when nothing else holds those goods, no transfer is even attempted - so this also logs the skip).
//   * ItemHelper.FillUpShowcaseShelfOrPointOfSale: its own loop, no shared helper. Prefix opens a scope in
//     which CargoInstance.MergeAmount declines a merge whose SOURCE cargo belongs to the held item (amount
//     untouched, so the loop just continues). The fill itself always runs, also when its target is held.
// Logged once per held item (the first time anything was declined for it).
using System;
using HarmonyLib;
using BigAmbitions.Items;

namespace BigAmbitionsMP
{
    internal static class HeldItemRestockGuard
    {
        private static ItemInstance? _loggedFor;
        /// <summary>Number of "[RestockGuard]" lines written this session (the DEV lever reports it).</summary>
        internal static int SkipLogs;
        /// <summary>Depth of ItemHelper.FillUpShowcaseShelfOrPointOfSale calls in progress (main thread only).</summary>
        internal static int FillScope;

        private static ItemInstance? Held(out global::ItemController? ctl)
        {
            ctl = null;
            var cur = BigAmbitions.PlacementSystem.PlacementSystem.CurrentPlaceableItemBeingPlaced;
            if (cur == null) return null;
            ctl = cur as global::ItemController;
            return cur.GetItemInstance();
        }

        /// <summary>The held item (or an item stacked on it) whose cargo list holds <paramref name="c"/>, else null.</summary>
        internal static ItemInstance? HeldOwnerOf(CargoInstance? c)
        {
            if (c == null) return null;
            var held = Held(out var ctl);
            if (held == null) return null;
            if (Owns(held, c)) return held;
            if ((object?)ctl == null) return null;
            var kids = ctl!.childItemControllers;
            if (kids == null) return null;
            for (int i = 0; i < kids.Count; i++)
            {
                var k = kids[i];
                var ki = (object?)k != null ? k.ItemInstance : null;
                if (ki != null && Owns(ki, c)) return ki;
            }
            return null;
        }

        private static bool Owns(ItemInstance it, CargoInstance c)
        {
            var list = it.cargoInstances;
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], c)) return true;
            return false;
        }

        /// <summary>Units of <paramref name="stockItemName"/> the native CountAvailableStock counted from one
        /// held item (same test as ReStockingHelper.cs:210-219), 0 when that item is not in this building.</summary>
        private static int Counted(global::BuildingRegistration reg, ItemInstance? it, string stockItemName)
        {
            if (it == null || it.cargoInstances == null) return 0;
            if (!it.ItemCached.HasTag(BigAmbitions.Tags.TagRef.Itemtag.isstockcontainer)) return 0;
            bool here = false;
            foreach (var v in reg.itemInstances.Values)
                if (ReferenceEquals(v, it)) { here = true; break; }
            if (!here) return 0;
            int n = 0;
            var list = it.cargoInstances;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c != null && c.itemName == stockItemName && c.nestedCargoInstances.Count == 0) n += c.amount;
            }
            return n;
        }

        internal static int HeldCounted(global::BuildingRegistration reg, string stockItemName, out ItemInstance? held)
        {
            held = Held(out var ctl);
            if (held == null) return 0;
            int n = Counted(reg, held, stockItemName);
            if ((object?)ctl == null) return n;
            var kids = ctl!.childItemControllers;
            if (kids == null) return n;
            for (int i = 0; i < kids.Count; i++)
            {
                var k = kids[i];
                if ((object?)k != null) n += Counted(reg, k.ItemInstance, stockItemName);
            }
            return n;
        }

        internal static void NoteSkip(ItemInstance? it, string role, string where)
        {
            try
            {
                if (it == null || ReferenceEquals(it, _loggedFor)) return;
                _loggedFor = it;
                SkipLogs++;
                string id = "";
                try { id = it.id?.ToString() ?? ""; } catch { }
                Plugin.Logger.LogInfo($"[RestockGuard] '{it.itemName}' (id {id}) is being moved by this player - "
                    + $"restocking left it alone {role} ({where}). Single player pauses the game during a move "
                    + "(PlacementHelper.cs:86-92), so its goods stay exactly as they were; other storage still restocks. "
                    + "Logged once per held item.");
            }
            catch { }
        }
    }

    /// <summary>SOURCE choke for ReStockingHelper: the only call that takes goods out of a storage item.</summary>
    [HarmonyPatch(typeof(global::ReStockingHelper), nameof(global::ReStockingHelper.TryAddStockAmount))]
    internal static class Patch_ReStockingHelper_TryAddStockAmount_HeldSource
    {
        static bool Prefix(CargoInstance sourceCargoInstance, ref int __result)
        {
            try
            {
                var owner = HeldItemRestockGuard.HeldOwnerOf(sourceCargoInstance);
                if (owner == null) return true;
                HeldItemRestockGuard.NoteSkip(owner, "as a restock source", "ReStockingHelper.TryAddStockAmount");
                __result = 0;
                return false;
            }
            catch { return true; }
        }
    }

    /// <summary>Keeps the refill plan honest: the held item's goods are not available while it is moved.</summary>
    [HarmonyPatch(typeof(global::ReStockingHelper), "CountAvailableStock")]
    internal static class Patch_ReStockingHelper_CountAvailableStock_HeldSource
    {
        static void Postfix(global::BuildingRegistration buildingRegistration, string stockItemName, ref int __result)
        {
            try
            {
                if (__result <= 0 || buildingRegistration == null || string.IsNullOrEmpty(stockItemName)) return;
                if (BigAmbitions.PlacementSystem.PlacementSystem.CurrentPlaceableItemBeingPlaced == null) return;
                int held = HeldItemRestockGuard.HeldCounted(buildingRegistration, stockItemName, out var heldItem);
                if (held <= 0) return;
                __result = Math.Max(0, __result - held);
                HeldItemRestockGuard.NoteSkip(heldItem, "as a restock source", "ReStockingHelper.CountAvailableStock");
            }
            catch { }
        }
    }

    /// <summary>ItemHelper's own fill loop: always runs (a held TARGET is filled as in single player - e.g. a
    /// station just placed, OnItemPlacementFinished); only the held item as SOURCE is declined, in MergeAmount below.</summary>
    [HarmonyPatch(typeof(global::ItemHelper), nameof(global::ItemHelper.FillUpShowcaseShelfOrPointOfSale), typeof(ItemInstance))]
    internal static class Patch_ItemHelper_FillUpShowcase_HeldItem
    {
        static void Prefix(out bool __state)
        {
            __state = false;
            try
            {
                HeldItemRestockGuard.FillScope++;
                __state = true;
            }
            catch { }
        }

        static Exception? Finalizer(Exception? __exception, bool __state)
        {
            if (__state && HeldItemRestockGuard.FillScope > 0) HeldItemRestockGuard.FillScope--;
            return __exception;
        }
    }

    /// <summary>Only inside FillUpShowcaseShelfOrPointOfSale: a merge whose SOURCE cargo is the held item's is declined.</summary>
    [HarmonyPatch(typeof(CargoInstance), nameof(CargoInstance.MergeAmount))]
    internal static class Patch_CargoInstance_MergeAmount_HeldSource
    {
        static bool Prefix(CargoInstance cargoInstanceToMerge)
        {
            if (HeldItemRestockGuard.FillScope <= 0) return true;
            try
            {
                var owner = HeldItemRestockGuard.HeldOwnerOf(cargoInstanceToMerge);
                if (owner == null) return true;
                HeldItemRestockGuard.NoteSkip(owner, "as a shelf-fill source", "ItemHelper.FillUpShowcaseShelfOrPointOfSale");
                return false;
            }
            catch { return true; }
        }
    }
}
