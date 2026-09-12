using System;
using HarmonyLib;
using TMPro;          // TMP_Text (map-card recolour)
using UnityEngine;
using Entities;       // InstanceBehavior
using Buildings;      // BuildingRegistration
using UI.Overlays;    // BuildingEntranceOverlay
using UI.Elements;    // LabelInfo
using UI.InGameUI;    // BuildingResume (city-map building card)

namespace BigAmbitionsMP
{
    /// <summary>Shared residency — MAP + WORLD-HOVER cues (2026-06-30) so a guest can FIND and RECOGNISE residences
    /// shared with them. On the map a shared residence gets a permanently-visible POI tinted a distinct teal (the
    /// owner's own are yellow); on the world hover its status reads "Shared" in teal instead of "Occupied" in red.
    /// "Shared with me" = GrantSync.CanEnterGranted (the enterable set the host pushes each client; residence-scoped).
    /// Word + colour are easy to change here. See docs/PERMISSIONS-SYSTEM.md.</summary>
    internal static class HousingMapCues
    {
        // A distinct, non-alarming hue for "a residence shared with you" (owner=yellow, available=green, occupied=red).
        internal static readonly Color32 SharedColor = new Color32(80, 200, 220, 255);   // teal
        internal const string SharedWord = "Shared";

        /// <summary>A residence the local player may enter on someone else's key — not one they own/rent.</summary>
        internal static bool IsSharedResidence(BuildingRegistration reg)
        {
            try
            {
                if (reg == null || reg.RentedByPlayer || reg.BuildingOwnedByPlayer) return false;   // mine → normal cue
                return GrantSync.CanEnterGranted(GameStateReader.AddressKey(reg));                  // grant is residence-scoped
            }
            catch { return false; }
        }

        /// <summary>MERGER PHASE 4d (R1) — a COMPANY building that reads as the member's own ONLY because the
        /// merger flip set RentedByPlayer on it (MergerFlip.cs:131-137). Entry, sleep, fridge and wardrobe are
        /// already correct for one (D22) — the single gap is paint: the native path claims it as this member's
        /// building (CityBuildingController.UpdatePoi, decompile :762-781), so the cues below tint it in the
        /// OWNER's colour instead. The "Shared" WORD is never applied to one: a merged member is an owner here,
        /// not a guest (D22/D18 — colour only, wording kept).</summary>
        internal static bool IsFlippedPartnerResidence(BuildingRegistration reg)
        {
            try
            {
                if (reg == null || !reg.RentedByPlayer) return false;
                if (!IsResidential(reg)) return false;   // R1 is the RESIDENCE cue set; a company SHOP's paint is not touched here
                return MergerFlip.IsFlipped(GameStateReader.AddressKey(reg));
            }
            catch { return false; }
        }

        /// <summary>Round-39c — ANY building shared with the local player: a granted residence OR a helper
        /// business (user 2026-07-07: business grant gave no map reference / POI / shared indication —
        /// the cues were residence-only). Drives the FIND-IT cues (POI tint + permanence + map filter);
        /// the word/status overrides stay residence-only so a business keeps its Open/Closed label.</summary>
        internal static bool IsSharedWithMe(BuildingRegistration reg)
        {
            try
            {
                if (reg == null) return false;
                if (IsFlippedPartnerResidence(reg)) return true;   // 4d R1: a company home — find-it cues in the owner's colour
                if (reg.RentedByPlayer || reg.BuildingOwnedByPlayer) return false;
                string k = GameStateReader.AddressKey(reg);
                return GrantSync.CanEnterGranted(k) || GrantSync.IsHelperBusiness(k);
            }
            catch { return false; }
        }

