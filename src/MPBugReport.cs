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
                        Plugin.Logger.LogWarning("[BugReport] Previous session did not close cleanly; crash report popup will be shown.");
                    }
                }

                WriteOpenMarker("normal start", false);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[BugReport] Session marker start failed: {ex.Message}");
            }
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

        public static BugReportResult Create(string reason, bool openFolder = true, IEnumerable<string>? attachments = null, IEnumerable<string>? discordTagIds = null)
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
            CopyUserAttachments(dir, attachments);
            WriteRedactedConfig(Path.Combine(dir, "config-redacted.json"));
            WriteSubmitNotes(Path.Combine(dir, "README-submit.txt"));

            var result = new BugReportResult { DirectoryPath = dir };
            string webhook = MPConfig.BugReportDiscordWebhookUrlLive();
            if (!string.IsNullOrWhiteSpace(webhook))
            {
                result.DiscordUploadQueued = true;
                string[] tags = CleanDiscordTagIds(discordTagIds);
                Task.Run(() => UploadToDiscord(webhook, dir, reason, tags));
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

        private static void WriteOpenMarker(string reason, bool crashTest)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_markerPath))
                    _markerPath = Path.Combine(SafeRoot(), "session-open.json");
                Directory.CreateDirectory(Path.GetDirectoryName(_markerPath) ?? SafeRoot());
                var marker = new Dictionary<string, string>
                {
                    ["State"] = "open",
                    ["Started"] = DateTime.Now.ToString("O"),
                    ["Reason"] = reason,
                    ["CrashTest"] = crashTest ? "true" : "false",
                    ["ModVersion"] = MyPluginInfo.PLUGIN_VERSION,
                    ["BuildTag"] = MyPluginInfo.BuildTag,
                    ["Role"] = Role(),
                    ["SessionId"] = MPLog.SessionId ?? "",
                    ["PlayerId"] = MPConfig.PlayerId ?? "",
                    ["StableIdKind"] = StableIdKind()
                };
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

        private static void UploadToDiscord(string webhookUrl, string dir, string reason, string[] discordTagIds)
        {
            try
            {
                if (!LooksLikeDiscordWebhook(webhookUrl))
                {
                    Plugin.Logger.LogWarning("[BugReport] Discord webhook URL is not a Discord webhook; upload skipped.");
                    return;
                }

                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                string boundary = "----BAMPBugReport" + Guid.NewGuid().ToString("N");
                var req = (HttpWebRequest)WebRequest.Create(webhookUrl);
                req.Method = "POST";
                req.UserAgent = "BigAmbitionsMP";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                req.ContentType = "multipart/form-data; boundary=" + boundary;

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
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[BugReport] Discord upload failed: {DiscordError(ex)}");
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
            string role = Role();
            string session = string.IsNullOrWhiteSpace(MPLog.SessionId) ? "nosession" : MPLog.SessionId;
            string raw = $"BAMP {DateTime.Now:yyyy-MM-dd HH-mm-ss} {role} {session}";
            if (!string.IsNullOrWhiteSpace(reason))
                raw += " " + reason.Trim();

            var sb = new StringBuilder();
            foreach (char c in raw)
            {
                if (char.IsControl(c)) continue;
                if (c == '`' || c == '@' || c == '#' || c == ':') sb.Append('-');
                else sb.Append(c);
            }

            string name = sb.ToString().Trim();
            if (name.Length > 100) name = name.Substring(0, 100).Trim();
            return string.IsNullOrWhiteSpace(name) ? "BAMP bug report" : name;
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
