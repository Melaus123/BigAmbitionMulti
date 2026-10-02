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
            StallWatch.Frame();   // P-MIDNIGHT: the per-frame main-thread stamp the stall watchdog reads (no allocation)
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

        // ── H-REPORTLOSS-1 (user-approved 2026-09-23) ─────────────────────────────────────
        // The confirmed loss: report 20260919-232955 (1.5 MB) hit the old 15 s timeout on its ONE
        // upload attempt, and 11 s later the next report's prune deleted its folder. Every POST
        // that reached the relay in 7 days was accepted, so the fix is on this side: a size
        // budget with a priority order, a timeout that grows with the upload, and a small
        // on-disk outbox that retries a failed report a few times instead of forgetting it.
        /// <summary>Zip budget per upload. A SPEED choice, not a Discord limit (Discord took
        /// everything up to 12 MB): a smaller upload finishes on a slow line.</summary>
        private const long UploadBudgetBytes = 9L * 1024 * 1024;
        private static long _devBudgetBytes = -1;   // TestDrive `bugbudget <KB>` (DEV builds only)
        private static long CurrentBudgetBytes => _devBudgetBytes > 0 ? _devBudgetBytes : UploadBudgetBytes;
        /// <summary>Room kept for bundle-index.txt, outbox.txt and the zip's own headers.</summary>
        private const long BundleReserveBytes = 64L * 1024;
        private const int MaxUploadAttempts = 6;
        private const int MaxPendingReports = 3;
        private const double MaxOutboxAgeDays = 7;
        /// <summary>Minutes from one attempt to the next, after attempts 1, 2 and 3: attempt 2 at
        /// +1 min, attempt 3 at +5 min and attempt 4 at +15 min (all counted from attempt 1).
        /// Attempts 5 and 6 happen at later game launches (the launch pass), never on a timer.</summary>
        private static readonly int[] RetryGapMinutes = { 1, 4, 10 };
        /// <summary>M3: user attachments are EXEMPT from the budget (the popup promises they go up,
        /// each up to 24 MB) but the whole POST must stay under the relay's 25 MiB ceiling; this
        /// leaves room for the multipart envelope around the zip.</summary>
        private const long UploadCeilingBytes = 25L * 1024 * 1024 - 256L * 1024;
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
                StallWatch.RotatePrevious();   // P-MIDNIGHT: the previous session's live ring log becomes '-prev' before this session writes

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
                if (MarkerClosed) return;   // M1: a clean shutdown already removed the marker - never write it back
                if (_sessionStartedAt < 0f) _sessionStartedAt = Time.unscaledTime;
                var marker = BuildMarker("normal start", false);
                marker["LastAlive"] = DateTime.Now.ToString("O");
                marker["Phase"] = StallWatch.PhaseText(phase ?? "");   // P-MIDNIGHT: names the running step, if any
                marker["UptimeSeconds"] = ((int)(Time.unscaledTime - _sessionStartedAt + 0.5f)).ToString(CultureInfo.InvariantCulture);
                _lastHeartbeatMarker = marker;   // M5: published COMPLETE - read-only from here on (the stall watchdog copies it)
                // Round-207g: serialize on the main thread (small object), WRITE on the
                // pool — the synchronous 30s disk write was the occasional ~70ms Pre.A
                // hitch. A lost heartbeat on crash costs ≤30s of "last alive" precision;
                // the open/close markers stay synchronous (crash-order-critical).
                string json = JsonConvert.SerializeObject(marker, Formatting.Indented);
                string path = _markerPath;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { lock (_markerCloseLock) { if (!_markerClosed) File.WriteAllText(path, json); } } catch { }   // M1
                });
            }
            catch { }
        }

        // P-MIDNIGHT (2026-09-27): the stall watchdog's marker write (BACKGROUND THREAD) - the last heartbeat's fields with
        // Phase naming the step the main thread is stuck in, so the next launch's crash hint names it.
        private static volatile Dictionary<string, string>? _lastHeartbeatMarker;

        // M1 (review of 47d8521, 2026-09-27): after a clean quit MarkCleanShutdown deletes the marker, but quit saves /
        // teardown stop frames, and the watchdog's stall write put it back ('open') -> a false crash popup at the next
        // launch. The CLOSED flag is set under this lock BEFORE the delete; every marker write after it checks the flag
        // under the same lock, so no write can land after the delete.
        private static readonly object _markerCloseLock = new object();
        private static bool _markerClosed;

        internal static bool MarkerClosed { get { lock (_markerCloseLock) return _markerClosed; } }

        /// <summary>DEV readout: does the session marker file exist right now.</summary>
        internal static bool MarkerFileExists()
        {
            try { string path = _markerPath; return !string.IsNullOrWhiteSpace(path) && File.Exists(path); } catch { return false; }
        }

        internal static void WriteStallMarker(string stallText)
        {
            try
            {
                var m = _lastHeartbeatMarker;
                string path = _markerPath;
                if (m == null || string.IsNullOrWhiteSpace(path)) return;
                var c = new Dictionary<string, string>(m);
                c["LastAlive"] = DateTime.Now.ToString("O");
                c.TryGetValue("Phase", out var ph);
                c["Phase"] = (ph ?? "") + "; " + stallText;
                string json = JsonConvert.SerializeObject(c, Formatting.Indented);
                lock (_markerCloseLock)
                {
                    if (_markerClosed) return;   // M1: the session closed cleanly - the marker stays deleted
                    File.WriteAllText(path, json);
                }
            }
            catch { }
        }

        public static void MarkCleanShutdown()
        {
            try
            {
                lock (_markerCloseLock)
                {
                    _markerClosed = true;   // M1: BEFORE the delete - no marker write lands after it
                    if (!string.IsNullOrWhiteSpace(_markerPath) && File.Exists(_markerPath))
                        File.Delete(_markerPath);
                }
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

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string dir = Path.Combine(root, "bamp-bug-" + stamp);
            // L16: two reports in the same second must never share a folder.
            for (int n = 2; Directory.Exists(dir); n++) dir = Path.Combine(root, "bamp-bug-" + stamp + "-" + n.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            // H-REPORTLOSS-1: outbox.lock is held from here through the peer-log wait and upload
            // attempt 1 (released in the background task's finally). It is the cross-process
            // "in use" guard every prune and outbox pass respects - this process's and the other
            // game instance's alike (the rig's instances share this folder).
            FileStream? folderLock = TryOpenOutboxLock(dir);
            bool lockHandedToTask = false;   // L14: released in the finally below unless a background task owns it
            try
            {

                // Submit to the RELAY by default (it holds the Discord webhook server-side).  A direct
                // webhook in config overrides it (maintainer local testing) and posts straight to Discord.
                // H-REPORTLOSS-1: the target is RECORDED in outbox.json now - a retry never goes to a
                // different address (a changed config abandons the report instead).
                string target = CurrentUploadTarget(out _);
                if (!string.IsNullOrWhiteSpace(target))
                    WriteOutbox(dir, new OutboxRecord
                    {
                        State = "pending", Target = target, Created = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                        Content = $"{MyPluginInfo.SHORT_NAME} bug report: {Role()} / session {Blank(MPLog.SessionId)} / {reason}",
                        ThreadName = DiscordThreadName(reason), Tags = CleanDiscordTagIds(discordTagIds),
                    });
                // User directive 2026-08-16 (only the latest report is kept whole) + H-REPORTLOSS-1
                // (undelivered reports are kept as their zip, at most 3). Runs AFTER this folder
                // exists, so the new report is the newest folder and is never touched.
                PruneOldReports(root, out _, out _, out _);

                // Batch 14: callers already prefix the reason ("manual bug report: " / "previous
                // crash: ", MPCanvasUI:5852) — re-prefixing here doubled it on every ring-dump
                // header in the field ("manual bug report: manual bug report: <text>", 10 bundles).
                string ring = MPLog.Dump(reason);
                WriteDescription(Path.Combine(dir, "description.txt"), reason);
                WriteReport(Path.Combine(dir, "report.md"), reason);
                CopyPlayerLogs(dir);
                WriteSaveStore(dir);   // bug-report v2 (task #40) + H-REPORTLOSS-1: newest save per connected player + full store listing
                CopyIfExists(ring, Path.Combine(dir, "bamp-ring.log"), MaxCopiedLogBytes);
                // P-MIDNIGHT: a crash report also carries the crashed session's own ring, kept on disk every ~10 s
                if (includeCrashArtifacts) CopyIfExists(StallWatch.PrevPath(), Path.Combine(dir, "bamp-ring-prev.log"), MaxCopiedLogBytes);
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

                if (!string.IsNullOrWhiteSpace(target))
                {
                    result.DiscordUploadQueued = true;
                    // A LATER successful retry reaches the caller too (the popup shows its existing
                    // success text if it is still open on this report).
                    if (onUploadComplete != null) _uploadWatchers[Path.GetFileName(dir)] = (dir, onUploadComplete);
                    lockHandedToTask = true;
                    Task.Run(() =>
                    {
                        bool ok = false;
                        try
                        {
                            // Bounded wait: completes EARLY when every peer's last file lands; the
                            // deadline is the failure path (peer offline/slow) and peer-logs.txt
                            // says so honestly. The upload then ships whatever arrived.
                            WaitForPeerLogs(peerGatherId);
                            ok = RunUploadAttempt(dir, lockHeld: true, AttemptKind.First);   // attempt 1, immediately
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[BugReport] upload task: {ex.Message}"); }
                        finally { try { folderLock?.Dispose(); } catch { } }
                        NotifyUploadWatcher(dir, ok, firstAttempt: true);
                    });
                }
                else if (peerGatherId != null)
                {
                    // No upload configured — still collect the peer logs into the local folder.
                    lockHandedToTask = true;
                    Task.Run(() => { try { WaitForPeerLogs(peerGatherId); } finally { try { folderLock?.Dispose(); } catch { } } });
                }
                // (otherwise nothing async touches this folder — the finally releases the lock now)

                Plugin.Logger.LogInfo($"[BugReport] Created report at {dir}");
                if (openFolder) TryOpenFolder(dir);
                return result;
            }
            finally { if (!lockHandedToTask) try { folderLock?.Dispose(); } catch { } }
        }

        /// <summary>User directive 2026-08-16 kept only the LAST report (folders carry saves + the
        /// zip). H-REPORTLOSS-1 (2026-09-23) keeps undelivered ones for retry: the NEWEST folder is
        /// always kept whole; a PENDING report that is not the newest is cut down to its zip +
        /// outbox.json + outbox.lock (the retry rebuilds from that zip); at most 3 pending reports
        /// are kept in total - past that the oldest pending one is deleted and logged; delivered
        /// (sent), given-up (abandoned) and pre-outbox folders that are not the newest are deleted
        /// as before. A folder whose outbox.lock cannot be opened is in use RIGHT NOW (an upload
        /// attempt or the peer-log wait, in this process or another) and is skipped - the lock is
        /// read live, never inferred from folder age (a timer here was called out and replaced
        /// 2026-08-16). Only exact-pattern folders are touched — the crash marker and anything a
        /// player parked in the root survive.</summary>
        private static void PruneOldReports(string root, out int pending, out long pendingBytes, out List<string> dropped)
        {
            pending = 0; pendingBytes = 0; dropped = new List<string>();
            int pruned = 0, cut = 0;
            try
            {
                var dirs = ReportDirsNewestFirst(root);
                for (int i = 0; i < dirs.Count; i++)
                {
                    string d = dirs[i], name = Path.GetFileName(d);
                    FileStream? lk = null;
                    try
                    {
                        bool inUse = i > 0 && !TryLockForPrune(d, out lk);
                        // L2: read AFTER the lock is ours (the newest / in-use folders are only counted, never touched).
                        bool isPending = ReadOutbox(d)?.State == "pending";
                        if (i == 0 || inUse)   // the newest folder is always kept; an in-use one is skipped
                        {
                            if (isPending) { pending++; pendingBytes += FolderBytes(d); }
                            continue;
                        }
                        if (isPending && pending < MaxPendingReports)
                        {
                            pending++;
                            if (CutToZip(d)) cut++;
                            pendingBytes += FolderBytes(d);
                            continue;
                        }
                        try { lk?.Dispose(); } catch { }
                        lk = null;
                        Directory.Delete(d, recursive: true);
                        if (isPending)
                        {
                            dropped.Add(name);
                            _uploadWatchers.TryRemove(name, out _);   // L9
                            Plugin.Logger.LogWarning($"[BugReport] Outbox: dropped pending report {name} (cap {MaxPendingReports} undelivered reports).");
                        }
                        else pruned++;
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[BugReport] prune '{name}': {ex.Message}"); }
                    finally { try { lk?.Dispose(); } catch { } }
                }
                if (pruned > 0 || cut > 0)
                    Plugin.Logger.LogInfo($"[BugReport] Pruned {pruned} old report folder(s); {pending} undelivered report(s) kept for retry ({cut} cut to their zip) — the newest report is always kept whole.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BugReport] report prune: {ex.Message}"); }
        }

        private static readonly System.Text.RegularExpressions.Regex _reportDirRx =
            new System.Text.RegularExpressions.Regex(@"^bamp-bug-\d{8}-\d{6}(-\d+)?$");   // L16: -2, -3 for same-second reports

        /// <summary>Every bamp-bug-yyyyMMdd-HHmmss folder in the root, newest first (the name IS
        /// the creation time, so an ordinal sort orders them).</summary>
        private static List<string> ReportDirsNewestFirst(string root)
        {
            var list = new List<string>();
            try
            {
                foreach (var d in Directory.GetDirectories(root))
                    if (_reportDirRx.IsMatch(Path.GetFileName(d))) list.Add(d);
            }
            catch { }
            list.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(b), Path.GetFileName(a)));
            return list;
        }

        /// <summary>Delete everything in a pending folder except its zip, outbox.json and
        /// outbox.lock. A folder that has no zip yet (the process died before attempt 1 built
        /// one) is left whole - cutting it would lose the report.</summary>
        private static bool CutToZip(string d)
        {
            string zip = Path.Combine(d, Path.GetFileName(d) + ".zip");
            if (!File.Exists(zip)) return false;
            bool any = false;
            foreach (var f in Directory.GetFiles(d))
            {
                string n = Path.GetFileName(f);
                if (n.Equals(Path.GetFileName(zip), StringComparison.OrdinalIgnoreCase) || n == OutboxFile || n == OutboxLockFile) continue;
                try { File.Delete(f); any = true; } catch { }
            }
            foreach (var sub in Directory.GetDirectories(d))
                try { Directory.Delete(sub, recursive: true); any = true; } catch { }
            return any;
        }

        private static long FolderBytes(string d)
        {
            long total = 0;
            try { foreach (var f in Directory.GetFiles(d, "*", SearchOption.AllDirectories)) try { total += new FileInfo(f).Length; } catch { } }
            catch { }
            return total;
        }

        // ── Outbox (H-REPORTLOSS-1) ─────────────────────────────────────────────────────────
        // Per report folder: outbox.json = {state pending|sent|abandoned, target (recorded at
        // creation), attempts[{utc, bytes, timeout, result, seconds}], nextAt}; outbox.lock is
        // held open (no sharing) for the length of an attempt - a cross-process guard, because
        // two game instances on one machine share this folder.
        private const string OutboxFile = "outbox.json";
        private const string OutboxLockFile = "outbox.lock";

        private sealed class OutboxAttempt
        {
            [JsonProperty("utc")] public string Utc = "";
            [JsonProperty("bytes")] public long Bytes;
            [JsonProperty("timeout")] public int Timeout;
            [JsonProperty("result")] public string Result = "";
            [JsonProperty("seconds")] public double Seconds;
        }

        private sealed class OutboxRecord
        {
            [JsonProperty("state")] public string State = "pending";
            [JsonProperty("target")] public string Target = "";
            [JsonProperty("created")] public string Created = "";
            [JsonProperty("attempts")] public List<OutboxAttempt> Attempts = new();
            [JsonProperty("nextAt")] public string? NextAt;
            [JsonProperty("reason")] public string Reason = "";
            // What the POST carries besides the zip - fixed at creation so a retry posts the same thread.
            [JsonProperty("content")] public string Content = "";
            [JsonProperty("threadName")] public string ThreadName = "";
            [JsonProperty("tags")] public string[] Tags = Array.Empty<string>();
        }

        private static OutboxRecord? ReadOutbox(string dir)
        {
            try
            {
                string p = Path.Combine(dir, OutboxFile);
                if (File.Exists(p))
                {
                    try { var ob = JsonConvert.DeserializeObject<OutboxRecord>(File.ReadAllText(p)); if (ob != null) return ob; } catch { }
                }
                // Re-check fold: a swap that failed after the old file was gone leaves the record only
                // under its .tmp name - read it there rather than treat the report as unmanaged.
                string t = p + ".tmp";
                if (File.Exists(t)) return JsonConvert.DeserializeObject<OutboxRecord>(File.ReadAllText(t));
                return null;
            }
            catch { return null; }
        }

        private static void WriteOutbox(string dir, OutboxRecord ob)
        {
            // L3: write a temp file, then swap it in, so a reader never sees a half-written outbox.json.
            try { ReplaceFile(Path.Combine(dir, OutboxFile + ".tmp"), Path.Combine(dir, OutboxFile), JsonConvert.SerializeObject(ob, Formatting.Indented)); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BugReport] outbox write '{Path.GetFileName(dir)}': {ex.Message}"); }
        }

        /// <summary>Put <paramref name="tmp"/> (written from <paramref name="text"/> when given) in
        /// place of <paramref name="target"/>: File.Replace when the target exists (atomic on the
        /// same volume), else a Move. The old file is never deleted before the new one is in place.</summary>
        private static void ReplaceFile(string tmp, string target, string? text = null)
        {
            if (text != null) File.WriteAllText(tmp, text);
            if (!File.Exists(target)) { File.Move(tmp, target); return; }
            try { File.Replace(tmp, target, null); }
            catch
            {
                // Re-check fold: File.Replace can fail AFTER removing the target (Windows error 1176,
                // e.g. antivirus holding the new file). Then the only copy is the .tmp - put it in place
                // instead of letting the caller delete it.
                if (!File.Exists(target) && File.Exists(tmp)) { File.Move(tmp, target); return; }
                throw;
            }
        }

        /// <summary>Open (creating it if needed) the folder's outbox.lock with NO sharing. Null =
        /// another holder has it (an attempt in flight here or in the other instance).</summary>
        private static FileStream? TryOpenOutboxLock(string dir)
        {
            try { return new FileStream(Path.Combine(dir, OutboxLockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch { return null; }
        }

        /// <summary>Prune side: a folder with no lock file (pre-outbox, or never uploaded) is free;
        /// one whose lock cannot be opened is in use and must be skipped.</summary>
        private static bool TryLockForPrune(string dir, out FileStream? lk)
        {
            lk = null;
            if (!File.Exists(Path.Combine(dir, OutboxLockFile))) return true;
            lk = TryOpenOutboxLock(dir);
            return lk != null;
        }

        private static string CurrentUploadTarget(out bool direct)
        {
            string directWebhook = MPConfig.BugReportDiscordWebhookUrlLive();
            direct = !string.IsNullOrWhiteSpace(directWebhook);
            return direct ? directWebhook : MPConfig.BugReportRelayUrlLive();
        }

        private enum AttemptKind { First, Due, Launch, StopCheckOnly }

        /// <summary>L4: may this pass try the report now? A timed retry needs nextAt reached. The
        /// launch pass tries every pending report once, EXCEPT one whose next attempt is still in
        /// the future (a Retry-After, or a failure moments ago) or whose last attempt is under 60 s
        /// old; a report whose attempt 1 is in flight is excluded by its lock.</summary>
        private static bool ReadyFor(OutboxRecord ob, bool launch)
        {
            if (!launch) return IsDue(ob);
            if (!string.IsNullOrEmpty(ob.NextAt) && !IsDue(ob)) return false;
            if (ob.Attempts.Count > 0
                && DateTime.TryParse(ob.Attempts[ob.Attempts.Count - 1].Utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)
                && (DateTime.UtcNow - last.ToUniversalTime()).TotalSeconds < 60) return false;
            return true;
        }

        private static bool IsDue(OutboxRecord ob)
            => !string.IsNullOrEmpty(ob.NextAt)
               && DateTime.TryParse(ob.NextAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
               && at.ToUniversalTime() <= DateTime.UtcNow;

        /// <summary>One upload at a time in this process - attempt 1 of a new report and the
        /// outbox retries share it.</summary>
        private static readonly SemaphoreSlim _uploadGate = new SemaphoreSlim(1, 1);
        private static int _outboxPassRunning;   // 0/1: one outbox pass at a time in this process

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Dir, Action<bool, string> Callback)> _uploadWatchers = new();

        /// <summary>Tell the report's creator (the popup) how its upload went: attempt 1's result
        /// always, and later only a SUCCESSFUL retry (a failed retry changes nothing it shows).</summary>
        private static void NotifyUploadWatcher(string dir, bool ok, bool firstAttempt)
        {
            try
            {
                string key = Path.GetFileName(dir);
                if (!_uploadWatchers.TryGetValue(key, out var w)) return;
                if (!firstAttempt && !ok) return;
                if (ok || ReadOutbox(dir)?.State != "pending") _uploadWatchers.TryRemove(key, out _);
                w.Callback(ok, w.Dir);
            }
            catch { }
        }

        /// <summary>Launch pass (Plugin init, beside MarkSessionStarted): enforce the pending cap,
        /// log what is waiting, then give every pending report ONE attempt (one at a time).</summary>
        public static void OutboxOnLaunch()
        {
            try
            {
                string root = SafeRoot();
                if (!Directory.Exists(root)) return;
                PruneOldReports(root, out int pending, out long bytes, out var dropped);
                Plugin.Logger.LogInfo($"[BugReport] Outbox: {pending} pending ({bytes / 1024} KB), dropped {(dropped.Count == 0 ? "none" : string.Join(", ", dropped))} (cap {MaxPendingReports})");
                if (pending > 0) StartOutboxPass(launch: true);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BugReport] outbox launch pass: {ex.Message}"); }
        }

        /// <summary>Recurring check from the ~30 s crash-marker heartbeat (MPCanvasUI): retries
        /// every pending report whose nextAt has come. The folder scan runs off the main thread.</summary>
        public static void OutboxTick()
        {
            try
            {
                if (Volatile.Read(ref _outboxPassRunning) != 0) return;
                StartOutboxPass(launch: false);
            }
            catch { }
        }

        private static void StartOutboxPass(bool launch)
        {
            if (Interlocked.CompareExchange(ref _outboxPassRunning, 1, 0) != 0) return;
            string root = SafeRoot();
            Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(root)) return;
                    var dirs = ReportDirsNewestFirst(root);
                    dirs.Reverse();   // oldest first
                    foreach (var d in dirs)
                    {
                        var ob = ReadOutbox(d);
                        if (ob == null || ob.State != "pending") continue;
                        if (!ReadyFor(ob, launch))
                        {
                            // Not due yet - but at launch the STOP rules still apply now (a report whose
                            // upload address changed, or that is too old, will never be sent: abandon it
                            // at once instead of carrying it as pending until its timer comes round).
                            if (launch) RunUploadAttempt(d, lockHeld: false, AttemptKind.StopCheckOnly);
                            continue;
                        }
                        bool ok = RunUploadAttempt(d, lockHeld: false, launch ? AttemptKind.Launch : AttemptKind.Due);
                        if (ok) NotifyUploadWatcher(d, true, firstAttempt: false);
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[BugReport] outbox pass: {ex.Message}"); }
                finally { Interlocked.Exchange(ref _outboxPassRunning, 0); }
            });
        }

#if BAMP_DEV
        /// <summary>TestDrive `bugbudget <KB>`: this process's upload budget (0 = the default).</summary>
        internal static string DevSetBudgetKB(long kb)
        {
            _devBudgetBytes = kb > 0 ? kb * 1024 : -1;
            return DevOutboxState();
        }

        /// <summary>TestDrive `bugbudget` (no argument): budget + outbox state + the size of the
        /// whole bug-reports folder, read-only.</summary>
        internal static string DevOutboxState()
        {
            int pending = 0; long total = 0; double minGap = -1;
            try
            {
                string root = SafeRoot();
                if (Directory.Exists(root))
                {
                    foreach (var d in ReportDirsNewestFirst(root))
                    {
                        var ob = ReadOutbox(d);
                        if (ob == null || ob.State != "pending") continue;
                        pending++;
                        // retryGapS: the smallest distance between a pending report's last attempt and its
                        // scheduled next one - a 429's Retry-After shows here as a gap of at least that long.
                        if (ob.Attempts.Count > 0 && !string.IsNullOrEmpty(ob.NextAt)
                            && DateTime.TryParse(ob.NextAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                            && DateTime.TryParse(ob.Attempts[ob.Attempts.Count - 1].Utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last))
                        {
                            double g = (at.ToUniversalTime() - last.ToUniversalTime()).TotalSeconds;
                            if (minGap < 0 || g < minGap) minGap = g;
                        }
                    }
                    total = FolderBytes(root);
                }
            }
            catch { }
            return $"budgetKB={CurrentBudgetBytes / 1024} pending={pending} retryGapS={(minGap < 0 ? -1 : (int)minGap)} reportsKB={total / 1024}";
        }
#endif

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
            sb.AppendLine($"Port: {MPConfig.Port} (join) · HostPort: {MPConfig.HostPort}{(MPServer.BoundPort > 0 && MPServer.BoundPort != MPConfig.HostPort ? $" (hosting on {MPServer.BoundPort})" : "")}");   // H-HOSTPORT-1; lobby review item 9: the hosting side compares with HostPort
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
            sb.AppendLine($"HostCarry: {(string.IsNullOrEmpty(MPSaveCoordinator.LastHostCarry) ? "none" : MPSaveCoordinator.LastHostCarry)}");   // RIVALS-HOST-SWITCH-1: the last host start's world carry (mode/from/counts)
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
            sb.AppendLine($"GameBuild: {Blank(MPContentFingerprint.GameBuildId)}");   // MACBUILD-1: "b<number>" — the id the join gate compares
            // MACBUILD-1: the module id is per-COMPILE and per-PLATFORM, so two reports showing the same GameBuild
            // and different GameModule are the same game build on different platforms (Mac vs Windows) — exactly the
            // pair the old module-id gate refused. Diagnostic only; nothing compares it.
            sb.AppendLine($"GameModule: {Blank(MPContentFingerprint.GameModuleId)}");
            // Round-102: GameVersion is coarse — two installs a month apart both report the same
            // string while carrying different item/business data (our own rig did exactly that,
            // and it read as a mod bug for four rounds). This fingerprint makes "these two players
            // are not running the same game content" visible at a glance across two reports.
            sb.AppendLine($"ContentFingerprint: {Blank(MPContentFingerprint.Cached)}");
            sb.AppendLine($"UnityVersion: {Blank(Application.unityVersion)}");
            sb.AppendLine($"Scene: {ActiveSceneName()}");
            sb.AppendLine($"GameRoot: {Blank(MPConfig.GameRootPath)}");
            sb.AppendLine($"InstalledMods: {Blank(ListInstalledMods())}");
            // H-MODSDIFFER-1 step 2 (2026-09-20): the FOLDER line above stays — a leftover or a
            // switched-off folder is still worth seeing when two reports are compared — but the
            // list the join-time comparison actually uses is this one: what the game LOADED.
            // Cached string, so this is safe wherever the report is assembled.
            sb.AppendLine($"LoadedMods: {Blank(MPContentFingerprint.CachedMods)}");
            sb.AppendLine($"PersistentDataPath: {Blank(Application.persistentDataPath)}");
            sb.AppendLine($"OS: {Environment.OSVersion}");
            sb.AppendLine($"64BitProcess: {Environment.Is64BitProcess}");
            sb.AppendLine();
            // A1 (H-STEAMNET-2 fold, 2026-09-26): every live Steam link (path, ping, rate, the controller's target,
            // learned ceiling, delivery, back-offs, peak, floorSec, ctl) and the last 8 closed links' close lines.
            // Steam ids are left as they are here; the upload's existing redaction still blanks IPs.
            sb.AppendLine("## Steam connections");
            try
            {
                var steamLines = SteamNetConfig.DescribeForReport();
                if (steamLines.Count == 0) sb.AppendLine("none");
                else foreach (var l in steamLines) sb.AppendLine("- " + l);
            }
            catch (Exception ex) { sb.AppendLine($"(read failed: {ex.GetType().Name}: {ex.Message})"); }
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
        internal static string ListInstalledMods()   // round-253 fed the join-time mod diff from here; since H-MODSDIFFER-1 step 2 that comes from MPContentFingerprint.ListLoadedMods() and this is only its pre-discovery FALLBACK
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
        // logs only imply, the save store STATES. Attach saves (loadable first-hand on the
        // rig) + a listing of EVERY copy in the lineage (size/time/day — where stale-copy
        // bugs are visible for ~1KB), and pull connected peers' logs so third-party reports
        // carry both halves.

        /// <summary>Write save-store.md (every lineage copy: player, day, size, mtime) and copy
        /// under saves/ - H-REPORTLOSS-1 (2026-09-23) - ONE .hsg of each player who matters to
        /// this report, chosen the way the load-time serve path chooses (the fenced, ranked
        /// eligible copy across the lineage - M1): the reporter's, and that of each CONNECTED player (host reporting: MPServer.ConnectedStableIds(); client
        /// reporting: the host via the manifest's IsHost slot, other clients only on an
        /// unambiguous DisplayName match with the lobby roster), each with its .meta sidecar and
        /// its session's manifest.bamp.json. The active-session-per-member set and the ANOMALY
        /// copies are gone: the ACTIVE session is the manual base, not the newest -auto-N
        /// (MPSaveCoordinator.cs:126), so it attached old saves, and saves were 76% of report
        /// bytes. The listing still shows every copy - stale/mismatched copies stay visible
        /// without uploading them. Local file IO only: the host holds the store natively and
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

                string savesDir = Path.Combine(dir, "saves");
                var rows = new List<(string Hsg, string Line)>();

                foreach (var s in MPSaveCoordinator.LineageSessions(session))
                {
                    string folder = "";
                    try { folder = MPSaveManager.MpSessionFolder(s); } catch { }
                    if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                    var manifest = MPSaveManager.ReadManifest(s);

                    foreach (var memberDir in Directory.GetDirectories(folder))
                    {
                        string stable = Path.GetFileName(memberDir);
                        if (!stable.StartsWith("guid-") && !stable.StartsWith("steam-")) continue;   // character folders only
                        string? hsg = NewestHsgIn(memberDir, out DateTime newest);
                        if (hsg == null) continue;
                        var slot = manifest?.Slots?.Find(x => x.StableId == stable);
                        int manifestDay = slot?.Day ?? -1;
                        // Day source is the game's own .hsg.meta sidecar — written by the SAME native
                        // Save() call as the .hsg, so it cannot lag the file it sits beside. The mod's
                        // manifest slot is the fallback only: field 150521 proved manifest days can run
                        // days behind the files (client '-auto' listed 24, the .hsg held 26).
                        int metaDay = MetaDay(hsg);
                        int day = metaDay >= 0 ? metaDay : manifestDay;
                        string who = !string.IsNullOrEmpty(slot?.DisplayName) ? slot!.DisplayName : stable;
                        long bytes = 0; try { bytes = new FileInfo(hsg).Length; } catch { }
                        // A manifest that disagrees with the sidecar is itself a defect worth
                        // seeing in every bundle — keep it visible instead of silently healing it.
                        string dayCell = day >= 0 ? day.ToString(CultureInfo.InvariantCulture) : "?";
                        if (metaDay >= 0 && manifestDay >= 0 && metaDay != manifestDay)
                            dayCell += $" (manifest says {manifestDay})";
                        rows.Add((hsg, $"| {s} | {who} | {dayCell} | {bytes:N0} | {newest:yyyy-MM-dd HH:mm:ss} |"));
                    }
                }

                var attached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var players = PlayersToAttach(session, out string whoNote);
                // M1: pick each player's copy exactly as the load-time SERVE path does (ResolveMemberSave):
                // the ranked eligible copy across the lineage, fenced by the loaded world's day and by the
                // rollback window - so after a load of an older slot (or a newer save-as fork of the same
                // playthrough) the report carries the timeline the player is actually on, never an
                // abandoned one. The listing above still shows every copy.
                int fenceDay = -1;
                try { fenceDay = MPSaveCoordinator.FenceDayFor(session); } catch { }
                foreach (var sid in players)
                {
                    var best = MPSaveCoordinator.LineageNewestEligible(session, sid, fenceDay, allowUnknownDay: true, out _);
                    if (best == null) continue;
                    string pickSession = best.Value.srcSession;
                    string? pickHsg = NewestHsgIn(best.Value.srcDir, out _);
                    if (pickHsg == null) continue;
                    string dest = Path.Combine(savesDir, pickSession, sid, Path.GetFileName(pickHsg));
                    if (!AttachSaveFile(pickHsg, dest)) continue;
                    attached.Add(pickHsg);
                    // Review F4: the day column is sidecar-dated — ship the sidecar beside its save
                    // so the column is auditable and the copy stays loadable through the native scanner.
                    try { string mp = pickHsg + ".meta"; if (File.Exists(mp)) AttachSaveFile(mp, dest + ".meta"); } catch { }
                    // The session's manifest rides along - without it the attached .hsg isn't loadable.
                    try
                    {
                        string mf = Path.Combine(MPSaveManager.MpSessionFolder(pickSession), "manifest.bamp.json");
                        string md = Path.Combine(savesDir, pickSession, "manifest.bamp.json");
                        if (File.Exists(mf) && !File.Exists(md)) AttachSaveFile(mf, md);
                    }
                    catch { }
                }
                foreach (var r in rows) sb.AppendLine(r.Line + (attached.Contains(r.Hsg) ? " yes |" : " - |"));
                sb.AppendLine();
                sb.AppendLine($"Attached: the newest save of {whoNote}. Rows without files are metadata only — stale/mismatched copies are visible without uploading them.");
                File.WriteAllText(Path.Combine(dir, "save-store.md"), sb.ToString());
                Plugin.Logger.LogInfo($"[BugReport] Save attach: {attached.Count} .hsg for {players.Count} player(s) ({whoNote}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BugReport] save store attach: {ex.Message}"); }
        }

        /// <summary>Stable ids whose newest save this report carries: the reporter, plus the
        /// players connected right now (see WriteSaveStore). A client cannot see stable ids of
        /// other clients, so it matches lobby names against the manifest's DisplayName and skips
        /// any name that more than one slot carries.</summary>
        private static List<string> PlayersToAttach(string session, out string note)
        {
            var set = new List<string>();
            note = "the reporter";
            try
            {
                string me = MPConfig.StableId ?? "";
                if (me.Length > 0) set.Add(me);
                if (MPServer.IsRunning)
                {
                    foreach (var sid in MPServer.ConnectedStableIds())
                        if (!string.IsNullOrEmpty(sid) && !set.Contains(sid)) set.Add(sid);
                    note = "the reporter (host) and every connected player";
                }
                else if (MPClient.IsConnected)
                {
                    var slots = MPSaveManager.ReadManifest(session)?.Slots ?? new List<MpSlot>();
                    var host = slots.Find(x => x.IsHost);
                    if (host != null && !string.IsNullOrEmpty(host.StableId) && !set.Contains(host.StableId)) set.Add(host.StableId);
                    var ambiguous = new List<string>();
                    foreach (var name in MPClient.LobbyPlayers)
                    {
                        var sids = slots.Where(x => string.Equals(x.DisplayName, name, StringComparison.Ordinal) && !string.IsNullOrEmpty(x.StableId))
                                        .Select(x => x.StableId).Distinct().ToList();
                        if (sids.Count == 1) { if (!set.Contains(sids[0])) set.Add(sids[0]); }
                        else if (sids.Count > 1) ambiguous.Add(name);
                    }
                    note = "the reporter (client), the host and name-matched connected players"
                         + (ambiguous.Count > 0 ? $" (skipped ambiguous: {string.Join(", ", ambiguous)})" : "");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BugReport] save attach players: {ex.Message}"); }
            return set;
        }

        /// <summary>Newest .hsg in one member folder (path + UTC write time).</summary>
        private static string? NewestHsgIn(string memberDir, out DateTime newestUtc)
        {
            newestUtc = DateTime.MinValue; string? best = null;
            try
            {
                foreach (var f in Directory.GetFiles(memberDir, "*.hsg"))
                {
                    DateTime w; try { w = File.GetLastWriteTimeUtc(f); } catch { continue; }
                    if (w > newestUtc) { newestUtc = w; best = f; }
                }
            }
            catch { }
            return best;
        }

        /// <summary>In-game day from the game's own .hsg.meta sidecar, -1 when absent or
        /// unreadable. Native Save() snapshots day = Current.Day in the SAME call that
        /// produces the .hsg (SaveGameManager.cs:230-242), so the DAY VALUE cannot lag the
        /// save the way the mod's manifest slot can. Review F13 caveat: the two FILES are
        /// not written atomically (.hsg compression is threaded, the sidecar is written
        /// synchronously right after) — never build file-time-based rules on this pair.
        /// The mirror pipeline ships the sidecar with every copy (round-275) and deletes
        /// leftovers when a payload has none (review F5).</summary>
        private static int MetaDay(string? hsgPath)
        {
            try
            {
                if (string.IsNullOrEmpty(hsgPath)) return -1;
                string mp = hsgPath + ".meta";
                if (!File.Exists(mp)) return -1;
                var tok = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(mp))["day"];
                return tok == null ? -1 : (int)tok;
            }
            catch { return -1; }
        }

        /// <summary>Copy one save file. Share-tolerant read (the .hsg may be mid-rotation); any
        /// failure = not attached, the listing row says so. H-REPORTLOSS-1: no size budget here
        /// any more - the upload planner puts saves LAST and leaves out whatever does not fit.</summary>
        private static bool AttachSaveFile(string source, string dest)
        {
            try
            {
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

        /// <summary>Opens a folder in the platform's file browser. Returns false when the launch threw
        /// (swallowed here so a missing explorer never breaks the caller; the caller may log it).</summary>
        internal static bool TryOpenFolder(string dir)
        {
            try
            {
                if (Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor)
                    Process.Start("explorer.exe", "\"" + dir + "\"");
                else
                    Application.OpenURL("file:///" + dir.Replace("\\", "/"));
                return true;
            }
            catch { return false; }
        }

        /// <summary>One upload attempt for one report folder (H-REPORTLOSS-1). Attempt 1 runs from
        /// Create (which already holds the folder lock); retries run from the outbox pass. Checks
        /// the stop rules right before sending, rebuilds a smaller zip on a retry, POSTs once,
        /// records the attempt in outbox.json and logs one line. True = delivered.</summary>
        private static bool RunUploadAttempt(string dir, bool lockHeld, AttemptKind kind)
        {
            FileStream? lk = null;
            bool gated = false;
            string name = Path.GetFileName(dir);
            try
            {
                if (!lockHeld)
                {
                    lk = TryOpenOutboxLock(dir);
                    if (lk == null) return false;   // another attempt (maybe the other instance's) has it
                }
                _uploadGate.Wait(); gated = true;
                // L4: re-read under the lock + gate — another process (or pass) may have moved it on.
                var ob = ReadOutbox(dir);
                if (ob == null || ob.State != "pending") return ob?.State == "sent";
                if ((kind == AttemptKind.Due || kind == AttemptKind.Launch) && !ReadyFor(ob, kind == AttemptKind.Launch)) return false;
                int k = ob.Attempts.Count + 1;

                string target = CurrentUploadTarget(out bool direct);
                string stop = "";
                if (ob.Attempts.Count >= MaxUploadAttempts) stop = $"{MaxUploadAttempts} attempts used";
                else if (DateTime.TryParse(ob.Created, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created)
                         && (DateTime.UtcNow - created.ToUniversalTime()).TotalDays > MaxOutboxAgeDays) stop = "older than 7 days";
                else if (!string.Equals(target, ob.Target, StringComparison.Ordinal)) stop = "the upload address changed since the report was made";
                else if (direct && !LooksLikeDiscordWebhook(target)) stop = "direct webhook URL is not a Discord webhook";
                if (stop.Length > 0)
                {
                    ob.State = "abandoned"; ob.NextAt = null; ob.Reason = stop;
                    WriteOutbox(dir, ob);
                    if (kind != AttemptKind.First) _uploadWatchers.TryRemove(name, out _);   // L9 (attempt 1's caller is told by NotifyUploadWatcher, which then drops it)
                    Plugin.Logger.LogWarning($"[BugReport] Outbox: {name} abandoned before attempt {k}/{MaxUploadAttempts} - {stop}.");
                    return false;
                }
                if (kind == AttemptKind.StopCheckOnly) return false;   // not due: the stop rules were all this pass had to apply

                // Built BEFORE the connection opens (task #40, user-directed 2026-08-16): a
                // refused/failed connection still leaves the REDACTED zip in the report folder —
                // the file an offline player should share manually (the loose files are raw by design).
                string zip = BuildUploadZip(dir, k, out var looseFiles);
                var r = PostReport(ob.Target, direct, zip, looseFiles, ob);

                ob.Attempts.Add(new OutboxAttempt
                {
                    Utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), Bytes = r.Bytes, Timeout = r.TimeoutS,
                    Result = r.Ok ? "OK " + r.StatusText : "FAIL " + r.StatusText + ": " + r.Message, Seconds = Math.Round(r.Ms / 1000.0, 1),
                });
                string next;
                if (r.Ok) { ob.State = "sent"; ob.NextAt = null; next = "none: sent"; }
                else if (r.Status == 400 || r.Status == 403) { ob.State = "abandoned"; ob.NextAt = null; ob.Reason = $"HTTP {r.Status} (final)"; next = $"none: HTTP {r.Status} is final"; }
                else if (k >= MaxUploadAttempts) { ob.State = "abandoned"; ob.NextAt = null; ob.Reason = $"{MaxUploadAttempts} attempts used"; next = $"none: {MaxUploadAttempts} attempts used"; }
                else if (k <= RetryGapMinutes.Length)
                {
                    DateTime at = DateTime.UtcNow.AddMinutes(RetryGapMinutes[k - 1]);
                    if (r.RetryAfterS > 0 && DateTime.UtcNow.AddSeconds(r.RetryAfterS) > at) at = DateTime.UtcNow.AddSeconds(r.RetryAfterS);   // 429: honour Retry-After
                    ob.NextAt = at.ToString("O", CultureInfo.InvariantCulture);
                    next = at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
                }
                else { ob.NextAt = null; next = "none: next game launch"; }
                WriteOutbox(dir, ob);
                if (ob.State == "abandoned" && kind != AttemptKind.First) _uploadWatchers.TryRemove(name, out _);   // L9 (attempt 1's caller is told by NotifyUploadWatcher, which then drops it)

                string head = $"[BugReport] upload attempt {k}/{MaxUploadAttempts} {name} bytes={r.Bytes} timeout={r.TimeoutS}s -> ";
                if (r.Ok) Plugin.Logger.LogInfo(head + $"OK {r.StatusText} in {r.Ms}ms; next={next}");
                else Plugin.Logger.LogWarning(head + $"FAIL {r.StatusText} after {r.Ms}ms: {r.Message} body={r.Body}; next={next}");
                return r.Ok;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[BugReport] upload attempt {name}: {ex.Message}");
                return false;
            }
            finally
            {
                if (gated) _uploadGate.Release();
                try { lk?.Dispose(); } catch { }
            }
        }

        private sealed class UploadOutcome
        {
            public bool Ok;
            public int Status;              // HTTP status, 0 when no response came back
            public string StatusText = "";  // the status number, or the WebExceptionStatus name
            public string Message = "";
            public string Body = "";        // first 300 chars of an error response
            public long Bytes;
            public int TimeoutS;
            public long Ms;
            public int RetryAfterS;
        }

        /// <summary>The single POST of one attempt. Timeout = 20 s + 1 s per 64 KB, clamped to
        /// 30..180 s (the old flat 15 s is what lost report 20260919-232955); ReadWriteTimeout
        /// 60 s per read/write; the body length is computed first and sent unbuffered, so a big
        /// report streams instead of being copied into memory.</summary>
        private static UploadOutcome PostReport(string url, bool direct, string zip, List<string> looseFiles, OutboxRecord ob)
        {
            var o = new UploadOutcome();
            var sw = Stopwatch.StartNew();
            try
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

                string boundary = "----BAMPBugReport" + Guid.NewGuid().ToString("N");
                var payloadObj = new Dictionary<string, object>
                {
                    ["content"] = ob.Content,
                    ["thread_name"] = ob.ThreadName
                };
                if (ob.Tags.Length > 0)
                    payloadObj["applied_tags"] = ob.Tags;
                var parts = new List<object>();   // byte[] (in memory) or string (a file streamed as-is)
                AddStringPart(parts, boundary, "payload_json", JsonConvert.SerializeObject(payloadObj), "application/json");
                // One zip per report (2026-07-08) — the redaction already happened inside the
                // bundle, and the file part streams .zip as binary. Loose files only as fallback.
                // M2: a POST with no file part would be recorded 'sent' while carrying nothing - refuse it.
                if (zip.Length == 0 && looseFiles.Count == 0)
                {
                    o.StatusText = "NoFile";
                    o.Message = "no zip and no loose files to send - POST refused, the report stays pending";
                    o.Ms = sw.ElapsedMilliseconds;
                    return o;
                }
                if (zip.Length > 0) AddFilePart(parts, boundary, "files[0]", zip);
                else for (int i = 0; i < looseFiles.Count; i++) AddFilePart(parts, boundary, "files[" + i + "]", looseFiles[i]);
                parts.Add(Ascii("--" + boundary + "--\r\n"));
                long total = 0;
                foreach (var p in parts) total += p is byte[] b ? b.Length : new FileInfo((string)p).Length;
                o.Bytes = total;
                o.TimeoutS = (int)Math.Min(180L, Math.Max(30L, 20L + total / 65536L));

                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.UserAgent = "BigAmbitionsMP";
                req.Timeout = o.TimeoutS * 1000;
                req.ReadWriteTimeout = 60000;
                req.ContentType = "multipart/form-data; boundary=" + boundary;
                req.ContentLength = total;
                req.AllowWriteStreamBuffering = false;
                req.AllowAutoRedirect = false;   // L5: a 3xx is a FAIL, never a silent re-POST elsewhere
                if (!direct)   // relay path — optional shared-key header (matches the Worker's RELAY_KEY)
                {
                    string relayKey = MPConfig.BugReportRelayKeyLive();
                    if (relayKey.Length > 0) req.Headers["X-BAMP-Key"] = relayKey;
                }

                using (var stream = req.GetRequestStream())
                    foreach (var p in parts)
                    {
                        if (p is byte[] b) WriteBytes(stream, b);
                        else using (var f = File.Open((string)p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) f.CopyTo(stream);
                    }

                using var resp = (HttpWebResponse)req.GetResponse();
                o.Status = (int)resp.StatusCode;
                o.StatusText = o.Status.ToString(CultureInfo.InvariantCulture);
                o.Ok = o.Status >= 200 && o.Status < 300;
                if (!o.Ok) { o.Message = "not a success status (redirects are not followed)"; o.Body = ResponseHead(resp, 300); }
            }
            catch (WebException wex)
            {
                o.Message = wex.Message;
                if (wex.Response is HttpWebResponse hr)
                {
                    o.Status = (int)hr.StatusCode;
                    o.StatusText = o.Status.ToString(CultureInfo.InvariantCulture);
                    try { int.TryParse(hr.Headers["Retry-After"], NumberStyles.Integer, CultureInfo.InvariantCulture, out o.RetryAfterS); } catch { }
                    o.Body = ResponseHead(hr, 300);
                    try { hr.Dispose(); } catch { }
                }
                else o.StatusText = wex.Status.ToString();
            }
            catch (Exception ex)
            {
                o.StatusText = ex.GetType().Name;
                o.Message = ex.Message;
            }
            o.Ms = sw.ElapsedMilliseconds;
            return o;
        }

        private static string ResponseHead(WebResponse resp, int max)
        {
            try
            {
                using var s = resp.GetResponseStream();
                if (s == null) return "";
                using var reader = new StreamReader(s);
                var buf = new char[max];
                int n = reader.ReadBlock(buf, 0, max);
                return new string(buf, 0, n).Replace('\r', ' ').Replace('\n', ' ');
            }
            catch { return ""; }
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

        /// <summary>Every file of a report folder that may go up - the bundle planner's candidate
        /// list (it orders them by priority and applies the budget; H-REPORTLOSS-1).</summary>
        private static IEnumerable<string> UploadFiles(string dir)
        {
            foreach (var name in new[] { "description.txt", "report.md", "save-store.md", "peer-logs.txt", "Player.log", "Player-prev.log", "bamp-ring.log", "bamp-ring-prev.log", "config-redacted.json" })
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

        // ── Bundle planner (H-REPORTLOSS-1) ─────────────────────────────────────────────────
        // Fill the budget in priority order, skipping an item that does not fit and moving on:
        //   1 the reporter's Player.log (already head+tail capped at 4 MB)
        //   2 small text files (description, report.md, save-store.md, peer-logs.txt, config,
        //     plus the generated bundle-index.txt and outbox.txt)
        //   3 peers' Player.logs   4 unity-crash files
        //   5 Player-prev logs (own + peers) and bamp-ring.log
        //   6 attachments (the per-file 24 MB skip at copy time stays)
        //   7 saves - LAST, the first thing to be cut
        // A RETRY sends less: attempt 2 drops the saves; attempt 3+ also drops attachments and
        // the Player-prev logs. The reporter's Player.log is never dropped.
        private sealed class BundleItem
        {
            public string Rel = "";        // zip entry name (Steam ids kept since batch 28 C; RedactEntryName is the seam)
            public int Priority;
            public int Order;
            public string? File;           // source on disk (raw; text is redacted when written)
            public string? ZipEntry;       // or: an entry of the previous zip (already redacted)
            public bool Text;
            public long RawBytes;
            public long PackedBytes;       // compressed size (estimate for text, raw size for binary)
            public bool In;
            public string Why = "";
        }

        private static int PriorityOf(string rel)
        {
            string r = rel.Replace('\\', '/');
            if (r.Equals("Player.log", StringComparison.OrdinalIgnoreCase)) return 1;
            if (r.StartsWith("saves/", StringComparison.OrdinalIgnoreCase)) return 7;
            if (r.StartsWith("attachments/", StringComparison.OrdinalIgnoreCase)) return 6;
            if (IsPrevLog(r) || r.Equals("bamp-ring.log", StringComparison.OrdinalIgnoreCase) || r.Equals("bamp-ring-prev.log", StringComparison.OrdinalIgnoreCase)) return 5;
            if (r.StartsWith("unity-crash/", StringComparison.OrdinalIgnoreCase)) return 4;
            if (r.StartsWith("peer/", StringComparison.OrdinalIgnoreCase)) return 3;
            return 2;
        }

        private static bool IsPrevLog(string rel) => Path.GetFileName(rel).IndexOf("Player-prev", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string RetryDropReason(BundleItem it, int attempt)
        {
            if (attempt >= 2 && it.Priority == 7) return "dropped on retry (attempt 2+ sends no saves)";
            if (attempt >= 3 && (it.Priority == 6 || IsPrevLog(it.Rel))) return "dropped on retry (attempt 3+ sends no attachments or Player-prev logs)";
            return "";
        }

        private static bool IsTextFile(string path)
        {
            string ext = Path.GetExtension(path);
            return ext.Equals(".log", StringComparison.OrdinalIgnoreCase) || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".md", StringComparison.OrdinalIgnoreCase) || ext.Equals(".json", StringComparison.OrdinalIgnoreCase);
        }

        private static byte[] RedactedBytes(string path) => Encoding.UTF8.GetBytes(RedactSensitive(File.ReadAllText(path)));

        /// <summary>Counts what a DeflateStream writes, so the planner learns a text file's packed
        /// size without holding the compressed bytes.</summary>
        private sealed class CountingStream : Stream
        {
            public long Count;
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => Count;
            public override long Position { get => Count; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => Count += count;
        }

        private static long PackedSize(byte[] data)
        {
            var cs = new CountingStream();
            using (var ds = new System.IO.Compression.DeflateStream(cs, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                ds.Write(data, 0, data.Length);
            return cs.Count;
        }

        /// <summary>The candidate list (from the report folder, or - for a folder already cut to
        /// its zip - from that zip's entries), sorted by priority and marked in/out.</summary>
        private static List<BundleItem> PlanBundle(string dir, int attempt, string? sourceZip)
        {
            var items = new List<BundleItem>();
            if (sourceZip != null)
            {
                using var za = new System.IO.Compression.ZipArchive(File.OpenRead(sourceZip), System.IO.Compression.ZipArchiveMode.Read);
                foreach (var e in za.Entries)
                {
                    if (e.FullName.EndsWith("/") || e.FullName == "bundle-index.txt" || e.FullName == "outbox.txt") continue;   // regenerated
                    items.Add(new BundleItem { Rel = e.FullName, ZipEntry = e.FullName, RawBytes = e.Length, PackedBytes = e.CompressedLength });
                }
            }
            else
                foreach (var f in UploadFiles(dir))
                {
                    string rel = f.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
                               ? f.Substring(dir.Length).TrimStart('\\', '/').Replace('\\', '/')
                               : Path.GetFileName(f);
                    bool text = IsTextFile(f);
                    long raw = new FileInfo(f).Length;
                    items.Add(new BundleItem
                    {
                        Rel = RedactEntryName(rel), File = f, Text = text, RawBytes = raw,
                        PackedBytes = text ? PackedSize(RedactedBytes(f)) : raw,   // saves/attachments are already compressed
                    });
                }
            for (int i = 0; i < items.Count; i++) { items[i].Priority = PriorityOf(items[i].Rel); items[i].Order = i; }
            items.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : a.Order.CompareTo(b.Order));

            long budget = CurrentBudgetBytes, used = BundleReserveBytes;
            foreach (var it in items)
            {
                string drop = RetryDropReason(it, attempt);
                if (drop.Length > 0) { it.Why = drop; continue; }
                if (it.Priority == 6) continue;   // M3: attachments are placed below, outside the budget
                long cost = it.PackedBytes + 256;   // + the entry's zip headers
                if (used + cost > budget)
                {
                    it.Why = $"over budget ({it.PackedBytes / 1024} KB packed, {Math.Max(0L, budget - used) / 1024} KB of {budget / 1024} KB left)";
                    continue;
                }
                used += cost;
                it.In = true;
            }
            // M3: the popup promises attachments (each up to 24 MB) go up, so they are EXEMPT from the
            // budget; only the relay's 25 MiB POST ceiling limits them. Smallest first, so whatever
            // does not fit is exactly the largest ones.
            long total = used;
            foreach (var it in items.Where(x => x.Priority == 6 && x.Why.Length == 0).OrderBy(x => x.PackedBytes))
            {
                long cost = it.PackedBytes + 256;
                if (total + cost > UploadCeilingBytes)
                {
                    it.Why = $"over the 25 MiB upload ceiling ({it.PackedBytes / 1024} KB; attachments are exempt from the {budget / 1024} KB budget, the largest are left out first)";
                    continue;
                }
                total += cost;
                it.In = true;
            }
            return items;
        }

        private static string BundleIndexText(string name, int attempt, List<BundleItem> plan)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Bundle index - {name}, upload attempt {attempt}/{MaxUploadAttempts}, budget {CurrentBudgetBytes / 1024} KB. Priority order: Player.log, small text, peers' logs, crash files, Player-prev + ring logs, attachments, saves.");
            sb.AppendLine("Included (bytes before compression):");
            foreach (var it in plan) if (it.In) sb.AppendLine($"  {it.RawBytes,12}  {it.Rel}");
            sb.AppendLine("Left out:");
            int n = 0;
            foreach (var it in plan) if (!it.In) { n++; sb.AppendLine($"  {it.RawBytes,12}  {it.Rel}  - {it.Why}"); }
            if (n == 0) sb.AppendLine("  none");
            return sb.ToString();
        }

        /// <summary>outbox.txt inside every zip: the reports on this machine still pending or given
        /// up, with their attempt history (never the upload address).</summary>
        private static string OutboxSummaryText(string root)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Undelivered bug reports on this machine at {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}Z (pending = still being retried, abandoned = given up):");
            int n = 0;
            try
            {
                foreach (var d in ReportDirsNewestFirst(root))
                {
                    var ob = ReadOutbox(d);
                    if (ob == null || (ob.State != "pending" && ob.State != "abandoned")) continue;
                    n++;
                    sb.AppendLine($"{Path.GetFileName(d)}  state={ob.State}  created={ob.Created}  next={(string.IsNullOrEmpty(ob.NextAt) ? "-" : ob.NextAt)}{(ob.Reason.Length > 0 ? "  reason=" + ob.Reason : "")}");
                    for (int i = 0; i < ob.Attempts.Count; i++)
                    {
                        var a = ob.Attempts[i];
                        sb.AppendLine($"    attempt {i + 1}: {a.Utc}  bytes={a.Bytes}  timeout={a.Timeout}s  {a.Result}  ({a.Seconds.ToString("0.0", CultureInfo.InvariantCulture)}s)");
                    }
                }
            }
            catch { }
            if (n == 0) sb.AppendLine("none");
            return sb.ToString();
        }

        /// <summary>Bundle the planned upload set into ONE zip (user directive 2026-07-08 — a single
        /// file per Discord post instead of a spray of attachments). Text files (.log/.txt/.md/.json)
        /// go through the same redaction as the loose-file upload — redaction must happen BEFORE
        /// compression, a zip entry can't be scrubbed in flight. The local report folder keeps the
        /// un-redacted originals. H-REPORTLOSS-1: the planner decides what goes in (bundle-index.txt
        /// in the zip lists every entry with its size and every left-out one with its reason); a
        /// folder already cut to its zip is rebuilt from that zip. Returns "" on failure, with
        /// <paramref name="loose"/> = the planned files in the same order (max 10) so a zip bug can
        /// never lose a report.</summary>
        private static string BuildUploadZip(string dir, int attempt, out List<string> loose)
        {
            loose = new List<string>();
            string name = Path.GetFileName(dir);
            string zipPath = Path.Combine(dir, name + ".zip");
            bool fromZip = !File.Exists(Path.Combine(dir, "description.txt")) && File.Exists(zipPath);
            List<BundleItem> plan;
            try { plan = PlanBundle(dir, attempt, fromZip ? zipPath : null); }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[BugReport] bundle plan failed ({ex.Message}){(fromZip ? " — resending the previous bundle." : " — falling back to loose files.")}");
                if (fromZip) return zipPath;
                AddLooseCapped(loose, UploadFiles(dir));
                return "";
            }

            string tmp = zipPath + ".tmp";
            try
            {
                string index = BundleIndexText(name, attempt, plan);
                string outboxTxt = OutboxSummaryText(Path.GetDirectoryName(dir) ?? dir);
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
                {
                    System.IO.Compression.ZipArchive? src = null;
                    try
                    {
                        if (fromZip) src = new System.IO.Compression.ZipArchive(File.OpenRead(zipPath), System.IO.Compression.ZipArchiveMode.Read);
                        foreach (var (entryName, body) in new[] { ("bundle-index.txt", index), ("outbox.txt", outboxTxt) })
                        {
                            var ge = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
                            using var gs = ge.Open();
                            WriteBytes(gs, Encoding.UTF8.GetBytes(RedactSensitive(body)));
                        }
                        foreach (var it in plan)
                        {
                            if (!it.In) continue;
                            var entry = zip.CreateEntry(it.Rel, System.IO.Compression.CompressionLevel.Optimal);
                            using var es = entry.Open();
                            if (it.ZipEntry != null)
                            {
                                var old = src?.GetEntry(it.ZipEntry);
                                if (old != null) using (var os = old.Open()) os.CopyTo(es);
                            }
                            else if (it.File != null && it.Text) WriteBytes(es, RedactedBytes(it.File));
                            else if (it.File != null)
                                using (var fsrc = File.Open(it.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) fsrc.CopyTo(es);
                        }
                    }
                    finally { src?.Dispose(); }
                }
                ReplaceFile(tmp, zipPath);   // M2: the previous zip is never deleted before the new one is in place

                int nIn = plan.Count(x => x.In), nOut = plan.Count - nIn;
                int hsgIn = plan.Count(x => x.In && x.Rel.EndsWith(".hsg", StringComparison.OrdinalIgnoreCase));
                int hsgOut = plan.Count(x => !x.In && x.Rel.EndsWith(".hsg", StringComparison.OrdinalIgnoreCase));
                bool logIn = plan.Any(x => x.In && x.Priority == 1);
                var outs = plan.Where(x => !x.In).Select(x => $"{x.Rel} ({x.Why})").ToList();
                string outList = outs.Count == 0 ? "" : "; left out: " + string.Join(", ", outs.Take(12)) + (outs.Count > 12 ? $" …+{outs.Count - 12} more" : "");
                Plugin.Logger.LogInfo($"[BugReport] Upload bundle: {Path.GetFileName(zipPath)} ({new FileInfo(zipPath).Length / 1024} KB), attempt {attempt}: {nIn} in, {nOut} left out, hsg in={hsgIn} out={hsgOut}, reporter Player.log {(logIn ? "included" : "LEFT OUT")}{outList}.");
                return zipPath;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                if (fromZip && File.Exists(zipPath))
                {
                    Plugin.Logger.LogWarning($"[BugReport] zip rebuild failed ({ex.Message}) — resending the previous bundle.");
                    return zipPath;
                }
                Plugin.Logger.LogWarning($"[BugReport] zip bundle failed ({ex.Message}) — falling back to loose files.");
                AddLooseCapped(loose, plan.Where(it => it.In && it.File != null).Select(it => it.File!));
                return "";
            }
        }

        /// <summary>Re-check fold: the loose-file fallback keeps the same two limits the zip path has -
        /// at most 10 files (Discord's per-message count) and the relay's ceiling in RAW bytes (loose
        /// files are not compressed), taken in priority order, skipping any that would not fit.</summary>
        private static void AddLooseCapped(List<string> loose, IEnumerable<string> files)
        {
            const long cap = 25L * 1024 * 1024 - 256 * 1024;
            long used = 0;
            foreach (var f in files)
            {
                if (loose.Count >= 10) break;
                long len = 0; try { len = new FileInfo(f).Length; } catch { continue; }
                if (used + len > cap) continue;
                loose.Add(f); used += len;
            }
        }

        private static void AddStringPart(List<object> parts, string boundary, string name, string value, string contentType)
        {
            parts.Add(Ascii("--" + boundary + "\r\n" + $"Content-Disposition: form-data; name=\"{name}\"\r\n" + $"Content-Type: {contentType}\r\n\r\n"));
            parts.Add(Encoding.UTF8.GetBytes(value));
            parts.Add(Ascii("\r\n"));
        }

        private static void AddFilePart(List<object> parts, string boundary, string name, string path)
        {
            parts.Add(Ascii("--" + boundary + "\r\n" + $"Content-Disposition: form-data; name=\"{name}\"; filename=\"{Path.GetFileName(path)}\"\r\n" + "Content-Type: application/octet-stream\r\n\r\n"));
            // .md and .json added 2026-08-26: this loose-file path is the FALLBACK used when the zip
            // build fails, and it was redacting only .log/.txt — so report.md, config-redacted.json and
            // every saves/**/*.json went up raw. The zip path already covers all four extensions, and
            // IP addresses (IPv4 and IPv6) must be stripped on BOTH upload paths, not just the zip one —
            // a fallback that quietly publishes more than the normal path is the worst shape.  Steam ids
            // and user folder names are KEPT on both paths (batch 28 C, user ruling 2026-09-21).
            // (Redact IP addresses from TEXT uploads so the host's public IP is never published to
            // Discord; the local report folder keeps the un-redacted originals. Maintainer decision 2026-06-16.)
            if (IsTextFile(path)) parts.Add(RedactedBytes(path));
            else parts.Add(path);   // binary (.zip / .hsg / images) streams as-is
            parts.Add(Ascii("\r\n"));
        }

        // Replace IPv4 addresses with a placeholder — hides the host's public IP from uploaded logs.
        // EFFORT BATCH 28 (C, user-approved 2026-09-21): this is now the ONLY redaction. Steam account
        // numbers and user folder names in paths are KEPT (they were blanked 2026-08-11 / 2026-08-26;
        // reversed - they carry diagnostic value, e.g. Proton / Steam Deck paths). A mod token
        // `mod:<name>@a.b.c.d` (MPContentFingerprint) is an assembly VERSION, not an address: the
        // lookbehind skips a dotted quad that directly follows `mod:<name>@`.
        // EFFORT BATCH 29 (R5): the exception is tightened to a real ASSEMBLY-NAME shape at a TOKEN START:
        // `mod:` at the start of the text or after whitespace / , ; | ( [ { " ' =, then a name that starts
        // with a letter or '_' and holds only letters, digits, '.', '_' or '-'.  `mod:10.0.0.1@<ip>` or
        // `xmod:a@<ip>` no longer shield an address.
        private static readonly System.Text.RegularExpressions.Regex _ipv4 =
            new System.Text.RegularExpressions.Regex(@"(?<!(?<![^\s,;|(\[{""'=])mod:[A-Za-z_][A-Za-z0-9_.\-]*@)\b(?:\d{1,3}\.){3}\d{1,3}\b", System.Text.RegularExpressions.RegexOptions.Compiled);

        // EFFORT BATCH 29 (R5): IPv6 addresses are blanked too (MPTransport logs full remote endpoints,
        // e.g. "[2001:db8::1]:7777").  Shapes: the full 8-group form, any '::'-compressed form, an
        // IPv4-mapped tail (::ffff:a.b.c.d, blanked whole) and a %zone suffix.  Guards against false hits:
        // not inside a word / after ':' '.' '%' (Steam "steam:<digits>", dotted names), at least one DIGIT
        // in the run (so "Add::Bed"-style C++ scopes stay), and no 8-group or '::' requirement is met by a
        // clock time ("12:34:56.789").  The port after "]:" is kept (only the address is sensitive).
        private static readonly System.Text.RegularExpressions.Regex _ipv6 =
            new System.Text.RegularExpressions.Regex(@"(?<![\w:.%])(?=[0-9A-Fa-f:]*\d)(?:(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}:){0,6}[0-9A-Fa-f]{0,4}::(?:[0-9A-Fa-f]{1,4}:){0,6}[0-9A-Fa-f]{0,4})(?:(?<=:)(?:\d{1,3}\.){3}\d{1,3})?(?:%[0-9A-Za-z]+)?(?![\w:]|\.\d)", System.Text.RegularExpressions.RegexOptions.Compiled);

        internal static string RedactSensitive(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = _ipv6.Replace(s, "[redacted-ip]");   // first: an IPv4-mapped IPv6 goes as ONE address
            return _ipv4.Replace(s, "[redacted-ip]");
        }

        /// <summary>Zip ENTRY NAMES: nothing to redact any more (Steam ids are kept, batch 28 C). Kept
        /// as the one seam the bundle builder calls, so a future rule has a single place to go.</summary>
        internal static string RedactEntryName(string rel) => rel;

        private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

        private static void WriteBytes(Stream stream, byte[] bytes) => stream.Write(bytes, 0, bytes.Length);
    }
}
