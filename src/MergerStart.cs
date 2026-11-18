using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Buildings;          // BuildingRegistration
using Helpers;            // BusinessTypeHelper, BusinessHelper
using UI.Notification;    // Notifications
using UnityEngine;
using SBUI = UI.Smartphone.Apps.BizMan.StartBusiness.StartBusinessUI;

namespace BigAmbitionsMP
{
    /// <summary>T-78 (v29): STARTING a business in a merged partner's building. The game's start is
    /// StartBusinessUI.SetUpBusiness (decompile UI.Smartphone.Apps.BizMan.StartBusiness/StartBusinessUI.cs:250-534): the
    /// front checks (:252-282), then the name/type write (:284-285) and the whole native setup. On a member's machine the
    /// partner's building is a FLIPPED replica, so that write landed on the replica only and the owner's empty copy later
    /// wiped it (bench T-T78-REPRO-20261118-033156). Now the start ROUTES to the machine that runs the shop (owner, or a
    /// stand-in per D69), which runs the game's own SetUpBusiness through RunGameStart; its normal business publish
    /// then carries the business to everyone.</summary>
    internal static class MergerStart
    {
        private static readonly FieldInfo? _fType = AccessTools.Field(typeof(SBUI), "_selectedType");
        private static readonly FieldInfo? _fReg  = AccessTools.Field(typeof(SBUI), "_buildingRegistration");
        private static readonly FieldInfo? _fName = AccessTools.Field(typeof(SBUI), "businessNameField");

        /// <summary>T-78 F1: the private start form (GetStartForm) and its inactive holder. Unity-null after a destroy.</summary>
        private static GameObject? _formHolder;
        private static SBUI? _form;
#if BAMP_DEV
        /// <summary>T-78 F6 rig lever `healhold on|off` (TestDrive): while on, TryHealEmptyOwnCopy adopts nothing, so the host's
        /// refusal of this owner's empty record can be observed.</summary>
        internal static bool HealHeld;
        private static readonly HashSet<string> _healHeldLogged = new();
        internal static void SetHealHold(bool on) { HealHeld = on; lock (_healHeldLogged) _healHeldLogged.Clear(); }
#endif

        private static readonly HashSet<string> _logged = new();

        internal static bool IsLiveType(string? type)
            => !string.IsNullOrEmpty(type) && !type!.EndsWith("_empty", StringComparison.Ordinal);

        /// <summary>The registration holds a business: a name, or a type other than the empty sentinel.</summary>
        internal static bool HoldsBusiness(BuildingRegistration reg)
        {
            if (reg == null) return false;
            string name = ""; try { name = reg.BusinessName ?? ""; } catch { }
            string type = ""; try { type = reg.businessTypeName ?? ""; } catch { }
            return name.Length > 0 || IsLiveType(type);
        }

        internal static SBUI? UiReg(SBUI ui, out BuildingRegistration? reg, out string type, out TMPro.TMP_InputField? field)
        {
            reg = null; type = ""; field = null;
            if (ui == null || _fType == null || _fReg == null || _fName == null) return null;
            reg = _fReg.GetValue(ui) as BuildingRegistration;
            type = _fType.GetValue(ui) as string ?? "";
            field = _fName.GetValue(ui) as TMPro.TMP_InputField;
            return ui;
        }

        /// <summary>D6: the game's ONE StartBusinessUI - a serialized child of the phone's BizManPresentation
        /// (decompile BizManPresentation.cs:65), not a singleton. Resources.FindObjectsOfTypeAll also returns inactive scene
        /// objects (the phone screen is inactive until shown); prefab assets are skipped by the scene test. An active one
        /// (the player has the start screen open) wins.</summary>
        internal static SBUI? FindStartUI()
        {
            SBUI? best = null;
            foreach (var ui in Resources.FindObjectsOfTypeAll<SBUI>())
            {
                if (ui == null) continue;
                try { if (!ui.gameObject.scene.IsValid()) continue; } catch { continue; }
                try { if (ui.gameObject.activeInHierarchy) return ui; } catch { }
                best ??= ui;
            }
            return best;
        }

