# Session Lifecycle (current state — the consolidation target)

## The implicit states (no single owner today)
Menu → Lobby (host/client) → Loading → WorldReady → Running → (OfflineFork | back to Menu)
Plus overlays: Quiesced (mid-join load), StartupHold (lobby-flow load), ManualPause.

## Who infers state today, and how (the whack-a-mole surface)
| Consumer | Heuristic used | Risk |
|---|---|---|
| MPCanvasUI in-game tick | IsInGame() (scene/PlayerController) | fires before load-finish |
| FullMenu injection | !IsStartupHeld + !IsLoadingOverlayUp + 4s grace | layered guards, post-hoc |
| Spawn de-stack | !hold + !overlay + roster + ActiveSessionName empty | 4 conditions, was wrong twice |
| Join quiesce | flag set at LoadData, cleared at IsInGame transition + 4s | magic 4s where an event belongs |
| Startup hold (lobby flow) | host-armed, released on all WorldReady | parallel system to quiesce |
| Menu UI hide | IsInGame() early-return | hide skipped on direct loads (fixed) |
| RemotePlayerManager | "EARLY-DATA" PlayerController-null check | spawned ghost mid-load once |
| MPSaveCoordinator pending load | PlayerController null + overlay down | correct shape, lives alone |
| GMShield / ActivityUI shields | swallow exceptions in MP | mask faults; review for retirement |

## Confirmed lifecycle laws (violations caused real bugs)
1. Never Load/LoadIntro over a running world → menu first (finding).
2. Never apply world stream into a loading scene → quiesce/hold (finding).
3. Never destroy native lifecycle objects (LoadingScreen) — coroutines own load-finish (finding).
4. PlayerController existence ≠ world ready — load-finish runs LONG after spawn (finding).
5. Saves must not fire while the world is unhealthy (proposed guard — not yet implemented).

## Consolidation plan (stage 3+)
- MPLifecycle: one state enum + events (WorldLoading, WorldReady, LeftWorld), shadow-mode first
  (log transitions only), then migrate consumers one per test.
- Replace quiesce DROP with QUEUE-until-WorldReady; replace 4s magic with the WorldReady event.
- Unify startup hold + quiesce as one "load fence".
- Retire GMShield once no storms observed for several sessions (registry).
