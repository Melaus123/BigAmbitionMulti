# Passenger ("ride shotgun") system — design plan

Status: **design / not started.** Living doc — update as decisions firm up.

## Goal

Let a player ride as a passenger in **another player's** car: walk up to it, get in,
have your view follow the car as the owner drives, and be set down beside the car
where it ends up when you get out. The owner can lock the car to refuse passengers.

## Hard constraints from the base game (already investigated — don't re-derive)

The decompile is decisive (`context-log-2026-06-14-vehicle-seat-data.md` in the decompile tree):

- The game is **single-occupant, driver only.** No seat list/count, no passenger seat,
  no per-seat or door transforms exist.
- Entering a car = `SetGoal(approachPoint, EnterVehicle)` (a normal **walk** to a point),
  then a teleport-parent to the car origin + a cosmetic seated model swap. **No
  door-open / get-in animation exists** — so we're not failing to match one.
- The only readable per-vehicle anchors are generic `navMeshTargets[]` (driver approach
  points, undifferentiated, no passenger door).
- Vehicles are identified by `vehicleTypeName`; full list via `VehicleTypeHelper.GetVehicleTypeNames()`.

**Consequence:** door/seat geometry and locking must be authored by us — nothing is readable.

## What we ALREADY have to build on (this is most of the plumbing)

The passenger system is **not** greenfield — driving is already synced and avatars
already pin to ghost cars:

- **Driven-vehicle sync.** `MsgType.VehicleSync` ([Protocol.cs](../src/Protocol.cs)) streams each
  player's fleet as `VehicleEntry { VehicleId, VehicleTypeName, isDriving, … }`. So the
  data needed for eligibility — *who owns it* and *are they driving it* — is already on the wire.
- **Ghost car objects.** `VehicleManager._remoteVehicles` ([VehicleManager.cs:181](../src/VehicleManager.cs:181))
  holds one ghost GameObject per `VehicleId`, position-tracked with velocity prediction +
  lerp. **So the passenger already sees a real car object to target — not just an avatar.**
  Board targets `_remoteVehicles[VehicleId].Go`.
- **Avatar-to-ghost pinning.** `RemotePlayerManager.SetRide(playerId, ghostTransform, offset)`
  rigidly pins an avatar onto a ghost at `ghost.TransformPoint(offset)` each frame
  ([RemotePlayerManager.cs:209](../src/RemotePlayerManager.cs:209)). Currently used to seat
  the **driver** inside their own moving ghost. **The passenger feature is the same
  mechanism applied to the local player onto a *remote* ghost at a *passenger* offset.**
- **Per-type offset table** `VehicleManager.RideOffsetFor(typeName)` ([:484](../src/VehicleManager.cs:484))
  — today coarse (scooters → sit-on; cars → 1 m back = driver seat). We extend it with
  passenger/door offsets.
- **Offset-capture tooling.** A `RideProbe` already exists to sample ground-truth seat
  offsets in-game (works in SP too). Reuse it to author passenger-seat/door offsets per car.
- **Driving visibility.** `RemotePlayerManager.SetDriving()` hides the walking model while driving.

## Design decisions

1. **Eligibility.** Ride a vehicle that is (a) owned by **another player** and (b)
   **unlocked.** Lock defaults to *unlocked*. All three facts (owner, driving, lock) are
   host-validated, never trusted from the wire. (Owner + driving already synced;
   lock is new state.)
