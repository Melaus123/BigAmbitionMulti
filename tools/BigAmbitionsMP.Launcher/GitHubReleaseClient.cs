using System.Net.Http.Headers;
using System.Text.Json;

namespace BigAmbitionsMP.Launcher;

internal sealed class GitHubReleaseClient : IDisposable
{
    private readonly string _owner;
    private readonly string _repo;
    private readonly HttpClient _http;

    public GitHubReleaseClient(LauncherSettings settings)
    {
        _owner = settings.GitHubOwner;
        _repo = settings.GitHubRepository;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(settings.ModFolderName, "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<ReleaseInfo> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        var url = $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return await GetLatestTagAsync(cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;

        string tag = ReadString(root, "tag_name");
        string name = ReadString(root, "name");
        string html = ReadString(root, "html_url");

        string? assetName = null;
        string? assetUrl = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string candidateName = ReadString(asset, "name");
                if (!candidateName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (candidateName.Contains("-DEV", StringComparison.OrdinalIgnoreCase)) continue;
                assetName = candidateName;
                assetUrl = ReadString(asset, "browser_download_url");
                break;
            }
        }

        return new ReleaseInfo(
            Version: NormalizeVersion(tag),
            DisplayName: string.IsNullOrWhiteSpace(name) ? tag : name,
            PageUrl: html,
            ZipAssetName: assetName,
            ZipDownloadUrl: assetUrl);
    }

    private async Task<ReleaseInfo> GetLatestTagAsync(CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{_owner}/{_repo}/tags?per_page=100";
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var tags = json.RootElement.ValueKind == JsonValueKind.Array
            ? json.RootElement.EnumerateArray()
                .Select(tag => ReadString(tag, "name"))
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .OrderByDescending(SemanticVersionKey)
                .ThenByDescending(tag => tag, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();

        string latestTag = tags.FirstOrDefault() ?? "";
        if (string.IsNullOrWhiteSpace(latestTag))
            throw new InvalidOperationException("No GitHub release or version tag was found.");

        return new ReleaseInfo(
            Version: NormalizeVersion(latestTag),
            DisplayName: $"{latestTag} (tag only)",
            PageUrl: $"https://github.com/{_owner}/{_repo}/releases/tag/{latestTag}",
            ZipAssetName: null,
            ZipDownloadUrl: null);
    }

    public async Task DownloadFileAsync(string url, string destination, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = File.Create(destination);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            readTotal += read;
            if (total is > 0)
            {
                int percent = (int)Math.Clamp(readTotal * 100 / total.Value, 0, 100);
                progress?.Report(new OperationProgress("Downloading release package...", percent));
            }
        }
    }

    private static string ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    internal static string NormalizeVersion(string version)
    {
        version = (version ?? "").Trim();
        return version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version[1..] : version;
    }

    private static Version SemanticVersionKey(string tag)
    {
        string version = NormalizeVersion(tag);
        var match = System.Text.RegularExpressions.Regex.Match(version, @"^(\d+)\.(\d+)\.(\d+)");
        return match.Success
            ? new Version(
                int.Parse(match.Groups[1].Value),
                int.Parse(match.Groups[2].Value),
                int.Parse(match.Groups[3].Value))
            : new Version(0, 0, 0);
    }

    public void Dispose() => _http.Dispose();
}
