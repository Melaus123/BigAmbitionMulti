namespace BigAmbitionsMP.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        string settingsPath = Path.Combine(AppContext.BaseDirectory, "launcher-settings.json");
        var settings = LauncherSettings.Load(settingsPath);
        var steam = new SteamLocator(settings);

        using var manager = new ModManager(settings, steam);
        using var releases = new GitHubReleaseClient(settings);
        var secrets = LauncherSecrets.Load(settings);
        var bugReporter = new LauncherBugReporter(settings, secrets, manager);
        Application.Run(new MainForm(settings, manager, releases, bugReporter));
    }
}
