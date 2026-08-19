using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BigAmbitionsMP
{
    public sealed class BugReportResult
    {
        public string DirectoryPath = "";
        public bool DiscordUploadQueued;
    }

    /// <summary>The native top-bar bug-report button (UI.Topbar.ReportBugButton) disables itself on
    /// modded saves — dead, highly visible real estate. Recycle it as OUR report entry point (user
    /// 2026-07-08; replaces the buried chat-bar button): keep it interactable, repaint it purple so
    /// it's clearly the MOD's, and route the click to our bug-report popup.
    ///
    /// NO COPY, NO INHERITED BEHAVIOR (user's explicit caution): the button is taken over IN PLACE,
    /// and its click event is REPLACED WHOLESALE — assigning a fresh ButtonClickedEvent drops the
    /// prefab's PERSISTENT listeners (RemoveAllListeners clears only runtime ones), so the native
    /// feedback flow can never fire alongside ours. The ReportBugButton component itself only acts
    /// in OnEnable, which we postfix — nothing re-disables or repaints the button afterwards.</summary>
    [HarmonyLib.HarmonyPatch(typeof(UI.Topbar.ReportBugButton), "OnEnable")]
    public static class Patch_ReportBugButton_ModTakeover
    {
        private static readonly Color ModPurple = new Color(0.35f, 0.31f, 0.81f, 1f);

        // Round-209 (rex's missing purple button): OnEnable is a ONE-SHOT, and on a
        // CLIENT it fires before InMpGame flips at scene-ready — the gate ate the only
        // attempt (the round-197 class, in a UI hook the network-era fix never covered).
        // Recurrence: a 1s retry tick runs while in an MP world until the takeover
        // succeeds; the OnEnable postfix stays for native re-enables. Reset per scene.
        private static bool  _recycled;
        private static float _retryNextAt;

        public static void ResetForScene() { _recycled = false; _retryNextAt = 0f; }

        /// <summary>Called from the canvas pre-block. Cheap: exits on a flag once
        /// recycled; searches at most once per second until then.</summary>
        public static void TickRetry()
        {
            try
            {
                if (_recycled) return;
                if (!MPServer.IsRunning && !MPClient.InMpGame) return;
                float now = UnityEngine.Time.unscaledTime;
                if (now < _retryNextAt) return;
                _retryNextAt = now + 1f;
                var inst = UnityEngine.Object.FindObjectOfType(typeof(UI.Topbar.ReportBugButton)) as UI.Topbar.ReportBugButton;
                if (inst != null) TryTakeover(inst);
            }
            catch { }
        }

        static void Postfix(UI.Topbar.ReportBugButton __instance) => TryTakeover(__instance);

        static void TryTakeover(UI.Topbar.ReportBugButton __instance)
        {
            try
            {
                var button = HarmonyLib.AccessTools.Field(typeof(UI.Topbar.ReportBugButton), "button")
                                 ?.GetValue(__instance) as UnityEngine.UI.Button;
                if (button == null) return;
                // Round-187 (user directive, field 20260729-141854: an offline SP player filed an
                // empty report through this button): the takeover exists for MULTIPLAYER sessions —
                // in plain single-player the button keeps its native modded-save behavior, and
                // offline mod reports stay possible through the mod's own menu entry.
                if (!MPServer.IsRunning && !MPClient.InMpGame) return;
                // Native leaves the button ENABLED only on unmodded saves — a state we can't be in
                // while loaded, but if it ever happens the native flow stays untouched.
                // Round-209: logged — this silent return was one of two candidate causes for
                // rex's missing purple button; if it ever fires it names itself now.
                if (button.interactable)
                {
                    if (!_recycled)
                        Plugin.Logger.LogWarning("[BugReport] top-bar button already interactable in an MP session — native flow left untouched (unexpected; takeover skipped).");
                    _recycled = true;   // stop the retry tick — native owns it
                    return;
                }

                button.interactable = true;
                button.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();   // wholesale replace — see summary
                button.onClick.AddListener(() =>
                {
                    try { MPCanvasUI.Instance?.OpenManualBugReport(); } catch { }
                });

                // Purple = unmistakably the mod's. Tint the target graphic; state colors multiply on
                // top of it (default ColorBlock normal is white), so hover/press keep the purple base.
                var img = button.targetGraphic as UnityEngine.UI.Image ?? button.GetComponent<UnityEngine.UI.Image>();
                if (img != null) img.color = ModPurple;

                // The native OnEnable just pointed the tooltip at "disabled because mods" — replace
                // with our own text. Localizor renders unknown keys as-is, and ',' splits lines.
                var tooltip = HarmonyLib.AccessTools.Field(typeof(UI.Topbar.ReportBugButton), "tooltip")
                                  ?.GetValue(__instance) as BasicTooltip;
                if (tooltip != null)
                {
                    tooltip.titleKey = "Report a " + MyPluginInfo.SHORT_NAME + " bug";
                    tooltip.descriptionKey = "Opens the multiplayer mod's bug report,Your logs are attached automatically";
                }
                _recycled = true;   // round-209: stop the retry tick
                Plugin.Logger.LogInfo("[BugReport] Native top-bar report button recycled as the mod's report entry point.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BugReport] top-bar button takeover: {ex.Message}"); }
        }
    }

    public static class MPBugReport
    {
        private const int MaxCopiedLogBytes = 4 * 1024 * 1024;
        private const long MaxUserAttachmentBytes = 24L * 1024L * 1024L;
        private static string _markerPath = "";
        private static string _pendingCrashSummary = "";

        public static bool PendingCrashDetected { get; private set; }
        public static string PendingCrashSummary => _pendingCrashSummary;
        /// <summary>Human-readable one-liner about WHERE the previous session died ("last alive
        /// 20:07:31, phase 'main menu', uptime 41s") — parsed from the heartbeat fields of the stale
        /// marker. Empty on markers from before the heartbeat existed. Task #5 (2026-07-08): the
        /// Prabaha report had NO way to tell a menu-kill loop from a real gameplay crash.</summary>
        public static string PendingCrashHint { get; private set; } = "";

        public static void MarkSessionStarted()
        {
            try
            {
                string root = SafeRoot();
                Directory.CreateDirectory(root);
                _markerPath = Path.Combine(root, "session-open.json");

                if (File.Exists(_markerPath))
                {
                    string old = File.ReadAllText(_markerPath);
                    if (old.IndexOf("\"State\":\"open\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        old.IndexOf("\"State\": \"open\"", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        PendingCrashDetected = true;
                        _pendingCrashSummary = old;
                        PendingCrashHint = BuildCrashHint(old);
                        Plugin.Logger.LogWarning($"[BugReport] Previous session did not close cleanly; crash report popup will be shown.{(PendingCrashHint.Length > 0 ? " " + PendingCrashHint : "")}");
                    }
                }

                WriteOpenMarker("normal start", false);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[BugReport] Session marker start failed: {ex.Message}");
            }
        }

        private static string BuildCrashHint(string markerJson)
        {
            try
            {
                var m = JsonConvert.DeserializeObject<Dictionary<string, string>>(markerJson);
                if (m == null) return "";
                m.TryGetValue("LastAlive", out var alive);
                m.TryGetValue("Phase", out var phase);
                m.TryGetValue("UptimeSeconds", out var up);
                m.TryGetValue("Started", out var started);

                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(alive) || !string.IsNullOrEmpty(phase))
                {
                    sb.Append("Previous session was last alive ");
                    sb.Append(string.IsNullOrEmpty(alive) ? "(unknown)" : alive);
                    if (!string.IsNullOrEmpty(phase)) sb.Append(", phase '").Append(phase).Append('\'');
                    if (!string.IsNullOrEmpty(up)) sb.Append(", uptime ").Append(up).Append('s');
                    sb.Append('.');
                }

                // Kill-vs-crash classification: a real native crash leaves a Crash_* folder whose
                // timestamp lines up with the death moment. Found → confident crash. Not found →
                // dump-less crash, freeze-kill, or plain Task-Manager close — we NEVER suppress the
                // popup for those (a kill of a FROZEN game is a true positive), we just say so and
                // give the harmless case an easy out. (User probe, 2026-07-08.)
                DateTime around = default;
                if (!DateTime.TryParse(alive, null, System.Globalization.DateTimeStyles.RoundtripKind, out around))
                    DateTime.TryParse(started, null, System.Globalization.DateTimeStyles.RoundtripKind, out around);
                bool? dump = around != default ? HasCrashFolderNear(around) : null;
                if (dump == true)
                    sb.Append(" A matching crash dump was found — this was a real crash.");
                else if (dump == false)
                    sb.Append(" No crash dump was found — if you closed the game via Task Manager (or it was still fine when it ended), you can dismiss this.");
                return sb.ToString().TrimStart();
            }
            catch { return ""; }
        }

        /// <summary>Does Unity's crash folder hold a Crash_* entry near this moment? (Written by the
        /// engine's crash handler on a native fault — the marker can't see it, the filesystem can.)</summary>
        private static bool HasCrashFolderNear(DateTime around)
        {
            try
            {
                string crashes = Path.Combine(Path.GetTempPath(), Application.companyName ?? "Hovgaard Games",
                                              Application.productName ?? "Big Ambitions", "Crashes");
                if (!Directory.Exists(crashes)) return false;
                foreach (var d in new DirectoryInfo(crashes).GetDirectories("Crash_*"))
                {
                    var dt = d.LastWriteTime - around;
                    if (dt.TotalMinutes > -2 && dt.TotalMinutes < 10) return true;   // died ≤heartbeat before; dump written shortly after
                }
            }
            catch { }
            return false;
        }

        // ── Heartbeat (task #5): stamp the open marker so the NEXT session can say where this one
        // died. Written every ~30s from MPCanvasUI.Update — a stale LastAlive/Phase in a leftover
        // marker = the death moment, accurate to the heartbeat interval.
        private static float _sessionStartedAt = -1f;

        public static void Heartbeat(string phase)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_markerPath)) return;
                if (_sessionStartedAt < 0f) _sessionStartedAt = Time.unscaledTime;
                var marker = BuildMarker("normal start", false);
                marker["LastAlive"] = DateTime.Now.ToString("O");
                marker["Phase"] = phase ?? "";
                marker["UptimeSeconds"] = ((int)(Time.unscaledTime - _sessionStartedAt + 0.5f)).ToString(CultureInfo.InvariantCulture);
                // Round-207g: serialize on the main thread (small object), WRITE on the
                // pool — the synchronous 30s disk write was the occasional ~70ms Pre.A
                // hitch. A lost heartbeat on crash costs ≤30s of "last alive" precision;
                // the open/close markers stay synchronous (crash-order-critical).
                string json = JsonConvert.SerializeObject(marker, Formatting.Indented);
                string path = _markerPath;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { File.WriteAllText(path, json); } catch { }
                });
            }
            catch { }
        }

        public static void MarkCleanShutdown()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_markerPath) && File.Exists(_markerPath))
                    File.Delete(_markerPath);
            }
            catch { }
        }

        public static void AcknowledgePendingCrash()
        {
            PendingCrashDetected = false;
            _pendingCrashSummary = "";
        }

