using System;
using BigAmbitions.Items;   // CargoInstance
using Buildings;            // BuildingRegistration (OwnerBusinessTail delegate signature)
using Helpers;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Shared BUILDING storage (fridges, shelves, registers, producers, delivery spots…) — pure
    /// per-container SENDER facade since unification Stage B (2026-08, design:
    /// .modding/03-systems/storage-unification-2026-08.md). The request side lives here; the wire
    /// is the unified StorageOp/StorageRes family (195/196, v16) and ALL apply/result logic is
    /// StorageSync, the one engine both containers share (ruling 37).
    ///
    /// Host-authoritative: guest → host (resolves the building owner from the addressKey —
    /// clients don't keep a building→owner map) → owner (applies to reg + pushes the room) →
    /// host → guest. THREADING: the engine's OwnerApply()/OnResult() MUST run on the Unity main
    /// thread; the network dispatch marshals them (see MPServer/MPClient). See docs/PERMISSIONS-SYSTEM.md.
    /// </summary>
    public static class BuildingStorageSync
    {
        public const byte OpTake     = 0;   // remove Amount of ItemName from the interior item (guest receives it)
        public const byte OpPut      = 1;   // add Amount of ItemName to the interior item (from the guest)
        public const byte OpSetStock = 2;   // round-32: set the item's STOCK type — NOTE: byte 2 means MarkPaid on the vehicle wire (the collision is why the engine's op is a string)

        // ── LOCAL byte → engine op string (the byte survives only as this facade's API surface;
        // the WIRE carries the string since Stage B). Unknown byte → "" → the engine refuses it
        // "unsupported" (ruling 39; the D-2 sentinel, now enforced engine-side). ──
        private static string OpName(byte op) => op == OpTake     ? StorageSync.OpTake
                                               : op == OpPut      ? StorageSync.OpPut
                                               : op == OpSetStock ? StorageSync.OpSetStock
                                               :                    "";

        /// <summary>Round-39c recognition tail — second consumer CustomerEntrySync calls through
        /// this name; the body lives in the engine.</summary>
        internal static void OwnerBusinessTail(BuildingRegistration reg) => StorageSync.OwnerBusinessTail(reg);

        // ── Guest side: start a take / put (unchanged) ───────────────────────────
        public static void RequestTake(string addressKey, string itemId, string itemName, int amount, bool paid, float price, string ctx = "")
            => Send(OpTake, addressKey, itemId, itemName, amount, paid, price, ctx);

        public static void RequestPut(string addressKey, string itemId, string itemName, int amount, bool paid, float price, string ctx = "")
            => Send(OpPut, addressKey, itemId, itemName, amount, paid, price, ctx);

        /// <summary>Round-47b (full sell/discard parity, user 2026-07-07): a helper sells or discards a
        /// whole stack row. The REMOVAL routes to the owner (stock truth); on a sell, the MONEY credits
        /// the HELPER's own wallet locally on confirm — native "whoever sells pockets it" semantics; the
        /// grant is trust-scoped and Transfers exist for gifting it back (user's design).</summary>
        public static void RequestStackOp(string addressKey, string itemId, string itemName, int amount, bool paid, float price, int count, bool sell)
        {
            if (string.IsNullOrEmpty(addressKey) || string.IsNullOrEmpty(itemId) || string.IsNullOrEmpty(itemName) || count <= 0) return;
            StorageSync.SendOp(new StorageOpPayload
            {
                Container = StorageSync.ContainerBuilding, AddressKey = addressKey, ItemId = itemId,
                PlayerId = MPConfig.PlayerId, Op = StorageSync.OpTake, ItemName = itemName,
                Amount = amount, Paid = paid, PricePerUnit = price,
                Count = count, Ctx = sell ? "stacksell" : "stackdiscard",
            });
        }

        /// <summary>Round-47: put a SEALED BOX back (hands-full race after a boxtake) — nested contents
        /// travel so the give-back doesn't strip the box.</summary>
        public static void RequestPutBox(string addressKey, string itemId, string itemName, int amount, bool paid, float price, List<CargoNestedInfo> nested)
        {
            if (string.IsNullOrEmpty(addressKey) || string.IsNullOrEmpty(itemId) || amount <= 0 || string.IsNullOrEmpty(itemName)) return;
            var req = new StorageOpPayload
            {
                Container = StorageSync.ContainerBuilding, AddressKey = addressKey, ItemId = itemId,
                PlayerId = MPConfig.PlayerId, Op = StorageSync.OpPut, ItemName = itemName,
                Amount = amount, Paid = paid, PricePerUnit = price, Ctx = "boxreturn",
            };
            if (nested != null) req.Nested.AddRange(nested);
            StorageSync.SendOp(req);
        }

        /// <summary>Round-32 (business helpers): ask the owner to change what a display/showcase item — or a
        /// producer (ctx="producerset") — stocks. The owner runs the same moves the native dropdown does.</summary>
        public static void RequestSetStock(string addressKey, string itemId, string newStockName, string ctx = "setstock")
            => Send(OpSetStock, addressKey, itemId, newStockName ?? "", 1, paid: false, price: 0f, ctx);

        /// <summary>Round-49 slice 2: capture the click-time cargo (nested + colors) for the helper
        /// PLACE flow — the verdict consumes it in the engine.</summary>
        public static void SetPendingPlace(string addressKey, string itemId, CargoInstance source)
            => StorageSync.SetPendingPlace(source);

        private static void Send(byte op, string addressKey, string itemId, string itemName, int amount, bool paid, float price, string ctx = "")
        {
            // SetStock legitimately carries an EMPTY ItemName ("clear the stock type" = the native
            // "undefined" dropdown choice); take/put never do.
            if (string.IsNullOrEmpty(addressKey) || string.IsNullOrEmpty(itemId) || amount <= 0
                || (string.IsNullOrEmpty(itemName) && op != OpSetStock))
                return;
            StorageSync.SendOp(new StorageOpPayload
            {
                Container = StorageSync.ContainerBuilding, AddressKey = addressKey, ItemId = itemId,
                PlayerId = MPConfig.PlayerId, Op = OpName(op), ItemName = itemName,
                Amount = amount, Paid = paid, PricePerUnit = price, Ctx = ctx,
            });
        }
    }
}
