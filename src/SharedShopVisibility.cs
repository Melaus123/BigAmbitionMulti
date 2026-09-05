using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BigAmbitions.Tags;                       // TagRef
using Buildings;                               // BuildingRegistration
using HarmonyLib;
using Helpers;                                 // BuildingHelper, BusinessTypeHelper, BuildingTypeHelper
using UI.Smartphone.Apps.BizMan;               // BusinessScrollerController
using UI.Smartphone.Apps.BizMan.Schedule;      // ScheduleHelper, ScheduleDaySelectionController, ScheduleDayButton, ScheduleCellView
using UnityEngine;
using UnityEngine.UI;

namespace BigAmbitionsMP
{
    /// <summary>
    /// SHARED-SHOP MANAGEMENT (the Business PERMISSION feature) — slice 2: visibility, tint, guards.
    /// Plan: .modding/03-systems/shared-shop-management-plan.md §2.1 / §2.2 / §4 (rulings 12, 14).
    ///
    /// THIS IS NOT THE MERGER. Merger-flipped shops are excluded from everything here (they have their own feature);
    /// the player's own shops, AI shops and single-player are untouched — every patch keys on "another player's
    /// shop" and most on "another player's shop that is SHARED with me through a Business grant".
    ///
    /// What it does, and nothing more:
    ///  • VISIBILITY (call-scoped, never an ownership flag): the shared shops are APPENDED to exactly two lists —
    ///    the BizMan main list (BusinessScrollerController.PopulateAllModels) and the BizMan page's business
    ///    dropdown (GetPlayerBuildingRegistrations, only while BizManBusiness.RefreshData is running). Every other
    ///    "pick one of your businesses" surface (employee assignment, mass actions, contracts, marketing, recruitment,
    ///    moving, interior firm, logistics, purchasing) keeps hiding them, so the player's own employees can never be
    ///    pointed at a shared shop and no contract can be opened on one.
    ///  • TINT: the list card and the page's business-type label are coloured the mod's "shared with me" teal
    ///    (HousingMapCues.SharedColor — the same colour the city map and building hover already use) so the player
    ///    can see it is someone else's. No text is added (ruling 17).
    ///  • TAB SET (allow-list): a shared shop's page shows Presentation + Schedule (factories too); warehouses and
    ///    headquarters show Presentation only in this slice. Any OTHER player's shop that is NOT shared (e.g. the
    ///    owner just went offline while the page was open) collapses to Presentation — the page can never show the
    ///    full native tab set for a shop the player does not own.
    ///  • GREYED CONTROLS (ruling 14: blocked = greyed, never a live control that does nothing): on another player's
    ///    shop — the contract-termination button, the rival "show employees" panel button, "open in EconoView"; on
    ///    a shared shop's Schedule tab — the week and per-day auto-fill buttons (slice 3 wires auto-fill to the
    ///    owner's staff) and the cinema licensing-fee toggles (money). Copy / paste / paste-to-all / clear keep
    ///    working (ruling 14 d). The temporarily-closed toggle was already routed to the owner for helpers.
    ///  • Nothing is put on screen beyond colour and greying (user ruling: no messages from this feature).
    /// </summary>
    public static class SharedShopVisibility
    {
        private const string Tag = "[SharedShop]";

        /// <summary>Another player's shop on this machine — shared or not — that is not merger-flipped. The fail-closed
        /// predicate for tab pruning and greying. (Offline MP saves still carry ownership stamps; pruning is the safe
        /// direction there, so there is deliberately no connection gate — same as the 2026-07-16 hide patches.)</summary>
        public static bool IsOtherPlayersShop(BuildingRegistration reg, string addr)
        {
            if (reg == null || string.IsNullOrEmpty(addr)) return false;
            try
            {
                if (MergerFlip.IsFlipped(addr)) return false;
                if (GameStatePatcher.IsForeignPlayerBusiness(reg)) return true;   // the contaminated case (rented here + stamped to another player)
                // The normal case: another SESSION player is stamped as the runner and this machine does not rent it.
                // (An AI rival's stamp is not a player — AI shop pages keep their native takeover controls.)
                string stamp = ""; try { stamp = reg.businessOwnerRivalId?.ToString() ?? ""; } catch { }
                return !string.IsNullOrEmpty(stamp) && stamp != MPConfig.PlayerId && GameStatePatcher.IsSessionPlayerRivalId(stamp);   // review r6 #2: roster-inclusive — the live-only test let the whole lock (incl. the terminate-contract hard stop) lapse while the owner was disconnected
            }
            catch { return false; }
        }

