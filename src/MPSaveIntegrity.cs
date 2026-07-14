using System;
using System.Collections.Generic;

namespace BigAmbitionsMP
{
    /// <summary>Load-time save-integrity sweep (campaign green-lit 2026-07-12):
    /// the recurring failure class here is a DANGLING SAVE REFERENCE (item name,
    /// employee id, rival id) hitting an unguarded native lookup inside a loop —
    /// one bad record kills a whole subsystem (payroll runaway, office-staff
    /// freeze, "New Text" shifts, customer-less stores).  This sweep fixes the
    /// data ONCE at world-ready instead of shielding every consumer: repair only
    /// where provably native-legal (argument documented per class), detect-only
    /// otherwise.  Wide net, quiet logs: silent on healthy saves; every action
    /// logged and summarized into bug reports (IntegrityFindings) so the field
    /// tells us which classes recur and which producers to hunt upstream.</summary>
    public static class MPSaveIntegrity
    {
        /// <summary>Per-class counts from the latest sweep ("" = clean) —
        /// stamped into report.md next to PatchIssues.</summary>
        public static string LastSummary = "";

        private const int LogCapPerClass = 10;

        public static void RunSweep(string reason)
        {
            var parts = new List<string>();
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return;

                // ── Class 1: item-names (REPAIR) ─────────────────────────────
                // reg.cachedAvailableProducts entries whose item no longer
                // resolves (content mod removed).  Native-legal removal: an
                // unresolvable item cannot be sold, and the game's own
                // RemoveSeasonalItems exists to remove unsellable entries —
                // which is exactly where the 2026-07-12 report NRE'd 3,467
                // times, silently killing a store's customer spawning.
                int itemFixed = 0, itemLogged = 0;
                try
                {
                    if (gi.BuildingRegistrations != null)
                        foreach (var reg in gi.BuildingRegistrations)
                        {
                            var list = reg?.cachedAvailableProducts;
                            if (list == null || list.Count == 0) continue;
                            for (int i = list.Count - 1; i >= 0; i--)
                            {
                                string name = list[i];
                                bool dead;
                                try { dead = string.IsNullOrEmpty(name) || BigAmbitions.Items.ItemsGetter.GetByName(name) == null; }
                                catch { dead = true; }
                                if (!dead) continue;
                                list.RemoveAt(i);
                                itemFixed++;
                                if (itemLogged++ < LogCapPerClass)
                                    Plugin.Logger.LogWarning($"[Integrity] {reason}: '{reg!.BusinessName}' sale cache referenced '{name}' — unresolvable (content mod removed?), removed.");
                            }
                        }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Integrity] item-names: {ex.Message}"); }
                if (itemFixed > 0) parts.Add($"item-names×{itemFixed} repaired");

                // ── Classes 2+3: duty shifts (REPAIR) + real-id shift orphans
                // (DETECT) — the existing zero-false-positive sweep; its
                // wider-net pass logs [ScheduleDiag] details for real ids.
                int dutyFixed = 0;
                try { dutyFixed = MPRegisterSync.RepairOrphanDutyShifts(reason); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Integrity] duty-shifts: {ex.Message}"); }
                if (dutyFixed > 0) parts.Add($"duty-shifts×{dutyFixed} repaired");
                int realOrphans = MPRegisterSync.LastRealIdOrphans;
                if (realOrphans > 0) parts.Add($"shift-employees×{realOrphans} logged");

                // ── Class 4: rival-refs (DETECT-ONLY) ────────────────────────
                // Registration owner-rival ids that resolve to no rival and no
                // session player — the rival-poor / contamination families.
                // Repair deliberately withheld: an erased AI landlord is
                // unrecoverable (worldgen-only assignment), so we count and
                // name, never guess.
                int rivalRefs = 0, rivalLogged = 0;
                try
                {
                    if (gi.BuildingRegistrations != null)
                        foreach (var reg in gi.BuildingRegistrations)
                        {
                            if (reg == null) continue;
                            foreach (var id in new[] { reg.businessOwnerRivalId, reg.buildingOwnerRivalId })
                            {
                                if (string.IsNullOrEmpty(id)) continue;
                                bool sessionPlayer = false;
                                try { sessionPlayer = GameStatePatcher.IsSessionPlayerId(id); } catch { }
                                if (sessionPlayer) { continue; }   // contamination sweep's territory
                                bool known = false;
                                try { known = BigAmbitions.Rivals.RivalsHelper.GetRivalData(id) != null; } catch { }
                                if (known) continue;
                                rivalRefs++;
                                if (rivalLogged++ < LogCapPerClass)
                                    Plugin.Logger.LogWarning($"[Integrity] {reason}: '{reg.BusinessName}' references rival '{id}' — unresolvable; left in place (deed repair is not provable).");
                            }
                        }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Integrity] rival-refs: {ex.Message}"); }
                if (rivalRefs > 0) parts.Add($"rival-refs×{rivalRefs} logged");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Integrity] sweep ({reason}): {ex.Message}"); }

            LastSummary = string.Join("; ", parts);
            if (LastSummary.Length > 0)
                Plugin.Logger.LogWarning($"[Integrity] {reason}: {LastSummary}.");
        }
    }
}
