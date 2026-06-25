# BigAmbitionsMP — Anti-Pattern Registry

A living list of bug *classes* this mod has repeatedly created, so we catch the
next instance the moment it's written instead of waiting for a player to feel it.

## How to use this

**Before cutting a release** (and any time you add scene/UI/poll code), run each
class's **detection grep** below. For every hit on a hot path, confirm it has the
documented guard — or fix it. A grep hit is not a bug; an *unguarded* hit on a
hot path is.

This is enforcement by detection, not by noticing symptoms: a class like the
scene-scan one only *stutters* when it's frequent enough, but the grep catches it
at any frequency.

When you fix a new instance of a known class, add it to that class's "Known
instances" list. When you discover a *new* recurring class, add a new section
using the same five fields (Pattern / Why it bites / Detection / Safe fixes /
Known instances).

---

## Class 1 — Expensive whole-scene scan on a hot path

**Pattern.** `FindObjectOfType` / `FindObjectsOfType` / `Resources.FindObjectsOfTypeAll`
/ `GameObject.Find*` called repeatedly (per-frame, or on a short poll) while
in-game, with no cache, latch, in-game guard, or stop condition. Each call walks
the entire object table (tens of thousands of objects), ~50ms each in the city.

**Why it bites.** FPS stays high because most frames are fine; only the scan
frames stall. The result is "high FPS but erratic frametime" — a periodic micro-
stutter. It's invisible to GC and to per-frame logic timers, and it's hard to
attribute because the cost lands in engine code, not in your bracketed C#.
Real symptom (0.1.4): `IsLoadingOverlayUp()` scanned the whole scene for a
`LoadingScreen` every 0.25s **forever** after load → a razor-sharp ~257ms-period,
~54ms hitch, multiplayer-only. Matched a field report verbatim: "high FPS, frametime
all over the place… expensive call fixed by caching or throttling."

**Detection grep.**
```
rg -n "FindObjectOfType|FindObjectsOfType|FindObjectsOfTypeAll|GameObject\.Find" src/
```
For each in-game hit, ask: is it cached / latched / guarded out of in-game / per-
message? If you can't *see* the guard in the code, treat it as suspect.

**Safe fixes (in order of preference).**
1. **Don't scan — go direct.** Use a singleton/registry the game already exposes
   (`UIs.Instance.gameSpeed`, `UIs.Instance.playerActivityUI`, the `TrafficManager`
   registry). Best fix: the scan disappears entirely.
2. **Cache + early-return on hit**, cleared on game load (`FindCBC`, `GetVehiclePool`,
   `GetMachine`, `FindHeldTemplate`).
3. **Latch a one-way "done" state** when the thing being looked for can't recur
   without a reload (`IsLoadingOverlayUp` → `_overlayConfirmedGone`, re-armed when
   `PlayerController` goes null).
4. **In-game guard** for menu-only scans: `if (IsInGame()) return;` (`TickMenuIntegration`,
   `TickThemeCapture`).
5. Run it **per-event/per-message**, not on a poll (the `GameStatePatcher` apply
   handlers).

> **Throttling alone is NOT a fix.** The LoadingScreen scan *was* throttled to
> 0.25s and still stuttered — 4×/sec of a 54ms walk is a felt hitch. A throttle
> only helps when paired with a real **stop condition** (cache/latch/guard).

**Known instances (all fixed).**
- `MPCanvasUI.IsLoadingOverlayUp` (`src/MPCanvasUI.cs:2501`) — LoadingScreen scan
  every 0.25s forever → **latch**. Fixed 0.1.4 (the 257ms MP stutter).
- `MPCanvasUI.TickMenuIntegration` (`src/MPCanvasUI.cs:3204`) — MainMenuController
  scan in-game → **`IsInGame()` guard**. ("twice a second, forever … rhythmic car stutter")
- `MPCanvasUI.TickThemeCapture` (`src/MPCanvasUI.cs:3146`) — font/sprite scans →
  **in-game guard + `ThemeReady` early-out**.
- `TrafficSync.GetVehiclePool` (`src/TrafficSync.cs:211`) — VehicleComponent walk at
  10Hz (60–80ms) → **registry-first + 10s cache**.
- `MPRestSync.GetActivityUiCached` (`src/MPRestSync.cs:590`) — PlayerActivityUI walk
  3×/0.5s (biggest mod frame cost) → **direct singleton**.
