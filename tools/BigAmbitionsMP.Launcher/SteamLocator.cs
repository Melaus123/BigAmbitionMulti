using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace BigAmbitionsMP.Launcher;

internal sealed class SteamLocator
{
    private readonly LauncherSettings _settings;

    public SteamLocator(LauncherSettings settings)
    {
        _settings = settings;
    }

    public string? FindGameExecutable()
    {
        foreach (string library in EnumerateSteamLibraries())
        {
            string manifest = Path.Combine(library, "steamapps", $"appmanifest_{_settings.SteamAppId}.acf");
            if (!File.Exists(manifest)) continue;

            string installDir = ReadInstallDir(manifest);
            if (string.IsNullOrWhiteSpace(installDir)) continue;

            string exe = Path.Combine(library, "steamapps", "common", installDir, _settings.SteamExecutableName);
            if (File.Exists(exe)) return exe;
        }

        return null;
    }

    private IEnumerable<string> EnumerateSteamLibraries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string steamPath in EnumerateSteamRoots())
        {
            if (seen.Add(steamPath))
                yield return steamPath;

            string librariesFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            foreach (string library in ReadLibraries(librariesFile))
            {
                if (seen.Add(library))
                    yield return library;
            }
        }
    }

    private static IEnumerable<string> EnumerateSteamRoots()
    {
        foreach (var path in ReadSteamInstallPathsFromRegistry())
        {
            if (Directory.Exists(path))
                yield return path;
        }
    }

    private static IEnumerable<string> ReadSteamInstallPathsFromRegistry()
    {
        string[] keyNames =
        {
            @"Software\Valve\Steam",
            @"Software\WOW6432Node\Valve\Steam",
        };

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (string keyName in keyNames)
            {
                using var key = root.OpenSubKey(keyName);
                string? path = key?.GetValue("SteamPath") as string
                    ?? key?.GetValue("InstallPath") as string;
                if (!string.IsNullOrWhiteSpace(path))
                    yield return path.Replace('/', Path.DirectorySeparatorChar);
            }
        }
    }

    private static IEnumerable<string> ReadLibraries(string librariesFile)
    {
        if (!File.Exists(librariesFile))
            yield break;

        string text = File.ReadAllText(librariesFile);
        foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase))
        {
            string path = Regex.Unescape(match.Groups[1].Value).Replace(@"\\", @"\");
            if (Directory.Exists(path))
                yield return path;
        }
    }

    private static string ReadInstallDir(string manifest)
    {
        string text = File.ReadAllText(manifest);
        var match = Regex.Match(text, "\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return match.Success ? Regex.Unescape(match.Groups[1].Value) : "";
    }
}
