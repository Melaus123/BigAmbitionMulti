using System;
using HarmonyLib;
using Buildings;   // BuildingRegistration
using Helpers;     // BusinessTypeHelper

namespace BigAmbitionsMP
{
    // PROBE-START: P-STARTBIZ-TYPE (this whole file is the probe — delete the file to remove it)
    /// <summary>Bundle 20260903-180410 (client 'Sceazy'): five NullReferenceExceptions at the FIRST line of
    /// StartBusinessUI.SetUpBusiness — `BusinessTypeHelper.GetData(_selectedType).courseRequired` — i.e. the business type
    /// the panel was holding had NO entry in the game's type table at click time, although every card it listed resolved
    /// at load (AddType would have thrown otherwise) and the "empty" sentinel resolves (the hourly simulator derefs it for
    /// every rented registration). Which value it was is not in the logs. This names it — nothing else. User ruling:
    /// parity only; if it is the game's or a content mod's defect the mod does not patch it.</summary>
    [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.StartBusiness.StartBusinessUI), nameof(UI.Smartphone.Apps.BizMan.StartBusiness.StartBusinessUI.SetUpBusiness))]
    internal static class StartBusinessProbe
    {
        private static readonly System.Reflection.FieldInfo? _fType = AccessTools.Field(typeof(UI.Smartphone.Apps.BizMan.StartBusiness.StartBusinessUI), "_selectedType");
        private static readonly System.Reflection.FieldInfo? _fReg  = AccessTools.Field(typeof(UI.Smartphone.Apps.BizMan.StartBusiness.StartBusinessUI), "_buildingRegistration");
        private static int _logs;

        static void Prefix(UI.Smartphone.Apps.BizMan.StartBusiness.StartBusinessUI __instance)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return;   // MP worlds only (parity rule)
                string? sel = _fType?.GetValue(__instance) as string;
                if (BusinessTypeHelper.GetData(sel) != null) return;                                     // the healthy case — silent
                if (_logs++ >= 20) return;
                var reg = _fReg?.GetValue(__instance) as BuildingRegistration;
                string addr = "<no registration>"; try { if (reg != null) addr = GameStateReader.AddressKey(reg); } catch { }
                bool active = false; try { active = __instance.gameObject.activeInHierarchy; } catch { }
                Plugin.Logger.LogWarning($"[PROBE] StartBiz/TYPE: confirm pressed with an UNRESOLVED business type '{(sel ?? "<null>")}' at '{addr}' panelActive={active} isEmptySentinel={(sel == "ba:businesstype_empty")} — the native SetUpBusiness will throw next (bundle 20260903-180410).");
            }
            catch (Exception ex) { if (_logs++ < 20) Plugin.Logger.LogWarning($"[PROBE] StartBiz/TYPE: {ex.Message}"); }
        }
    }
    // PROBE-END: P-STARTBIZ-TYPE
}
