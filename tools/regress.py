"""tools/regress.py - run rig scenarios back to back on the DEPLOYED build, each from a clean fixture.

Usage:  python tools/regress.py [<scenario-id> ...]     (ids, not paths: t-skippace-till-1)

The fixture reset is built from the SCENARIO ITSELF, not hard-coded: each scenario's "vars" name the
session it loads (the same "session" var its `hostload ${session}` step uses), optionally the
"playthrough" folder, and "keepMerger": true for a fixture whose premise IS a standing merged pair
(the reporter saves fx-hq1 / fx-till1 - a plain reset would dissolve the company before the scenario
ever loads it). Grown out of scratchpad/mac1_regress.py, which is left where it is and still works.
"""
import hashlib, os, subprocess, sys, glob, re, json
sys.stdout.reconfigure(errors="replace")
ROOT = r"C:\code\BigAmbitionsMP"
DEP = r"C:\Users\allsc\AppData\LocalLow\Hovgaard Games\Big Ambitions\ModsLocal\BigAmbitionsMP\BigAmbitionsMP.dll"
SRC = os.path.join(ROOT, r"bin\Dev\net48\BigAmbitionsMP.dll")
def md5(p): return hashlib.md5(open(p,'rb').read()).hexdigest()
assert md5(DEP) == md5(SRC), "deployed != bin/Dev"
print("deployed", md5(DEP))
if subprocess.run('tasklist //FI "IMAGENAME eq Big Ambitions.exe"', shell=True, capture_output=True, text=True).stdout.count("Big Ambitions.exe"):
    sys.exit("game running - abort")
scen = sys.argv[1:] or ["t-rig3-smoke","t-hqparity3","t-walletdupe","t-traffic-apart","t-p4d-cues","t-taxone","t-p4c-cargo"]

def fixture_args(sid):
    """The fixture_reset call THIS scenario needs, read from its own vars."""
    path = os.path.join(ROOT, "tools", "scenarios", "%s.json" % sid)
    v = {}
    try:
        v = (json.load(open(path, encoding="utf-8-sig")).get("vars") or {})
    except Exception as ex:
        print("  (scenario vars unreadable, default fixture)", ex)
    a = ["python", "tools/fixture_reset.py", "--session", str(v.get("session", "save1"))]
    if v.get("playthrough"): a += ["--playthrough", str(v["playthrough"])]
    if str(v.get("keepMerger", "")).strip().lower() in ("1", "true", "yes"): a += ["--keep-merger"]
    return a

for s in scen:
    before = set(glob.glob(os.path.join(ROOT,".modding","work","runs","*")))
    fx = fixture_args(s)
    print("--", s, "fixture:", " ".join(fx[2:]))
    for cmd in (fx, fx + ["--check"], ["python","tools/rigrun.py","tools/scenarios/%s.json"%s]):
        r = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True, timeout=2400)
        if r.returncode and "fixture_reset" in cmd[1]:
            print(s, "FIXTURE FAIL", (r.stdout or "")[-300:]); break
    out = (r.stdout or "")
    verdict = [l for l in out.splitlines() if re.search(r"RESULT|PASS|FAIL|steps", l)][-3:]
    new = sorted(set(glob.glob(os.path.join(ROOT,".modding","work","runs","*"))) - before)
    print("==", s, "rc", r.returncode, "|", " / ".join(verdict), "|", [os.path.basename(n) for n in new])
    for d in new:
        for lf in glob.glob(os.path.join(d,"*Player*.log")):
            if "pre-launch" in lf: continue
            L = open(lf, encoding='utf-8', errors='replace').read().splitlines()
            exc = [l for l in L if "Exception" in l and "[BAMP]" not in l][:2]
            pf = [l for l in L if re.search(r"Patch class .* FAILED", l)][:2]
            hb = [l for l in L if "Harmony provenance" in l][:1]
            print("   ", os.path.basename(lf), "exceptions", len([l for l in L if "Exception" in l]), "patchfail", len(pf), "|", (hb[0][-90:] if hb else "no provenance line"))
            for l in exc + pf: print("      ", l[:160])
