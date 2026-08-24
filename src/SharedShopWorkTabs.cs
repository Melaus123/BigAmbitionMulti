using System;
using System.Collections.Generic;
using Buildings;                               // BuildingRegistration
using HarmonyLib;
using Helpers;                                 // BuildingHelper, EmployeeHelper
using TMPro;
using UnityEngine;
using UI.Smartphone.Apps.BizMan;               // BizManBusiness
using UI.Smartphone.Apps.BizMan.Factory;       // BizManFactory + panels
using WhInventory = UI.Smartphone.Apps.BizMan.Warehouse.Inventory;
using WhDrivers   = UI.Smartphone.Apps.BizMan.Warehouse.Drivers;
using UI.Smartphone.Apps.BizMan.Warehouse;     // WarehouseProductModel, WarehouseProductsScrollerController, DriverStation

namespace BigAmbitionsMP
{
    /// <summary>
    /// SHARED-SHOP MANAGEMENT (the Business PERMISSION feature) — slice 6: warehouse &amp; factory tabs.
    /// 6a Inventory · 6b Drivers · 6c Factory. Plan §3 slice 6, rulings 12, 17, 26, 28–31.
    ///
    /// THIS IS NOT THE MERGER. Everything keys on "a building another player shares with me through a DIRECT
    /// Business grant"; own buildings, AI buildings and single-player keep the native path untouched.
    ///
    /// Why nothing here trusts the replica: detailed contents of a building (pallets, boxes, cargo) reach another
    /// machine ONLY while a player stands INSIDE it (InteriorSync subscription) — a helper opening BizMan from
    /// across town has stale or empty item data (finding 2026-08-24). So, like slice 4's sales history, each tab
    /// asks the OWNER on open (SharedWorkInfo "request") and shows what the owner's machine computed with the
    /// game's own methods. One small reply per open: data-efficient and never stale (ruling 31's bar).
    ///
    ///  • INVENTORY (6a): boxes + product rows owner-computed (the deliveries ledger never syncs at all).
    ///    SELL ALL greyed AND blocked (ruling 29) — the native button credits the CLICKER; the merger will
    ///    route it, permissions never does.
    ///  • DRIVERS (6b): slot contents carried onto the replica so the NATIVE tab renders and its own error
    ///    pop-ups behave natively; vehicle name + required skill travel too (the owner's VehicleInstances are
    ///    deliberately absent from this machine's save list — the skill check would otherwise THROW).
    ///    An assignment change is picked up by the 2 s scan and routed; the owner validates with the native
    ///    checks and echoes the tab — a rejected edit reverts by that echo.
    ///  • FACTORY (6c): per-workstation config carried and merged onto the replica (created typed if absent —
    ///    the machine may never have synced); active state, inactive reasons and ingredient stock are OWNER-
    ///    computed and substituted by call-scoped patches while the tab is open (they read pallet space and
    ///    ingredients, only fresh on the owner's machine). Recipe / produce-up-to / priority / RENAME (ruled
    ///    allowed 2026-08-24) are scanned and routed like the drivers.
    ///
    /// Both business kinds land here: a factory lives in a warehouse-type building, so its registration IS an
    /// Entities.Warehouse. Nothing on screen beyond the native widgets (ruling 17).
    /// </summary>
    public static class SharedShopWorkTabs
    {
        private const string Tag = "[SharedShop]";
        private const int   MaxProductRows = 500;    // sanity caps on a received snapshot
        private const int   MaxSlots       = 64;
        private const int   MaxStations    = 300;
        private const int   MaxAliasLen    = 48;
        private const float ScanSeconds    = 2f;     // edit-scan cadence (same as staff/prices)
        private const float PollSeconds    = 5f;     // ruling 32 live parity: re-ask while a surface sits open (sig-gated - unchanged = silence)
        private const float EditQuiet      = 3f;     // no poll right after sending an edit (the echo is already coming)
        private const int   MaxCardReqPerRound = 8;  // list-card requests per poll round (edit bucket is 10/s)

        // ── helper-side session ──
        private static string _openAddr = "";        // the shared building whose tab is open here
        private static string _openTab  = "";        // "inventory" | "drivers" | "factory"
        private static float  _nextScan;
        private static float  _nextPoll;
        private static float  _lastEditSentAt = -999f;
        private static string _tabSig = "";          // owner-computed sig of the open tab's held snapshot
        private static readonly HashSet<string> _logged = new();

        // BizMan list cards (per address - independent of the per-building tab session)
        private static readonly Dictionary<string, List<DriverSlotInfo>>  _cardSlots = new();
        private static readonly Dictionary<string, List<WorkProductInfo>> _cardInv   = new();
        private static readonly Dictionary<string, string> _cardSig  = new();
        private static readonly Dictionary<string, float>  _cardNext = new();   // addr -> earliest next request
        private static WarehouseList _whList;        // the list component, captured on its own Load (registry, not a scene sweep)
        private static bool _renderCards;

        // inventory (6a)
        private static bool _renderInventory;
        private static int  _boxesMax, _boxesCurrent;
        private static readonly List<WorkProductInfo> _rows = new();

        // drivers (6b)
        private static bool _renderDrivers;
        private static readonly Dictionary<int, DriverSlotInfo> _slotInfo = new();      // index → carried slot
        private static readonly Dictionary<string, float> _vehReq = new();              // vehicleId → required skill
        private static readonly Dictionary<int, string> _slotBaseline = new();          // index → driver id we believe the OWNER has

        // factory (6c)
        private static bool _renderFactory;
        private static readonly Dictionary<string, WorkstationInfo> _wsInfo = new();    // station id → carried info
        private static readonly Dictionary<string, (string recipe, bool upTo, int amount, string alias)> _wsBaseline = new();
        private static readonly Dictionary<string, string> _orderBaseline = new();      // workstationType → csv of ids in priority order
        private static readonly Dictionary<string, int> _resourceStock = new();         // ingredient → units, owner-counted
        private static string _factoryAddrString = "";                                  // the open factory's Address text (scoped-patch key)

        public static void Reset()
        {
            CloseSession(); _logged.Clear(); _nextScan = 0f;
            _cardSlots.Clear(); _cardInv.Clear(); _cardSig.Clear(); _cardNext.Clear(); _whList = null; _renderCards = false;
        }

        private static string AddrOf(BuildingRegistration reg)
        {
            try { return reg != null ? GameStateReader.AddressKey(reg) : ""; } catch { return ""; }
        }

        /// <summary>The registration the BizMan page is currently showing, or null.</summary>
        private static BuildingRegistration OpenPageReg()
        {
            try
            {
                var page = OpenPage();
                return page != null ? page.buildingRegistration : null;
            }
            catch { return null; }
        }

        private static BizManBusiness OpenPage()
        {
            try
            {
                var ui = InstanceBehavior<UI.UIs>.Instance;
                return ui != null && ui.fullMenu != null && ui.fullMenu.bizMan != null ? ui.fullMenu.bizMan.business : null;
            }
            catch { return null; }
        }

