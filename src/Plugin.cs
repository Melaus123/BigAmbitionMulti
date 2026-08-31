using System;
using System.Linq;
using System.Threading.Tasks;
using BAModAPI;
using HarmonyLib;
using UnityEngine;

// The loader discovers mod classes ONLY through this assembly attribute —
// extending ModBigAmbitionsBase alone logs "[ModDiscovery] No
// RegisterModClassAttribute attributes were found" and nothing runs.
[assembly: RegisterModClass(typeof(BigAmbitionsMP.ModEntry))]

namespace BigAmbitionsMP
{
    /// <summary>
    /// EA 0.11+ entry point — loaded by the game's OFFICIAL mod loader
    /// (BigAmbitions.ModsInternal): ModsLocal\BigAmbitionsMP\BigAmbitionsMP.dll
    /// with Harmony + LiteNetLib in the Dependencies\ subfolder.
    /// (The BepInEx/IL2CPP entry for EA 0.10 lives on the 'main' branch.)
    ///
    /// Scope: Initialization is the loader's PERSISTENT scope (ModLifecycleLoader.
    /// LifetimeScope) — loaded once at boot and NOT unloaded on menu↔city scene
    /// transitions, matching the old BepInEx chainloader lifetime.  MainMenu/City
    /// scopes get unloaded on every transition, which would tear down our
    /// patches, net stack and DontDestroyOnLoad UI mid-session.
    /// </summary>
    [ModEntryOnInitializationLoad]
    public class ModEntry : ModBigAmbitionsBase
    {
        public static ModEntry Instance { get; private set; } = null!;

        /// <summary>Patch classes that failed or bound nothing at load — a dead patch class is
        /// SILENT FEATURE LOSS (2026-07-09 audit: the slice-4 wallet guard shipped unapplied for two
        /// days). Rides every bug report so the field self-reports it.</summary>
        public static readonly System.Collections.Generic.List<string> PatchIssues = new();

        /// <summary>Round-229 (field 20260801-193731): count of OUR patch classes that THREW
        /// during boot patching (the loop walks this assembly only — other mods' hook failures
        /// never enter this number). One machine had all 253 fail (TypeLoadException storm,
        /// machine-local environment) yet the mod offered Host/Join as healthy — the host's own
        /// world load silently bounced while clients were sent into the world.</summary>
        public static int PatchFailCount;

        /// <summary>Two-tier response (user-directed light touch): a few failures WARN but
        /// never block (a game hotfix nicking one optional hook must not lock everyone out —
        /// the retired Escape guard shipped harmlessly dead for five weeks); at this many the
        /// failure is systemic (broken install, conflicting mod, or a game update newer than
        /// the mod) and MP entry is refused to protect shared saves.</summary>
        public const int PatchFailHardBlock = 10;

        public static bool MpDisabledByPatchFailure => PatchFailCount >= PatchFailHardBlock;
        public static bool PatchesDegraded => PatchFailCount > 0;

        /// <summary>Round-254: the Harmony version this build compiled against (set by the
        /// provenance probe) and our own install root — both feed the culprit line when the
        /// SHARED Harmony copy another mod loaded first turns out broken or outdated.</summary>
        public static string CompiledHarmonyVersion = "?";
        internal static string ModRootForDiag = "";

