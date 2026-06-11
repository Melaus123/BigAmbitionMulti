# Probe Registry

| Probe | Location | Prefix | Status | Purpose |
|---|---|---|---|---|
| LoadTrace | MPCanvasUI.TickLoadTrace | [LoadTrace] | ACTIVE | Wide-net: 1 Hz timeline for 25s post-scene-load (pos, controller, money, day, name, timescale) — localize host-load "sorry state" |
| Post-load position | MPCanvasUI quiesce-off | [UI] post-load position | ACTIVE | Did save position-restore run |
| FullMenu hierarchy dump | MPFullMenuProbe | [FullMenu] | ACTIVE (one-shot done) | Shell + page-interior inventory — REMOVE after Hub visual true-up |
| Color mechanism dump | RemotePlayerManager.ProbeColors | [Colors] | ACTIVE (one-shot) | Tint mechanism classification — REMOVE after dye sync verified |
| Spawn telemetry | MPCanvasUI.TickSpawnOffset | [Spawn] probe# | ACTIVE | Verify de-stack placement (now fresh-games-only) |
| GMShield | MPPatches Patch_GameManager_Update | [GMShield] | ACTIVE (shield) | Swallow GameManager.Update exceptions in MP (mid-join storms) — review for retirement |
| ActivityUI shield | MPPatches | [ActivityUI] | PERMANENT | Benign UI-tick NRE swallow |
| Gley/NavMesh shields | MPPatches | [Gley]/[NavShield] | PERMANENT | Ghost-contact NRE swallow |
| Loan ledger diag | MPHub | [Hub] ledger… | ACTIVE | Persistence verification lines — demote after sweep |
| Nav watchdog | MPRestSync | [Rest] NAV LOCK | PERMANENT (diagnostic-only) | Names activity nav-lock if ever recurs |

Rules: every new probe gets a row + prefix; resolved probes REMOVED from code promptly.
| Lifecycle shadow | MPLifecycle.Tick (MPCanvasUI Update) | [Lifecycle] | ACTIVE | Stage-3 shadow phase tracker: transitions + STUCK-IN-LOADING >60s detector (mid-join acceptance instrumentation) |
