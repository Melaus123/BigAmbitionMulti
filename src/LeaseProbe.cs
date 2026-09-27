// PROBE-START: P-LEASEPROBE
using System.Reflection;
using System.Text;
using Buildings;
using Entities;
using HarmonyLib;
using Helpers;

namespace BigAmbitionsMP
{
    /// <summary>P-LEASEPROBE (pre-approved 2026-09-26, H-MERGEROWNFLIP-1 part A) - a LOG-ONLY probe. It CHANGES
    /// NOTHING: it reads the flip table, the rental ledger and the call stack and prints them.
    ///
    /// QUESTION: in bundle 20260923-222227 a member's lease was refunded three times ($18,180 each) with none of
    /// the mod's three patches on BizManPresentation.OnTerminateContractConfirm logging a word - which code path
    /// actually ends a lease, and is another mod's copy of the terminate logic involved?
    ///
    /// WHAT IT LOGS (every line tagged [LeaseProbe], each kind on its own per-world budget):
    ///   - enter / exit / throw around BizManPresentation.TerminateContract and OnTerminateContractConfirm, patched
    ///     FIRST (prefix) and LAST (postfix, finalizer): address, flag, flipped, TrulyMine, ledger tenant;
    ///   - once per loaded world, when it settles: the Harmony patch owners on those two methods, both
    ///     HudConfirm.Show overloads, BizManPresentation.RentBuilding and BuildingHelper.RentBuilding;
    ///   - every deposit-return money event (ba:transaction_depositreturn / ba:transaction_depositreturnfurniture,
    ///     observed on GameManager.ChangeMoney - the choke point the wallet forward and CompanyFeed read): the
    ///     address and the top 12 stack frames (method, type, assembly), which names the path directly;
    ///   - a WARNING when a flip ON hits a key the ledger names MINE (the defect's signature; the flip ON line
    ///     itself now carries the ledger tenant);
    ///   - once per loaded world, BEFORE its first flip: every company building's flag, business name and tenant.
    /// </summary>
    internal static class LeaseProbe
    {
        private const int EnterExitBudget = 40, StackBudget = 8, DumpBudget = 2, FlipBudget = 20, LoadBudget = 40;
        private static int _enterExit, _stacks, _dumps, _flipLines, _loadLines;
        private static object? _world;          // the save object the budgets belong to
        private static object? _dumpedFor;      // world whose patch owners were dumped
        private static object? _loadDumpedFor;  // world whose company buildings were dumped

        /// <summary>A new save object = a new world: every budget starts again.</summary>
        private static void NoteWorld()
        {
            var cur = SaveGameManager.Current;
            if (cur == null || ReferenceEquals(cur, _world)) return;
            _world = cur;
            _enterExit = _stacks = _dumps = _flipLines = _loadLines = 0;
        }

        private static string Describe(BuildingRegistration? reg)
        {
            if (reg == null) return "reg=null";
            string key = "?"; try { key = GameStateReader.AddressKey(reg); } catch { }
            bool rented = false; try { rented = reg.RentedByPlayer; } catch { }
            bool known = MergerFlip.TryLedgerOwner(key, out var owner);
            bool other = false; try { other = GrantSync.IsOtherOwned(key); } catch { }
            return $"'{key}' rented={rented} flipped={MergerFlip.IsFlipped(key)} truly={MergerFlip.TrulyMine(reg)} "
                 + $"ledger='{(known ? owner : "?")}' mine={MergerFlip.LedgerSaysMine(key)} otherOwned={other} role={(MPServer.IsRunning ? "host" : "client")}";
        }

        private static BuildingRegistration? RegOf(object? pres)
        {
            try
            {
                if (pres == null) return null;
                object? biz = AccessTools.Field(typeof(BizManPresentation), "bizManBusiness")?.GetValue(pres);
                if (ReferenceEquals(biz, null)) return null;
                return AccessTools.Field(biz.GetType(), "buildingRegistration")?.GetValue(biz) as BuildingRegistration;
            }
            catch { return null; }
        }

