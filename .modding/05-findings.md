# Confirmed Findings

- **Overlay watchdog regression (2026-06-11, CONFIRMED + FIXED 4afab01):** force-dismissing the
  LoadingScreen object mid-load kills its load-finish coroutine → CharacterController never
  re-enabled, game clock never started, HUD half-bound, player stranded at default spawn.
  PlayerController spawns LONG before the overlay legitimately drops, so any "world up + overlay
  up = stuck" heuristic trips on every normal host load. Evidence: fresh-game broke identically
  (excluding save data), watchdog fired-count = 1 in the broken run, LoadTrace signature.
  RULE: never destroy/disable native lifecycle objects.
- **Mid-join stream poisoning (CONFIRMED, fixed 7985c70 + 441c0a6):** applying live world traffic
  into a half-loaded scene (remote spawn mid-load, clock hard-snap) breaks GameManager permanently
  (NRE every frame). Fix: join quiesce drops streams from LoadData receipt until world-ready + 4s.
- **Load over a running world breaks GameManager permanently (CONFIRMED, fixed 911a99e):**
  SaveGameManager.Load / LoadIntro from in-game = NRE storm. Menu detour required (LoadScene.LoadMainMenu,
  deferred completion via TickPendingLoad).
- **Clothing dye = float texture-array slice index (probe-CLASSIFIED):** garments are instanced
  materials, mpb=False, base colors white, shaders SH_CharacterClothes(Array); the dye is a
  Float/Range shader property, not a Color. Sync fix ad74c71 — user verification PENDING.
- **Loan persistence (USER-VERIFIED):** loans.bamp.json survives save/exit/reload; ledger rides
  every session save; re-broadcast on WorldReady.
- **Bank loan model (dump consts):** InterestRate=20 TOTAL premium, YearsToPayLoan=4 → 244-day
  term (61-day year); dailies = ceil(P*0.20/244), ceil(P/244).
- **IL2CPP interop rules:** reflection-Invoke needs the DECLARED type's wrapper
  (Activator.CreateInstance(type, ptr); plain `as Il2CppObjectBase` for the pointer — TryCast
  to the abstract base throws). Never reflection-invoke properties returning generic IL2CPP structs.
- **Process:** multi-line code edits ONLY via the Edit tool (PS .Replace CRLF no-ops silently
  — cost 4+ rounds); xcopy deploy skips silently while the game runs (verify timestamps);
  Harmony GetMethod on overloaded names throws AmbiguousMatch inside TargetMethods (enumerate).