        /// <summary>Shared-building session opener used by every tab hook. Returns false when this page is not
        /// a shared building (native behaviour stands).</summary>
        private static bool OpenSession(string tab)
        {
            var reg = OpenPageReg();
            string addr = AddrOf(reg);
            if (reg == null || !SharedShopSchedule.IsSharedShop(reg, addr)) { CloseSession(); return false; }
            bool reopened = _openAddr != addr || _openTab != tab;
            _openAddr = addr; _openTab = tab;
            if (reopened)
            {
                if (tab == "inventory") { _rows.Clear(); _boxesMax = 0; _boxesCurrent = 0; }
                if (tab == "drivers")   { _slotInfo.Clear(); _vehReq.Clear(); _slotBaseline.Clear(); }
                if (tab == "factory")   { _wsInfo.Clear(); _wsBaseline.Clear(); _orderBaseline.Clear(); _resourceStock.Clear(); _factoryAddrString = SafeAddrString(reg); }
                RequestInfo(addr, tab);
            }
            return true;
        }

        private static string SafeAddrString(BuildingRegistration reg)
        {
            try { return reg?.Address?.ToString() ?? ""; } catch { return ""; }
        }

        private static void CloseSession()
        {
            _openAddr = ""; _openTab = ""; _factoryAddrString = ""; _tabSig = "";
            _renderInventory = false; _renderDrivers = false; _renderFactory = false;
            _rows.Clear(); _boxesMax = 0; _boxesCurrent = 0;
            _slotInfo.Clear(); _vehReq.Clear(); _slotBaseline.Clear();
            _wsInfo.Clear(); _wsBaseline.Clear(); _orderBaseline.Clear(); _resourceStock.Clear();
        }

