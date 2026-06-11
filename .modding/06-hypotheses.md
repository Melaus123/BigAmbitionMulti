# Hypotheses (open)

## H1: Host "sorry state" on session load = spawn de-stack offset + drift re-pin fighting the save's position restore
- Reasoning: only recent system that physically moves/pins the LOCAL player post-load; matches "wrong spot + unable to move"; host log otherwise clean.
- DOES NOT explain "info very bugged" — H1 alone is insufficient. (Which info is bugged is still UNKNOWN — awaiting user detail.)
- Test: fix shipped a77b336 (de-stack gated to fresh games) — THIS COUNTS AS TEST #2 for the symptom (cap reached → localize, no more fixes).
- Confirmed if: clean load post-a77b336 with LoadTrace showing stable position + normal info.
- Refuted if: LoadTrace still shows wrong pos/info → de-stack was not (sole) cause.

## H2: "Bugged info" = world/UI state half-applied because a load-finish step died (same class as fade/placement kills)
- Reasoning (inferred): GMShield #N events at stream-resume previously killed fade + placement; other load-finish steps (HUD init?) may share the window.
- Distinguisher: LoadTrace money/day/name fields — garbage from frame 1 (wrong slot/H3) vs correct-then-degrading (interference) vs UI-only.

## H3: Wrong character slot loaded (host loaded another player's .hsg)
- Reasoning: would explain wrong position + wrong info together.
- Evidence AGAINST: host log loaded char guid d2168c9d… (differs from client's 98109fa…); LoadOwnHsg reads per-stableId folder (read, not verified at runtime).
- Distinguisher: LoadTrace character name field.

# Refuted / resolved (kept for the record)
- Mid-join NRE storm = stream applying into half-loaded scene → CONFIRMED, fixed by join quiesce (7985c70) + 4s delayed resume (441c0a6).
- Clothing color mismatch = MPB tints → REFUTED (probe: mpb=False). Actual: float texture-array dye index → fix ad74c71 (UNVERIFIED by user yet).

## H4 (NEW, prime): the loantest1 SAVE DATA is poisoned — disconnect-autosaves fired during broken sessions and wrote a half-initialized world
- Evidence FOR: no commits between last-good and first-bad touch the host load path; symptoms = loaded-garbage pattern (money/calendar/portrait/street all wrong); LoadTrace shows values stable-but-wrong from frame 1 (not degrading).
- Evidence AGAINST: cc=False + frozen clock also fit a missing load-finish step (H2).
- DISCRIMINATOR (single host-only run): fresh MP session → save → exit → reload THAT session. Clean = H4 confirmed (delete loantest1; add save-guard: refuse HostSaveNow while world not healthy). Broken = code path → git bisect (known-good-loads tag = 18a0a69).
- H1 (de-stack) REFUTED for the info symptoms: bugged state persisted with de-stack gated.

## RESOLVED: host world-entry "sorry state" = OVERLAY WATCHDOG force-killing LoadingScreen mid-load
- CONFIRMED 2026-06-11: fresh new game broken identically (H4 refuted); watchdog fired-count = 1 in the broken run; signature (cc=False forever, clock frozen, default-spawn position, HUD half-bound) = the LoadingScreen coroutine murdered before load-finish. PlayerController spawns long before the overlay legitimately drops → 12s "stuck" check tripped on EVERY normal host load.
- Fix: watchdog demoted to diagnostic-only (30s, log line, touches nothing). The stuck-overlay problem it bandaged was already properly fixed by the 4s quiesce delay.
- Lesson reinforced: the bandaid CREATED the regression spiral the user sensed.

## BACKLOG (deferred, user-reported 2026-06-11): host hold survives a client who cancels during load
- Symptom: client cancels instead of loading in → host keeps "waiting for player" overlay + pause until the 90s startup timeout.
- Hypothesized fix shape (inferred, unverified): OnPeerDisconnected during the hold window must remove the peer from the waiting roster (_inGamePlayers/_worldReadyPlayers tracking) and re-evaluate release — current cleanup likely only handles in-game departures.
- Defer until lifecycle stage 4 (the hold is being unified into the load fence anyway — fix it there, not as another pre-consolidation patch).

## BACKLOG (user, 2026-06-11): rest dock wedges open after click-bench-then-walk-away
- Symptom: clicked bench, walked away before sitting → dock stuck open, X gone (X visibility tied to CancelButtonIndex of a live activity?), dock survived EXIT TO MENU (scene-exit reset missed this state).
- Notes: approach-cancelled state = activity in MovingTowardsActivity then aborted; Seated/dock visibility logic likely wedged on a stale cached activity. Scene-exit must force-hide the dock unconditionally.
- Defer until after stage-4/5 consolidation (current plan).

## BACKLOG (user, 2026-06-11): host rubberbands a few times when first moving after login
- Suspect (user + matching mechanism): spawn de-stack RE-ASSERT pins the player to _spawnTarget for the probe window (drift 1.2-8m snapped back) — walking immediately = rubberband. Fix lands with SPAWN PRE-PLACEMENT (assign distinct spawn positions at placement time; delete the offset/probe/re-assert machinery entirely).
