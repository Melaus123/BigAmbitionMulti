# Release-hardening campaign — plan of action (2026-06-12)

Approved scope: pattern sweep (workflow), content-coverage matrix, latency runs,
soak + lifecycle chaos, version gate + self-diagnosis. Deferred: 3-player and
fresh-install runs.

## Ground rules (interference control)
1. **One variable per runtime session.** Baseline first; never combine a new
   fix-batch with a new test condition in the same run.
2. **No mod-source edits while the read-only sweep workflow is running** —
   agents must see a consistent tree. Docs/scripts outside `src\` are fine.
3. **Nothing latency-related ever enters the mod source or the shipped build.**
   Network conditioning is an external tool (clumsy), started and force-killed
   by a session wrapper script. Belt-and-braces: the mod logs peer ping each
   audit cycle, so a conditioner accidentally left running is visible in any
   log ("ping >5ms on loopback" = something is still conditioning).
4. **Fixes from findings follow normal discipline**: one at a time, two-test
   cap, localize before fixing, Test Plan per run.

## Workstreams

### W1 — Pattern sweep + content matrix (items 1+2) — RUNNING
- [Me] Background multi-agent workflow over `src\` + the decompile: one hunter
  per historical bug class, plus five content-axis auditors (activities,
  vehicles, items, businesses, venues). Every candidate finding is skeptically
  re-verified before it reaches the report.
- [You] Nothing. Output = vetted findings list, severity-ranked.

### W2 — Version gate + self-diagnosis (item 6) — after W1 returns
- [Me] Code: (a) Hello handshake gate — mod version + protocol number checked
  host-side, clean "version mismatch" refusal (HelloPayload.Version is
  currently logged at MPServer.cs:1140 but never checked); (b) peer-ping log
  line each audit cycle (also serves rule 3); (c) bug-report bundle (zip both
  logs + session manifest); (d) confirm MPAudit stays on in release builds.
- [You] Nothing now; verified in session S1.

### W3 — Latency tooling (item 3)
- [Me] Fetch clumsy, write `local\launch-mp-latency-test.bat`: start conditioner
  (lag ~100ms + jitter, ~2% loss on the mod's UDP port) → run the normal
  two-instance launcher → on exit, force-kill the conditioner and verify the
  process is gone. Post-session checklist printed at the end.
- [You] Run session S2 (~30 min of normal play). Nothing to remember —
  the wrapper owns start/stop, and the ping log line catches a leftover
  conditioner in any later run.

### W4 — Soak + lifecycle chaos (item 4) — after W1 returns (touches src)
- [Me] Code: extend MPAutopilot with a random in-game action driver (move,
  enter/exit buildings, buy, vote skips, periodic saves); oracle script that
  scans both logs for audit mismatches + exceptions/warnings; chaos driver
  script (kill client mid-load, cancel-during-load, rejoin loops, save-during-skip).
- [You] S3: start the soak bat in the evening, leave the PC on overnight.
  S4: ~30 min semi-attended chaos run (script does the killing/restarting;
  you observe per a short Test Plan).

### W5 — Fixes from findings
- [Me] Code, one finding at a time, after W1/W2 land.
- [You] Specific observation checks folded into S1/S4 Test Plans — no
  separate sessions.

## Runtime session order (each separate; do not combine)
| # | Session | Verifies | Your effort |
|---|---------|----------|-------------|
| S1 | Baseline two-instance run (after W2) | hardening pass has no false-positive drops; version gate handshake; landed fixes | ~20–30 min normal play touching money/shops/loans/saves |
| S2 | Latency run (clumsy via wrapper) | timing/ordering robustness under 100ms + loss | ~30 min, same play script as S1 |
| S3 | Overnight soak (autopilot driver) | divergence/exceptions over hours; MPAudit is the oracle | start a bat, leave PC on |
| S4 | Lifecycle chaos (scripted) | join/leave/load edges (kill mid-load, cancel-during-load, rejoin) | ~30 min semi-attended |

## Status
- 2026-06-12: W1 launched (background workflow). W2–W4 queued behind it.
  Sessions S1–S4 scheduled after W2 lands.
