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

            Plugin.Logger.LogInfo($"BigAmbitionsMP loading (official loader, modId='{context.ModId}', root='{context.ModRootPath}')...");

            MPConfig.Init(context.ModRootPath);
            MPBugReport.MarkSessionStarted();

            // Persistent host object for our UI component (Mono: custom
            // MonoBehaviours need no registration — AddComponent just works).
            _uiHost = new GameObject("BigAmbitionsMP");
            UnityEngine.Object.DontDestroyOnLoad(_uiHost);
            _uiHost.AddComponent<MPCanvasUI>();

            // Apply Harmony patches per-class so a single bad class can't take
            // down the rest (PatchAll aborts on the first throw).
            _harmony = new Harmony("com.bamp.bigambitionsmp");
            int okClasses = 0, failClasses = 0, deadClasses = 0, totalPatched = 0;
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
                Plugin.Logger.LogError($"[Plugin] {failClasses} patch class(es) FAILED (>= {PatchFailHardBlock}) — systemic; multiplayer entry is DISABLED this run (broken install, conflicting mod, or game update newer than the mod).");
            else if (PatchesDegraded)
                Plugin.Logger.LogWarning($"[Plugin] {failClasses} patch class(es) FAILED (< {PatchFailHardBlock}) — multiplayer stays ENABLED; affected features may misbehave.");

            Plugin.Logger.LogInfo($"{MyPluginInfo.DISPLAY_NAME} (BigAmbitionsMP) v{MyPluginInfo.PLUGIN_VERSION} ({MyPluginInfo.BuildTag}) loaded. Canvas UI active.");
            return Task.CompletedTask;
        }

        public override Task OnUnloadAsync()
        {
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