        // ── Call-scoped tenancy (plan §2.1 item 2) ────────────────────────────
        // A shared shop's replica is NOT RentedByPlayer on this machine, and the page's native code keys its tab set
        // (SetUpTabs) and its Presentation view (OnEnable → SetActiveView / SetAiOwned) on exactly that flag. For the
        // duration of those two synchronous calls ONLY, the flag is raised on THAT registration and lowered again in
        // the finalizer — it can never span a tick, a save, or a publish (the binding rule on ownership flips).
        // Second caller since 2026-08-22: SharedShopStaff.ScopeToOwnerShops, around the game's own "can an employee be
        // assigned to this business" filter, which is gated on the same flag. Internal, not private, so there is ONE
        // raise/lower pair in the mod rather than a copy per surface.
        internal static bool RaiseTenancy(BuildingRegistration reg, string addr)
        {
            try
            {
                if (reg == null || reg.RentedByPlayer) return false;
                if (!SharedShopSchedule.IsSharedShop(reg, addr)) return false;
                reg.RentedByPlayer = true;
                return true;
            }
            catch { return false; }
        }
        internal static void LowerTenancy(BuildingRegistration reg, bool raised)
        {
            if (!raised || reg == null) return;
            try { reg.RentedByPlayer = false; } catch { }
        }

        private static string AddrOf(BuildingRegistration reg)
        {
            try { return reg != null ? GameStateReader.AddressKey(reg) : ""; } catch { return ""; }
        }

        /// <summary>The same teal the mod already uses for "shared with me" on the city map and the building hover
        /// (HousingMapCues.SharedColor) — one colour means "someone else's, shared with you" everywhere.</summary>
        private static Color Tint => HousingMapCues.SharedColor;

        /// <summary>The same teal, for the other shared-shop surfaces that need it (7c colours the agency
        /// call's business dropdown with it).</summary>
        internal static Color SharedTint => HousingMapCues.SharedColor;

        // ── Tab allow-list ────────────────────────────────────────────────────

        /// <summary>The tabs another player's shop page may show in this slice, by business type. RealEstate is the
        /// LOCAL player's own deed (the game adds it from the local realEstate list — a bought building can host another
        /// player's tenant shop) and stays native whenever the local player holds it.</summary>
        private static List<string> AllowedTabs(BuildingRegistration reg, bool shared)
        {
            var list = new List<string> { "Presentation" };
            try
            {
                var addr = reg.Address;
                if (SaveGameManager.Current?.realEstate != null && SaveGameManager.Current.realEstate.Exists(x => x != null && x.address == addr))
                    list.Add("RealEstate");
            }
            catch { }
            if (!shared) return list;
            string type = ""; try { type = reg.businessTypeName ?? ""; } catch { }
            switch (type)
            {
                case "ba:businesstype_headquarters":  // ruling 27: HQ stays out of the permission feature
                case "ba:businesstype_empty":
                case "":
                    break;
                case "ba:businesstype_warehouse":
                    list.Add("Drivers");              // slice 6b: carried slot info + routed assignment
                    list.Add("Inventory");            // slice 6a: owner-computed figures; Sell All greyed (ruling 29)
                    list.Add("Settings");             // slice 7d (user 2026-08-26): rename + logo routed, type/shutdown greyed
                    break;
                case "ba:businesstype_factory":
                    list.Add("Factory");              // slice 6c: carried config/state + routed edits (rename allowed, 2026-08-24)
                    list.Add("Schedule");
                    list.Add("Drivers");              // slice 6b
                    list.Add("Inventory");            // slice 6a — no pricing tab natively (ruling 30)
                    list.Add("Settings");             // slice 7d (user 2026-08-26): rename + logo routed, type/shutdown greyed
                    break;
                default:
                    list.Add("Schedule");             // every ordinary business
                    list.Add("InventoryPricing");     // slice 4: the helper sets this shop's retail prices
                    list.Add("Insight");              // slice 7a: read-only dashboard, owner-carried figures
                    list.Add("Deliveries");           // slice 7b: carried contracts + routed edits (ruling 33: billed-on-delivery = allowed)
                    list.Add("Marketing");            // slice 7c: carried campaigns + routed toggles/cancel (ruling 33/40: a daily expense, never a click-time cost)
                    list.Add("Settings");             // slice 7d: rename + logo routed (ruling 42); type change and shutdown greyed (ruling 34)
                    break;
            }
            return list;
        }

