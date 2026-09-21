"""fixture_reset.py - blank every merger-related section of a rig fixture's session manifests.

WHY (2026-09-11, T-P3-3 run 1): a scenario that ends (or aborts) with a company still standing saves that
merger into the fixture slot's manifest (and the teardown's disconnect save into the -disconnect sibling).
The next scenario then loads an already-merged world: T-P3-3 logged GROWN instead of FORMED and its
"host is not a member" premise was gone. Every scenario must end dissolved, and this script repairs the
fixture when one did not.

Usage:  python tools/fixture_reset.py [--session save1] [--playthrough *] [--check] [--keep-merger]
  --check        only report; exit 1 when any manifest carries merger/paperwork/absence state.
  --keep-merger  leave every "Merger*" key AND Paperwork alone (blank only Absence / Transfers / Cargo*).
                 For a fixture whose PREMISE is a standing merged pair (the 2026-09-21 reporter-save
                 fixtures fx-hq1 / fx-till1): a plain reset would dissolve the very company the
                 scenario loads. CompanyBooks is not in KEYS_EXACT, so it is never blanked either way.
  --playthrough  defaults to "*" - match whatever playthrough folder the fixture was recorded under,
                 because the imported reporter saves do not share the rig's original playthrough id.
Blanks: Merger, MergerWalletBalance, MergerWalletContributed, Paperwork, Absence (and any key starting with
"Merger") in <SaveGames>/_BAMP_MP*/**/<playthrough>/<session>*/manifest.bamp.json. Never touches a .hsg.
NEVER DELETES A SESSION FOLDER. Sibling "<session>-disconnect*" / "<session>-auto*" folders the rig itself
wrote are REPORTED by --check as a WARN line and otherwise left exactly where they are.
"""
import glob, json, os, sys

SG = os.path.join(os.environ.get("USERPROFILE", ""), r"AppData\LocalLow\Hovgaard Games\Big Ambitions\SaveGames")
KEYS_EXACT = ("Paperwork", "Absence", "Transfers", "CargoTransfers", "CargoClosed", "CargoApplied")   # Transfers = build A's employee moves; Cargo* = 4c part 2b
KEEP_WITH_MERGER = ("Paperwork",)   # --keep-merger: the standing company's paperwork is part of the merger
SIDECARS = ("cargo-marks.bamp.json", "cargo-transit.bamp.json")   # 4c part 2b r2/r3: the PER-MACHINE cargo sidecars beside each manifest (idempotence marks; the host's in-transit table) - deleted on reset


def main(argv):
    session, playthrough, check, keep_merger = "save1", "*", False, False
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--session": session = argv[i + 1]; i += 2; continue
        if a == "--playthrough": playthrough = argv[i + 1]; i += 2; continue
        if a == "--check": check = True; i += 1; continue
        if a == "--keep-merger": keep_merger = True; i += 1; continue
        print("unknown arg", a); return 2
    dirty = 0
    siblings = set()
    for root in sorted(glob.glob(os.path.join(SG, "_BAMP_MP*"))):
        pattern = os.path.join(root, "**", playthrough, session + "*", "manifest.bamp.json")
        for m in sorted(glob.glob(pattern, recursive=True)):
            for side in SIDECARS:
                marks = os.path.join(os.path.dirname(m), side)
                if os.path.exists(marks):
                    dirty += 1
                    if check: print("DIRTY ", os.path.relpath(marks, SG), "(cargo sidecar present)")
                    else: os.remove(marks); print("REMOVED", os.path.relpath(marks, SG))
            raw = open(m, "rb").read()
            try:
                j = json.loads(raw.decode("utf-8-sig"))
            except Exception as ex:
                print("UNREADABLE", m, ex); continue
            # a rig-written sibling SESSION FOLDER is reported, never removed (see the module doc)
            try:
                d = os.path.dirname(m)
                base = os.path.basename(d)
                if base != session and (base.startswith(session + "-disconnect") or base.startswith(session + "-auto")):
                    siblings.add(os.path.relpath(d, SG))
            except Exception:
                pass
            keys = [k for k in j if k.startswith("Merger") or k in KEYS_EXACT]
            if keep_merger:
                keys = [k for k in keys if not k.startswith("Merger") and k not in KEEP_WITH_MERGER]
            state = {k: (len(j[k]) if isinstance(j[k], (list, dict)) else j[k]) for k in keys}
            has = any(v for v in state.values())
            rel = os.path.relpath(m, SG)
            if not has:
                print("clean ", rel); continue
            dirty += 1
            if check:
                print("DIRTY ", rel, state); continue
            for k in keys:
                v = j[k]
                j[k] = [] if isinstance(v, list) else {} if isinstance(v, dict) else 0 if isinstance(v, (int, float)) else v
            open(m, "w", encoding="utf-8", newline="\n").write(json.dumps(j, indent=2))
            print("RESET ", rel, state)
    if check:
        for sib in sorted(siblings):
            print("WARN  ", sib, "(rig-written sibling session folder - left alone on purpose)")
        print("fixture %s: %d dirty manifest(s), %d sibling session folder(s)" % (session, dirty, len(siblings)))
        return 1 if dirty else 0
    print("fixture %s: %d manifest(s) reset" % (session, dirty))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