        /// <summary>T-78 F1 (review): the PRIVATE start form RunGameStart drives - never the player's start screen. A clone of the
        /// scene's StartBusinessUI (FindStartUI) under an INACTIVE DontDestroyOnLoad holder: inactive in hierarchy, so its Awake
        /// (StartBusinessUI.cs:54-66 - keyboard/Steam input hookup, the sourced-product scan; SetUpBusiness reads none of it)
        /// never runs; nothing hides it (Hide :92-96 nulls the registration the logo callback :523-527 reads later); and it has
        /// no OnBusinessStarted subscribers (C# events are not serialized). Re-created when Unity-null. Its serialized
        /// businessNameField must have been remapped to its OWN child, or it is not used.</summary>
        private static SBUI? GetStartForm(out string why)
        {
            why = "";
            if (_form != null) return _form;
            var original = FindStartUI();
            if (original == null) { why = "no StartBusinessUI instance in the scene"; return null; }
            if (_formHolder == null)
            {
                _formHolder = new GameObject("BAMP_MergerStartForm");
                _formHolder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_formHolder);
            }
            var clone = UnityEngine.Object.Instantiate(original, _formHolder.transform);
            var cf = _fName?.GetValue(clone) as TMPro.TMP_InputField;
            if (cf == null || !cf.transform.IsChildOf(clone.transform))
            {
                why = "the private start form's name field is not its own";
                string what = cf == null ? "it has no name field" : "its name field '" + cf.name + "' still belongs to the game's form";
                if (_logged.Add("form-remap"))
                    Plugin.Logger.LogWarning($"[Merger] private start form NOT usable: {what} - no business start runs on this machine.");
                try { UnityEngine.Object.Destroy(clone.gameObject); } catch { }
                return null;
            }
            _form = clone;
            Plugin.Logger.LogInfo($"[Merger] private start form created (inactive copy of '{original.name}'; its name field is its own).");
            return clone;
        }

        /// <summary>D6 SHARED DRIVER: run the GAME'S OWN SetUpBusiness for `reg` with this name and type - on the PRIVATE start
        /// form (GetStartForm), never the player's screen. Every caller goes through the same call, and the D1 prefix is on the
        /// method (any instance), so it decides as for a player's click: on a member's replica it routes, on the machine that
        /// runs the shop it runs natively. bypassDiploma (D70) is for a start applied FOR another member (routed start, heal,
        /// hand-back): the game's name/type checks are then run here QUIETLY first, because their failure branches raise the
        /// game's notice on THIS player's screen for something they did not do; and the required diploma reads completed for
        /// that one call (the save's own Diploma.completed, restored after - F2: EducationHelper.HasCompletedDiploma is a
        /// one-liner, EducationHelper.cs:19-22, that a patch could miss once inlined into SetUpBusiness).
        /// Returns true when the registration now holds the business.</summary>
        internal static bool RunGameStart(BuildingRegistration reg, string name, string type, bool bypassDiploma, out string why)
        {
            why = "";
            if (reg == null) { why = "no registration here"; return false; }
            name = (name ?? "").Trim(); type = type ?? "";
            try
            {
                if (bypassDiploma)
                {
                    if (HoldsBusiness(reg)) { why = $"a business already runs here ('{reg.BusinessName}', {reg.businessTypeName})"; return false; }
                    if (name.Length == 0 || name.StartsWith(".", StringComparison.Ordinal)
                        || name.Any(c => Path.GetInvalidFileNameChars().Contains(c)))
                    { why = $"the name '{name}' is not one the game accepts"; return false; }
                    if (!IsLiveType(type) || BusinessTypeHelper.GetData(type) == null)
                    { why = $"the type '{type}' is not a business type registered here"; return false; }
                    if (SaveGameManager.Current.BuildingRegistrations.Exists(x => x != null && !ReferenceEquals(x, reg) && x.BusinessName == name))
                    { why = $"another building here already carries the name '{name}'"; return false; }
                }
                var ui = GetStartForm(out var formWhy);
                if (ui == null || UiReg(ui, out _, out _, out var field) == null || field == null)
                { why = formWhy.Length > 0 ? formWhy : "no StartBusinessUI instance in the scene"; return false; }
                DiplomaName need = DiplomaName.Undefined;
                bool diplomaSet = false, diplomaWas = false;
                try
                {
                    if (bypassDiploma)
                    {
                        need = BusinessTypeHelper.GetData(type).courseRequired;
                        if (need != DiplomaName.Undefined)
                        {
                            var d = EducationHelper.GetDiploma(need);
                            if (d != null) { diplomaWas = d.completed; d.completed = true; diplomaSet = true; }
                        }
                    }
                    _fReg!.SetValue(ui, reg);
                    _fType!.SetValue(ui, type);
                    field.text = name;
                    ui.SetUpBusiness();
                }
                catch (Exception ex) { why = "the game's start threw: " + ex.Message; }
                finally
                {
                    if (diplomaSet) { try { EducationHelper.GetDiploma(need).completed = diplomaWas; } catch { } }
                    // The private form's logo callback (StartBusinessUI.cs:523-527) rings the bell for whichever registration the
                    // form holds when the logo finishes (a later start may have replaced it): ring it for this building now.
                    try { GlobalEvents.onBuildingRegistrationChange?.Invoke(reg.Address); } catch { }
                }
            }
            catch (Exception ex) { why = ex.Message; return false; }
            bool held = HoldsBusiness(reg) && (reg.BusinessName ?? "") == name;
            if (!held && why.Length == 0) why = "the registration does not hold the business after the call";
            else if (held && why.Length > 0)
            {
                string k = ""; try { k = GameStateReader.AddressKey(reg) ?? ""; } catch { }
                Plugin.Logger.LogWarning($"[Merger] start of '{k}' ('{name}' type={type}) holds the business, but the game's start reported: {why}");
            }
            return held;
        }

