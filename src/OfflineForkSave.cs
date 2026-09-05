using System;
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>H-FORK-1 r2 (review r1 #2): in the OFFLINE FORK (host lost, player kept playing) saves go through the game's
    /// own path — CheckAutoSave and the pause-menu save fall through to vanilla by design ("saves from now on go to your
    /// single-player games"). The MP save path (MPSaveCoordinator.PerformLocalSave) strips the MP-only runtime objects
    /// around the write and restores them after; the native path never did, so every fork save wrote other players'
    /// employee records, BAMP_DUTY_ stand-ins, synthetic player rival states and ghost vehicles into the player's
    /// single-player save. Same strips, same order, same restore — only when OfflineFork is set and no MP path is live
    /// (the coordinator's own call to Save runs with OfflineFork false, so this never double-applies).</summary>
    [HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.Save), typeof(SaveGameManager.SaveType), typeof(string), typeof(string))]
    public static class Patch_SaveGameManager_Save_OfflineForkStrips
    {
        private sealed class State
        {
            public Action? RestoreSynthetics;
            public string GhostActiveId = "";
            public bool Veiled;
        }

        static void Prefix(out object? __state)
        {
            __state = null;
            try
            {
                if (!MPClient.OfflineFork || MPServer.IsRunning || MPClient.IsClientInWorld) return;
                var st = new State();
                try { GameStatePatcher.StripGhostVehicles("fork-save"); } catch (Exception ex) { Plugin.Logger.LogWarning($"[ForkSave] ghost-vehicle strip: {ex.Message}"); }
                try { GameStatePatcher.StripSyntheticRivalStates("fork-save"); } catch (Exception ex) { Plugin.Logger.LogWarning($"[ForkSave] rival-state strip: {ex.Message}"); }
                try { st.RestoreSynthetics = MPRegisterSync.StripSyntheticsForSave("fork-save"); } catch (Exception ex) { Plugin.Logger.LogWarning($"[ForkSave] synthetics strip: {ex.Message}"); }
                try { MergerFlip.VeilPush(); st.Veiled = true; } catch (Exception ex) { Plugin.Logger.LogWarning($"[ForkSave] veil push: {ex.Message}"); }
                try
                {
                    var gi = SaveGameManager.Current;
                    string av = gi?.ActiveVehicleId ?? "";
                    if (av.StartsWith("BAMP_", StringComparison.Ordinal) && !av.StartsWith("BAMP_TESTRIG", StringComparison.Ordinal))
                    {
                        st.GhostActiveId = av;
                        gi!.ActiveVehicleId = null;
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[ForkSave] ActiveVehicleId strip: {ex.Message}"); }
                __state = st;
                Plugin.Logger.LogInfo("[ForkSave] offline-fork save: MP-only runtime objects stripped for the write (restored after) (H-FORK-1 r2).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[ForkSave] prefix: {ex.Message}"); }
        }

        static Exception? Finalizer(Exception? __exception, object? __state)
        {
            try
            {
                if (__state is State st)
                {
                    try { st.RestoreSynthetics?.Invoke(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[ForkSave] synthetics restore: {ex.Message}"); }
                    try { if (st.Veiled) MergerFlip.VeilPop(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[ForkSave] veil pop: {ex.Message}"); }
                    try { if (st.GhostActiveId.Length > 0 && SaveGameManager.Current != null) SaveGameManager.Current.ActiveVehicleId = st.GhostActiveId; } catch (Exception ex) { Plugin.Logger.LogWarning($"[ForkSave] ActiveVehicleId restore: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[ForkSave] finalizer: {ex.Message}"); }
            return __exception;   // never swallow the game's own save exception
        }
    }
}