        /// <summary>Round-254: name the loaded 0Harmony's ORIGIN so a player can self-diagnose
        /// which mod to remove. Load order is deterministic (workshop id ascending, decompile-
        /// verified ModDiscoveryRegistry:188), our id is young/high, so a broken OLDER mod wins
        /// the race every launch — the culprit is whoever's folder the loaded copy sits in.</summary>
        internal static string DescribeLoadedHarmony()
        {
            try
            {
                System.Reflection.Assembly? h = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "0Harmony") { h = a; break; }
                if (h == null) return "no Harmony library is loaded at all";
                string loc = ""; try { loc = h.Location ?? ""; } catch { }
                string ver = h.GetName().Version?.ToString() ?? "?";
                string mod = "an unknown mod";
                try
                {
                    if (!string.IsNullOrEmpty(ModRootForDiag)
                        && loc.StartsWith(ModRootForDiag, StringComparison.OrdinalIgnoreCase))
                        mod = "this multiplayer mod's own copy";
                    else
                    {
                        var wm = System.Text.RegularExpressions.Regex.Match(loc, @"[\\/]workshop[\\/]content[\\/]1331550[\\/](\d+)[\\/]");
                        if (wm.Success)
                            mod = $"Workshop mod {wm.Groups[1].Value} (steamcommunity.com/sharedfiles/filedetails/?id={wm.Groups[1].Value})";
                        else
                        {
                            var lm = System.Text.RegularExpressions.Regex.Match(loc, @"[\\/]ModsLocal[\\/]([^\\/]+)[\\/]");
                            if (lm.Success) mod = $"locally installed mod '{lm.Groups[1].Value}'";
                        }
                    }
                }
                catch { }
                return $"Harmony v{ver}, loaded from {mod}\nFile: {loc}";
            }
            catch (Exception ex) { return $"(could not inspect the loaded Harmony: {ex.Message})"; }
        }