        /// <summary>D5 (OWNER, MAIN THREAD - ApplyBusinessInfoLocal runs on it): a shop this machine truly owns holds NO
        /// business, yet the company record for it carries one (a start that landed on a member's replica before T-78, or
        /// one this machine missed). Unless this machine shut that business down itself, adopt it with the game's own start.</summary>
        internal static void TryHealEmptyOwnCopy(BuildingRegistration reg, BusinessInfo info)
        {
            try
            {
                if (reg == null || info == null || !MergerSync.IAmMember) return;
                if (!MergerFlip.TrulyMine(reg) || HoldsBusiness(reg)) return;
                string key = info.AddressKey ?? "";
                string name = (info.BusinessName ?? "").Trim();
                if (key.Length == 0 || name.Length == 0 || !IsLiveType(info.BusinessTypeName)) return;
                if (!string.IsNullOrEmpty(info.BusinessOwnerRivalId)) return;   // an AI business is never adopted
                if (!string.IsNullOrEmpty(info.OwnerPlayerId) && info.OwnerPlayerId != MPConfig.PlayerId) return;   // a record naming someone else
                if (BusinessSync.IsShutDownHere(key)) return;   // I closed it myself: the stale copy must not resurrect it
#if BAMP_DEV
                if (HealHeld)
                {
                    bool first; lock (_healHeldLogged) first = _healHeldLogged.Add(key);
                    if (first)
                        Plugin.Logger.LogInfo($"[Merger] heal held (rig): my empty '{key}' would adopt '{name}' type={info.BusinessTypeName} from the company record.");
                    return;
                }
#endif
                if (RunGameStart(reg, name, info.BusinessTypeName, bypassDiploma: true, out var why))
                {
                    GameStatePatcher.SetOpenStateNoNative(reg, key, info.TemporarilyClosed, "healed from the company record");
                    Plugin.Logger.LogInfo($"[Merger] healed my empty '{key}' from the company record: '{name}' type={info.BusinessTypeName}");
                }
                else if (_logged.Add("heal|" + key + "|" + why))
                    Plugin.Logger.LogWarning($"[Merger] heal of my empty '{key}' from the company record ('{name}' type={info.BusinessTypeName}) NOT applied: {why}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] heal of '{info?.AddressKey}': {ex.Message}"); }
        }

        /// <summary>T-78 F5 (MEMBER, MAIN THREAD - ApplyBusinessInfoLocal): a ROUTED start landed on a partner's building this
        /// machine shows. The game switches its start screen only through OnBusinessStarted inside its own start, which ran on
        /// the runner's machine; BizManPresentation.BusinessHasStarted (decompile :187-191) is exactly SetInitialTab() +
        /// RefreshData() on the BizMan business page, so the open page here takes the same two steps - only when it is open
        /// on that very registration.</summary>
        internal static void RefreshOpenBizManAfterStart(BuildingRegistration reg, string key)
        {
            try
            {
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var page = ui != null && ui.fullMenu != null && ui.fullMenu.bizMan != null ? ui.fullMenu.bizMan.business : null;
                if (page == null || !page.gameObject.activeInHierarchy || !ReferenceEquals(page.buildingRegistration, reg)) return;
                page.SetInitialTab();
                page.RefreshData();
                Plugin.Logger.LogInfo($"[Merger] '{key}' now holds '{reg.BusinessName}' - the open BizMan page took the game's business-started steps (T-78).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] BizMan page refresh after the start of '{key}': {ex.Message}"); }
        }

