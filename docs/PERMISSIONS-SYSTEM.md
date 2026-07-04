# Player-to-Player Permissions System — Design

**Status:** Design / pre-implementation. **No code written yet.**
**Created / last updated:** 2026-06-28
**Related:** `docs/PASSENGER-SYSTEM.md` (vehicle ghost + lock foundations), `context-log-2026-06-14-passenger-system.md`

Confirmed-vs-inferred is tracked inline: **[code]** = read from source this session (re-verify the load-bearing vehicle internals before Phase 2), **[decided]** = user decision, **[open]** = unresolved, needs investigation or a call.

---

## Vision

The multiplayer **"Business" hub button** (today: Loans + Transfers) is meant to be the home for **all player-to-player interaction outside of chat**. This feature adds a **Permissions tab**: granting other players access to things you own. Three permission types, designed and built **one at a time**:

1. **Vehicle permissions** — detailed below (in design).
2. **Housing permissions** — TBD (stub at the bottom).
3. **Business permissions** — TBD (stub at the bottom).

All three share one grant infrastructure (allow-list model + persistence + sync + the Permissions tab UI). That **shared skeleton is built once with vehicles and reused** by the other two.

---

## Shared model (all three permission types)

- **Grant unit:** per-person, **global** — you authorize a *player* (not an individual asset); the grant covers all your assets of that type. **[decided 2026-06-28]**
- **Direction:** the **owner** grants; only the owner can grant/revoke access to their own property.
- **Authority:** **host-authoritative** — the host holds the live allow-list and enforces every access decision (mirrors existing ownership/lock enforcement).
- **Persistence:** grants **persist across sessions**. **[decided 2026-06-28]** The native save format is off-limits (hard project rule), so persistence is a **mod-owned side-car file**. Proposed approach: **each player persists their own *outgoing* grants locally and re-broadcasts them on join**; the host aggregates and distributes. Robust across separate computers (the grant travels with the granting owner) and avoids central-save coupling. Hinges on `PlayerId` stability — see Open Investigations #2.
- **UI:** a new **"Permissions"** tab in the hub. The hub (`MPCanvasUI.cs`) is uGUI, tabbed via `_hubTab` (0=Transfers, 1=Loans → add 2=Permissions); a reusable per-player row widget (`AddHubRow`) and connected-player roster (`MPRestSync.AllPlayers()`) already exist. **[code]**

---

## Vehicle Permissions

### Contract — "keys to the car"

Model: **exactly like handing a friend your real-world car keys.** They use the vehicle as their own; ownership — and every consequence of ownership — stays with you.

**The permitted player CAN (their changes are real and seen by everyone):**
- **Drive** the vehicle.
- Access **cargo + storage**.
- **Refuel** it — **billed to the borrower** (whoever refuels pays, from their own wallet).
- **Repair** it — **billed to the borrower**; the repair actually applies to the car for all players.
- **Bypass the lock.** The lock still exists and still gates players *without* permission; a permitted player holds a "key" and ignores it.

**Ownership stays with the owner — normal game systems attribute to the owner, as always (NOT suppressed):**
- **Parking fines → the owner.** Borrower parks badly → the *owner* is ticketed, exactly like IRL.
- **Taxes, valuation, net worth, depreciation, insurance → the owner.**
- **Damage → the owner's real vehicle**, propagated to everyone. Borrower crashes it → the owner's car is damaged.
- The vehicle remains the **owner's asset** on their books.

**The key constraint (the subtle part):** granting access must **not** inadvertently make the game treat the **borrower** as an owner of that vehicle. The borrower *uses* it; ownership systems must never fire *on them* for it (not on their asset list, not charged its taxes, not ticketed for it, not in their fleet/net-worth). All ownership effects route to the true owner; **none leak onto the borrower.** The borrower only ever pays for services they actively perform (fuel/repair).

**Behavioral rules:**
- Lock unchanged for non-permitted players (and stays cheap / non-persistent — not worth persisting). **[decided 2026-06-28]**
- **Driver arbitration:** if a permitted player is already driving, the owner (or another permittee) who gets in becomes a **passenger** by default. **[decided 2026-06-28]**

### Current architecture (from code reads 2026-06-28 — re-verify before Phase 2)

- **Vehicle ownership:** each player broadcasts `VehicleFleetPayload {OwnerId, Vehicles[]}` (~10 Hz); host builds `_ownerOf` (vehicleId→ownerId) in `PassengerSync`; `OwnerOf(vehicleId)`. **[code]**
- **Lock:** `PassengerSync._unlocked` set, default **LOCKED**, `IsLocked()/SetLock()`, resets on load. Gates (a) passenger boarding via `HostCanBoard()` and (b) cargo via `VehicleStorageSync.OwnerApply()`. Binary/global today. **[code]**
- **Non-owners cannot drive today:** their copy of your car is a **ghost with `VehicleController` stripped** (`VehicleManager` ~line 713). **[code — confirmed 2026-06-28]**
- **Owner drives their own car via native game logic**; that entry point is currently **un-gated** by the mod. **[code — confirmed 2026-06-28]**
- **No per-player permission/whitelist exists anywhere yet.** **[code]**

