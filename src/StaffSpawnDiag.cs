using System;
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>Report 20260818-144517 ("can't see the employees of any other business,
    /// including NPC businesses" — 3-player group, client role): the bug does NOT
    /// reproduce on the rig (the group's own save staffed every shop correctly here,
    /// visually confirmed), and their submitted logs cannot contain the failure moment
    /// (the reporter never entered a building during the logged session — all 31
    /// location-stamped lines read mine='').  So the instrument must travel:
    /// RELEASE-COMPILED by design (user-approved 2026-08-18) — one compact line per
    /// building entry recording everything the native staffing decision saw, plus a
    /// WARN tripwire for the impossible state.  Quiet on healthy machines: once per
    /// address per launch, and only in MP.
    ///
    /// 5-point checklist (2026-08-18): this method already carries our
    /// Patch_NoAiStaffInPlayerShops PREFIX (MPPatches:5880 — skips native staffing
    /// inside session players' shops).  A postfix runs even when a prefix skips the
    /// original, so this line prints for suppressed entries too and SAYS so
    /// (ourSuppression=True) — and the loader's "targets shared with an earlier
    /// class" annotation for this pairing is expected and benign.  No mod code calls
    /// the target.  MP-gated.  A postfix cannot absorb native exceptions; this one's
    /// own body is fully try/caught.</summary>
    public static class StaffSpawnDiag
    {
        private static readonly System.Collections.Generic.HashSet<string> _logged = new();

        [HarmonyPatch(typeof(BuildingManager), "SetupAiEmployeeStations")]
        public static class Patch_StaffSpawn_EntryDiag
        {
            static void Postfix(BuildingManager __instance)
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                try
                {
                    var reg = __instance.buildingRegistration;
                    if (reg == null) return;
                    string addr = GameStateReader.AddressKey(reg);
                    if (string.IsNullOrEmpty(addr) || !_logged.Add(addr)) return;   // once per address per launch

                    bool playerOwned = false, ourSuppression = false, ignoreTag = false;
                    try { playerOwned = reg.RentedByPlayer; } catch { }
                    try { ourSuppression = GameStatePatcher.IsAnyPlayerBusiness(reg); } catch { }
                    try { ignoreTag = Helpers.BusinessTypeHelper.GetData(reg)?.HasTag(BigAmbitions.Tags.TagRef.Businesstag.ignorespawnaibusinessemployees) ?? false; } catch { }
                    string layout = "?";
                    try { layout = reg.Layout == null ? "NULL" : (reg.Layout.Length == 0 ? "empty" : "set"); } catch { }

                    // Count what the native pass had to work with: stations under the
                    // interior item container AND the loaded business layout (the same
                    // two sources SetupAiEmployeeStations walks).  One-shot per entry —
                    // player-paced, not a hot path.
                    int stations = 0, assigned = 0;
                    try
                    {
                        void Count(UnityEngine.Transform root)
                        {
                            if (root == null) return;
                            foreach (var st in root.GetComponentsInChildren<EmployeeStationController>(true))
                            { if (st == null) continue; stations++; if (st.employee != null || st.employeeInstance != null) assigned++; }
                        }
                        Count(__instance.IndoorItemContainer);   // 1.0 port: property — Hamptons redirect
                        Count(__instance.currentLayout);
                    }
                    catch { stations = -1; }

                    string line = $"[StaffSpawn] '{addr}' type={reg.businessTypeName} stations={stations} assigned={assigned} layout={layout} playerOwned={playerOwned} ourSuppression={ourSuppression} ignoreTag={ignoreTag}";
                    if (stations > 0 && assigned == 0 && !playerOwned && !ourSuppression && !ignoreTag)
                        Plugin.Logger.LogWarning(line + " — STAFF-SPAWN ANOMALY: every known gate open yet nothing staffed (report 20260818-144517 class; needs rooting).");
                    else
                        Plugin.Logger.LogInfo(line);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffSpawn] entry diag: {ex.Message}"); }
            }
        }
    }
}
