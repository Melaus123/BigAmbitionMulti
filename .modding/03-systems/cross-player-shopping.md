# Wave 2: cross-player shopping (design, 2026-06-11)

Player B walks into player A's store and buys something → B pays, A earns, shelf
stock drops everywhere. N-player by design (any buyer, any owner).

## Confirmed groundwork (recon)
- `CashRegisterController : EmployeeStationController` (dump 121037) is the
  player-shopping surface: `Interact()` → full-service (`InteractAsFullService` →
  `OpenFullServiceOrderUI` → `OnPlaceOrder(orderedCargoInstances, orderedVehicles…)` →
  `MakeFullServiceSelfPurchase(Order, tpc)`) or `InteractAsSelfService`.
  "SelfPurchase" = the native player-buys-at-own-register flow — in the shared
  world EVERY machine's save marks player businesses as "the player's", so the
  native flow runs locally on B's machine unmodified (B pays, local stock drops).
- Ownership: `BusinessOwnerPlayerId` already rides BusinessSync (BusinessSync.cs:472)
  — every machine can answer "whose shop is this" by addressKey.
- Interiors are HOST-authoritative subscription snapshots (InteriorSync): host
  polls subscribed buildings, broadcasts diffs. Consequence: B's local stock
  decrement is NOT authoritative — if the host world doesn't apply the sale,
  B sees ghost restock on re-entry and A never sees the sale.
- Cash is per-player (CashByStableId / GetKnownCash); MoneyAdjust (108) +
  NotifyParty already move cash cross-player (loans/gifts machinery).

## Money + stock flow (v1)
1. BUYER (B): native purchase completes locally (B's cash down — authoritative,
   cash is per-player). Patch the purchase-completion point; if
   `OwnerOf(addressKey) != MPConfig.PlayerId` and owner is a real player →
   send **RemoteSale{AddressKey, BuyerId, Total, Items[{ItemName, Count, UnitPrice}]}** to host.
2. HOST: validate (known business, owner exists, total sane vs item prices) →
   a) apply the sale to ITS world: find the building's interior, decrement the
      matching CargoInstances (stock authority; InteriorSync diff then carries
      it to everyone subscribed, including A's machine);
   b) credit OWNER A's cash via the existing MoneyAdjust path (+Total) with a
      private notice ("Sale at <shop>: +$N — sold to <B>");
   c) ledger line in the Hub feed (visibility for both parties).
3. OWNER (A): nothing special — cash credit + notice arrive via existing paths;
   stock via InteriorSync.

## Double-count guard
B's machine ran the NATIVE self-purchase: B's local copy of A's business also
recorded revenue in its books. That copy is non-authoritative (BusinessSync /
host snapshots own business state) — verify after slice 1 that the host's
business books do NOT also natively register the sale (they shouldn't: the
purchase ran only on B's machine), so A is paid exactly once via MoneyAdjust.
If the game's own business-revenue bookkeeping is wanted instead (shows in A's
BizMan financials), v2 can apply the sale through the business's native
register-sale API on the host and drop the MoneyAdjust — pick ONE channel.

## Hook point (to be confirmed by probe before patching)
Candidate: `CashRegisterController.OnPlaceOrder` (buyer side, has the cargo list
and the register's building context). MUST verify with a one-run probe:
- does it fire for both full-service and self-service checkout?
- where does Total come from (Order? sum of CargoInstance prices)?
- addressKey of the building containing the register.
Probe first, patch second (two-test cap applies).

## Slices
1. Protocol RemoteSale (111) + buyer-side detect + host validation + MoneyAdjust
   credit + notices. (Stock left local-only — acceptable for the first test run.)
2. Host-side authoritative stock decrement + InteriorSync carry.
3. Polish: self-service path, vehicle orders (skip v1), BizMan-books variant if
   the user prefers native financials over cash credit.