        /// <summary>Re-run UpdatePoi on every residence currently shared with us so its POI exists + gets recoloured.
        /// POIs are built on city load, BEFORE the host's grant set arrives, so we refresh when that set changes.
        /// MAIN THREAD.</summary>
        internal static void RefreshSharedPois()
        {
            try
            {
                var cm = InstanceBehavior<CityManager>.Instance;
                if (cm?.cityBuildingControllers == null) return;
                int n = 0, company = 0;
                foreach (var cbc in cm.cityBuildingControllers)
                {
                    if (cbc?.buildingRegistration == null) continue;
                    try
                    {
                        if (!IsSharedWithMe(cbc.buildingRegistration)) continue;
                        cbc.UpdatePoi(); n++;
                        if (IsFlippedPartnerResidence(cbc.buildingRegistration)) company++;
                    }
                    catch { }
                }
                if (n > 0) Plugin.Logger.LogInfo($"[Housing] map: tinted {n} shared building POI(s) (residences + helper businesses).");
                if (company > 0) Plugin.Logger.LogInfo($"[Cues] tinted {company} residences");   // 4d R6
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] RefreshSharedPois: {ex.Message}"); }
        }

        /// <summary>4d R6: residential is the building TYPE, not the business type (the residence list filters on
        /// it — decompile PrivateResidenceScrollerController.cs:12-15).</summary>
        internal static bool IsResidential(BuildingRegistration reg)
        {
            try { return reg != null && reg.GetBuildingType() == "ba:buildingtype_residential"; }
            catch { return false; }
        }

        // Reflection get/set that tries a property then a field — TextLocalizationComponent (Localizor) isn't a
        // referenced type, so its Key / Prefix / TextContainer must be reached this way.
        internal static object GetMember(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            var p = t.GetProperty(name);
            if (p != null && p.CanRead) return p.GetValue(o);
            return AccessTools.Field(t, name)?.GetValue(o);
        }

        internal static void SetMember(object o, string name, object val)
        {
            if (o == null) return;
            var t = o.GetType();
            var p = t.GetProperty(name);
            if (p != null && p.CanWrite) { p.SetValue(o, val); return; }
            AccessTools.Field(t, name)?.SetValue(o, val);
        }
    }

