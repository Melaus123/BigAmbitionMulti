using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BigAmbitionsMP.Launcher;

internal sealed class ModManager : IDisposable
{
    private readonly LauncherSettings _settings;
    private readonly SteamLocator _steam;
    private readonly string[] _requiredFiles;
    private readonly string[] _recommendedFiles;

    public ModManager(LauncherSettings settings, SteamLocator steam)
    {
        _settings = settings;
        _steam = steam;
        _requiredFiles = NormalizeRelativePaths(settings.RequiredFiles);
        _recommendedFiles = NormalizeRelativePaths(settings.RecommendedFiles);

        ModDirectory = Path.Combine(
            KnownFolders.GetLocalAppDataLow(),
            settings.PublisherFolder,
            settings.GameDataFolder,
            settings.ModsFolderName,
            settings.ModFolderName);

        DataDirectory = Path.Combine(
            KnownFolders.GetLocalAppDataLow(),
            settings.PublisherFolder,
            settings.GameDataFolder,
            settings.ModFolderName);
    }

    public string ModDirectory { get; }

    /// <summary>The mod's runtime-data root (mod 0.1.11+): config, logs, and
    /// bug reports live OUTSIDE the mod folder so Steam Workshop revalidation
    /// can't clobber them and the in-game Workshop upload can't ship them.</summary>
    public string DataDirectory { get; }

    /// <summary>Prefer the 0.1.11+ data-root logs; fall back to the legacy
    /// in-mod-folder location for installs that haven't run 0.1.11 yet.</summary>
    public string LogsDirectory
    {
        get
        {
            string modern = Path.Combine(DataDirectory, "logs");
            return Directory.Exists(modern) ? modern : Path.Combine(ModDirectory, "logs");
        }
    }

    public ModStatus GetStatus()
    {
        bool installed = File.Exists(Path.Combine(ModDirectory, _settings.MainAssembly));
        var missingRequired = _requiredFiles
            .Where(file => !File.Exists(Path.Combine(ModDirectory, file)))
            .ToArray();
        var missingRecommended = _recommendedFiles
            .Where(file => !File.Exists(Path.Combine(ModDirectory, file)))
            .ToArray();

        return new ModStatus(
            Installed: installed,
            GameRunning: IsGameRunning(),
            InstalledVersion: installed ? ReadInstalledVersion() : "not installed",
            ModDirectory: ModDirectory,
            MissingRequiredFiles: missingRequired,
            MissingRecommendedFiles: missingRecommended);
    }