        /// <summary>D7 (OWNER, on return): a STAND-IN started a business in my building while I was away (D69). Called
        /// BEFORE the returned identity/logo and the closed flag are applied, so those land on the started business.</summary>
        internal static void TryAdoptReturnedStart(BuildingRegistration reg, BusinessPaperwork b)
        {
            try
            {
                if (reg == null || b == null || HoldsBusiness(reg)) return;
                string name = (b.BusinessName ?? "").Trim();
                if (name.Length == 0 || !IsLiveType(b.BusinessTypeName)) return;   // "" = not carried / an older bundle
                if (RunGameStart(reg, name, b.BusinessTypeName, bypassDiploma: true, out var why))
                    Plugin.Logger.LogInfo($"[Absence] returned start for '{b.AddressKey}': '{name}' type={b.BusinessTypeName} (started by the stand-in).");
                else
                    Plugin.Logger.LogWarning($"[Absence] returned start for '{b.AddressKey}' ('{name}' type={b.BusinessTypeName}) NOT applied: {why}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Absence] returned start for '{b?.AddressKey}': {ex.Message}"); }
        }

        /// <summary>D7 (HOST, filing a LIVE CLIENT stand-in's paperwork - MPServer.HostFileSimulatedPaperwork): the stand-in
        /// started a business in the away owner's building. The host does not run that shop, so no native start here: the
        /// name and type land on the host's copy as a received record's do (GameStatePatcher.ApplyBusinessInfoLocal - the
        /// identity write, the BIZTYPE-1 unknown-type guard, the producer slot guard and the type-change notify; that block
        /// is inline there, not callable), and the host's business scan publishes it. Returns true when written.</summary>
        internal static bool HostAdoptStandInStart(BuildingRegistration hreg, BusinessPaperwork mb, string senderPid)
        {
            try
            {
                if (hreg == null || mb == null || HoldsBusiness(hreg)) return false;
                string name = (mb.BusinessName ?? "").Trim(), type = mb.BusinessTypeName ?? "";
                if (name.Length == 0 || !IsLiveType(type)) return false;
                if (BusinessTypeHelper.GetData(type) == null)
                {
                    Plugin.Logger.LogWarning($"[Paperwork] stand-in start '{mb.AddressKey}' from '{senderPid}': type '{type}' is not registered on this machine - not written (BIZTYPE-1).");
                    return false;
                }
                hreg.BusinessName = name;
                hreg.businessTypeName = type;
                try
                {
                    if (hreg is Entities.Warehouse wh)
                    {
                        int want = 0;
                        try { want = Buildings.BuildingSizeHelper.GetData(Helpers.BuildingHelper.GetBuilding(wh.Address))?.numberOfVehicleSlots ?? 0; } catch { }
                        if (want > 0 && (wh.vehicleSlots == null || wh.vehicleSlots.Count < want)) wh.ResetVehicleSlots();
                    }
                }
                catch { }
                try { GlobalEvents.onBuildingRegistrationChange?.Invoke(hreg.Address); } catch { }
                try { AI.Customers.CustomerEntries.CustomerEntriesHelper.UpdateCustomerEntriesForPlayerBusiness(hreg, TimeHelper.GetDayOfWeek()); } catch { }
                Plugin.Logger.LogInfo($"[Paperwork] stand-in start '{mb.AddressKey}' from '{senderPid}': '{name}' type={type} written on the host's copy (no native start - the host does not run it).");
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Paperwork] stand-in start '{mb?.AddressKey}' from '{senderPid}': {ex.Message}"); return false; }
        }
    }

    /// <summary>T-78 D1: a member's start on a merged partner's building it does NOT run routes to the machine that runs it.
    /// The game's own front checks (StartBusinessUI.cs:252-282) run HERE first with the game's own notices - the diploma
    /// check on the clicking machine is the one that counts (D70) - then "mergerstart" goes out and the native write never
    /// touches the replica. The runner (owner, or a stand-in, D69) falls through to the native method unchanged.</summary>
    [HarmonyPatch(typeof(SBUI), nameof(SBUI.SetUpBusiness))]
    internal static class Patch_SetUpBusiness_MergerRoute
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(SBUI __instance)
        {
            BuildingRegistration? reg; string type; TMPro.TMP_InputField? field; string key;
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return true;
                if (MergerFlip.FlippedCount == 0) return true;
                if (MergerStart.UiReg(__instance, out reg, out type, out field) == null || reg == null || field == null) return true;
                key = GameStateReader.AddressKey(reg);
                if (!MergerFlip.IsFlipped(key) || MergerFlip.BooksHere(reg)) return true;
                if (BusinessTypeHelper.GetData(type) == null) return true;   // the native line 252 throws as before (P-STARTBIZ-TYPE)
            }
            catch { return true; }
            var f = field!;
            try
            {
                DiplomaName need = BusinessTypeHelper.GetData(type).courseRequired;
                if (need != DiplomaName.Undefined && !EducationHelper.HasCompletedDiploma(need))
                { EducationHelper.ShowCourseRequiredNotification(type, need); return false; }
                if (string.IsNullOrWhiteSpace(f.text))
                { Notifications.ShowError("bizman_presentation_notification_no_business_name_entered"); return false; }
                f.text = f.text.Trim();
                if (f.text.StartsWith("."))
                { Notifications.ShowError("bizman_notification_name_cannot_start_dot", "bizman_notification_name_cannot_start_dot", trackOnSaveGame: false); return false; }
                if (f.text.Any((char x) => Path.GetInvalidFileNameChars().Contains(x)))
                { Notifications.ShowError("bizman_notification_invalid_business_name_characters", "name_invalid_characters", trackOnSaveGame: false); return false; }
                string name = f.text;
                if (SaveGameManager.Current.BuildingRegistrations.Exists((BuildingRegistration x) => x.BusinessName == name))
                { Notifications.ShowError("bizman_settings_notification_name_duplicated"); return false; }
                if (type == "ba:businesstype_empty")
                { Notifications.ShowError("bizman_presentation_notification_no_business_type_entered"); return false; }
                SharedShopWorkTabs.SendEdit(new SharedWorkEditPayload
                { PlayerId = MPConfig.PlayerId, AddressKey = key, Op = "mergerstart", StrValue = name, TypeName = type });
                Plugin.Logger.LogInfo($"[Merger] start routed for '{key}': '{name}' type={type}");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] start route for '{key}': {ex.Message} - not started on this copy."); }
            return false;
        }
    }

    /// <summary>T-78 D4: the game's business teardown (BusinessHelper.ShutdownBusiness, decompile Helpers/BusinessHelper.cs:1340)
    /// on a shop this machine TRULY owns marks the address, so this machine's records for it carry ShutDown=true and the host
    /// accepts the empty record (BusinessSync.ReadInfo). Reached by BizManSettings' confirm (:374), a business move
    /// (BizManTransfer:45), the routed shutdown, the mod's own lease end (GameStatePatcher ~:7962) and the contents handover
    /// (ContentsHandover ~:917). The player's own lease end does NOT reach it: the game calls BusinessHelper.SetBuildingForRent
    /// (:1447, which runs the teardown) only from city generation and save compatibility fixes. The mark clears when a
    /// business starts there again, or when the host's own record for the address arrives empty (BusinessSync.ClearShutDown).</summary>
    [HarmonyPatch(typeof(BusinessHelper), nameof(BusinessHelper.ShutdownBusiness))]
    internal static class Patch_ShutdownBusiness_MarkShutDown
    {
        static void Prefix(BuildingRegistration registration, out string __state)
        {
            __state = "";
            try { if (registration != null && MergerFlip.TrulyMine(registration)) __state = GameStateReader.AddressKey(registration) ?? ""; }
            catch { }
        }

        static void Postfix(string __state)
        {
            if (string.IsNullOrEmpty(__state)) return;
            BusinessSync.MarkShutDown(__state);
            Plugin.Logger.LogInfo($"[BusinessSync] shutdown marked for '{__state}': my records carry ShutDown until a business starts there again (T-78).");
        }
    }
}
