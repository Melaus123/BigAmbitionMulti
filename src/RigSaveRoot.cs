#if BAMP_DEV
using System;
using System.IO;
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>EFFORT RIG-SAVEROOT (2026-09-26) - DEV BUILDS ONLY. Keeps a RIG instance's ENTIRE save root out
    /// of Steam Cloud.
    ///
    /// WHY: rig instances are launched outside Steam (local\launch-mp-test.bat) and used to write into
    /// &lt;LocalLow&gt;\Hovgaard Games\Big Ambitions\SaveGames - the one folder Steam Cloud syncs for this game
    /// (every entry of Steam's remotecache.vdf for app 1331550 sits under that path). After a run Steam found
    /// files it never saw (sign/billboard jpgs, .hsg, autosaves in the vanilla folder AND in _BAMP_MP*) and raised
    /// a cloud conflict for the user.
    ///
    /// HOW: the rig driver (tools/rigrun.py) sets the environment variable BAMP_RIG_SAVEROOT=&lt;dir&gt; before it
    /// runs the launcher; the launcher's bats pass the environment through. When that variable is non-empty this
    /// class redirects the game's ONE save-root property - SaveGamePathHelper.SaveGameFolderPath (decompile
    /// SaveGamePathHelper.cs:9, Application.persistentDataPath + "SaveGames") - to &lt;dir&gt;\&lt;role&gt;
    /// (role h / c / d by install folder, the same rule TestDrive uses), so every native write that builds on it
    /// (CurrentVersionFolderPath -> character folders -> .hsg, autosaves, portraits, LogoHelper sign images) lands
    /// there. The mod's MP stores (_BAMP_MP, _BAMP_MP_SIMCLIENT*) are siblings of the version folder
    /// (MPSaveManager.MpVersionFolder), so they follow with no further change. It must run BEFORE
    /// MPSaveManager.EnsureVersionCached (Plugin.OnLoadAsync), which caches the version folder once.
    ///
    /// Variable absent (every normal launch, every player): nothing is patched and nothing is logged.
    /// Release/Debug builds: this file is compiled out.</summary>
    internal static class RigSaveRoot
    {
        internal const string EnvVar = "BAMP_RIG_SAVEROOT";
        private static string? _dir;

        /// <summary>The redirected save root, or null when the redirect is off.</summary>
        internal static string? Dir => _dir;

        internal static void Apply()
        {
            string v;
            try { v = Environment.GetEnvironmentVariable(EnvVar) ?? ""; }
            catch { v = ""; }
            v = v.Trim().Trim('"');
            if (v.Length == 0) return;   // normal launch: silent, untouched
            try
            {
                string dir = Path.GetFullPath(Path.Combine(v, ComputeRole()));
                string synced = Path.GetFullPath(Path.Combine(UnityEngine.Application.persistentDataPath, "SaveGames"));
                if (dir.TrimEnd('\\', '/').StartsWith(synced.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    Plugin.Logger.LogError($"[RigSaveRoot] REFUSED: '{dir}' is inside the Steam-synced '{synced}' - save root NOT redirected.");
                    return;
                }
                Directory.CreateDirectory(dir);
                _dir = dir;
                var getter = AccessTools.PropertyGetter(typeof(SaveGamePathHelper), nameof(SaveGamePathHelper.SaveGameFolderPath));
                new Harmony("com.bamp.bigambitionsmp.rigsaveroot")
                    .Patch(getter, prefix: new HarmonyMethod(typeof(RigSaveRoot), nameof(SaveGameFolderPathPrefix)));
                Plugin.Logger.LogWarning($"[RigSaveRoot] save root redirected to {dir}");
                // Self-check: the version folder must now answer from the rig root (a JIT-inlined copy of the old
                // getter would slip past the patch - say so loudly rather than write into SaveGames unnoticed).
                string ver = "";
                try { ver = SaveGamePathHelper.CurrentVersionFolderPath() ?? ""; } catch (Exception ex) { ver = "(threw " + ex.GetType().Name + ")"; }
                if (!ver.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                    Plugin.Logger.LogError($"[RigSaveRoot] version folder still answers '{ver}' - the redirect did NOT reach it.");
            }
            catch (Exception ex)
            {
                _dir = null;
                Plugin.Logger.LogError($"[RigSaveRoot] redirect FAILED, save root NOT redirected: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool SaveGameFolderPathPrefix(ref string __result)
        {
            var d = _dir;
            if (d == null) return true;
            __result = d;
            return false;
        }

        /// <summary>TestDrive's role rule (src/TestDrive.cs ComputeRole): install folder BigAmbitions&lt;N&gt; (N 2..9)
        /// = (char)('c' + N - 2); anything else (the Steam install) = "h".</summary>
        private static string ComputeRole()
        {
            try
            {
                string name = Path.GetFileName(MPConfig.GameRootPath.TrimEnd('\\', '/'));
                int i = name.Length;
                while (i > 0 && name[i - 1] >= '0' && name[i - 1] <= '9') i--;
                if (i > 0 && i < name.Length &&
                    string.Equals(name.Substring(0, i), "BigAmbitions", StringComparison.OrdinalIgnoreCase))
                {
                    int n;
                    if (int.TryParse(name.Substring(i), out n) && n >= 2 && n <= 9)
                        return ((char)('c' + n - 2)).ToString();
                }
            }
            catch { }
            return "h";
        }
    }
}
#endif