    /// <summary>City-MAP building card (BuildingResume): the residence-status line still read "Occupied" (red) for a
    /// guest — the world-hover patch covers the 3D door overlay, THIS covers the map-mode card. openStateLabel is a
    /// TextLocalizationComponent (Localizor, un-referenced) so we reach it by reflection: set Prefix="Shared" + empty
    /// Key (the game renders Prefix + Localize(Key) = "Shared") and recolour the text teal, after the game set red.</summary>
    [HarmonyPatch(typeof(BuildingResume), "UpdateDetails")]
    public static class Patch_BuildingResume_GuestStatus
    {
        static void Postfix(BuildingResume __instance)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                var cbc = HousingMapCues.GetMember(__instance, "CityBuildingController") as CityBuildingController;
                if (cbc == null) return;
                var reg = cbc.buildingRegistration;
                // 4d R1: a merger company building keeps the game's own status WORD (the member owns it) and only
                // takes the owner's colour; a true guest residence keeps the "Shared" wording it has always had.
                bool company = HousingMapCues.IsFlippedPartnerResidence(reg);
                if (!company && !HousingMapCues.IsSharedResidence(reg)) return;
                var label = HousingMapCues.GetMember(__instance, "openStateLabel");
                if (label == null) return;
                if (!company)
                {
                    HousingMapCues.SetMember(label, "Prefix", HousingMapCues.SharedWord);
                    HousingMapCues.SetMember(label, "Key", "");
                }
                if (HousingMapCues.GetMember(label, "TextContainer") is TMP_Text tc)
                {
                    if (!company) tc.text = HousingMapCues.SharedWord;
                    tc.color = PlayerColours.TryColourForAddressKey(GameStateReader.AddressKey(reg), out var c) ? c : HousingMapCues.SharedColor;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] map-card status: {ex.Message}"); }
        }
    }

    /// <summary>Map POI: tint a shared residence teal + keep it permanently visible (like an owned one) so the guest
    /// can locate it. Postfix runs after the game set the normal icon + permanent flag.</summary>
    [HarmonyPatch(typeof(CityBuildingController), nameof(CityBuildingController.UpdatePoi))]
    public static class Patch_CityBuildingController_SharedPoi
    {
        static void Postfix(CityBuildingController __instance)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                var reg = __instance?.buildingRegistration;
                if (!HousingMapCues.IsSharedWithMe(reg)) return;
                var poi = __instance.poi;
                if (poi == null) return;
                poi.SetPermanent(true);
                poi.SetIcon(reg.GetPOIIcon(), PlayerColours.TryColourForAddressKey(GameStateReader.AddressKey(reg), out var c) ? c : HousingMapCues.SharedColor);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] shared POI: {ex.Message}"); }
        }
    }

    /// <summary>World hover: a residence shared with the guest reads "Shared" (teal) instead of "Occupied" (red).</summary>
    [HarmonyPatch(typeof(BuildingEntranceOverlay), nameof(BuildingEntranceOverlay.GetSecondLineRightLabel))]
    public static class Patch_BuildingEntranceOverlay_GuestStatus
    {
        static void Postfix(BuildingEntranceOverlay __instance, ref LabelInfo __result)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                var reg = __instance?._currentCBC?.buildingRegistration;
                bool company = HousingMapCues.IsFlippedPartnerResidence(reg);
                if (!company && !HousingMapCues.IsSharedResidence(reg)) return;
                Color32 col = PlayerColours.TryColourForAddressKey(GameStateReader.AddressKey(reg), out var c) ? c : HousingMapCues.SharedColor;
                // 4d R1: colour only on a company building — the game's own label (key + arguments + localize) is
                // rebuilt unchanged, so no wording is added (LabelInfo's fields are readonly, decompile LabelInfo.cs:5-18).
                __result = company
                    ? (__result == null ? __result : new LabelInfo(__result.key, __result.arguments, col, __result.localize))
                    : new LabelInfo(HousingMapCues.SharedWord, col, localize: false);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] guest status label: {ex.Message}"); }
        }
    }

    /// <summary>City MAP visibility. A shared residence belongs to NO filter category, so the game's ApplyFilters
    /// (which runs on map-open and on every filter toggle) HIDES its permanent POI and re-colours filtered ones,
    /// burying the world tint. Per the user's choice, shared residences ride the "Rented by you" filter: after
    /// ApplyFilters runs, if that filter is on, re-show + re-tint each shared residence teal. (When the filter is
    /// off we return early, so the game's hide stands — exactly the intended toggle behaviour.)</summary>
    [HarmonyPatch(typeof(CityMapFilters), nameof(CityMapFilters.ApplyFilters))]
    public static class Patch_CityMapFilters_SharedResidences
    {
        static void Postfix(CityMapFilters __instance)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                if (!CityMap.IsOpen || __instance == null) return;
                if (!__instance.IsFilterSelected("buildingresume_rented_by_you")) return;   // ride the residences-you-use filter
                var cm = InstanceBehavior<CityManager>.Instance;
                if (cm?.cityBuildingControllers == null) return;
                int n = 0;
                foreach (var cbc in cm.cityBuildingControllers)
                {
                    if (cbc?.buildingRegistration == null) continue;
                    if (!HousingMapCues.IsSharedWithMe(cbc.buildingRegistration)) continue;
                    if (cbc.poi == null) cbc.CreatePOI();
                    cbc.poi.SetHidden(false);
                    Color32 own = PlayerColours.TryColourForAddressKey(GameStateReader.AddressKey(cbc.buildingRegistration), out var c) ? c : HousingMapCues.SharedColor;
                    cbc.SetHighlight(true, own);
                    cbc.poi.SetIcon(cbc.buildingRegistration.GetPOIIcon(), own);
                    n++;
                }
                if (n > 0) Plugin.Logger.LogInfo($"[Housing] map filter: revealed {n} shared building(s) under 'rented by you'.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] map filter cue: {ex.Message}"); }
        }
    }
}
