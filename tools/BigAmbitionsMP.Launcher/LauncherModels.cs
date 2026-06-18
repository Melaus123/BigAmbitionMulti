namespace BigAmbitionsMP.Launcher;

internal sealed record ModStatus(
    bool Installed,
    bool GameRunning,
    string InstalledVersion,
    string ModDirectory,
    IReadOnlyList<string> MissingRequiredFiles,
    IReadOnlyList<string> MissingRecommendedFiles)
{
    public bool Healthy => Installed && MissingRequiredFiles.Count == 0;
}

internal sealed record ReleaseInfo(
    string Version,
    string DisplayName,
    string PageUrl,
    string? ZipAssetName,
    string? ZipDownloadUrl)
{
    public bool HasInstallableZip => !string.IsNullOrWhiteSpace(ZipDownloadUrl);
}

internal sealed record OperationProgress(string Message, int Percent = -1);