        /// <summary>Round-254b (user ruling: the banner names the mod, nothing else — the
        /// remedy is identical either way). Returns the short culprit line, and whether it
        /// actually points at ANOTHER mod (naming ourselves or nothing = generic message).</summary>
        /// <summary>2026-08-18 (user: "thats not how names work"): players see NAMES in
        /// their mod list, never Workshop ids — the Lord-Wolf report relayed "not showing
        /// up" instead of the culprit because the banner led with a number.  Resolve the
        /// culprit folder to its DISPLAY NAME via the game's own loader registry
        /// (ModDiscoveryRegistry.Entries → DiscoveredModEntry.ModFolder/ModDisplayName;
        /// names live inside mod DLLs, not on disk, so the registry is the only source).
        /// Reflection, not a compile reference: this path runs once, on a failing boot.
        /// The id + URL stay as fine print — support still wants them.</summary>
        private static string ResolveModDisplayName(string culpritFolder)
        {
            try
            {
                // Separator normalization is load-bearing: Unity's persistentDataPath
                // uses FORWARD slashes while the loaded DLL's Location uses backslashes
                // — the first rig test fell to the fallback branch on exactly this.
                static string Norm(string p) => p.Replace('/', '\\').TrimEnd('\\');
                string want = Norm(culpritFolder);
                string wantLeaf = System.IO.Path.GetFileName(want);
                string leafHit = "";
                var regT = HarmonyLib.AccessTools.TypeByName("BigAmbitions.ModsInternal.ModDiscoveryRegistry");
                var entriesProp = regT == null ? null : HarmonyLib.AccessTools.Property(regT, "Entries");
                if (entriesProp?.GetValue(null) is not System.Collections.IDictionary byScope) return "";
                foreach (System.Collections.DictionaryEntry scope in byScope)
                {
                    if (scope.Value is not System.Collections.IEnumerable list) continue;
                    foreach (var entry in list)
                    {
                        if (entry == null) continue;
                        var t = entry.GetType();
                        string folder = Norm(HarmonyLib.AccessTools.Property(t, "ModFolder")?.GetValue(entry) as string ?? "");
                        if (folder.Length == 0) continue;
                        string name = HarmonyLib.AccessTools.Property(t, "ModDisplayName")?.GetValue(entry) as string ?? "";
                        if (string.Equals(folder, want, StringComparison.OrdinalIgnoreCase)
                            || want.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))
                            return name;
                        // Last-resort: same leaf folder name (covers differently-rooted
                        // registry paths); only used if no full-path match exists.
                        if (leafHit.Length == 0 && wantLeaf.Length > 0
                            && string.Equals(System.IO.Path.GetFileName(folder), wantLeaf, StringComparison.OrdinalIgnoreCase))
                            leafHit = name;
                    }
                }
                return leafHit;
            }
            catch { }
            return "";
        }

        internal static string CulpritShort(out bool isOtherMod)
        {
            isOtherMod = false;
            try
            {
                System.Reflection.Assembly? h = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "0Harmony") { h = a; break; }
                string loc = ""; try { loc = h?.Location ?? ""; } catch { }
                if (string.IsNullOrEmpty(loc)) return "";
                if (!string.IsNullOrEmpty(ModRootForDiag)
                    && loc.StartsWith(ModRootForDiag, StringComparison.OrdinalIgnoreCase)) return "";
                var wm = System.Text.RegularExpressions.Regex.Match(loc, @"[\\/]workshop[\\/]content[\\/]1331550[\\/](\d+)[\\/]");
                if (wm.Success)
                {
                    isOtherMod = true;
                    string folder = "";
                    try { folder = loc.Substring(0, wm.Index + wm.Length).TrimEnd('\\', '/'); } catch { }
                    string name = folder.Length > 0 ? ResolveModDisplayName(folder) : "";
                    string headline = name.Length > 0
                        ? $"\"{name}\""
                        : $"Workshop mod {wm.Groups[1].Value}";   // name unresolvable → the id is still better than nothing
                    return $"{headline}\n(Workshop item {wm.Groups[1].Value} — steamcommunity.com/sharedfiles/filedetails/?id={wm.Groups[1].Value})"
                         + (folder.Length > 0 ? $"\nFolder: {folder}" : "");
                }
                var lm = System.Text.RegularExpressions.Regex.Match(loc, @"^(.*[\\/]ModsLocal[\\/][^\\/]+)");
                if (lm.Success)
                {
                    isOtherMod = true;
                    string folder = lm.Groups[1].Value;
                    string name = ResolveModDisplayName(folder);
                    return (name.Length > 0 ? $"\"{name}\"\n" : $"Local mod '{System.IO.Path.GetFileName(folder)}'\n")
                         + $"Folder: {folder}";
                }
                return "";
            }
            catch { return ""; }
        }

        /// <summary>Round-254b: the ONE banner text for every Harmony-conflict shape —
        /// concise, names the conflicting mod, presents both removals neutrally. The
        /// window itself never times out (dismiss-on-OK), so it is read at leisure.
        /// All classification detail (incomplete vs outdated, versions, file paths)
        /// stays in the LOG lines the callers emit.</summary>
        internal static string BuildConflictBanner()
        {
            // Round-254c: EXACT user-specified wording (2026-08-13) — name the culprit,
            // nothing else. No remedy text, no technical detail (all of that is in the log).
            string culprit = CulpritShort(out bool other);
            return other
                ? $"Another installed mod is incompatible with {MyPluginInfo.DISPLAY_NAME}:\n\n{culprit}"
                : $"{MyPluginInfo.DISPLAY_NAME} failed to load and is disabled. Details are in the game log.";
        }

        public static string PatchFailureNotice =>
            $"{MyPluginInfo.SHORT_NAME} could not attach to the game ({PatchFailCount} hook(s) failed) — " +
            "multiplayer is disabled to protect your saves. Verify the game files in Steam " +
            "(right-click the game > Properties > Installed Files), and check for a mod update; " +
            "if neither fixes it, remove other mods and retry.";

        public static string PatchDegradedNotice =>
            $"{MyPluginInfo.SHORT_NAME}: {PatchFailCount} game hook(s) failed to install. Multiplayer will run, " +
            "but some features may misbehave. If you notice problems, verify the game files in Steam " +
            "and check for a mod update.";

        private Harmony? _harmony;
        private GameObject? _uiHost;

        public override Task OnLoadAsync(ModContext context)
        {
            Instance = this;
            Plugin.Logger = new ModLog(context.Logger);

            // ── Double-install guard (Workshop readiness) ─────────────────────
            // The game loads mods from BOTH ModsLocal and Steam Workshop with no
            // dedup — a Manager install plus a Workshop subscription would run
            // TWO copies of this mod (two Harmony patch sets = breakage).
            // Assembly statics are per-copy, so coordinate through AppDomain
            // data slots: the first copy claims the singleton slot; any later
            // copy records itself in the duplicate slot (the live copy's UI
            // polls it and warns the player) and loads NOTHING.
            var priorRoot = AppDomain.CurrentDomain.GetData("BAMP_SINGLETON_ROOT") as string;
            if (priorRoot != null)
            {
                AppDomain.CurrentDomain.SetData("BAMP_DUPLICATE_ROOT", context.ModRootPath ?? "unknown");
                Plugin.Logger.LogError(
                    $"{MyPluginInfo.DISPLAY_NAME}: another copy is already loaded from '{priorRoot}' — " +
                    $"this copy ('{context.ModRootPath}') will NOT start. Keep ONE install: either the " +
                    "Steam Workshop subscription or the local ModsLocal copy, not both.");
                return Task.CompletedTask;
            }
            AppDomain.CurrentDomain.SetData("BAMP_SINGLETON_ROOT", context.ModRootPath ?? "unknown");
            ModRootForDiag = context.ModRootPath ?? "";   // round-254: culprit-vs-own-copy check

            Plugin.Logger.LogInfo($"BigAmbitionsMP loading (official loader, modId='{context.ModId}', root='{context.ModRootPath}')...");

            MPConfig.Init(context.ModRootPath);
            MPBugReport.MarkSessionStarted();
            MPBugReport.CachePaths();   // bug-report v2: persistentDataPath is main-thread-only; the peer-log responder runs off-thread

            // Persistent host object for our UI component (Mono: custom
            // MonoBehaviours need no registration — AddComponent just works).
            _uiHost = new GameObject("BigAmbitionsMP");
            UnityEngine.Object.DontDestroyOnLoad(_uiHost);
            _uiHost.AddComponent<MPCanvasUI>();

            // Apply Harmony patches per-class so a single bad class can't take
            // down the rest (PatchAll aborts on the first throw).
            //
            // Round-241 provenance probe (field 20260803-222126: 253/253 classes threw
            // TypeLoadException on a byte-correct game install with 34 mods — the shape of a
            // SHARED-HARMONY VERSION CONFLICT, but the bundle could not name the culprit).
            // One line settles it in the next such bundle: which 0Harmony actually loaded,
            // from whose folder, versus the version we compiled against.
            try
            {
                var loaded = typeof(Harmony).Assembly;
                string compiledAgainst = "?";
                foreach (var r in typeof(ModEntry).Assembly.GetReferencedAssemblies())
                    if (r.Name == "0Harmony") { compiledAgainst = r.Version?.ToString() ?? "?"; break; }
                CompiledHarmonyVersion = compiledAgainst;   // round-254: reused by the failure banner
                string loc = "";
                try { loc = loaded.Location; } catch { }
                Plugin.Logger.LogInfo($"[Plugin] Harmony provenance: loaded v{loaded.GetName().Version} from '{(string.IsNullOrEmpty(loc) ? "(in-memory)" : loc)}', compiled against v{compiledAgainst}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plugin] Harmony provenance probe: {ex.Message}"); }
            // ── Round-254: the Harmony BOOTSTRAP itself can die before any patching ──
            // Field 2026-08-13 (Vitinhoooo): an OLDER mod (load order = workshop id
            // ascending, always beats our young id) shipped 0Harmony without
            // MonoMod.RuntimeDetour — Harmony's type initializer threw ONCE, stayed
            // poisoned, and our unwrapped `new Harmony(...)` rethrew it, killing
            // OnLoadAsync entirely: no mod, no button, no explanation. Now: catch,
            // CLASSIFY (incomplete package vs anything else), name the culprit's
            // folder, and return NORMALLY — the canvas (built above, Harmony-free)
            // survives purely to show the player which mod to remove (user ruling
            // 2026-08-13: identify the incompatible mod; the player decides which
            // of the two goes).
            int okClasses = 0, failClasses = 0, deadClasses = 0, totalPatched = 0;
            try
            {
            _harmony = new Harmony("com.bamp.bigambitionsmp");
            foreach (var t in typeof(ModEntry).Assembly.GetTypes())
            {
                if (!t.GetCustomAttributes(typeof(HarmonyPatch), true).Any()) continue;
                try
                {
                    var before = _harmony.GetPatchedMethods().Count();
                    // Patch() returns the methods THIS class attached to — the per-class truth. The old
                    // global-target diff couldn't tell "target shared with an earlier class" (benign,
                    // the round-15 goose chase) from "bound NOTHING" (dead class = silent feature loss;
                    // 2026-07-09 audit: the slice-4 wallet guard shipped unapplied behind exactly this
                    // ambiguity, next to eleven unreviewable zero lines).
                    var bound = _harmony.CreateClassProcessor(t).Patch();
                    var added = _harmony.GetPatchedMethods().Count() - before;
                    totalPatched += added;
                    int boundCount = bound?.Count ?? 0;
                    if (boundCount == 0)
                    {
                        deadClasses++;
                        PatchIssues.Add($"{t.Name}: bound nothing");
                        Plugin.Logger.LogWarning($"[Plugin] Patch class {t.Name} BOUND NOTHING — dead class (empty TargetMethod(s)); whatever it guards is OFF.");
                    }
                    else
                    {
                        okClasses++;
                        Plugin.Logger.LogInfo($"[Plugin] Patched {t.Name}: {boundCount} method(s)"
                            + (added == 0 ? " (targets shared with an earlier class)" : ""));
                    }
                }
                catch (Exception ex)
                {
                    // Walk to the ROOT cause (round-65): HarmonyException wraps the real
                    // TargetMethod failure — the outer message alone left the MIREL
                    // report undiagnosable (which of 34 mods broke the scan? unknowable).
                    var root = ex;
                    while (root.InnerException != null) root = root.InnerException;
                    string inner = ReferenceEquals(root, ex) ? "" : $" ← {root.GetType().Name}: {root.Message}";
                    failClasses++;
                    PatchIssues.Add($"{t.Name}: {ex.GetType().Name}: {ex.Message}{inner}");
                    Plugin.Logger.LogError($"[Plugin] Patch class {t.Name} FAILED: {ex.GetType().Name}: {ex.Message}{inner}");
                    // Round-229: the root Message alone can be nameless ("Failure has occurred
                    // while loading a type") — for the first few failures keep the FULL root
                    // (stack + TypeLoadException.TypeName when present) so a field bundle
                    // names what would not load. Capped: 253 identical stacks help nobody.
                    if (failClasses <= 3)
                    {
                        string? tln = (root as TypeLoadException)?.TypeName;
                        Plugin.Logger.LogError($"[Plugin] {t.Name} root detail{(string.IsNullOrEmpty(tln) ? "" : $" (type '{tln}')")}: {root}");
                    }
                }
            }
            PatchFailCount = failClasses;
            Plugin.Logger.LogInfo($"[Plugin] Patch summary: {okClasses} class(es) OK, {failClasses} failed, {deadClasses} dead, {totalPatched} method(s) patched total.");
            if (MpDisabledByPatchFailure)
            {
                Plugin.Logger.LogError($"[Plugin] {failClasses} patch class(es) FAILED (>= {PatchFailHardBlock}) — systemic; multiplayer entry is DISABLED this run (broken install, conflicting mod, or game update newer than the mod).");
                // Round-254 case B (the round-241 shape): Harmony STARTED but our patches
                // failed en masse — classic OUTDATED shared copy loaded first. Full
                // classification goes to the LOG; the banner just names the mod (254b).
                string src = DescribeLoadedHarmony();
                MPCanvasUI.FatalBootBanner = BuildConflictBanner();
                Plugin.Logger.LogError($"[Plugin] Harmony origin for the failure above: {src.Replace("\n", " | ")} (we compiled against v{CompiledHarmonyVersion}).");
            }
            else if (PatchesDegraded)
                Plugin.Logger.LogWarning($"[Plugin] {failClasses} patch class(es) FAILED (< {PatchFailHardBlock}) — multiplayer stays ENABLED; affected features may misbehave.");
            }
            catch (Exception bootEx)
            {
                // Round-254 case A: the shared Harmony never initialized at all.
                Exception root = bootEx;
                while (root.InnerException != null) root = root.InnerException;
                string src = DescribeLoadedHarmony();
                string what;
                if (root is System.IO.FileNotFoundException fnf)
                {
                    string missing = fnf.FileName ?? fnf.Message;
                    int comma = missing.IndexOf(',');                       // 'MonoMod.RuntimeDetour, Version=...' → short name
                    if (comma > 0) missing = missing.Substring(0, comma);
                    what = $"an INCOMPLETE copy of Harmony (the patching library all mods share): its companion file '{missing}' is missing";
                }
                else
                    what = $"a broken copy of Harmony (the patching library all mods share) — it failed to start ({root.GetType().Name}: {root.Message})";
                PatchFailCount = 9999;   // every existing "MP disabled" gate engages
                MPCanvasUI.FatalBootBanner = BuildConflictBanner();   // 254b: concise, mod-naming only
                Plugin.Logger.LogError($"[Plugin] HARMONY BOOTSTRAP FAILED (round-254): {what}. Origin: {src.Replace("\n", " | ")}. "
                    + $"Multiplayer is disabled this session; the on-screen banner names the culprit. Root: {root}");
            }

            // Field 20260830-170644: the exit notification rides the game's own completion
            // event now (fires after the street teleport, and on every real exit path) —
            // see MPPatches.ExitBuildingNotifier for the two defects the old prefix had.
            try { MPPatches.ExitBuildingNotifier.Install(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Plugin] exit notifier: {ex.Message}"); }

            Plugin.Logger.LogInfo($"{MyPluginInfo.DISPLAY_NAME} (BigAmbitionsMP) v{MyPluginInfo.PLUGIN_VERSION} ({MyPluginInfo.BuildTag}) loaded. Canvas UI active.");
            return Task.CompletedTask;
        }

        public override Task OnUnloadAsync()
        {
            try { MPPatches.ExitBuildingNotifier.Uninstall(); } catch { }
            try { MPBugReport.MarkCleanShutdown(); } catch { }
            try { _harmony?.UnpatchAll(_harmony.Id); } catch { }
            try { MPServer.Stop(); } catch { }
            try { MPClient.Disconnect(); } catch { }
            if (_uiHost != null) UnityEngine.Object.Destroy(_uiHost);
            return Task.CompletedTask;
        }
    }

    /// <summary>Static façade kept so the rest of the codebase compiles
    /// unchanged: Plugin.Logger.Log* everywhere routes to the official
    /// loader's IModLogger and Unity's console.</summary>
    public static class Plugin
    {
        public static ModLog Logger { get; internal set; } = new ModLog(null);
    }

    /// <summary>BepInEx-ManualLogSource-shaped wrapper over IModLogger.</summary>
    public class ModLog
    {
        private readonly IModLogger? _inner;
        public ModLog(IModLogger? inner) { _inner = inner; }

        public void LogInfo(object msg)    { MPLog.Record("INFO", msg?.ToString() ?? ""); var s = $"[BAMP] {msg}";  if (_inner != null) _inner.Info(s);  else Debug.Log(s); }
        public void LogWarning(object msg) { MPLog.Record("WARN", msg?.ToString() ?? ""); var s = $"[BAMP] {msg}";  if (_inner != null) _inner.Warn(s);  else Debug.LogWarning(s); }
        public void LogError(object msg)   { MPLog.Record("ERR ", msg?.ToString() ?? ""); var s = $"[BAMP] {msg}";  if (_inner != null) _inner.Error(s); else Debug.LogError(s); }
        public void LogDebug(object msg)   => LogInfo(msg);
    }
}