- Char-gen name prefill (`src/MPPatches.cs:231`) — per-frame FindObjectsOfType poll
  drained SP 90→12fps → **one-shot on `IntroCharacterCustomizer.Start`**.
- `MPCanvasUI.TickSuppressBlackOverlay` — `FindObjectsOfType(Canvas)` every 2s FOREVER while the (rarely-present)
  BlackOverlay was uncached: a throttle with no stop condition. Now scans only in a short window armed by each
  building entry (`ArmBlackOverlayScan`, called from `Patch_DelayedEnterBuilding`). Fixed 2026-06-17.

**Audit status.** 2026-06-14: full sweep of all 24 call sites — 0 latent. 2026-06-17 re-audit (after the
passenger / bug-report-PR churn) found ONE new instance (`TickSuppressBlackOverlay`, above), now fixed.
Re-verify before each release via the grep above — "eradicated" only lasts until the next poll/scan is added.

**Watch-list (safe today, fragile if changed).**
- `RemotePlayerManager.FindHeldTemplate` (`src/RemotePlayerManager.cs:875`) — uses the
  heaviest API (`Resources.FindObjectsOfTypeAll(Transform)`); safe only because
  double-cached. Don't loosen the cache.

---

## Class 2 — NRE from a Unity "fake-null" object in a patched/hot path

**Pattern.** A Harmony patch body or a per-frame read dereferences a game object
(vehicle, character, NavMeshAgent, animator, controller) that the game can
**destroy or swap mid-operation**. Unity's overloaded `==` reports the destroyed
reference as null, but *using* it (method call, field access) still throws
`NullReferenceException`. The NRE lands inside a hot game method we've patched.

**Why it bites.** An NRE thrown from inside a patched game method can abort the
game's own logic for that frame — producing hangs and stuck states (taxi boarding
hang, ride that never ends), not just a logged error. It's intermittent (only when
the swap races our access), so it's hard to reproduce.

**Detection grep.**
```
rg -n "HarmonyPatch|Finalizer|GetComponent|\.transform|\.gameObject" src/ | rg -i "patch|finalizer"
```
For each patch that dereferences a game object, ask: can that object be destroyed
between the game obtaining it and our code touching it?

