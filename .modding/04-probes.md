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

| SalesProbe | MPPatches Patch_SalesProbe_OnPlaceOrder | [SalesProbe] | ACTIVE (recon) | Wave-2: what a register purchase looks like (items, register, position) — confirms the RemoteSale hook point. REMOVE when slice 1 lands. |

| ShopGate | MPPatches Patch_DelayedEnterBuilding | [ShopGate] | ACTIVE (recon) | Wave-2: building classification on entry (open/playerOwned/rented/rival ids) — discriminates why a player-owned shop is not shoppable for visitors. REMOVE with the customer-mode fix. |

| LoadTrace v2 | MPCanvasUI.TickLoadTrace | [LoadTrace] | ACTIVE (localization) | RE-ARMED 2026-06-11 for the client stuck-load (overlay never tears down, clock dead, GM NRE flood): 1 Hz × 30s then 10s × 5 min after every MP scene-load — pos/money/clock/timeScale/LoadingScreen-alpha/pendingFreeze/conn. REMOVE when the stuck-load is localized. |

| FreezeGate telemetry | MPCanvasUI.TickOverlayFreezeGate | [FreezeGate] | ACTIVE (localization) | 15s heartbeat while pending-freeze armed + LOUD warn if the silent IsConnected clear disarms it — answers why the 180s fail-safe never fired in the 2026-06-11 run. REMOVE with the fail-safe fix. |

| ShelfGate override | MPPatches Patch_ShelfGate_ShouldShow | [ShelfGate] | ACTIVE (fix attempt #1, instrumented) | Forces shelf CTA ON inside another LOBBY player's shop (ShopGate proved the cause: player id in businessOwnerRivalId has no RivalData record). Throttled log on every forced frame window. If pickup works end-to-end → keep + replicate pattern for the register gate; if downstream breaks → log localizes the next gate. |

| InteriorMask | RemotePlayerManager.SpawnOrUpdate + VehicleManager.ApplyVehicleFleet | [InteriorMask] | PERMANENT (production diagnostic) | Logs every avatar/ghost hide-show from the cross-interior mask (same-type interiors share one coordinate space). Lines are load-bearing evidence if masking ever misfires. |
