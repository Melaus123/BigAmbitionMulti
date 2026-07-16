using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace BigAmbitionsMP.Launcher;

internal sealed class LauncherBugReporter
{
    private const long MaxAttachmentBytes = 24L * 1024L * 1024L;

    private readonly LauncherSettings _settings;
    private readonly LauncherSecrets _secrets;
    private readonly ModManager _manager;

    public LauncherBugReporter(LauncherSettings settings, LauncherSecrets secrets, ModManager manager)
    {
        _settings = settings;
        _secrets = secrets;
        _manager = manager;
    }

    public bool HasDiscordWebhook => LooksLikeDiscordWebhook(_secrets.DiscordWebhookUrl);

    public async Task<LauncherBugReportResult> CreateAndSendAsync(
        string description,
        IEnumerable<string> attachments,
        string launcherLog,
        ModStatus? status,
        ReleaseInfo? latest,
        CancellationToken cancellationToken)
    {
        description = string.IsNullOrWhiteSpace(description) ? "launcher bug report" : description.Trim();
        string dir = CreateReportDirectory();
        WriteReportFiles(dir, description, launcherLog, status, latest);
        CopyAttachments(dir, attachments);

        var result = new LauncherBugReportResult(dir);
        if (!HasDiscordWebhook)
            return result;

        await UploadToDiscordAsync(dir, description, cancellationToken).ConfigureAwait(false);
        return result with { DiscordUploaded = true };
    }

    private string CreateReportDirectory()
    {
        // Data root, not the mod folder (0.1.11 policy: the mod folder is static
        // content only — it is also the Workshop upload source).
        string root = Path.Combine(_manager.DataDirectory, "launcher-bug-reports");
        Directory.CreateDirectory(root);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string dir = Path.Combine(root, "bamp-launcher-" + stamp);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void WriteReportFiles(string dir, string description, string launcherLog, ModStatus? status, ReleaseInfo? latest)
    {
        File.WriteAllText(Path.Combine(dir, "description.txt"), description);
        File.WriteAllText(Path.Combine(dir, "launcher-log.txt"), string.IsNullOrWhiteSpace(launcherLog) ? "(empty)" : launcherLog);

        var sb = new StringBuilder();
        sb.AppendLine("# BAMP Manager Bug Report");
        sb.AppendLine();
        sb.AppendLine($"Created: {DateTime.Now:O}");
        sb.AppendLine($"Launcher: {_settings.AppTitle}");
        sb.AppendLine($"LauncherAssembly: {Assembly.GetExecutingAssembly().GetName().Version}");
        sb.AppendLine($"OS: {Environment.OSVersion}");
        sb.AppendLine($"64BitProcess: {Environment.Is64BitProcess}");
        sb.AppendLine($"ModDirectory: {_manager.ModDirectory}");
        sb.AppendLine($"Installed: {status?.Installed}");
        sb.AppendLine($"InstalledVersion: {status?.InstalledVersion ?? "unknown"}");
        sb.AppendLine($"GameRunning: {status?.GameRunning}");
        sb.AppendLine($"MissingRequiredFiles: {string.Join(", ", status?.MissingRequiredFiles ?? Array.Empty<string>())}");
        sb.AppendLine($"MissingRecommendedFiles: {string.Join(", ", status?.MissingRecommendedFiles ?? Array.Empty<string>())}");
        sb.AppendLine($"LatestVersion: {latest?.DisplayName ?? "not checked"}");
        sb.AppendLine($"LatestHasPackage: {latest?.HasInstallableZip}");
        sb.AppendLine();
        sb.AppendLine("## Player Description");
        sb.AppendLine(description);
        File.WriteAllText(Path.Combine(dir, "report.md"), sb.ToString());

        var redactedSettings = new
        {
            _settings.AppTitle,
            _settings.GitHubOwner,
            _settings.GitHubRepository,
            _settings.SteamAppId,
            _settings.ModFolderName,
            _settings.MainAssembly,
            Webhook = HasDiscordWebhook ? "<configured>" : "",
            Tag = _settings.LauncherBugReportTagLabel,
            TagId = _settings.LauncherBugReportTagId,
        };
        File.WriteAllText(
            Path.Combine(dir, "settings-redacted.json"),
            JsonSerializer.Serialize(redactedSettings, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void CopyAttachments(string dir, IEnumerable<string> attachments)
    {
        string attachDir = Path.Combine(dir, "attachments");
        var skipped = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in attachments)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(raw) || !File.Exists(raw)) continue;
                var file = new FileInfo(raw);
                if (file.Length > MaxAttachmentBytes)
                {
                    skipped.Add($"{file.Name} is over 24 MB.");
                    continue;
                }

                Directory.CreateDirectory(attachDir);
                string name = UniqueSafeName(file.Name, usedNames);
                File.Copy(file.FullName, Path.Combine(attachDir, name), overwrite: true);
            }
            catch (Exception ex)
            {
                skipped.Add($"{Path.GetFileName(raw)}: {ex.Message}");
            }
        }

        if (skipped.Count > 0)
        {
            Directory.CreateDirectory(attachDir);
            File.WriteAllLines(Path.Combine(attachDir, "skipped-attachments.txt"), skipped);
        }
    }

    private async Task UploadToDiscordAsync(string dir, string description, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var form = new MultipartFormDataContent();

        var payload = new Dictionary<string, object?>
        {
            ["content"] = "BAMP Manager bug report: " + Shorten(description, 260),
            ["thread_name"] = DiscordThreadName(description),
            ["applied_tags"] = new[] { _settings.LauncherBugReportTagId },
        };
        form.Add(new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), "payload_json");

        int index = 0;
        foreach (string file in UploadFiles(dir))
        {
            var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, $"files[{index}]", Path.GetFileName(file));
            index++;
        }

        using var response = await http.PostAsync(_secrets.DiscordWebhookUrl, form, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Discord upload failed: {(int)response.StatusCode} {response.ReasonPhrase} {body}");
    }

    private static IEnumerable<string> UploadFiles(string dir)
    {
        foreach (string name in new[] { "report.md", "description.txt", "launcher-log.txt", "settings-redacted.json" })
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path)) yield return path;
        }

        string attachDir = Path.Combine(dir, "attachments");
        if (!Directory.Exists(attachDir)) yield break;
        foreach (string file in Directory.EnumerateFiles(attachDir).Take(6))
            yield return file;
    }

    private static bool LooksLikeDiscordWebhook(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && (uri.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase)
                   || uri.Host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase))
               && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase);
    }

    private static string DiscordThreadName(string description)
    {
        string name = Shorten(description, 70).Trim();
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, ' ');
        return string.IsNullOrWhiteSpace(name) ? "BAMP Manager bug report" : name;
    }

    private static string Shorten(string value, int max)
    {
        value = (value ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
        while (value.Contains("  ", StringComparison.Ordinal)) value = value.Replace("  ", " ");
        return value.Length <= max ? value : value[..max].TrimEnd() + "...";
    }

    private static string UniqueSafeName(string name, HashSet<string> usedNames)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(name)) name = "attachment.bin";

        string candidate = name;
        int index = 2;
        while (!usedNames.Add(candidate))
        {
            candidate = Path.GetFileNameWithoutExtension(name) + "-" + index + Path.GetExtension(name);
            index++;
        }
        return candidate;
    }
}

internal sealed record LauncherBugReportResult(string DirectoryPath)
{
    public bool DiscordUploaded { get; init; }
}
