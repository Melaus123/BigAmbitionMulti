using System.Text.Json;
using System.Reflection;
using Buildings;
using Entities;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Reads live game state from SaveGameManager.Current for broadcast purposes.
    /// </summary>
    public static class GameStateReader
    {
        // ── Game time ─────────────────────────────────────────────────────────

        // Cached reflected members for GameInstance time fields.
        // Populated on first call while in-game; null = field not yet found or unavailable.
        // Log confirms: P:Day(Int32), P:Hour(Int32), P:Minute(Single) all exist as properties.
        private static bool       _timeProbed;
        private static FieldInfo?    _fDay;
        private static FieldInfo?    _fHour;
        private static PropertyInfo? _pDay;
        private static PropertyInfo? _pHour;
        private static PropertyInfo? _pMinute;   // fractional part — must add Minute/60 to hour

        /// <summary>
        /// Returns the current in-game day and fractional hour (0–24) from the host's GameInstance.
        /// Uses a one-time reflection probe to locate field/property names and caches the result.
        /// Logs all time-related members the first time it runs so we can identify the correct names.
        /// </summary>
        public static (int day, float hourOfDay) GetGameTime()
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return (0, 0f);

                // ── One-time discovery probe ───────────────────────────────
                if (!_timeProbed)
                {
                    _timeProbed = true;
                    var t = gi.GetType();

                    // Log ALL public instance fields and properties that look time-related
                    var relevant = t
                        .GetFields(BindingFlags.Public | BindingFlags.Instance)
                        .Select(f => $"F:{f.Name}({f.FieldType.Name})")
                        .Concat(t
                            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                            .Select(p => $"P:{p.Name}({p.PropertyType.Name})"))
                        .Where(s =>
                        {
                            var n = s.ToLower();
                            return n.Contains("day") || n.Contains("time") ||
                                   n.Contains("hour") || n.Contains("minute") ||
                                   n.Contains("clock") || n.Contains("tick");
                        })
                        .ToArray();

                    Plugin.Logger.LogInfo(
                        $"[GameStateReader] GameInstance time-related members: " +
                        (relevant.Length > 0 ? string.Join(", ", relevant) : "(none found)"));

                    // Try to cache specific known candidates
                    _fDay  = t.GetField("day",  BindingFlags.Public | BindingFlags.Instance);
                    _fHour = t.GetField("hour", BindingFlags.Public | BindingFlags.Instance)
                          ?? t.GetField("currentHour", BindingFlags.Public | BindingFlags.Instance)
                          ?? t.GetField("timeOfDay",   BindingFlags.Public | BindingFlags.Instance);
                    _pDay  = t.GetProperty("Day",  BindingFlags.Public | BindingFlags.Instance);
                    _pHour = t.GetProperty("Hour", BindingFlags.Public | BindingFlags.Instance)
                          ?? t.GetProperty("CurrentHour", BindingFlags.Public | BindingFlags.Instance)
                          ?? t.GetProperty("TimeOfDay",   BindingFlags.Public | BindingFlags.Instance);
                    _pMinute = t.GetProperty("Minute", BindingFlags.Public | BindingFlags.Instance);

                    // Log writability — needed to know if clock-revert suppression
                    // can actually put time back, or only partially.
                    Plugin.Logger.LogInfo(
                        $"[GameStateReader] Time member writability: " +
                        $"Day={(_pDay?.CanWrite.ToString() ?? "n/a")} " +
                        $"Hour={(_pHour?.CanWrite.ToString() ?? "n/a")} " +
                        $"Minute={(_pMinute?.CanWrite.ToString() ?? "n/a")}");
                }

                // ── Read day ──────────────────────────────────────────────
                int day = 0;
                if      (_fDay != null) day = (int)(_fDay.GetValue(gi) ?? 0);
                else if (_pDay != null) day = (int)(_pDay.GetValue(gi) ?? 0);

                // ── Read hour (integer 0-23) + minute (float 0-60) ───────
                float hour = 0f;
                if      (_fHour != null) hour = Convert.ToSingle(_fHour.GetValue(gi));
                else if (_pHour != null) hour = Convert.ToSingle(_pHour.GetValue(gi));

                // Add fractional minutes so the clock-rate monitor can see sub-hour advances
                if (_pMinute != null)
                {
                    float minute = Convert.ToSingle(_pMinute.GetValue(gi));
                    hour += minute / 60f;
                }

                return (day, hour);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[GameStateReader] GetGameTime: {ex.Message}");
                return (0, 0f);
            }
        }

        /// <summary>
        /// Attempts to apply a day/time value to the local GameInstance.
        /// Only writes values that differ by more than a small threshold to avoid jitter.
        /// </summary>
        public static void SetGameTime(int day, float hourOfDay)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return;

                // Ensure probe has run
                if (!_timeProbed) GetGameTime();

                // hourOfDay is fractional (Hour + Minute/60).  The game stores Hour as
                // an Int32 property and Minute as a Single property — writing the raw
                // float into the Int32 Hour setter throws.  Split before writing.
                int   hourInt = (int)hourOfDay;
                float minute  = (hourOfDay - hourInt) * 60f;
                if (hourInt < 0)  hourInt = 0;
                if (hourInt > 23) hourInt = 23;
                if (minute  < 0f) minute  = 0f;

                // ── Day (Int32) ──────────────────────────────────────────
                if (_fDay != null)
                {
                    var cur = Convert.ToInt32(_fDay.GetValue(gi) ?? 0);
                    if (cur != day) _fDay.SetValue(gi, day);
                }
                else if (_pDay != null && _pDay.CanWrite)
                {
                    var cur = Convert.ToInt32(_pDay.GetValue(gi) ?? 0);
                    if (cur != day) _pDay.SetValue(gi, day);
                }

                // ── Hour (Int32) ─────────────────────────────────────────
                if (_fHour != null)
                {
                    var cur = Convert.ToInt32(_fHour.GetValue(gi) ?? 0);
                    if (cur != hourInt) _fHour.SetValue(gi, hourInt);
                }
                else if (_pHour != null && _pHour.CanWrite)
                {
                    var cur = Convert.ToInt32(_pHour.GetValue(gi) ?? 0);
                    if (cur != hourInt) _pHour.SetValue(gi, hourInt);
                }

                // ── Minute (Single) ──────────────────────────────────────
                if (_pMinute != null && _pMinute.CanWrite)
                    _pMinute.SetValue(gi, minute);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[GameStateReader] SetGameTime: {ex.Message}");
            }
        }


        // ── GameSpeedController probe ─────────────────────────────────────────
        //
        // Discovered at runtime via AppDomain reflection so we don't need to know
        // the namespace at compile time.  Instance is populated via a Harmony Postfix
        // on TogglePause (see MPPatches.Patch_GSC_TogglePause).
        //
        // In IL2CPP interop the game state is exposed as PROPERTIES (not fields).
        // Confirmed from log: P:Paused(Boolean), P:isFastForwarding(Boolean),
        //   P:playerGameSpeed(GameSpeed), P:currentGameSpeed(GameSpeed).
        // The raw IL2CPP field slots are NativeFieldInfoPtr_* IntPtrs — unusable.

        private static bool _gscProbed;
        private static Type? _gscType;
        private static object? _gscInstance;       // captured by Harmony postfix
        private static PropertyInfo? _gscPPaused;        // "Paused" property (bool)
        private static PropertyInfo? _gscPIsFastFwd;     // "isFastForwarding" property (bool)
        private static PropertyInfo? _gscPPlayerSpd;     // "playerGameSpeed" property (GameSpeed enum)
        private static PropertyInfo? _gscPCurrentSpd;    // "currentGameSpeed" property (GameSpeed enum)
        private static PropertyInfo? _gscPTimeCtrl;      // "isTimeControlDisabled" property (bool)

        /// <summary>
        /// Called by the Harmony postfix on GameSpeedController.TogglePause to cache
        /// the instance for diagnostic reads.
        /// </summary>
        public static void CacheGSCInstance(object instance)
        {
            _gscInstance = instance;
        }

        /// <summary>
        /// Looks up the named method on GameSpeedController.  Used by the Harmony
        /// TargetMethod() delegate so we don't need a compile-time type reference.
        /// Returns null if the type hasn't been found yet (patch will be skipped).
        /// </summary>
        public static System.Reflection.MethodBase? FindGSCMethod(string methodName)
        {
            EnsureGSCProbed();
            return _gscType?.GetMethod(methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }

        /// <summary>
        /// Returns a diagnostic string with all readable GameSpeedController state.
        /// Safe to call every frame — returns a placeholder if the instance hasn't
        /// been captured yet (i.e. TogglePause hasn't fired yet this session).
        /// </summary>
        public static string GetGSCDiagnostic()
        {
            try
            {
                EnsureGSCProbed();
                if (_gscType == null)     return "(GSC type not found)";
                if (_gscInstance == null) return "(GSC instance not yet cached — press pause in-game first)";

                var parts = new List<string>();
                if (_gscPPaused    != null) parts.Add($"Paused={_gscPPaused.GetValue(_gscInstance)}");
                if (_gscPIsFastFwd != null) parts.Add($"isFastFwd={_gscPIsFastFwd.GetValue(_gscInstance)}");
                if (_gscPPlayerSpd != null) parts.Add($"playerSpd={_gscPPlayerSpd.GetValue(_gscInstance)}");
                if (_gscPCurrentSpd!= null) parts.Add($"currentSpd={_gscPCurrentSpd.GetValue(_gscInstance)}");
                if (_gscPTimeCtrl  != null) parts.Add($"timeCtrlDisabled={_gscPTimeCtrl.GetValue(_gscInstance)}");
                parts.Add($"timeScale={UnityEngine.Time.timeScale:F3}");
                return string.Join(", ", parts);
            }
            catch (Exception ex)
            {
                return $"(GSC error: {ex.Message})";
            }
        }

        /// <summary>
        /// Returns true if the local player's GameSpeedController.Paused property is true —
        /// i.e. the player themselves clicked the pause button (not force-paused by network).
        /// Returns false if the instance hasn't been captured yet.
        /// </summary>
        public static bool GetGSCPaused()
        {
            if (_gscInstance == null || _gscPPaused == null) return false;
            try { return (bool)(_gscPPaused.GetValue(_gscInstance) ?? false); }
            catch { return false; }
        }

        private static System.Reflection.MethodBase? _gscMTogglePause;

        /// <summary>Drive the game's REAL pause (red screen border, pulsing pause
        /// button, player movement stops) to match <paramref name="paused"/>.
        /// Used by the mod's freeze states (startup hold, disconnect pause,
        /// host-loss notice) so they read as a true pause instead of a silent
        /// clock stop.  MAIN THREAD ONLY (IL2CPP).  No-ops gracefully if the
        /// controller can't be resolved — the timeScale clamps still hold.</summary>
        public static void SetNativePause(bool paused)
        {
            try
            {
                EnsureGSCProbed();
                if (_gscType == null) return;

                // The Harmony postfix caches the instance on the first pause
                // press, but our holds usually fire before any press — discover
                // it (and re-discover if the cached one died with its scene).
                bool dead = false;
                try { var uo = _gscInstance as UnityEngine.Object; dead = _gscInstance != null && uo != null && uo == null; } catch { }
                if (_gscInstance == null || dead)
                {
                    var arr = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.From(_gscType), true);
                    if (arr != null && arr.Length > 0)
                        _gscInstance = Activator.CreateInstance(_gscType, arr[0].Pointer);   // typed wrapper for reflection
                }
                if (_gscInstance == null) return;
                if (GetGSCPaused() == paused) return;   // already in the wanted state

                _gscMTogglePause ??= _gscType.GetMethod("TogglePause",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_gscMTogglePause == null) return;
                _gscMTogglePause.Invoke(_gscInstance, null);
                Plugin.Logger.LogInfo($"[GSC] Native pause → {paused}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[GSC] SetNativePause({paused}): {ex.Message}"); }
        }

        /// <summary>
        /// Returns true if GameSpeedController.isFastForwarding is true — i.e. the game
        /// is currently in a time-skip state (bench skip, sleep, or any other fast-forward).
        /// This is the authoritative skip-active flag regardless of Time.timeScale value.
        /// Returns false if the instance hasn't been captured yet.
        /// </summary>
        public static bool GetGSCIsFastForwarding()
        {
            if (_gscInstance == null || _gscPIsFastFwd == null) return false;
            try { return (bool)(_gscPIsFastFwd.GetValue(_gscInstance) ?? false); }
            catch { return false; }
        }

        /// <summary>
        /// Returns true if GameSpeedController.isTimeControlDisabled is true — i.e. the
        /// game has locked out speed control because a blocking menu/dialog is open
        /// (bench rest, bed, phone, shop…).  This is the signal that a pause is a
        /// "menu pause" rather than a deliberate pause-button press.
        /// Returns false if the instance hasn't been captured yet.
        /// </summary>
        public static bool GetGSCTimeControlDisabled()
        {
            if (_gscInstance == null || _gscPTimeCtrl == null) return false;
            try { return (bool)(_gscPTimeCtrl.GetValue(_gscInstance) ?? false); }
            catch { return false; }
        }

        private static void EnsureGSCProbed()
        {
            if (_gscProbed) return;
            _gscProbed = true;

            // Search all loaded assemblies for GameSpeedController by name
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    // Fast path: exact name lookup (works if in global namespace)
                    _gscType = asm.GetType("GameSpeedController");
                    if (_gscType == null)
                    {
                        // Slow path: scan all types in the assembly
                        _gscType = asm.GetTypes()
                            .FirstOrDefault(t => t.Name == "GameSpeedController");
                    }
                    if (_gscType != null) break;
                }
                catch { /* skip assemblies that can't be enumerated */ }
            }

            if (_gscType == null)
            {
                Plugin.Logger.LogWarning("[GameStateReader] GameSpeedController not found in any assembly.");
                return;
            }

            Plugin.Logger.LogInfo(
                $"[GameStateReader] GameSpeedController found in '{_gscType.Assembly.GetName().Name}'.");

            // Log all fields, properties and public methods for discovery
            var allFlags = BindingFlags.Public | BindingFlags.NonPublic |
                           BindingFlags.Instance | BindingFlags.Static;
            var fields  = _gscType.GetFields(allFlags)
                .Select(f => $"F:{f.Name}({f.FieldType.Name}){(f.IsStatic ? "[S]" : "")}");
            var props   = _gscType.GetProperties(allFlags)
                .Select(p => $"P:{p.Name}({p.PropertyType.Name}){(p.GetMethod?.IsStatic == true ? "[S]" : "")}");
            var methods = _gscType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => !m.IsSpecialName)
                .Select(m => $"M:{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");

            Plugin.Logger.LogInfo(
                $"[GameStateReader] GSC fields:   {string.Join(" | ", fields)}\n" +
                $"[GameStateReader] GSC props:    {string.Join(" | ", props)}\n" +
                $"[GameStateReader] GSC methods:  {string.Join(" | ", methods)}");

            // Cache the properties we care about.
            // In IL2CPP interop all instance state is exposed as properties — the
            // NativeFieldInfoPtr_* entries in the field list are just native pointers.
            // Property names are case-sensitive; "Paused" has a capital P.
            var instFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _gscPPaused     = _gscType.GetProperty("Paused",           instFlags);
            _gscPIsFastFwd  = _gscType.GetProperty("isFastForwarding", instFlags);
            _gscPPlayerSpd  = _gscType.GetProperty("playerGameSpeed",  instFlags);
            _gscPCurrentSpd = _gscType.GetProperty("currentGameSpeed", instFlags);
            _gscPTimeCtrl   = _gscType.GetProperty("isTimeControlDisabled", instFlags);

            Plugin.Logger.LogInfo(
                $"[GameStateReader] GSC cache: Paused={_gscPPaused?.Name ?? "null"} " +
                $"isFastFwd={_gscPIsFastFwd?.Name ?? "null"} " +
                $"playerSpd={_gscPPlayerSpd?.Name ?? "null"} " +
                $"currentSpd={_gscPCurrentSpd?.Name ?? "null"}");
        }

        /// <summary>
        /// Returns the current ProductMarketEntry list serialised as JSON.
        /// Called by the host when building a world snapshot or periodic market broadcast.
        /// </summary>
        public static string GetMarketEntriesJson()
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return "[]";

                // IL2CPP List<T> doesn't support LINQ directly — iterate with index
                var entries = new List<MarketEntryDto>();
                for (int i = 0; i < gi.productMarketEntries.Count; i++)
                {
                    var e = gi.productMarketEntries[i];
                    entries.Add(new MarketEntryDto
                    {
                        ItemName         = e.itemName.ToString(),
                        ImportPriceIndex = e.importPriceIndex,
                    });
                }

                return JsonSerializer.Serialize(entries);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[GameStateReader] GetMarketEntriesJson failed: {ex.Message}");
                return "[]";
            }
        }

        /// <summary>
        /// Returns a stable address key string for a Building: "StreetNumber StreetName"
        /// </summary>
        public static string AddressKey(Building building)
            => $"{building.StreetNumber} {building.StreetName}";

        public static string AddressKey(BuildingRegistration reg)
            => $"{reg.StreetNumber} {reg.StreetName}";

        /// <summary>Same key from an Address value (used to match the rival
        /// business-breakdown cells, which carry an Address not a registration).</summary>
        public static string AddressKey(Address address)
            => $"{address.streetNumber} {address.streetName}";

        // ── GameVariables probe ───────────────────────────────────────────────
        // One-time discovery probe: logs the structure of GameVariables so we can
        // identify which member controls Story Mode vs Custom/Sandbox and the
        // difficulty — needed to force multiplayer new games into Custom mode.

        private static bool _gameVarsProbed;

        public static void ProbeGameVariables()
        {
            if (_gameVarsProbed) return;
            _gameVarsProbed = true;
            try
            {
                var t = typeof(GameVariables);
                var flags = BindingFlags.Public | BindingFlags.NonPublic |
                            BindingFlags.Instance | BindingFlags.Static;

                var instFlags = BindingFlags.Public | BindingFlags.Instance;

                var fields = t.GetFields(flags)
                    .Select(f => $"F:{f.Name}({f.FieldType.Name}){(f.IsStatic ? "[S]" : "")}");
                var props  = t.GetProperties(flags)
                    .Select(p => $"P:{p.Name}({p.PropertyType.Name})");
                var methods = t.GetMethods(flags)
                    .Where(m => !m.IsSpecialName)
                    .Select(m => $"M:{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");

                Plugin.Logger.LogInfo(
                    $"[GameStateReader] GameVariables fields:  {string.Join(" | ", fields)}");
                Plugin.Logger.LogInfo(
                    $"[GameStateReader] GameVariables props:   {string.Join(" | ", props)}");
                Plugin.Logger.LogInfo(
                    $"[GameStateReader] GameVariables methods: {string.Join(" | ", methods)}");

                // Dump every public instance property — for a default instance, then
                // for an instance at each difficulty value.  If the dumps differ,
                // setting `difficulty` auto-populates the ~20 settings (a preset);
                // if identical, the preset is applied elsewhere (the new-game menu).
                var dumpProps = t.GetProperties(instFlags);
                void DumpInstance(string label, GameVariables gv)
                {
                    var parts = new List<string>();
                    foreach (var p in dumpProps)
                    {
                        try { parts.Add($"{p.Name}={p.GetValue(gv)}"); } catch { }
                    }
                    Plugin.Logger.LogInfo($"[GameStateReader] GV[{label}]: {string.Join(", ", parts)}");
                }

                DumpInstance("default", new GameVariables());

                var diffProp = t.GetProperty("difficulty", instFlags);
                if (diffProp != null && diffProp.PropertyType.IsEnum && diffProp.CanWrite)
                {
                    foreach (var dv in Enum.GetValues(diffProp.PropertyType))
                    {
                        try
                        {
                            var gv = new GameVariables();
                            diffProp.SetValue(gv, dv);
                            DumpInstance($"difficulty={dv}", gv);
                        }
                        catch (Exception ex)
                        {
                            Plugin.Logger.LogWarning($"[GameStateReader] difficulty {dv}: {ex.Message}");
                        }
                    }
                }
                else
                {
                    Plugin.Logger.LogWarning(
                        "[GameStateReader] 'difficulty' not an enum/writable property — preset probe skipped.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[GameStateReader] ProbeGameVariables: {ex.Message}");
            }
        }

        // ── GameInstance time-skip probe ──────────────────────────────────────
        // Discovery probe to find the method the bench/bed/car rest uses to skip
        // time.  The skip advances GameInstance's Day/Hour/Minute directly (not via
        // GameSpeedController), so the responsible method is on GameInstance or
        // something it owns.  This logs every time/skip/boost-related member name
        // so we can identify the method to intercept for a zero-overshoot fix.

        private static bool _giProbed;

        private static bool RelevantTimeName(string n)
        {
            n = n.ToLowerInvariant();
            return n.Contains("time")   || n.Contains("day")     || n.Contains("hour")  ||
                   n.Contains("minute") || n.Contains("boost")   || n.Contains("temporal") ||
                   n.Contains("skip")   || n.Contains("rest")    || n.Contains("sleep") ||
                   n.Contains("fast")   || n.Contains("forward") || n.Contains("advance") ||
                   n.Contains("wait")   || n.Contains("clock")   || n.Contains("tick");
        }

        public static void ProbeGameInstance()
        {
            if (_giProbed) return;
            var gi = SaveGameManager.Current;
            if (gi == null) return;          // not in-game yet — retry on next call
            _giProbed = true;
            try
            {
                var t = gi.GetType();
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                var methods = t.GetMethods(flags)
                    .Where(m => !m.IsSpecialName && RelevantTimeName(m.Name))
                    .Select(m => $"M:{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");
                var props = t.GetProperties(flags)
                    .Where(p => RelevantTimeName(p.Name))
                    .Select(p => $"P:{p.Name}({p.PropertyType.Name})");

                Plugin.Logger.LogInfo(
                    $"[GameStateReader] GameInstance time/skip methods: {string.Join(" | ", methods)}");
                Plugin.Logger.LogInfo(
                    $"[GameStateReader] GameInstance time/skip props:   {string.Join(" | ", props)}");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[GameStateReader] ProbeGameInstance: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns the live values of all GameInstance boost/temporal/skip/fast-forward
        /// properties — used to capture what the bench rest mechanism actually set when
        /// a clock-rate skip is detected.
        /// </summary>
        public static string DumpGameInstanceTimeState()
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return "(no GameInstance)";
                var t = gi.GetType();
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                var parts = new List<string>();
                foreach (var p in t.GetProperties(flags))
                {
                    var n = p.Name.ToLowerInvariant();
                    if (!(n.Contains("boost") || n.Contains("temporal") || n.Contains("skip") ||
                          n.Contains("fast")  || n.Contains("forward")))
                        continue;
                    try { parts.Add($"{p.Name}={p.GetValue(gi)}"); } catch { }
                }
                return parts.Count > 0 ? string.Join(", ", parts) : "(no boost/skip props)";
            }
            catch (Exception ex)
            {
                return $"(error: {ex.Message})";
            }
        }
    }

    // ── Plain DTO for JSON serialisation (no IL2CPP dependency) ──────────────

    public class MarketEntryDto
    {
        public string ItemName         { get; set; } = "";
        public float  ImportPriceIndex { get; set; }
    }
}