        private static readonly System.Reflection.FieldInfo _fTabs        = AccessTools.Field(typeof(BizManBusiness), "_tabs");
        private static readonly System.Reflection.FieldInfo _fMenu        = AccessTools.Field(typeof(BizManBusiness), "menu");
        private static readonly System.Reflection.FieldInfo _fSelectedTab = AccessTools.Field(typeof(BizManBusiness), "_selectedTab");
        private static readonly System.Reflection.FieldInfo _fTypeLabel   = AccessTools.Field(typeof(BizManBusiness), "businessTypeLabel");
        private static readonly HashSet<string> _prunedLogged = new();

        /// <summary>After the game builds the tab list for the page's registration: another player's shop keeps only
        /// the allowed tabs, the tab buttons are re-shown accordingly, and the selected tab is moved onto an allowed
        /// one (the game's own dropdown handler indexes _tabs[1] when the current tab is gone — a one-tab list would
        /// throw, so the selection is fixed HERE, before that line runs).</summary>
        [HarmonyPatch(typeof(BizManBusiness), "SetUpTabs")]
        public static class Patch_BizManBusiness_SetUpTabs_Prune
        {
            static void Prefix(BizManBusiness __instance, out bool __state)
            {
                __state = false;
                try { __state = RaiseTenancy(__instance.buildingRegistration, AddrOf(__instance.buildingRegistration)); } catch { }
            }
            static void Finalizer(BizManBusiness __instance, bool __state)
            {
                try { LowerTenancy(__instance.buildingRegistration, __state); } catch { }
            }
            static void Postfix(BizManBusiness __instance)
            {
                try
                {
                    var reg = __instance.buildingRegistration;
                    string addr = AddrOf(reg);
                    if (!IsOtherPlayersShop(reg, addr)) return;
                    bool shared = SharedShopSchedule.IsSharedShop(reg, addr);
                    var allowed = AllowedTabs(reg, shared);
                    if (_fTabs?.GetValue(__instance) is not List<string> tabs) return;
                    tabs.RemoveAll(t => !allowed.Contains(t));
                    if (_fMenu?.GetValue(__instance) is Transform menu)
                        foreach (Transform item in menu)
                            item.gameObject.SetActive(tabs.Contains(item.name));
                    string sel = _fSelectedTab?.GetValue(__instance) as string ?? "";
                    if (!tabs.Contains(sel))
                        _fSelectedTab?.SetValue(__instance, tabs.Contains("Schedule") ? "Schedule" : "Presentation");
                    if (_prunedLogged.Add(addr + (shared ? "|s" : "|u")))
                        Plugin.Logger.LogInfo($"{Tag} '{addr}' is {(shared ? "shared with me" : "another player's, not shared")} — management tabs limited to: {string.Join(", ", tabs)}.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} tab prune: {ex.Message}"); }
            }
        }

        /// <summary>The initial tab the game picks (Insight / Factory / Drivers …) may not be allowed on another
        /// player's shop — land on Schedule when it is allowed, else Presentation. Runs before SetUpTabs on the
        /// BizMan.Open route, so it derives the allow-list directly.</summary>
        [HarmonyPatch(typeof(BizManBusiness), nameof(BizManBusiness.SetInitialTab))]
        public static class Patch_BizManBusiness_SetInitialTab_Allowed
        {
            static void Postfix(BizManBusiness __instance)
            {
                try
                {
                    var reg = __instance.buildingRegistration;
                    string addr = AddrOf(reg);
                    if (!IsOtherPlayersShop(reg, addr)) return;
                    var allowed = AllowedTabs(reg, SharedShopSchedule.IsSharedShop(reg, addr));
                    string sel = _fSelectedTab?.GetValue(__instance) as string ?? "";
                    // Native defaults gate on RentedByPlayer (false on a replica) and land on Presentation —
                    // re-derive them for a shared building so the page opens where the owner's would
                    // (field 2026-08-24): factory → Factory, warehouse → Drivers, ordinary → Insight
                    // (allowed since slice 7a — before that Schedule stood in; the fallback chain still
                    // lands there when Insight is pruned). An explicitly requested tab was vetted above.
                    string type = ""; try { type = reg.businessTypeName ?? ""; } catch { }
                    string want = type == "ba:businesstype_factory" ? "Factory"
                                : type == "ba:businesstype_warehouse" ? "Drivers"
                                : "Insight";
                    if (sel == "Presentation" && allowed.Contains(want)) { _fSelectedTab?.SetValue(__instance, want); return; }
                    if (!allowed.Contains(sel))
                        _fSelectedTab?.SetValue(__instance, allowed.Contains(want) ? want : allowed.Contains("Schedule") ? "Schedule" : "Presentation");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} initial tab: {ex.Message}"); }
            }
        }

        // ── Call-scoped visibility: the BizMan dropdown and the BizMan main list ──

        private static int _bizManRefreshDepth;   // > 0 only while BizManBusiness.RefreshData runs (its dropdown read)
        public static bool InBizManRefresh => _bizManRefreshDepth > 0;

        [HarmonyPatch(typeof(BizManBusiness), nameof(BizManBusiness.RefreshData))]
        public static class Patch_BizManBusiness_RefreshData_Scope
        {
            [HarmonyPriority(Priority.First)]   // the increment must run even if a later prefix cancels the method
            static void Prefix() { _bizManRefreshDepth++; }
            static void Finalizer(BizManBusiness __instance)
            {
                if (_bizManRefreshDepth > 0) _bizManRefreshDepth--;   // never latch negative
                try { ApplyPageTintAndGuards(__instance); } catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} page tint: {ex.Message}"); }
            }
        }