        internal static void Line(string what, object? pres)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                NoteWorld();
                if (_enterExit >= EnterExitBudget) return;
                _enterExit++;
                Plugin.Logger.LogInfo($"[LeaseProbe] {what}: {Describe(RegOf(pres))}{(_enterExit == EnterExitBudget ? " (enter/exit budget reached)" : "")}");
            }
            catch { }
        }

        /// <summary>1 Hz from MergerFlip.Tick: once per loaded world, when it has settled, the patch-owner dump.</summary>
        internal static void Tick()
        {
            if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
            var cur = SaveGameManager.Current;
            if (cur == null || ReferenceEquals(cur, _dumpedFor)) return;
            if (!MPWorldReady.IsSettled) return;
            NoteWorld();
            _dumpedFor = cur;
            if (_dumps >= DumpBudget) return;
            _dumps++;
            var targets = new List<KeyValuePair<string, MethodBase?>>
            {
                new("BizManPresentation.TerminateContract",          AccessTools.Method(typeof(BizManPresentation), "TerminateContract")),
                new("BizManPresentation.OnTerminateContractConfirm", AccessTools.Method(typeof(BizManPresentation), "OnTerminateContractConfirm")),
                new("BizManPresentation.RentBuilding",               AccessTools.Method(typeof(BizManPresentation), "RentBuilding")),
                new("BuildingHelper.RentBuilding",                   AccessTools.Method(typeof(BuildingHelper), "RentBuilding")),
            };
            foreach (var m in typeof(HudConfirm).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "Show") continue;
                var ps = m.GetParameters();
                targets.Add(new("HudConfirm.Show(" + (ps.Length > 0 ? ps[0].ParameterType.Name : "") + ",...)", m));
            }
            foreach (var t in targets)
            {
                try
                {
                    if (t.Value == null) { Plugin.Logger.LogInfo($"[LeaseProbe] patches on {t.Key}: METHOD NOT FOUND"); continue; }
                    var info = Harmony.GetPatchInfo(t.Value);
                    if (info == null) { Plugin.Logger.LogInfo($"[LeaseProbe] patches on {t.Key}: none"); continue; }
                    var sb = new StringBuilder();
                    void Part(string label, IEnumerable<Patch> list)
                    {
                        sb.Append(' ').Append(label).Append('[');
                        int n = 0;
                        foreach (var p in list)
                        {
                            if (n++ > 0) sb.Append(", ");
                            var pm = p.PatchMethod;
                            sb.Append(p.owner).Append(':').Append(pm?.DeclaringType?.FullName ?? "?").Append('.').Append(pm?.Name ?? "?")
                              .Append(" p").Append(p.priority).Append(" {").Append(pm?.DeclaringType?.Assembly.GetName().Name ?? "?").Append('}');
                        }
                        sb.Append(']');
                    }
                    Part("prefix", info.Prefixes); Part("postfix", info.Postfixes); Part("transpiler", info.Transpilers); Part("finalizer", info.Finalizers);
                    Plugin.Logger.LogInfo($"[LeaseProbe] patches on {t.Key}:{sb}");
                }
                catch (System.Exception ex) { Plugin.Logger.LogInfo($"[LeaseProbe] patches on {t.Key}: read failed ({ex.Message})"); }
            }
        }

        /// <summary>MergerFlip.Tick, before its flip loop: once per loaded world, every company building as it stands.</summary>
        internal static void LoadDumpOnce()
        {
            var cur = SaveGameManager.Current;
            if (cur == null || ReferenceEquals(cur, _loadDumpedFor)) return;
            var keys = MergerSync.MyGroupBuildingKeys;
            if (keys == null || keys.Count == 0) return;   // no company known yet - wait for the first merger state
            NoteWorld();
            _loadDumpedFor = cur;
            Plugin.Logger.LogInfo($"[LeaseProbe] load: {keys.Count} company building(s) before the first flip of this world (role={(MPServer.IsRunning ? "host" : "client")}).");
            foreach (var k in keys)
            {
                if (_loadLines >= LoadBudget) { Plugin.Logger.LogInfo("[LeaseProbe] load: line budget reached."); break; }
                _loadLines++;
                try
                {
                    var reg = GameStatePatcher.FindRegistration(k);
                    string name = ""; try { name = reg?.BusinessName?.ToString() ?? ""; } catch { }
                    Plugin.Logger.LogInfo($"[LeaseProbe] load: {Describe(reg)} name='{name}'");
                }
                catch { }
            }
        }

        /// <summary>MergerFlip.Tick, right after a flip ON: the defect's signature as a WARNING.</summary>
        internal static void OnFlipOn(string key)
        {
            if (!MergerFlip.LedgerSaysMine(key)) return;
            NoteWorld();
            if (_flipLines >= FlipBudget) return;
            _flipLines++;
            Plugin.Logger.LogWarning($"[LeaseProbe] flip ON of a key the ledger names MINE: '{key}'{(_flipLines == FlipBudget ? " (budget reached)" : "")}");
        }

        /// <summary>GameManager.ChangeMoney postfix: every deposit-return money event with the call stack that raised it.</summary>
        internal static void OnMoney(float amount, TransactionInfo? info, Address? address)
        {
            try
            {
                string type = info?.Type ?? "";
                if (type != "ba:transaction_depositreturn" && type != "ba:transaction_depositreturnfurniture") return;
                NoteWorld();
                if (_stacks >= StackBudget) return;
                _stacks++;
                string key = "?"; try { if (address != null) key = GameStateReader.AddressKey(address); } catch { }
                BuildingRegistration? reg = null; try { if (key != "?") reg = GameStatePatcher.FindRegistration(key); } catch { }
                var sb = new StringBuilder();
                var st = new System.Diagnostics.StackTrace(1, false);
                int n = 0;
                for (int i = 0; i < st.FrameCount && n < 12; i++)
                {
                    var m = st.GetFrame(i)?.GetMethod();
                    if (m == null) continue;
                    if (n++ > 0) sb.Append(" <- ");
                    var dt = m.DeclaringType;
                    string asm = "?"; try { asm = dt?.Assembly.GetName().Name ?? "(dynamic)"; } catch { }
                    sb.Append(dt?.FullName ?? "(no type)").Append("::").Append(m.Name).Append(" {").Append(asm).Append('}');
                }
                Plugin.Logger.LogInfo($"[LeaseProbe] deposit-return {type} {amount:F2} at {Describe(reg)} stack: {sb}{(_stacks == StackBudget ? " (stack budget reached)" : "")}");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(BizManPresentation), "TerminateContract")]
    internal static class LeaseProbe_TerminateContract
    {
        [HarmonyPriority(Priority.First)]
        static void Prefix(BizManPresentation __instance) => LeaseProbe.Line("enter TerminateContract", __instance);
        [HarmonyPriority(Priority.Last)]
        static void Postfix(BizManPresentation __instance) => LeaseProbe.Line("exit TerminateContract", __instance);
        [HarmonyPriority(Priority.Last)]
        static System.Exception? Finalizer(BizManPresentation __instance, System.Exception? __exception)
        {
            if (__exception != null) LeaseProbe.Line($"throw TerminateContract ({__exception.GetType().Name}: {__exception.Message})", __instance);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(BizManPresentation), "OnTerminateContractConfirm")]
    internal static class LeaseProbe_TerminateConfirm
    {
        [HarmonyPriority(Priority.First)]
        static void Prefix(BizManPresentation __instance) => LeaseProbe.Line("enter OnTerminateContractConfirm", __instance);
        [HarmonyPriority(Priority.Last)]
        static void Postfix(BizManPresentation __instance) => LeaseProbe.Line("exit OnTerminateContractConfirm", __instance);
        [HarmonyPriority(Priority.Last)]
        static System.Exception? Finalizer(BizManPresentation __instance, System.Exception? __exception)
        {
            if (__exception != null) LeaseProbe.Line($"throw OnTerminateContractConfirm ({__exception.GetType().Name}: {__exception.Message})", __instance);
            return __exception;
        }
    }

    [HarmonyPatch]
    internal static class LeaseProbe_ChangeMoney
    {
        static MethodBase? TargetMethod() => AccessTools.Method(typeof(GameManager), "ChangeMoney");
        static void Postfix(float amount, TransactionInfo transactionInfo, Address address) => LeaseProbe.OnMoney(amount, transactionInfo, address);
    }
}
// PROBE-END: P-LEASEPROBE
