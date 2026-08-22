// PROBE-START: P-SHAREDSTAFF-LIST — why the owner's people are not shown/tinted in My Employees
// Registered in .modding/04-probes.md. TEMPORARY: delete this whole file when the question is answered.
//
// Question (field 2026-08-22): the client received the owner's bench ("bench of 'melaus' applied: +24") but the user
// saw no teal names in My Employees. Every branch below produces the SAME silence in the existing logs, so reading
// cannot separate them:
//   (a) the My Employees list was never built on that machine (app not opened there);
//   (b) it was built, but the global employee-query filter stripped the owner's records (our ListScope exemption
//       failing) — zero rows of theirs;
//   (c) it was built and their rows were present, but the tint postfix never ran or found no name component;
//   (d) it was built, rows present, tint applied — and what the user looked at was something else.
// One line per list build answers all four.
using System;
using System.Collections.Generic;
using Entities;
using HarmonyLib;
using UI.Smartphone.Apps.MyEmployees;
using UnityEngine;

namespace BigAmbitionsMP
{
    public static class ProbeSharedStaffList
    {
        private static int _tintSeen, _tintApplied, _tintNoName;
        private static int _builds;

        /// <summary>Counted by SharedShopStaff's row-tint postfix (see the PROBE lines there).</summary>
        public static void NoteRow(bool grant, bool hasName)
        {
            _tintSeen++;
            if (!hasName) _tintNoName++;
            else if (grant) _tintApplied++;
        }

        /// <summary>Runs AFTER every other postfix on PopulateAllModels (the injected-staff hide filter included), so
        /// the model list it counts is exactly what the screen will show.</summary>
        [HarmonyPatch(typeof(EmployeeScrollerController), "PopulateAllModels")]
        [HarmonyPriority(Priority.Last)]
        public static class Probe_EmployeeList_Build
        {
            static void Postfix(List<EmployeeModel> allModels)
            {
                try
                {
                    _builds++;
                    int injected = 0, bench = 0, grantOwned = 0;
                    var gi = SaveGameManager.Current;
                    if (gi?.EmployeeInstances != null)
                        foreach (var e in gi.EmployeeInstances)
                        {
                            if (e == null || string.IsNullOrEmpty(e.id)) continue;
                            if (!MPRegisterSync.IsInjectedStaff(e.id)) continue;
                            injected++;
                            if (MPRegisterSync.IsInjectedUnassigned(e.id)) bench++;
                            if (SharedShopStaff.IsFromGrantOwner(e.id)) grantOwned++;
                        }
                    int modelsGrant = 0, modelsInjected = 0;
                    if (allModels != null)
                        foreach (var m in allModels)
                        {
                            if (m == null || string.IsNullOrEmpty(m.employeeId)) continue;
                            if (MPRegisterSync.IsInjectedStaff(m.employeeId)) modelsInjected++;
                            if (SharedShopStaff.IsFromGrantOwner(m.employeeId)) modelsGrant++;
                        }
                    Plugin.Logger.LogWarning(
                        $"[PROBE] MyEmployees list build #{_builds}: rows={allModels?.Count ?? -1} "
                        + $"(injected in list={modelsInjected}, of a granting owner={modelsGrant}) | "
                        + $"records on this machine: injected={injected} bench={bench} fromGrantOwner={grantOwned} | "
                        + $"listScope={SharedShopStaff.ListScope} sharedShops={GrantSync.SharedManageCount} "
                        + $"pid='{MPConfig.PlayerId}' | rows drawn since the app opened: seen={_tintSeen} teal={_tintApplied} noNameComponent={_tintNoName} "
                        + "(rows are drawn AFTER this line — read the counts on the next build, or scroll the list and reopen)");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[PROBE] MyEmployees list build: {ex.Message}"); }
            }
        }

        /// <summary>Was the app opened here at all? One line per open, so branch (a) is never a guess.</summary>
        [HarmonyPatch(typeof(MyEmployees), "OnEnable")]
        public static class Probe_MyEmployees_Open
        {
            static void Postfix()
            {
                try
                {
                    _tintSeen = _tintApplied = _tintNoName = 0; _builds = 0;
                    Plugin.Logger.LogWarning($"[PROBE] My Employees opened on '{MPConfig.PlayerId}' (shared shops: {GrantSync.SharedManageCount}).");
                }
                catch { }
            }
        }
    }
}
// PROBE-END: P-SHAREDSTAFF-LIST
