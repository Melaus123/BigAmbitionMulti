using System;
using HarmonyLib;
using Buildings;
using Helpers;
using Entities;

namespace BigAmbitionsMP
{
    /// <summary>Crash-guard. The game's <c>InteriorElement.Deserialize</c> reads
    /// <c>serializedInteriorDesign.materials.Length</c> with NO null-check, so a design whose <c>materials</c> is
    /// null throws an NRE inside <c>BuildingManager.LoadBuilding</c> → aborts the <c>EnterBuildingCoroutine</c> →
    /// the player is stuck on a BLACK SCREEN on load (user 2026-06-30). A null (rather than empty) materials array
    /// reached a building's <c>interiorDesigns</c> via an earlier interior-sync apply; the apply is now fixed to
    /// write an empty array, but this ALSO rescues saves already carrying the bad data. Skip Deserialize for a
    /// null-materials design (there is nothing to apply); valid designs run unchanged. Defensive + universal — a
    /// no-op for well-formed data.</summary>
    [HarmonyPatch(typeof(InteriorElement), nameof(InteriorElement.Deserialize))]
    public static class Patch_InteriorElement_Deserialize_NullGuard
    {
        static bool Prefix(SerializedInteriorDesign serializedInteriorDesign)
        {
            try { return serializedInteriorDesign != null && serializedInteriorDesign.materials != null; }
            catch { return false; }
        }
    }
}
