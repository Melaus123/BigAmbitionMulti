"""Non-game regression guards for Melaus123/BigAmbitionMulti issue #8.

Run: python -m unittest discover -s tools/tests -p 'test_issue8_*.py' -v
The actual Unity/game two-player round-trip is tools/scenarios/t-issue8-quantities.json.
These tests deliberately require neither Steam nor proprietary Big Ambitions assemblies.
"""
import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def source(name):
    return (ROOT / "src" / name).read_text(encoding="utf-8-sig")


class Issue8Contracts(unittest.TestCase):
    def test_plan_identity_survives_company_feed(self):
        paperwork = source("PaperworkSync.cs")
        absence = source("MergerAbsence.cs")
        self.assertIn("RemoveInstalledForOwner(tag, keepLogistics)", paperwork)
        self.assertIn("MergerFlip.IsFlipped(pl.HeadquartersAddressKey)", paperwork)
        self.assertIn("!MergerAbsence.SimulatesHere(pl.HeadquartersAddressKey)", paperwork)
        self.assertIn("keepLogistics.Contains((live.id ?? \"\")", absence)
        self.assertIn("ReconcileDisplayLogistics(addr, pl)", absence)
        self.assertIn("if (DisplayLogisticsMatches(live, latest))", absence)
        self.assertIn("live.destinations.AddRange(updated)", absence)

    def test_native_editor_does_not_get_overwritten(self):
        paperwork = source("PaperworkSync.cs")
        absence = source("MergerAbsence.cs")
        plans = source("CompanyPlans.cs")
        self.assertIn("HasPendingLogisticsEdit(string id)", paperwork)
        self.assertIn("CompanyPlans.HeadquartersEditInProgress()", absence)
        self.assertIn("_deferredDisplayLogistics[live] = latest", absence)
        self.assertIn("CompanyLists.HasPendingLogisticsEdit(kv.Key.id", absence)
        self.assertIn("MergerAbsence.FlushDeferredDisplayLogistics()", plans)
        self.assertIn("GetComponentsInChildren<TMPro.TMP_InputField>(false)", plans)
        self.assertIn("GetComponentsInChildren<UnityEngine.UI.InputField>(false)", plans)

    def test_partner_importer_cannot_silently_write_detached_copy(self):
        plans = source("CompanyPlans.cs")
        patches = source("MPPatches.cs")
        self.assertIn("RefuseStalePartnerPurchasingPlan(ImportPartnership? plan)", plans)
        self.assertIn("CompanyLists.TryOwnerOfAddress(hq, out var owner)", plans)
        self.assertIn("plan as ImportPartnership", plans)
        self.assertEqual(3, patches.count("if (CompanyPlans.RefuseStalePartnerPurchasingPlan(ip)) return false;"))

    def test_real_quantity_probe_is_read_only(self):
        plans = source("CompanyPlans.cs")
        a = plans.index("private static string HqLogTargets(string id)")
        b = plans.index("public static string TestDriveHqLog(string arg)", a)
        read = plans[a:b]
        self.assertIn("pl.destinations[i]", read)
        self.assertIn("t.targetAmount", read)
        self.assertNotIn("MarkChange()", read)
        self.assertNotIn("RoutePaneEdit(", read)
        self.assertIn('if (op == "targets") return HqLogTargets(id);', plans)

    def test_two_player_scenario_valid_and_covers_both_directions(self):
        path = ROOT / "tools/scenarios/t-issue8-quantities.json"
        data = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual(2, data["instances"])
        seq = [x["seq"] for x in data["steps"]]
        self.assertEqual(list(range(1, len(seq) + 1)), seq)
        commands = [x.get("cmd", "") for x in data["steps"]]
        for required in ("hqlog ${logPlan} target 0 ${logItem} 14",
                         "hqlog ${logPlan} target 0 ${logItem} 25",
                         "hqtarget ${puPlan} ${puItem} 31",
                         "planedit purchasing ${puPlan} target ${puItem} 47"):
            self.assertIn(required, commands)
        self.assertEqual(2, sum(c.startswith("hqlog ${logPlan} target 0 ") for c in commands))
        for step in data["steps"]:
            self.assertIn(step["role"], ("h", "c", "both"))
            for field in ("expect_result", "refute_result"):
                if field in step:
                    re.compile(re.sub(r"\$\{[^}]+\}", "TOKEN", step[field]))
            for log in step.get("expect_log", []):
                re.compile(re.sub(r"\$\{[^}]+\}", "TOKEN", log["regex"]))


if __name__ == "__main__":
    unittest.main()
