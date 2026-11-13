// PROBE-START: P-T29-OPEN
// Log-only probe for T-29 (ACTIVE 2026-11-13; all configurations; .modding/04-probes.md P-T29-OPEN).
// Question: a player's log showed the game's "Employee with ID <id> not found" and then a
// NullReferenceException in MyEmployees.ShowEmployee via DelayShowEmployee, three in-game days after
// the mod removed its copy of a fired partner employee. WHICH SCREEN asked to open the missing employee?
// DelayShowEmployee (decompile UI.Smartphone.Apps.MyEmployees/MyEmployees.cs:296-302) runs ShowEmployee
// one frame later from a coroutine, so only a prefix on DelayShowEmployee itself still has the calling
// screen on the stack; the ShowEmployee prefix covers the direct callers. Never changes the outcome.
// Remove when: a log shows the calling screen, or T-29 is closed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Entities;                                // EmployeeInstance
using HarmonyLib;
using Helpers;                                 // EmployeeHelper
using UI.Smartphone.Apps.MyEmployees;          // MyEmployees

namespace BigAmbitionsMP
{
    internal static class ProbeT29
    {
        private static readonly HashSet<string> _seen = new HashSet<string>();   // once per id per game session

        /// <summary>Logs one line when the employee is null or its id no longer resolves in the game's own
        /// lookup (EmployeeHelper.GetEmployeeById, which holds employees AND candidates). Never throws.</summary>
        internal static void Check(EmployeeInstance employeeInstance, bool fromShowEmployee)
        {
            try
            {
                string id = employeeInstance?.id;
                bool missing = employeeInstance == null
                    || EmployeeHelper.GetEmployeeById(id, false) == null;   // showError:false - add no game error line
                if (!missing) return;
                var frames = new StackTrace().GetFrames();
                // A ShowEmployee run by DelayShowEmployee's one-frame callback: the DelayShowEmployee prefix
                // already logged it with the calling screen on the stack; this stack is only the coroutine.
                if (fromShowEmployee && frames != null)
                    foreach (var f in frames)
                    {
                        var m = f?.GetMethod();
                        if (m != null && m.Name.Contains("DelayShowEmployee")) return;
                    }
                if (!_seen.Add(id ?? "null")) return;
                Plugin.Logger.LogWarning($"[PROBE T29-OPEN] employee screen asked to open a missing employee id='{id ?? "null"}' caller={Caller(frames)}");
            }
            catch { }
        }

        /// <summary>The first 6 frames outside Harmony and the mod (and Harmony's patch wrappers), as Type.Method.</summary>
        private static string Caller(StackFrame[] frames)
        {
            var sb = new StringBuilder();
            int n = 0;
            Assembly mod = typeof(ProbeT29).Assembly, harmony = typeof(Harmony).Assembly;
            if (frames != null)
                foreach (var f in frames)
                {
                    if (n >= 6) break;
                    MethodBase m = f?.GetMethod();
                    if (m == null || m.Name.StartsWith("DMD<")) continue;
                    var t = m.DeclaringType;
                    if (t == null || t.Assembly == mod || t.Assembly == harmony) continue;
                    while (t.DeclaringType != null && t.Name.StartsWith("<")) t = t.DeclaringType;   // closure -> its screen
                    if (n > 0) sb.Append(" < ");
                    sb.Append(t.Name).Append('.').Append(m.Name);
                    n++;
                }
            return n == 0 ? "(none)" : sb.ToString();
        }
    }

    [HarmonyPatch(typeof(MyEmployees), nameof(MyEmployees.DelayShowEmployee))]
    public static class Patch_ProbeT29_DelayShowEmployee
    {
        static void Prefix(EmployeeInstance employeeInstance) { ProbeT29.Check(employeeInstance, false); }
    }

    [HarmonyPatch(typeof(MyEmployees), nameof(MyEmployees.ShowEmployee))]
    public static class Patch_ProbeT29_ShowEmployee
    {
        static void Prefix(EmployeeInstance employeeInstance) { ProbeT29.Check(employeeInstance, true); }
    }
}
// PROBE-END: P-T29-OPEN
