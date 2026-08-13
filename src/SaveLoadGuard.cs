using System;
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>Round-245 fix A — LOAD-THROW CONTAINMENT (field 20260805-123035 "my character
    /// is welded to my friend's").  A damaged .hsg (their crash tore the file mid-compression)
    /// made a native compat pass throw INSIDE SaveGameManager.Load — the load aborted midway,
    /// and the MP session then CONTINUED on a half-initialized world: the friend joined,
    /// spawned, and the host's dead player binding latched onto the remote avatar (the
    /// "weld"; interactions dead).  One damaged file produced a whole surreal session.
    ///
    /// Containment, MP only (single-player keeps vanilla behavior): when Load throws, log
    /// LOUDLY naming the save, tell the player on screen that the FILE is damaged (their
    /// other saves are fine — the field pair recovered exactly that way), stop the MP
    /// session so nobody joins the wreck, and return to the main menu.  The exception is
    /// swallowed — we own the abort; rethrowing would leave the flow in whatever undefined
    /// state the native caller happens to produce.</summary>
    public static class SaveLoadGuard
    {
        /// <summary>The shared containment: tell the player the FILE is broken (their
        /// other saves are fine — the round-245 field pair recovered exactly that way),
        /// stop the MP session so nobody joins the wreck, and return to the main menu.
        /// Round-251 note: the PRIMARY trigger is now GuardedNativeLoad's return-value
        /// check (rig batch-2 proved native Load catches its own exceptions and returns
        /// false — the Finalizer below never saw them); the Finalizer stays as a belt
        /// for anything that genuinely escapes.</summary>
        internal static void ContainFailedLoad(string name)
        {
            try { MPCanvasUI.PostLobbyNotice($"Save '{name}' is damaged and could not be loaded. Your OTHER saves are fine — load one of those."); } catch { }
            try { PassengerHud.Toast($"Save '{name}' is damaged — returning to menu. Load a different save.", 10f); } catch { }
            try { if (MPServer.IsRunning) MPServer.Stop(); } catch { }
            try { if (MPClient.IsConnected) MPClient.Disconnect(); } catch { }
            try { UI.Load.LoadScene.LoadMainMenu(BAModAPI.ModActivationScope.City); }
            catch (Exception ex) { Plugin.Logger.LogError($"[MPSave] menu return after failed load: {ex.Message}"); }
        }

        [HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.Load),
                      typeof(SaveGameManager.SaveGameStruct), typeof(bool))]
        public static class Patch_SaveGameManager_Load_FailureContainment
        {
            static Exception? Finalizer(Exception? __exception, SaveGameManager.SaveGameStruct saveGame)
            {
                if (__exception == null) return null;
                bool inMp = MPServer.IsRunning || MPClient.IsConnected || MPClient.InMpGame;
                if (!inMp) return __exception;   // SP: vanilla behavior, exception and all

                string name = "";
                try { name = saveGame?.name ?? ""; } catch { }
                Plugin.Logger.LogError($"[MPSave] LOAD FAILED MID-LOAD for save '{name}' — the file is damaged "
                    + "(a native pass threw while reading it). Aborting to the main menu instead of continuing on a "
                    + $"half-initialized world (round-245). Root: {__exception}");
                ContainFailedLoad(name);
                return null;   // swallowed — the abort above is the single deterministic outcome
            }
        }
    }
}