    public async Task InstallOrUpdateAsync(ReleaseInfo release, GitHubReleaseClient releases, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        if (!release.HasInstallableZip)
            throw new InvalidOperationException("The latest GitHub release does not contain an installable .zip asset.");
        if (IsGameRunning())
            throw new InvalidOperationException($"Close {_settings.GameDataFolder} before installing or updating the mod.");

        string tempRoot = Path.Combine(Path.GetTempPath(), $"{_settings.ModFolderName}-Manager-" + Guid.NewGuid().ToString("N"));
        string zipPath = Path.Combine(tempRoot, "release.zip");
        string extractRoot = Path.Combine(tempRoot, "extract");
        Directory.CreateDirectory(tempRoot);

        try
        {
            progress?.Report(new OperationProgress("Downloading release package...", 0));
            await releases.DownloadFileAsync(release.ZipDownloadUrl!, zipPath, progress, cancellationToken).ConfigureAwait(false);

            progress?.Report(new OperationProgress("Extracting package...", 60));
            ZipFile.ExtractToDirectory(zipPath, extractRoot, overwriteFiles: true);

            string payloadRoot = FindPayloadRoot(extractRoot);
            var packageFiles = ReadPackageFileSet(payloadRoot);
            ValidatePayload(payloadRoot, packageFiles.RequiredFiles);

            string? backupDir = null;
            if (Directory.Exists(ModDirectory) && Directory.EnumerateFileSystemEntries(ModDirectory).Any())
            {
                progress?.Report(new OperationProgress("Backing up current installation...", 70));
                backupDir = CreateBackup();
            }

            progress?.Report(new OperationProgress("Installing files...", 82));
            Directory.CreateDirectory(ModDirectory);
            CopyDirectory(payloadRoot, ModDirectory);
            // Copy-over never removes obsolete payload: pre-0.1.11 zips shipped the
            // icons at the mod-folder ROOT (they moved to assets\ for the Workshop
            // thumbnail auto-pick). Harmless to the mod, but clean them so updated
            // installs match fresh ones.
            TryDeleteFile(Path.Combine(ModDirectory, "BAMP_ChatIcon.png"));
            TryDeleteFile(Path.Combine(ModDirectory, "BAMP_HubIcon.png"));
            WriteInstallManifest(release, packageFiles);

            progress?.Report(new OperationProgress(backupDir == null
                ? "Installation complete."
                : $"Installation complete. Backup: {backupDir}", 100));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    public Task RepairAsync(ReleaseInfo release, GitHubReleaseClient releases, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
        => InstallOrUpdateAsync(release, releases, progress, cancellationToken);

    public Task UninstallAsync(IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            if (IsGameRunning())
                throw new InvalidOperationException($"Close {_settings.GameDataFolder} before uninstalling the mod.");

            progress?.Report(new OperationProgress("Removing mod files...", 20));
            var trackedFiles = _requiredFiles
                .Concat(_recommendedFiles)
                .Concat(_settings.KnownManifestNames)
                .Concat(new[] { _settings.InstallManifestName })
                .Select(NormalizeRelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var file in trackedFiles)
                TryDeleteFile(Path.Combine(ModDirectory, file));

            RemoveEmptyChildDirectories(ModDirectory);

            progress?.Report(new OperationProgress("Uninstall complete. Config, logs, and backups were preserved.", 100));
        }, cancellationToken);
    }

    public void OpenModFolder()
    {
        Directory.CreateDirectory(ModDirectory);
        OpenFolder(ModDirectory);
    }

    public void OpenLogsFolder()
    {
        Directory.CreateDirectory(LogsDirectory);
        OpenFolder(LogsDirectory);
    }

    public bool TryLaunchGame(out string message)
    {
        if (IsGameRunning())
        {
            message = $"{_settings.GameDataFolder} is already running.";
            return false;
        }

        string? exe = _steam.FindGameExecutable();
        if (!string.IsNullOrWhiteSpace(exe))
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            message = "Game launched.";
            return true;
        }

        message = $"Could not find {_settings.SteamExecutableName} through Steam libraries.";
        return false;
    }

