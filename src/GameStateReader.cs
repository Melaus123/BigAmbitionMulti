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
        // EA 0.11 (Mono): GameInstance.Day (int), .Hour (int), .Minute (float)
        // are public FIELDS — read/written directly (the 0.10 reflection probe
        // looked for properties and would miss them entirely).

        /// <summary>
        /// Returns the current in-game day and fractional hour (0–24) from the host's GameInstance.
        /// </summary>
        public static (int day, float hourOfDay) GetGameTime()
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return (0, 0f);
                return (gi.Day, gi.Hour + gi.Minute / 60f);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[GameStateReader] GetGameTime: {ex.Message}");
                return (0, 0f);
            }
        }

        /// <summary>
        /// Attempts to apply a day/time value to the local GameInstance.
        /// </summary>
        public static void SetGameTime(int day, float hourOfDay)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return;

                // hourOfDay is fractional (Hour + Minute/60).  Hour is an int
                // field, Minute a float field — split before writing.
                int   hourInt = (int)hourOfDay;
                float minute  = (hourOfDay - hourInt) * 60f;
                if (hourInt < 0)  hourInt = 0;
                if (hourInt > 23) hourInt = 23;
                if (minute  < 0f) minute  = 0f;

                if (gi.Day  != day)     gi.Day  = day;
                if (gi.Hour != hourInt) gi.Hour = hourInt;
                gi.Minute = minute;
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
        // EA 0.11 (Mono): Paused is a real PROPERTY; isFastForwarding,
        // playerGameSpeed and isTimeControlDisabled are public FIELDS and
        // currentGameSpeed is a private field — resolved property-or-field
        // via MPReflect (the 0.10 interop layer exposed them all as properties).

        private static bool _gscProbed;
        private static Type? _gscType;
        private static object? _gscInstance;       // captured by Harmony postfix
        private static MemberInfo? _gscPPaused;        // "Paused" (bool property)
        private static MemberInfo? _gscPIsFastFwd;     // "isFastForwarding" (bool field)
        private static MemberInfo? _gscPPlayerSpd;     // "playerGameSpeed" (GameSpeed field)
        private static MemberInfo? _gscPCurrentSpd;    // "currentGameSpeed" (GameSpeed field, private)
        private static MemberInfo? _gscPTimeCtrl;      // "isTimeControlDisabled" (bool field)

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
                if (_gscPPaused    != null) parts.Add($"Paused={MPReflect.Get(_gscPPaused, _gscInstance)}");
                if (_gscPIsFastFwd != null) parts.Add($"isFastFwd={MPReflect.Get(_gscPIsFastFwd, _gscInstance)}");
                if (_gscPPlayerSpd != null) parts.Add($"playerSpd={MPReflect.Get(_gscPPlayerSpd, _gscInstance)}");
                if (_gscPCurrentSpd!= null) parts.Add($"currentSpd={MPReflect.Get(_gscPCurrentSpd, _gscInstance)}");
                if (_gscPTimeCtrl  != null) parts.Add($"timeCtrlDisabled={MPReflect.Get(_gscPTimeCtrl, _gscInstance)}");
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
            try { return (bool)(MPReflect.Get(_gscPPaused, _gscInstance) ?? false); }
            catch { return false; }
        }

        private static System.Reflection.MethodBase? _gscMTogglePause;

        /// <summary>Drive the game's REAL pause (red screen border, pulsing pause
        /// button, player movement stops) to match <paramref name="paused"/>.
        /// Used by the mod's freeze states (startup hold, disconnect pause,
        /// host-loss notice) so they read as a true pause instead of a silent
        /// clock stop.  MAIN THREAD ONLY (IL2CPP).  No-ops gracefully if the
        /// controller can't be resolved — the timeScale clamps still hold.</summary>
        // Round-36c ("both stuck paused after re-host", dump-proven): the 0.25s rate limiter below
        // SILENTLY DROPPED a genuine transition (hold-true and release-false landed within the window on
        // a fast re-host) — the flag stayed paused while timeScale ran. The DESIRED state now persists
        // here and TickPendingNativePause re-applies it every frame until the flag matches; the limiter
        // keeps throttling the actual toggles (its anti-tug-of-war job) but can no longer lose the intent.
        private static bool? _pendingNativePause;

        /// <summary>Call once per frame (MPCanvasUI.Update): converge the native pause flag onto the last
        /// requested state — survives rate-limit drops, late GSC discovery, and scene churn.</summary>
        public static void TickPendingNativePause()
        {
            try
            {
                if (_pendingNativePause == null) return;
                SetNativePause(_pendingNativePause.Value);   // no-op → clears pending; limiter throttles retries
            }
            catch { }
        }

        public static void SetNativePause(bool paused)
        {
            try
            {
                _pendingNativePause = paused;   // remember the INTENT first — the tick converges on it
                EnsureGSCProbed();
                if (_gscType == null) return;

                // The Harmony postfix caches the instance on the first pause
                // press, but our holds usually fire before any press — discover
                // it (and re-discover if the cached one died with its scene).
                // Round-39 (half-pause on new game, log-proven): the old check `uo != null && uo == null`
                // was a contradiction — Unity's overloaded == makes both operands agree, so a DESTROYED
                // cached instance was never detected. Every scene reload (new game / rejoin) then invoked
                // TogglePause on the corpse: it mutated managed fields (incl. writing LastPlayerPause into
                // the LIVE save) and threw at the first destroyed-Unity-object touch — host got a
                // half-executed pause + silent false-convergence off the corpse's stale flag, the guest's
                // convergence tick retried the corpse forever. The `is` pattern keeps the managed reference
                // alive past the cast so the Unity lifetime check actually runs.
                bool dead = false;
                try { dead = _gscInstance is UnityEngine.Object uo && uo == null; } catch { }
                if (_gscInstance == null || dead)
                {
                    // Direct singleton first (perf pass 2026-06-12): UIs.gameSpeed
                    // IS the GameSpeedController — no object-table walk needed.
                    try { _gscInstance = UI.UIs.Instance?.gameSpeed; } catch { }
                    if (_gscInstance == null)
                    {
                        var arr = UnityEngine.Object.FindObjectsOfType(_gscType, true);
                        if (arr != null && arr.Length > 0)
                            _gscInstance = arr[0];   // Mono: the found object IS the typed instance
                    }
                }
                if (_gscInstance == null) return;
                if (GetGSCPaused() == paused) { _pendingNativePause = null; return; }   // already in the wanted state

                _gscMTogglePause ??= _gscType.GetMethod("TogglePause",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_gscMTogglePause == null) return;
                // Rate limit: a state fight must never spin at frame rate (the
                // 2026-06-10 pause tug-of-war froze the host).
                if (UnityEngine.Time.realtimeSinceStartup - _lastPauseToggleAt < 0.25f) return;
                _lastPauseToggleAt = UnityEngine.Time.realtimeSinceStartup;
                AllowNativePauseCall = true;            // our key through the MP pause-suppression patches
                try { _gscMTogglePause.Invoke(_gscInstance, null); }
                finally { AllowNativePauseCall = false; }
                _pendingNativePause = null;             // applied — stop converging
                Plugin.Logger.LogInfo($"[GSC] Native pause → {paused}.");
            }
            catch (Exception ex)
            {
                // TargetInvocationException hides the real error — surface the inner one (round-39: the
                // opaque outer message alone cost a diagnosis pass).
                var inner = (ex as System.Reflection.TargetInvocationException)?.InnerException;
                Plugin.Logger.LogWarning($"[GSC] SetNativePause({paused}): {(inner ?? ex).Message}");
            }
        }

        /// <summary>True while OUR code is intentionally driving the pause state —
        /// the only key through the MP blanket pause-suppression patches.</summary>
        public static bool AllowNativePauseCall;
        private static float _lastPauseToggleAt;

        /// <summary>Watchdog (rest-v3): nothing may freeze time in MP outside the
        /// explicit pause/vote systems.  Clears BOTH freeze switches: the pause
        /// flag AND the skip engine's "time control disabled" lock — the second
        /// one is what kept hard-locking sessions (2026-06-10).</summary>
        public static void EnsureTimeNotLocked()
        {
            try
            {
                if (GetGSCPaused()) SetNativePause(false);
                if (GetGSCTimeControlDisabled())
                {
                    var m = _gscType?.GetMethod("DisableTimeControl",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (m != null && _gscInstance != null)
                    {
                        AllowNativePauseCall = true;
                        try { m.Invoke(_gscInstance, new object[] { false }); }
                        finally { AllowNativePauseCall = false; }
                        Plugin.Logger.LogWarning("[GSC] WATCHDOG: time-control lock cleared (nothing may freeze time outside the vote system).");
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[GSC] EnsureTimeNotLocked: {ex.Message}"); }
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
            try { return (bool)(MPReflect.Get(_gscPIsFastFwd, _gscInstance) ?? false); }
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
            try { return (bool)(MPReflect.Get(_gscPTimeCtrl, _gscInstance) ?? false); }
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
            _gscPPaused     = MPReflect.PropertyOrField(_gscType, "Paused",           instFlags);
            _gscPIsFastFwd  = MPReflect.PropertyOrField(_gscType, "isFastForwarding", instFlags);
            _gscPPlayerSpd  = MPReflect.PropertyOrField(_gscType, "playerGameSpeed",  instFlags);
            _gscPCurrentSpd = MPReflect.PropertyOrField(_gscType, "currentGameSpeed", instFlags);
            _gscPTimeCtrl   = MPReflect.PropertyOrField(_gscType, "isTimeControlDisabled", instFlags);

            Plugin.Logger.LogInfo(
                $"[GameStateReader] GSC cache: Paused={_gscPPaused?.Name ?? "null"} " +
                $"isFastFwd={_gscPIsFastFwd?.Name ?? "null"} " +
                $"playerSpd={_gscPPlayerSpd?.Name ?? "null"} " +
                $"currentSpd={_gscPCurrentSpd?.Name ?? "null"}");

            // SAFETY VALIDATION (anti-pattern Class 3): Paused / isFastForwarding / isTimeControlDisabled feed
            // the time-freeze watchdog (EnsureTimeNotLocked). If a game update renames any, MPReflect returns
            // null and every watchdog getter silently reads false → the watchdog believes time is NEVER locked
            // and stops clearing real locks (re-opening the 2026-06-10 hard-lock). Fail LOUDLY so a version bump
            // surfaces HERE, at load, instead of as a frozen session in the field.
            if (_gscPPaused == null || _gscPIsFastFwd == null || _gscPTimeCtrl == null)
                Plugin.Logger.LogError(
                    "[GameStateReader] CRITICAL: a GameSpeedController time-watchdog member failed to resolve " +
                    $"(Paused={_gscPPaused?.Name ?? "NULL"}, isFastForwarding={_gscPIsFastFwd?.Name ?? "NULL"}, " +
                    $"isTimeControlDisabled={_gscPTimeCtrl?.Name ?? "NULL"}) — the time-freeze watchdog is DISABLED " +
                    "and sessions may hard-lock. A game update likely renamed a field; re-map it in EnsureGSCProbed.");
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
                    var dto = new MarketEntryDto
                    {
                        ItemName         = e.itemName.ToString(),
                        ImportPriceIndex = e.importPriceIndex,
                    };
                    if (e.demandValues != null)
                        for (int d = 0; d < e.demandValues.Count; d++)
                        {
                            var nd = e.demandValues[d];
                            if (nd == null) continue;
                            dto.DemandValues.Add(new NeighborhoodDemandDto
                            {
                                Neighborhood             = nd.neighborhood ?? "",
                                Demand                   = nd.demand,
                                Providers                = nd.providers,
                                LastDaySold              = nd.lastDaySold,
                                LastDayProvidersExceeded = nd.lastDayProvidersExceeded,
                                HasPlayerMonopoly        = nd.hasPlayerMonopoly,
                            });
                        }
                    entries.Add(dto);
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(entries);
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
        /// <summary>Per-neighborhood demand state — the core sales driver. Synced so a client whose AI economy
        /// is host-authoritative reads the host's demand instead of a stale/divergent local copy.</summary>
        public List<NeighborhoodDemandDto> DemandValues { get; set; } = new();
    }

    public class NeighborhoodDemandDto
    {
        public string Neighborhood             { get; set; } = "";
        public int    Demand                   { get; set; }
        public int    Providers                { get; set; } = -1;
        public int    LastDaySold              { get; set; }
        public int    LastDayProvidersExceeded { get; set; }
        public bool   HasPlayerMonopoly        { get; set; }
    }
}
