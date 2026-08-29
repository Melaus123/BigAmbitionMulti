// PROBE-START: P-RESTOCK-AUTHORITY — does the hourly "restock the shop you are standing in" pass
// make the right OWNERSHIP decision under multiplayer + the company merger?
//
// WHY THIS EXISTS: game 1.0 added BuildingManager.RunCurrentBuildingHourly ->
// BusinessHelper.RestockCurrentBusinessIfNeeded, which restocks the ONE building the local player
// is physically inside — the single building BusinessSimulatorHelper.RunHourly deliberately skips.
// It gates on registration.RentedByPlayer, which is exactly the flag the merger flip falsifies, so
// it was added to Patch_MergerAuthorityVeil.Steps. The veil un-flips partner buildings around the
// call, so the pass should fire on the REAL owner's machine and nowhere else.
//
// Two cases decide whether that is right, and NEITHER is visible without sitting and watching
// shelf counts for an in-game hour:
//   (i)  a HOST standing inside a partner's merger-flipped shop  -> must NOT restock it;
//   (ii) a CLIENT standing inside their OWN shop                 -> MUST restock it.
// This probe reports the decision and the reason directly, so the answer is a log line.
//
// PRIORITY MATTERS: Prefix runs at Priority.Last so it observes RentedByPlayer AFTER the veil's
// own Prefix has pushed (un-flipped). That is the value the native method will actually read —
// reading it any earlier would report the pre-veil flag and answer the wrong question.
using System;
using HarmonyLib;
using Helpers;   // BusinessTypeHelper
using UI;        // UIs (time-machine gate)

namespace BigAmbitionsMP
{
    [HarmonyPatch]
    internal static class RestockAuthorityProbe
    {
        static System.Reflection.MethodBase? TargetMethod() =>
            VehicleManager.FindGameType("Helpers.BusinessHelper")?.GetMethod(
                "RestockCurrentBusinessIfNeeded",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
              | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly);

        private static int _logs;
        private static int _nullLogs;

        [HarmonyPriority(Priority.Last)]
        static void Prefix(BuildingRegistration registration)
        {
            try
            {
                // MP-only and throttled (review 2026-08-29). The question this probe exists to answer
                // is a multiplayer/merger authority question; single-player has no second machine and
                // no flip, so a line per in-game hour spent indoors there is pure noise. The cap
                // matches every other logger added this round.
                // IsClientInWorld, NOT IsConnected (2026-08-29). MPClient.cs:93-99 states the rule: "Suppressions of native world-mutating passes, SHIELDS OVER MOD-CREATED STATE, and replica protections must gate on THIS instead of IsConnected." IsConnected goes false the instant a link drops, while the MP world - and every ghost in it - is still loaded.
                // For this probe specifically: a disconnected-but-still-in-world client STILL runs the
                // veil and STILL makes a restock decision, so gating on IsConnected would blind the
                // probe on exactly the machine whose decision is hardest to reason about.
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                string role = MPServer.IsRunning ? "HOST" : "CLIENT";

                // Null FIRST, and it does not spend budget: the 40-line cap exists to bound the
                // interesting decisions, and a null registration is not one of them. (Charging it
                // could exhaust the budget on no-ops before either target case ever occurred.)
                if (registration == null)
                {
                    if (_nullLogs++ < 2)
                        Plugin.Logger.LogInfo($"[PROBE] restock ({role}): registration NULL — native will no-op.");
                    return;
                }
                if (_logs++ >= 40) return;

                string key = "";
                try { key = GameStateReader.AddressKey(registration); } catch { }

                bool flipped = false;
                try { flipped = MergerFlip.IsFlipped(key); } catch { }

                // The three gates the native method tests, in its own order.
                bool rented = false;
                try { rented = registration.RentedByPlayer; } catch { }

                bool timeMachine = false;
                try { timeMachine = InstanceBehavior<UIs>.Instance.timeMachine.isRunning; } catch { }

                bool retail = false;
                string typeName = "?";
                try
                {
                    var data = BusinessTypeHelper.GetData(registration);
                    typeName = registration.businessTypeName ?? "?";
                    retail = data != null && data.spawnCustomers
                          && data.simulator is Buildings.Retail.Simulation.RetailBusinessSimulator;
                }
                catch { }

                string verdict =
                      timeMachine ? "NO — time machine running"
                    : !rented     ? "NO — RentedByPlayer is false at call time (this machine is not the owner)"
                    : !retail     ? "NO — not a customer-spawning retail business"
                    :               "YES — will restock";

                Plugin.Logger.LogInfo(
                    $"[PROBE] restock ({role}): '{registration.BusinessName}' @ {key} type={typeName} "
                  + $"| RentedByPlayer(post-veil)={rented} mergerFlipped={flipped} "
                  + $"nativeBizOwner='{registration.businessOwnerRivalId}' deedOwner='{registration.buildingOwnerRivalId}' "
                  + $"| RESTOCK: {verdict}");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[PROBE] restock probe: {ex.Message}");
            }
        }
    }
}
// PROBE-END: P-RESTOCK-AUTHORITY