2. **No in-seat model (initial).** While riding, the rider's own model is **hidden**
   (can't see through windows; aligning a body per car is high-effort/low-payoff). So we
   author **no seat-body anchors** — only a door approach point + an exit offset. A visible
   seated body is a possible later nicety.
3. **No new vehicle-motion netcode.** The rider follows the existing **ghost car**
   (`_remoteVehicles[VehicleId].Go`) via the existing pin mechanism. Perspective + exit
   position derive from that ghost's transform. Nothing new to sync for motion.
4. **Board/exit reuses the driver's interaction** (decision: identical flow), with two
   differences: (a) the approach point is the **passenger door**, and (b) the **lock is
   checked first — before walking**. If locked, show a **"door locked" popup** and do not
   move. Start with a generic passenger-side offset; refine to per-`vehicleTypeName` door
   points later for "walk to the correct door."
5. **Lock UI** = a toggle appended to the **in-car driver menu**
   (`UIs.Instance.playerHUD.itemPanelUI.vehicleInfo` — already shows parking/park/sell
   while the owner is in the car). Shown only to the driver/owner.
6. **Host-authoritative** eligibility and seat assignment.

## Synced state

- Existing (reused): `VehicleEntry { VehicleId, owner, isDriving, type, pose }`.
- New: per-vehicle `locked` bool (driver-set); seat occupancy (MVP = one passenger slot;
  later N); per-rider `{ ridingVehicleId, seatIndex }` folded into the player snapshot.
- Events (reliable): `BoardRequest`, `BoardApproved{seat}`, `BoardRejected{reason}`,
  `Exited`, `KickPassenger{netId}`, `SetLock{locked}`.

## Flow

**Board** — initiated like entering your own car:
1. Client checks the synced `locked` flag first. If locked → "door locked" popup, stop.
2. Else → `BoardRequest{vehicleId}` to host. Host validates owner≠me, unlocked, seat free
   → `BoardApproved{seat}` / `BoardRejected{reason}`.
3. On approval: `SetGoal(passengerDoorPoint, onArrive)` — walk to the door (game's own navmesh).
4. On arrive: hide local model; freeze nav-agent; **pin self to the ghost** at the
   passenger offset (the existing `RideAttach`/`RideOffset` mechanism, applied to the local
   player); lock camera to the ghost; mark `ridingVehicleId+seat` in the snapshot so other
   clients hide our avatar.

**Ride** — local player pinned to `_remoteVehicles[VehicleId].Go` each frame; camera
follows. Driver stops/leaves → grace, then auto-exit.

**Exit** — confirm (same control), or driver kick, or driver-gone grace:
- Unfreeze; place beside the car at the **door/exit offset relative to the car's current
  position**; navmesh-resnap; brief reboard cooldown.

**Lock** — driver toggles in the in-car menu → `SetLock` to host → host enforces future
boards + broadcasts. Default: keeps current riders, blocks new boards.

## Per-vehicle door/seat offsets (authoring)

- Extend `RideOffsetFor` into a table keyed on `vehicleTypeName`:
  `{ driverOffset (exists), passengerDoorApproachOffset, passengerExitOffset }` (+ per-seat later).
- Use the existing **`RideProbe`** to capture offsets in-game per car (drag/sample, dump to
  table) rather than eyeballing numbers in code. Generic side offset covers all cars until
  authored.

## Phasing

- **MVP:** generic passenger-side offset; single passenger; eligibility (other-owner +
  unlocked, host-validated); board via existing enter flow + lock-check-first + "door
  locked" popup; pin local player to the ghost (reuse `RideAttach`); hide model + camera
  follow; exit beside car; lock toggle on the in-car menu.
- **P2:** per-`vehicleTypeName` door table (via RideProbe) + walk-to-correct-door; driver "kick."
- **P3:** multiple seats / passengers; seat-availability UI.
- **P4 (optional):** visible seated body model.

## Borrow vs build

- **Already ours (reuse):** driven-vehicle sync, ghost-car objects, avatar→ghost pinning,
  per-type offset table, RideProbe, drive-visibility hiding.
- **Build new:** the lock state + UI toggle, host-authoritative passenger eligibility/seat
  assignment, the "door locked" popup, passenger-side offsets (vs the existing driver
  offset), and the exit-beside-car placement.
- **From the collaborator (concept only — MelonLoader/TCP, not portable):** confirmation
  that the freeze + pin + hide-model + camera-lock + reboard-cooldown shape works; we
  already have the pin half.

## Open items

1. ~~Does a remote driver render a car?~~ **Resolved:** yes — `VehicleManager._remoteVehicles`
   gives a per-`VehicleId` ghost car object; board targets it.
2. **Board/exit trigger:** reuse the driver's enter interaction (decided). Confirm the
   exact input/prompt hook used for entering a car and mirror it for the passenger door.
3. **Lock UI hook (to investigate):** exact injection point on `itemPanelUI.vehicleInfo`
   (locate the park/sell controls, add a sibling toggle via our uGUI injection).
4. **Rider camera (to investigate):** reuse `GameManager.vehicleCamera` locked to the ghost
   vs our own follow cam.
