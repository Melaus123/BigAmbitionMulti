using System;
using BigAmbitions.Items;   // CargoInstance
using Buildings;            // BuildingRegistration (OwnerBusinessTail delegate signature)
using Helpers;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Shared BUILDING storage (fridges, shelves, registers, producers, delivery spots…) — WIRE
    /// ADAPTER since unification Stage A (2026-08-25, design:
    /// .modding/03-systems/storage-unification-2026-08.md). The request side lives here
    /// unchanged; the owner-apply and result handling moved VERBATIM into StorageSync, the one
    /// engine both containers share (ruling 37). The BuildingCargoReq/Res wire (138/139) is
    /// unchanged in Stage A; Stage B replaces it with the unified StorageOp/Res family.
    ///
    /// Host-authoritative: guest → host (resolves the building owner from the addressKey —
    /// clients don't keep a building→owner map) → owner (applies to reg + pushes the room) →
    /// host → guest. THREADING: OwnerApply()/OnResult() MUST run on the Unity main thread; the
    /// network dispatch marshals them (see MPServer/MPClient). See docs/PERMISSIONS-SYSTEM.md.
    /// </summary>
    public static class BuildingStorageSync
    {
        public const byte OpTake     = 0;   // remove Amount of ItemName from the interior item (guest receives it)
        public const byte OpPut      = 1;   // add Amount of ItemName to the interior item (from the guest)
        public const byte OpSetStock = 2;   // round-32: set the item's STOCK type — NOTE: byte 2 means MarkPaid on the vehicle wire (the collision is why the engine's op is a string)

        // Purity review D-2: an UNKNOWN byte maps to the "" sentinel and is refused "unsupported"
        // (ruling 39) — a fallthrough here would RUN ApplySetStock for junk bytes (mutating).
        private static string OpName(byte op) => op == OpTake     ? StorageSync.OpTake
                                               : op == OpPut      ? StorageSync.OpPut
                                               : op == OpSetStock ? StorageSync.OpSetStock
                                               :                    "";
        private static byte OpByte(string op) => op == StorageSync.OpTake ? OpTake
                                               : op == StorageSync.OpPut  ? OpPut
                                               :                            OpSetStock;

        private static StorageSync.StorageOpData ToEngine(BuildingCargoReqPayload req)
        {
            var d = new StorageSync.StorageOpData
            {
                Container = StorageSync.ContainerBuilding, AddressKey = req.AddressKey, ItemId = req.ItemId,
                PlayerId = req.PlayerId, Op = OpName(req.Op), Ctx = req.Ctx ?? "", ItemName = req.ItemName,
                Amount = req.Amount, Paid = req.Paid, PricePerUnit = req.PricePerUnit, Count = req.Count,
            };
            if (req.Nested != null) d.Nested.AddRange(req.Nested);
            return d;
        }

        private static StorageSync.StorageResData ToEngine(BuildingCargoResPayload res)
        {
            var d = new StorageSync.StorageResData
            {
                Container = StorageSync.ContainerBuilding, AddressKey = res.AddressKey, ItemId = res.ItemId,
                PlayerId = res.PlayerId, Op = OpName(res.Op), Ctx = res.Ctx ?? "", ItemName = res.ItemName,
                Amount = res.Amount, Paid = res.Paid, PricePerUnit = res.PricePerUnit, Count = res.Count,
                Ok = res.Ok, Reason = res.Reason,
            };
            if (res.Nested != null) d.Nested.AddRange(res.Nested);
            return d;
        }

        private static BuildingCargoResPayload ToWire(StorageSync.StorageResData res)
        {
            var w = new BuildingCargoResPayload
            {
                AddressKey = res.AddressKey, ItemId = res.ItemId, PlayerId = res.PlayerId, Op = OpByte(res.Op),
                ItemName = res.ItemName, Amount = res.Amount, Paid = res.Paid, PricePerUnit = res.PricePerUnit,
                Ctx = res.Ctx, Count = res.Count, Ok = res.Ok, Reason = res.Reason,
            };
            if (res.Nested != null) w.Nested.AddRange(res.Nested);
            return w;
        }

        // ── Owner / guest seams (MAIN THREAD ONLY) — engine delegation ───────────
        public static BuildingCargoResPayload OwnerApply(BuildingCargoReqPayload req)
        {
            if (OpName(req.Op).Length == 0)   // off-contract byte: refuse without touching the engine
            {
                Plugin.Logger.LogWarning($"[BStore] unknown op byte {req.Op} from '{req.PlayerId}' — refused (unsupported).");
                return new BuildingCargoResPayload
                {
                    AddressKey = req.AddressKey, ItemId = req.ItemId, PlayerId = req.PlayerId, Op = req.Op,
                    ItemName = req.ItemName, Amount = req.Amount, Paid = req.Paid, PricePerUnit = req.PricePerUnit,
                    Ctx = req.Ctx, Ok = false, Reason = "unsupported",
                };
            }
            return ToWire(StorageSync.OwnerApply(ToEngine(req)));
        }

        public static void OnResult(BuildingCargoResPayload res)
            => StorageSync.OnResult(ToEngine(res));

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
            var req = new BuildingCargoReqPayload
            {
                AddressKey = addressKey, ItemId = itemId, PlayerId = MPConfig.PlayerId,
                Op = OpTake, ItemName = itemName, Amount = amount, Paid = paid, PricePerUnit = price,
                Count = count, Ctx = sell ? "stacksell" : "stackdiscard",
            };
            if (MPServer.IsRunning) MPServer.HandleBuildingCargoReq(req, MPConfig.PlayerId);
            else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.BuildingCargoReq, MPConfig.PlayerId, req));
        }

        /// <summary>Round-47: put a SEALED BOX back (hands-full race after a boxtake) — nested contents
        /// travel so the give-back doesn't strip the box.</summary>
        public static void RequestPutBox(string addressKey, string itemId, string itemName, int amount, bool paid, float price, List<CargoNestedInfo> nested)
        {
            if (string.IsNullOrEmpty(addressKey) || string.IsNullOrEmpty(itemId) || amount <= 0 || string.IsNullOrEmpty(itemName)) return;
            var req = new BuildingCargoReqPayload
            {
                AddressKey = addressKey, ItemId = itemId, PlayerId = MPConfig.PlayerId,
                Op = OpPut, ItemName = itemName, Amount = amount, Paid = paid, PricePerUnit = price, Ctx = "boxreturn",
            };
            if (nested != null) req.Nested.AddRange(nested);
            if (MPServer.IsRunning) MPServer.HandleBuildingCargoReq(req, MPConfig.PlayerId);
            else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.BuildingCargoReq, MPConfig.PlayerId, req));
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
            var req = new BuildingCargoReqPayload
            {
                AddressKey = addressKey, ItemId = itemId, PlayerId = MPConfig.PlayerId,
                Op = op, ItemName = itemName, Amount = amount, Paid = paid, PricePerUnit = price, Ctx = ctx,
            };
            if (MPServer.IsRunning) MPServer.HandleBuildingCargoReq(req, MPConfig.PlayerId);
            else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.BuildingCargoReq, MPConfig.PlayerId, req));
        }
    }
}
