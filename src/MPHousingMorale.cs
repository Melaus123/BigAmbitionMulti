using System;

namespace BigAmbitionsMP
{
    /// <summary>Shared-residence morale (user request 2026-07-19): a Housing
    /// grant should count as HAVING a home — the native no-home penalty
    /// ('ba:happinessmodifier_no_home') is added at character start and only
    /// ever removed by personally renting a residential, so a granted guest
    /// who sleeps in the owner's bed still eats the morale hit.
    ///
    /// Poll-based reconcile (frontload principle — no grant-arrival event
    /// ordering to race): every ~10s, if the player rents NO residential of
    /// their own but holds a Housing grant on someone's residence, lift the
    /// penalty; if the grant is gone (revoked/owner left) and they are still
    /// homeless, restore it.  Restoring is safe because "homeless ⇒ has
    /// no_home" is the native invariant (start adds it; only renting removes
    /// it), and AddModifier self-gates on the disableHappiness setting.
    /// Shared residences are NOT RentedByPlayer on the guest's machine
    /// (HousingMapCues.IsSharedWithMe), so the native rent checks stay
    /// untouched by this — the reconcile is purely additive.</summary>
    public static class MPHousingMorale
    {
        private const string NoHome = "ba:happinessmodifier_no_home";
        private static float _nextAt;
        private static bool _liftedLogged, _restoredLogged;

        public static void Reset() { _nextAt = 0f; _liftedLogged = _restoredLogged = false; }

        public static void Tick()
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                if (UnityEngine.Time.unscaledTime < _nextAt) return;
                _nextAt = UnityEngine.Time.unscaledTime + 10f;

                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null || gi.happinessModifiers == null) return;

                bool ownHome = false, sharedHome = false;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    bool residential;
                    try { residential = reg.GetBuildingType() == "ba:buildingtype_residential"; } catch { continue; }
                    if (!residential) continue;
                    if (reg.RentedByPlayer) { ownHome = true; break; }
                    if (!sharedHome && HousingMapCues.IsSharedWithMe(reg)) sharedHome = true;
                }

                bool hasPenalty = gi.happinessModifiers.Exists(m => m != null && m.type == NoHome);

                if (!ownHome && sharedHome && hasPenalty)
                {
                    Helpers.HappinessHelper.RemoveModifier(NoHome);
                    if (!_liftedLogged) { _liftedLogged = true; _restoredLogged = false; Plugin.Logger.LogInfo("[Morale] a shared residence counts as home — no-home penalty lifted."); }
                }
                else if (!ownHome && !sharedHome && !hasPenalty)
                {
                    Helpers.HappinessHelper.AddModifier(NoHome);
                    if (!_restoredLogged) { _restoredLogged = true; _liftedLogged = false; Plugin.Logger.LogInfo("[Morale] no residence and no housing grant — no-home penalty restored."); }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Morale] tick: {ex.Message}"); }
        }
    }
}
