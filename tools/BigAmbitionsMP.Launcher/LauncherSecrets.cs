using System.Text.Json;

namespace BigAmbitionsMP.Launcher;

internal sealed record LauncherSecrets
{
    public string DiscordWebhookUrl { get; init; } = "";

    public static LauncherSecrets Load(LauncherSettings settings)
    {
        string envWebhook = Environment.GetEnvironmentVariable(settings.LauncherWebhookEnvironmentVariable) ?? "";
        if (!string.IsNullOrWhiteSpace(envWebhook))
            return new LauncherSecrets { DiscordWebhookUrl = envWebhook.Trim() };

        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, settings.LauncherSecretsFileName),
            Path.Combine(AppContext.BaseDirectory, "config", settings.LauncherSecretsFileName),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, settings.LauncherSecretsFileName),
        };

        foreach (string path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(path)) continue;
                var secrets = JsonSerializer.Deserialize<LauncherSecrets>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true });
                if (!string.IsNullOrWhiteSpace(secrets?.DiscordWebhookUrl))
                    return secrets;
            }
            catch { }
        }

        return new LauncherSecrets();
    }
}