        /// <summary>GrantSync calls this when the host's shared-manage set changes (owner offline/online, grant
        /// revoked/given): if the BizMan page is open on an affected shop, rebuild it so the tab set and the greying
        /// follow the new truth at once (ruling 5 — the shop drops out when its owner leaves).</summary>
        public static void OnSharedManageChanged(HashSet<string> changedAddressKeys)
        {
            if (changedAddressKeys == null || changedAddressKeys.Count == 0) return;
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try
                {
                    var ui = InstanceBehavior<UI.UIs>.Instance;
                    var page = ui != null && ui.fullMenu != null && ui.fullMenu.bizMan != null ? ui.fullMenu.bizMan.business : null;
                    if (page == null || !page.gameObject.activeInHierarchy) return;
                    string addr = AddrOf(page.buildingRegistration);
                    if (addr.Length == 0 || !changedAddressKeys.Contains(addr)) return;
                    Plugin.Logger.LogInfo($"{Tag} '{addr}' changed sharing state while its page was open — rebuilding the page.");
                    page.RefreshData();
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} page rebuild on sharing change: {ex.Message}"); }
            });
        }

        private static readonly System.Reflection.FieldInfo _fClosedToggle = AccessTools.Field(typeof(BizManBusiness), "temporarilyClosedToggle");

