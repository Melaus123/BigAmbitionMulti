using System;
using System.Collections.Generic;
using System.Linq;
using Buildings;                               // BuildingRegistration
using Dialogs;                                 // the eight NPC phone dialogs
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>
    /// ACCESS GATES (build D9, 2026-09-11) — eight NPC phone contacts refuse to talk to a player who rents
    /// no building of their own. A player who holds a Business grant on another player's shop, or who is in
    /// a merger, is running a business too, so those eight now answer.
    ///
    /// NO NEW PERMISSIONS. This file only satisfies the "you must rent a building" test; what each contact
    /// then lets the player do is decided by the mod's existing access rules, unchanged. The dialogs that
    /// additionally demand a non-blank BusinessName, an HR diploma or a rented WAREHOUSE get nothing extra:
    /// the raised registrations carry their own names and types, so e.g. an access set with no warehouse
    /// still gets the import manager's refusal — correctly.
    ///
    /// HOW: the same call-scoped tenancy idiom the mod already uses for shared shops
    /// (SharedShopVisibility.RaiseTenancy / LowerTenancy, src/SharedShopVisibility.cs:74-88), generalised
    /// from one registration to a list. A Prefix raises RentedByPlayer on every registration this machine
    /// may manage through a Business grant, the dialog constructor reads the list it always read, and a
    /// Finalizer lowers exactly those again (and rethrows). The raise can never span a tick, a save or a
    /// publish, and nothing is ever written to a save-persisted field.
    ///
    /// Inert everywhere else: HasAccessTenancy() is false in single-player and outside an MP session
    /// (MergerSync.IAmMember reads an empty group map; GrantSync.SharedManageCount is 0), and every Prefix
    /// returns an empty list then. It is also inert for a player who already rents something natively —
    /// that player's gate is open without help.
    /// </summary>
    public static class AccessGates
    {
        private const string Tag = "[AccessGates]";

        /// <summary>Dialog types already logged this session (G5: one INFO line per dialog type).</summary>
        private static readonly HashSet<string> _logged = new HashSet<string>();

        // ── The predicate ─────────────────────────────────────────────────────

        /// <summary>The local player is a merger member, or holds Business access to at least one other
        /// player's registration. Live read at the moment of the call — never cached across frames.
        /// The "shared with me" test is the mod's existing one (SharedShopSchedule.IsSharedShop, which asks
        /// the host-pushed direct-grant list GrantSync.IsSharedManage); no new predicate is invented here.</summary>
        internal static bool HasAccessTenancy()
        {
            try
            {
                if (MergerSync.IAmMember) return true;                                  // MergerFlip has already raised these locally
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return false;     // single-player / no session
                if (GrantSync.SharedManageCount == 0) return false;                     // cheap exit: nothing is shared with me
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return false;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    if (SharedShopSchedule.IsSharedShop(reg, AddrOf(reg))) return true;
                }
                return false;
            }
            catch { return false; }
        }

        private static string AddrOf(BuildingRegistration reg)
        {
            try { return reg != null ? GameStateReader.AddressKey(reg) : ""; } catch { return ""; }
        }

        /// <summary>True when this machine rents nothing of its own — i.e. the native gate is shut.</summary>
        private static bool RentsNothingNatively()
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return false;
                return !gi.BuildingRegistrations.Any(r => r != null && r.RentedByPlayer);
            }
            catch { return false; }
        }

        // ── Call-scoped raise / lower over the access set ─────────────────────

        /// <summary>Raise tenancy on every registration this player may manage through a Business grant, for
        /// the duration of one synchronous dialog constructor. Returns the list the Finalizer must lower.</summary>
        private static List<BuildingRegistration> OpenGate(string dialogType)
        {
            var raised = new List<BuildingRegistration>();
            try
            {
                if (!HasAccessTenancy()) return raised;        // G4: inert without access/merger
                if (!RentsNothingNatively()) return raised;    // the gate is already open natively (a merger member's flipped shops land here)
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return raised;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    string addr = AddrOf(reg);
                    if (!SharedShopSchedule.IsSharedShop(reg, addr)) continue;
                    if (SharedShopVisibility.RaiseTenancy(reg, addr)) raised.Add(reg);
                }
                if (raised.Count > 0) LogOnce(dialogType, raised.Count);
            }
            catch (Exception ex)
            {
                try { Plugin.Logger.LogWarning($"{Tag} '{dialogType}' gate: {ex.Message}"); } catch { }
            }
            return raised;
        }

        /// <summary>Lower exactly the registrations this call raised.</summary>
        private static void CloseGate(List<BuildingRegistration> raised)
        {
            if (raised == null) return;
            foreach (var reg in raised)
            {
                try { SharedShopVisibility.LowerTenancy(reg, raised: true); } catch { }
            }
        }

        private static void LogOnce(string dialogType, int count)
        {
            try
            {
                if (!_logged.Add(dialogType)) return;
                Plugin.Logger.LogInfo($"{Tag} '{dialogType}' opened for an access holder ({count} registration(s) raised)");
            }
            catch { }
        }

        /// <summary>How many registrations the access set holds — for the log line of the one gate that needs
        /// no raise (the food-delivery predicate).</summary>
        private static int AccessRegistrationCount()
        {
            int n = 0;
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return 0;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    if (SharedShopSchedule.IsSharedShop(reg, AddrOf(reg))) n++;
                }
            }
            catch { }
            return n;
        }

        // ── R1/R2: the game's OWN notion of the active dialog ─────────────────
        //
        // DialogController.cs:22  `public static DialogController current;` — the open dialog window.
        // DialogController.cs:38  `public Dialog dialog;`                  — the dialog object it is running.
        // Both are set when a conversation starts (UI.Dialog/DialogUI.cs:50 and :57;
        // UI.Smartphone.Apps.Contacts/ContactsApp.cs:1112 then the CallDialogFactory switch, :1120-1140) and both
        // are cleared to null on EVERY close route — hang up, finish, back out, phone closed (DialogUI.StopDialog
        // :92-93; ContactsApp.FinishDialog :1147-1148; ContactsApp.CancelDialog :1160-1161; ContactsApp :1204).
        // So: no flag of our own and no timer — we read the game's own field. All eight contacts are
        // `Dialogs.Dialog` subclasses (Dialogs/*.cs:11-16), so a type test on that field is exact.
        // NOTE the ordering: DialogUI.cs:57 assigns `dialog` only AFTER the ctor has run inside GetDialog, so
        // this is false during the ctor itself — the r1 ctor raise still carries the first entry's gate.

        // ── R1 (D9 r3): TWO type sets, not one ────────────────────────────────
        //
        // ACCESS-DIALOG set = all eight patched above. Every one of them now ANSWERS an access holder, because
        // the user's rule (2026-09-11) is "remove the rent-a-building requirement; change NOTHING about what
        // access allows" — and for several of them "the removal may make no actual difference to what they can
        // do". Answering is free: the NPC picks up the phone, then applies its own rules.
        //
        // PICKER-WIDEN set = two. Listing someone else's shop in a contact's "which of your businesses?" picker
        // is NOT just an answer — it aims the call's follow-up at that shop. That is legitimate only where the
        // same follow-up is already inside what a Business grant allows, i.e. where the grant already carries
        // the matching BizMan tab for an ORDINARY shared shop (SharedShopVisibility.AllowedTabs, :109-149):
        //   WholesaleStoreManagerDialog → a delivery contract  → "Deliveries" tab (AllowedTabs:143) → WITHIN.
        //   MarketingAgencyDialog       → a marketing campaign → "Marketing"  tab (AllowedTabs:144) → WITHIN.
        // The other six are BEYOND access, so they are answered but NOT widened and their pickers keep listing
        // only buildings the player rents himself:
        //   RecruitmentAgencyDialog             hires onto the OWNER's payroll — no tab, no grant.
        //   MovingServiceDialog                 moves furniture between buildings — no tab (and origin ==
        //                                       destination was selectable while it was widened).
        //   InteriorInstallationFirmAgentDialog interior work on the owner's building — no tab.
        //   ImportManagerDialog                 wants a rented WAREHOUSE; a shared warehouse grants no imports.
        //   FoodDeliveryDialog                  no tab.
        //   FurnitureStoreManagerDialog         furnishing someone else's shop — no tab.
        // Warehouses and factories are held back even for the two widened types: AllowedTabs gives a shared
        // warehouse or factory neither "Deliveries" nor "Marketing" (:127-141). That exclusion, and the
        // picker's own filter delegate, are applied by the append Postfix in SharedShopVisibility.cs.
        //
        // PICKER-NARROW set (2026-09-12, user-approved) = FIVE service dialogs whose in-dialog business picker
        // must never offer a merged PARTNER's shop - not even to a company member, and not even on a stand-in
        // machine that merely runs the absent owner's world (MergerAbsence.SimulatesHere):
        //   MovingServiceContractSettings           origin picker + destination picker
        //   InteriorInstallationFirmDesignSettings  design-target picker
        //   RecruitmentSettings                     campaign-target picker - ONLY PARTLY since H-MERGERHIRE-1
        //                                           (2026-09-19): a merger-FLIPPED partner shop whose owner is
        //                                           in the session is offered again and the booking is ROUTED
        //   FurnitureDeliveryContractSettings       delivery destination (calls the base method)
        //   FoodDeliveryContractSettings            delivery destination (inherits the base method)
        // Each books a PER-MACHINE contract: written on the booking machine, paid from the shared wallet and
        // executed there against that machine's replica of the shop - the owner never hears of it, and these
        // bookings are not in the absence hand-over lists, so a stand-in's booking would strand on its own
        // machine at the owner's return. Patched in the G5 region at the foot of this file. TWO service dialogs
        // are ROUTED instead of narrowed: the WHOLESALE dialog's DeliveryContractSettings is a SEPARATE class
        // and was never among them (its deliveries route as "mergercontract"), and RECRUITMENT joined it
        // (H-MERGERHIRE-1, op "mergercampaign"). Recruitment's picker still hides a partner shop whenever the
        // booking could not reach the shop's REAL OWNER: an ABSENT owner's shop (someone is standing in for
        // them, MergerAbsence.AwayAnywhere) stays hidden, because the campaign's hourly tick reads the owner's
        // own RecruitmentCampaigns list and a booking is in no absence hand-over list.

        /// <summary>The name of the dialog the game currently has open, when it is one of the TWO whose picker may
        /// be widened (the table above); else null. R3: the cheap MP gates run first, so single-player and a
        /// session with nothing shared never reach the DialogController type test.</summary>
        internal static string ActiveAccessDialogType()
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return null;   // R3: cheapest gate first
                if (GrantSync.SharedManageCount == 0) return null;                   // nothing is shared with me
                var ctrl = DialogController.current;
                if (ctrl == null) return null;
                var d = ctrl.dialog;
                if (d == null) return null;
                if (d is WholesaleStoreManagerDialog || d is MarketingAgencyDialog)  // PickerWidenTypes only
                    return d.GetType().Name;
                return null;
            }
            catch { return null; }
        }

        /// <summary>True while one of the two picker-widen contacts is the dialog the game itself has open.</summary>
        internal static bool AccessDialogActive() => ActiveAccessDialogType() != null;

        /// <summary>Dialog types whose picker scope has already been logged this session (R5).</summary>
        private static readonly HashSet<string> _pickerLogged = new HashSet<string>();

        /// <summary>R5: one INFO line per dialog type per session, the first time the picker scope opens.</summary>
        internal static void LogPickerScopeOnce(string dialogType, int count)
        {
            try
            {
                if (string.IsNullOrEmpty(dialogType) || !_pickerLogged.Add(dialogType)) return;
                Plugin.Logger.LogInfo($"{Tag} picker scope opened for '{dialogType}' ({count} shared shop(s) listed)");
            }
            catch { }
        }

        // ── G2: the seven dialog constructors ─────────────────────────────────
        // Each reads the live tenancy list inline in its own parameterless constructor, so the raise has to
        // straddle the constructor itself. Decompile line of the gate in each ctor is quoted per class.

        /// <summary>FurnitureStoreManagerDialog.cs:21 — `BuildingRegistrations.Any(x => x.RentedByPlayer)`.</summary>
        [HarmonyPatch(typeof(FurnitureStoreManagerDialog), MethodType.Constructor, new Type[0])]
        public static class Patch_FurnitureStoreManagerDialog_Ctor
        {
            static void Prefix(out List<BuildingRegistration> __state) { __state = OpenGate("FurnitureStoreManagerDialog"); }
            static void Finalizer(List<BuildingRegistration> __state) { CloseGate(__state); }
        }

        /// <summary>InteriorInstallationFirmAgentDialog.cs:27 — `BuildingRegistrations.Any(x => x.RentedByPlayer)`.</summary>
        [HarmonyPatch(typeof(InteriorInstallationFirmAgentDialog), MethodType.Constructor, new Type[0])]
        public static class Patch_InteriorInstallationFirmAgentDialog_Ctor
        {
            static void Prefix(out List<BuildingRegistration> __state) { __state = OpenGate("InteriorInstallationFirmAgentDialog"); }
            static void Finalizer(List<BuildingRegistration> __state) { CloseGate(__state); }
        }

        /// <summary>MarketingAgencyDialog.cs:18 — tenancy AND a non-blank BusinessName. The raised
        /// registrations carry their own names; nothing extra is done for the name test.</summary>
        [HarmonyPatch(typeof(MarketingAgencyDialog), MethodType.Constructor, new Type[0])]
        public static class Patch_MarketingAgencyDialog_Ctor
        {
            static void Prefix(out List<BuildingRegistration> __state) { __state = OpenGate("MarketingAgencyDialog"); }
            static void Finalizer(List<BuildingRegistration> __state) { CloseGate(__state); }
        }

        /// <summary>MovingServiceDialog.cs:23 — `BuildingRegistrations.Exists(x => x.RentedByPlayer)`.</summary>
        [HarmonyPatch(typeof(MovingServiceDialog), MethodType.Constructor, new Type[0])]
        public static class Patch_MovingServiceDialog_Ctor
        {
            static void Prefix(out List<BuildingRegistration> __state) { __state = OpenGate("MovingServiceDialog"); }
            static void Finalizer(List<BuildingRegistration> __state) { CloseGate(__state); }
        }

        /// <summary>RecruitmentAgencyDialog.cs:26 — tenancy AND a non-blank BusinessName; the separate
        /// DiplomaName.BasicHr test (line 27) is untouched and still refuses without the diploma.</summary>
        [HarmonyPatch(typeof(RecruitmentAgencyDialog), MethodType.Constructor, new Type[0])]
        public static class Patch_RecruitmentAgencyDialog_Ctor
        {
            static void Prefix(out List<BuildingRegistration> __state) { __state = OpenGate("RecruitmentAgencyDialog"); }
            static void Finalizer(List<BuildingRegistration> __state) { CloseGate(__state); }
        }

        /// <summary>WholesaleStoreManagerDialog.cs:21 — tenancy AND a non-blank BusinessName.</summary>
        [HarmonyPatch(typeof(WholesaleStoreManagerDialog), MethodType.Constructor, new Type[0])]
        public static class Patch_WholesaleStoreManagerDialog_Ctor
        {
            static void Prefix(out List<BuildingRegistration> __state) { __state = OpenGate("WholesaleStoreManagerDialog"); }
            static void Finalizer(List<BuildingRegistration> __state) { CloseGate(__state); }
        }

        /// <summary>ImportManagerDialog.cs:19 — a rented WAREHOUSE with a business. Deliberately no special
        /// case: if the access set holds no warehouse the manager still shows NoWarehouse(), which is what
        /// "no change to what the permission allows" means.</summary>
        [HarmonyPatch(typeof(ImportManagerDialog), MethodType.Constructor, new Type[0])]
        public static class Patch_ImportManagerDialog_Ctor
        {
            static void Prefix(out List<BuildingRegistration> __state) { __state = OpenGate("ImportManagerDialog"); }
            static void Finalizer(List<BuildingRegistration> __state) { CloseGate(__state); }
        }

        // ── G2: the eighth gate is a named predicate, not an inline read ───────

        /// <summary>FoodDeliveryDialog.cs:65 `private static bool PlayerHasRentedBuilding()` =
        /// `BuildingHelper.GetPlayerBuildingRegistrations().Count > 0`. A named predicate, so it is answered
        /// directly — no tenancy is raised for this one.</summary>
        [HarmonyPatch(typeof(FoodDeliveryDialog), "PlayerHasRentedBuilding")]
        public static class Patch_FoodDeliveryDialog_PlayerHasRentedBuilding
        {
            static void Postfix(ref bool __result)
            {
                try
                {
                    if (__result) return;
                    if (!HasAccessTenancy()) return;    // G4: inert without access/merger
                    int n = AccessRegistrationCount();  // R4: exactly the set the seven ctors would raise
                    if (n == 0) return;                 // a merger member whose company owns nothing stays refused, like the other seven
                    __result = true;
                    LogOnce("FoodDeliveryDialog", n);
                }
                catch { }
            }
        }

        // ── G5: partner-shop service pickers (2026-09-12) ───────────────────
        //
        // The eight gates above say "the NPC answers the phone". This region says "and your partner's shop is
        // not on the menu": the five service dialogs listed in the header book per-machine contracts that
        // execute on the BOOKING machine, so a partner's shop is never bookable in them, on any machine.
        //
        // ONE EXCEPTION (H-MERGERHIRE-1, 2026-09-19): RECRUITMENT. A campaign is no longer booked here at all -
        // the member's confirmation is ROUTED to the shop's real owner, who pays for it and whose own hourly
        // tick finds the candidates (RecruitmentHelper.RunHourly), and the mod already shares those candidates
        // back to every co-member (CompanyCandidates.cs:13-45). So that picker offers a partner shop again -
        // but only a merger-FLIPPED one whose owner can actually take the booking: not while THIS machine
        // stands in for them (SimulatesHere), not while any machine does (MergerAbsence.AwayAnywhere), and
        // never a plain foreign business, which no route exists for.
        //
        // NO NEW ON-SCREEN TEXT: the shops are simply not offered, and when nothing remains the dialogs' own
        // native empty branches disable the controls (MovingServiceContractSettings.cs:93-97,
        // InteriorInstallationFirmDesignSettings.cs:114-117).
        //
        // INERT without a merger: PartnerShop (below) can only be true here if a merger's presentation flip
        // put the partner's registrations back, because the helper postfix on
        // BuildingHelper.GetPlayerBuildingRegistrations (MPPatches.cs ~:5537-5544) has already removed foreign
        // registrations before these filters ever run. All five targets are private, so string method names as
        // the constructor gates do; every postfix is wrapped in try/catch and leaves __result alone on failure.

        private static readonly HashSet<string> _pickerNarrowLogged = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>M6 (2026-09-12): "this registration is a PARTNER's shop". IsForeignPlayerBusiness ALONE is false
        /// under a merger — the presentation flip PARKS the runner stamp and leaves the registration's rival id empty
        /// (GameStatePatcher.cs:5184-5192, `reg.businessOwnerRivalId = flipped ? "" : newBusinessOwner;`) — which is
        /// exactly the case these five dialogs were built for, so the flip table (and the absence simulation, where
        /// this machine runs an absent member's shop) answer for it instead.</summary>
        private static bool PartnerShop(BuildingRegistration reg)
        {
            try
            {
                if (reg == null) return false;
                if (GameStatePatcher.IsForeignPlayerBusiness(reg)) return true;
                string key = GameStateReader.AddressKey(reg);
                if (string.IsNullOrEmpty(key)) return false;
                return (MergerFlip.FlippedCount > 0 && MergerFlip.IsFlipped(key)) || MergerAbsence.SimulatesHere(key);
            }
            catch { return false; }
        }

        /// <summary>One log line per dialog per session - one line per dropped registration would be chatty.</summary>
        private static void LogPickerOnce(string dialogType)
        {
            try
            {
                if (!_pickerNarrowLogged.Add(dialogType)) return;
                Plugin.Logger.LogInfo($"[AccessGates] {dialogType}: a partner shop is not offered here (booked only by the machine that runs it).");
            }
            catch { }
        }

        [HarmonyPatch(typeof(global::UI.Dialog.MovingServiceContractSettings), "PlayerBuildingFilterOrigin")]
        public static class Patch_MovingServiceContractSettings_FilterOrigin
        {
            static void Postfix(BuildingRegistration buildingRegistration, ref bool __result)
            {
                try
                {
                    if (!__result || !PartnerShop(buildingRegistration)) return;   // M6: the flip parks the stamp
                    __result = false;
                    LogPickerOnce("MovingService");
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(global::UI.Dialog.MovingServiceContractSettings), "PlayerBuildingFilterDestination")]
        public static class Patch_MovingServiceContractSettings_FilterDestination
        {
            static void Postfix(BuildingRegistration buildingRegistration, ref bool __result)
            {
                try
                {
                    if (!__result || !PartnerShop(buildingRegistration)) return;   // M6: the flip parks the stamp
                    __result = false;
                    LogPickerOnce("MovingService");
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(global::UI.Dialog.InteriorInstallationFirmDesignSettings), "PlayerBuildingFilter")]
        public static class Patch_InteriorInstallationFirmDesignSettings_Filter
        {
            static void Postfix(BuildingRegistration buildingRegistration, ref bool __result)
            {
                try
                {
                    if (!__result || !PartnerShop(buildingRegistration)) return;   // M6: the flip parks the stamp
                    __result = false;
                    LogPickerOnce("InteriorInstallationFirm");
                }
                catch { }
            }
        }

        /// <summary>H-MERGERHIRE-1: may a recruitment campaign be booked on this partner shop from HERE? Only a
        /// merger-FLIPPED co-member shop qualifies (a plain foreign business has no route), and only while the
        /// shop's real owner can take the booking themselves: the campaign is charged to that owner and ticks in
        /// that owner's save, and no absence hand-over list carries one, so a stand-in must never be offered it.
        /// The two absence reads are the whole test - this machine standing in, and any machine standing in.</summary>
        private static bool RecruitmentRoutable(BuildingRegistration reg)
        {
            try
            {
                if (reg == null || MergerFlip.FlippedCount == 0) return false;
                string key = GameStateReader.AddressKey(reg);
                if (string.IsNullOrEmpty(key)) return false;
                if (!MergerFlip.IsFlipped(key)) return false;            // a plain foreign business stays hidden
                if (MergerAbsence.SimulatesHere(key)) return false;      // I am the stand-in: the booking would strand here
                if (MergerAbsence.AwayAnywhere(key)) return false;       // someone else stands in: the owner is away
                return true;
            }
            catch { return false; }
        }

        [HarmonyPatch(typeof(global::UI.Dialog.RecruitmentSettings), "PlayerBuildingFilter")]
        public static class Patch_RecruitmentSettings_Filter
        {
            static void Postfix(BuildingRegistration buildingRegistration, ref bool __result)
            {
                try
                {
                    if (!__result || !PartnerShop(buildingRegistration)) return;   // M6: the flip parks the stamp
                    if (RecruitmentRoutable(buildingRegistration))                 // H-MERGERHIRE-1: routed, so it stays listed
                    { LogRecruitmentRouteOnce(GameStateReader.AddressKey(buildingRegistration)); return; }
                    __result = false;
                    LogPickerOnce("RecruitmentAgency");
                }
                catch { }
            }
        }

        private static readonly HashSet<string> _recruitRouteLogged = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>One INFO line per partner shop per session, the first time the recruitment picker offers it.</summary>
        private static void LogRecruitmentRouteOnce(string key)
        {
            try
            {
                if (string.IsNullOrEmpty(key) || !_recruitRouteLogged.Add(key)) return;
                Plugin.Logger.LogInfo($"[Merger] recruitment: '{key}' is offered here - a campaign booked on it is routed to its owner.");
            }
            catch { }
        }

        /// <summary>Furniture delivery overrides this and calls base (FurnitureDeliveryContractSettings.cs:25-27),
        /// food delivery inherits it unchanged - one patch on the base covers both. The wholesale dialog's
        /// DeliveryContractSettings is a separate MonoBehaviour with its own filter and is not touched.</summary>
        [HarmonyPatch(typeof(global::UI.Dialog.DeliveryContractSettingsBase), "GetDeliveryDestinations")]
        public static class Patch_DeliveryContractSettingsBase_Destinations
        {
            static void Postfix(ref List<BuildingRegistration> __result)
            {
                try
                {
                    if (__result == null || __result.Count == 0) return;
                    int before = __result.Count;
                    __result.RemoveAll(r => PartnerShop(r));   // M6: the flip parks the stamp
                    if (__result.Count != before) LogPickerOnce("DeliveryContract");
                }
                catch { }
            }
        }

        // ── G3: loan cap — NOT PATCHED, deliberately ──────────────────────────
        //
        // The brief asks for a Postfix on BankDialog.GetEconomyMaxLoanAmount that lifts a $15,500 tutorial
        // CAP for access holders. The decompiled method (Dialogs/BankDialog.cs:315-328) does the opposite:
        //
        //     float a = 0f;
        //     if (TutorialHelper.IsTutorialEnabled() && !CompletedQuestEntries.Contains("tutorial_quest_…_1"))
        //         a = 15500f;                        // ← a FLOOR, then …
        //     a = Mathf.Max(a, wealthBeforeLoans);   // … Max() against the economy figures
        //     a = Mathf.Max(a, dailyIncome * payBackDays * 0.25f);
        //     return Mathf.FloorToInt(Mathf.Max(0f, a - remainingLoanAmount));
        //
        // $15,500 is a guaranteed MINIMUM during the tutorial, not a ceiling: the branch can only ever raise
        // the result. "The ungated amount the method would return with the quest satisfied" is therefore
        // less than or equal to what it returns today, so the requested patch would LOWER an access holder's
        // maximum loan — the opposite of the user's intent. Standing rule 7 (the code wins over the brief),
        // so nothing is patched here and the question goes back to the manager. It is moot in MP today in
        // any case: MP keeps the tutorial off (src/MPServer.cs:3799), and with the tutorial off the branch
        // never runs. Nothing in this file touches CompletedQuestEntries or any other save-persisted field.
    }
}