### Sub-problems

1. **Grant model + persistence + sync + UI** — well understood; the shared skeleton above. Reuses existing chokepoints; lowest risk.
2. **Driving authority hand-off (the crux).** Today a non-owner gets a stripped ghost. To let them drive, the **borrower's machine must simulate the car while the owner's machine follows it as a ghost** — an authority swap, both directions, mid-session, triggered by who takes the driver seat. Biggest feasibility risk. Tightly coupled with #3.
3. **Ownership attribution & leakage prevention.** Owner keeps ALL ownership consequences (fines, taxes, valuation, damage) firing normally and attributed to them; the borrower's now-drivable car must **not** register as borrower-owned. Damage propagates owner-authoritatively. Much may fall out of the authority model; the risk is that un-stripping the controller (in #2) makes the borrower's local game adopt the car as theirs.
4. **Driver arbitration.** Entering your own car is currently native/un-gated; intercept it so the host can route a second entrant to a passenger seat.
5. **Paid services (repair/refuel).** Borrower-initiated, **billed to the borrower**, applied to the owner-authoritative car state, propagated to all (mirrors the existing storage request→owner-apply→broadcast pattern).

### Phased build order (each phase independently testable)

- **Phase 1 — Skeleton:** grant model + persistence + sync + Permissions tab + **lock-bypass for ride & cargo only** (no driving). Low-risk; reuses existing chokepoints (`HostCanBoard`, `VehicleStorageSync.OwnerApply`); builds the infrastructure every later phase **and the other two permission types** reuse.
- **Phase 2 — Driving:** authority hand-off (#2) **+** borrower-side ownership-leakage prevention (#3, the coupled half) — making it drivable and keeping it off the borrower's books are the same change.
- **Phase 3 — Arbitration + attribution:** driver/passenger arbitration (#4) **+** verify/fix that owner-side ownership effects (fines, damage) still attribute correctly through the handoff.
- **Phase 4 — Paid services:** refuel + repair billed to the actor, applied owner-authoritatively (#5).

### Open investigations (read-only; gate the design)

1. **Vehicle simulation/authority model** — **RESOLVED 2026-06-28 (verified in code).** Owner's machine: native `VehicleController`/`CarController`, full physics, owner enters/drives via native logic un-gated by the mod. Every OTHER machine: a "ghost" — `VehicleHelper.CreateAndSpawnVehicle` makes a real vehicle, then the mod **destroys all gameplay components** (`_killVehicleComponents`: VehicleController, CarController, DamageHandler, WheelControllerManager, Fuel/SpeedLimiter/FlipOver wrappers, bike/scooter controllers — `VehicleManager.cs:237-255`) and sets every Rigidbody kinematic (`VehicleManager.cs:739-749`); position is then driven purely by the ~10 Hz `VehicleFleetPayload` (full pos+quaternion + a `Driving` bool — `Protocol.cs:536-584`) and dead-reckoned each frame. **No authority field exists** — authority is implicit and 1:1-permanent. KEY INSIGHT (code's own words, `VehicleManager.cs:234-236`): the strip exists *"so the game stops treating it as a vehicle anyone owns — no map icon, no tickets, not enterable."* → **In this game, "drivable" and "owned" are the same object.** See the Phase 2 strategy fork below.
2. **`PlayerId` stability** — **RESOLVED 2026-06-28 (verified in code).** `PlayerId` is the DISPLAY name (Steam name or `Player-xxxxxx` GUID), user-changeable — **NOT** a stable key. The stable key is **`MPConfig.StableId`** = `steam-<SteamID64>` (or `guid-<…>` for non-Steam), minted once and persisted verbatim forever (`MPConfig.cs:359-383`); it already keys persistent building-ownership across sessions, and the host already maps live `PlayerId → StableId` from the Hello handshake. **→ Persistent grants key on `StableId`; the Permissions UI shows `PlayerId`.** Persistence design unblocked.
3. **Ownership systems map** *(gates Phases 2/3)* — where parking fines, taxes, valuation, damage, and fleet/asset registration fire; confirm they attribute to the owner and find how to keep the borrower's drivable car off the borrower's books.
4. **Repair/refuel internals** *(gates Phase 4)* — where the cost is charged and where state mutates, so the bill can be re-routed to whoever performs the service.

### Phase 2 driving — CONFIRMED APPROACH (2026-06-28, user-approved)

The hard truth (confirmed in code): **a drivable vehicle and an "owned" vehicle are the same object** — the ghost strategy strips the gameplay components *specifically* to stop a non-owner's game treating the car as owned (`VehicleManager.cs:234-236`: "no map icon, no tickets, not enterable"). So letting a non-owner DRIVE re-introduces ownership machinery on their side — exactly the leakage we must prevent. Phases 2 and 3 are therefore the same problem.

**Decision — driver-authoritative local simulation, with the owner as a persistent fallback.** While a permitted player drives, *their own machine simulates the car locally* (instant, zero input lag — exactly how an owner drives today) and broadcasts position to everyone, who render a ghost. This just widens the model the game already uses ("the single machine driving a car simulates it; all others ghost it") from *owner-only* to *owner or permitted driver*. **No steering is ever forwarded to the owner** — the rejected remote-control idea (input round-tripping owner↔borrower at 10 Hz + latency) would be laggy/unplayable.

**Safety rule that makes disconnects clean:** the owner **never destroys their real car**. While borrowed it drops into "follow mode" (kinematic ghost trailing the borrower — the same treatment already applied to others' cars); the borrower holds only a *transient drivable proxy*. On a borrower disconnect the host (which already tracks connections) revokes drive-authority and the owner instantly re-promotes its real car at the last synced position. Nothing is orphaned. **Drive-authority is host-arbitrated** (granted when a permitted player takes the wheel and no one else is driving it; revoked on exit/disconnect). This keeps the **safe fallback** of the owner-authoritative instinct *and* the **responsive feel** of local simulation.

**Spike to prove first:** that a car can carry a *live driving controller but stay OUT of the ownership registry* — the ghost code removes cars from that registry precisely to kill tickets/map-icon (`VehicleManager.cs:708-709`), strongly implying ownership effects key off the registry, not the controller. If it holds, no-leakage falls out for free. Build pieces: (1) drivable-but-unregistered proxy on the driver's machine; (2) authority flip (who-broadcasts / who-follows); (3) fuel/damage/odometer back-prop borrower→owner so the owner's save + ownership systems stay truthful; (4) revert-on-disconnect/exit.

**Spike findings (2026-06-28, verified in decompiled `mono-0.11` source) — FEASIBLE:**
- **Driving has no ownership gate.** `VehicleController.EnterVehicle()` just sets `controlledByPlayer = true` (VehicleController.cs:292) with NO "is this mine" check; `CarController.Update()` polls driving input only when `controlledByPlayer` (CarController.cs:236). So `EnterVehicle()` on any car with a live controller drives it.
- **Owner-charges key off `SaveGameManager.VehicleInstances`, NOT the registry.** `ParkingSimulator.RunHourly()` (ParkingSimulator.cs:119) + `TaxHelper` iterate `VehicleInstances`; `AllPlayerVehicles` is incidental. Net worth excludes vehicles; no insurance/upkeep exists. The ticket loop even SKIPS the actively-driven car (`ActiveVehicleId == id → continue`, ParkingSimulator.cs:122).
- **The mod ALREADY keeps ghosts off the books** via `DeregisterGhostFromSave` (removes the instance from `VehicleInstances`, VehicleManager.cs:600/700) — so a ghost never charges the borrower. That's the no-leakage lever, already pulled.
- **⇒ The drivable proxy = keep the controller (currently stripped) + keep `DeregisterGhostFromSave`/`UnregisterPlayerVehicle` + `EnterVehicle()` on demand.** (The earlier "charges key off the registry" wording was slightly off — they key off `VehicleInstances`, which is even cleaner for us.)
- **In-game unknowns the spike must answer:** does a live controller on a save-deregistered car NRE in its own lifecycle? does it drive well (camera/input/physics)? does `ActiveVehicleId`/exit behave for a non-owned proxy? (The click-to-enter interaction check is sidestepped — the mod calls `EnterVehicle()` itself.)

_Options weighed before deciding (for the record): (A) reconstitute + local handoff — **chosen**, in the form above; (B) owner stays sole simulator while the borrower forwards input — **rejected** for input latency._

### Architectural constraint (ACCEPTED 2026-06-28)

Vehicles only exist in a session while their **owner is online** — they're owner-simulated, and a vehicle that drops out of the owner's ~10 Hz fleet broadcast has its ghost despawned (`Protocol.cs:570-574`). So **a borrowed car is only usable while its owner is in the game** — a forced deviation from the pure "keys IRL" premise. **User accepted**, with the requirement that sudden owner/driver disconnects be guarded against bugs — which the Phase 2 "owner never destroys their real car + host revokes authority on disconnect" rule directly addresses.

### Decisions log
- 2026-06-28 — Per-person **global** grant (not per-vehicle).
- 2026-06-28 — Grants **persist** across sessions; lock stays non-persistent.
- 2026-06-28 — **Driving is in scope** (full "keys" model).
- 2026-06-28 — Ownership consequences (fines/taxes/valuation/damage) **attribute to the owner as normal — NOT suppressed**; the requirement is preventing ownership-*leakage onto the borrower*. (Corrected from an earlier "suppress owner auto-systems" framing.)
- 2026-06-28 — Repair/refuel **billed to whoever performs them**.
- 2026-06-28 — Persistent grants key on **`StableId`** (not `PlayerId`); UI shows `PlayerId`. *(investigation result)*
- 2026-06-28 — Phase 2 driving = **driver-authoritative local sim + owner-as-fallback** (user-approved): driver's machine simulates locally (good feel); owner never destroys the real car (kept as a kinematic follower → safe on disconnect); host arbitrates drive-authority; no steering forwarded to the owner. Spike first: prove a drivable-but-unregistered proxy.
- 2026-06-28 — **Accepted** (user): a borrowed car is usable only while its **owner is online**; sudden-disconnect bugs to be guarded (handled by the Phase 2 owner-as-fallback rule).
- 2026-06-28 — Phase 1 persistence = **host-side session manifest** (`MpManifest.Grants`, StableId-keyed, mirrors `BuildingOwners`) — REVISED from owner-local config: clients never learn each other's StableIds (stable is client→host only at Hello), so the owner can't durably record *who* it granted; the host can. Per co-op session. *(user-approved)*
- 2026-06-28 — Offline grantees ARE toggle-able (user request): the host feeds each owner their grantee list incl. offline; revoke by StableId handle. Grants reactivate when the grantee returns.
- 2026-06-28 — Phase 1 UI = one Vehicle-key toggle per player row (scrolling list, offline grantees included); the 3-column matrix from the approved mockup is deferred until Housing/Business add columns.

---

## Vehicle Permissions — Phase 1 detailed design (ride + cargo skeleton)

> **STATUS — IMPLEMENTED 2026-06-28 (compiles Dev + Release; ships in Release; NOT in-game tested).** Built end to end: `GrantSync` (runtime PlayerId table + host StableId store + local grantee list), the `PermissionGrantSet` / `PermissionSnapshot` / `PermissionOwnGrants` messages, enforcement "a granted player bypasses the lock" at `HostCanBoard` + `VehicleStorageSync.OwnerApply` + the ride gate, a **Permissions hub tab** with a scrolling per-player key list, **offline-grantee toggling**, and **host-side manifest persistence** (`MpManifest.Grants`, StableId-keyed). Changes from the design text below: (1) persistence is the **session manifest**, not owner-local config — clients don't know each other's StableIds, so the host owns the store + persistence (mirrors `BuildingOwners`); (2) **offline grantees** are fully supported (host feeds each owner a grantee list incl. offline; revoke by StableId handle); (3) the UI is **one Vehicle-key toggle per player row** for v1 (scroll + offline included), not the literal 3-column matrix — columns return as Housing/Business land. **Deferred:** a search box, the Housing/Business columns, and in-game (2-machine) verification.

**Goal:** stand up the entire grant infrastructure — data model, sync, persistence, the Permissions tab — and wire it to the two existing enforcement chokepoints so a granted player can **ride and use the cargo** of an owner's *locked* vehicles. No driving yet (that's Phase 2). Low-risk because it mirrors the proven vehicle-lock path almost line-for-line; it builds everything Phases 2-4 **and** the housing/business permissions reuse.

### Mental model
A grant = "**the vehicle lock, but per-player and persistent**." The lock already has a complete send → host-validate → broadcast → join-replay → enforce pipeline (traced 2026-06-28); grants copy it, with three differences: (a) keyed by **`StableId`**, not the live `PlayerId`; (b) **global per owner** (covers all the owner's vehicles → no `vehicleId` in the grant); (c) **persisted** on the owner's machine and re-announced on join.

### Data model (mirrors `PassengerSync._unlocked`)
New `GrantSync` module (or extend `PassengerSync`), replicated read-everywhere like `_unlocked`:
```
// ownerStableId → set of grantee StableIds that owner has handed keys to.
Dictionary<string, HashSet<string>> _grants;
bool IsGranted(string ownerStableId, string granteeStableId);
void SetGrant(string ownerStableId, string granteeStableId, bool granted);
```

### Sync (mirrors `VehicleLockSet` end-to-end — `MPServer.cs:2632-2642`, `MPClient.cs:1010/1088`)
- **Message type** (`Protocol.cs` enum): `PermissionGrantSet`; payload `{ OwnerStableId, GranteeStableId, Granted }`.
- **Owner → host:** `MPClient.SendPermissionGrant(...)` / host-direct `MPServer.HostSetGrant(...)` — copies `SendVehicleLock` / `HostSetLock`.
- **Host validate + broadcast:** `HandlePermissionGrantSet` — verify the sender really is that owner (`SenderIs` + map senderPid→StableId), apply `SetGrant`, re-`Broadcast`.
- **Client apply:** `HandlePermissionGrantMsg` → `SetGrant`. Dispatch cases added to the `MPServer.cs` (~790) and `MPClient.cs` (~321) switches.
- **Join replay:** add a `Grants` list to `PassengerSnapshotPayload` + `BuildSnapshot`/`ApplySnapshot` (`PassengerSync.cs:171-196`), sent from the single-source-of-truth `SendJoinReplayTo` (`MPServer.cs:1555-1576`). Each joiner also **re-announces its own persisted outgoing grants** right after Hello, so grants reattach to whatever host/session it joins.

### Persistence (owner-local config — chosen over the session manifest)
- Each owner persists its **own outgoing grant set** in its local config via `MPConfig` `Get/Set` (`BigAmbitionsMP.cfg.<InstallKey>.json`): key `OutgoingGrants` = JSON array of `{ StableId, LastKnownName }`.
- On join the owner reads and broadcasts it; the host merges into `_grants` and distributes.
- **Why config, not `MpManifest`:** a grant is the owner's *standing decision about their own property* and must travel with the owner **across sessions and across different hosts/computers** (the two-machine goal). The manifest is scoped to one host's MP-session save — wrong scope. The config lives on the owner's machine and follows them. Grants key on `StableId` directly, so we don't need the manifest's reconnect re-keying — the set is just re-announced.

### Enforcement (the two existing chokepoints — "granted bypasses the lock")
- **Riding:** `PassengerSync.HostCanBoard` (`PassengerSync.cs:204`) currently rejects if `IsLocked(vehicleId)`. Change to reject only if `IsLocked(vehicleId)` **AND not** `IsGranted(ownerStableOf(vehicle), requesterStable)`. The host has `StableIdByPlayer` to map both pids → StableIds.
- **Cargo:** `VehicleStorageSync.OwnerApply` — same change (deny only if locked AND not granted).
- Net rule: a granted player holds a key → the lock doesn't apply to them; everyone else is unchanged.

### UI — the Permissions tab (mechanical; widgets already exist)
- Hub `_hubTab` 0=Transfers / 1=Loans → add **2=Permissions**: grow `_hubTabRT/_hubTabImg/_hubTabLbl` 2→3, add `tabNames[2]="Permissions"`, a `_hubPagePermissions` page GO, and the visibility + click-dispatch cases (`MPCanvasUI.cs`).
- Page content: a per-player list (reuse `AddHubRow` + `MakeHubScroll`) showing **(a)** currently-connected players and **(b)** offline players you've already granted (by last-known name), each with an **Allow / Revoke** toggle. Toggling sends `PermissionGrantSet` and updates the local persisted set. Live roster from `MPRestSync.AllPlayers()`; offline grantees from the persisted set.

### One extra piece of replication
Host-side enforcement already has everything (it holds `StableIdByPlayer`). For the **client-side UX** (the borrower's game showing "board" instead of a "locked" toast — `PassengerRide.cs:159-163`) the client must evaluate "am I granted by this vehicle's owner." Simplest: the host tells each client *the set of owners who have granted it* (derivable from `_grants` + that client's StableId) — all the client-side check needs, no full roster required.

### Honest scope of Phase 1
Granted players gain ride + cargo access to your **locked** vehicles (unlocked ones are already open to all), so the visible payoff is modest. The headline — **driving** — is Phase 2 on this same grant. Phase 1's real value is standing up the full grant infrastructure (model, sync, persistence, UI, enforcement pattern) that driving and the housing/business permissions all reuse.

### Files touched (Phase 1)
`Protocol.cs` (enum + payloads), `PassengerSync.cs` or new `GrantSync.cs` (state + IsGranted/SetGrant + snapshot), `MPClient.cs` (send + handlers + dispatch), `MPServer.cs` (validate/broadcast + join-replay + per-client granted-by set), `MPConfig.cs` (outgoing-grants persistence helpers), `VehicleStorageSync.cs` (cargo enforcement), `MPCanvasUI.cs` (Permissions tab + rows). No native save-format changes.

---

## Housing Permissions — "Shared Residency"

**Status:** Design (2026-06-29), grounded in a code investigation of housing/residency (mono-0.11 + mod). Awaiting go-ahead to build; two billing/scope details open (end of section).

### Contract — "keys to the house, with the right to redecorate"
Model: like giving a friend a key to your home **and** letting them rearrange it. They use and reshape the place as their own; the lease, the bills, and the property stay yours. **[decided 2026-06-29]**

**A granted guest CAN, for ALL your homes:**
- **Enter** freely — bypass the game's rented-only entry gate.
- **Live there:** sleep, and use personal storage, wardrobe, and appliances exactly as the owner does.
- **Change the home:** move / place / remove / alter furniture (interior design). Their edits are real, synced to everyone, and persist on the owner's save.

**Ownership + cost stay with the owner (NOT transferred, NOT suppressed):**
- **Rent, property tax, the lease → the owner.** The guest never pays for the house.
- The building stays the **owner's** asset on their books; the guest's game must never adopt it as rented-by-them (the no-leakage rule, exactly as for vehicles).

### What the code gates today (investigation 2026-06-29 — re-verify before build)
- **Residency = a rented building.** `BuildingRegistration.RentedByPlayer` (bool) — BuildingRegistration.cs:44. Bought property = `GameInstance.realEstate` (List<RealEstate>) — GameInstance.cs:155. **[code]**
- **Entry IS gated.** `BuildingHelper.CanEnterBuilding(address)` (BuildingHelper.cs:153-165): true if `RentedByPlayer` (your own rental); else only if the building is an OPEN business; residences tagged `cantenterunlessrented` are otherwise closed. → **You can enter only your OWN residence; a guest currently cannot enter the owner's home.** (The old stub's "anyone can enter" was imprecise.) **[code]**
- **Sleep is owner-gated.** `BedController.cs:22` / `SleepActivity.cs:22`: sleep requires `BuildingManager.IsPlayerOwnedBusiness` (inside a building you own/rent). A guest fails this. **[code]**
- **Furniture edit is owner-gated.** `InteriorDesignerController` opens only for the building's owner/renter. **[code]**
- **Storage / wardrobe / appliances are NOT owner-gated** — only *location*-gated (anyone physically inside uses them: StorageShelfController.cs:42-66, WardrobeController.cs:11-22). → Once entry is granted, these need NO extra work. **[code]**
- **The mod adds NO building-access check today.** `InteriorSync.HandleRequest` (InteriorSync.cs:67-99) subscribes ANY player to ANY building's interior with no ownership check. Ownership is synced host-side: `MPServer.BuildingOwners` (addressKey→renter pid, MPServer.cs:22) + `MPServer.BuildingRealEstateOwners` (addressKey→buyer pid, MPServer.cs:28), shipped in WorldSnapshotPayload (Protocol.cs:397-401). **[code]**

### Build plan (chokepoints — mirrors the vehicle grant pattern)
1. **Grant infra — extend `GrantSync` to per-TYPE grants (Vehicle | Housing).** A grant becomes (kind, ownerStable → grantee stables); `PermissionGrantSet` + the manifest carry the kind; vehicle paths pass `Vehicle` and stay behavior-unchanged. Realizes the doc's "one skeleton, reused" intent and lights up the Permissions-tab columns.
2. **Entry** — Postfix `BuildingHelper.CanEnterBuilding`: if it returned false but the building's owner (`BuildingOwners`/`BuildingRealEstateOwners`) granted me Housing → return true. [NEW]
3. **Interior sync (host-authoritative)** — gate `InteriorSync.HandleRequest`: serve the interior only to the owner + granted guests (mirrors `HostCanBoard`). [NEW]
4. **Sleep** — patch the bed/sleep gate so a granted guest inside the owner's home may sleep. [NEW]
5. **Furniture edit** — open the interior designer for granted guests; their changes ride the existing `InteriorSync` (real + synced + persisted). [NEW — the owner's "make changes" requirement]
6. **Storage / wardrobe / appliances** — no work (location-gated once inside).
7. **No leakage** — verify entering/editing never bills the guest or registers the home as theirs (rent/tax key off the owner's `BuildingRegistration`; the guest's game must not flip `RentedByPlayer`).
8. **UI** — add a **Housing** toggle to each Permissions-tab row (the deferred per-type matrix begins to materialize).

### Resolved details (2026-06-29, user)
1. **Scope = homes, not businesses.** A Housing grant covers your **residential** buildings; businesses stay under the separate Business permission (type #3). **[decided]**
2. **Furniture billing.** Placing furniture bills NOTHING — the purchase happens earlier, when the guest manually brings the item in (already bought). Only **flooring + wallpaper** are paid at change-time → **billed to the actor** (whoever makes the change). **[decided]**
3. **Guest-placed furniture auto-owns to the OWNER.** Anything a guest places becomes the owner's (it lives in the owner's authoritative interior). Survives revocation, and avoids a separate guest-owned-item dynamic + its bugs. The guest's bring-in is effectively a gift. **[decided]**

### Furniture interactions (functionality — make it all work for a guest)
Investigation 2026-06-29 (Explore): the ownership gate is **NOT universal**. `BuildingManager.IsPlayerOwnedBusiness` (BuildingManager.cs:141) = `buildingRegistration?.RentedByPlayer`. Only **4 controllers** gate on it (or on `RentedByPlayer`):
- **BedController** (`!IsPlayerOwnedBusiness`, inline) — sleep.
- **TVController** (`CanUseTV => IsPlayerOwnedBusiness`, property).
- **ComputerController** (`RentedByPlayer`, inline ~lines 29/43).
- **WorkoutMachineController** (`CanUseMachine()` → `!IsPlayerOwnedBusiness`, line 127).

Everything else a home contains is **already open** to anyone inside (toilet/sink/shower via HygieneItemController, fridge, wardrobe, shelves, seats/couches/chairs) → no work once entry is granted.

**Approach: a SCOPED override** — a "granted guest in this building" check that flips the gate ONLY during the specific furniture interaction (a per-method Prefix sets a flag; an `IsPlayerOwnedBusiness` Postfix returns true while the flag is set; cleared after). This unblocks the 4 items WITHOUT making the guest read as the building owner anywhere else (**no ownership leakage** — they can't sell/manage/claim the property). Prefer `CanUseTV`/`CanUseMachine` Postfixes where a clean gate method exists; use the scoped flag for the inline gates (bed, computer).

### Write-path architecture (interior edits — fridge / furniture / flooring) — **owner-always-authority via save data, FINALIZED 2026-06-29**
A guest may edit while the owner is ONLINE (anywhere — they need NOT be home). **KEY: the interior DATA lives in the owner's save AT ALL TIMES** — `BuildingRegistration.itemInstances` (Dict; the fridge + its `cargoInstances`), `.interiorDesigns`, `.retailPrices`, `.dirtSpots`; `InteriorSync.BuildSnapshot` reads them from `SaveGameManager.Current.BuildingRegistrations`, NOT from loaded GameObjects (InteriorSync.cs:408-504). So the owner's machine is the **ALWAYS-LIVE authority** — no need to keep the room loaded, **no host-cache, no reconciliation, no session-end hole** (supersedes the earlier owner-away/reconciliation sketch).
- **Guest edit → granular request to the owner** (mirrors `VehicleStorageSync` RequestTake/Put → OwnerApply → OnResult — the proven "edit another player's cargo" path): fridge = cargo take/put; furniture = item place/move/remove; flooring = a design change. Payload: (addressKey, the delta).
- **Owner applies to `reg.itemInstances` / `reg.interiorDesigns` ON DEMAND** for the named building, regardless of where the owner's avatar is — cargo ops are `ItemInstance` methods on the data object, and a loaded controller shares the same `ItemInstance` ref, so inside-or-not is uniform. Then the owner pushes that building's snapshot → host broadcasts → guest reconciles (optimistic until confirmed). Persists in the owner's save normally.
- **Push change needed:** let the owner push ANY owned building's snapshot on demand when an edit arrives (today `TickClientOwner` pushes only `_localOwnerAddress`).
- **Constraint (accepted, user):** owner must be ONLINE — same as borrowed cars. A guest can't edit a fully-logged-off owner's home.
- **DISCONNECT SAFETY (BINDING, user 2026-06-29):** if the owner disconnects while a guest is INSIDE the home, the guest must NOT be softlocked — the interior may go stale / "empty", but the guest MUST be able to EXIT. EXIT must never be gated (the `CanEnterBuilding` grant patch is ENTRY-only; exit is a local action). Don't eject/freeze the guest mid-interior on owner DC; a mid-edit request just fails gracefully. (Mirrors the vehicle "graceful on disconnect" rule.)
- **Rules:** guest-placed furniture **auto-owns to the owner**; **flooring/wallpaper billed to the actor**; fridge food the guest takes goes to the guest, food they add becomes the owner's.
- **Build order:** fridge cargo first (closest to VehicleStorageSync) → furniture place/move/remove → flooring/wallpaper billing.

### Furniture + flooring write-path (interior designer) — **BUILT 2026-06-29 (compiles Dev+Release)**
The interior designer is a **batched editor with a revert system + a debit-on-close**, NOT granular ops — so the cleanest guest path is "edit locally, forward the RESULT on close," not per-op forwarding.
- **Chokepoints (game):** PLACE = `FurnitureToolSetup.PlaceItemFurnitureTool` (FurnitureToolSetup.cs:51); MOVE = `InteriorDesignerController.SetPositionRotationAndAttachment` (:523); REMOVE = `SellToolSetup.AddToSoldList` → `BuildingRegistration.RemoveItemInstanceFromBuilding` (:752); add/remove on the reg = `BuildingRegistration.AddItemInstanceToBuilding` (:746). Flooring/wallpaper = `BuildingRegistration.interiorDesigns` (:81), changed via FloorToolSetup / InteriorElementTool. **Cost** is batched + debited on close: `InteriorDesignerController.TryChangeBalance` (:278) → `HandleOnClose` → `GameManager.ChangeMoneySafe` (:375-387).
- **Design-mode GATE:** the design button is hidden for non-owners (`CurrentBuildingUI.cs:158`, gates on `IsPlayerOwnedBusiness`) and the Furniture/Duplicate tools are disabled on open (`:233-234`). Must ungate for a granted guest.
- **CHOSEN approach — Option 2 (edit-local, forward-on-close):** (1) ungate design mode for a granted guest (CurrentBuildingUI:158 + :233-234); (2) the guest edits their LOCAL synced reg in design mode — **flooring/wallpaper cost debits the GUEST automatically** (they're the local player → "billed to the actor" for free; furniture is pre-bought, no placement cost); (3) on `HandleOnClose`, the guest **forwards the edited interior snapshot to the owner**; (4) the owner **ADOPTS** it (applies to its reg via the existing snapshot-apply path) → persists + `PushOwnedBuildingNow` → re-sync. Furniture auto-owns to the owner (it lands in the owner's reg). Works owner-anywhere-online (owner applies to its always-present reg).
- **EDGES to handle/accept:** (a) **mid-session clobber** — while a guest is designing, the owner's (or another player's) snapshot could overwrite the guest's in-progress LOCAL edits → suppress applying received snapshots for that building while the local guest is in design mode. (b) **cargo-clobber** — the close-forward snapshot includes the fridge cargo; if cargo changed during the design session it could revert → exclude cargo from the design-forward, or accept the rare edge.

### Deferred refinements (documented so they survive the context window)
- **Single-user furniture (one at a time):** **computer + workout machine** limited to ONE user at a time; **TV + bed have NO limit**. **[decided 2026-06-29 — deferred to a refinement, NOT v1]** The game's `ItemController.Occupied` is LOCAL-only (per-machine) so it does NOT stop two machines using the same item at once; a real cross-machine single-user lock needs host-arbitrated occupancy (the vehicle-seat pattern) for the computer + workout machine. Build after the H1+H2 batch tests out.
- **Access refresh on building-ownership change:** `RefreshBuildingAccess` currently fires on grant/roster change; also fire it on rent/buy/vacate so a guest gains/loses access to newly-acquired/sold homes without a grant toggle.
- **Fridge guest-UX nuances (built 2026-06-29; revisit after the test):** (1) a guest "eats" by **taking the item to hand** then eating from hand — there is no in-place eat (reuses the take path, which avoids an item dup; an `OpConsume` op could restore in-place eating later). (2) **No walk-to-fridge** for a guest — the action routes immediately (the native `MoveTowardsEntity` walk is skipped by the Prefix). (3) **Putting in from a pushed cart / vehicle source** (vs a held item) may not consume the source on `OnResult` — the held-item case is clean; the cart case is an untested edge. All three are cosmetic/edge; functional for v1.

### Decisions log (Housing)
- 2026-06-29 — Model = **"keys to the house + the right to redecorate"** (user): full use (enter / sleep / storage / wardrobe / appliances) PLUS furniture editing; owner keeps paying for the house; no ownership/cost transfer to the guest. *(user foundation)*
- 2026-06-29 — Scope = **residences only**, separate UI permission; furniture placement bills nothing (pre-bought); **flooring + wallpaper billed to the actor**. *(user)*
- 2026-06-29 — **Guest-placed furniture auto-owns to the owner** (simplicity; survives revocation). *(user)*
- 2026-06-29 — **Single-user limit** on computer + workout machine (one at a time); TV + bed unlimited — **deferred refinement, not v1**. *(user)*
- 2026-06-29 — All other home furniture (toilet/sink/shower/fridge/wardrobe/shelves/seats) is already open to anyone inside → guests get it free once entry is granted. *(investigation)*
- 2026-06-29 — **Guest may edit while the owner is online but AWAY** (user choice). Implementation **finalized = owner-always-authority via save data** (supersedes the host-cache/reconciliation sketch — the interior data is always in `reg.itemInstances`, so the owner applies edits on demand + pushes; works owner-anywhere-while-online, no reconciliation, no session-end hole). Mirror VehicleStorageSync for the granular forwarding. Build fridge cargo first. *(user)*
- 2026-06-29 — **DISCONNECT SAFETY (binding):** owner DC while a guest is inside must never softlock the guest — stale/empty interior is fine, but they MUST be able to EXIT (exit stays ungated; no eject/freeze; mid-edit requests fail gracefully). *(user)*

---

## Business Permissions

**TBD** — to design after vehicle permissions. Foundations seen in code: `BusinessInfo.RentedByPlayer` / `OwnerPlayerId` / `BusinessOwnerPlayerId`; existing **owner-authority checks** already gate business edits, sale, listing, and cashier registration — that's the enforcement pattern to extend. **[code]**
