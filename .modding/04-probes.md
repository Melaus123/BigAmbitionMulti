# Probe Registry

| Probe | Location | Prefix | Status | Purpose |
|---|---|---|---|---|
| Post-load position | MPCanvasUI OnLifecyclePhase (WorldReady) | [UI] world-ready position | PERMANENT (diagnostic) | One line per load: did save position-restore run |
| GMShield | MPPatches Patch_GameManager_Update | [GMShield] | ACTIVE (shield) | Swallow GameManager.Update exceptions in MP — RETIREMENT-GATED: remove after several clean sessions log zero swallows |
| ActivityUI shield | MPPatches | [ActivityUI] | PERMANENT | Benign UI-tick NRE swallow |
| Gley/NavMesh shields | MPPatches | [Gley]/[NavShield] | PERMANENT | Ghost-contact NRE swallow |
| Nav watchdog | MPRestSync | [Rest] NAV LOCK | PERMANENT (diagnostic-only) | Names activity nav-lock if ever recurs |
| Overlay watchdog | MPCanvasUI.TickOverlayWatchdog | [UI] overlay-watchdog | PERMANENT (diagnostic-only) | 30s stuck-overlay log line; NEVER touches LoadingScreen (force-dismiss variant caused the June 2026 regression) |
| Lifecycle transitions | MPLifecycle | [Lifecycle] | PERMANENT (production) | No longer a probe: THE phase tracker (stage 4); transition lines + STUCK-IN-LOADING >60s are load-bearing diagnostics |
| Phase reports | MPServer.RecordPhaseReport | [Server] phase: | PERMANENT (diagnostic) | Per-player reported phase transitions — the fence's visibility |
| HubRoster | MPCanvasUI chips rebuild | [HubRoster] | ACTIVE (surveillance) | Host target-list contents (empty-chips bug never reproduced — remove if quiet through Wave 2) |

Removed 2026-06-11 (probe cleanup sweep): LoadTrace (target bug resolved: overlay-watchdog regression),
FullMenu hierarchy dump + MPFullMenuProbe.cs (dumps captured in .modding for Hub true-up),
MPPhoneProbe.cs (dead file, unwired since phone-button fix), ProbeColors + DumpRendererColors
(dye mechanism found + sync verified), ProbeMorphs + DumpScaledBones (callerless dead code;
blendshape sync shipped), spawn telemetry (deleted with sidestep migration #3),
loan ledger diag (demoted: save line only when loans exist).

Rules: every new probe gets a row + prefix; resolved probes REMOVED from code promptly.

NEXT SWEEP CANDIDATES (one-shot discovery probes, missions complete, still wired in
MPCanvasUI ~2455: ProbeAppearance / ProbeAnimatorLive / ProbeTraffic — verify each is
log-only (no cache priming/side effects) before deleting; they are guarded one-shots, harmless meanwhile).

| FullMenu dump v2 | MPFullMenuProbe (MPCanvasUI wiring) | [FullMenu] | ACTIVE (capture pending) | RESTORED 2026-06-11: round-1 dumps lived only in BepInEx''s log (overwritten per run) — data lost. v2 PERSISTS to .modding/ui-dumps/fullmenu.txt and dumps EVERY page opened (round 1 stopped at the first). REMOVE after Hub visual true-up. |
