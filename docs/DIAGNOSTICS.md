# Diagnostic-code convention

We keep losing track of probes and diagnostic scaffolding. This makes every piece of
diagnostic code **findable with one grep** and **self-describing** about whether it stays
or goes.

## The rule (one tag, three classes)

Every diagnostic element (probe, perf bracket, debug log, dump writer, A/B toggle,
kill-switch, debug keybind) **must** carry a tag comment on its declaration:

```
// DIAG:FIELD — <purpose>
// DIAG:DEVTOOL — <purpose>
// DIAG:INVESTIGATION(<name>) — <purpose>
```

One grep finds everything: `rg "DIAG:" src/`

### The three classes

| Class | Ships in player build? | Lifetime | Rules |
|---|---|---|---|
| **`DIAG:FIELD`** | **Yes** | Permanent | The *only* class allowed outside `#if BAMP_DEV`. May write **only** to the player-submittable log (`Plugin.Logger`). Must be cheap (runs on every player). **No** disk writes, **no** gameplay effect, **no** keybinds. |
| **`DIAG:DEVTOOL`** | No | Permanent | Long-term general tooling. **Must** be `#if BAMP_DEV`. May write to disk / be expensive. |
| **`DIAG:INVESTIGATION(name)`** | No | **Temporary** | Tied to a named investigation. **Must** be `#if BAMP_DEV`. **Deleted when that investigation concludes** — the `(name)` lets a sweep target it. |

## Hard rules

1. **Tag everything.** No diagnostic code without a `DIAG:` tag. Un-tagged diagnostic code is a bug.
2. **Dev-only by default.** Only `DIAG:FIELD` may exist outside `#if BAMP_DEV`. `DEVTOOL` and `INVESTIGATION` are always `#if BAMP_DEV`.
3. **FIELD is log-only.** A `DIAG:FIELD` element may only emit to the player log, must be cheap, and must never touch disk, gameplay, or input. If it can't meet that bar, it's a `DEVTOOL`.
4. **Investigations are temporary.** When an investigation concludes, grep `DIAG:INVESTIGATION(<name>)` and delete every hit. Don't let them rot into permanent litter.
5. **No shipping keybinds.** Debug keybinds (F-keys etc.) are `DEVTOOL`/`INVESTIGATION` only, never in a player build. Prefer console commands. Remove with the investigation.

## Pre-release sweep (add to the release checklist)

Before cutting a release, run:

```
rg "DIAG:" src/
```

and verify:
- Nothing but `DIAG:FIELD` appears **outside** a `#if BAMP_DEV` block.
- No `DIAG:INVESTIGATION(<name>)` remains for an investigation that's concluded.
- No diagnostic code is **un-tagged** (spot-check `MPPerf`, `Find*`, `[Probe`, `Dump`, kill-switch flags).

## Current inventory (tagged 2026-06-14)

- `DIAG:FIELD` — `MPPerf` `[Perf]` per-frame summary + all its per-subsystem brackets
  (incl. `RemUpd`/`RemLate`). The one diagnostic that ships, by design, so bug-report logs
  carry perf data.
- `DIAG:DEVTOOL` — `MPLoadProfiler` (load timing → `C:\dumps`, `#if BAMP_DEV`),
  `MPSaveCoordinator.DiagWrite`/`DiagArm`/`DiagPhase` (save tracing → `C:\dumps`,
  `#if BAMP_DEV`).
- `DIAG:INVESTIGATION(anim-norun)` — `MPCanvasUI.TickAnimProbe` (the open "no running
  animation" host bug). Remove when that bug is closed.
- *Removed this pass:* the stutter investigation's harness, the entry-bug kill-switches,
  the vehicle-colour dump (concluded), and the F12 black-overlay A/B toggle.

> Migration note: anything predating this convention that still lacks a `DIAG:` tag should
> be tagged the next time it's touched, or removed if its investigation has concluded.
