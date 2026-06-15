# Passenger ("ride shotgun") system — design plan

Status: **design complete; door-data resolved (derive from wheels + approach point); implementation not started.** Living doc.

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
- **Offset data (no live tool).** A prior `RideProbe` diagnostic (since removed) found the
  driver offset is ≈ zero for open-top vehicles and ~1 m back for closed cars
  (`RideOffsetFor`). There is **no** live probe now — passenger offsets would be hand-tuned
  or captured with a small dev helper we'd rebuild.
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
7. **No driving for the passenger.** The rider is a pure occupant and can never gain
   vehicle control — no driving input or authority while pinned.
8. **Passenger HUD** (bottom of screen) mirrors the driver's in-car menu, trimmed (below).

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
- Capture offsets with a small dev helper (spawn car, drag anchors, dump to table) rather
  than eyeballing numbers in code. A generic passenger-side offset covers all cars until
  authored. (No live `RideProbe` exists anymore — rebuild if needed.)

## Passenger HUD (bottom of screen)

While riding, show the passenger a bottom-of-screen menu modeled on the driver's in-car
menu (`itemPanelUI.vehicleInfo`), but **trimmed**:

- **Sleep** — the same option the driver gets, gated by the **same requirements** the
  driver's sleep uses (reuse the game's sleep-eligibility check; don't reimplement it).
- **Exit Vehicle** — this is the driver's "Park car" action **relabeled**. A passenger
  leaving has *no* parking side-effect (it's not their car), so the wording must not say
  "park" — it just dismounts them (see Exit flow).
- **No "Sell car"** — owner-only.
- **No driving controls** — the passenger never gets vehicle control (decision 7).

The driver keeps their normal menu (park / sell / sleep) plus the new **Lock** toggle.

## Phasing

- **MVP:** generic passenger-side offset; single passenger; eligibility (other-owner +
  unlocked, host-validated); board via existing enter flow + lock-check-first + "door
  locked" popup; pin local player to the ghost (reuse `RideAttach`); hide model + camera
  follow; exit beside car; lock toggle on the in-car menu.
- **P2:** per-`vehicleTypeName` door table (offsets captured via a small rebuilt dev helper) + walk-to-correct-door; driver "kick."
- **P3:** multiple seats / passengers; seat-availability UI.
- **P4 (optional):** visible seated body model.

## Borrow vs build

- **Already ours (reuse):** driven-vehicle sync, ghost-car objects, avatar→ghost pinning,
  per-type offset table, drive-visibility hiding.
- **Build new:** the lock state + UI toggle, host-authoritative passenger eligibility/seat
  assignment, the "door locked" popup, passenger-side offsets (vs the existing driver
  offset), and the exit-beside-car placement.
- **From the collaborator (concept only — MelonLoader/TCP, not portable):** confirmation
  that the freeze + pin + hide-model + camera-lock + reboard-cooldown shape works; we
  already have the pin half.

## Open items — resolved (implementation hooks identified)

1. ~~Does a remote driver render a car?~~ **Resolved:** yes — `VehicleManager._remoteVehicles`
   gives a per-`VehicleId` ghost car object; board targets it.
2. **Board/exit trigger — resolved.** Entering a car is **UI-CTA-driven**: `VehicleOverlay`
   / `VehicleCtaBehavior` call `VehicleController.DriveVehicle()` (which runs the `SetGoal`
   walk-to-entrance). For boarding we add a parallel **"Ride" CTA** on the vehicle overlay,
   shown only when the target is another player's *unlocked* ghost, that runs our board flow
   (lock check → `SetGoal(passengerDoor)` → pin). Exit mirrors it.
3. **Lock UI + passenger HUD — resolved.** The in-car buttons live on **`ItemPanelUI`**
   (`autoParkButton`, `sleepButton`; `vehicleInfo` is a `VehicleInfoPanel`). Sleep
   eligibility is **`VehicleInfoPanel.CanSleep()`** — reuse it verbatim (decision 8). The
   **lock toggle** is a sibling button injected onto `ItemPanelUI`, shown only to the
   driver/owner. The **passenger HUD** mirrors this panel: `sleepButton` gated by
   `CanSleep()` + an **"Exit Vehicle"** button (the park action, relabeled), no sell, no driving.
4. **Rider camera — resolved.** `CameraHelper.SetCamera(GameManager.Instance.vehicleCamera)`
   (a Cinemachine vcam). For the passenger, point the vcam's Follow target at the ghost
   transform; restore on exit.

## Door/seat data — RESOLVED: derive from wheels + the game's approach point

Probe result (`vordv150`/F150, `honzamimic`, `vordtiaravic`): **no car names its doors** —
the body is a single mesh. But every car carries, consistently named:
- four wheels `FrontLeft/FrontRight/RearLeft/RearRight_WheelController` → the car's lateral
  (track) and longitudinal (wheelbase) frame. F150: wheels at **x=±0.77**, front **z=+1.51**,
  rear **z=−1.69** (local).
- the game's driver approach point **`NavmeshTarget`** (left/driver side; F150 at **(−1.21, 0, 0.33)**).

So we **derive** door/seat anchors automatically, per car, **no manual measuring, no position
table**:
- **Passenger door approach** = X-mirror of `NavmeshTarget` (right side): ~**(+1.21, 0, 0.33)**
  for the F150 — matches the collaborator's hardcoded `right * 1.1m` (sanity-confirmed).
- **Lateral seat/standing offset** = right-wheel |x| (≈ track/2) + a small margin.
- **Front vs rear seat z** from the front/rear wheel z (for multi-seat in P3).

Only **seat *count*** is authored — a short `vehicleTypeName`-keyed table (MVP = 1 passenger).
`VehicleHierarchyProbe` (`DIAG:DEVTOOL`) stays so we accumulate wheel/navmesh data for the
remaining car types during normal play. (Its `footprint` line is unreliable — bounds get
polluted by shadow/effect renderers; use the wheels, not the footprint.)
