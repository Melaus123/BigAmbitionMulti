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
                File.WriteAllText(_markerPath, JsonConvert.SerializeObject(marker, Formatting.Indented));
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

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string dir = Path.Combine(root, "bamp-bug-" + stamp);
            Directory.CreateDirectory(dir);

            string ring = MPLog.Dump("manual bug report: " + reason);
            WriteDescription(Path.Combine(dir, "description.txt"), reason);
            WriteReport(Path.Combine(dir, "report.md"), reason);
            CopyPlayerLogs(dir);
            CopyIfExists(ring, Path.Combine(dir, "bamp-ring.log"), MaxCopiedLogBytes);
            // Task #5: the actual crash evidence lives OUTSIDE Player.log — but only CRASH reports
            // carry it (a stale Crash_* folder on an unrelated manual report is misleading noise).
            if (includeCrashArtifacts) CollectUnityCrashArtifacts(dir);
            CopyUserAttachments(dir, attachments);
            WriteRedactedConfig(Path.Combine(dir, "config-redacted.json"));
            WriteSubmitNotes(Path.Combine(dir, "README-submit.txt"));

            var result = new BugReportResult { DirectoryPath = dir };

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
                    bool ok = UploadReport(target, direct, dir, reason, tags);
                    try { onUploadComplete?.Invoke(ok, dir); } catch { }
                });
            }

            Plugin.Logger.LogInfo($"[BugReport] Created report at {dir}");
            if (openFolder) TryOpenFolder(dir);
            return result;
        }

        private static string SafeRoot()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(MPConfig.ModRootPath) && MPConfig.ModRootPath != ".")
                    return Path.Combine(MPConfig.ModRootPath, "bug-reports");
            }
            catch { }
            return Path.Combine(Path.GetTempPath(), "BigAmbitionsMP-bug-reports");
        }

        /// <summary>Task #5 (2026-07-08, Prabaha report): a hard crash writes its evidence to Unity's
        /// crash folder (%LOCALAPPDATA%\Temp\{Company}\{Product}\Crashes\Crash_*), NOT Player.log —
        /// the report we received proved our collection was blind to the actual death. Copy the newest
        /// crash folder (≤7 days old): error.log + its Player.log snapshot always; the minidump only
        /// when small enough to ride the upload.</summary>
        private const long MaxCrashDumpBytes = 8L * 1024L * 1024L;

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
                    bool isDump = f.Extension.Equals(".dmp", StringComparison.OrdinalIgnoreCase);
                    if (isDump && f.Length > MaxCrashDumpBytes) continue;
                    if (!isDump && f.Length > MaxCopiedLogBytes) continue;
                    try { File.Copy(f.FullName, Path.Combine(sub, "crash-" + f.Name), overwrite: true); copied++; } catch { }
                }
                Plugin.Logger.LogInfo($"[BugReport] Unity crash artifacts: '{newest.Name}' ({newest.LastWriteTime:g}) — {copied} file(s) attached.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BugReport] crash artifact collection: {ex.Message}"); }
        }

        private static void WriteReport(string path, string reason)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# BigAmbitionsMP Bug Report");
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
            sb.AppendLine($"PreviousCrashDetected: {PendingCrashDetected}");
            if (!string.IsNullOrWhiteSpace(PendingCrashHint))
                sb.AppendLine($"PreviousCrashHint: {PendingCrashHint}");
            sb.AppendLine();
            sb.AppendLine("## Runtime");
            sb.AppendLine($"GameVersion: {Blank(Application.version)}");
            sb.AppendLine($"UnityVersion: {Blank(Application.unityVersion)}");
            sb.AppendLine($"Scene: {ActiveSceneName()}");
            sb.AppendLine($"GameRoot: {Blank(MPConfig.GameRootPath)}");
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

        private static void CopyPlayerLogs(string dir)
        {
            try
            {
                string baseDir = Application.persistentDataPath;
                CopyIfExists(Path.Combine(baseDir, "Player.log"), Path.Combine(dir, "Player.log"), MaxCopiedLogBytes);
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

                using var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                input.Seek(-maxBytes, SeekOrigin.End);
                using var output = File.Create(dest);
                var header = Encoding.UTF8.GetBytes($"# Log truncated to last {maxBytes} bytes from {source}\r\n");
                output.Write(header, 0, header.Length);
                input.CopyTo(output);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BugReport] Copy '{source}': {ex.Message}"); }
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
                    string content = $"BigAmbitionsMP bug report: {Role()} / session {Blank(MPLog.SessionId)} / {reason}";
                    var payloadObj = new Dictionary<string, object>
                    {
                        ["content"] = content,
                        ["thread_name"] = DiscordThreadName(reason)
                    };
                    if (discordTagIds.Length > 0)
                        payloadObj["applied_tags"] = discordTagIds;
                    var payload = JsonConvert.SerializeObject(payloadObj);
                    WriteStringPart(stream, boundary, "payload_json", payload, "application/json");

                    int index = 0;
                    foreach (var file in UploadFiles(dir))
                    {
                        WriteFilePart(stream, boundary, "files[" + index + "]", file);
                        index++;
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
            return name.Length == 0 ? "BAMP bug report" : name;
        }

        private static IEnumerable<string> UploadFiles(string dir)
        {
            foreach (var name in new[] { "description.txt", "Player.log", "Player-prev.log", "bamp-ring.log" })
            {
                string path = Path.Combine(dir, name);
                if (File.Exists(path)) yield return path;
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
                WriteBytes(stream, Encoding.UTF8.GetBytes(RedactIps(File.ReadAllText(path))));
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
        private static string RedactIps(string s) => string.IsNullOrEmpty(s) ? s : _ipv4.Replace(s, "[redacted-ip]");

        private static void WriteAscii(Stream stream, string value) => WriteBytes(stream, Encoding.ASCII.GetBytes(value));

        private static void WriteBytes(Stream stream, byte[] bytes) => stream.Write(bytes, 0, bytes.Length);
    }
}
