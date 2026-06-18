using System.Text.Json;

namespace BigAmbitionsMP.Launcher;

internal sealed record LauncherSettings
{
    public string AppTitle { get; init; } = "";
    public string Disclaimer { get; init; } = "";
    public string GitHubOwner { get; init; } = "";
    public string GitHubRepository { get; init; } = "";
    public string SteamAppId { get; init; } = "";
    public string SteamProcessName { get; init; } = "";
    public string SteamExecutableName { get; init; } = "";
    public string PublisherFolder { get; init; } = "";
    public string GameDataFolder { get; init; } = "";
    public string ModsFolderName { get; init; } = "";
    public string ModFolderName { get; init; } = "";
    public string MainAssembly { get; init; } = "";
    public string InstallManifestName { get; init; } = "";
    public string BackupFolderName { get; init; } = "";
    public string LauncherSecretsFileName { get; init; } = "";
    public string LauncherWebhookEnvironmentVariable { get; init; } = "";
    public string LauncherBackgroundImage { get; init; } = "";
    public string LauncherBugReportIcon { get; init; } = "";
    public string LauncherBugReportTagLabel { get; init; } = "";
    public string LauncherBugReportTagId { get; init; } = "";
    public string[] KnownManifestNames { get; init; } = Array.Empty<string>();
    public string[] RequiredFiles { get; init; } = Array.Empty<string>();
    public string[] RecommendedFiles { get; init; } = Array.Empty<string>();

    public static LauncherSettings Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Launcher settings file was not found.", path);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        var settings = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path), options)
            ?? throw new InvalidOperationException("Launcher settings file is empty.");
        settings.Validate();
        return settings;
    }

    private void Validate()
    {
        Require(AppTitle, nameof(AppTitle));
        Require(GitHubOwner, nameof(GitHubOwner));
        Require(GitHubRepository, nameof(GitHubRepository));
        Require(SteamAppId, nameof(SteamAppId));
        Require(SteamProcessName, nameof(SteamProcessName));
        Require(SteamExecutableName, nameof(SteamExecutableName));
        Require(PublisherFolder, nameof(PublisherFolder));
        Require(GameDataFolder, nameof(GameDataFolder));
        Require(ModsFolderName, nameof(ModsFolderName));
        Require(ModFolderName, nameof(ModFolderName));
        Require(MainAssembly, nameof(MainAssembly));
        Require(InstallManifestName, nameof(InstallManifestName));
        Require(BackupFolderName, nameof(BackupFolderName));
        Require(LauncherSecretsFileName, nameof(LauncherSecretsFileName));
        Require(LauncherWebhookEnvironmentVariable, nameof(LauncherWebhookEnvironmentVariable));
        Require(LauncherBackgroundImage, nameof(LauncherBackgroundImage));
        Require(LauncherBugReportIcon, nameof(LauncherBugReportIcon));
        Require(LauncherBugReportTagLabel, nameof(LauncherBugReportTagLabel));
        Require(LauncherBugReportTagId, nameof(LauncherBugReportTagId));

        if (RequiredFiles.Length == 0)
            throw new InvalidOperationException("Launcher settings must define at least one required file.");
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Launcher settings value '{name}' is required.");
    }
}
