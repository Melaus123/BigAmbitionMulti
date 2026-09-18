using System;
using HarmonyLib;
using Helpers;
using TMPro;

namespace BigAmbitionsMP
{
    /// <summary>PERSONA-COUNT-1 (player bundle 20260908-002008, 2026-09-17): the Persona page's "employees" tile
    /// read 548 for a host with fewer than 100. The game counts <c>EmployeeHelper.EmployeeInstancesDictionary</c>
    /// (UI.Smartphone.Apps.Persona/CharacterInfo.cs:48, every non-candidate), and that DICTIONARY holds every
    /// partner-staff copy the roster sync injects (MPRegisterSync.cs:2220) and every duty stand-in (:1374) - the
    /// round-94 census filter edits only the LIST, and the round-80 query filter only the query overload, so this
    /// reader saw them all. Same predicate as the goal filter at MPPatches.cs:6291-6294: a merged partner's people
    /// stay counted (one company), everyone else's copies and every stand-in do not. A second Postfix on the same
    /// method as the business-count parity, which is the accepted shape (MPPatches.cs:6675).</summary>
    [HarmonyPatch(typeof(UI.Smartphone.Apps.Persona.CharacterInfo), "OnEnable")]
    public static class Patch_CharacterInfo_EmployeeCountParity
    {
        private static int _logged;

        static void Postfix(UI.Smartphone.Apps.Persona.CharacterInfo __instance)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return;   // vanilla outside a session
                var dict = EmployeeHelper.EmployeeInstancesDictionary;
                if (dict == null) return;
                int own = 0, hidden = 0;
                foreach (var kv in dict)
                {
                    var e = kv.Value;
                    if (e == null) continue;
                    bool candidate; try { candidate = e.IsCandidate; } catch { continue; }
                    if (candidate) continue;
                    string id = e.id ?? kv.Key ?? "";
                    if (MPRegisterSync.IsSyntheticDuty(id) || (MPRegisterSync.IsInjectedStaff(id) && !MPRegisterSync.IsInjectedFromMergedPartner(id)))
                    { hidden++; continue; }
                    own++;
                }
                if (hidden == 0) return;   // the native count was already right
                var lbl = AccessTools.Field(typeof(UI.Smartphone.Apps.Persona.CharacterInfo), "totalEmployeesLabel")?.GetValue(__instance) as TMP_Text;
                if (lbl != null) lbl.SetText(own.ToString());
                if (_logged++ < 3)
                    Plugin.Logger.LogInfo($"[Economics] persona employee count: {own} shown ({hidden} injected/stand-in record(s) not counted; PERSONA-COUNT-1).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Economics] persona employee count: {ex.Message}"); }
        }
    }
}
