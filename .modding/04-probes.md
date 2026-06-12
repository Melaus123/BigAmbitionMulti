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

| ShelfGate override | MPPatches Patch_ShelfGate_ShouldShow | [ShelfGate] | PERMANENT (production) | VERIFIED: forces shelf CTA ON inside another lobby player's shop (player owner ids have no RivalData record). Load-bearing for cross-player shopping. |

| MPSale finalizer | MPPatches Patch_MPOrderFinalizer | [MPSale] | PERMANENT (production) | The MP order finalizer: skips native OnPlaceOrder in player shops (it NREs on the replica's missing stock graph and books locally anyway); charges from the synced store table after a ~2s service beat (cancel-safe), RemoteSale carries revenue + structured items to the owner. VERIFIED at $22 ×2 purchases 2026-06-12. |

| SelfCheckout routing | MPPatches Patch_RegisterInteract_SelfCheckout | [SelfCheckout] | PERMANENT (production) | Register click in a duty-staffed player shop routes to the game's native self-checkout flow. VERIFIED. |

| Stock decrement | GameStatePatcher.ApplySaleStockDecrement | [Stock] | PERMANENT (production) | Host-authoritative stock decrement per sale (slice 2); logs every deduction + SHORT warnings. Verification pending one run. |

| InteriorMask | RemotePlayerManager.SpawnOrUpdate + VehicleManager.ApplyVehicleFleet | [InteriorMask] | PERMANENT (production diagnostic) | Logs every avatar/ghost hide-show from the cross-interior mask (same-type interiors share one coordinate space). Lines are load-bearing evidence if masking ever misfires. |

| RegShield | MPPatches Patch_RegisterOrder_Shield (OnPlaceOrder) | [RegShield] | ACTIVE (probe + shield) | Prefix logs employee/employeeInstance/playerCustomer null-state at order time in player shops (names the NRE root); Finalizer swallows the native NRE there + OnOrderCancel so the buyer dequeues instead of hard-locking. Probe half REMOVED once the purchase path is fixed; shield half stays until purchase verified. RUN 2: shield fired but OnOrderCancel ALSO NREd — superseded by RegGuard prevention; keep as last-line shield. |

| RegGuard | MPPatches Patch_RegisterQueue_Guard (CanOrder) | [RegGuard] | ACTIVE (production guard) | Blocks queue-join in player shops while the register has no LOCAL employeeInstance — the doomed-queue hard lock becomes impossible. Allows through once synthetic staffing lands. |

| SynthStaff | (DELETED 2026-06-12) | [SynthStaff] | REMOVED | Synthetic-employee staffing machinery deleted after the self-checkout pivot verified: the native staffing evaluator's first gate (ShouldUpdateEmployee) refuses rival-translated shops; no roster/WorkShift injection can reach it. Also removed: StaffEval gate-override + shift probe, AssignProbe, RegGuard's synthetic allowance, RegShield's probe prefix (shield finalizer kept). |

## Sweep 2026-06-12 (dead-code cleanup, user-requested)
REMOVED (missions complete, all verified log-only before deletion):
- MPFullMenuProbe.cs + wiring (dumps archived in .modding/ui-dumps/fullmenu.txt)
- MPRideProbe.cs + Sample call + ProbeOwnFleetSolo (ride sync user-verified perfect)
- VehicleManager discovery complex: ProbeVehicles/ProbeVehicleSystem/ProbeDrivenVehicle/
  ProbePlayerMembers/ProbeParentChanges/ProbeParkedVehicles(+helpers)/ProbeTrafficExtras/
  ProbeCarColor/ProbeTaxi/ProbeTraffic/ScanGleyInternals/ScanGleyApi/ScanSceneTraffic/
  CountObjectsOfType + Dump*/DescribeValue helpers + _systemTypes
- RemotePlayerManager: ProbeLocalCharacter/ProbeAppearance/ProbeAnimatorLive
- GameStateReader: ProbeGameVariables/ProbeGameInstance (+ MPServer call site)
- LoadTrace v2 (stuck-load localized + fixed: watchdog demoted, fence fallback)
- SalesProbe (RemoteSale hook landed + verified)
- ShopGate classification log (discriminator shipped as purchaser-enable; the
  functional SetCurrentShop call in the same block KEPT)
- GMShield Finalizer (zero swallows post-port; Patch_GameManager_Update Postfix
  KEPT - DrainQueue + timescale enforcement are load-bearing)
- HubRoster surveillance log (quiet through Wave 2; sig-rebuild logic KEPT)
KEPT deliberately: FreezeGate telemetry (system still under verification),
RegShield swallow (last-line shield), autopilot + F3 test rig (testing aids),
MPLoadProfiler (load forensics), all PERMANENT rows above.
