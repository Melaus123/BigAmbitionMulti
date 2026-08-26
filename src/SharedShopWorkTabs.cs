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
    ///
    ///  • INSIGHT (7a, v18): read-only dashboard on ORDINARY shared shops. Promotion + satisfaction +
    ///    per-day customers are written onto the replica (the pricing carry owns the SAME history
    ///    entries' itemSales — its apply preserves our fields); the customer-capacity table is
    ///    substituted call-scoped (the replica's itemInstances are partial/stale). No edits to route.
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
        private const int   MaxCardReqPerRound = 4;  // list-card requests per poll round (review #4: small bursts; the rest go next round)
        private const int   MaxContracts    = 32;    // sanity cap: contracts carried per shop
        private const int   MaxContractRows = 120;   // 7b-2 sanity cap: product rows carried per contract
        private const int   MaxStockRows    = 200;   // 7b-2 sanity cap: owner-counted stock figures per shop
        private const float StockAskQuiet   = 1.5f;  // 7b-2 rate cap on the stock refresh
        private const float PendingHoldSeconds = 30f;// 7b-2: how long an unanswered local edit may hold owner truth back before the owner wins (review M5)

        // ── helper-side session ──
        private static string _openAddr = "";        // the shared building whose tab is open here
        private static string _openTab  = "";        // "inventory" | "drivers" | "factory"
        private static float  _nextScan;
        private static float  _nextPoll;
        private static float  _nextCardPoll;         // review #4: cards poll on their own half-phase, never sharing a second with the tab poll
        private static bool   _orderBaselineStale;   // review #5: an external priority write invalidates the order baseline
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

        // 7a insight — PER-ADDRESS caches that survive tab switches (ruling 36: reopening must render
        // the last-known truth instantly, with a sig-carrying refresh that stays silent when unchanged).
        private const int MaxCapacityRows = 40;
        private static bool _renderInsight;
        private static readonly Dictionary<string, List<CapacityRowInfo>> _insCapByAddr = new();
        private static readonly Dictionary<string, string> _insSigByAddr = new();
        private static readonly Dictionary<string, SharedInsightInfo> _insScalarsByAddr = new();   // review N2: the replica can be rewritten while the tab is closed — re-apply on reopen
        private static readonly Dictionary<string, WorkstationInfo> _wsInfo = new();    // station id → carried info
        private static readonly Dictionary<string, (string recipe, bool upTo, int amount, string alias)> _wsBaseline = new();
        private static readonly Dictionary<string, string> _orderBaseline = new();      // workstationType → csv of ids in priority order
        private static readonly Dictionary<string, int> _resourceStock = new();         // ingredient → units, owner-counted
        private static string _factoryAddrString = "";                                  // the open factory's Address text (scoped-patch key)
        // 7b-2 (field 2026-08-26): the deliveries tab prints a per-row stock figure and a product ORDER that a
        // helper cannot derive locally. Stock rides the snapshot but deliberately stays OUT of the sig — see
        // AskDeliveriesStock for why the refresh is event-driven rather than a 5 s stream.
        private static bool  _dcForceApply;      // apply the next deliveries snapshot even when its sig is unchanged
        private static bool  _dcListChanged;     // the applied snapshot added/removed a contract — rebuild the tab, not just the open panel
        private static float _dcNextStockAsk;    // rate cap on the on-selection stock refresh
        private static bool  _dcRepainting;      // our own repaint re-enters the native row builder — it must not ask again (ask→snapshot→repaint→ask)
        private static float _dcPendingSince;    // when this machine first held owner truth back for an unanswered edit (0 = nothing pending)
        private static readonly Dictionary<string, Dictionary<string, string>> _dcSentDigest = new();   // addr → (wk#ord → the digest we ROUTED); an echo answering an older one must not overwrite a newer local edit

        public static void Reset()
        {
            CloseSession(); _logged.Clear(); _nextScan = 0f; _nextPoll = 0f; _nextCardPoll = 0f; _orderBaselineStale = false;
            _cardSlots.Clear(); _cardInv.Clear(); _cardSig.Clear(); _cardNext.Clear(); _whList = null; _renderCards = false;
            _insCapByAddr.Clear(); _insSigByAddr.Clear(); _insScalarsByAddr.Clear();
            _dcByAddr.Clear(); _dcSigByAddr.Clear(); _dcStockSig.Clear(); _dcBaseline.Clear(); _dcSentDigest.Clear();
            _dcLastSentSig = ""; _dcNextAllowedSend = 0f; _dcSendTries = 0;
            _dcWindowDepth = 0; _dcInsertActive = false; _dcTenancyRaised = false; _dcInsertReg = null; _dcInsertAddr = "";
            _dcHiddenLocal.Clear();
            _dcForceApply = false; _dcListChanged = false; _dcNextStockAsk = 0f;
            _dcPendingSince = 0f; _dcRepainting = false;
            // The stock table is SharedShopStock's; SharedShopPrices.Reset clears it at this same call site.
        }

        private static string AddrOf(BuildingRegistration reg)
        {
            try { return reg != null ? GameStateReader.AddressKey(reg) : ""; } catch { return ""; }
        }

        /// <summary>The shared building whose work tab is open here, and which tab it is. Read by
        /// SharedShopStock, whose stock substitution is scoped to the surface actually on screen.</summary>
        internal static string OpenAddr => _openAddr;
        internal static string OpenTab  => _openTab;

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
                _tabSig = "";   // review #9: a sig from another building/tab must never suppress this one's first snapshot
                if (tab == "inventory") { _rows.Clear(); _boxesMax = 0; _boxesCurrent = 0; }
                if (tab == "drivers")   { _slotInfo.Clear(); _vehReq.Clear(); _slotBaseline.Clear(); }
                if (tab == "factory")   { _wsInfo.Clear(); _wsBaseline.Clear(); _orderBaseline.Clear(); _resourceStock.Clear(); _factoryAddrString = SafeAddrString(reg); }
                // insight: the per-address cache is NOT cleared (ruling 36) — the cached sig makes the
                // refresh silent when the owner's content is unchanged (the P3 pattern).
                if (tab == "insight" && _insSigByAddr.TryGetValue(addr, out var cachedSig))
                {
                    _tabSig = cachedSig;
                    Plugin.Logger.LogInfo($"{Tag} insight reopened for '{addr}' — cached figures render now; silent-if-unchanged refresh in flight.");   // review N3: the request trail is the interview
                }
                if (tab == "deliveries")
                {
                    _dcLastSentSig = ""; _dcNextAllowedSend = 0f; _dcSendTries = 0;
                    // Per-session, per-BUILDING: switching shops inside BizMan does not disable the page, so
                    // without this the previous shop's hold clock carried over and a pending edit on THIS shop
                    // could be discarded on its first held snapshot (fix-verification M5).
                    _dcPendingSince = 0f; _dcForceApply = false; _dcListChanged = false;
                    if (_dcSigByAddr.TryGetValue(addr, out var dcSig))
                    {
                        _tabSig = dcSig;   // ruling 36: carried contracts render now; refresh silent if unchanged
                        Plugin.Logger.LogInfo($"{Tag} deliveries reopened for '{addr}' — carried contracts render now; silent-if-unchanged refresh in flight.");
                    }
                }
                RequestInfo(addr, tab, _tabSig);
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
            _renderInventory = false; _renderDrivers = false; _renderFactory = false; _renderDcFigures = false;
            _renderInsight = false;   // the per-address insight caches deliberately survive (ruling 36)
            _renderDeliveries = false; _renderDcFigures = false;   // carried contracts survive too (ruling 36); only Reset() drops them
            _dcForceApply = false; _dcListChanged = false; _dcPendingSince = 0f;   // per-session flags; the carried caches survive (ruling 36)
            _dcSentDigest.Clear();   // a stale "what we routed" map must not trip the re-route test on a later echo
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

        /// <summary>Returns true when any carried driver id could NOT be resolved locally (its injected
        /// record has not arrived) — the caller then leaves the sig unstored so the next round retries.</summary>
        private static bool ApplySlotsToReplica(BuildingRegistration reg, List<DriverSlotInfo> slots)
        {
            if (reg is not Entities.Warehouse wh || slots == null) return false;
            if (wh.vehicleSlots == null) wh.vehicleSlots = new List<Entities.VehicleSlot>();
            bool unresolved = false;
            foreach (var s in slots)
            {
                // Review #13: never GROW past the native slot count (the load-time sizing guard owns it) —
                // an over-long list would persist in the helper's save.
                if (s == null || s.Index < 0 || s.Index >= wh.vehicleSlots.Count) continue;
                var slot = wh.vehicleSlots[s.Index];
                if (slot == null) continue;
                slot.vehicleInstanceId = s.VehicleId ?? "";
                // Review #3: the native card derefs GetEmployeeById(id) with NO null guard — an id whose
                // injected record has not landed yet would NPE the whole warehouse list. Only resolvable
                // ids are written; the rest read as unassigned until the roster arrives.
                string drv = s.DriverId ?? "";
                if (drv.Length != 0)
                {
                    bool known = false;
                    try { known = Helpers.EmployeeHelper.GetEmployeeById(drv, showError: false) != null; } catch { }
                    if (!known) { drv = ""; unresolved = true; }
                }
                slot.employeeDriverId = drv;
            }
            return unresolved;
        }

        /// <summary>Put the owner's slot contents onto the replica so the native tab (and its checks) read truth.</summary>
        private static void ApplyDriversSnapshot(SharedWorkInfoPayload p, BuildingRegistration reg)
        {
            _slotInfo.Clear(); _vehReq.Clear(); _slotBaseline.Clear();
            bool unresolved = ApplySlotsToReplica(reg, p.Slots);
            var whb = reg as Entities.Warehouse;
            foreach (var s in p.Slots)
            {
                if (s == null || s.Index < 0 || s.Index >= MaxSlots) continue;
                _slotInfo[s.Index] = s;
                // The baseline is what the REPLICA now holds (review #3: unresolvable ids were not
                // written) — baselining the owner's value would make the scan route a spurious unassign.
                string written = "";
                try { if (whb?.vehicleSlots != null && s.Index < whb.vehicleSlots.Count) written = whb.vehicleSlots[s.Index]?.employeeDriverId ?? ""; } catch { }
                _slotBaseline[s.Index] = written;
                if (!string.IsNullOrEmpty(s.VehicleId)) _vehReq[s.VehicleId] = s.RequiredSkill;
            }
            if (unresolved) _tabSig = "";   // next poll re-fetches and retries the resolve
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
                    // Review #8: only CARRIED stations are substituted — anything else (e.g. the helper's
                    // OWN factory reached while this tab is open) falls through to the native check.
                    if (!_wsInfo.TryGetValue(__instance?.id ?? "", out var info) || info?.Stacked == null) return true;
                    __result = info.Stacked.Contains(machineName);
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
                        // Review #7: the wire carries NAMES only — a blind rebuild would strip childId /
                        // attachmentIndex off attachments the interior sync already linked. Rebuild only
                        // when the name sequence differs, carrying matching entries over intact.
                        bool same = fw.stackedItems.Count == w.Stacked.Count;
                        if (same)
                            for (int si = 0; si < w.Stacked.Count && same; si++)
                                same = fw.stackedItems[si] != null && fw.stackedItems[si].childItemName == w.Stacked[si];
                        if (!same)
                        {
                            var old = new List<AttachableChild>(fw.stackedItems);
                            fw.stackedItems.Clear();
                            foreach (var name in w.Stacked)
                            {
                                if (string.IsNullOrEmpty(name)) continue;
                                AttachableChild keep = null;
                                for (int oi = 0; oi < old.Count; oi++)
                                    if (old[oi] != null && old[oi].childItemName == name) { keep = old[oi]; old.RemoveAt(oi); break; }
                                fw.stackedItems.Add(keep ?? new AttachableChild { childItemName = name });
                            }
                        }
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
            if (_orderBaselineStale) { _orderBaselineStale = false; RebuildOrderBaseline(reg); }   // review #5: external priority write
            var pendingProduce = new List<(string id, bool upTo, int amount)>();
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
                    pendingProduce.Add((kv.Key, upTo, amount));   // review #12: batched below — one op per group
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
            if (pendingProduce.Count > 0)
            {
                // Review #12: the native panel copies produce-up-to across the whole product group, so ONE
                // user action arrives as N station changes — batched per (setting, amount) so the rate cap
                // can never half-apply a group.
                var groups = new Dictionary<string, List<string>>();
                foreach (var pp in pendingProduce)
                {
                    string gk = (pp.upTo ? "1" : "0") + "|" + pp.amount;
                    if (!groups.TryGetValue(gk, out var lst)) groups[gk] = lst = new List<string>();
                    lst.Add(pp.id);
                }
                foreach (var g in groups)
                {
                    bool upTo = g.Key[0] == '1';
                    int amount = int.Parse(g.Key.Substring(g.Key.IndexOf('|') + 1));
                    SendEdit(new SharedWorkEditPayload { PlayerId = MPConfig.PlayerId, AddressKey = _openAddr, Op = "produce", StationId = string.Join(",", g.Value), BoolValue = upTo, IntValue = amount });
                    Plugin.Logger.LogInfo($"{Tag} routing produce-up-to of {g.Value.Count} workstation(s) at '{_openAddr}' to the owner: {(upTo ? amount.ToString() : "continuous")}.");
                }
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

        // ═══════════════ 7a — INSIGHT (read-only dashboard) ═══════════════

        /// <summary>Insight on a shared shop: session opens (first open requests the owner's figures) and the
        /// NATIVE render then runs on the replica. TRUE first open (no cache for this address): the replica's
        /// own AI-derived promotion/satisfaction are ZEROED first — plausible-but-wrong numbers must never
        /// pose as the owner's (review MINOR-4); the snapshot overwrites them one round-trip later. A REOPEN
        /// renders the carried values still sitting on the replica plus the cached capacity (ruling 36 — no
        /// stale flash), with a sig-carrying refresh in flight. Non-shared shops: untouched native path.</summary>
        [HarmonyPatch(typeof(BizManInsight), nameof(BizManInsight.RefreshData))]
        public static class Patch_BizManInsight_RefreshData_Session
        {
            static void Prefix(BuildingRegistration buildingRegistration)
            {
                try
                {
                    if (!OpenSession("insight")) return;
                    var reg = buildingRegistration;
                    if (reg == null) return;
                    if (_insScalarsByAddr.TryGetValue(_openAddr, out var cached))
                    {
                        // Review N2: the replica may have been rewritten while the tab was closed and the
                        // cached sig would suppress a resend — re-assert the carried truth before render.
                        ApplyInsightScalars(reg, cached);
                        return;
                    }
                    // True first open: never present the replica's own AI-derived figures as the owner's.
                    if (reg.promotion != null) { reg.promotion.total = 0; reg.promotion.trafficIndex = 0; reg.promotion.marketing = 0; }
                    if (reg.satisfaction != null)
                    {
                        reg.satisfaction.overall = 0; reg.satisfaction.customerService = 0;
                        reg.satisfaction.pricing = 0; reg.satisfaction.facility = 0; reg.satisfaction.cleanliness = 0;
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} insight open: {ex.Message}"); }
            }
        }

        /// <summary>Call-scoped: while a shared shop's Insight tab is open HERE and a snapshot has landed, the
        /// customer-capacity table is the owner's rows — the replica's itemInstances are partial/stale (empty
        /// unless someone stood inside recently) and would render a wrong, usually empty, list. Gated on the
        /// exact registration, so the owner's own machine, other shops, and the sim never see substituted data.
        /// The native ItemCapacity/Shelf types recompute their limits from the carried shelves.</summary>
        [HarmonyPatch(typeof(ItemHelper), nameof(ItemHelper.GetItemsSortedByCapacity))]
        public static class Patch_ItemsSortedByCapacity_Carried
        {
            static bool Prefix(BuildingRegistration registration, bool requireEmployee, bool checkMissingRequirements, ref IEnumerable<BigAmbitions.Items.Item.ItemCapacity> __result)
            {
                try
                {
                    // Review MAJOR-3: the carried rows were computed with the Insight tab's flags — any
                    // caller with different semantics (the sim's requireEmployee pass) keeps native data.
                    if (requireEmployee || !checkMissingRequirements) return true;
                    if (_openTab != "insight") return true;
                    if (AddrOf(registration) != _openAddr) return true;
                    // No cache yet (true first open): an EMPTY table until truth arrives — never the
                    // replica's stale furniture walk (review MINOR-4's capacity half).
                    if (!_insCapByAddr.TryGetValue(_openAddr, out var rows)) rows = _emptyCap;
                    var list = new List<BigAmbitions.Items.Item.ItemCapacity>();
                    foreach (var row in rows)
                    {
                        if (row == null || string.IsNullOrEmpty(row.ItemName)) continue;
                        var cap = new BigAmbitions.Items.Item.ItemCapacity(row.ItemName);
                        if (row.Shelves != null)
                            foreach (var s in row.Shelves)
                                if (s != null) cap.itemShelves.Add(new BigAmbitions.Items.Item.ItemCapacityShelf(s.Name, s.PerHour) { amount = s.Amount });
                        list.Add(cap);
                    }
                    __result = list;
                    return false;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} capacity substitute: {ex.Message}"); return true; }
            }
        }

        private static readonly List<CapacityRowInfo> _emptyCap = new();

        /// <summary>Write the owner's Insight figures onto the replica, then re-render. The history MERGE sets
        /// only totalCustomers/hourReports — itemSales on the same entries belong to the pricing carry
        /// (SharedShopPrices preserves ours on its replace, we never touch its fields).</summary>
        private static void ApplyInsightScalars(BuildingRegistration reg, SharedInsightInfo b)
        {
            try
            {
                if (reg.promotion == null) reg.promotion = new Promotion();
                reg.promotion.total = b.PromoTotal; reg.promotion.trafficIndex = b.PromoTraffic; reg.promotion.marketing = b.PromoMarketing;
                if (reg.satisfaction == null) reg.satisfaction = new Satisfaction();
                reg.satisfaction.overall = b.SatOverall; reg.satisfaction.customerService = b.SatService;
                reg.satisfaction.pricing = b.SatPricing; reg.satisfaction.facility = b.SatInterior; reg.satisfaction.cleanliness = b.SatClean;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} insight scalars: {ex.Message}"); }
        }

        private static void ApplyInsightSnapshot(SharedWorkInfoPayload p, BuildingRegistration reg)
        {
            var b = p.Insight;
            if (b != null)
            {
                ApplyInsightScalars(reg, b);
                _insScalarsByAddr[p.AddressKey] = b;   // review N2: reopen re-asserts these against replica rewrites
            }
            try
            {
                var gi = SaveGameManager.Current;
                // Review MAJOR-1: day numbers are OWNER-basis — rebase to the local clock exactly as
                // the pricing carry does (SharedShopPrices' shift), so the two writers of this list
                // always key the same calendar day. OwnerDay < 0 = an owner without a world.
                // Review N1: the guard skips ONLY the history — scalars/capacity/sig still apply, so
                // the sig gate can never vouch for an un-applied snapshot.
                if (gi != null && p.OwnerDay >= 0)
                {
                int shift = gi.Day - p.OwnerDay;
                if (reg.orderHistory == null) reg.orderHistory = new List<Entities.OrderHistoryEntry>();
                if (p.InsightDays != null)
                    foreach (var d in p.InsightDays)
                    {
                        if (d == null) continue;
                        int day = d.Day + shift;
                        if (day < 0) continue;
                        var e = reg.orderHistory.Find(h => h != null && h.dayNumber == day);
                        if (e == null)
                        {
                            e = new Entities.OrderHistoryEntry
                            {
                                dayNumber = day,
                                itemSales = new List<Entities.OrderHistoryEntry.ItemReport>(),
                                hourReports = new List<Entities.OrderHistoryEntry.HourReport>(),
                            };
                            reg.orderHistory.Add(e);
                        }
                        e.totalCustomers = d.Customers;
                        if (d.Hours != null)
                        {
                            var hr = new List<Entities.OrderHistoryEntry.HourReport>();
                            for (int h = 0; h < d.Hours.Count && h < 24; h++)
                                hr.Add(new Entities.OrderHistoryEntry.HourReport { hour = h, customers = d.Hours[h] });
                            e.hourReports = hr;
                        }
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} insight history: {ex.Message}"); }
            var cap = new List<CapacityRowInfo>();
            if (p.Capacity != null)
                foreach (var c in p.Capacity)
                    if (c != null && cap.Count < MaxCapacityRows) cap.Add(c);
            _insCapByAddr[p.AddressKey] = cap;
            _insSigByAddr[p.AddressKey] = p.Sig ?? "";
            _renderInsight = true;
        }

        /// <summary>OWNER: the Insight tab's figures, read straight off the real registration.</summary>
        private static void BuildInsight(BuildingRegistration reg, SharedWorkInfoPayload reply)
        {
            var b = new SharedInsightInfo();
            try
            {
                if (reg.promotion != null) { b.PromoTotal = reg.promotion.total; b.PromoTraffic = reg.promotion.trafficIndex; b.PromoMarketing = reg.promotion.marketing; }
                if (reg.satisfaction != null)
                {
                    b.SatOverall = reg.satisfaction.overall; b.SatService = reg.satisfaction.customerService;
                    b.SatPricing = reg.satisfaction.pricing; b.SatInterior = reg.satisfaction.facility; b.SatClean = reg.satisfaction.cleanliness;
                }
            }
            catch { }
            reply.Insight = b;
            try
            {
                int today = SaveGameManager.Current?.Day ?? -1;
                if (today < 0) return;   // review MINOR-5: no world, no figures — never fabricate days
                for (int d = Math.Max(0, today - 7); d <= today; d++)   // the native chart windows: 7-day totals incl. today; hours for yesterday
                {
                    Entities.OrderHistoryEntry? e = null;
                    try { e = reg.orderHistory?.Find(h => h != null && h.dayNumber == d); } catch { }
                    var info = new InsightDayInfo { Day = d, Customers = e?.totalCustomers ?? 0 };
                    if (d == today - 1 && e?.hourReports != null)
                    {
                        var hours = new int[24];
                        foreach (var r in e.hourReports) if (r != null && r.hour >= 0 && r.hour < 24) hours[r.hour] = r.customers;
                        info.Hours = new List<int>(hours);
                    }
                    reply.InsightDays.Add(info);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} insight days build: {ex.Message}"); }
            try
            {
                foreach (var cap in reg.itemInstances.Values.GetItemsSortedByCapacity(reg))
                {
                    if (cap == null || string.IsNullOrEmpty(cap.itemName)) continue;
                    var row = new CapacityRowInfo { ItemName = cap.itemName };
                    if (cap.itemShelves != null)
                        foreach (var s in cap.itemShelves)
                            if (s != null) row.Shelves.Add(new CapShelfInfo { Name = s.itemName ?? "", Amount = s.amount, PerHour = s.customersPerHour });
                    reply.Capacity.Add(row);
                    if (reply.Capacity.Count >= MaxCapacityRows) break;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} insight capacity build: {ex.Message}"); }
        }

        // ═══════════════ 7b — DELIVERIES (carried contracts + routed edits) ═══════════════

        // Carried DeliveryContract INSTANCES per shared address. They live OUTSIDE the save list — the
        // helper's own delivery sim can never execute or bill them — and are INSERTED into the real
        // list only for the duration of the tab's synchronous native render (the RaiseTenancy shape:
        // restored in a finalizer before any economy code can run). The instances persist across tab
        // switches (ruling 36) and snapshots merge IN PLACE so the settings panel's held reference
        // stays valid. Money rule (ruling 33): everything here bills the OWNER on delivery — allowed.
        private static readonly Dictionary<string, List<Entities.DeliveryContract>> _dcByAddr = new();
        private static readonly Dictionary<string, string> _dcSigByAddr = new();   // owner sig — reopen refresh is silent-if-unchanged
        private static readonly Dictionary<string, string> _dcStockSig  = new();   // 7b-2: digest of the carried stock figures (they ride OUTSIDE the sig)
        private static readonly Dictionary<string, Dictionary<string, string>> _dcBaseline = new();   // addr → (wk#ord → owner-truth per-contract digest); review MINOR-6: only CHANGED contracts route
        private static bool _renderDeliveries;
        // A figures-only change (stock / sold-last-week) must NOT go through the panel re-render: native's
        // LoadProducts resets the selected row, which would COLLAPSE the very row whose number just arrived.
        // Re-binding the scroller refreshes the expanded row's figures and keeps it open.
        private static bool _renderDcFigures;
        private static string _dcLastSentSig = ""; private static float _dcNextAllowedSend; private static int _dcSendTries;
        private static int _dcWindowDepth; private static bool _dcInsertActive; private static bool _dcTenancyRaised;
        private static string _dcInsertAddr = ""; private static BuildingRegistration? _dcInsertReg;
        private static readonly List<Entities.DeliveryContract> _dcHiddenLocal = new();   // review MAJOR-2: the helper's own stale same-address contracts, parked for the window

        /// <summary>Deliveries tab on a shared shop: session + carried contracts inserted + tenancy raised
        /// for the native render (GetListOfItemsForSale must take the player branch and read the CARRIED
        /// product list — the AI branch would build the wrong rows).</summary>
        [HarmonyPatch(typeof(BizManDeliveries), nameof(BizManDeliveries.RefreshData))]
        public static class Patch_BizManDeliveries_RefreshData_Session
        {
            static void Prefix(out bool __state)
            {
                __state = false;
                try
                {
                    if (!OpenSession("deliveries")) return;
                    __state = BeginContractsWindow(_openAddr);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} deliveries tab: {ex.Message}"); }
            }
            static void Finalizer(bool __state) { if (__state) EndContractsWindow(); }
        }

        /// <summary>The product-row builder reads (and normalizes) the contract OUTSIDE RefreshData too —
        /// the settings panel calls it on selection and after cancel/start. Same window, depth-counted.</summary>
        [HarmonyPatch(typeof(BizManDeliveriesProductsScrollerController), nameof(BizManDeliveriesProductsScrollerController.LoadProducts))]
        public static class Patch_DeliveriesProducts_Load_Window
        {
            static void Prefix(Entities.DeliveryContract deliveryContract, out bool __state)
            {
                __state = false;
                try
                {
                    if (_openTab != "deliveries" || _openAddr.Length == 0) return;
                    if (!IsCarriedContract(deliveryContract)) return;
                    // Review MINOR-7: this builder never reads the contract LIST — only the tenancy
                    // half of the window is load-bearing here.
                    __state = BeginContractsWindow(_openAddr, insert: false);
                }
                catch { }
            }
            static void Finalizer(bool __state) { if (__state) EndContractsWindow(); }

        /// <summary>7b-2: stock is refreshed at the moments the game itself recomputes it, and THIS is
            /// that moment — the row models (stock included) are rebuilt here, whether the trigger was the tab
            /// opening, a contract being selected, or an order being started or cancelled (those two call the
            /// row builder directly, without going through ShowContractSettings — verified in the decompile).
            /// Event-driven rather than a five-second stream, because the OWNER's STOCK figure is equally
            /// frozen between panel opens (native computes it in the row model's constructor), so a helper
            /// screen that ticked live would not be parity (ruling 28).
            ///
            /// CORRECTION 2026-08-26 (review MAJOR-1): that reasoning holds for stock and NOT for "sold last
            /// week". Native recomputes THAT one in BizManDeliveriesProductCellView.OpenBottomPart, which runs
            /// on every row EXPAND and does not pass through this builder at all — the owner's figure is live
            /// per expand. Expanding a row therefore arms its own ask; see Patch_ProductCell_OpenBottom_Ask.</summary>
            static void Postfix(Entities.DeliveryContract deliveryContract)
            {
                try
                {
                    if (_dcRepainting || _openTab != "deliveries" || _openAddr.Length == 0) return;
                    if (!IsCarriedContract(deliveryContract)) return;
                    if (Time.unscaledTime < _dcNextStockAsk) return;
                    _dcNextStockAsk = Time.unscaledTime + StockAskQuiet;
                    _dcForceApply = true;                    // the sig will be unchanged; this reply must still land
                    RequestInfo(_openAddr, "deliveries");    // no sig — the owner always answers
                }
                catch { }
            }
        }

        private static bool BeginContractsWindow(string addr, bool insert = true)
        {
            if (_dcWindowDepth++ == 0)
            {
                try
                {
                    var gi = SaveGameManager.Current;
                    var reg = GameStatePatcher.FindRegistration(addr);
                    if (gi != null && reg != null)
                    {
                        _dcInsertReg = reg; _dcInsertAddr = addr;
                        // Review MINOR-8: tenancy is ALWAYS raised — GetListOfItemsForSale must take the
                        // player branch even before the first snapshot lands.
                        _dcTenancyRaised = SharedShopVisibility.RaiseTenancy(reg, addr);
                        if (insert)
                        {
                            // Review MAJOR-2: the helper's own STALE contracts at this address would
                            // render as unrouted phantoms — park them for the window's duration.
                            // Review 7b-N1: a survivor from an aborted window is RESTORED first, never dropped.
                            if (_dcHiddenLocal.Count > 0)
                            {
                                foreach (var s in _dcHiddenLocal) if (s != null && !gi.DeliveryContracts.Contains(s)) gi.DeliveryContracts.Add(s);
                                _dcHiddenLocal.Clear();
                            }
                            var carried = _dcByAddr.TryGetValue(addr, out var lc) ? lc : null;
                            foreach (var c in gi.DeliveryContracts)
                                if (c != null && c.businessAddress == reg.Address && (carried == null || !carried.Contains(c)))
                                    _dcHiddenLocal.Add(c);
                            foreach (var c in _dcHiddenLocal) gi.DeliveryContracts.Remove(c);
                            if (_dcHiddenLocal.Count > 0 && _logged.Add("dc-phantom|" + addr))
                                Plugin.Logger.LogWarning($"{Tag} {_dcHiddenLocal.Count} LOCAL contract(s) for shared '{addr}' hidden from the tab — stale save data for another player's shop.");
                            if (carried != null)
                                foreach (var c in carried) if (c != null && !gi.DeliveryContracts.Contains(c)) gi.DeliveryContracts.Add(c);
                            _dcInsertActive = true;
                        }
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} contracts window begin: {ex.Message}"); }
            }
            return true;
        }

        private static void EndContractsWindow()
        {
            if (_dcWindowDepth == 0 || --_dcWindowDepth > 0) return;
            // EVERY undo runs, each isolated, in a fixed order, and none of them may be skipped because an
            // earlier one threw (fix-verification MAJOR). The residues are not cosmetic:
            //   • an owner's contract left in THIS machine's live save list is executed and BILLED by the
            //     helper's own delivery sim, and persists on the next save — the exact breach the carry exists
            //     to prevent (ruling 33);
            //   • tenancy left raised leaves `RentedByPlayer` true on a replica of someone else's shop, which
            //     native rent and ownership code reads;
            //   • the helper's parked contracts are their OWN save data.
            // The state reset then runs unconditionally, so nothing latches either.
            try
            {
                var gi = SaveGameManager.Current;
                if (_dcInsertActive && gi != null && _dcByAddr.TryGetValue(_dcInsertAddr, out var list))
                    foreach (var c in list) if (c != null) gi.DeliveryContracts.Remove(c);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} contracts window end (carried removal): {ex.Message}"); }
            try
            {
                if (_dcTenancyRaised && _dcInsertReg != null) SharedShopVisibility.LowerTenancy(_dcInsertReg, true);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} contracts window end (tenancy): {ex.Message}"); }
            try
            {
                // Review 7b-N1 + review MINOR: the parked LOCAL contracts are the helper's own SAVE DATA, and
                // the restore is deliberately NOT gated on _dcInsertActive — parked-but-not-active is exactly
                // the state whose restore matters most.
                if (_dcHiddenLocal.Count > 0)
                {
                    var gi = SaveGameManager.Current;
                    if (gi != null)
                        foreach (var c in _dcHiddenLocal) if (c != null && !gi.DeliveryContracts.Contains(c)) gi.DeliveryContracts.Add(c);
                    _dcHiddenLocal.Clear();
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} contracts window end (parked restore): {ex.Message}"); }
            _dcInsertActive = false; _dcTenancyRaised = false; _dcInsertReg = null; _dcInsertAddr = "";
        }

        private static readonly System.Reflection.FieldInfo? _fSettingsContract =
            AccessTools.Field(typeof(BizManContractSettings), "_deliveryContract");

        /// <summary>Refresh ONLY the carried figures on an open contract panel: re-bind the product rows
        /// without rebuilding them. Native's LoadProducts resets the selected row, so the full re-render would
        /// close the very row whose number just arrived — and "sold last week" is printed only by the expanded
        /// row (review MAJOR-1). Re-binding runs the cell's own SetData, which re-opens the selected row's
        /// bottom panel and recomputes that figure from the freshly carried table.
        ///
        /// The STOCK label lives on the row MODEL, which a re-bind does not rebuild, so its field is refreshed
        /// here first (fix-verification MAJOR-B). Leaving it alone looked like parity — native's own figure is
        /// frozen between row builds too — but the owner's frozen read is LIVE at the moment it is taken,
        /// while a helper's comes from the carried table, which outlives the tab. On a shop with ONE contract
        /// there is then no path back: every reopen answers from the cached sig, arms an ask, routes the reply
        /// here, and the correct figure sits in the table unshown, forever one ask behind.
        ///
        /// Scroll position is captured and restored, exactly as native's own selection reloads do
        /// (SelectCellAndReload / UnSelectRowAndReload): a bare ReloadData snaps the list to the top, which
        /// would scroll the refreshed row out of view (fix-verification MAJOR-A).</summary>
        private static void RepaintDeliveryFigures(BizManBusiness page)
        {
            try
            {
                var del = page.GetComponentInChildren<BizManDeliveries>(true);
                var set = del != null ? del.contractSettings : null;
                if (set == null || !set.gameObject.activeInHierarchy) return;
                var sc = set.bizManDeliveriesProductsScrollerController;
                if (sc == null || sc.scroller == null) return;
                var reg = GameStatePatcher.FindRegistration(_openAddr);
                _dcRepainting = true;   // re-binding re-enters OpenBottomPart; it must not arm another ask
                try
                {
                    if (reg != null && sc.data != null)
                        foreach (var m in sc.data)
                        {
                            var it = m?.deliveryContractItem;
                            if (it == null || string.IsNullOrEmpty(it.itemName)) continue;
                            // The same read the native model constructor does — our postfix answers it from
                            // the freshly carried table.
                            m!.stock = BuildingHelper.CountTotalResourcesInStock(reg, it.itemName, includeProducers: true, includePallets: false);
                        }
                    float keep = sc.scroller.ScrollPosition;
                    sc.scroller.ReloadData();
                    sc.scroller.SetScrollPositionImmediately(keep);
                }
                finally { _dcRepainting = false; }
            }
            catch (Exception ex)
            {
                _dcRepainting = false;
                Plugin.Logger.LogWarning($"{Tag} deliveries figure repaint: {ex.Message}");
            }
        }

        /// <summary>Repaint an open Deliveries tab WITHOUT throwing the reader back to the first contract.
        ///
        /// Native RefreshData hides the settings panel, rebuilds the contract list and auto-selects entry #1.
        /// Every landing snapshot ran it — including the echo of the helper's OWN amount edit — so editing the
        /// second contract kicked you back to the first a few seconds later, on both machines (field
        /// 2026-08-26). When the contract SET is unchanged and a panel is open on a contract that still exists,
        /// only that contract is re-rendered; a changed set still needs the full rebuild.</summary>
        private static void RepaintDeliveries(BizManDeliveries del, bool listChanged, IEnumerable<Entities.DeliveryContract>? live)
        {
            try
            {
                var set = del.contractSettings;
                Entities.DeliveryContract? open = null;
                if (_fSettingsContract == null && _logged.Add("dc-norefl"))
                    Plugin.Logger.LogWarning($"{Tag} BizManContractSettings._deliveryContract did not resolve — every deliveries repaint falls back to the full rebuild, which jumps back to the first contract.");
                if (!listChanged && set != null && set.gameObject.activeInHierarchy && _fSettingsContract != null)
                    open = _fSettingsContract.GetValue(set) as Entities.DeliveryContract;
                if (open != null)
                {
                    // After an End Contract the panel's contract is gone from the live list, and only a full
                    // rebuild can drop its row.
                    bool stillThere = false;
                    if (live != null) foreach (var c in live) if (ReferenceEquals(c, open)) { stillThere = true; break; }
                    if (!stillThere) open = null;
                }
                // Both paths run under the guard: the full rebuild re-selects a contract and so re-enters the
                // native row builder too, and a repaint asking for the stock it was just given is a round trip
                // for nothing.
                _dcRepainting = true;
                try
                {
                    if (open != null && set != null) { set.ShowContractSettings(open); return; }
                    del.RefreshData();
                }
                finally { _dcRepainting = false; }
            }
            catch (Exception ex)
            {
                _dcRepainting = false;
                Plugin.Logger.LogWarning($"{Tag} deliveries repaint: {ex.Message}");
            }
        }

        /// <summary>"Sold last week" is printed ONLY by an expanded product row, and native recomputes it
        /// inside OpenBottomPart every single time a row opens — it is live per expand, not frozen at panel
        /// open the way stock is (review MAJOR-1 corrected the opposite assumption). Expanding a row is
        /// therefore its own native recompute moment, and it does not pass through the row builder, so it
        /// needs its own ask. The row draws with the figure currently held; the reply repaints it in place a
        /// moment later WITHOUT collapsing it (RepaintDeliveryFigures).</summary>
        [HarmonyPatch(typeof(BizManDeliveriesProductCellView), "OpenBottomPart")]
        public static class Patch_ProductCell_OpenBottom_Ask
        {
            static void Postfix()
            {
                try
                {
                    if (_dcRepainting || _openTab != "deliveries" || _openAddr.Length == 0) return;
                    if (Time.unscaledTime < _dcNextStockAsk) return;
                    _dcNextStockAsk = Time.unscaledTime + StockAskQuiet;
                    _dcForceApply = true;                    // the sig will be unchanged; this reply must still land
                    RequestInfo(_openAddr, "deliveries");    // no sig — the owner always answers
                }
                catch { }
            }
        }

        /// <summary>Review MINOR-9: scoped to the OPEN address — the window it guards only ever covers that one.</summary>
        private static bool IsCarriedContract(Entities.DeliveryContract c)
            => c != null && _openAddr.Length > 0 && _dcByAddr.TryGetValue(_openAddr, out var l) && l.Contains(c);

        /// <summary>The item names a helper can actually EDIT on this shop's contracts — the shop's sale
        /// list + the business type's primary products (+ paper bags where customers need them). This is
        /// EXACTLY the set the native row builder keeps; rows outside it are silently removed by every
        /// render (AddMissingProducts), so they must never enter a digest or be zeroed on the owner
        /// (review MAJOR-1: render housekeeping must never masquerade as a user edit).</summary>
        private static HashSet<string> ContractDomain(BuildingRegistration reg)
        {
            var set = new HashSet<string>();
            try
            {
                if (reg.cachedAvailableProducts != null)
                    foreach (var s in reg.cachedAvailableProducts) if (!string.IsNullOrEmpty(s)) set.Add(s);
                var bt = BusinessTypeHelper.GetData(reg);
                if (bt != null)
                {
                    foreach (var s in bt.GetPrimaryProducts()) if (!string.IsNullOrEmpty(s)) set.Add(s);
                    if (bt.HasTag(BigAmbitions.Tags.TagRef.Businesstag.customersneedpaperbags)) set.Add("ba:itemname_paperbag");
                }
            }
            catch { }
            return set;
        }

        private static string WholesaleKeyOf(Entities.DeliveryContract c)
        {
            try { return AddrOf(BuildingHelper.GetBuildingRegistration(c.wholesaleAddress)); } catch { return ""; }
        }

        /// <summary>One contract's editable-state digest. Amount-0 rows and rows OUTSIDE the edit domain
        /// are excluded — the native row builder injects/removes those locally on every render.</summary>
        private static string ContractDigestOne(Entities.DeliveryContract c, HashSet<string> domain)
        {
            var rows = new List<string>();
            if (c.items != null)
                foreach (var it in c.items)
                    if (it != null && it.amount > 0 && domain.Contains(it.itemName)) rows.Add(it.itemName + "=" + it.amount);
            return DigestOf(c.enabled, c.isUrgentOrder, c.repeatingOrder, c.nextDeliveryDay, rows);
        }

        /// <summary>The same digest built from the OWNER's wire description instead of a local instance, so a
        /// baseline entry seeded from the wire is comparable with one digested from an object (fix-verification
        /// M4). Row ORDER is deliberately not part of either: the two machines can hold the same amounts in a
        /// different order for a moment, and that must not read as an edit.</summary>
        private static string ContractDigestOfInfo(ContractInfo info, HashSet<string> domain, int shift)
        {
            var rows = new List<string>();
            if (info.Items != null)
                foreach (var it in info.Items)
                    if (it != null && it.Amount > 0 && !string.IsNullOrEmpty(it.ItemName) && domain.Contains(it.ItemName))
                        rows.Add(it.ItemName + "=" + it.Amount);
            return DigestOf(info.Enabled, info.Urgent, info.Repeating, info.NextDeliveryDay + shift, rows);
        }

        private static string DigestOf(bool enabled, bool urgent, bool repeating, int nextDay, List<string> rows)
        {
            rows.Sort(StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder(64);
            sb.Append(enabled ? 1 : 0).Append(urgent ? 1 : 0).Append(repeating ? 1 : 0).Append(':').Append(nextDay);
            foreach (var r in rows) sb.Append(',').Append(r);
            return sb.ToString();
        }

        /// <summary>The per-contract baseline map (wk#ord → digest) for one address's carried list.</summary>
        private static Dictionary<string, string> ContractBaselineOf(string addr, List<Entities.DeliveryContract> list)
        {
            var map = new Dictionary<string, string>();
            var reg = GameStatePatcher.FindRegistration(addr);
            var domain = reg != null ? ContractDomain(reg) : new HashSet<string>();
            var ordinals = new Dictionary<string, int>();
            foreach (var c in list)
            {
                if (c == null) continue;
                string wk = WholesaleKeyOf(c); if (wk.Length == 0) continue;
                ordinals.TryGetValue(wk, out int ord); ordinals[wk] = ord + 1;
                map[wk + "#" + ord] = ContractDigestOne(c, domain);
            }
            return map;
        }

        /// <summary>End Contract: the native confirm calls contract.Remove(), which targets the SAVE list —
        /// a carried instance isn't there and native removal would silently no-op. Route to the owner and
        /// drop the carried row optimistically; the echo re-syncs (a refusal restores it).</summary>
        [HarmonyPatch(typeof(Entities.DeliveryContract), nameof(Entities.DeliveryContract.Remove))]
        public static class Patch_DeliveryContract_Remove_Routed
        {
            static bool Prefix(Entities.DeliveryContract __instance)
            {
                try
                {
                    string addr = ""; List<Entities.DeliveryContract>? list = null;
                    foreach (var kv in _dcByAddr) if (kv.Value.Contains(__instance)) { addr = kv.Key; list = kv.Value; break; }
                    if (list == null) return true;   // not carried — native path stands
                    string wk = WholesaleKeyOf(__instance);
                    int ord = 0;
                    foreach (var c in list) { if (ReferenceEquals(c, __instance)) break; if (WholesaleKeyOf(c) == wk) ord++; }
                    SendEdit(new SharedWorkEditPayload
                    {
                        PlayerId = MPConfig.PlayerId, AddressKey = addr, Op = "endcontract",
                        OwnerDay = SaveGameManager.Current?.Day ?? -1,
                        Contract = new ContractInfo { WholesaleKey = wk, Ordinal = ord },
                    });
                    list.Remove(__instance);
                    _dcBaseline[addr] = ContractBaselineOf(addr, list);
                    // Dropping a contract SHIFTS every later ordinal, and the sent-digest map is keyed by
                    // ordinal — a stale entry would then be compared against a different contract and could
                    // re-route the wrong one (fix-verification round 2, MINOR).
                    _dcSentDigest.Remove(addr);
                    _dcListChanged = true;   // a row leaves the list — the tab must rebuild, not just repaint
                    _renderDeliveries = true;
                    Plugin.Logger.LogInfo($"{Tag} end-contract routed for '{addr}' (wholesaler '{wk}').");
                    return false;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} end-contract route: {ex.Message}"); return true; }
            }
        }

        /// <summary>2 s scan: ONLY contracts whose per-contract digest differs from the owner-truth
        /// baseline route (review MINOR-6 — never a burst of unchanged siblings into the shared edit
        /// bucket). Domain-filtered digests make native render housekeeping invisible (MAJOR-1).</summary>
        private static void ScanContractEdits()
        {
            if (_openAddr.Length == 0 || !_dcByAddr.TryGetValue(_openAddr, out var list)) return;
            if (!_dcBaseline.TryGetValue(_openAddr, out var baseline)) return;   // no owner truth yet — nothing to diff against
            var gi = SaveGameManager.Current; if (gi == null) return;
            var reg = GameStatePatcher.FindRegistration(_openAddr); if (reg == null) return;
            var domain = ContractDomain(reg);
            var changed = new List<(string wk, int ord, Entities.DeliveryContract c)>();
            var ordinals = new Dictionary<string, int>();
            var agg = new System.Text.StringBuilder(64);
            foreach (var c in list)
            {
                if (c == null) continue;
                string wk = WholesaleKeyOf(c); if (wk.Length == 0) continue;
                ordinals.TryGetValue(wk, out int ord); ordinals[wk] = ord + 1;
                string dig = ContractDigestOne(c, domain);
                if (!baseline.TryGetValue(wk + "#" + ord, out var basev) || basev != dig)
                { changed.Add((wk, ord, c)); agg.Append('|').Append(wk).Append('#').Append(ord).Append(':').Append(dig); }
            }
            // NOTE the sent-digest map is deliberately NOT cleared here (fix-verification round 2, MAJOR).
            // "Nothing differs from the baseline right now" does NOT mean nothing is in flight: type 20, let
            // the scan route it, then correct it back to 10 — this line runs while the owner's answer for 20
            // is still travelling. Dropping the stamp made that echo unrecognisable as an answer to a
            // superseded value, so it overwrote the corrected 10 and the next scan, comparing against a
            // baseline that now said 20, sent nothing. The correction vanished from both machines. The map is
            // cleared where it is actually reconciled: after a merge, and on close/reset/grant-revoke.
            if (changed.Count == 0) { _dcLastSentSig = ""; _dcSendTries = 0; _dcPendingSince = 0f; return; }
            string now = agg.ToString();
            if (now == _dcLastSentSig)
            {
                if (_dcSendTries >= 3)
                {
                    // Review MINOR-5: the owner is not answering — stop resending until something changes.
                    if (_logged.Add("dc-giveup|" + _openAddr))
                        Plugin.Logger.LogWarning($"{Tag} contract edit for '{_openAddr}' unanswered after 3 sends — holding until the state or the owner changes.");
                    return;
                }
                if (Time.unscaledTime < _dcNextAllowedSend) return;   // echo in flight
            }
            else _dcSendTries = 0;
            if (!_dcSentDigest.TryGetValue(_openAddr, out var sentMap)) { sentMap = new Dictionary<string, string>(); _dcSentDigest[_openAddr] = sentMap; }
            foreach (var (wk, ord, c) in changed)
            {
                // Review M4: remember exactly WHAT was routed for this contract. When the owner's echo comes
                // back, a local state that has moved on since must not be overwritten by that older answer.
                sentMap[wk + "#" + ord] = ContractDigestOne(c, domain);
                var info = new ContractInfo
                {
                    WholesaleKey = wk, Ordinal = ord, Enabled = c.enabled, Urgent = c.isUrgentOrder,
                    NextDeliveryDay = c.nextDeliveryDay, Repeating = c.repeatingOrder,
                };
                if (c.items != null)
                    foreach (var it in c.items)
                        if (it != null && it.amount > 0 && domain.Contains(it.itemName))
                            info.Items.Add(new ContractItemInfo { ItemName = it.itemName, Amount = it.amount });
                SendEdit(new SharedWorkEditPayload { PlayerId = MPConfig.PlayerId, AddressKey = _openAddr, Op = "contract", OwnerDay = gi.Day, Contract = info });
            }
            _dcLastSentSig = now; _dcNextAllowedSend = Time.unscaledTime + 6f; _dcSendTries++;
            Plugin.Logger.LogInfo($"{Tag} {changed.Count} contract edit(s) routed for '{_openAddr}'.");
        }

        /// <summary>OWNER: contracts + the shop's product list (feeds the helper's AddMissingProducts) + a stock
        /// figure per row.
        ///
        /// EVERY product row is carried — quantity-zero rows included — IN THE OWNER'S ORDER (field 2026-08-26).
        /// The native list is drawn in the contract's own stored order with no sort, and a dropped zero row does
        /// not simply vanish on the helper: their own render re-creates it at the BOTTOM of the list. That is how
        /// the two screens came to list the same products in different orders. Carrying a zero row is a DISPLAY
        /// decision only — the edit digests still ignore amount-0 and out-of-domain rows, so native render
        /// housekeeping still cannot masquerade as an edit (7b review MAJOR-1).</summary>
        private static void BuildDeliveries(BuildingRegistration reg, SharedWorkInfoPayload reply)
        {
            var gi = SaveGameManager.Current; if (gi == null) return;
            string addr = AddrOf(reg);
            var names = new HashSet<string>();
            try
            {
                if (reg.cachedAvailableProducts != null)
                    foreach (var prod in reg.cachedAvailableProducts)
                        if (!string.IsNullOrEmpty(prod)) reply.Products.Add(new WorkProductInfo { ItemName = prod });
            }
            catch { }
            var ordinals = new Dictionary<string, int>();
            foreach (var c in gi.DeliveryContracts)
            {
                if (c == null || c.businessAddress != reg.Address) continue;
                string wk = WholesaleKeyOf(c);
                if (wk.Length == 0) continue;
                ordinals.TryGetValue(wk, out int ord); ordinals[wk] = ord + 1;
                var info = new ContractInfo
                {
                    WholesaleKey = wk, Ordinal = ord, Enabled = c.enabled, Urgent = c.isUrgentOrder,
                    NextDeliveryDay = c.nextDeliveryDay, Repeating = c.repeatingOrder, DeliveryFee = c.deliveryFee,
                };
                if (c.items != null)
                    foreach (var it in c.items)
                    {
                        if (it == null || string.IsNullOrEmpty(it.itemName)) continue;
                        if (info.Items.Count >= MaxContractRows)
                        {
                            if (_logged.Add("dc-rowcap|" + addr + "|" + wk + "#" + ord))
                                Plugin.Logger.LogWarning($"{Tag} contract '{wk}#{ord}' at '{addr}' has more than {MaxContractRows} product rows — the remainder is NOT carried to the helper.");
                            break;
                        }
                        info.Items.Add(new ContractItemInfo { ItemName = it.itemName, Amount = it.amount, OrderedThisWeek = it.amountOrderedThisWeek, OrderedLastWeek = it.amountOrderedLastWeek });
                        names.Add(it.itemName);
                    }
                reply.Contracts.Add(info);
                if (reply.Contracts.Count >= MaxContracts)
                {
                    if (_logged.Add("dc-ccap|" + addr))
                        Plugin.Logger.LogWarning($"{Tag} '{addr}' has more than {MaxContracts} delivery contracts — the remainder is NOT carried to the helper.");
                    break;
                }
            }
            // Units on hand: the native row builder counts the shop's INTERIOR per row, which a helper does not
            // hold (field 2026-08-26). Counted here with the game's own routine, on the machine whose interior is
            // authoritative — the same fix the pricing tab has had since 2026-08-22, over the WIDER set this tab
            // shows: the whole edit domain (everything its own AddMissingProducts can inject) plus every row
            // already on a contract. Complete coverage is what lets the helper show 0 for an unlisted row rather
            // than falling back to its own hollow count.
            try
            {
                foreach (var n in ContractDomain(reg)) names.Add(n);
                // One pass over the order history for ALL names (review MINOR): the native helper re-walks
                // the whole history per item, and this loop calls it up to MaxStockRows times on every poll
                // from every helper with the tab open. Mirrors native exactly, including its per-entry BREAK —
                // a day with two reports for one item contributes only the first.
                var soldMap = new Dictionary<string, int>();
                try
                {
                    int since = gi.Day - 7;
                    if (reg.orderHistory != null)
                        foreach (var h in reg.orderHistory)
                        {
                            if (h == null || h.dayNumber < since || h.itemSales == null) continue;
                            var countedThisEntry = new HashSet<string>();
                            foreach (var sale in h.itemSales)
                            {
                                if (sale == null || string.IsNullOrEmpty(sale.itemName)) continue;
                                if (!countedThisEntry.Add(sale.itemName)) continue;
                                soldMap.TryGetValue(sale.itemName, out int had);
                                soldMap[sale.itemName] = had + sale.amountSold;
                            }
                        }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} sold-last-week scan: {ex.Message}"); }
                foreach (var n in names)
                {
                    if (reply.Stock.Count >= MaxStockRows)
                    {
                        if (_logged.Add("dc-stockcap|" + addr))
                            Plugin.Logger.LogWarning($"{Tag} more than {MaxStockRows} stock figures for '{addr}' — the rest will read 0 on the helper's deliveries tab.");
                        break;
                    }
                    reply.Stock.Add(new StockInfo { ItemName = n, Count = BuildingHelper.CountTotalResourcesInStock(reg, n, includeProducers: true, includePallets: false) });
                    // "Sold last week" on the expanded row is the same class of figure: native sums THIS
                    // shop's own orderHistory over the last 7 days, and a replica's is empty — so the helper
                    // read 0, or (worse) the right number only if they had opened Inventory & Pricing for the
                    // same shop first, because that tab's carry fills the same list. Summed above from the
                    // history this machine actually holds.
                    soldMap.TryGetValue(n, out int soldN);
                    reply.SoldLastWeek.Add(new StockInfo { ItemName = n, Count = soldN });
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} deliveries stock build: {ex.Message}"); }
        }

        /// <summary>HELPER: merge the owner's contracts IN PLACE onto the carried instances (the settings
        /// panel may hold one), adopt the owner's ROW and CONTRACT order, and take the owner's stock figures.</summary>
        private static void ApplyDeliveriesSnapshot(SharedWorkInfoPayload p, BuildingRegistration reg)
        {
            var gi = SaveGameManager.Current;
            if (gi == null)
            {
                // Nothing was applied, so the sig the caller already committed must be taken back
                // (fix-verification MINOR: the one exit that still vouched for an un-applied snapshot).
                _tabSig = _dcSigByAddr.TryGetValue(p.AddressKey, out var lastGood) ? lastGood : "";
                return;
            }
            int shift = p.OwnerDay >= 0 ? gi.Day - p.OwnerDay : 0;
            _dcForceApply = false;   // this reply IS the one the forced ask was waiting for (review MINOR: cleared where it lands, never on a gate that dropped it)
            // Units on hand are display-only, so they are always safe to take — even on a snapshot whose
            // contract rows are held back below. Keyed to THIS surface (review M1).
            string stockSig = StockSigOf(p.Stock) + "/" + StockSigOf(p.SoldLastWeek);
            bool stockChanged = !_dcStockSig.TryGetValue(p.AddressKey, out var oldStockSig) || oldStockSig != stockSig;
            _dcStockSig[p.AddressKey] = stockSig;
            SharedShopStock.Set(SharedShopStock.Deliveries, p.AddressKey, p.Stock);
            SharedShopStock.SetSold(p.AddressKey, p.SoldLastWeek);
            if (!_dcByAddr.TryGetValue(p.AddressKey, out var carriedNow)) { carriedNow = new List<Entities.DeliveryContract>(); _dcByAddr[p.AddressKey] = carriedNow; }
            string appliedSig = _dcSigByAddr.TryGetValue(p.AddressKey, out var heldSig) ? heldSig : "";
            // A snapshot must never revert an edit this machine has made and the owner has not answered yet.
            // The answer is the ECHO, and echoes always apply; an ordinary poll — or the on-selection stock ask
            // added with the 2026-08-26 fix — landing between the keystroke and the 2 s scan would otherwise
            // throw the helper's typing away with no trace.
            if (!p.Echo && HasPendingContractEdits(p.AddressKey, carriedNow))
            {
                // Review M5: the hold must not be permanent. If no echo EVER comes — grant revoked between
                // send and apply, ownership flipped, message lost — an unbounded hold freezes the tab on
                // divergent local data with no on-screen signal, which is worse than losing the edit.
                if (_dcPendingSince <= 0f) _dcPendingSince = Time.unscaledTime;
                if (Time.unscaledTime - _dcPendingSince < PendingHoldSeconds)
                {
                    // The sig must describe what this machine actually HOLDS: restore the last APPLIED sig
                    // rather than blanking it, or the 5 s poll pulls a full snapshot every round for as long
                    // as the edit stays unanswered.
                    _tabSig = appliedSig;
                    if (_logged.Add("dc-hold|" + p.AddressKey))
                        Plugin.Logger.LogInfo($"{Tag} deliveries snapshot for '{p.AddressKey}' held back — contract edits here are still awaiting the owner's answer (stock figures applied).");
                    if (stockChanged) _renderDcFigures = true;
                    return;
                }
                Plugin.Logger.LogWarning($"{Tag} contract edits on '{p.AddressKey}' went unanswered for {PendingHoldSeconds:F0}s — applying the OWNER's truth and discarding the local divergence (ruling 32 beats a silent freeze).");
                _logged.Remove("dc-hold|" + p.AddressKey);
            }
            _dcPendingSince = 0f;
            var list = carriedNow;
            string beforeKeys = ContractKeysOf(list);
            string beforeRows = ContractRowsOf(list);
            bool merged = false, reroute = false, dropped = false;
            var rerouteBase = new Dictionary<string, string>();   // contracts deliberately NOT applied → the owner's truth for them
            try
            {
                // Review MINOR-4: the deliveries snapshot's product list is AUTHORITATIVE — an empty
                // one is truth too (both machines must normalize against the SAME domain).
                if (reg.cachedAvailableProducts == null) reg.cachedAvailableProducts = new List<string>();
                reg.cachedAvailableProducts.Clear();
                if (p.Products != null)
                    foreach (var pr in p.Products) if (pr != null && !string.IsNullOrEmpty(pr.ItemName)) reg.cachedAvailableProducts.Add(pr.ItemName);
                var domain = ContractDomain(reg);   // one read for the whole merge — the product list above feeds it
                _dcSentDigest.TryGetValue(p.AddressKey, out var sentMap);
                var ordered = new List<Entities.DeliveryContract>();
                var seen = new HashSet<Entities.DeliveryContract>();
                var ordinals = new Dictionary<string, int>();
                if (p.Contracts != null)
                    foreach (var info in p.Contracts)
                    {
                        if (info == null || string.IsNullOrEmpty(info.WholesaleKey)) continue;
                        if (ordered.Count >= MaxContracts)
                        {
                            dropped = true;
                            if (_logged.Add("dc-rcap|" + p.AddressKey))
                                Plugin.Logger.LogWarning($"{Tag} more than {MaxContracts} contracts received for '{p.AddressKey}' — the rest are not shown.");
                            break;
                        }
                        ordinals.TryGetValue(info.WholesaleKey, out int expect); ordinals[info.WholesaleKey] = expect + 1;
                        Entities.DeliveryContract? target = null;
                        int n = 0;
                        foreach (var c in list)
                        {
                            // Review MAJOR-3: the ordinal counts ALL same-key entries — a seen-skip here
                            // desynced the count from the sender's absolute ordinal.
                            if (c == null || WholesaleKeyOf(c) != info.WholesaleKey) continue;
                            if (n == expect) { if (!seen.Contains(c)) target = c; break; }
                            n++;
                        }
                        if (target == null)
                        {
                            var wreg = GameStatePatcher.FindRegistration(info.WholesaleKey);
                            if (wreg == null)
                            {
                                dropped = true;
                                if (_logged.Add("dc-nowreg|" + info.WholesaleKey))
                                    Plugin.Logger.LogWarning($"{Tag} contract for '{p.AddressKey}' names wholesaler '{info.WholesaleKey}', which this machine has no registration for — that contract is NOT shown.");
                                continue;
                            }
                            target = new Entities.DeliveryContract
                            {
                                wholesaleAddress = wreg.Address, businessAddress = reg.Address,
                                items = new List<Entities.DeliveryContractItem>(),
                            };
                            list.Add(target);
                        }
                        if (target.items == null) target.items = new List<Entities.DeliveryContractItem>();
                        // Review M4: an echo answers ONE routed edit. If this contract has MOVED ON since that
                        // edit was sent — another click landed while the answer was in flight — applying the
                        // echo would overwrite the newer value, and the next scan would compare it against a
                        // baseline that already agrees and send nothing. The keystroke would vanish. Keep the
                        // local state for this contract and re-route it instead; the owner echoes again, so
                        // convergence still holds, one round later.
                        if (p.Echo && sentMap != null
                            && sentMap.TryGetValue(info.WholesaleKey + "#" + expect, out var sentDig)
                            && ContractDigestOne(target, domain) != sentDig)
                        {
                            reroute = true;
                            // The baseline for THIS contract must be the owner's wire truth, not the local
                            // object: seeding it from the local (newer) value made the re-armed scan find
                            // nothing to send, so the newer amount sat on screen looking confirmed while the
                            // owner kept the older one (fix-verification M4).
                            rerouteBase[info.WholesaleKey + "#" + expect] = ContractDigestOfInfo(info, domain, shift);
                            seen.Add(target); ordered.Add(target);
                            continue;
                        }
                        target.enabled = info.Enabled; target.isUrgentOrder = info.Urgent;
                        target.repeatingOrder = info.Repeating; target.deliveryFee = info.DeliveryFee;
                        target.nextDeliveryDay = info.NextDeliveryDay + shift;
                        // Adopt the OWNER'S ROW ORDER exactly (field 2026-08-26). Existing row objects are
                        // REUSED — the open panel's cells hold references to them — and re-seated in the order
                        // they arrived.
                        var rebuilt = new List<Entities.DeliveryContractItem>();
                        var placed = new HashSet<string>();
                        if (info.Items != null)
                            foreach (var it in info.Items)
                            {
                                if (it == null || string.IsNullOrEmpty(it.ItemName) || !placed.Add(it.ItemName)) continue;
                                if (rebuilt.Count >= MaxContractRows)
                                {
                                    dropped = true;
                                    if (_logged.Add("dc-icap|" + p.AddressKey + "|" + info.WholesaleKey))
                                        Plugin.Logger.LogWarning($"{Tag} more than {MaxContractRows} product rows received for a contract at '{p.AddressKey}' — the rest are not shown.");
                                    break;
                                }
                                var row = target.items.Find(x => x != null && x.itemName == it.ItemName)
                                          ?? new Entities.DeliveryContractItem { itemName = it.ItemName };
                                row.amount = it.Amount; row.amountOrderedThisWeek = it.OrderedThisWeek; row.amountOrderedLastWeek = it.OrderedLastWeek;
                                rebuilt.Add(row);
                            }
                        // Rows the owner does not have: KEEP the ones inside the edit domain, after the owner's
                        // — exactly where the owner's own AddMissingProducts would put them. Dropping them
                        // orphaned row objects the open panel's cells were bound to, so an amount typed into
                        // one went into a detached object and was never routed (review M3). Rows OUTSIDE the
                        // domain are the ones native itself strips, so they go.
                        foreach (var row in target.items)
                        {
                            if (row == null || string.IsNullOrEmpty(row.itemName)
                                || placed.Contains(row.itemName) || !domain.Contains(row.itemName)) continue;
                            if (rebuilt.Count >= MaxContractRows)
                            {
                                // The cap must not drop these silently: a dropped row is one the open panel's
                                // cells may still be bound to, so an amount typed into it goes nowhere — the
                                // M3 symptom, reappearing only on contracts past the cap.
                                dropped = true;
                                if (_logged.Add("dc-icap|" + p.AddressKey + "|" + info.WholesaleKey))
                                    Plugin.Logger.LogWarning($"{Tag} a contract at '{p.AddressKey}' is at the {MaxContractRows}-row cap — local-only rows are NOT shown and cannot be edited here.");
                                break;
                            }
                            rebuilt.Add(row); placed.Add(row.itemName);
                        }
                        target.items.Clear();
                        target.items.AddRange(rebuilt);
                        seen.Add(target); ordered.Add(target);
                    }
                // The contract LIST is drawn in the owner's save order, so the wire order IS that order —
                // carrying it through keeps the two screens listing the same contracts in the same places.
                list.Clear(); list.AddRange(ordered);
                merged = true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} deliveries merge for '{p.AddressKey}': {ex.Message}"); }
            if (!merged)
            {
                // A half-applied merge must not be vouched for by the sig, and the baseline has to describe
                // what this machine actually holds or the scan routes the difference as an "edit".
                _tabSig = appliedSig;
                _dcBaseline[p.AddressKey] = ContractBaselineOf(p.AddressKey, list);
                if (stockChanged) _renderDcFigures = true;
                return;
            }
            _dcSentDigest.Remove(p.AddressKey);   // reconciled with owner truth; the next scan re-stamps
            _dcListChanged |= beforeKeys != ContractKeysOf(list);   // OR: two snapshots can land before one render
            bool rowsChanged = beforeRows != ContractRowsOf(list);
            bool sigChanged = !_dcSigByAddr.TryGetValue(p.AddressKey, out var oldSig) || oldSig != (p.Sig ?? "");
            var baseline = ContractBaselineOf(p.AddressKey, list);
            foreach (var kv in rerouteBase) baseline[kv.Key] = kv.Value;   // owner truth for the contracts we did NOT apply
            _dcBaseline[p.AddressKey] = baseline;
            // Part of this snapshot may not have been applied — deliberately (a re-route) or because content
            // was dropped (a wholesaler this machine has no registration for, a cap). Either way neither sig
            // may claim it: the poll then keeps asking with the older sig, which costs one small reply every
            // five seconds while the condition lasts and is the price of never falsely reporting "up to date".
            if (reroute || dropped) _tabSig = appliedSig;
            else _dcSigByAddr[p.AddressKey] = p.Sig ?? "";
            if (reroute)
            {
                _dcLastSentSig = ""; _dcSendTries = 0; _nextScan = 0f;   // the newer local value has to reach the owner
                Plugin.Logger.LogInfo($"{Tag} an echo for '{p.AddressKey}' answered an older edit — the newer local value is being re-routed.");
            }
            else { _dcLastSentSig = ""; _dcSendTries = 0; }   // owner truth landed — any held give-up re-arms
            // Repaint only on a real change: a no-op repaint would reset the open row and the scroll position
            // under the helper's cursor for nothing. A changed ROW SET counts — the cells are bound to those
            // row objects (review M3).
            // Structural changes rebuild the panel; a change to the carried FIGURES alone only re-binds the
            // rows, so an expanded row stays expanded and shows its new number (review MAJOR-1).
            if (p.Echo || sigChanged || rowsChanged || _dcListChanged) _renderDeliveries = true;
            else if (stockChanged) _renderDcFigures = true;
        }

        /// <summary>Every carried contract's row NAMES in order — the test for "did the row set or its order
        /// move", which the repaint must honour because the open panel's cells are bound to those row objects.</summary>
        private static string ContractRowsOf(List<Entities.DeliveryContract> list)
        {
            var sb = new System.Text.StringBuilder(128);
            foreach (var c in list)
            {
                if (c?.items == null) continue;
                sb.Append('|');
                foreach (var it in c.items) if (it != null) sb.Append(it.itemName).Append(',');
            }
            return sb.Length.ToString() + ":" + sb.ToString().GetHashCode().ToString("X8");
        }

        /// <summary>The carried contracts' identity keys in list order — the cheap test for "did the SET or the
        /// ORDER change", which decides whether the tab must be rebuilt or just the open panel repainted.</summary>
        private static string ContractKeysOf(List<Entities.DeliveryContract> list)
        {
            var ords = new Dictionary<string, int>();
            var sb = new System.Text.StringBuilder(64);
            foreach (var c in list)
            {
                if (c == null) continue;
                string wk = WholesaleKeyOf(c); if (wk.Length == 0) continue;
                ords.TryGetValue(wk, out int o); ords[wk] = o + 1;
                sb.Append('|').Append(wk).Append('#').Append(o);
            }
            return sb.ToString();
        }

        private static string StockSigOf(List<StockInfo>? rows)
        {
            if (rows == null || rows.Count == 0) return "";
            var sb = new System.Text.StringBuilder(rows.Count * 12);
            foreach (var r in rows) if (r != null) sb.Append(r.ItemName).Append('=').Append(r.Count).Append(',');
            return sb.Length.ToString() + ":" + sb.ToString().GetHashCode().ToString("X8");
        }

        /// <summary>Does this machine hold contract changes the owner has not answered? The baseline is owner
        /// truth as of the last applied snapshot, digested through the SAME domain filter the edit scan uses, so
        /// native render housekeeping never counts as a pending edit.</summary>
        private static bool HasPendingContractEdits(string addr, List<Entities.DeliveryContract> list)
        {
            if (!_dcBaseline.TryGetValue(addr, out var baseline)) return false;   // no owner truth yet — nothing to diverge from
            var now = ContractBaselineOf(addr, list);
            if (now.Count != baseline.Count) return true;
            foreach (var kv in now)
                if (!baseline.TryGetValue(kv.Key, out var b) || b != kv.Value) return true;
            return false;
        }

        /// <summary>OWNER: apply a routed contract edit with the native gates; false = echo reverts.</summary>
        private static bool ApplyContractOp(BuildingRegistration reg, SharedWorkEditPayload p)
        {
            var info = p.Contract;
            var gi = SaveGameManager.Current;
            if (info == null || string.IsNullOrEmpty(info.WholesaleKey) || gi == null) return false;
            var wreg = GameStatePatcher.FindRegistration(info.WholesaleKey);
            if (wreg == null) return false;
            var mine = new List<Entities.DeliveryContract>();
            foreach (var c in gi.DeliveryContracts)
                if (c != null && c.businessAddress == reg.Address && c.wholesaleAddress == wreg.Address) mine.Add(c);
            if (info.Ordinal < 0 || info.Ordinal >= mine.Count) return false;
            var target = mine[info.Ordinal];
            if (p.Op == "endcontract")
            {
                if (!Entities.DeliveryHelper.CanModifyContract(target.nextDeliveryDay)) return false;
                target.Remove();   // OUR Remove patch only intercepts CARRIED instances — the owner's real one removes natively
                return true;
            }
            bool ok = true;
            if (info.Enabled != target.enabled)
            {
                if (info.Enabled)
                {
                    target.enabled = true;
                    target.UpdateNextDeliveryDay();
                    GameEvent.Invoke("ba:gameevent_updateddeliverycontract");   // the native StartOrder fires this
                }
                else if (Entities.DeliveryHelper.CanModifyContract(target.nextDeliveryDay)) { target.enabled = false; target.isUrgentOrder = false; }
                else ok = false;
            }
            if (target.enabled && info.Urgent && !target.isUrgentOrder)
            {
                // native MakeUrgentOrder: refuse when a delivery is already due tomorrow
                if (target.nextDeliveryDay != gi.Day + 1) { target.nextDeliveryDay = gi.Day + 1; target.isUrgentOrder = true; }
                else ok = false;
            }
            target.repeatingOrder = info.Repeating;
            if (info.Items != null && target.items != null)
            {
                foreach (var it in info.Items)
                {
                    if (it == null || string.IsNullOrEmpty(it.ItemName)) continue;
                    var row = target.items.Find(x => x != null && x.itemName == it.ItemName);
                    if (row == null)
                    {
                        bool sells = false;
                        try { sells = wreg.GetListOfItemsForSale().Contains(it.ItemName); } catch { }
                        if (!sells) { ok = false; continue; }   // only items the wholesaler sells may be added
                        row = new Entities.DeliveryContractItem { itemName = it.ItemName, amount = 0 };
                        target.items.Add(row);
                    }
                    int amt = Math.Max(0, it.Amount);
                    try
                    {
                        if (!Entities.DeliveryHelper.AreWholesaleAndImportLimitsDisabled())
                        {
                            int cap = Math.Max(0, row.ItemCached.maxWholesaleOrderAmount - row.amountOrderedThisWeek);
                            if (amt > cap) { amt = cap; ok = false; }   // clamp = the native input's weekly ceiling
                        }
                    }
                    catch { }
                    row.amount = amt;
                }
                // Review MAJOR-1's owner half: only rows INSIDE the edit domain may be zeroed on
                // absence — rows outside it never travel from the helper (its render removes them),
                // so their absence says nothing about a user's intent.
                var ownerDomain = ContractDomain(reg);
                foreach (var row in target.items)
                    if (row != null && row.amount > 0 && ownerDomain.Contains(row.itemName)
                        && !info.Items.Exists(x => x != null && x.ItemName == row.itemName))
                        row.amount = 0;   // the helper zeroed it (amount-0 rows never travel)
            }
            return ok;
        }

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
                    // A stock ask still waiting on its reply asks with NO sig, so the owner cannot answer with
                    // silence — that makes this poll the retry, rather than leaving a one-shot flag stranded.
                    RequestInfo(_openAddr, _openTab, _dcForceApply && _openTab == "deliveries" ? "" : _tabSig);
            }
            if (_nextCardPoll <= 0f) _nextCardPoll = Time.unscaledTime + PollSeconds / 2f;   // half-phase offset (review #4)
            if (Time.unscaledTime >= _nextCardPoll)
            {
                _nextCardPoll = Time.unscaledTime + PollSeconds;
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
                        if (reg != null && fac.machineList != null) { fac.machineList.SetUp(reg); RebuildOrderBaseline(reg); }
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} factory render: {ex.Message}"); }
                }
            }
            if (_renderInsight)
            {
                var ins = page.GetComponentInChildren<BizManInsight>(true);
                if (ins != null && ins.gameObject.activeInHierarchy)
                {
                    _renderInsight = false;
                    try
                    {
                        var reg = GameStatePatcher.FindRegistration(_openAddr);
                        if (reg != null) ins.RefreshData(reg);   // re-enters our prefix; same tab → no re-request
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} insight render: {ex.Message}"); }
                }
            }
            if (_renderDeliveries)
            {
                var del = page.GetComponentInChildren<BizManDeliveries>(true);
                if (del != null && del.gameObject.activeInHierarchy)
                {
                    _renderDeliveries = false; _renderDcFigures = false;   // the full path covers the figures too
                    bool listChanged = _dcListChanged; _dcListChanged = false;
                    // A full rebuild re-enters our prefix (same tab → no re-request; the window inserts the
                    // carried rows); the targeted path re-renders only the open contract.
                    RepaintDeliveries(del, listChanged, _dcByAddr.TryGetValue(_openAddr, out var lcarried) ? lcarried : null);
                }
            }
            if (_renderDcFigures)
            {
                _renderDcFigures = false;
                RepaintDeliveryFigures(page);
            }
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + ScanSeconds;
                try
                {
                    if (_openTab == "drivers") ScanDriverEdits();
                    else if (_openTab == "factory") ScanFactoryEdits();
                    else if (_openTab == "deliveries") ScanContractEdits();
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
            if (GrantSync.SharedManageCount == 0) return;   // review #11: no sweep when nothing is shared (single-player incl.)
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
                        string name = info.VehicleType;
                        try { var loc = VehicleStoragePanel.Localize(info.VehicleType); if (!string.IsNullOrEmpty(loc)) name = loc; } catch { }
                        // Review #14: also write through the Localizor component (Prefix + empty Key — the
                        // BuildingResume precedent), so a language change re-renders the same name instead
                        // of reverting the label to "Unassigned".
                        try
                        {
                            foreach (var comp in nameTf.GetComponents<Component>())
                                if (comp != null && comp.GetType().Name == "TextLocalizationComponent")
                                { HousingMapCues.SetMember(comp, "Prefix", name); HousingMapCues.SetMember(comp, "Key", ""); break; }
                        }
                        catch { }
                        var tmp = nameTf.GetComponentInChildren<TMP_Text>(true);
                        if (tmp == null) continue;
                        tmp.text = name;
                        try { tmp.color = InstanceBehavior<GlobalReferences>.Instance.colors.midnight; } catch { }
                    }
                }
                catch (Exception ex) { if (_logged.Add("cardveh")) Plugin.Logger.LogWarning($"{Tag} card vehicle names: {ex.Message}"); }
            }
        }

        /// <summary>OWNER: after applying a routed edit, repaint OUR OWN open screens for that building -
        /// data alone never repaints a native tab (ruling 32; the bug class that lagged slice 4's owner).</summary>
        private static void RefreshOwnerOpenSurfaces(string addressKey, string tab, bool listChanged = false)
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
                    else if (tab == "deliveries")
                    {
                        // 7b ruling 32: the OWNER's open Deliveries tab repaints after a helper's edit — without
                        // being thrown back to contract #1 (field 2026-08-26). An END CONTRACT must still force
                        // the full rebuild: only that rebuilds the LIST, and the owner may well have a different
                        // contract open, in which case the ended one would keep its row — with a live button
                        // bound to a contract no longer in the save, so clicks on it would write nowhere
                        // (review M2).
                        var del = page.GetComponentInChildren<BizManDeliveries>(true);
                        if (del != null && del.gameObject.activeInHierarchy)
                            RepaintDeliveries(del, listChanged, SaveGameManager.Current?.DeliveryContracts);
                    }
                }
                if (tab == "drivers" && _whList != null && _whList.gameObject.activeInHierarchy)
                    _whList.Load();   // the owner's list card shows the same slots
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} owner repaint: {ex.Message}"); }
        }

        /// <summary>Review #5 aggravator: switching to Schedule/Presentation on the same building has no
        /// work-tab hook, so the session (and its 2 s scan) stayed alive with no work tab on screen.</summary>
        [HarmonyPatch(typeof(BizManBusiness), "SetTab")]
        public static class Patch_BizManBusiness_SetTab_CloseWork
        {
            static void Postfix(string tabName)
            {
                try
                {
                    if (_openAddr.Length == 0) return;
                    // 7a review: EVERY session-opening tab MUST be in this keep-list — their RefreshData
                    // prefixes open the session DURING the native SetTab body, and this postfix would
                    // close it instantly (the 7a blocker).
                    if (tabName == "Inventory" || tabName == "Drivers" || tabName == "Factory" || tabName == "Insight" || tabName == "Deliveries") return;
                    CloseSession();
                }
                catch { }
            }
        }

        /// <summary>Review #2: the native list REWRITES priority = index on every draw, and a never-opened
        /// factory arrives with every priority 0 — a baseline taken from the snapshot then differs from what
        /// the draw leaves behind, and the scan would route a reorder nobody made. The order baseline is
        /// therefore always rebuilt from the REPLICA after our own deferred draw.</summary>
        private static void RebuildOrderBaseline(BuildingRegistration reg)
        {
            try
            {
                _orderBaseline.Clear();
                var order = new Dictionary<string, List<(int prio, string id)>>();
                foreach (var kv in _wsBaseline)
                {
                    FactoryWorkstationInstance fw = null;
                    try { if (reg.itemInstances.TryGetValue(kv.Key, out var ii)) fw = ii as FactoryWorkstationInstance; } catch { }
                    if (fw == null) continue;
                    string type = fw.workstationType ?? "";
                    if (!order.TryGetValue(type, out var lst)) order[type] = lst = new List<(int, string)>();
                    lst.Add((fw.priority, kv.Key));
                }
                foreach (var kv in order)
                {
                    kv.Value.Sort((a, b) => a.prio != b.prio ? a.prio.CompareTo(b.prio) : string.CompareOrdinal(a.id, b.id));
                    _orderBaseline[kv.Key] = string.Join(",", kv.Value.ConvertAll(x => x.id));
                }
            }
            catch { }
        }

        /// <summary>GameStatePatcher calls this when an interior snapshot writes workstation config onto a
        /// replica (review #5): the scan must never mistake that external write for a user edit — a stale
        /// in-flight snapshot would otherwise be routed back and silently revert the owner's config.</summary>
        public static void OnExternalWorkstationWrite(FactoryWorkstationInstance fw)
        {
            try
            {
                if (fw == null || _openTab != "factory" || _openAddr.Length == 0) return;
                string id = fw.id ?? "";
                if (!_wsBaseline.ContainsKey(id)) return;
                _wsBaseline[id] = (fw.selectedRecipeId ?? "", fw.produceUpTo, fw.produceUpToValue, fw.alias ?? "");
                _orderBaselineStale = true;   // the write may have moved a priority too
            }
            catch { }
        }

        /// <summary>GrantSync calls this when the shared-manage set changes (review #10): card overrides and
        /// the replica slot contents we wrote must not outlive a revoked grant.</summary>
        public static void OnSharedManageChanged(HashSet<string> changed)
        {
            if (changed == null || changed.Count == 0) return;
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try
                {
                    foreach (var addr in changed)
                    {
                        if (string.IsNullOrEmpty(addr) || GrantSync.IsSharedManage(addr)) continue;   // still shared — keep
                        _cardSlots.Remove(addr); _cardInv.Remove(addr); _cardSig.Remove(addr); _cardNext.Remove(addr);
                        // Carried owner data must not outlive the grant that justified it — the tab caches and
                        // the stock table go with the card figures (review MINOR).
                        _dcByAddr.Remove(addr); _dcSigByAddr.Remove(addr); _dcStockSig.Remove(addr);
                        _dcBaseline.Remove(addr); _dcSentDigest.Remove(addr);
                        _insCapByAddr.Remove(addr); _insSigByAddr.Remove(addr); _insScalarsByAddr.Remove(addr);
                        SharedShopStock.Clear(addr);
                        try
                        {
                            if (GameStatePatcher.FindRegistration(addr) is Entities.Warehouse wh && wh.vehicleSlots != null)
                                foreach (var sl in wh.vehicleSlots) if (sl != null) sl.employeeDriverId = "";
                        }
                        catch { }
                        if (_openAddr == addr) CloseSession();
                        _renderCards = true;   // redraw the list without the dropped figures
                    }
                }
                catch { }
            });
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
        private static void BuildAndSendSnapshot(string addressKey, string tab, string toPid, string requesterSig = "", bool echo = false)
        {
            var reg = GameStatePatcher.FindRegistration(addressKey);
            if (reg == null || !MergerFlip.TrulyMine(reg)) return;
            // 7a/7b: Insight + Deliveries serve ORDINARY shops — only the warehouse-backed tabs need the cast.
            Entities.Warehouse? wh = reg as Entities.Warehouse;
            if (tab != "insight" && tab != "deliveries" && wh == null)
            {
                if (_logged.Add("work-notwh|" + addressKey))
                    Plugin.Logger.LogWarning($"{Tag} work-info request for '{addressKey}' but its registration is not a warehouse/factory — ignored.");
                return;
            }
            var reply = new SharedWorkInfoPayload
            {
                PlayerId = MPConfig.PlayerId, Action = "snapshot", Tab = tab,
                AddressKey = addressKey, ToPid = toPid,
                OwnerDay = SaveGameManager.Current?.Day ?? -1,   // day rebasing on the receiver (review MAJOR-1)
            };
            try
            {
                if (tab == "insight") BuildInsight(reg, reply);
                else if (tab == "deliveries") BuildDeliveries(reg, reply);
                else if (tab == "inventory") BuildInventory(wh!, reply);   // wh non-null past the guard above
                else if (tab == "drivers") BuildDrivers(wh!, reply);
                else if (tab == "factory") BuildFactory(wh!, reply);
                else if (tab == "card") BuildCard(wh!, reply);
                else return;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} work-info build ({tab}): {ex.Message}"); }
            reply.Echo = echo;
            reply.Sig = SigOf(reply);
            if (requesterSig.Length > 0 && requesterSig == reply.Sig) return;   // unchanged - the poll stays silent
            if (requesterSig.Length == 0)
                Plugin.Logger.LogInfo($"{Tag} sending '{toPid}' the {tab} figures of '{addressKey}': " +
                    (tab == "inventory" ? $"{reply.Products.Count} product(s), boxes {reply.BoxesCurrent}/{reply.BoxesMax}."
                    : tab == "drivers"  ? $"{reply.Slots.Count} slot(s)."
                    : tab == "card"     ? $"{reply.Slots.Count} slot(s), {reply.Products.Count} inventory row(s)."
                    : tab == "insight"  ? $"{reply.InsightDays.Count} day(s), {reply.Capacity.Count} capacity row(s)."
                    : tab == "deliveries" ? $"{reply.Contracts.Count} contract(s), {reply.Products.Count} product(s)."
                    :                     $"{reply.Stations.Count} workstation(s), {reply.ResourceStock.Count} resource count(s)."));
            if (MPServer.IsRunning) MPServer.HostRouteSharedWorkInfo(reply, MPConfig.PlayerId);
            else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.SharedWorkInfo, MPConfig.PlayerId, reply));
        }

        /// <summary>Owner-computed content signature - both compares happen on the owner's machine, so any
        /// deterministic digest works. Unchanged content means the poll reply is silence and no re-render.</summary>
        private static string SigOf(SharedWorkInfoPayload r)
        {
            var sb = new System.Text.StringBuilder(256);
            sb.Append(r.Tab).Append('@').Append(r.AddressKey).Append('#').Append(r.BoxesMax).Append('/').Append(r.BoxesCurrent);
            if (r.Products != null) foreach (var x in r.Products) if (x != null) sb.Append('|').Append(x.ItemName).Append(':').Append(x.Stock).Append(':').Append(x.Deliveries).Append(':').Append(x.Consumption).Append(':').Append(x.DaysLeft);
            if (r.Slots != null) foreach (var x in r.Slots) if (x != null) sb.Append('|').Append(x.Index).Append(':').Append(x.VehicleId).Append(':').Append(x.VehicleType).Append(':').Append(x.RequiredSkill).Append(':').Append(x.DriverId);
            if (r.Stations != null) foreach (var x in r.Stations) if (x != null) { sb.Append('|').Append(x.Id).Append(':').Append(x.RecipeId).Append(':').Append(x.Priority).Append(':').Append(x.ProduceUpTo).Append(':').Append(x.UpToValue).Append(':').Append(x.Alias).Append(':').Append(x.Active); if (x.Reasons != null) foreach (var rr in x.Reasons) sb.Append('~').Append(rr); }
            if (r.ResourceStock != null) foreach (var x in r.ResourceStock) if (x != null) sb.Append('|').Append(x.ItemName).Append(':').Append(x.Count);
            if (r.Insight != null) sb.Append('|').Append(r.Insight.PromoTotal).Append(':').Append(r.Insight.PromoTraffic).Append(':').Append(r.Insight.PromoMarketing)
                .Append(':').Append(r.Insight.SatOverall).Append(':').Append(r.Insight.SatService).Append(':').Append(r.Insight.SatPricing)
                .Append(':').Append(r.Insight.SatInterior).Append(':').Append(r.Insight.SatClean);
            if (r.InsightDays != null) foreach (var d in r.InsightDays) if (d != null) { sb.Append('|').Append(d.Day).Append(':').Append(d.Customers); if (d.Hours != null) foreach (var h in d.Hours) sb.Append(',').Append(h); }
            if (r.Capacity != null) foreach (var c in r.Capacity) if (c != null) { sb.Append('|').Append(c.ItemName); if (c.Shelves != null) foreach (var s in c.Shelves) if (s != null) sb.Append(':').Append(s.Name).Append('~').Append(s.Amount).Append('~').Append(s.PerHour); }
            if (r.Contracts != null) foreach (var c in r.Contracts) if (c != null) { sb.Append('|').Append(c.WholesaleKey).Append('#').Append(c.Ordinal).Append(':').Append(c.Enabled ? 1 : 0).Append(c.Urgent ? 1 : 0).Append(c.Repeating ? 1 : 0).Append(':').Append(c.NextDeliveryDay).Append(':').Append(c.DeliveryFee.ToString("F0")); if (c.Items != null) foreach (var it in c.Items) if (it != null) sb.Append(',').Append(it.ItemName).Append('=').Append(it.Amount).Append('~').Append(it.OrderedThisWeek); }
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
                _cardSlots[p.AddressKey] = p.Slots != null ? new List<DriverSlotInfo>(p.Slots) : new List<DriverSlotInfo>();
                _cardInv[p.AddressKey]   = p.Products != null ? new List<WorkProductInfo>(p.Products) : new List<WorkProductInfo>();
                bool unresolved = ApplySlotsToReplica(reg, p.Slots);   // driver names resolve natively once the slots are real
                if (!unresolved) _cardSig[p.AddressKey] = p.Sig ?? "";
                else _cardSig.Remove(p.AddressKey);   // review #3: retry next round until the roster lands
                _renderCards = true;
                return;
            }
            if (_openAddr.Length == 0 || p.AddressKey != _openAddr) return;   // stale reply for a tab no longer open
            // Unchanged content never disturbs the open screen — EXCEPT an edit echo: a REJECTED edit
            // leaves the owner's content (and so its sig) unchanged, and the gate would swallow the
            // revert with it (review BLOCKER). Echoes always apply.
            // 7b-2: stock figures ride OUTSIDE the sig (they change as customers buy, and streaming them would
            // make the helper's screen livelier than the owner's own — which freezes at panel open). So a stock
            // refresh asked for on contract selection carries an unchanged sig, and this gate would swallow it:
            // that one reply is flagged through.
            // The flag is cleared where the reply LANDS (in ApplyDeliveriesSnapshot), never here: clearing it
            // on a gate that dropped the message is the one-shot-eaten-by-a-gate failure, and while it stays
            // set the ordinary 5 s poll asks with no sig, so the refresh is recurrence-covered.
            bool forced = _dcForceApply && p.Tab == "deliveries" && p.Tab == _openTab;
            if (!p.Echo && !forced && p.Tab == _openTab && _tabSig.Length > 0 && p.Sig == _tabSig) return;
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
            else if (p.Tab == "insight" && p.Tab == _openTab) ApplyInsightSnapshot(p, reg);   // review MINOR-10: never while another tab is up
            else if (p.Tab == "deliveries" && p.Tab == _openTab) ApplyDeliveriesSnapshot(p, reg);
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
                if (reg == null || !MergerFlip.TrulyMine(reg)) return;
                bool applied; string echoTab;
                if (p.Op == "contract" || p.Op == "endcontract")
                {
                    // 7b: contract ops serve ORDINARY shops — no warehouse cast.
                    echoTab = "deliveries";
                    applied = ApplyContractOp(reg, p);
                }
                else if (reg is Entities.Warehouse wh)
                {
                    echoTab = p.Op == "driver" ? "drivers" : "factory";
                    applied = p.Op switch
                    {
                        "driver"  => ApplyDriverOp(wh, p),
                        "recipe"  => ApplyRecipeOp(wh, p),
                        "produce" => ApplyProduceOp(wh, p),
                        "order"   => ApplyOrderOp(wh, p),
                        "alias"   => ApplyAliasOp(wh, p),
                        _ => false,
                    };
                }
                else return;
                if (!applied) Plugin.Logger.LogInfo($"{Tag} work edit '{p.Op}' on '{p.AddressKey}' from '{p.PlayerId}' NOT applied — echoing truth back.");
                BuildAndSendSnapshot(p.AddressKey, echoTab, p.PlayerId, "", echo: true);   // echo either way: apply confirms, reject REVERTS (Echo bypasses the helper's sig gate — review blocker)
                // ruling 32: data alone never repaints a native tab. "endcontract" is the ONLY routed op that
                // changes the owner's contract LIST (ApplyContractOp creates item rows, never contracts).
                // The deliveries branch repaints even on a REFUSED op: ApplyContractOp is the first op that can
                // write some of an edit and still report failure (a row clamped to the weekly limit, an item
                // the wholesaler does not sell), and gating the repaint on success left the owner's open tab
                // showing pre-edit numbers over a save that already held the new ones (fix-verification MINOR).
                if (applied || echoTab == "deliveries")
                    RefreshOwnerOpenSurfaces(p.AddressKey, echoTab, p.Op == "endcontract" && applied);
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
            int amount = Mathf.Clamp(p.IntValue, 0, 1000000);
            int applied = 0;
            // Review #12: one op may carry a whole product GROUP (csv of station ids) — see the scan side.
            foreach (var id in (p.StationId ?? "").Split(','))
            {
                var fw = StationOf(wh, id);
                if (fw == null) continue;
                fw.produceUpTo = p.BoolValue;
                fw.produceUpToValue = amount;
                applied++;
            }
            if (applied > 0) Plugin.Logger.LogInfo($"{Tag} '{p.PlayerId}' set produce-up-to on {applied} workstation(s) at '{p.AddressKey}': {(p.BoolValue ? amount.ToString() : "continuous")}.");
            return applied > 0;
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
