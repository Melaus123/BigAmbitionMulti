#if BAMP_DEV
using System;
using System.Collections.Generic;
using System.Text;
using Buildings.Office.Headquarters;             // LogisticsManagerPlan
using Entities;
using Helpers;
using Streets;                                   // Address, as CompanyPlans.cs uses it
using HarmonyLib;                                // AccessTools (private game fields/methods, MPPatches.PlanSet)
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>T-53 (evidence only) DEV levers, dev build only; they drive tools/scenarios/t-logwarehouse.json.
    /// `logpane open|dropdown|state|pick` puts the GAME'S OWN logistics plan pane on a partner's display copy,
    /// opens the pane's own warehouse dropdown, reads what the warehouse gate will see, and picks a warehouse
    /// through the dropdown's own option select - so a fresh copy arriving between the open and the pick
    /// leaves the pane holding the very object it was opened with, exactly as on the player's screen.
    /// Nothing here changes mod behaviour: the pick runs the game's own handler and the existing gate
    /// (MPPatches.Patch_LogisticsPlanWarehouse_MergerGate) decides.</summary>
    internal static class DevT53
    {
        /// <summary>The plan object `logpane open` handed to the pane's LoadPlan - kept only to report
        /// whether the pane still shows that same object.</summary>
        private static LogisticsManagerPlan? _held;

        internal static string Run(string arg)
        {
            try
            {
                var a = (arg ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (a.Length == 0) return "ERR usage: logpane open <planId> | dropdown | state | wh | pick <warehouseKey>";
                if (SaveGameManager.Current == null) return "ERR logpane: no game loaded";
                switch (a[0].ToLowerInvariant())
                {
                    case "open":     return a.Length < 2 ? "ERR usage: logpane open <planId>" : Open(a[1]);
                    case "dropdown": return Dropdown();
                    case "state":    return "OK logpane state " + State();
                    case "wh":       return Wh();
                    case "pick":     return a.Length < 2 ? "ERR usage: logpane pick <warehouseKey>" : Pick(string.Join(" ", a, 1, a.Length - 1));
                    default:         return $"ERR logpane: no op '{a[0]}'";
                }
            }
            catch (Exception ex) { return "ERR logpane: " + ex.Message; }
        }

        private static UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagerPlanUI? Pane()
            => CompanyPlans.PaneOf("logistics") as UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagerPlanUI;

        private static T? FieldOf<T>(object o, string name) where T : class
        {
            var f = AccessTools.Field(o.GetType(), name);
            return f == null ? null : f.GetValue(o) as T;
        }

        private static LogisticsManagerPlan? Shown(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagerPlanUI pane)
            => FieldOf<LogisticsManagerPlan>(pane, "_currentPlan");   // decompile LogisticsManagerPlanUI.cs:71

        private static string Key(Address? ad)
        {
            try { return ad == null ? "" : (GameStateReader.AddressKey(ad) ?? ""); } catch { return ""; }
        }

        /// <summary>`logpane open <planId>`: the game's own BizMan.Open on the plan's headquarters at the
        /// LogisticsManagers tab (decompile BizMan.cs:60-76), then the pane's own LoadPlan with the display copy
        /// (LogisticsManagerPlanUI.cs:127 - what the list's SelectPlan calls on a row click).</summary>
        private static string Open(string id)
        {
            var gi = SaveGameManager.Current;
            LogisticsManagerPlan? pl = null;
            foreach (var x in gi.logisticsManagerPlans ?? new List<LogisticsManagerPlan>())
                if (x != null && x.id == id) { pl = x; break; }
            if (pl == null) return $"ERR logpane open: no logistics plan '{id}' here";
            if (!CompanyLists.IsDisplayPlan(pl)) return $"ERR logpane open: '{id}' is not a partner's display copy here (premise)";
            if (pl.isFactory) return $"ERR logpane open: '{id}' is a factory plan (premise: a logistics plan)";
            var ui = InstanceBehavior<UI.UIs>.Instance;
            if (ui == null || ui.fullMenu == null || ui.fullMenu.bizMan == null) return "ERR logpane open: no BizMan app";
            ui.fullMenu.bizMan.Open(pl.headquartersAddress, "LogisticsManagers");
            var pane = Pane();
            if (pane == null) return "ERR logpane open: no logistics plan pane found after BizMan.Open";
            pane.LoadPlan(pl);
            _held = pl;
            var whs = FieldOf<List<BuildingRegistration>>(pane, "_warehouses");   // decompile :73, filled by :185
            var sb = new StringBuilder();
            foreach (var r in whs ?? new List<BuildingRegistration>())
            {
                string k = ""; try { k = GameStateReader.AddressKey(r); } catch { }
                if (sb.Length > 0) sb.Append(',');
                sb.Append('\'').Append(k).Append('\'');
            }
            return $"OK logpane open {id} hq='{Key(pl.headquartersAddress)}' offered=[{sb}] " + State();
        }

        /// <summary>`logpane wh` (read-only): this machine's OWN warehouse businesses as the pane's warehouse
        /// dropdown filters them - business type ba:businesstype_warehouse and building type
        /// ba:buildingtype_warehouse (decompile LogisticsManagerPlanUI.cs:192-199), RentedByPlayer through the
        /// game's GetPlayerBuildingRegistrations (:185) and TrulyMine here - and which plan, if any, already takes
        /// each as its source: the game drops a warehouse ANOTHER plan targets (_occupiedWarehouses, :177-184).
        /// pick = the first free one, else the first one; occupant/kind = that plan's id and own|display.</summary>
        private static string Wh()
        {
            var gi = SaveGameManager.Current;
            var sb = new StringBuilder();
            string pick = "", occ = "-", kind = "none";
            bool pickFree = false;
            foreach (var r in gi.BuildingRegistrations ?? new List<BuildingRegistration>())
            {
                if (r == null || r.businessTypeName != "ba:businesstype_warehouse") continue;
                string bt = ""; try { bt = r.GetBuildingType(); } catch { }
                if (bt != "ba:buildingtype_warehouse") continue;
                bool mine = false; try { mine = r.RentedByPlayer && MergerFlip.TrulyMine(r); } catch { }
                if (!mine) continue;
                string k = ""; try { k = GameStateReader.AddressKey(r); } catch { }
                if (k.Length == 0) continue;
                string o = "-", ok = "none";
                foreach (var pl in gi.logisticsManagerPlans ?? new List<LogisticsManagerPlan>())
                    if (pl != null && Key(pl.targetAddress) == k) { o = pl.id; ok = CompanyLists.IsDisplayPlan(pl) ? "display" : "own"; break; }
                if (sb.Length > 0) sb.Append(',');
                sb.Append('\'').Append(k).Append("'=").Append(o).Append(':').Append(ok);
                if (pick.Length == 0 || (!pickFree && o == "-")) { pick = k; occ = o; kind = ok; pickFree = o == "-"; }
            }
            if (pick.Length == 0) return "ERR logpane wh: this machine owns no warehouse business (premise)";
            return $"OK logpane wh pick='{pick}' occupant={occ} kind={kind} all=[{sb}]";
        }

        /// <summary>`logpane dropdown`: the warehouse dropdown's own middle-box click (decompile
        /// UI.Elements/Dropdown.cs:198-213 -> ShowOptions :215, which sets currentDropdown :229 and shows the
        /// options panel :230 before its first yield). Not clicked again when it is already open (the click
        /// would close it).</summary>
        private static string Dropdown()
        {
            var pane = Pane();
            if (pane == null) return "ERR logpane dropdown: no logistics plan pane";
            var dd = FieldOf<UI.Elements.Dropdown>(pane, "warehouseDropdown");   // decompile :45
            if (dd == null) return "ERR logpane dropdown: the pane has no warehouse dropdown";
            var rect = FieldOf<RectTransform>(dd, "optionsPanelParentRect");     // Dropdown.cs:54
            if (rect == null || !rect.gameObject.activeSelf)
                AccessTools.Method(typeof(UI.Elements.Dropdown), "ClickMiddleBox").Invoke(dd, null);
            return "OK logpane dropdown " + State();
        }

        /// <summary>`logpane pick <warehouseKey>`: the dropdown's own SelectOption (Dropdown.cs:399-419), whose
        /// onOptionSelected listener is the pane's OnChangedWarehouse (LogisticsManagerPlanUI.cs:83/:210) - the
        /// handler the warehouse gate prefixes. pre[...] is read immediately before, on the same frame.</summary>
        private static string Pick(string whKey)
        {
            var pane = Pane();
            if (pane == null) return "ERR logpane pick: no logistics plan pane";
            var dd = FieldOf<UI.Elements.Dropdown>(pane, "warehouseDropdown");
            var whs = FieldOf<List<BuildingRegistration>>(pane, "_warehouses");
            var shown = Shown(pane);
            if (dd == null || whs == null || shown == null) return "ERR logpane pick: the pane holds no plan or no warehouse list";
            int idx = -1;
            for (int i = 0; i < whs.Count; i++)
            {
                string k = ""; try { k = GameStateReader.AddressKey(whs[i]); } catch { }
                if (k == whKey) { idx = i + 1; break; }   // option 0 = "unassigned" (decompile :186)
            }
            if (idx < 0) return $"ERR logpane pick: '{whKey}' is not offered by the pane's warehouse dropdown (premise)";
            if (dd.SelectedOptionIndex == idx) return $"ERR logpane pick: '{whKey}' is already the selected option - SelectOption would fire nothing";
            string pre = State();
            string before = Key(shown.targetAddress);
            dd.SelectOption(idx);
            string after = Key(shown.targetAddress);
            string outcome = after == whKey ? "accepted" : "refused";
            return $"OK logpane pick wh='{whKey}' idx={idx} plan={shown.id} pre[{pre}] outcome={outcome} target='{before}'->'{after}'";
        }

        /// <summary>What the warehouse gate (MPPatches.cs Patch_LogisticsPlanWarehouse_MergerGate) will see:
        /// known = the pane's object IS an element of this machine's logisticsManagerPlans; display =
        /// CompanyLists.IsDisplayPlan (the gate's first branch); live = the list's element with the same id is a
        /// display copy and is NOT the pane's object; planSet = MPPatches.PlanSet on the pane's object (the own-plan
        /// branch's input: own/sim/elsewhere/none); edit = CompanyPlans.EditWindowOpen (why a redraw would defer).</summary>
        private static string State()
        {
            var pane = Pane();
            if (pane == null) return "pane=none";
            var shown = Shown(pane);
            bool active = pane.gameObject.activeInHierarchy;
            if (shown == null) return $"pane={active} plan=- edit='{CompanyPlans.EditWindowOpen()}'";
            bool known = false; LogisticsManagerPlan? live = null;
            foreach (var x in SaveGameManager.Current.logisticsManagerPlans ?? new List<LogisticsManagerPlan>())
            {
                if (ReferenceEquals(x, shown)) known = true;
                if (x != null && x.id == shown.id && live == null) live = x;
            }
            string set = "?";
            try
            {
                var m = AccessTools.Method(typeof(MPPatches), "PlanSet", new[] { typeof(LogisticsManagerPlan) });
                int s = m != null ? (int)m.Invoke(null, new object[] { shown }) : int.MinValue;
                set = s == 0 ? "own" : s == 1 ? "sim" : s == -1 ? "elsewhere" : s == 2 ? "none" : "?";
            }
            catch { }
            bool liveOther = live != null && !ReferenceEquals(live, shown) && CompanyLists.IsDisplayPlan(live);
            return $"pane={active} plan={shown.id} held={ReferenceEquals(shown, _held)} known={known} display={CompanyLists.IsDisplayPlan(shown)} "
                 + $"liveCopy={liveOther} planSet={set} target='{Key(shown.targetAddress)}' edit='{CompanyPlans.EditWindowOpen()}'";
        }
    }
}
#endif
