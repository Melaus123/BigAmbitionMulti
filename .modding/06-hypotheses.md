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