        /// <summary>The shop's open/closed flag changed on THIS machine because of the OTHER side — a helper's routed
        /// toggle applied here as the owner, or the owner's push applied here as a helper. The game's BizMan page reads
        /// that flag only when it (re)opens (it has no change listener), so an open page kept showing the old switch and
        /// security line (field test 2026-08-21: the helper's click closed the owner's gym, but no open page showed it).
        /// Any thread — the work is queued onto the main thread.</summary>
        public static void RefreshOpenStateIfPageOpen(string addressKey)
        {
            if (string.IsNullOrEmpty(addressKey)) return;
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try
                {
                    var ui = InstanceBehavior<UI.UIs>.Instance;
                    var page = ui != null && ui.fullMenu != null && ui.fullMenu.bizMan != null ? ui.fullMenu.bizMan.business : null;
                    if (page == null || !page.gameObject.activeInHierarchy) return;
                    var reg = page.buildingRegistration;
                    if (reg == null || AddrOf(reg) != addressKey) return;
                    bool closed = false; try { closed = reg.temporarilyClosed; } catch { }
                    if (_fClosedToggle?.GetValue(page) is Toggle t && t.isOn != closed) t.SetIsOnWithoutNotify(closed);
                    try { page.UpdateSecurityInfo(); } catch { }
                    Plugin.Logger.LogInfo($"{Tag} '{addressKey}' is now {(closed ? "closed" : "open")} — the open BizMan page's switch was updated.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} open-state refresh: {ex.Message}"); }
            });
        }

        /// <summary>The game's "my businesses" helper — APPEND the shared shops, but ONLY for the BizMan page's own
        /// dropdown. Runs after the 2026-07-16 hide postfix (Priority.Low), which keeps removing them everywhere else.</summary>
        [HarmonyPatch(typeof(BuildingHelper), nameof(BuildingHelper.GetPlayerBuildingRegistrations))]
        [HarmonyPriority(Priority.Low)]
        public static class Patch_GetPlayerBuildingRegistrations_AppendSharedForBizMan
        {
            static void Postfix(List<BuildingRegistration> __result)
            {
                try
                {
                    if (_bizManRefreshDepth <= 0 || __result == null) return;
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;   // same gate as the list append
                    if (GrantSync.SharedManageCount == 0) return;
                    var gi = SaveGameManager.Current;
                    if (gi?.BuildingRegistrations == null) return;
                    foreach (var reg in gi.BuildingRegistrations)
                    {
                        if (reg == null) continue;
                        string addr = AddrOf(reg);
                        if (!SharedShopSchedule.IsSharedShop(reg, addr)) continue;
                        string name = ""; try { name = reg.BusinessName?.ToString() ?? ""; } catch { }
                        if (name.Length == 0) continue;                      // the page's own filter: established businesses only
                        if (!__result.Contains(reg)) __result.Add(reg);
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} dropdown append: {ex.Message}"); }
            }
        }

        /// <summary>The BizMan main list — APPEND a card for each shared shop (the game's own visibility rule applied:
        /// revenue-generating, or an empty building flagged to show). Runs after the hide postfix.</summary>
        [HarmonyPatch(typeof(BusinessScrollerController), "PopulateAllModels")]
        [HarmonyPriority(Priority.Low)]
        public static class Patch_BizManBusinessList_AppendShared
        {
            private static int _logged;
            static void Postfix(List<BusinessCellView.BusinessModel> allModels)
            {
                try
                {
                    if (allModels == null) return;
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                    if (GrantSync.SharedManageCount == 0) return;
                    var gi = SaveGameManager.Current;
                    if (gi?.BuildingRegistrations == null) return;
                    int added = 0;
                    foreach (var reg in gi.BuildingRegistrations)
                    {
                        if (reg == null) continue;
                        string addr = AddrOf(reg);
                        if (!SharedShopSchedule.IsSharedShop(reg, addr)) continue;
                        bool visible = false;
                        try
                        {
                            if (BusinessTypeHelper.GetData(reg).HasTag(TagRef.Businesstag.generatesrevenue)) visible = true;
                            else if (reg.businessTypeName == "ba:businesstype_empty") visible = BuildingTypeHelper.GetData(reg).HasTag(TagRef.Buildingtypetag.showinbusinesslist);
                        }
                        catch { }
                        if (!visible) continue;
                        bool present = false;
                        foreach (var m in allModels) if (m != null && m.Address == reg.Address) { present = true; break; }
                        if (present) continue;
                        allModels.Add(new BusinessCellView.BusinessModel(reg));
                        added++;
                    }
                    if (added > 0 && _logged++ < 3)
                        Plugin.Logger.LogInfo($"{Tag} business list: showing {added} shop(s) shared with me by other players.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} list append: {ex.Message}"); }
            }
        }

        // ── Tint ──────────────────────────────────────────────────────────────

        private sealed class CardColors { public Color Name; public Color Type; public bool Captured; }
        private static readonly ConditionalWeakTable<BusinessCellView, CardColors> _cardDefaults = new();
        private static readonly System.Reflection.FieldInfo _fCardType = AccessTools.Field(typeof(BusinessCellView), "businessType");

        /// <summary>List card: colour the name and type of a shared shop; restore the card's own colours otherwise
        /// (cells are recycled between rows).</summary>
        [HarmonyPatch(typeof(BusinessCellView), nameof(BusinessCellView.SetData))]
        public static class Patch_BusinessCellView_SetData_Tint
        {
            static void Postfix(BusinessCellView __instance, BusinessCellView.BusinessModel data)
            {
                try
                {
                    if (__instance == null || data == null || __instance.businessName == null) return;
                    // businessType is a TextLocalizationComponent (Localizor — an un-referenced assembly): reach its
                    // TextContainer by reflection, the way HousingMapCues does.
                    var typeText = HousingMapCues.GetMember(_fCardType?.GetValue(__instance), "TextContainer") as TMPro.TMP_Text;
                    var def = _cardDefaults.GetOrCreateValue(__instance);
                    if (!def.Captured) { def.Name = __instance.businessName.color; def.Type = typeText != null ? typeText.color : Color.white; def.Captured = true; }
                    bool shared = false;
                    if (!data.IsRealEstate && data.Address != null)
                    {
                        BuildingRegistration reg = null;
                        try { reg = BuildingHelper.GetBuildingRegistration(data.Address); } catch { }
                        shared = SharedShopSchedule.IsSharedShop(reg, AddrOf(reg));
                    }
                    __instance.businessName.color = shared ? Tint : def.Name;
                    if (typeText != null) typeText.color = shared ? Tint : def.Type;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} card tint: {ex.Message}"); }
            }
        }

        // ── Warehouses and factories (the BizMan page's SECOND list) ──────────
        // WarehouseList.Load enumerates `RentedByPlayer && GetBuildingType() == "ba:buildingtype_warehouse"`. It keys
        // on the BUILDING type, not the business type, which is why FACTORIES appear in it alongside storage
        // warehouses — one list, one fix. Two reasons the owner's never showed (field 2026-08-22): the tenancy filter
        // excludes them, and the teal above only ever covered BusinessCellView cards, which these entries are not.
        // Tenancy is raised for the duration of Load so the game's own code builds the entries (icons, inventory
        // read-outs and all), then lowered in the finalizer — it may never outlive the call.

        private static readonly System.Reflection.FieldInfo _fWarehouseEntry = AccessTools.Field(typeof(WarehouseList), "warehouseEntry");
        private static bool _warehouseLabelWarned;

        /// <summary>Is this entry one of an owner's shared buildings? (Also the stand-down MPPatches' warehouse-list
        /// veto consults, so "listed" and "not vetoed" can never disagree.)</summary>
        internal static bool IsSharedWarehouseEntry(BuildingRegistration reg)
        {
            try { return reg != null && SharedShopSchedule.IsSharedShop(reg, AddrOf(reg)); } catch { return false; }
        }

        [HarmonyPatch(typeof(WarehouseList), "Load")]
        public static class Patch_WarehouseList_Load_IncludeShared
        {
            static void Prefix(out List<BuildingRegistration> __state) { __state = RaiseSharedWarehouses(); }
            static void Finalizer(List<BuildingRegistration> __state)
            {
                if (__state == null) return;
                foreach (var r in __state) LowerTenancy(r, raised: true);
            }
        }

        private static List<BuildingRegistration> RaiseSharedWarehouses()
        {
            var raised = new List<BuildingRegistration>();
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return raised;
                if (GrantSync.SharedManageCount == 0) return raised;
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return raised;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    string addr = AddrOf(reg);
                    if (!SharedShopSchedule.IsSharedShop(reg, addr)) continue;
                    bool warehouseBuilding = false;
                    try { warehouseBuilding = reg.GetBuildingType() == "ba:buildingtype_warehouse"; } catch { }
                    if (!warehouseBuilding) continue;
                    if (RaiseTenancy(reg, addr)) raised.Add(reg);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} warehouse list: {ex.Message}"); }
            return raised;
        }

        /// <summary>Teal the entry the game has just appended. These are plain instantiated rows, not recycled table
        /// cells, so the colour is written directly and needs no restore path.</summary>
        [HarmonyPatch(typeof(WarehouseList), "SetUpEntry")]
        public static class Patch_WarehouseList_SetUpEntry_Tint
        {
            static void Postfix(WarehouseList __instance, Entities.Warehouse warehouse)
            {
                try
                {
                    if (__instance == null || !IsSharedWarehouseEntry(warehouse)) return;
                    var template = _fWarehouseEntry?.GetValue(__instance) as Transform;
                    var parent = template != null ? template.parent : null;
                    if (parent == null || parent.childCount == 0) return;
                    // Object.Instantiate(template, template.parent) appends: the row just built is the last sibling.
                    var row = parent.GetChild(parent.childCount - 1);
                    int tinted = 0;
                    foreach (var t in row.GetComponentsInChildren<TMPro.TMP_Text>(true))
                        if (t != null && t.transform.name == "WarehouseName") { t.color = Tint; tinted++; }
                    if (tinted == 0 && !_warehouseLabelWarned)
                    {
                        _warehouseLabelWarned = true;
                        Plugin.Logger.LogWarning($"{Tag} could not find the name label on a shared warehouse row — it is listed, but not teal.");
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} warehouse tint: {ex.Message}"); }
            }
        }

        private static Color? _defaultTypeLabelColor;

        /// <summary>Page (after RefreshData): colour the business-type label of a shared shop; grey "open in EconoView"
        /// on any other player's shop (EconoView hides those businesses). Restored on every own-shop page.</summary>
        private static void ApplyPageTintAndGuards(BizManBusiness page)
        {
            if (page == null) return;
            var reg = page.buildingRegistration;
            string addr = AddrOf(reg);
            bool other  = IsOtherPlayersShop(reg, addr);
            bool shared = other && SharedShopSchedule.IsSharedShop(reg, addr);
            // businessTypeLabel is a TextLocalizationComponent (Localizor, un-referenced) — reflection, as HousingMapCues.
            if (HousingMapCues.GetMember(_fTypeLabel?.GetValue(page), "TextContainer") is TMPro.TMP_Text labelText)
            {
                _defaultTypeLabelColor ??= labelText.color;
                labelText.color = shared ? Tint : _defaultTypeLabelColor.Value;
            }
            SetButtonsCalling(page.transform, "OpenInEconoView", interactable: !other);
        }

        private static readonly HashSet<string> _noButtonLogged = new();

        /// <summary>Find every Button under <paramref name="root"/> whose click is wired (in the prefab) to a method of
        /// that name, and set its interactable state. Greyed = Unity's disabled tint; the control stays visible.
        /// The wiring is a prefab fact the decompile cannot show — so when GREYING finds nothing, say so once: the
        /// hard Prefix guards still hold, but a control would be live-looking, which ruling 14 forbids.</summary>
        /// <summary>Same, for the other shared-shop surfaces (7d greys Shutdown with it).</summary>
        internal static int SetButtonsCallingPublic(Transform root, string methodName, bool interactable)
            => SetButtonsCalling(root, methodName, interactable);

        private static int SetButtonsCalling(Transform root, string methodName, bool interactable)
        {
            int matched = 0;
            if (root == null) return 0;
            foreach (var b in root.GetComponentsInChildren<Button>(true))
            {
                try
                {
                    int n = b.onClick.GetPersistentEventCount();
                    for (int i = 0; i < n; i++)
                        if (b.onClick.GetPersistentMethodName(i) == methodName) { b.interactable = interactable; matched++; break; }
                }
                catch { }
            }
            if (matched == 0 && !interactable && _noButtonLogged.Add(methodName))
                Plugin.Logger.LogWarning($"{Tag} could not find a button wired to '{methodName}' under '{root.name}' — it cannot be greyed (the action itself is still blocked by its guard). The prefab wires it differently; needs a look.");
            return matched;
        }

        private static readonly HashSet<string> _blockLogged = new();
        private static bool BlockOnOtherPlayersShop(BizManBusiness page, string what)
        {
            var reg = page?.buildingRegistration;
            string addr = AddrOf(reg);
            if (!IsOtherPlayersShop(reg, addr)) return false;
            if (_blockLogged.Add(what + "|" + addr))
                Plugin.Logger.LogInfo($"{Tag} '{what}' on another player's shop '{addr}' — not allowed (reserved), ignored.");
            return true;
        }

        // ── Presentation tab guards ───────────────────────────────────────────

        private static readonly System.Reflection.FieldInfo _fPresBiz = AccessTools.Field(typeof(BizManPresentation), "bizManBusiness");

        /// <summary>Another player's shop: the contract-termination and buy-building buttons are greyed (closing shops and
        /// deeds are reserved — ruling 4 / §4.1 row 15) and the rival "show employees" button (the poaching panel) is
        /// hidden. Own shops: restored.</summary>
        [HarmonyPatch(typeof(BizManPresentation), "OnEnable")]
        public static class Patch_BizManPresentation_OnEnable_Guards
        {
            // Raised tenancy for the native view choice: a shared shop shows the "I run this" layout (no takeover /
            // rent panels), not the AI-owned takeover view it would get with the flag down. Lowered in the finalizer.
            static void Prefix(BizManPresentation __instance, out bool __state)
            {
                __state = false;
                try
                {
                    var page = _fPresBiz?.GetValue(__instance) as BizManBusiness;
                    __state = RaiseTenancy(page?.buildingRegistration, AddrOf(page?.buildingRegistration));
                }
                catch { }
            }
            static void Finalizer(BizManPresentation __instance, bool __state)
            {
                try { LowerTenancy((_fPresBiz?.GetValue(__instance) as BizManBusiness)?.buildingRegistration, __state); } catch { }
            }
            static void Postfix(BizManPresentation __instance)
            {
                try
                {
                    var page = _fPresBiz?.GetValue(__instance) as BizManBusiness;
                    var reg = page?.buildingRegistration;
                    bool other = IsOtherPlayersShop(reg, AddrOf(reg));
                    SetButtonsCalling(__instance.transform, "TerminateContract", interactable: !other);
                    SetButtonsCalling(__instance.transform, "SendBuyBuildingOffer", interactable: !other);
                    if (other && __instance.showEmployeesButton != null) __instance.showEmployeesButton.SetActive(false);
                    if (other && !SharedShopSchedule.IsSharedShop(reg, AddrOf(reg))) ShopValuation.Request(AddrOf(reg));   // H-BIZ-1 (review #5): a SHARED shop shows the "mine" view (tenancy raised for OnEnable) — nothing to fill
                    // (the buy-offer box is re-shown by the "make an offer" button later; its Send button is greyed
                    //  even while inactive — GetComponentsInChildren(true) — and the method is hard-blocked below)
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} presentation guards: {ex.Message}"); }
            }
        }

        /// <summary>HARD STOP behind the greyed button: terminating the contract runs entirely on the local replica —
        /// it would credit the CLICKER with the owner's inventory value and deposit and blank the shop locally.
        /// Closing shops is reserved (ruling 4); on another player's shop the confirm does nothing.</summary>
        [HarmonyPatch(typeof(BizManPresentation), "OnTerminateContractConfirm")]
        public static class Patch_BizManPresentation_TerminateConfirm_Block
        {
            static bool Prefix(BizManPresentation __instance)
            {
                try { return !BlockOnOtherPlayersShop(_fPresBiz?.GetValue(__instance) as BizManBusiness, "terminate contract"); }
                catch { return true; }
            }
        }

        /// <summary>HARD STOP behind the greyed button: a buy-building offer on another player's shop (deed — reserved).</summary>
        [HarmonyPatch(typeof(BizManPresentation), nameof(BizManPresentation.SendBuyBuildingOffer))]
        public static class Patch_BizManPresentation_BuyOffer_Block
        {
            static bool Prefix(BizManPresentation __instance)
            {
                try { return !BlockOnOtherPlayersShop(_fPresBiz?.GetValue(__instance) as BizManBusiness, "buy-building offer"); }
                catch { return true; }
            }
        }

        // ── Schedule tab guards (shared shops only — other players' shops never show the tab) ──

        private static bool ScheduleIsOnSharedShop()
        {
            try
            {
                var reg = ScheduleHelper.Business != null ? ScheduleHelper.Business.buildingRegistration : null;
                return SharedShopSchedule.IsSharedShop(reg, AddrOf(reg));
            }
            catch { return false; }
        }

        private static readonly System.Reflection.FieldInfo _fLicToggle    = AccessTools.Field(typeof(ScheduleCellView), "toggle");
        private static readonly System.Reflection.FieldInfo _fRenameInput  = AccessTools.Field(typeof(ScheduleCellView), "workstationNameInputField");

        /// <summary>Pasting a day (and paste-to-all) also copies the cinema licensing-fee switches into the LOCAL
        /// save's list keyed by the owner's address — a silent local write nothing routes. On a shared shop the
        /// paste carries hours and shifts only (the licensing toggle itself is greyed below).</summary>
        [HarmonyPatch(typeof(ScheduleDayButton), "CopyDisabledLicensingFeesBetweenDays")]
        public static class Patch_ScheduleDayButton_CopyLicensing_Block
        {
            static bool Prefix() { try { return !ScheduleIsOnSharedShop(); } catch { return true; } }
        }
        [HarmonyPatch(typeof(ScheduleDayButton), "ClearDisabledLicensingFeesOfDay")]
        public static class Patch_ScheduleDayButton_ClearLicensing_Block
        {
            static bool Prefix() { try { return !ScheduleIsOnSharedShop(); } catch { return true; } }
        }

        // Auto-fill (week + per-day) is LIVE on a shared shop since slice 3: the auto-fill guard in MPPatches exempts
        // the owner's copied staff for the owner's shop (SharedShopStaff.AllowedInAutoFill), the fill writes the local
        // replica, and the slice-1 scan routes the changed days to the owner once the fill has finished.

        /// <summary>Per workstation row: the cinema licensing-fee toggle (spends the owner's money — reserved) and the
        /// workstation-rename field (ruling 14 e: later — the name is not synced, so a local rename would silently stick
        /// only here) are greyed on a shared shop. On own shops the game's own readOnly rule is left as it set it.</summary>
        [HarmonyPatch(typeof(ScheduleCellView), "SetWorkstationData")]
        public static class Patch_ScheduleCellView_SetWorkstationData_Guards
        {
            static void Postfix(ScheduleCellView __instance)
            {
                try
                {
                    bool shared = ScheduleIsOnSharedShop();
                    if (_fLicToggle?.GetValue(__instance) is Toggle t) t.interactable = !shared;
                    if (_fRenameInput?.GetValue(__instance) is TMPro.TMP_InputField f)
                    {
                        f.interactable = !shared;
                        if (shared) f.readOnly = true;   // the game set readOnly for its own reason just before; only ADD the restriction
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} workstation row guard: {ex.Message}"); }
            }
        }
    }
}
