using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Entities;          // TodoTask
using UI.Notification;   // Notifications, NotificationType
using UI.Tasks;          // TasksUI

namespace BigAmbitionsMP
{
    // PROBE-START: P-PARITY-SURFACES (this whole file is the probe — delete the file to remove it)
    /// <summary>User hands-on 2026-09-17 on a merged company: the two members' objective panels (the left task
    /// list) and their top-right notices did not match, and neither could be read fast enough to say how. Every
    /// notice goes through the one routine <c>Notifications.Show</c> (UI.Notification/Notifications.cs:26) as a
    /// language KEY plus fill-in values; every task reaches the left panel through <c>TasksUI.AddTodoTaskToUI</c>
    /// (:274) and leaves it through CompleteTodoTask (:483), InstantlyCompleteTodoTask (:554) or
    /// InstantlyCompleteListOfTasks (:531); UpdateTodoTask (:384) relabels one. This logs each, on every machine,
    /// as `[PROBE]` lines that compare across the two logs. Multiplayer worlds only; nothing on screen; caps so a
    /// noisy session cannot flood a log. Read-only: no game state is touched.</summary>
    internal static class ParityProbe
    {
        internal const int Cap = 500;
        internal static int NotifyLines, TaskLines;

        internal static bool InMpWorld
        {
            get { try { return MPServer.IsRunning || MPClient.IsClientInWorld || MPClient.OfflineFork; } catch { return false; } }
        }

        /// <summary>The first frame above the patched method that is neither Harmony's nor this probe's nor the
        /// notification class itself — the part of the game that raised the notice.</summary>
        internal static string Caller()
        {
            try
            {
                var st = new System.Diagnostics.StackTrace(false);
                for (int i = 1; i < st.FrameCount; i++)
                {
                    var m = st.GetFrame(i)?.GetMethod();
                    var t = m?.DeclaringType;
                    if (m == null || t == null) continue;
                    string full = t.FullName ?? "";
                    if (t == typeof(Notifications) || full.Contains("ParityProbe") || full.StartsWith("HarmonyLib", StringComparison.Ordinal)) continue;
                    return full + "." + m.Name;
                }
            }
            catch { }
            return "?";
        }

        internal static string Task(TodoTask t)
        {
            if (t == null) return "null";
            string addr = "-";
            try { if (t.address != null) addr = GameStateReader.AddressKey(t.address); } catch { addr = "?"; }
            return $"type={t.type} id='{t.id}' addr='{addr}' item='{t.itemName}' inst='{t.itemInstanceId}' emp='{t.employeeId}' "
                 + $"prio={t.priority}+{t.priorityOffset} days={t.remainingDays} req='{t.businessRequirement}'";
        }

        internal static void LogTask(string op, TodoTask t)
        {
            try
            {
                if (!InMpWorld) return;
                if (TaskLines++ >= Cap) return;
                Plugin.Logger.LogInfo($"[PROBE] Task {op} {Task(t)}");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Notifications), nameof(Notifications.Show))]
    internal static class ParityProbe_Notify
    {
        static void Prefix(NotificationType notificationType, string headerKey, Dictionary<string, string> notificationData,
                           string duplicateIdentifier, bool trackOnSaveGame)
        {
            try
            {
                if (!ParityProbe.InMpWorld) return;
                if (ParityProbe.NotifyLines++ >= ParityProbe.Cap) return;
                var sb = new StringBuilder();
                if (notificationData != null)
                    foreach (var kv in notificationData) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
                Plugin.Logger.LogInfo($"[PROBE] Notify {notificationType} '{headerKey}' data={{{sb}}} dup='{duplicateIdentifier}' "
                                    + $"track={trackOnSaveGame} from {ParityProbe.Caller()}");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(TasksUI), "AddTodoTaskToUI")]
    internal static class ParityProbe_TaskAdd
    {
        static void Prefix(TodoTask task) => ParityProbe.LogTask("+", task);
    }

    [HarmonyPatch(typeof(TasksUI), nameof(TasksUI.UpdateTodoTask))]
    internal static class ParityProbe_TaskUpdate
    {
        static void Prefix(TodoTask task) => ParityProbe.LogTask("~", task);
    }

    [HarmonyPatch(typeof(TasksUI), nameof(TasksUI.CompleteTodoTask))]
    internal static class ParityProbe_TaskComplete
    {
        static void Prefix(TodoTask task) => ParityProbe.LogTask("-", task);
    }

    [HarmonyPatch(typeof(TasksUI), nameof(TasksUI.InstantlyCompleteTodoTask))]
    internal static class ParityProbe_TaskInstant
    {
        static void Prefix(TodoTask task) => ParityProbe.LogTask("-instant", task);
    }

    [HarmonyPatch(typeof(TasksUI), nameof(TasksUI.InstantlyCompleteListOfTasks))]
    internal static class ParityProbe_TaskInstantList
    {
        // The game enumerates `tasks` once itself; this reads the same sequence first. Every caller passes a
        // materialised list or a pure query over the save's task list, so a second pass changes nothing.
        static void Prefix(IEnumerable<TodoTask> tasks)
        {
            try { if (tasks != null && ParityProbe.InMpWorld) foreach (var t in tasks) ParityProbe.LogTask("-list", t); }
            catch { }
        }
    }
    // PROBE-END: P-PARITY-SURFACES
}
