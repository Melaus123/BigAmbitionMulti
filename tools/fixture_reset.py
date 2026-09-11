"""fixture_reset.py - blank every merger-related section of a rig fixture's session manifests.

WHY (2026-09-11, T-P3-3 run 1): a scenario that ends (or aborts) with a company still standing saves that
merger into the fixture slot's manifest (and the teardown's disconnect save into the -disconnect sibling).
The next scenario then loads an already-merged world: T-P3-3 logged GROWN instead of FORMED and its
"host is not a member" premise was gone. Every scenario must end dissolved, and this script repairs the
fixture when one did not.

Usage:  python tools/fixture_reset.py [--session save1] [--playthrough 738b6442...] [--check]
  --check  only report; exit 1 when any manifest carries merger/paperwork/absence state.
Blanks: Merger, MergerWalletBalance, MergerWalletContributed, Paperwork, Absence (and any key starting with
"Merger") in <SaveGames>/_BAMP_MP*/**/<playthrough>/<session>*/manifest.bamp.json. Never touches a .hsg.
"""
import glob, json, os, sys

SG = os.path.join(os.environ.get("USERPROFILE", ""), r"AppData\LocalLow\Hovgaard Games\Big Ambitions\SaveGames")
KEYS_EXACT = ("Paperwork", "Absence")


def main(argv):
    session, playthrough, check = "save1", "738b64427f2f4ae99e0f79b66196173c", False
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--session": session = argv[i + 1]; i += 2; continue
        if a == "--playthrough": playthrough = argv[i + 1]; i += 2; continue
        if a == "--check": check = True; i += 1; continue
        print("unknown arg", a); return 2
    dirty = 0
    for root in sorted(glob.glob(os.path.join(SG, "_BAMP_MP*"))):
        pattern = os.path.join(root, "**", playthrough, session + "*", "manifest.bamp.json")
        for m in sorted(glob.glob(pattern, recursive=True)):
            raw = open(m, "rb").read()
            try:
                j = json.loads(raw.decode("utf-8-sig"))
            except Exception as ex:
                print("UNREADABLE", m, ex); continue
            keys = [k for k in j if k.startswith("Merger") or k in KEYS_EXACT]
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
        print("fixture %s: %d dirty manifest(s)" % (session, dirty))
        return 1 if dirty else 0
    print("fixture %s: %d manifest(s) reset" % (session, dirty))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
