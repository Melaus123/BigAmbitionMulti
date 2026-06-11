# BigAmbitionsMP — Orientation

BepInEx 6 (be.755) IL2CPP multiplayer mod for Big Ambitions (Unity 2022.3.62, HDRP).
Host-authoritative: host owns world simulation (loans ledger, consensus skips, business
snapshots); clients mirror via LiteNetLib messages (Protocol.cs, MessageType 1–108).

Key source map (src/):
- MPServer / MPClient — net transport, message routing, join control (bans, pending joins)
- MPSaveCoordinator / MPSaveManager — MP session saves (`_BAMP_MP/<ver>/<session>/`), per-player .hsg, manifest, loans.bamp.json
- MPCanvasUI — ALL custom UI (chat, rest dock, Business Hub, lobby, join panel) + per-frame ticks + watchdogs
- MPHub — gifts/loans logic; MPHubNativePage — "Business" app injected into the game's FullMenu
- MPRestSync / TimeSync — rest/skip consensus, time guardian, startup hold
- RemotePlayerManager — ghost avatars, appearance sync (variants + colors + floats + blends)
- MPPatches — all Harmony patches (nested classes w/ class-level [HarmonyPatch] REQUIRED)

Decompiled reference: C:\code\cpp2il\dumper-out\dump.cs + script.json (RVAs for capstone disasm).
Installs: HOST = Steam dir; CLIENT = C:\BigAmbitions2 (build auto-deploys both; xcopy SKIPS silently if game running — verify timestamps).
Logs: BepInEx\LogOutput.log per install (reset per launch); Unity Player.log SHARED LocalLow path (crashed instance = Player-prev.log).
Context log (session history): context-log-2026-05-18-bamp-game-start.md (project root).
