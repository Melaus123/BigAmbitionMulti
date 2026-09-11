using System;
using System.Collections.Generic;
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>H-SWAP-1 (2026-09-10): the host's DAILY AI COMPETITION PASS evicts CLIENT tenants.
    /// On the host another player's rented building reads RentedByPlayer == FALSE (that flag is the
    /// LOCAL player's own tenancy), so the vanilla passes treat it as AI/vacant stock:
    ///   - CompetitionHelper.SwapBuilding (decompile :240, called from RunDaily :97 residential and
    ///     :101 warehouse) picks a random "!AvailableForRent and !RentedByPlayer" registration (:265)
    ///     and puts it back on the rent market (:266) - a client's apartment or warehouse is listed
    ///     out from under them, and the record is published to the other clients.
    ///   - CompetitionHelper.TryShutdownBusinesses (:664) skips only RentedByPlayer registrations
    ///     (:675). The mutation itself is already blocked deeper by
    ///     Patch_BuildingRegistration_ShutDownAIBusiness_ShieldPlayers (MPPatches.cs), but the
    ///     "business closed" MarketEvent and the kill counter are written BEFORE that blocked call -
    ///     a phantom closure news item naming a client's open shop, and the AI's daily closure
    ///     budget spent on nothing.
    ///
    /// Fix (the call-scoped raise pattern the permissions effort used around the competitor scan):
    /// for the DURATION OF EACH PASS ONLY, every PLAYER-HELD registration that currently reads
    /// RentedByPlayer == false is raised to true, and restored in the Finalizer. Both passes then
    /// take their own vanilla "skip the player's building" branch. Nothing persists: the Finalizer
    /// lowers exactly the registrations this class raised, on the normal and the throwing path.
    ///
    /// Player-held = GameStatePatcher.IsAnyPlayerBusiness (session-pid stamp arm + merger flip +
    /// host ownership LEDGER arm IsLedgerReservedToPlayer, which excludes host ids) - it covers
    /// RESIDENCES as well as shops: BuildingHelper.RentBuilding is the single rent path for every
    /// building type (it branches on residential internally, decompile :694/:698), the mod's
    /// Patch_RentBuilding relays it to the host (MPPatches.cs :73 -> MPClient.RequestRentBuilding),
    /// and the host's HandleRentRequest stamps BuildingOwners[addr] = senderPid with NO type gate
    /// (MPServer.cs :3980) plus businessOwnerRivalId = pid (GameStatePatcher.HostReflectPlayerRent
    /// :5797). Host-only by construction (MPServer.IsRunning); single player is a no-op; clients
    /// never run RunDaily anyway (Patch_CompetitionHelper_RunDaily_SkipOnClient).
    ///
    /// Deliberately NOT touched: businessOwnerRivalId, POIs, MergerFlip (a flipped partner shop on a
    /// member host already reads RentedByPlayer == true, so it is simply never in the raise list),
    /// and the existing ShutDownAIBusiness shield (belt and braces).</summary>
    [HarmonyPatch]
    public static class Patch_AiPass_TenancyRaise
    {
        /// <summary>The registrations THIS class raised, in raise order - the only ones the
        /// Finalizer lowers again.</summary>
        private static readonly List<BuildingRegistration> _raised = new List<BuildingRegistration>();

        /// <summary>method name -> in-game day it last logged (one INFO line per pass per day;
        /// SwapBuilding runs many times a day, once per neighbourhood and type).</summary>
        private static readonly Dictionary<string, int> _lastLogDay = new Dictionary<string, int>();

        /// <summary>Re-entrancy depth: raise on 0 -> 1, restore on 1 -> 0. RunDaily calls the two
        /// passes sequentially, never nested, but a nested call must not double-raise or lower the
        /// outer pass's registrations halfway through it.</summary>
        private static int _depth;

        static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            List<System.Reflection.MethodBase> found = new List<System.Reflection.MethodBase>();
            try
            {
                foreach (var name in new string[] { "TryShutdownBusinesses", "SwapBuilding" })
                {
                    List<System.Reflection.MethodBase> matches = new List<System.Reflection.MethodBase>();
                    foreach (var m in typeof(Helpers.CompetitionHelper).GetMethods(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Static))
                        if (m.Name == name) matches.Add(m);
                    if (matches.Count == 1) { found.Add(matches[0]); continue; }
                    // Yield nothing rather than guess an overload: a wrong binding would raise
                    // tenancy around a method that never restores it.
                    Plugin.Logger.LogWarning($"[AiRaise] CompetitionHelper.{name}: {matches.Count} match(es), expected exactly 1 - that pass is NOT guarded, client tenancies stay exposed to it.");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[AiRaise] target scan: {ex.Message}"); }
            return found;
        }

        static void Prefix(System.Reflection.MethodBase __originalMethod)
        {
            try
            {
                if (!MPServer.IsRunning) return;      // single player / client: vanilla, untouched
                if (_depth++ != 0) return;            // nested: the outer call already raised
                _raised.Clear();
                var regs = SaveGameManager.Current?.BuildingRegistrations;
                if (regs == null) return;
                foreach (var reg in regs)
                {
                    if (reg == null) continue;
                    bool rented = false;
                    try { rented = reg.RentedByPlayer; } catch { }
                    if (rented) continue;             // the HOST's own tenancy - vanilla already spares it
                    if (!GameStatePatcher.IsAnyPlayerBusiness(reg)) continue;   // AI / unowned: leave to the sim
                    try { reg.RentedByPlayer = true; } catch { continue; }
                    _raised.Add(reg);
                }
                if (_raised.Count > 0) LogOncePerDay(__originalMethod?.Name ?? "?", _raised.Count);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[AiRaise] prefix: {ex.Message}"); }
        }

        static Exception Finalizer(Exception __exception)
        {
            try
            {
                if (_depth > 0 && --_depth == 0)
                {
                    foreach (var reg in _raised)
                        try { reg.RentedByPlayer = false; } catch { }
                    _raised.Clear();
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[AiRaise] finalizer: {ex.Message}"); }
            return __exception;   // never swallow the pass's exception
        }

        private static void LogOncePerDay(string method, int count)
        {
            try
            {
                int day = SaveGameManager.Current?.Day ?? -1;
                if (_lastLogDay.TryGetValue(method, out var last) && last == day) return;
                _lastLogDay[method] = day;
                Plugin.Logger.LogInfo($"[AiRaise] raised {count} player-held building(s) around CompetitionHelper.{method}");
            }
            catch { }
        }
    }
}