    private string ReadInstalledVersion()
    {
        foreach (string manifest in _settings.KnownManifestNames.Prepend(_settings.InstallManifestName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string manifestVersion = ReadManifestVersion(Path.Combine(ModDirectory, NormalizeRelativePath(manifest)));
            if (!string.IsNullOrWhiteSpace(manifestVersion)) return manifestVersion;
        }

        string dll = Path.Combine(ModDirectory, _settings.MainAssembly);
        try
        {
            var info = FileVersionInfo.GetVersionInfo(dll);
            string? productVersion = info.ProductVersion;
            if (LooksLikeModVersion(productVersion))
                return productVersion!;
        }
        catch { }

        return ScanDllForVersion(dll);
    }

    private static string ReadManifestVersion(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("version", out var v)) return v.GetString() ?? "";
            if (root.TryGetProperty("Version", out var v2)) return v2.GetString() ?? "";
        }
        catch { }
        return "";
    }

    private static string ScanDllForVersion(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            string text = Encoding.Unicode.GetString(bytes);
            var matches = Regex.Matches(text, @"\bv?\d+\.\d+\.\d+(?:[-.][A-Za-z0-9]+)?\b")
                .Select(match => match.Value)
                .Where(LooksLikeModVersion)
                .ToArray();
            return matches.FirstOrDefault() ?? "unknown";
        }
        catch { return "unknown"; }
    }

    private static bool LooksLikeModVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        version = version.Trim();
        if (Regex.IsMatch(version, @"^1\.0\.0(?:\.0)?(?:\+|$)")) return false;
        return Regex.IsMatch(version, @"^v?\d+\.\d+\.\d+(?:[-.][A-Za-z0-9]+)?$");
    }

    private string FindPayloadRoot(string extractRoot)
    {
        string direct = Path.Combine(extractRoot, _settings.ModFolderName);
        if (File.Exists(Path.Combine(direct, _settings.MainAssembly))) return direct;
        if (File.Exists(Path.Combine(extractRoot, _settings.MainAssembly))) return extractRoot;

        string? dll = Directory.EnumerateFiles(extractRoot, _settings.MainAssembly, SearchOption.AllDirectories).FirstOrDefault();
        if (dll == null)
            throw new InvalidOperationException($"The release package does not contain {_settings.MainAssembly}.");
        return Path.GetDirectoryName(dll) ?? extractRoot;
    }

    private PackageFileSet ReadPackageFileSet(string payloadRoot)
    {
        foreach (string manifestName in _settings.KnownManifestNames.Prepend(_settings.InstallManifestName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = Path.Combine(payloadRoot, NormalizeRelativePath(manifestName));
            var fileSet = TryReadPackageFileSet(path);
            if (fileSet != null) return fileSet;
        }

        return new PackageFileSet(_requiredFiles, _recommendedFiles);
    }

    private PackageFileSet? TryReadPackageFileSet(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var required = ReadStringArray(root, "requiredFiles");
            var recommended = ReadStringArray(root, "recommendedFiles");
            if (required.Length == 0) return null;
            return new PackageFileSet(NormalizeRelativePaths(required), NormalizeRelativePaths(recommended));
        }
        catch
        {
            return null;
        }
    }

    private static string[] ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return property.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    private static void ValidatePayload(string payloadRoot, string[] requiredFiles)
    {
        var missing = requiredFiles
            .Where(file => !File.Exists(Path.Combine(payloadRoot, file)))
            .ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException("The release package is missing required files: " + string.Join(", ", missing));
    }

    private string CreateBackup()
    {
        string backupRoot = Path.Combine(ModDirectory, _settings.BackupFolderName);
        string backupDir = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(backupDir);

        foreach (var entry in Directory.EnumerateFileSystemEntries(ModDirectory))
        {
            string name = Path.GetFileName(entry);
            if (name.Equals(_settings.BackupFolderName, StringComparison.OrdinalIgnoreCase)) continue;
            string target = Path.Combine(backupDir, name);
            if (Directory.Exists(entry)) CopyDirectory(entry, target);
            else File.Copy(entry, target, overwrite: true);
        }
        return backupDir;
    }

    private void WriteInstallManifest(ReleaseInfo release, PackageFileSet packageFiles)
    {
        var manifest = new
        {
            version = release.Version,
            release = release.DisplayName,
            releasePage = release.PageUrl,
            installedAtUtc = DateTime.UtcNow,
            requiredFiles = packageFiles.RequiredFiles,
            recommendedFiles = packageFiles.RecommendedFiles,
        };
        string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(ModDirectory, _settings.InstallManifestName), json);
    }

    private bool IsGameRunning()
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.ProcessName.Equals(_settings.SteamProcessName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
            finally { process.Dispose(); }
        }
        return false;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void RemoveEmptyChildDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch { }
        }
    }

    private static string[] NormalizeRelativePaths(IEnumerable<string> paths)
        => paths.Select(NormalizeRelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string NormalizeRelativePath(string path)
        => (path ?? "").Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private static void OpenFolder(string path)
        => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    public void Dispose()
    {
    }

    private sealed record PackageFileSet(string[] RequiredFiles, string[] RecommendedFiles);
}
