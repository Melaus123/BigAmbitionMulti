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

**Audit status.** 2026-06-14: full sweep of all 24 call sites — **0 latent
instances**. Every periodic/in-game scan is cached, latched, guarded, or per-message.
Class considered eradicated; re-verify before each release via the grep above.

**Watch-list (safe today, fragile if changed).**
- `RemotePlayerManager.FindHeldTemplate` (`src/RemotePlayerManager.cs:875`) — uses the
  heaviest API (`Resources.FindObjectsOfTypeAll(Transform)`); safe only because
  double-cached. Don't loosen the cache.
- `MPPhoneButton.DeepDumpPhoneArt` (`src/MPPhoneButton.cs:569`) — dead diagnostic with a
  full-scene scan; safe because unreferenced. Delete rather than re-wire.