**Safe fixes.**
1. **Unity-aware null guard** at the top of the body: `if (obj == null) return;`
   (uses Unity's destroyed-object semantics).
2. **Harmony `Finalizer`** that swallows the NRE for that specific method when the
   object raced away — the established pattern here (used as a shield, logs once).
3. Re-resolve the object from a live registry instead of holding a stale reference.

**Known instances (all fixed).**
- NavMesh NRE shield, taxi boarding (`fd70d09` / `7fbe85e`).
- Gley vehicle NRE shield, taxi-hang root cause (`fd70d09`).
- `EntityController.UpdateNavMeshTargets` finalizer shield.

---

## Class 3 — Reflection / hardcoded field access that silently breaks on a game update

**Pattern.** Reading a game type via reflection (`AccessTools.Field/Property`,
`GetField`, field-name strings) or assuming a specific field/shape. Big Ambitions
is in active Early Access — field names, shapes, and which field holds the live
value **change between versions**.

**Why it bites.** It fails *silently* — no crash, just a wrong or stale value, or a
scan that quietly misses newly-added entries. Far harder to catch than a compile
error because the build is fine and only the behavior is wrong.

**Detection grep.**
```
rg -n "AccessTools\.(Field|Property)|GetField|GetProperty|\.GetValue\(" src/
```
For each, confirm it tolerates a missing/renamed field (null-checks the lookup) and
reads the *live* source, not a stale snapshot field.

**Safe fixes.**
1. Prefer a stable public API / singleton over reflection where one exists.
2. Validate the reflected member resolved (non-null) on load; log loudly if not.
3. After a game version bump, re-run the detection grep and re-verify each site.

**Known instances.**
- Activity-duration scan missed the new 0.11 fields → recurring skip-cancel bug
  (`42be2e9`).
- `GameStateReader.GetGameTime` read a stale save-file time field instead of the
  live clock (found 2026-06-14; a minor read-source bug, separate from the stutter).
- `GameStateReader` GameSpeedController watchdog members (`Paused` / `isFastForwarding` /
  `isTimeControlDisabled`) had null-checked reads but NO loud load-time validation — a rename would *silently*
  disable the time-freeze watchdog (re-opening the 2026-06-10 hard-lock). Added an on-load non-null assert that
  `LogError`s (the safe-fix #2 above). Fixed 2026-06-17.

---

## Class 4 — New host-authoritative state not replayed to joiners / hot-joiners

**Pattern.** A new host-authoritative state (building ownership, interiors, traffic, parked
cars, market, rivals, loans, **passenger locks/seats**, …) is kept in sync via *incremental*
updates, but is **not** included in the initial state a player receives when they **join**
(or hot-join mid-session). The host has it; a fresh joiner does not — until the next
incremental update, which for event-driven state may be **never**.

**Why it bites.** Invisible to the host (always has the state) and to the person who added
it (they test by playing *as host*). Only a **joiner** sees the gap, and only for state that
doesn't refresh on a timer. Classic symptom: "the player who joined sees X missing/wrong."
It's forgotten every time because adding the state and adding its *join replay* are two
separate edits in two separate places.

**Detection.** When you add a new authoritative state, ask: *is it in the join/connect
snapshot path?* Every authoritative subsystem should send a full snapshot to a connecting
peer.
```
rg "Snapshot|OnPeerConnected|HandleHello|Welcome|SendInitial" src/
```
The new state should appear in that on-connect send. If it only has an incremental
broadcast and no on-connect send, it's this bug.

**Fix.** Add a full-state snapshot for the new state, sent to a peer in the **on-connect /
WorldReady** flow alongside the existing ones (BusinessSnapshot, ParkedSnapshot, …). Make
sure a mid-session hot-join hits the same path.

**Known instances.** The on-connect send list is long *precisely because this keeps
recurring*: business, interior, parked, traffic, market, rivals, appearance, loans, … Most
recent: passenger **locks + seat occupancy** (added 2026-06-15) — shipped *with* a
`PassengerSnapshot` on join, specifically to not repeat this class.
- `gi.marketEvents` (shortages/hype) — broadcast only on change (hash-gated), missing from the on-connect send →
  a hot-joiner saw no active market events, possibly forever. Added `SendMarketEventsTo` to both on-connect paths
  + cleared the hash on `Reset`. Fixed 2026-06-17.
- player-run shop **retail prices** were seeded on-connect only for AI businesses, not player shops — a hot-joiner's
  price-competition sim ran on stale inputs until an owner re-priced or the joiner entered the shop. The host now
  caches every price payload it sends/relays (`MPPriceSync._hostCache`, fed via `HostRecord` from
  `BroadcastRetailPrices`) and replays them with `SendPlayerShopPricesTo` on both on-connect paths. Fixed 2026-06-17.

---

## Class 5 — New MP state vs. the native save (leak-in / persist-out)

**Pattern.** Our MP-only state collides with the game's single-player save two ways, both
easy to forget:
- **Leak-in:** MP-only *runtime* objects (remote ghosts, replicated props) get written
  **into** the native save (e.g. ghost vehicles landing in `gi.VehicleInstances`),
  corrupting/bloating a save that's later loaded in single-player.
- **Persist-out:** a net-new authoritative state that *should* survive a save/load isn't in
  the native save at all, so it's silently lost on reload.

**Why it bites.** The save format is the game's, not ours — modifying it risks corrupting
saves (a feature we must never break), and the gap is invisible until someone loads.

**Rules / decision tree for every new authoritative state:**
- **Never modify the native save format** (no new fields on `gi` / the `.hsg`). World/economy
  state already lives in the host's native save and re-syncs to clients via the join replay
  (Class 4) on load — leave persistence to the game.
- **Runtime / avatar state** (positions, who's-driving/riding) → **not saved**; re-established
  on load. (Passenger occupancy is here.)
- **MP-only runtime objects** → keep them **out** of the save (de-register before the game
  serialises — see `DeregisterGhostFromSave`).
- **Net-new persistent settings** (e.g. a vehicle lock) → either accept a reset on load, or
  persist in a **side-file** keyed to the session — **never** inside the native save.

**Detection.** Adding state? Ask: *could this be written into the save?* (→ de-register) and
*should it survive a load?* (→ side-file, not the native save).
```
rg "VehicleInstances|SaveGameManager.Current|DeregisterGhost|gi\." src/
```

**Known instances.** Ghost vehicles kept out of `gi.VehicleInstances`
(`DeregisterGhostFromSave`, "never save data"). Passenger occupancy = runtime (resets on
load via `PassengerSync.Reset()`); passenger lock = runtime for MVP (resets), side-file if
we ever want it to persist — never injected into the save.
- Synthetic register cashiers (`BAMP_DUTY_*` EmployeeInstances + their injected WorkShifts) were stripped at
  world-ready + `Reset` but NOT on the SAVE path — so a coordinated/autosave with a remote player on register-duty
  leaked them into the `.hsg`, contaminating a single-player load (where world-ready never fires). Added
  `MPRegisterSync.StripSyntheticsForSave` (strip ALL — live included — then restore the exact objects after
  serialization) to `PerformLocalSave`, beside the ghost/rival-state strips. Fixed 2026-06-17. **Lesson: the save
  choke point must strip EVERY MP-only injected object, not just the ones the world-ready pass cleans.**

---

## Class 6 — Cloning a native UI control inherits its serialized behavior

**Pattern.** We `Instantiate` a native UI control (a Button, a list row) to reuse its
styling, then wire our own behavior onto the clone. But the clone carries the original's
**serialized state** — most dangerously its **persistent `onClick` listener** set in the
prefab (e.g. the Park button's persistent click → `ClickPark` → `ExitVehicle`).
`RemoveAllListeners()` clears only **runtime** (`AddListener`) listeners, NOT persistent
ones — so the clone fires BOTH our action and the original's.

**Why it bites.** It looks wired correctly (we added our listener and it works), so the bug
shows up only as a spurious *second* action on click (our button "Unlock" also exited the
vehicle). The clone also drags along other serialized components — localization drivers that
re-overwrite our label, the source's icon, key-hint labels.

**Detection grep.**
```
rg -n "Instantiate\(" src/ | rg -i "button|panel|row|cell"
```
For each cloned control, confirm `onClick` was **replaced** (not just `RemoveAllListeners`),
and any inherited driver components (localization, etc.) were stripped.

**Safe fixes.**
1. **Replace the whole event, don't clear it:** `btn.onClick = new Button.ButtonClickedEvent();`
   then `AddListener(...)`. This drops persistent + runtime listeners. The established pattern
   here — `MPPhoneButton` (comments it: "clear template listeners"), `MPHubNativePage`, `MPCanvasUI`.
2. **Strip inherited driver components** you don't want (e.g. a `TextLocalizationComponent`
   that would re-localize your label out from under you).
3. **Build from scratch** when you don't actually need the native styling (`PassengerHud`).

**Known instances (all fixed).**
- Menu / phone "Multiplayer" buttons cloned from native rows → reset `onClick`
  (`MPPhoneButton`, `MPHubNativePage`, `MPCanvasUI`).
- Passenger **Lock/Unlock** button cloned from Park → forgot the reset, used
  `RemoveAllListeners` → unlocking ALSO exited the car. Fixed 2026-06-15.

> The recurring "remote avatars shove vehicles" bug is **not** in this registry — it was a
> single bug we mis-fixed twice (we kept freezing *ghost-vehicle* rigidbodies when the shover
> was the *remote-avatar collider*). Final fix: solid collider + per-pair `IgnoreCollision`
> (`TickVehicleCollisionIgnores`). One bug, not a class.

---

## Class 7 — Rendering a reused pool index as one continuous entity

**Pattern.** A networked entity is keyed by a **reusable pool index** (a slot number), not a
stable identity. When the source recycles a slot — despawns the object in slot N and reuses N
for a *different* object elsewhere — the receiver still treats slot N as the *same* entity and
animates continuity (lerp/smooth) from the old position to the new one.

**Why it bites.** Invisible on the host (it owns the real objects). On the client the one ghost
for that slot is dragged across the map between unrelated objects — a "car zooming with no
physics" streak. A *model/type* check catches reuse that changes the mesh, but **same-type reuse
slips through** and slides. Confirmed 0.1.x: Gley recycles its `VehicleComponent` pool index
(`vc.GetIndex()`), so a traffic ghost slid between recycled cars (the red `[StreakMarker]`
confirmed the streaking ghosts were exactly these).

**Detection grep.**
```
rg -n "GetIndex|\.Index|poolIndex|slot" src/ | rg -i "ghost|snapshot|sync"
```
For each index-keyed sync, ask: is the index a **stable per-entity id**, or a **reusable slot**?
If reusable, does the receiver treat a discontinuity as a *new* entity?

**Safe fixes.**
1. **Treat any discontinuity as a new entity** (the invariant): on the receiver, if a slot's new
   position jumps further than the entity could physically travel between packets (> a teleport
   threshold) OR its model/type changed, **destroy + respawn** the ghost fresh — never slide it.
2. Key by a **stable identity** if the source exposes one (preferred; Gley does not).

**Known instances (fixed).**
- Traffic ghost slid between recycled Gley pool slots → clean-break on model-change OR
  `> SnapDistance` jump in `TrafficSync.ApplySnapshot`. Fixed 2026-06-16.

---

## Class 8 — Destructive snapshot apply (clearing local persistent state from a non-authoritative copy)

**Pattern.** A host→client sync handler `Clear()`s and repopulates a LOCAL, PERSISTENT collection on a
player-owned registration (interior `itemInstances` / `interiorDesigns` / `retailPrices` / `dirtSpots`,
business `scheduleDays`, …) from a network snapshot. But **the host is not authoritative for a player's own
business** — its copy is a blank/stale replica (the real data lives in that client's `.hsg`). So an empty or
stale snapshot **wipes the player's real data**, and because these are saved fields the loss persists after a
save/reload.

**Why it bites.** Invisible to the host (it never receives its own state) and to the owner while present
(they push their truth on entry/poll/exit). It only bites when a receiver applies a *host-built* copy of a
*player-owned* thing — a visitor viewing the shop, or the owner before ownership has synced / right after a
join. The clear-then-repopulate looks correct in isolation; the danger is only the **empty source × player-
owned target** combination — which is exactly the case the host can't fill (owner offline / hasn't visited).

**Detection grep.**
```
rg -n "reg\.\w+\.Clear\(\)" src/
```
For each clear on a player-owned registration collection, confirm it's gated: either the snapshot is flagged
**authoritative** (only the owner's own push is) or there's a **count + ownership** guard
(`Count > 0 && !IsSessionPlayerBusiness(reg)`). An ungated clear on a player business is this bug.

**Safe fixes.**
1. **Whole-snapshot authoritative flag (preferred — collection-agnostic).** The host flags any snapshot it
   built from its own replica of a player-owned thing `Authoritative = false`; the receiver refuses to modify
   a player business's interior from a non-authoritative snapshot. ONE gate protects every collection, present
   and future (`ApplyInteriorSnapshot`).
2. **Count + ownership guard** for paths with no owner-push channel: never overwrite a player business's own
   state from an empty/stale host sync (`ApplyBusinessInfoLocal`).

**⚠ GOTCHA — use the RIGHT "is this the receiver's own business?" test.** `IsSessionPlayerBusiness(reg)` is the
WRONG test for this guard: it keys on `reg.businessOwnerRivalId`, which is **empty for the receiver's own
freshly-loaded shop** (the game tracks your own business via `RentedByPlayer`, not a rival-id) and **true for
ANY other player's shop**. So `!IsSessionPlayerBusiness(reg)` *fails open* for your own shop (lets the host
clobber it) while *over-skipping* other players' shops (which should still receive the host's relay). The
correct "mine" test is **`reg.RentedByPlayer || info.OwnerPlayerId == MPConfig.PlayerId`** — what
`ApplyInteriorSnapshot`'s 3-way OR already used. The first `scheduleDays` fix (2026-06-17 AM) used
`!IsSessionPlayerBusiness` and was silently ineffective until a broad re-audit caught it.

**Not just `.Clear()` — scalar overwrites count too.** The same class includes plain field assignments
(`reg.BusinessName` / `BusinessDescription` / sign / logo / `RentedByPlayer` / …) overwritten from a stale host
replica. The `rg "reg\.\w+\.Clear\(\)"` grep MISSES these — also scan `ApplyBusinessInfoLocal`-style appliers
for unconditional `reg.<field> = info.<field>` writes onto a player-owned reg.

**Known instances (all fixed).**
- `ApplyInteriorSnapshot` `itemInstances` — empty non-authoritative snapshot cleared shop stock (the original
  "furniture vanishing on re-enter" report). Fixed via owner-authoritative push + item guard.
- `ApplyInteriorSnapshot` `interiorDesigns` / `retailPrices` / `dirtSpots` — same wipe, unguarded siblings of
  the items fix → whole-snapshot `Authoritative` flag + a single receiver gate. Fixed 2026-06-17.
- `ApplyBusinessInfoLocal` — owner's own shop **name / type / description / sign / logo / availability / rent**
  (scalar writes, all unguarded) + **operating hours** (the schedule guard used the wrong `IsSessionPlayerBusiness`
  test and failed open). Gated behind a correct `receiverOwnsThis` (`RentedByPlayer || OwnerPlayerId==self`).
  Fixed 2026-06-17 PM.
- `ApplyBusinessInfoLocal` ownership — `RentedByPlayer` defaulted to `false`, clobbering the client's own tenancy
  when the host's `OwnerPlayerId` was momentarily empty (ownership-sync gap at join). Now PRESERVES prior tenancy
  unless the host positively attributes the building elsewhere. Fixed 2026-06-17 PM.
- `PopulateRivalOwnedFromSync` `dailyIncomes` — rival-stats apply matched the owner's own reg by address only and
  overwrote its income series from a foreign figure under ownership divergence. Guarded with `RentedByPlayer`.
  Fixed 2026-06-17 PM.

---

## Class 9 — Injected synthetic / data-gapped game entity deref'd by the game's own per-entity logic

**Pattern.** To make the game "see" MP state, we inject a synthetic instance of a GAME entity
(today: a fabricated `EmployeeInstance` for remote-player register staffing) into a collection the
game **iterates every tick** (`gi.EmployeeInstances`) — or a real entity ends up pointing at data
that only resolves on another machine. The game then runs its **full per-entity logic** on it (UI
lookups, hourly satisfaction / complaint / retirement processing) and **dereferences a field our
synthetic never populated, or a workplace this machine can't resolve**. The object is structurally
valid (not destroyed — that's Class 2), just under-populated / data-gapped vs. what the game assumes.

**Why it bites.** The synthetic looks fine where WE use it (the staffing shows). The crash lands in
the GAME's own code, on a field WE never set, far from the injection site. Worst case it's inside a
per-hour/per-tick loop, so the unhandled NRE **aborts the whole tick** — every employee AND every
delivery step after it stop ("everyone stopped working / HQ stopped working"), not just one entity.
Intermittent (only when the game exercises that path — e.g. an unhappy employee files a complaint),
and **whack-a-mole**: each newly-exercised field is a fresh crash.

**Detection grep.**
```
rg -n "CreateAIEmployeeInstance|EmployeeInstances\.Add|EmployeeInstancesDictionary\[" src/
```
For each injected synthetic, list every GAME per-entity method that runs over that collection
(`UpdateSatisfaction`, `RunComplaintsHourly`, `RunHourly`, daily ticks, Contacts/tooltip lookups) —
each is a candidate deref of a field we left unset.

**Safe fixes (in order of preference).**
1. **Exclude the synthetic from the game's per-entity logic wholesale** — a Prefix that skips the
   game's `RunHourly` for our injected ids (`id.StartsWith(SyntheticDutyEmployeeIdPrefix)`). ONE
   exclusion beats guarding each sub-method as it crashes (avoids the whack-a-mole).
2. **Guard the specific failing method** (Prefix returns false on the bad condition) + **log loudly**
   so the ROOT (why the data is missing) stays visible — the bandaid pattern (`[OrphanEmployee]`).
3. **Fully populate** every field the game reads (least preferred — whack-a-mole; only viable for a
   small known set, like the name fix).

**Known instances (all guarded).**
- Synthetic on-duty `EmployeeInstance` with null `characterData.name` → crashed the phone Contacts app
  (`ContactScrollerController` name lookup + `EmployeeTooltip.Localize`). Fixed 2026-06-19, set a
  non-empty name (`MPRegisterSync.cs:398`).
- Synthetic on-duty employee has zero demands → `EmployeeInstance.UpdateSatisfaction` did 0/0 = NaN →
  skip the synthetic (`Patch_EmployeeInstance_UpdateSatisfaction_SkipSynthetic`). 2026-06-19.
- An employee whose `assignedAddress` doesn't resolve on this machine → `UnfulfilledDemandsComplaint.
  GetComplaintMessageData` NRE escaped `EmployeeHelper.RunHourly` and **aborted the hourly economy
  tick** ("HQ stopped working", bug-20260621-181535). Guarded by skipping the orphan's complaint +
  loud `[OrphanEmployee]` warning (`Patch_EmployeeInstance_RunComplaintsHourly_GuardOrphanBuilding`).
  ROOT (why the workplace is unresolvable in MP) **still open** — the warning is the tracer.

---

*Registry seeded from real fix history (git log + investigation notes); it is not
exhaustive — add a class the moment a second instance of any pattern shows up.*
