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

## REVISED after first test (2026-06-11)
- BUYING IS THE NATIVE FLOW, period (user): pick items, click register, walk up,
  pay.  The F4 buy bypass + direct OpenFullServiceOrderUI invoke were REMOVED.
- Duty = the native Work mechanic mirrored (WorkActivity polled; no keybinds).
- The duty map's single consumer: a staffed-gate patch (NOT YET WRITTEN) so the
  native register-click passes on the customer's machine when a player works
  the register: MPRegisterSync.IsStaffedByOtherPlayer(registerPos).
- BLOCKER first: the [ShopGate] classification case — the visiting client could
  not even pick from the shelf (rival-translation insufficient for an OPERATING
  shop).  Fix that, retest native pickup, THEN find/patch the staffed gate.

## Slices
1. Protocol RemoteSale (111) + buyer-side detect + host validation + MoneyAdjust
   credit + notices. (Stock left local-only — acceptable for the first test run.)
2. Host-side authoritative stock decrement + InteriorSync carry.
3. Polish: self-service path, vehicle orders (skip v1), BizMan-books variant if
   the user prefers native financials over cash credit.

## 2026-06-11 — ShopGate verdict + fix attempt #1 (ShelfGate)

LOG-PROVEN cause of the shelf no-interact: working shops carry a REAL rival GUID in
businessOwnerRivalId (e.g. ''jTnQhoNwhkuGDTnL6K0Sxw=='' AI gift shop — interactable);
the player-owned shop carries the PLAYER id (''Host''), which has NO RivalData record —
players are deliberately segregated out of the rivals cache (Wave-5 rollback guard:
ClientPlayerRoster + synthetic leaderboard rows only).  All other entry flags were
IDENTICAL between working and broken shops (open=True playerOwnedBiz=False rented=False).

FIX ATTEMPT #1 (shipped): Patch_ShelfGate_ShouldShow postfix — forces the shelf CTA ON
when CurrentShopOwner is another lobby player.  Instrumented ([ShelfGate] log).  Open
question it answers next run: does the downstream pickup/basket/pay path resolve the
rival record AGAIN (→ next gate to patch) or run purely on interior cargo data (→ done).
Register staffed-gate patch (IsStaffedByOtherPlayer) remains the step after.

Rejected alternatives: real RivalData records for players (rival AI simulation would
act on them — buy buildings etc.); mapping player shops onto existing AI rivals (wrong
ownership semantics); owner-mode translation (rented=true would give visitors free
restock pickup, not customer purchase).

## 2026-06-11 — register: native-unaided DEAD END; user ruling = SYNTHETIC EMPLOYEE

Run 2 evidence: RegShield swallowed the OnPlaceOrder NRE but native OnOrderCancel
ALSO NREd (probe prefix NREd too) — the local customer-service graph is broken at
every layer without a serving entity.  2-attempt cap reached on letting the native
checkout run unaided.  USER RULING: synthetic employee (most native feel).

SHIPPED v1: duty broadcast carries shop Address; non-owner machines inject a
factory-built EmployeeInstance (EmployeeHelper.CreateAIEmployeeInstance(CustomerService),
ns Entities) into gi.EmployeeInstances assigned to the shop (checkout stations,
Mon-Sun, weeklyHours=168 [?], wage 0, id BAMP_DUTY_*).  Game''s own employee sim
should spawn + assign the NPC = the PROVEN pre-session-hire cross-machine path.
RegGuard (CanOrder postfix) blocks queue-join while register.employeeInstance==null.

OPEN after first run: does the sim spawn the NPC (schedule semantics)?  Does
OnPlaceOrder then succeed or hit the missing-rival-record null next?  v2 items:
hide the NPC body under the worker''s avatar; strip BAMP_DUTY_* from saves
(client-owned-shop case); price display timing (host must price BEFORE visitors).