        private static void RequestInfo(string addr, string tab, string sig = "")
        {
            if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
            var p = new SharedWorkInfoPayload { PlayerId = MPConfig.PlayerId, Action = "request", Tab = tab, AddressKey = addr, Sig = sig };
            if (sig.Length == 0) Plugin.Logger.LogInfo($"{Tag} {tab} figures requested for '{addr}'.");
            if (MPServer.IsRunning) MPServer.HostRouteSharedWorkInfo(p, MPConfig.PlayerId);
            else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.SharedWorkInfo, MPConfig.PlayerId, p));
        }

        /// <summary>Closing the session. The BizMan PAGE's OnDisable covers every way a tab goes away (close
        /// app, switch business, switch tab — the last re-opens through its own hook). Slice-4 seam.</summary>
        [HarmonyPatch(typeof(BizManBusiness), "OnDisable")]
        public static class Patch_BizManBusiness_OnDisable_WorkSession
        {
            static void Postfix() { try { CloseSession(); } catch { } }
        }

        // ═══════════════ 6a — INVENTORY ═══════════════

        /// <summary>The native refresh reads the REPLICA (stale boxes, stale rows) and re-enables Sell All from
        /// stale cargo — on a shared building it is skipped whole and the owner's figures render instead.</summary>
        [HarmonyPatch(typeof(WhInventory), "RefreshData")]
        public static class Patch_WarehouseInventory_RefreshData_Shared
        {
            static bool Prefix()
            {
                try
                {
                    if (!OpenSession("inventory")) return true;
                    _renderInventory = true;   // render (cached or empty) next Tick; the reply re-renders when it lands
                    return false;              // the native read of the replica never runs for a shared building
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} inventory tab: {ex.Message}"); return true; }
            }
        }

        /// <summary>Ruling 29: a helper never sells the owner's stock — the native button credits the CLICKER.
        /// The button is greyed by the render; this backstop blocks every other path to the method.</summary>
        [HarmonyPatch(typeof(WhInventory), nameof(WhInventory.SellAllInventory))]
        public static class Patch_WarehouseInventory_SellAll_Block
        {
            static bool Prefix()
            {
                if (_openAddr.Length == 0) return true;
                Plugin.Logger.LogInfo($"{Tag} Sell All on shared '{_openAddr}' blocked (ruling 29 — the merger will route it; permissions never).");
                return false;
            }
        }

        // boxesLabel is a TextLocalizationComponent (Localizor, an un-referenced assembly) — reached by
        // reflection exactly as HousingMapCues does; its Arguments carries the {maxBoxes, currentBoxes} pair.
        private static readonly System.Reflection.FieldInfo _fBoxesLabel = AccessTools.Field(typeof(WhInventory), "boxesLabel");

        private static void RenderInventory(WhInventory inv)
        {
            try
            {
                var label = _fBoxesLabel?.GetValue(inv);
                if (label != null) HousingMapCues.SetMember(label, "Arguments", new { maxBoxes = _boxesMax, currentBoxes = _boxesCurrent });
                else if (_logged.Add("boxeslabel")) Plugin.Logger.LogWarning($"{Tag} inventory render: boxesLabel field not found — box count stays as the game drew it.");
            }
            catch { }
            try { if (inv.sellAllButton != null) inv.sellAllButton.interactable = false; } catch { }   // ruling 29
            try
            {
                var sc = inv.productsScrollerController;
                if (sc == null) return;
                sc.data.Clear();
                foreach (var r in _rows)
                    if (r != null && !string.IsNullOrEmpty(r.ItemName))
                        sc.data.Add(new WarehouseProductModel(r.ItemName, r.Stock, r.Deliveries, r.Consumption));
                sc.scroller.ReloadData();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} inventory rows: {ex.Message}"); }
        }

        // ═══════════════ 6b — DRIVERS ═══════════════

        /// <summary>Session + request only — the NATIVE refresh then runs against the carried slot contents, so
        /// the stations, the dropdown, and the game's own error pop-ups all behave natively.</summary>
        [HarmonyPatch(typeof(WhDrivers), "RefreshData")]
        public static class Patch_WarehouseDrivers_RefreshData_Session
        {
            static void Prefix()
            {
                try { OpenSession("drivers"); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} drivers tab: {ex.Message}"); }
            }
        }

        /// <summary>The station names its truck by looking it up in the LOCAL save's vehicle list — the owner's
        /// vehicles are deliberately absent there (ghost-leak fix), so every slot would read "no vehicle". The
        /// carried name fills it in; slotNumber is 1-based (the native counter).</summary>
        [HarmonyPatch(typeof(DriverStation), nameof(DriverStation.SetUp))]
        public static class Patch_DriverStation_SetUp_CarriedVehicle
        {
            static void Postfix(DriverStation __instance, int slotNumber)
            {
                try
                {
                    if (_openTab != "drivers" || _openAddr.Length == 0) return;
                    if (!_slotInfo.TryGetValue(slotNumber - 1, out var info) || info == null || string.IsNullOrEmpty(info.VehicleId)) return;
                    string name = info.VehicleType;
                    try { var loc = VehicleStoragePanel.Localize(info.VehicleType); if (!string.IsNullOrEmpty(loc)) name = loc; } catch { }
                    var fld = __instance.vehicleNameField;
                    if (fld != null) { fld.gameObject.SetActive(true); fld.SetText(name); }
                    if (__instance.noVehicleAssignedObj != null) __instance.noVehicleAssignedObj.SetActive(false);
                }
                catch (Exception ex) { if (_logged.Add("slotname")) Plugin.Logger.LogWarning($"{Tag} driver slot name: {ex.Message}"); }
            }
        }

        /// <summary>The native skill check does a HARD lookup of the vehicle in the local save list and THROWS
        /// when it is absent (First, not FirstOrDefault). While a shared Drivers tab is open, vehicles that are
        /// not local are answered from the carried required-skill figure.</summary>
        [HarmonyPatch(typeof(WhDrivers), "HasDriverEnoughSkill")]
        public static class Patch_WarehouseDrivers_SkillCheck_Carried
        {
            static bool Prefix(Entities.EmployeeInstance driver, string vehicleInstanceId, ref bool __result)
            {
                try
                {
                    if (_openTab != "drivers" || _openAddr.Length == 0) return true;
                    bool local = false;
                    try { local = SaveGameManager.Current != null && SaveGameManager.Current.VehicleInstances.Exists(x => x != null && x.id == vehicleInstanceId); } catch { }
                    if (local) return true;   // the native check can answer safely
                    if (!_vehReq.TryGetValue(vehicleInstanceId ?? "", out var req)) { __result = false; return false; }
                    __result = false;
                    try { __result = driver != null && driver.HasSkill("ba:skill_deliverydriver") && driver.GetSkillValue("ba:skill_deliverydriver") >= req; } catch { }
                    return false;
                }
                catch { return true; }
            }
        }

        private static void ApplySlotsToReplica(BuildingRegistration reg, List<DriverSlotInfo> slots)
        {
            if (reg is not Entities.Warehouse wh || slots == null) return;
            if (wh.vehicleSlots == null) wh.vehicleSlots = new List<Entities.VehicleSlot>();
            foreach (var s in slots)
            {
                if (s == null || s.Index < 0 || s.Index >= MaxSlots) continue;
                while (wh.vehicleSlots.Count <= s.Index) wh.vehicleSlots.Add(new Entities.VehicleSlot());
                var slot = wh.vehicleSlots[s.Index];
                slot.vehicleInstanceId = s.VehicleId ?? "";
                slot.employeeDriverId  = s.DriverId ?? "";
            }
        }

        /// <summary>Put the owner's slot contents onto the replica so the native tab (and its checks) read truth.</summary>
        private static void ApplyDriversSnapshot(SharedWorkInfoPayload p, BuildingRegistration reg)
        {
            _slotInfo.Clear(); _vehReq.Clear(); _slotBaseline.Clear();
            ApplySlotsToReplica(reg, p.Slots);
            foreach (var s in p.Slots)
            {
                if (s == null || s.Index < 0 || s.Index >= MaxSlots) continue;
                _slotInfo[s.Index] = s;
                _slotBaseline[s.Index] = s.DriverId ?? "";
                if (!string.IsNullOrEmpty(s.VehicleId)) _vehReq[s.VehicleId] = s.RequiredSkill;
            }
            _renderDrivers = true;
        }

        /// <summary>2 s scan: the native dropdown wrote the replica's slot — route the change to the owner.</summary>
        private static void ScanDriverEdits()
        {
            var reg = GameStatePatcher.FindRegistration(_openAddr);
            if (reg is not Entities.Warehouse wh || wh.vehicleSlots == null) return;
            for (int i = 0; i < wh.vehicleSlots.Count && i < MaxSlots; i++)
            {
                string cur = "";
                try { cur = wh.vehicleSlots[i]?.employeeDriverId ?? ""; } catch { }
                if (!_slotBaseline.TryGetValue(i, out var known)) continue;   // never carried — not ours to route
                if (cur == known) continue;
                _slotBaseline[i] = cur;   // optimistic; the owner's echo re-asserts truth either way
                SendEdit(new SharedWorkEditPayload { PlayerId = MPConfig.PlayerId, AddressKey = _openAddr, Op = "driver", SlotIndex = i, StrValue = cur });
                Plugin.Logger.LogInfo($"{Tag} routing driver slot {i + 1} of '{_openAddr}' to the owner: '{(cur.Length == 0 ? "unassigned" : cur)}'.");
            }
        }

        // ═══════════════ 6c — FACTORY ═══════════════

        /// <summary>Session + request. The native OnEnable has already drawn the machine list from the replica
        /// (possibly stale); the snapshot re-draws it with the owner's truth moments later.</summary>
        [HarmonyPatch(typeof(BizManFactory), "OnEnable")]
        public static class Patch_BizManFactory_OnEnable_Session
        {
            static void Postfix()
            {
                try { OpenSession("factory"); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} factory tab: {ex.Message}"); }
            }
        }

        /// <summary>"Is the machine running?" reads business-open + schedule (synced) but also pallet space and
        /// ingredients — only fresh on the owner's machine. While a shared Factory tab is open, the owner's
        /// verdict is substituted for that building's workstations.</summary>
        [HarmonyPatch(typeof(FactoryWorkstationInstance), nameof(FactoryWorkstationInstance.IsWorkstationActive))]
        public static class Patch_Workstation_Active_Carried
        {
            static void Postfix(FactoryWorkstationInstance __instance, BuildingRegistration registration, ref bool __result)
            {
                try
                {
                    if (_openTab != "factory" || _openAddr.Length == 0 || __instance == null) return;
                    if (AddrOf(registration) != _openAddr) return;
                    if (_wsInfo.TryGetValue(__instance.id ?? "", out var info) && info != null) __result = info.Active;
                }
                catch { }
            }
        }

        /// <summary>The idle-reason tooltip, same substitution as the active flag.</summary>
        [HarmonyPatch(typeof(FactoryWorkstationInstance), nameof(FactoryWorkstationInstance.GetInactiveReasonKeys))]
        public static class Patch_Workstation_Reasons_Carried
        {
            static void Postfix(FactoryWorkstationInstance __instance, BuildingRegistration registration, ref List<string> __result)
            {
                try
                {
                    if (_openTab != "factory" || _openAddr.Length == 0 || __instance == null) return;
                    if (AddrOf(registration) != _openAddr) return;
                    if (_wsInfo.TryGetValue(__instance.id ?? "", out var info) && info != null && info.Reasons != null)
                        __result = new List<string>(info.Reasons);
                }
                catch { }
            }
        }

        /// <summary>The "is the production machine mounted?" check walks the LOADED interior's item controllers
        /// and NULL-REFS when the building is not loaded here. While a shared Factory tab is open it is answered
        /// from the carried attachment names instead.</summary>
        [HarmonyPatch(typeof(FactoryWorkstationInstance), nameof(FactoryWorkstationInstance.HasProductionMachine))]
        public static class Patch_Workstation_HasMachine_Data
        {
            static bool Prefix(FactoryWorkstationInstance __instance, string machineName, ref bool __result)
            {
                try
                {
                    if (_openTab != "factory" || _openAddr.Length == 0) return true;
                    __result = false;
                    if (_wsInfo.TryGetValue(__instance?.id ?? "", out var info) && info?.Stacked != null)
                        __result = info.Stacked.Contains(machineName);
                    else if (__instance?.stackedItems != null)
                        foreach (var st in __instance.stackedItems)
                            if (st != null && st.childItemName == machineName) { __result = true; break; }
                    return false;
                }
                catch { return true; }
            }
        }

        /// <summary>Ingredient stock in the recipe table counts pallets from item data that is only fresh on the
        /// owner's machine — the carried counts are substituted while THIS factory's tab is open. Scoped by
        /// address: every other building keeps the native count.</summary>
        [HarmonyPatch(typeof(BuildingHelper), nameof(BuildingHelper.CountResourcesInPallets))]
        public static class Patch_CountResourcesInPallets_Carried
        {
            static void Postfix(object address, string resourceName, ref int __result)
            {
                try
                {
                    if (_openTab != "factory" || _factoryAddrString.Length == 0 || _resourceStock.Count == 0) return;
                    if (address == null || address.ToString() != _factoryAddrString) return;
                    if (_resourceStock.TryGetValue(resourceName ?? "", out var owned)) __result = owned;
                }
                catch { }
            }
        }

        /// <summary>Merge the owner's workstation configs onto the replica (created typed if absent — the machine
        /// may never have synced here), then re-draw the list. A machine the owner REMOVED stays as a stale row
        /// until the next interior visit — accepted residual, logged once.</summary>
        private static void ApplyFactorySnapshot(SharedWorkInfoPayload p, BuildingRegistration reg)
        {
            _wsInfo.Clear(); _wsBaseline.Clear(); _orderBaseline.Clear(); _resourceStock.Clear();
            _factoryAddrString = SafeAddrString(reg);
            if (p.ResourceStock != null)
                foreach (var st in p.ResourceStock)
                    if (st != null && !string.IsNullOrEmpty(st.ItemName)) _resourceStock[st.ItemName] = st.Count;
            var orderByType = new Dictionary<string, List<(int prio, string id)>>();
            int created = 0;
            foreach (var w in p.Stations)
            {
                if (w == null || string.IsNullOrEmpty(w.Id) || string.IsNullOrEmpty(w.ItemName)) continue;
                if (_wsInfo.Count >= MaxStations) break;
                FactoryWorkstationInstance fw = null;
                try
                {
                    if (reg.itemInstances.TryGetValue(w.Id, out var ii)) fw = ii as FactoryWorkstationInstance;
                    if (fw == null)
                    {
                        fw = new FactoryWorkstationInstance(w.ItemName) { id = w.Id, itemName = w.ItemName };
                        reg.itemInstances[w.Id] = fw; created++;
                    }
                    fw.selectedRecipeId = w.RecipeId ?? "";
                    fw.priority         = w.Priority;
                    fw.produceUpTo      = w.ProduceUpTo;
                    fw.produceUpToValue = w.UpToValue;
                    fw.alias            = w.Alias ?? "";
                    if (!string.IsNullOrEmpty(w.WorkstationType)) fw.workstationType = w.WorkstationType;
                    if (w.Stacked != null && fw.stackedItems != null)
                    {
                        fw.stackedItems.Clear();
                        foreach (var name in w.Stacked)
                            if (!string.IsNullOrEmpty(name)) fw.stackedItems.Add(new AttachableChild { childItemName = name });
                    }
                }
                catch (Exception ex) { if (_logged.Add("wsapply")) Plugin.Logger.LogWarning($"{Tag} workstation apply: {ex.Message}"); continue; }
                _wsInfo[w.Id] = w;
                _wsBaseline[w.Id] = (w.RecipeId ?? "", w.ProduceUpTo, w.UpToValue, w.Alias ?? "");
                string type = fw.workstationType ?? "";
                if (!orderByType.TryGetValue(type, out var lst)) orderByType[type] = lst = new List<(int, string)>();
                lst.Add((w.Priority, w.Id));
            }
            foreach (var kv in orderByType)
            {
                kv.Value.Sort((a, b) => a.prio != b.prio ? a.prio.CompareTo(b.prio) : string.CompareOrdinal(a.id, b.id));
                _orderBaseline[kv.Key] = string.Join(",", kv.Value.ConvertAll(x => x.id));
            }
            if (created > 0) Plugin.Logger.LogInfo($"{Tag} '{_openAddr}': {created} workstation(s) existed only on the owner's machine — created typed replicas for the tab.");
            _renderFactory = true;
        }

        /// <summary>2 s scan: the native panel wrote the replica's workstation — route each change to the owner.</summary>
        private static void ScanFactoryEdits()
        {
            var reg = GameStatePatcher.FindRegistration(_openAddr);
            if (reg == null || _wsBaseline.Count == 0) return;
            var orderNow = new Dictionary<string, List<(int prio, string id)>>();
            foreach (var kv in _wsBaseline)
            {
                FactoryWorkstationInstance fw = null;
                try { if (reg.itemInstances.TryGetValue(kv.Key, out var ii)) fw = ii as FactoryWorkstationInstance; } catch { }
                if (fw == null) continue;
                string recipe = fw.selectedRecipeId ?? "", alias = fw.alias ?? "";
                bool upTo = fw.produceUpTo; int amount = fw.produceUpToValue;
                var b = kv.Value;
                if (recipe != b.recipe)
                {
                    _wsBaseline[kv.Key] = (recipe, b.upTo, b.amount, b.alias); b = _wsBaseline[kv.Key];
                    SendEdit(new SharedWorkEditPayload { PlayerId = MPConfig.PlayerId, AddressKey = _openAddr, Op = "recipe", StationId = kv.Key, StrValue = recipe });
                    Plugin.Logger.LogInfo($"{Tag} routing recipe of workstation '{kv.Key}' at '{_openAddr}' to the owner.");
                }
                if (upTo != b.upTo || amount != b.amount)
                {
                    _wsBaseline[kv.Key] = (b.recipe, upTo, amount, b.alias); b = _wsBaseline[kv.Key];
                    SendEdit(new SharedWorkEditPayload { PlayerId = MPConfig.PlayerId, AddressKey = _openAddr, Op = "produce", StationId = kv.Key, BoolValue = upTo, IntValue = amount });
                    Plugin.Logger.LogInfo($"{Tag} routing produce-up-to of workstation '{kv.Key}' at '{_openAddr}' to the owner: {(upTo ? amount.ToString() : "continuous")}.");
                }
                if (alias != b.alias)
                {
                    _wsBaseline[kv.Key] = (b.recipe, b.upTo, b.amount, alias);
                    SendEdit(new SharedWorkEditPayload { PlayerId = MPConfig.PlayerId, AddressKey = _openAddr, Op = "alias", StationId = kv.Key, StrValue = alias });
                    Plugin.Logger.LogInfo($"{Tag} routing rename of workstation '{kv.Key}' at '{_openAddr}' to the owner.");
                }
                string type = fw.workstationType ?? "";
                if (!orderNow.TryGetValue(type, out var lst)) orderNow[type] = lst = new List<(int, string)>();
                lst.Add((fw.priority, kv.Key));
            }
            foreach (var kv in orderNow)
            {
                kv.Value.Sort((a, b2) => a.prio != b2.prio ? a.prio.CompareTo(b2.prio) : string.CompareOrdinal(a.id, b2.id));
                string csv = string.Join(",", kv.Value.ConvertAll(x => x.id));
                if (_orderBaseline.TryGetValue(kv.Key, out var known) && known != csv)
                {
                    _orderBaseline[kv.Key] = csv;
                    SendEdit(new SharedWorkEditPayload { PlayerId = MPConfig.PlayerId, AddressKey = _openAddr, Op = "order", StrValue = kv.Key + "|" + csv });
                    Plugin.Logger.LogInfo($"{Tag} routing workstation order of '{kv.Key}' at '{_openAddr}' to the owner.");
                }
            }
        }

        // ═══════════════ shared plumbing ═══════════════

        private static void SendEdit(SharedWorkEditPayload p)
        {
            _lastEditSentAt = Time.unscaledTime;
            if (MPServer.IsRunning) MPServer.HostRouteSharedWorkEdit(p, MPConfig.PlayerId);
            else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.SharedWorkEdit, MPConfig.PlayerId, p));
        }

        /// <summary>MAIN THREAD (MPCanvasUI.Update). Deferred renders (never the same frame a tab was enabled —
        /// the native code defers its own first fill for the same scroller reason) + the 2 s edit scan.</summary>
        public static void Tick()
        {
            // The LIST cards live OUTSIDE any per-building session — they must run even when no building
            // page is open (field 2026-08-24: the session guard below starved them — a helper viewing the
            // list never re-rendered or polled its cards, so they stayed as the native empty first draw).
            if (Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + PollSeconds;
                if (_openAddr.Length > 0 && Time.unscaledTime - _lastEditSentAt > EditQuiet)
                    RequestInfo(_openAddr, _openTab, _tabSig);
                try { PollCards(); } catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} card poll: {ex.Message}"); }
            }
            if (_renderCards && _whList != null && _whList.gameObject.activeInHierarchy)
            {
                _renderCards = false;
                try { _whList.Load(); } catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} card render: {ex.Message}"); }
            }
            if (_openAddr.Length == 0) return;
            var page = OpenPage();
            if (page == null) return;
            if (_renderInventory)
            {
                var inv = page.GetComponentInChildren<WhInventory>(true);
                if (inv != null && inv.gameObject.activeInHierarchy)
                {
                    _renderInventory = false;
                    try { RenderInventory(inv); } catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} inventory render: {ex.Message}"); }
                }
            }
            if (_renderDrivers)
            {
                var drv = page.GetComponentInChildren<WhDrivers>(true);
                if (drv != null && drv.gameObject.activeInHierarchy)
                {
                    _renderDrivers = false;
                    try { AccessTools.Method(typeof(WhDrivers), "RefreshData")?.Invoke(drv, null); }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} drivers render: {ex.Message}"); }
                }
            }
            if (_renderFactory)
            {
                var fac = page.GetComponentInChildren<BizManFactory>(true);
                if (fac != null && fac.gameObject.activeInHierarchy)
                {
                    _renderFactory = false;
                    try
                    {
                        var reg = GameStatePatcher.FindRegistration(_openAddr);
                        if (reg != null && fac.machineList != null) fac.machineList.SetUp(reg);
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} factory render: {ex.Message}"); }
                }
            }
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + ScanSeconds;
                try
                {
                    if (_openTab == "drivers") ScanDriverEdits();
                    else if (_openTab == "factory") ScanFactoryEdits();
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} edit scan: {ex.Message}"); }
            }
        }

        private static void PollCards()
        {
            if (_whList == null || !_whList.gameObject.activeInHierarchy) return;
            RequestCards();
        }

        /// <summary>Ask the owner for each shared warehouse/factory card, debounced per address.</summary>
        private static void RequestCards()
        {
            var gi = SaveGameManager.Current;
            if (gi?.BuildingRegistrations == null) return;
            int sent = 0;
            float now = Time.unscaledTime;
            foreach (var reg in gi.BuildingRegistrations)
            {
                if (reg == null) continue;
                string type = ""; try { type = reg.GetBuildingType() ?? ""; } catch { }
                if (type != "ba:buildingtype_warehouse") continue;
                string addr = AddrOf(reg);
                if (addr.Length == 0 || !SharedShopSchedule.IsSharedShop(reg, addr)) continue;
                if (_cardNext.TryGetValue(addr, out var next) && now < next) continue;
                _cardNext[addr] = now + PollSeconds;
                _cardSig.TryGetValue(addr, out var sig);
                RequestInfo(addr, "card", sig ?? "");
                if (++sent >= MaxCardReqPerRound) break;   // the rest go next round - never a rate-cap burst
            }
        }

        // ═══════════════ the BizMan list cards ═══════════════

        /// <summary>Capture the list component (registry over sweep) and request the shared cards' figures.
        /// The native card reads slot contents (empty on a replica until carried), the local vehicle list
        /// (owner vehicles absent) and pallet cargo (stale) - the carried figures fix all three.</summary>
        [HarmonyPatch(typeof(WarehouseList), nameof(WarehouseList.Load))]
        public static class Patch_WarehouseList_Load_Cards
        {
            static void Postfix(WarehouseList __instance)
            {
                try { _whList = __instance; RequestCards(); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} card request: {ex.Message}"); }
            }
        }

        /// <summary>The card's inventory summary calls this on the REGISTRATION - carried rows substitute for
        /// a shared building (keyed by address: every other warehouse keeps the native answer).</summary>
        [HarmonyPatch(typeof(Entities.Warehouse), nameof(Entities.Warehouse.GetInventoryForDisplay))]
        public static class Patch_Warehouse_InventoryForDisplay_Carried
        {
            static void Postfix(Entities.Warehouse __instance, int maxEntries, ref List<(string, int, int)> __result)
            {
                try
                {
                    if (_cardInv.Count == 0) return;
                    string addr = AddrOf(__instance);
                    if (!_cardInv.TryGetValue(addr, out var rows) || rows == null) return;
                    var list = new List<(string, int, int)>();
                    foreach (var r in rows)
                    {
                        if (r == null || string.IsNullOrEmpty(r.ItemName)) continue;
                        list.Add((r.ItemName, r.Stock, r.DaysLeft));
                        if (list.Count >= Math.Max(0, maxEntries)) break;
                    }
                    __result = list;
                }
                catch { }
            }
        }

        /// <summary>Fill the card's vehicle names for a shared building: the native lookup reads the LOCAL
        /// save's vehicle list, where the owner's trucks are deliberately absent - every slot read
        /// "Unassigned" in red. Runs on the row just built (last sibling, same trick as the teal tint).</summary>
        [HarmonyPatch(typeof(WarehouseList), "SetUpEntry")]
        public static class Patch_WarehouseList_SetUpEntry_CarriedVehicles
        {
            private static readonly System.Reflection.FieldInfo _fEntry = AccessTools.Field(typeof(WarehouseList), "warehouseEntry");
            static void Postfix(WarehouseList __instance, Entities.Warehouse warehouse)
            {
                try
                {
                    string addr = AddrOf(warehouse);
                    if (!_cardSlots.TryGetValue(addr, out var slots) || slots == null || slots.Count == 0) return;
                    var template = _fEntry?.GetValue(__instance) as Transform;
                    var parent = template != null ? template.parent : null;
                    if (parent == null || parent.childCount == 0) return;
                    var row = parent.GetChild(parent.childCount - 1);
                    var slotList = row.Find("VehicleSlotsList");
                    if (slotList == null) return;
                    int slotIdx = 0;
                    for (int c = 0; c < slotList.childCount; c++)
                    {
                        var child = slotList.GetChild(c);
                        if (child == null || !child.gameObject.activeSelf) continue;   // the inactive template
                        int i = slotIdx++;
                        DriverSlotInfo info = null;
                        foreach (var sl in slots) if (sl != null && sl.Index == i) { info = sl; break; }
                        if (info == null || string.IsNullOrEmpty(info.VehicleId)) continue;
                        var nameTf = child.Find("VehicleName");
                        if (nameTf == null) continue;
                        var tmp = nameTf.GetComponentInChildren<TMP_Text>(true);
                        if (tmp == null) continue;
                        string name = info.VehicleType;
                        try { var loc = VehicleStoragePanel.Localize(info.VehicleType); if (!string.IsNullOrEmpty(loc)) name = loc; } catch { }
                        tmp.text = name;
                        try { tmp.color = InstanceBehavior<GlobalReferences>.Instance.colors.midnight; } catch { }
                    }
                }
                catch (Exception ex) { if (_logged.Add("cardveh")) Plugin.Logger.LogWarning($"{Tag} card vehicle names: {ex.Message}"); }
            }
        }

        /// <summary>OWNER: after applying a routed edit, repaint OUR OWN open screens for that building -
        /// data alone never repaints a native tab (ruling 32; the bug class that lagged slice 4's owner).</summary>
        private static void RefreshOwnerOpenSurfaces(string addressKey, string tab)
        {
            try
            {
                var page = OpenPage();
                if (page != null && AddrOf(page.buildingRegistration) == addressKey)
                {
                    if (tab == "drivers")
                    {
                        var drv = page.GetComponentInChildren<WhDrivers>(true);
                        if (drv != null && drv.gameObject.activeInHierarchy)
                            AccessTools.Method(typeof(WhDrivers), "RefreshData")?.Invoke(drv, null);
                    }
                    else if (tab == "factory")
                    {
                        var fac = page.GetComponentInChildren<BizManFactory>(true);
                        var reg = page.buildingRegistration;
                        if (fac != null && fac.gameObject.activeInHierarchy && fac.machineList != null && reg != null)
                            fac.machineList.SetUp(reg);
                    }
                }
                if (tab == "drivers" && _whList != null && _whList.gameObject.activeInHierarchy)
                    _whList.Load();   // the owner's list card shows the same slots
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} owner repaint: {ex.Message}"); }
        }

        // ═══════════════ transport ═══════════════

        public static void HandleWorkInfo(SharedWorkInfoPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey)) return;
                if (p.Action == "request") OwnerAnswer(p);
                else if (p.Action == "snapshot") ApplySnapshot(p);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} work info: {ex.Message}"); }
        }

        /// <summary>OWNER, MAIN THREAD: answer ONE helper with this building's figures, computed here with the
        /// game's own methods — the only machine whose item data is authoritative.</summary>
        private static void OwnerAnswer(SharedWorkInfoPayload req)
        {
            if (!GrantSync.IsGrantedDirect(GrantKind.Business, MPConfig.PlayerId, req.PlayerId))
            {
                if (_logged.Add("work-nogrant|" + req.PlayerId))
                    Plugin.Logger.LogInfo($"{Tag} work-info request from '{req.PlayerId}' but they hold no Business grant from me — ignored.");
                return;
            }
            BuildAndSendSnapshot(req.AddressKey, req.Tab, req.PlayerId, req.Sig);
        }

        /// <summary>OWNER: build one tab's snapshot and send it to one helper (also the echo after an edit).
        /// When the requester's sig matches the fresh content, nothing is sent - the poll costs no reply.</summary>
        private static void BuildAndSendSnapshot(string addressKey, string tab, string toPid, string requesterSig = "")
        {
            var reg = GameStatePatcher.FindRegistration(addressKey);
            if (reg == null || !MergerFlip.TrulyMine(reg)) return;
            if (reg is not Entities.Warehouse wh)
            {
                if (_logged.Add("work-notwh|" + addressKey))
                    Plugin.Logger.LogWarning($"{Tag} work-info request for '{addressKey}' but its registration is not a warehouse/factory — ignored.");
                return;
            }
            var reply = new SharedWorkInfoPayload
            {
                PlayerId = MPConfig.PlayerId, Action = "snapshot", Tab = tab,
                AddressKey = addressKey, ToPid = toPid,
            };
            try
            {
                if (tab == "inventory") BuildInventory(wh, reply);
                else if (tab == "drivers") BuildDrivers(wh, reply);
                else if (tab == "factory") BuildFactory(wh, reply);
                else if (tab == "card") BuildCard(wh, reply);
                else return;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} work-info build ({tab}): {ex.Message}"); }
            reply.Sig = SigOf(reply);
            if (requesterSig.Length > 0 && requesterSig == reply.Sig) return;   // unchanged - the poll stays silent
            if (requesterSig.Length == 0)
                Plugin.Logger.LogInfo($"{Tag} sending '{toPid}' the {tab} figures of '{addressKey}': " +
                    (tab == "inventory" ? $"{reply.Products.Count} product(s), boxes {reply.BoxesCurrent}/{reply.BoxesMax}."
                    : tab == "drivers"  ? $"{reply.Slots.Count} slot(s)."
                    : tab == "card"     ? $"{reply.Slots.Count} slot(s), {reply.Products.Count} inventory row(s)."
                    :                     $"{reply.Stations.Count} workstation(s), {reply.ResourceStock.Count} resource count(s)."));
            if (MPServer.IsRunning) MPServer.HostRouteSharedWorkInfo(reply, MPConfig.PlayerId);
            else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.SharedWorkInfo, MPConfig.PlayerId, reply));
        }

        /// <summary>Owner-computed content signature - both compares happen on the owner's machine, so any
        /// deterministic digest works. Unchanged content means the poll reply is silence and no re-render.</summary>
        private static string SigOf(SharedWorkInfoPayload r)
        {
            var sb = new System.Text.StringBuilder(256);
            sb.Append(r.Tab).Append('#').Append(r.BoxesMax).Append('/').Append(r.BoxesCurrent);
            if (r.Products != null) foreach (var x in r.Products) if (x != null) sb.Append('|').Append(x.ItemName).Append(':').Append(x.Stock).Append(':').Append(x.Deliveries).Append(':').Append(x.Consumption).Append(':').Append(x.DaysLeft);
            if (r.Slots != null) foreach (var x in r.Slots) if (x != null) sb.Append('|').Append(x.Index).Append(':').Append(x.VehicleId).Append(':').Append(x.VehicleType).Append(':').Append(x.RequiredSkill).Append(':').Append(x.DriverId);
            if (r.Stations != null) foreach (var x in r.Stations) if (x != null) { sb.Append('|').Append(x.Id).Append(':').Append(x.RecipeId).Append(':').Append(x.Priority).Append(':').Append(x.ProduceUpTo).Append(':').Append(x.UpToValue).Append(':').Append(x.Alias).Append(':').Append(x.Active); if (x.Reasons != null) foreach (var rr in x.Reasons) sb.Append('~').Append(rr); }
            if (r.ResourceStock != null) foreach (var x in r.ResourceStock) if (x != null) sb.Append('|').Append(x.ItemName).Append(':').Append(x.Count);
            return sb.Length.ToString() + ":" + sb.ToString().GetHashCode().ToString("X8");
        }

        /// <summary>The BizMan list card's summaries: slots (as the Drivers tab) + the native top-4 inventory
        /// rows, computed with the game's own method on the owner's data.</summary>
        private static void BuildCard(Entities.Warehouse wh, SharedWorkInfoPayload reply)
        {
            BuildDrivers(wh, reply);
            try
            {
                foreach (var t in wh.GetInventoryForDisplay(4))
                    reply.Products.Add(new WorkProductInfo { ItemName = t.Item1 ?? "", Stock = t.Item2, DaysLeft = t.Item3 });
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} card build: {ex.Message}"); }
        }

        private static void BuildInventory(Entities.Warehouse wh, SharedWorkInfoPayload reply)
        {
            // Pallet-shelf capacity and fill: the native tab's own arithmetic, run on the owner's data.
            foreach (var ii in wh.itemInstances.Values)
            {
                if (ii == null) continue;
                var item = BigAmbitions.Items.ItemsGetter.GetByName(ii.itemName);
                if (item == null || !item.HasTag(BigAmbitions.Tags.TagRef.Itemtag.iswarehousestorage)) continue;
                reply.BoxesMax     += item.cargoCapacity;
                reply.BoxesCurrent += ii.cargoInstances != null ? ii.cargoInstances.Count : 0;
            }
            foreach (var prod in wh.GetProducts())
            {
                if (string.IsNullOrEmpty(prod)) continue;
                reply.Products.Add(new WorkProductInfo
                {
                    ItemName    = prod,
                    Stock       = BuildingHelper.CountResourcesInPallets(wh.Address, prod),
                    Deliveries  = wh.GetProductDeliveries(prod),
                    Consumption = wh.GetProductConsumption(prod),
                });
                if (reply.Products.Count >= MaxProductRows) break;
            }
        }

        private static void BuildDrivers(Entities.Warehouse wh, SharedWorkInfoPayload reply)
        {
            if (wh.vehicleSlots == null) return;
            for (int i = 0; i < wh.vehicleSlots.Count && i < MaxSlots; i++)
            {
                var slot = wh.vehicleSlots[i];
                var info = new DriverSlotInfo { Index = i, VehicleId = slot?.vehicleInstanceId ?? "", DriverId = slot?.employeeDriverId ?? "" };
                if (info.VehicleId.Length > 0)
                {
                    try
                    {
                        var vi = SaveGameManager.Current.VehicleInstances.Find(x => x != null && x.id == info.VehicleId);
                        if (vi != null)
                        {
                            info.VehicleType   = vi.vehicleTypeName ?? "";
                            try { info.RequiredSkill = vi.VehicleType != null ? (float)vi.VehicleType.requiredDeliveryDriverSkillValue : 0f; } catch { }
                        }
                    }
                    catch { }
                }
                reply.Slots.Add(info);
            }
        }

        private static void BuildFactory(Entities.Warehouse wh, SharedWorkInfoPayload reply)
        {
            var resources = new HashSet<string>();
            foreach (var ii in wh.itemInstances.Values)
            {
                if (ii is not FactoryWorkstationInstance fw || string.IsNullOrEmpty(fw.workstationType)) continue;
                var w = new WorkstationInfo
                {
                    Id = fw.id ?? "", ItemName = fw.itemName ?? "", WorkstationType = fw.workstationType ?? "",
                    Alias = fw.alias ?? "", RecipeId = fw.selectedRecipeId ?? "",
                    Priority = fw.priority, ProduceUpTo = fw.produceUpTo, UpToValue = fw.produceUpToValue,
                };
                try
                {
                    w.Active = fw.IsWorkstationActive(wh);
                    if (!w.Active) foreach (var r in fw.GetInactiveReasonKeys(wh)) if (!string.IsNullOrEmpty(r)) w.Reasons.Add(r);
                }
                catch { }
                try
                {
                    if (fw.stackedItems != null)
                        foreach (var st in fw.stackedItems)
                            if (st != null && !string.IsNullOrEmpty(st.childItemName)) w.Stacked.Add(st.childItemName);
                }
                catch { }
                try
                {
                    var ws = fw.Workstation;
                    if (ws?.supportedRecipes != null)
                        foreach (var rec in ws.supportedRecipes)
                            if (rec?.ingredients != null)
                                foreach (var ing in rec.ingredients)
                                    if (!string.IsNullOrEmpty(ing.item)) resources.Add(ing.item);   // RecipeItem is a struct
                }
                catch { }
                reply.Stations.Add(w);
                if (reply.Stations.Count >= MaxStations) break;
            }
            foreach (var name in resources)
            {
                try { reply.ResourceStock.Add(new StockInfo { ItemName = name, Count = BuildingHelper.CountResourcesInPallets(wh.Address, name) }); } catch { }
                if (reply.ResourceStock.Count >= 200) break;
            }
        }

        /// <summary>HELPER, MAIN THREAD: keep the owner's figures and repaint if that tab is still the one open.</summary>
        private static void ApplySnapshot(SharedWorkInfoPayload p)
        {
            var reg = GameStatePatcher.FindRegistration(p.AddressKey);
            if (reg == null) return;
            if (p.Tab == "card")
            {
                if (_cardSig.TryGetValue(p.AddressKey, out var oldSig) && oldSig == p.Sig) return;
                _cardSig[p.AddressKey] = p.Sig ?? "";
                _cardSlots[p.AddressKey] = p.Slots != null ? new List<DriverSlotInfo>(p.Slots) : new List<DriverSlotInfo>();
                _cardInv[p.AddressKey]   = p.Products != null ? new List<WorkProductInfo>(p.Products) : new List<WorkProductInfo>();
                ApplySlotsToReplica(reg, p.Slots);   // the card's driver names resolve natively once the slots are real
                _renderCards = true;
                return;
            }
            if (_openAddr.Length == 0 || p.AddressKey != _openAddr) return;   // stale reply for a tab no longer open
            if (p.Tab == _openTab && _tabSig.Length > 0 && p.Sig == _tabSig) return;   // unchanged - never disturb the open screen
            if (p.Tab == _openTab) _tabSig = p.Sig ?? "";
            if (p.Tab == "inventory")
            {
                _boxesMax = p.BoxesMax; _boxesCurrent = p.BoxesCurrent;
                _rows.Clear();
                if (p.Products != null)
                    foreach (var r in p.Products)
                    {
                        if (r != null && !string.IsNullOrEmpty(r.ItemName)) _rows.Add(r);
                        if (_rows.Count >= MaxProductRows) break;
                    }
                _renderInventory = true;
            }
            else if (p.Tab == "drivers") ApplyDriversSnapshot(p, reg);
            else if (p.Tab == "factory") ApplyFactorySnapshot(p, reg);
        }

        // ═══════════════ owner-side edit apply ═══════════════

        /// <summary>OWNER, MAIN THREAD: apply one routed edit with the native checks, then echo that tab's
        /// snapshot to the editor — a rejected edit reverts on the helper by that same echo.</summary>
        public static void OwnerApplyEdit(SharedWorkEditPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey) || string.IsNullOrEmpty(p.PlayerId)) return;
                if (!GrantSync.IsGrantedDirect(GrantKind.Business, MPConfig.PlayerId, p.PlayerId))
                {
                    if (_logged.Add("edit-nogrant|" + p.PlayerId))
                        Plugin.Logger.LogInfo($"{Tag} work edit from '{p.PlayerId}' but they hold no Business grant from me — ignored.");
                    return;
                }
                var reg = GameStatePatcher.FindRegistration(p.AddressKey);
                if (reg is not Entities.Warehouse wh || !MergerFlip.TrulyMine(reg)) return;
                string echoTab = p.Op == "driver" ? "drivers" : "factory";
                bool applied = p.Op switch
                {
                    "driver"  => ApplyDriverOp(wh, p),
                    "recipe"  => ApplyRecipeOp(wh, p),
                    "produce" => ApplyProduceOp(wh, p),
                    "order"   => ApplyOrderOp(wh, p),
                    "alias"   => ApplyAliasOp(wh, p),
                    _ => false,
                };
                if (!applied) Plugin.Logger.LogInfo($"{Tag} work edit '{p.Op}' on '{p.AddressKey}' from '{p.PlayerId}' NOT applied — echoing truth back.");
                BuildAndSendSnapshot(p.AddressKey, echoTab, p.PlayerId);   // echo either way: apply confirms, reject reverts
                if (applied) RefreshOwnerOpenSurfaces(p.AddressKey, echoTab);   // ruling 32: data alone never repaints a native tab
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} OwnerApplyEdit: {ex.Message}"); }
        }

        private static bool ApplyDriverOp(Entities.Warehouse wh, SharedWorkEditPayload p)
        {
            if (wh.vehicleSlots == null || p.SlotIndex < 0 || p.SlotIndex >= wh.vehicleSlots.Count) return false;
            var slot = wh.vehicleSlots[p.SlotIndex];
            string newId = p.StrValue ?? "";
            Entities.EmployeeInstance oldDrv = null;
            try { if (!string.IsNullOrEmpty(slot.employeeDriverId)) oldDrv = EmployeeHelper.GetEmployeeById(slot.employeeDriverId); } catch { }
            if (newId.Length == 0)
            {
                slot.employeeDriverId = "";
                try { oldDrv?.UpdateWeeklyHoursAndDays(); } catch { }
                Plugin.Logger.LogInfo($"{Tag} '{p.PlayerId}' unassigned the driver of slot {p.SlotIndex + 1} at '{p.AddressKey}'.");
                return true;
            }
            // The native listener's three checks, on the owner's authoritative data.
            if (string.IsNullOrEmpty(slot.vehicleInstanceId)) return false;
            Entities.EmployeeInstance drv = null;
            try { drv = EmployeeHelper.GetEmployeeById(newId); } catch { }
            if (drv == null) return false;
            string here = ""; try { here = drv.assignedAddress?.ToString() ?? ""; } catch { }
            if (here != SafeAddrString(wh)) return false;                   // the native dropdown only offers this building's staff
            try { foreach (var s in wh.vehicleSlots) if (s != null && s.employeeDriverId == newId) return false; } catch { }
            try
            {
                var vi = SaveGameManager.Current.VehicleInstances.Find(x => x != null && x.id == slot.vehicleInstanceId);
                if (vi == null || !drv.HasSkill("ba:skill_deliverydriver")
                    || drv.GetSkillValue("ba:skill_deliverydriver") < (float)vi.VehicleType.requiredDeliveryDriverSkillValue) return false;
            }
            catch { return false; }
            slot.employeeDriverId = newId;
            try { oldDrv?.UpdateWeeklyHoursAndDays(); } catch { }
            try { drv.UpdateWeeklyHoursAndDays(); } catch { }
            try { GameEvent.Invoke("ba:gameevent_employeeassigned"); } catch { }
            Plugin.Logger.LogInfo($"{Tag} '{p.PlayerId}' assigned driver '{newId}' to slot {p.SlotIndex + 1} at '{p.AddressKey}'.");
            return true;
        }

        private static FactoryWorkstationInstance StationOf(Entities.Warehouse wh, string id)
        {
            try { return !string.IsNullOrEmpty(id) && wh.itemInstances.TryGetValue(id, out var ii) ? ii as FactoryWorkstationInstance : null; }
            catch { return null; }
        }

        private static bool ApplyRecipeOp(Entities.Warehouse wh, SharedWorkEditPayload p)
        {
            var fw = StationOf(wh, p.StationId);
            if (fw == null || string.IsNullOrEmpty(p.StrValue)) return false;
            try
            {
                var ws = fw.Workstation;
                if (ws?.supportedRecipes == null || !ws.supportedRecipes.Exists(r => r != null && r.id == p.StrValue)) return false;
            }
            catch { return false; }
            if (fw.selectedRecipeId == p.StrValue) return true;
            fw.selectedRecipeId = p.StrValue;
            try { GameEvent.Invoke("ba:gameevent_onfactorymachinerecipechanged"); } catch { }   // how native tools announce it
            Plugin.Logger.LogInfo($"{Tag} '{p.PlayerId}' set the recipe of workstation '{p.StationId}' at '{p.AddressKey}'.");
            return true;
        }

        private static bool ApplyProduceOp(Entities.Warehouse wh, SharedWorkEditPayload p)
        {
            var fw = StationOf(wh, p.StationId);
            if (fw == null) return false;
            int amount = Mathf.Clamp(p.IntValue, 0, 1000000);
            // The native panel copies the setting to every same-product workstation in the group — the helper's
            // machine ran that same native code, so each station arrives as its own op; applied per station here.
            fw.produceUpTo = p.BoolValue;
            fw.produceUpToValue = amount;
            Plugin.Logger.LogInfo($"{Tag} '{p.PlayerId}' set produce-up-to of workstation '{p.StationId}' at '{p.AddressKey}': {(p.BoolValue ? amount.ToString() : "continuous")}.");
            return true;
        }

        private static bool ApplyOrderOp(Entities.Warehouse wh, SharedWorkEditPayload p)
        {
            string v = p.StrValue ?? "";
            int bar = v.IndexOf('|');
            if (bar <= 0 || bar >= v.Length - 1) return false;
            string type = v.Substring(0, bar);
            var ids = v.Substring(bar + 1).Split(',');
            int applied = 0;
            for (int i = 0; i < ids.Length && i < MaxStations; i++)
            {
                var fw = StationOf(wh, ids[i]);
                if (fw == null || fw.workstationType != type) continue;
                fw.priority = i; applied++;
            }
            if (applied > 0) Plugin.Logger.LogInfo($"{Tag} '{p.PlayerId}' reordered {applied} '{type}' workstation(s) at '{p.AddressKey}'.");
            return applied > 0;
        }

        private static bool ApplyAliasOp(Entities.Warehouse wh, SharedWorkEditPayload p)
        {
            var fw = StationOf(wh, p.StationId);
            if (fw == null) return false;
            string alias = (p.StrValue ?? "").Trim();
            if (alias.Length > MaxAliasLen) alias = alias.Substring(0, MaxAliasLen);
            if (alias.Length == 0) return false;   // the native field ignores empty names too
            fw.alias = alias;                       // rename ruled ALLOWED for the shared panel (user 2026-08-24)
            Plugin.Logger.LogInfo($"{Tag} '{p.PlayerId}' renamed workstation '{p.StationId}' at '{p.AddressKey}'.");
            return true;
        }
    }
}