#if BAMP_DEV
        // Dev builds ONLY (maintainer decision 2026-06-16): the intentional-crash test must never ship in Release.
        public static void CrashForTest(string reason)
        {
            reason = string.IsNullOrWhiteSpace(reason) ? "manual crash test" : reason.Trim();
            WriteOpenMarker(reason, true);
            Plugin.Logger.LogError("[BugReport] Intentional crash test requested. The game will close now.");
            Environment.FailFast("BigAmbitionsMP crash report test: " + reason);
        }
#endif

        public static BugReportResult Create(string reason, bool openFolder = true, IEnumerable<string>? attachments = null, IEnumerable<string>? discordTagIds = null, Action<bool, string>? onUploadComplete = null, bool includeCrashArtifacts = false)
        {
            reason = string.IsNullOrWhiteSpace(reason) ? "manual report" : reason.Trim();

            string root = SafeRoot();
            Directory.CreateDirectory(root);
            PruneOldReports(root);   // user directive 2026-08-16: only the last report is kept

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string dir = Path.Combine(root, "bamp-bug-" + stamp);
            Directory.CreateDirectory(dir);
            MarkReportBusy(dir);   // released in the terminal path below — the prune skips busy dirs

            // Batch 14: callers already prefix the reason ("manual bug report: " / "previous
            // crash: ", MPCanvasUI:5852) — re-prefixing here doubled it on every ring-dump
            // header in the field ("manual bug report: manual bug report: <text>", 10 bundles).
            string ring = MPLog.Dump(reason);
            WriteDescription(Path.Combine(dir, "description.txt"), reason);
            WriteReport(Path.Combine(dir, "report.md"), reason);
            CopyPlayerLogs(dir);
            WriteSaveStore(dir);   // bug-report v2 (task #40): active-session saves + full store listing
            CopyIfExists(ring, Path.Combine(dir, "bamp-ring.log"), MaxCopiedLogBytes);
            // Task #5: the actual crash evidence lives OUTSIDE Player.log — but only CRASH reports
            // carry it (a stale Crash_* folder on an unrelated manual report is misleading noise).
            if (includeCrashArtifacts) CollectUnityCrashArtifacts(dir);
            CopyUserAttachments(dir, attachments);
            WriteRedactedConfig(Path.Combine(dir, "config-redacted.json"));
            WriteSubmitNotes(Path.Combine(dir, "README-submit.txt"));

            var result = new BugReportResult { DirectoryPath = dir };

            // Bug-report v2 (task #40): ask every connected peer for its logs. Third-party
            // reports ("my friend crashed", bundle 20260811-225015) carried only the
            // reporter's half of the evidence. Null when not in an MP session — menu
            // reports proceed exactly as before.
            string? peerGatherId = StartPeerLogGather(dir);

            // Submit to the RELAY by default (it holds the Discord webhook server-side).  A direct
            // webhook in config overrides it (maintainer local testing) and posts straight to Discord.
            string directWebhook = MPConfig.BugReportDiscordWebhookUrlLive();
            string target = !string.IsNullOrWhiteSpace(directWebhook) ? directWebhook : MPConfig.BugReportRelayUrlLive();
            bool direct = !string.IsNullOrWhiteSpace(directWebhook);
            if (!string.IsNullOrWhiteSpace(target))
            {
                result.DiscordUploadQueued = true;
                string[] tags = CleanDiscordTagIds(discordTagIds);
                Task.Run(() =>
                {
                    try
                    {
                        // Bounded wait: completes EARLY when every peer's last file lands; the
                        // deadline is the failure path (peer offline/slow) and peer-logs.txt
                        // says so honestly. The upload then ships whatever arrived.
                        WaitForPeerLogs(peerGatherId);
                        bool ok = UploadReport(target, direct, dir, reason, tags);
                        try { onUploadComplete?.Invoke(ok, dir); } catch { }
                    }
                    finally { MarkReportDone(dir); }
                });
            }
            else if (peerGatherId != null)
                // No upload configured — still collect the peer logs into the local folder.
                Task.Run(() => { try { WaitForPeerLogs(peerGatherId); } finally { MarkReportDone(dir); } });
            else
                MarkReportDone(dir);   // nothing async touches this folder — releasable immediately

            Plugin.Logger.LogInfo($"[BugReport] Created report at {dir}");
            if (openFolder) TryOpenFolder(dir);
            return result;
        }

        /// <summary>User directive 2026-08-16: keep only the LAST report — folders now carry
        /// saves + the zip (2–5MB each) and were never cleaned up. Runs as each NEW report is
        /// created and deletes every other bamp-bug-* folder, EXCEPT any whose background work
        /// (peer-log wait / upload) is still running — read LIVE from the busy registry at the
        /// moment of deletion, never inferred from folder age (a timer here was called out and
        /// replaced 2026-08-16: elapsed time is not a proxy for "upload finished"). A folder
        /// left busy by a crashed process is not in the fresh registry and prunes normally.
        /// Only exact-pattern folders are touched — the crash marker and anything a player
        /// parked in the root survive.</summary>
        private static void PruneOldReports(string root)
        {
            try
            {
                var rx = new System.Text.RegularExpressions.Regex(@"^bamp-bug-\d{8}-\d{6}$");
                int pruned = 0;
                foreach (var d in Directory.GetDirectories(root))
                {
                    if (!rx.IsMatch(Path.GetFileName(d))) continue;
                    if (IsReportBusy(d)) continue;   // live in-flight check — the event-driven guard
                    try { Directory.Delete(d, recursive: true); pruned++; }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[BugReport] prune '{Path.GetFileName(d)}': {ex.Message}"); }
                }
                if (pruned > 0) Plugin.Logger.LogInfo($"[BugReport] Pruned {pruned} old report folder(s) — only the latest report is kept.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BugReport] report prune: {ex.Message}"); }
        }

        // Busy registry: every report dir with background work still running (peer-log wait,
        // upload stream). Registered at creation, released in the terminal path's finally —
        // the prune reads this LIVE instead of guessing from timestamps.
        private static readonly HashSet<string> _reportDirsInUse = new();
        private static void MarkReportBusy(string dir) { lock (_reportDirsInUse) _reportDirsInUse.Add(dir); }
        private static void MarkReportDone(string dir) { lock (_reportDirsInUse) _reportDirsInUse.Remove(dir); }
        private static bool IsReportBusy(string dir)   { lock (_reportDirsInUse) return _reportDirsInUse.Contains(dir); }

        private static string SafeRoot()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(MPConfig.DataRootPath) && MPConfig.DataRootPath != ".")
                    return Path.Combine(MPConfig.DataRootPath, "bug-reports");
            }
            catch { }
            return Path.Combine(Path.GetTempPath(), "BigAmbitionsMP-bug-reports");
        }

        /// <summary>Task #5 (2026-07-08, Prabaha report): a hard crash writes its evidence to Unity's
        /// crash folder (%LOCALAPPDATA%\Temp\{Company}\{Product}\Crashes\Crash_*), NOT Player.log —
        /// the report we received proved our collection was blind to the actual death. Copy the newest
        /// crash folder (≤7 days old): error.log + its Player.log snapshot — TEXT files, so the zip's
        /// IPv4 redaction covers them. The MINIDUMP is deliberately EXCLUDED (maintainer determination
        /// 2026-07-08): a .dmp carries raw process memory — the host's public IP and the relay key can
        /// sit there as live strings, a binary dump can't be redacted, and without the game's debug
        /// symbols it adds nothing over error.log's stack anyway.</summary>
        private static void CollectUnityCrashArtifacts(string dir)
        {
            try
            {
                string crashes = Path.Combine(Path.GetTempPath(), Application.companyName ?? "Hovgaard Games",
                                              Application.productName ?? "Big Ambitions", "Crashes");
                if (!Directory.Exists(crashes)) return;
                DirectoryInfo newest = null;
                foreach (var d in new DirectoryInfo(crashes).GetDirectories("Crash_*"))
                    if (newest == null || d.LastWriteTime > newest.LastWriteTime) newest = d;
                if (newest == null || (DateTime.Now - newest.LastWriteTime).TotalDays > 7) return;

                string sub = Path.Combine(dir, "unity-crash");
                Directory.CreateDirectory(sub);
                int copied = 0;
                foreach (var f in newest.GetFiles())
                {
                    if (f.Extension.Equals(".dmp", StringComparison.OrdinalIgnoreCase)) continue;   // see summary
                    if (f.Length > MaxCopiedLogBytes) continue;
                    try { File.Copy(f.FullName, Path.Combine(sub, "crash-" + f.Name), overwrite: true); copied++; } catch { }
                }
                Plugin.Logger.LogInfo($"[BugReport] Unity crash artifacts: '{newest.Name}' ({newest.LastWriteTime:g}) — {copied} file(s) attached (minidump excluded by policy).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BugReport] crash artifact collection: {ex.Message}"); }
        }

        private static void WriteReport(string path, string reason)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {MyPluginInfo.DISPLAY_NAME} — Bug Report");
            sb.AppendLine();
            sb.AppendLine($"Created: {DateTime.Now:O}");
            sb.AppendLine($"Reason: {reason}");
            sb.AppendLine($"Mod: {MyPluginInfo.PLUGIN_VERSION} ({MyPluginInfo.BuildTag})");
            sb.AppendLine($"Role: {Role()}");
            sb.AppendLine($"Session: {Blank(MPLog.SessionId)}");
            sb.AppendLine($"PlayerId: {Blank(MPConfig.PlayerId)}");
            sb.AppendLine($"StableIdKind: {StableIdKind()}");
            sb.AppendLine($"Port: {MPConfig.Port}");
            sb.AppendLine($"LobbyPlayers: {string.Join(", ", LobbyPlayers())}");
            sb.AppendLine($"ConnectedClients: {(MPServer.IsRunning ? MPServer.ConnectedCount.ToString(CultureInfo.InvariantCulture) : "n/a")}");
            sb.AppendLine($"ClientConnected: {MPClient.IsConnected}");
            // Batch 14: PendingCrashDetected is LAUNCH-scoped state that Acknowledge can clear
            // before/independently of this report — field triage sorting on it reached wrong
            // conclusions (a real-dump bundle read False).  Keep it for continuity, but the
            // hint line below is the trustworthy evidence; readers should prefer it.
            sb.AppendLine($"PreviousCrashDetected: {PendingCrashDetected} (launch-scoped; may read False on reports filed after acknowledge — trust PreviousCrashHint)");
            if (!string.IsNullOrWhiteSpace(PendingCrashHint))
                sb.AppendLine($"PreviousCrashHint: {PendingCrashHint}");
            // A failed/dead patch class is silent feature loss — every report names them (2026-07-09).
            sb.AppendLine($"PatchIssues: {(ModEntry.PatchIssues.Count == 0 ? "none" : string.Join(" | ", ModEntry.PatchIssues))}");
            // What the save-integrity sweep repaired/detected on this save — even when the
            // player reports something unrelated, the field self-reports data health (2026-07-12).
            sb.AppendLine($"IntegrityFindings: {(string.IsNullOrEmpty(MPSaveIntegrity.LastSummary) ? "none" : MPSaveIntegrity.LastSummary)}");
            sb.AppendLine($"TornSaveReads: {(string.IsNullOrEmpty(MPSaveCoordinator.LastTornRead) ? "none" : MPSaveCoordinator.LastTornRead)}");   // round-251B detect-only signal
            // Round-90b (user-directed 2026-08-17): the navigation-blocker state AT REPORT TIME.
            // "I'm stuck" + a key marked "owner CLOSED — STUCK" here = the round-90 class caught
            // in the act; keys marked "owner open" are normal play (map being read, driving).
            // Round-279 (fix C): prefer the snapshot taken at popup-open — the live read
            // is self-poisoned (the popup's input-block sets HelpSystem before this runs).
            sb.AppendLine($"NavBlockers: {(MPRestSync.PreReportNavBlockers != null ? MPRestSync.PreReportNavBlockers + " (sampled at popup open)" : MPRestSync.DescribeNavBlockersForReport())}");
            sb.AppendLine();
            sb.AppendLine("## Runtime");
            // (InstalledMods below — round-57, Rialgame report 2026-07-22: a broken third-party
            // mod (Voogle Route, missing companion DLL) was only inferable from exception text;
            // every report should answer "what else is running?" at a glance.)
            sb.AppendLine($"GameVersion: {Blank(Application.version)}");
            // Round-102: GameVersion is coarse — two installs a month apart both report the same
            // string while carrying different item/business data (our own rig did exactly that,
            // and it read as a mod bug for four rounds). This fingerprint makes "these two players
            // are not running the same game content" visible at a glance across two reports.
            sb.AppendLine($"ContentFingerprint: {Blank(MPContentFingerprint.Cached)}");
            sb.AppendLine($"UnityVersion: {Blank(Application.unityVersion)}");
            sb.AppendLine($"Scene: {ActiveSceneName()}");
            sb.AppendLine($"GameRoot: {Blank(MPConfig.GameRootPath)}");
            sb.AppendLine($"InstalledMods: {Blank(ListInstalledMods())}");
            sb.AppendLine($"PersistentDataPath: {Blank(Application.persistentDataPath)}");
            sb.AppendLine($"OS: {Environment.OSVersion}");
            sb.AppendLine($"64BitProcess: {Environment.Is64BitProcess}");
            sb.AppendLine();
            sb.AppendLine("## Notes");
            sb.AppendLine("- Add what you were doing when the bug happened.");
            sb.AppendLine("- If another player was connected, attach their report too.");
            if (!string.IsNullOrWhiteSpace(_pendingCrashSummary))
            {
                sb.AppendLine();
                sb.AppendLine("## Previous Session Marker");
                sb.AppendLine("```json");
                sb.AppendLine(_pendingCrashSummary);
                sb.AppendLine("```");
            }
            File.WriteAllText(path, sb.ToString());
        }

        private static void WriteDescription(string path, string reason)
        {
            var sb = new StringBuilder();
            sb.AppendLine("PLAYER DESCRIPTION");
            sb.AppendLine("==================");
            sb.AppendLine(string.IsNullOrWhiteSpace(reason) ? "(no description provided)" : reason.Trim());
            sb.AppendLine();
            sb.AppendLine("REPORT CONTEXT");
            sb.AppendLine("==============");
            sb.AppendLine($"Created: {DateTime.Now:O}");
            sb.AppendLine($"Role: {Role()}");
            sb.AppendLine($"Session: {Blank(MPLog.SessionId)}");
            sb.AppendLine($"PlayerId: {Blank(MPConfig.PlayerId)}");
            sb.AppendLine($"Mod: {MyPluginInfo.PLUGIN_VERSION} ({MyPluginInfo.BuildTag})");
            sb.AppendLine($"Scene: {ActiveSceneName()}");
            File.WriteAllText(path, sb.ToString());
        }

        private static string _startedStamp;   // the ORIGINAL session start — heartbeats must not reset it

        private static Dictionary<string, string> BuildMarker(string reason, bool crashTest)
        {
            return new Dictionary<string, string>
            {
                ["State"] = "open",
                ["Started"] = _startedStamp ??= DateTime.Now.ToString("O"),
                ["Reason"] = reason,
                ["CrashTest"] = crashTest ? "true" : "false",
                ["ModVersion"] = MyPluginInfo.PLUGIN_VERSION,
                ["BuildTag"] = MyPluginInfo.BuildTag,
                ["Role"] = Role(),
                ["SessionId"] = MPLog.SessionId ?? "",
                ["PlayerId"] = MPConfig.PlayerId ?? "",
                ["StableIdKind"] = StableIdKind()
            };
        }

        private static void WriteOpenMarker(string reason, bool crashTest)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_markerPath))
                    _markerPath = Path.Combine(SafeRoot(), "session-open.json");
                Directory.CreateDirectory(Path.GetDirectoryName(_markerPath) ?? SafeRoot());
                var marker = BuildMarker(reason, crashTest);
                File.WriteAllText(_markerPath, JsonConvert.SerializeObject(marker, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[BugReport] Session marker write failed: {ex.Message}");
            }
        }

        private static string Role()
        {
            if (MPServer.IsRunning) return "host";
            if (MPClient.IsConnected) return "client";
            return "offline";
        }

        private static List<string> LobbyPlayers()
        {
            try
            {
                if (MPServer.IsRunning) return new List<string>(MPServer.LobbyPlayers);
                if (MPClient.IsConnected) return new List<string>(MPClient.LobbyPlayers);
            }
            catch { }
            return new List<string>();
        }

        private static string ActiveSceneName()
        {
            try { return SceneManager.GetActiveScene().name ?? ""; }
            catch { return ""; }
        }

        private static string StableIdKind()
        {
            string id = MPConfig.StableId ?? "";
            if (id.StartsWith("steam-", StringComparison.OrdinalIgnoreCase)) return "steam";
            if (id.StartsWith("guid-", StringComparison.OrdinalIgnoreCase)) return "guid";
            return string.IsNullOrWhiteSpace(id) ? "unset" : "other";
        }

        private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "(blank)" : value;

        /// <summary>Round-57: enumerate installed mod folders — Workshop items (with the inner mod
        /// folder named when present) + ModsLocal — so a report answers "what else is running?"
        /// without exception archaeology. Best-effort: any failure yields a partial/empty list.</summary>
        internal static string ListInstalledMods()   // round-253: also feeds the join-time mod-mismatch info (MPContentFingerprint.CachedMods)
        {
            var parts = new System.Collections.Generic.List<string>();
            try
            {
                var root = MPConfig.GameRootPath;                                     // .../steamapps/common/Big Ambitions
                if (!string.IsNullOrEmpty(root))
                {
                    var steamapps = Path.GetDirectoryName(Path.GetDirectoryName(root));
                    if (!string.IsNullOrEmpty(steamapps))
                    {
                        var ws = Path.Combine(steamapps, "workshop", "content", "1331550");
                        if (Directory.Exists(ws))
                            foreach (var item in Directory.GetDirectories(ws))
                            {
                                string id = Path.GetFileName(item), inner = "";
                                try { var subs = Directory.GetDirectories(item); if (subs.Length > 0) inner = Path.GetFileName(subs[0]); } catch { }
                                // Round-258: the workshop also carries shared building-LAYOUT
                                // blueprints (Layout.json + Metadata.json, no code, no content
                                // dirs) — pure save-side templates that cannot desync gameplay.
                                // Tag them so reports still show them but the join-time mod
                                // comparison (DiffMods) can skip them: a layout subscriber must
                                // not trip mismatch warnings against a non-subscriber.
                                bool blueprint = false;
                                try
                                {
                                    blueprint = File.Exists(Path.Combine(item, "Layout.json"))
                                             && Directory.GetFiles(item, "*.dll", SearchOption.TopDirectoryOnly).Length == 0
                                             && string.IsNullOrEmpty(inner);
                                }
                                catch { }
                                if (blueprint) parts.Add($"layout:{id}");
                                else parts.Add(string.IsNullOrEmpty(inner) ? $"workshop:{id}" : $"workshop:{id}({inner})");
                            }
                    }
                }
                var local = Path.Combine(Application.persistentDataPath, "ModsLocal");
                if (Directory.Exists(local))
                    foreach (var d in Directory.GetDirectories(local))
                        parts.Add($"local:{Path.GetFileName(d)}");
            }
            catch { }
            return string.Join(", ", parts);
        }

        private static void CopyPlayerLogs(string dir)
        {
            try
            {
                // T-BR2 run 2026-08-17 defect: this read the DEFAULT location while the peer-send
                // path honored consoleLogPath — a client with a -logFile redirect attached the
                // wrong machine-half's log (its own live log lives at consoleLogPath). Both paths
                // now choose the same way: the real live log first, default-location fallback.
                string baseDir = Application.persistentDataPath;
                string live = "";
                try { live = Application.consoleLogPath ?? ""; } catch { }
                if (string.IsNullOrEmpty(live) || !File.Exists(live)) live = Path.Combine(baseDir, "Player.log");
                CopyIfExists(live, Path.Combine(dir, "Player.log"), MaxCopiedLogBytes);
                CopyIfExists(Path.Combine(baseDir, "Player-prev.log"), Path.Combine(dir, "Player-prev.log"), MaxCopiedLogBytes);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BugReport] Player log copy failed: {ex.Message}"); }
        }

        private static void CopyIfExists(string source, string dest, int maxBytes)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return;
                var fi = new FileInfo(source);
                if (fi.Length <= maxBytes)
                {
                    File.Copy(source, dest, true);
                    return;
                }

                // Round-70 (TrainingWh33ls 20260723-063104): the tail-only cap discarded the session
                // HEAD — mod list, patch summary, and the FIRST occurrence of a frame-spam exception —
                // exactly the diagnostic part of a spam-flooded log. Keep head + tail; the omitted
                // middle of a log that big is repetition by definition.
                int headBytes = Math.Min(256 * 1024, maxBytes / 4);
                int tailBytes = maxBytes - headBytes;
                using var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var output = File.Create(dest);
                var head = new byte[headBytes];
                int headRead = input.Read(head, 0, headBytes);
                output.Write(head, 0, headRead);
                var sep = Encoding.UTF8.GetBytes($"\r\n\r\n# ---- middle omitted: kept first {headRead} and last {tailBytes} of {fi.Length} bytes ({source}) ----\r\n\r\n");
                output.Write(sep, 0, sep.Length);
                input.Seek(-tailBytes, SeekOrigin.End);
                input.CopyTo(output);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BugReport] Copy '{source}': {ex.Message}"); }
        }

        // ── Bug-report v2 (task #40, user-directed 2026-08-15) ─────────────────────────────
        // Bundle 20260811-225015 (a 3-day rollback diagnosed by hand-correlating ~30k log
        // lines of save sizes across two sessions) is the template case: the evidence the
        // logs only imply, the save store STATES. Attach the active session's saves (one
        // .hsg per player — loadable first-hand on the rig) + a listing of EVERY copy in
        // the lineage (size/time/day — where stale-copy bugs are visible for ~1KB), and
        // pull connected peers' logs so third-party reports carry both halves.

        /// <summary>Total bytes of .hsg/.json save files attached per report. Saves are
        /// already compressed — they add full weight to the zip (logs don't). The Discord
        /// relay accepts 24MB; 6MB leaves room for logs and old multi-player worlds.</summary>
        private const long SaveAttachBudgetBytes = 6L * 1024 * 1024;

        /// <summary>Write save-store.md (every lineage copy: player, day, size, mtime) and
        /// copy the ACTIVE session's files under saves/ — plus any copy whose manifest day
        /// disagrees with the active session's by more than the ±1 midnight-straddle the
        /// round-233 fence tolerates (those are exactly the stale/future copies rollback
        /// bugs live in). Local file IO only: the host holds the store natively and
        /// clients hold the mirrored copy, so this works even for menu reports.</summary>
        private static void WriteSaveStore(string dir)
        {
            try
            {
                string session = "";
                try { session = MPSaveCoordinator.ActiveSessionName ?? ""; } catch { }
                var sb = new StringBuilder();
                sb.AppendLine("# Save store at report time");
                if (string.IsNullOrWhiteSpace(session))
                {
                    sb.AppendLine("No active MP session — no saves attached (menu report or a world that has never saved).");
                    File.WriteAllText(Path.Combine(dir, "save-store.md"), sb.ToString());
                    return;
                }
                int fmt = 0; try { fmt = MPSaveManager.StoreFormat(); } catch { }
                sb.AppendLine($"ActiveSession: {session}   StoreFormat: v{fmt}");
                sb.AppendLine();
                sb.AppendLine("| session | player | day | .hsg bytes | written (UTC) | attached |");
                sb.AppendLine("|---|---|---|---|---|---|");

                long budget = SaveAttachBudgetBytes;
                string savesDir = Path.Combine(dir, "saves");

                // The anomaly reference: each member's day in the ACTIVE session's manifest.
                var activeManifest = MPSaveManager.ReadManifest(session);
                var activeDays = new Dictionary<string, int>();
                if (activeManifest?.Slots != null)
                    foreach (var s in activeManifest.Slots) activeDays[s.StableId] = s.Day;

                foreach (var s in MPSaveCoordinator.LineageSessions(session))
                {
                    string folder = "";
                    try { folder = MPSaveManager.MpSessionFolder(s); } catch { }
                    if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                    bool isActive = s == session;
                    var manifest = isActive ? activeManifest : MPSaveManager.ReadManifest(s);

                    // Session-root json (manifest + ledgers) rides along for the active
                    // session — without it the attached .hsg set isn't loadable.
                    if (isActive)
                        foreach (var j in Directory.GetFiles(folder, "*.json"))
                        {
                            long len = 0; try { len = new FileInfo(j).Length; } catch { }
                            if (AttachSaveFile(j, Path.Combine(savesDir, s, Path.GetFileName(j)), budget)) budget -= len;
                        }

                    foreach (var memberDir in Directory.GetDirectories(folder))
                    {
                        string stable = Path.GetFileName(memberDir);
                        if (!stable.StartsWith("guid-") && !stable.StartsWith("steam-")) continue;   // character folders only
                        string? hsg = null; DateTime newest = DateTime.MinValue;
                        foreach (var f in Directory.GetFiles(memberDir, "*.hsg"))
                        {
                            DateTime w; try { w = File.GetLastWriteTimeUtc(f); } catch { continue; }
                            if (w > newest) { newest = w; hsg = f; }
                        }
                        if (hsg == null) continue;
                        var slot = manifest?.Slots?.Find(x => x.StableId == stable);
                        int day = slot?.Day ?? -1;
                        string who = !string.IsNullOrEmpty(slot?.DisplayName) ? slot!.DisplayName : stable;
                        long bytes = 0; try { bytes = new FileInfo(hsg).Length; } catch { }

                        bool anomaly = !isActive && day >= 0 && activeDays.TryGetValue(stable, out int ad) && Math.Abs(day - ad) > 1;
                        string attachNote = "-";
                        if (isActive || anomaly)
                        {
                            string dest = Path.Combine(savesDir, isActive ? s : "anomaly-" + s, stable, Path.GetFileName(hsg));
                            if (AttachSaveFile(hsg, dest, budget)) { budget -= bytes; attachNote = anomaly ? "ANOMALY-ATTACHED" : "yes"; }
                            else attachNote = "over size budget";
                        }
                        sb.AppendLine($"| {s} | {who} | {(day >= 0 ? day.ToString(CultureInfo.InvariantCulture) : "?")} | {bytes:N0} | {newest:yyyy-MM-dd HH:mm:ss} | {attachNote} |");
                    }
                }
                sb.AppendLine();
                sb.AppendLine("Rows without files are metadata only — stale/mismatched copies are visible without uploading them.");
                File.WriteAllText(Path.Combine(dir, "save-store.md"), sb.ToString());
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BugReport] save store attach: {ex.Message}"); }
        }

        /// <summary>Copy one save file under the budget. Share-tolerant read (the .hsg may
        /// be mid-rotation); any failure = not attached, the listing row says so.</summary>
        private static bool AttachSaveFile(string source, string dest, long budgetLeft)
        {
            try
            {
                if (new FileInfo(source).Length > budgetLeft) return false;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var src = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var dst = File.Create(dest);
                src.CopyTo(dst);
                return true;
            }
            catch { return false; }
        }

        // ── Peer log pull ────────────────────────────────────────────────────────────────

        private sealed class PeerGather
        {
            public string Dir = "";
            public int ExpectedPeers;
            public int CompletedPeers;
            public readonly Dictionary<string, int> Remaining = new();   // pid → files still expected
            public readonly List<string> Notes = new();
            public readonly System.Threading.ManualResetEventSlim Done = new(false);
            public readonly object Lock = new();
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PeerGather> _peerGathers = new();

        /// <summary>Ask every connected peer for its logs. Returns the gather id the upload
        /// task waits on, or null when there is nobody to ask (menu / solo / offline).</summary>
        private static string? StartPeerLogGather(string dir)
        {
            try
            {
                int expected;
                bool asHost = false;
                try { asHost = MPServer.IsRunning; } catch { }
                if (asHost) expected = MPServer.ConnectedPids().Count;
                else expected = MPClient.IsConnected ? 1 : 0;   // a client's one peer is the host
                if (expected == 0) return null;

                string id = Guid.NewGuid().ToString("N");
                _peerGathers[id] = new PeerGather { Dir = dir, ExpectedPeers = expected };
                var payload = new PeerLogRequestPayload { RequestId = id };
                if (asHost) MPServer.BroadcastAny(MessageEnvelope.Create(MessageType.PeerLogRequest, "host", payload));
                else MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.PeerLogRequest, MPConfig.PlayerId, payload));
                Plugin.Logger.LogInfo($"[BugReport] Requested logs from {expected} connected player(s) for this report.");
                return id;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BugReport] peer log request: {ex.Message}"); return null; }
        }

        /// <summary>Block the UPLOAD TASK (never the game) until every peer's last file
        /// lands or the 12s deadline passes, then write peer-logs.txt naming exactly what
        /// arrived and what didn't. The event completes early on the last reply — the
        /// deadline is only the failure bound for offline/slow peers.</summary>
        private static void WaitForPeerLogs(string? gatherId)
        {
            if (string.IsNullOrEmpty(gatherId) || !_peerGathers.TryGetValue(gatherId!, out var g)) return;
            bool all = false;
            try { all = g.Done.Wait(TimeSpan.FromSeconds(12)); } catch { }
            _peerGathers.TryRemove(gatherId!, out _);
            try
            {
                var sb = new StringBuilder();
                lock (g.Lock)
                {
                    sb.AppendLine($"Peer log collection: {g.CompletedPeers}/{g.ExpectedPeers} player(s) replied" +
                                  (all ? "." : " before the 12s deadline — missing players were offline or slow (their machine can file its own report)."));
                    foreach (var n in g.Notes) sb.AppendLine("- " + n);
                    if (g.Notes.Count == 0) sb.AppendLine("- no files received");
                }
                File.WriteAllText(Path.Combine(g.Dir, "peer-logs.txt"), sb.ToString());
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BugReport] peer log status: {ex.Message}"); }
        }

        /// <summary>Receiver side of PeerLogReply (both roles): store the file under peer/
        /// and complete the gather when every expected peer has delivered its last file.</summary>
        internal static void HandlePeerLogReply(PeerLogReplyPayload? p)
        {
            if (p == null || string.IsNullOrEmpty(p.RequestId) || !_peerGathers.TryGetValue(p.RequestId, out var g)) return;
            try
            {
                lock (g.Lock)
                {
                    string pid = string.IsNullOrWhiteSpace(p.FromPid) ? "peer" : p.FromPid;
                    if (p.TotalFiles <= 0)
                    {
                        if (!g.Remaining.ContainsKey(pid)) { g.Remaining[pid] = 0; g.CompletedPeers++; g.Notes.Add($"{pid}: no readable log on their machine"); }
                    }
                    else
                    {
                        if (!g.Remaining.ContainsKey(pid)) g.Remaining[pid] = p.TotalFiles;
                        if (!string.IsNullOrEmpty(p.FileName) && !string.IsNullOrEmpty(p.GzipBase64))
                        {
                            string peerDir = Path.Combine(g.Dir, "peer");
                            Directory.CreateDirectory(peerDir);
                            string name = pid + "-" + p.FileName;
                            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                            File.WriteAllText(Path.Combine(peerDir, name), GunzipToText(p.GzipBase64));
                            g.Notes.Add($"{pid}: {p.FileName} ({p.RawLength:N0} chars{(p.Truncated ? ", head+tail capped" : "")})");
                        }
                        if (--g.Remaining[pid] <= 0) g.CompletedPeers++;
                    }
                    if (g.CompletedPeers >= g.ExpectedPeers) g.Done.Set();
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BugReport] peer log reply: {ex.Message}"); }
        }

        /// <summary>Sender side of PeerLogRequest (both roles, background thread): read this
        /// machine's logs with the same 4MB head+tail cap the reporter's own copies get,
        /// redact HERE (raw text never crosses the wire), gzip, and hand each file to
        /// <paramref name="send"/>. A peer with nothing readable still replies (TotalFiles=0)
        /// so the reporter's wait completes without eating the deadline.</summary>
        internal static void RespondToPeerLogRequest(string requestId, Action<PeerLogReplyPayload> send)
        {
            try
            {
                // consoleLogPath is the ACTUAL live log — it follows -logFile redirects
                // (players with Steam launch options; the rig's second instance writes
                // Player-instance2.log). persistentDataPath\Player.log is the default-
                // location fallback; Player-prev.log only exists at the default location.
                var files = new List<string>();
                string live = _consoleLogPath;
                string baseDir = PersistentDataPathSafe();
                if (!string.IsNullOrEmpty(live) && File.Exists(live)) files.Add(live);
                else if (!string.IsNullOrEmpty(baseDir) && File.Exists(Path.Combine(baseDir, "Player.log"))) files.Add(Path.Combine(baseDir, "Player.log"));
                string prev = string.IsNullOrEmpty(baseDir) ? "" : Path.Combine(baseDir, "Player-prev.log");
                if (prev.Length > 0 && File.Exists(prev) && !files.Contains(prev)) files.Add(prev);
                if (files.Count == 0)
                {
                    send(new PeerLogReplyPayload { RequestId = requestId, FromPid = MPConfig.PlayerId, TotalFiles = 0 });
                    return;
                }
                foreach (var f in files)
                {
                    string text = RedactSensitive(ReadLogTextCapped(f, MaxCopiedLogBytes, out bool truncated));
                    send(new PeerLogReplyPayload
                    {
                        RequestId = requestId, FromPid = MPConfig.PlayerId, FileName = Path.GetFileName(f),
                        GzipBase64 = GzipToBase64(text), RawLength = text.Length, TotalFiles = files.Count, Truncated = truncated,
                    });
                }
                Plugin.Logger.LogInfo($"[BugReport] Sent {files.Count} redacted log file(s) for a report being filed by another player.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BugReport] peer log respond: {ex.Message}"); }
        }

        /// <summary>Unity's Application.persistentDataPath/consoleLogPath are main-thread
        /// APIs; the peer responder runs on a background thread. Cached from Plugin init.</summary>
        private static string _persistentDataPath = "";
        private static string _consoleLogPath = "";
        internal static void CachePaths()
        {
            try { _persistentDataPath = Application.persistentDataPath ?? ""; } catch { }
            try { _consoleLogPath = Application.consoleLogPath ?? ""; } catch { }
        }
        private static string PersistentDataPathSafe()
        {
            if (!string.IsNullOrEmpty(_persistentDataPath)) return _persistentDataPath;
            try { _persistentDataPath = Application.persistentDataPath ?? ""; } catch { }
            return _persistentDataPath;
        }

        /// <summary>Same head+tail cap as CopyIfExists, returning text instead of a file.</summary>
        private static string ReadLogTextCapped(string source, int maxBytes, out bool truncated)
        {
            truncated = false;
            var fi = new FileInfo(source);
            using var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fi.Length <= maxBytes)
            {
                using var sr = new StreamReader(input, Encoding.UTF8);
                return sr.ReadToEnd();
            }
            truncated = true;
            int headBytes = Math.Min(256 * 1024, maxBytes / 4);
            int tailBytes = maxBytes - headBytes;
            var head = new byte[headBytes];
            int headRead = input.Read(head, 0, headBytes);
            input.Seek(-tailBytes, SeekOrigin.End);
            var tail = new byte[tailBytes];
            int tailRead = input.Read(tail, 0, tailBytes);
            return Encoding.UTF8.GetString(head, 0, headRead)
                 + $"\r\n\r\n# ---- middle omitted: kept first {headRead} and last {tailRead} of {fi.Length} bytes ----\r\n\r\n"
                 + Encoding.UTF8.GetString(tail, 0, tailRead);
        }

        private static string GzipToBase64(string text)
        {
            var raw = Encoding.UTF8.GetBytes(text);
            using var ms = new MemoryStream();
            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                gz.Write(raw, 0, raw.Length);
            return Convert.ToBase64String(ms.ToArray());
        }

        private static string GunzipToText(string b64)
        {
            using var ms = new MemoryStream(Convert.FromBase64String(b64));
            using var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress);
            using var sr = new StreamReader(gz, Encoding.UTF8);
            return sr.ReadToEnd();
        }

        private static void CopyUserAttachments(string dir, IEnumerable<string>? attachments)
        {
            if (attachments == null) return;
            try
            {
                string attachDir = Path.Combine(dir, "attachments");
                Directory.CreateDirectory(attachDir);
                var skipped = new List<string>();
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in attachments)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(raw) || !File.Exists(raw)) continue;
                        var fi = new FileInfo(raw);
                        if (fi.Length > MaxUserAttachmentBytes)
                        {
                            skipped.Add($"{fi.Name} ({fi.Length / 1024 / 1024} MB, over 24 MB Discord upload limit)");
                            continue;
                        }

                        string safe = SafeAttachmentName(fi.Name);
                        string name = safe;
                        int n = 2;
                        while (usedNames.Contains(name) || File.Exists(Path.Combine(attachDir, name)))
                        {
                            name = Path.GetFileNameWithoutExtension(safe) + "-" + n + Path.GetExtension(safe);
                            n++;
                        }
                        usedNames.Add(name);
                        File.Copy(fi.FullName, Path.Combine(attachDir, name), true);
                    }
                    catch (Exception ex)
                    {
                        skipped.Add($"{Path.GetFileName(raw)} ({ex.Message})");
                    }
                }

                if (skipped.Count > 0)
                    File.WriteAllLines(Path.Combine(attachDir, "skipped-attachments.txt"), skipped);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[BugReport] User attachments failed: {ex.Message}");
            }
        }

        private static string SafeAttachmentName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "attachment.bin";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            if (name.Length > 80)
            {
                string ext = Path.GetExtension(name);
                string stem = Path.GetFileNameWithoutExtension(name);
                if (stem.Length > 70) stem = stem.Substring(0, 70);
                name = stem + ext;
            }
            return string.IsNullOrWhiteSpace(name) ? "attachment.bin" : name;
        }

        private static void WriteRedactedConfig(string path)
        {
            try
            {
                var redacted = new Dictionary<string, string>();
                if (File.Exists(MPConfig.ConfigPath))
                    redacted = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(MPConfig.ConfigPath))
                               ?? new Dictionary<string, string>();

                foreach (var key in redacted.Keys.ToList())
                {
                    if (key.IndexOf("StableId", StringComparison.OrdinalIgnoreCase) >= 0)
                        redacted[key] = StableIdKind();
                    else if (key.IndexOf("Webhook", StringComparison.OrdinalIgnoreCase) >= 0)
                        redacted[key] = string.IsNullOrWhiteSpace(redacted[key]) ? "" : "<configured>";
                    else if (key.IndexOf("HostIP", StringComparison.OrdinalIgnoreCase) >= 0)
                        redacted[key] = IpKind(redacted[key]);
                }

                File.WriteAllText(path, JsonConvert.SerializeObject(redacted, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[BugReport] Redacted config failed: {ex.Message}");
            }
        }

        private static string IpKind(string value)
        {
            if (!IPAddress.TryParse(value, out var ip)) return string.IsNullOrWhiteSpace(value) ? "" : "configured";
            if (IPAddress.IsLoopback(ip)) return "loopback";
            byte[] b = ip.GetAddressBytes();
            if (b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)))
                return "private";
            return "public";
        }

        private static void WriteSubmitNotes(string path)
        {
            File.WriteAllText(path,
                "Attach this whole folder to the GitHub issue or Discord thread.\r\n" +
                "GitHub issues: https://github.com/Melaus123/BigAmbitionMulti/issues\r\n" +
                "If Discord upload is configured, this report was also queued for webhook upload.\r\n");
        }

        private static void TryOpenFolder(string dir)
        {
            try
            {
                if (Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor)
                    Process.Start("explorer.exe", "\"" + dir + "\"");
                else
                    Application.OpenURL("file:///" + dir.Replace("\\", "/"));
            }
            catch { }
        }

        private static bool UploadReport(string url, bool direct, string dir, string reason, string[] discordTagIds)
        {
            try
            {
                if (direct && !LooksLikeDiscordWebhook(url))
                {
                    Plugin.Logger.LogWarning("[BugReport] Direct webhook URL is not a Discord webhook; upload skipped.");
                    return false;
                }

                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

                // One zip per report (2026-07-08) — the redaction already happened inside the
                // bundle, and WriteFilePart streams .zip as binary. Loose files only as fallback.
                // Built BEFORE the connection opens (task #40, user-directed 2026-08-16): a
                // refused/failed connection then still leaves the REDACTED zip in the report
                // folder — the file an offline player should share manually (the loose files
                // are raw by design) — and the rig can verify the zip without a live relay.
                string zip = BuildUploadZip(dir);

                string boundary = "----BAMPBugReport" + Guid.NewGuid().ToString("N");
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.UserAgent = "BigAmbitionsMP";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                req.ContentType = "multipart/form-data; boundary=" + boundary;
                if (!direct)   // relay path — optional shared-key header (matches the Worker's RELAY_KEY)
                {
                    string relayKey = MPConfig.BugReportRelayKeyLive();
                    if (relayKey.Length > 0) req.Headers["X-BAMP-Key"] = relayKey;
                }

                using (var stream = req.GetRequestStream())
                {
                    string content = $"{MyPluginInfo.SHORT_NAME} bug report: {Role()} / session {Blank(MPLog.SessionId)} / {reason}";
                    var payloadObj = new Dictionary<string, object>
                    {
                        ["content"] = content,
                        ["thread_name"] = DiscordThreadName(reason)
                    };
                    if (discordTagIds.Length > 0)
                        payloadObj["applied_tags"] = discordTagIds;
                    var payload = JsonConvert.SerializeObject(payloadObj);
                    WriteStringPart(stream, boundary, "payload_json", payload, "application/json");

                    if (zip.Length > 0)
                    {
                        WriteFilePart(stream, boundary, "files[0]", zip);
                    }
                    else
                    {
                        int index = 0;
                        foreach (var file in UploadFiles(dir))
                        {
                            WriteFilePart(stream, boundary, "files[" + index + "]", file);
                            index++;
                        }
                    }

                    WriteAscii(stream, "--" + boundary + "--\r\n");
                }

                using var resp = (HttpWebResponse)req.GetResponse();
                Plugin.Logger.LogInfo($"[BugReport] Discord upload completed: {(int)resp.StatusCode} {resp.StatusCode}");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[BugReport] Discord upload failed: {DiscordError(ex)}");
                return false;
            }
        }

        private static string DiscordError(Exception ex)
        {
            try
            {
                if (ex is WebException web && web.Response != null)
                {
                    using var resp = web.Response;
                    using var stream = resp.GetResponseStream();
                    if (stream != null)
                    using (var reader = new StreamReader(stream))
                    {
                        string body = reader.ReadToEnd();
                        if (!string.IsNullOrWhiteSpace(body))
                            return ex.Message + " body=" + body;
                    }
                }
            }
            catch { }
            return ex.Message;
        }

        private static bool LooksLikeDiscordWebhook(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                   && (uri.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase)
                       || uri.Host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase))
                   && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase);
        }

        private static string[] CleanDiscordTagIds(IEnumerable<string>? ids)
        {
            if (ids == null) return Array.Empty<string>();
            var clean = new List<string>();
            foreach (var raw in ids)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var sb = new StringBuilder(raw.Length);
                foreach (char c in raw.Trim())
                    if (char.IsDigit(c)) sb.Append(c);
                string id = sb.ToString();
                if (id.Length > 0 && !clean.Contains(id))
                    clean.Add(id);
            }
            return clean.ToArray();
        }

        private static string DiscordThreadName(string reason)
        {
            // Keep only what's useful in a forum-list title: [role] + the player's own words
            // (crash-tagged).  The date / session / "manual bug report:" noise lives in the body.
            string role = Role();
            string desc = (reason ?? "").Trim();
            bool crash = desc.StartsWith("previous crash", StringComparison.OrdinalIgnoreCase);
            foreach (var p in new[] { "previous crash:", "manual bug report:", "manual report:", "bug report:" })
                if (desc.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { desc = desc.Substring(p.Length).Trim(); break; }
            if (desc.Length == 0) desc = crash ? "crash" : "bug report";

            string title = "[" + role + "] " + (crash ? "CRASH — " : "") + desc;
            var sb = new StringBuilder();
            foreach (char c in title) sb.Append(char.IsControl(c) ? ' ' : c);
            string name = sb.ToString().Trim();
            if (name.Length > 90) name = name.Substring(0, 90).TrimEnd() + "…";
            return name.Length == 0 ? MyPluginInfo.SHORT_NAME + " bug report" : name;
        }

        private static IEnumerable<string> UploadFiles(string dir)
        {
            foreach (var name in new[] { "description.txt", "report.md", "save-store.md", "peer-logs.txt", "Player.log", "Player-prev.log", "bamp-ring.log", "config-redacted.json" })
            {
                string path = Path.Combine(dir, name);
                if (File.Exists(path)) yield return path;
            }

            string crashDir = Path.Combine(dir, "unity-crash");
            if (Directory.Exists(crashDir))
                foreach (var path in Directory.GetFiles(crashDir)) yield return path;

            // Bug-report v2 (task #40): attached saves + peer logs, nested structure intact
            // (saves/<session>/<stable>/save.hsg restores by straight copy). The .hsg files
            // are binary → the zip streams them raw; the .log/.json entries go through the
            // text redaction like everything else.
            foreach (var sub in new[] { "saves", "peer" })
            {
                string d = Path.Combine(dir, sub);
                if (Directory.Exists(d))
                    foreach (var path in Directory.GetFiles(d, "*", SearchOption.AllDirectories)) yield return path;
            }

            string attachDir = Path.Combine(dir, "attachments");
            if (!Directory.Exists(attachDir)) yield break;
            foreach (var path in Directory.GetFiles(attachDir))
            {
                if (Path.GetFileName(path).Equals("skipped-attachments.txt", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (new FileInfo(path).Length <= MaxUserAttachmentBytes)
                    yield return path;
            }
        }

        /// <summary>Bundle the whole upload set into ONE zip (user directive 2026-07-08 — a single
        /// file per Discord post instead of a spray of attachments). Text files (.log/.txt/.md/.json)
        /// go through the same IPv4 redaction the loose-file upload applied — redaction must happen
        /// BEFORE compression, a zip entry can't be scrubbed in flight. The local report folder keeps
        /// the un-redacted originals, exactly as before. Returns "" on failure (caller falls back to
        /// loose files so a zip bug can never lose a report).</summary>
        private static string BuildUploadZip(string dir)
        {
            try
            {
                string zipPath = Path.Combine(dir, Path.GetFileName(dir) + ".zip");
                using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
                using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
                {
                    foreach (var file in UploadFiles(dir))
                    {
                        // Preserve the one level of structure that matters (unity-crash/, attachments/).
                        string rel = file.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
                                   ? file.Substring(dir.Length).TrimStart('\\', '/').Replace('\\', '/')
                                   : Path.GetFileName(file);
                        var entry = zip.CreateEntry(rel, System.IO.Compression.CompressionLevel.Optimal);
                        using var es = entry.Open();
                        string ext = Path.GetExtension(file);
                        bool text = ext.Equals(".log", StringComparison.OrdinalIgnoreCase) || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                                 || ext.Equals(".md", StringComparison.OrdinalIgnoreCase) || ext.Equals(".json", StringComparison.OrdinalIgnoreCase);
                        if (text)
                        {
                            var bytes = Encoding.UTF8.GetBytes(RedactSensitive(File.ReadAllText(file)));
                            es.Write(bytes, 0, bytes.Length);
                        }
                        else
                        {
                            using var src = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            src.CopyTo(es);
                        }
                    }
                }
                Plugin.Logger.LogInfo($"[BugReport] Upload bundle: {Path.GetFileName(zipPath)} ({new FileInfo(zipPath).Length / 1024} KB).");
                return zipPath;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[BugReport] zip bundle failed ({ex.Message}) — falling back to loose files.");
                return "";
            }
        }

        private static void WriteStringPart(Stream stream, string boundary, string name, string value, string contentType)
        {
            WriteAscii(stream, "--" + boundary + "\r\n");
            WriteAscii(stream, $"Content-Disposition: form-data; name=\"{name}\"\r\n");
            WriteAscii(stream, $"Content-Type: {contentType}\r\n\r\n");
            WriteBytes(stream, Encoding.UTF8.GetBytes(value));
            WriteAscii(stream, "\r\n");
        }

        private static void WriteFilePart(Stream stream, string boundary, string name, string path)
        {
            WriteAscii(stream, "--" + boundary + "\r\n");
            WriteAscii(stream, $"Content-Disposition: form-data; name=\"{name}\"; filename=\"{Path.GetFileName(path)}\"\r\n");
            WriteAscii(stream, "Content-Type: application/octet-stream\r\n\r\n");
            string ext = Path.GetExtension(path);
            if (ext.Equals(".log", StringComparison.OrdinalIgnoreCase) || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            {
                // Redact IPv4 addresses from TEXT uploads so the host's public IP is never published to Discord
                //   (the local report folder keeps the un-redacted originals). Maintainer decision 2026-06-16.
                WriteBytes(stream, Encoding.UTF8.GetBytes(RedactSensitive(File.ReadAllText(path))));
            }
            else
            {
                using (var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    file.CopyTo(stream);
            }
            WriteAscii(stream, "\r\n");
        }

        // Replace IPv4 addresses with a placeholder — hides the host's public IP from uploaded logs.
        private static readonly System.Text.RegularExpressions.Regex _ipv4 =
            new System.Text.RegularExpressions.Regex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", System.Text.RegularExpressions.RegexOptions.Compiled);
        // Bug-report v2 (task #40): the Windows account name in file paths is often a real
        // name (bundle 20260811-225015 showed the host's) and carries zero diagnostic value —
        // players are identified by in-game name + stable id, never by the account segment.
        // [\\/]+ (not [\\/]) so JSON-escaped paths (C:\\Users\\name) redact too; the segment
        // itself (8.3 short forms included) is replaced, everything after it is preserved.
        private static readonly System.Text.RegularExpressions.Regex _userPath =
            new System.Text.RegularExpressions.Regex(@"(?i)([A-Z]:[\\/]+Users[\\/]+)([^\\/\r\n""']+)", System.Text.RegularExpressions.RegexOptions.Compiled);
        internal static string RedactSensitive(string s)
            => string.IsNullOrEmpty(s) ? s : _userPath.Replace(_ipv4.Replace(s, "[redacted-ip]"), "$1[user]");

        private static void WriteAscii(Stream stream, string value) => WriteBytes(stream, Encoding.ASCII.GetBytes(value));

        private static void WriteBytes(Stream stream, byte[] bytes) => stream.Write(bytes, 0, bytes.Length);
    }
}
