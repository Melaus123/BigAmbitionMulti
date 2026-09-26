"""fixture_reset.py - build the rig's OWN save roots from a fixture, then blank every merger-related section.

WHERE THE RIG SAVES (RIG-SAVEROOT, 2026-09-26): rig instances never write into
<LocalLow>\\Hovgaard Games\\Big Ambitions\\SaveGames - that is the folder Steam Cloud syncs for this game
(every entry of Steam's remotecache.vdf for app 1331550 sits under it), and rig writes there raised cloud
conflicts for the user. rigrun.py sets BAMP_RIG_SAVEROOT=<LocalLow>\\...\\BAMP_RigSaves and the DEV build
redirects each instance's whole save root to BAMP_RigSaves\\<role> (h = host, c / d = the client installs;
src/RigSaveRoot.cs). SaveGames is only ever READ here, as the copy source.

prepare (the default action, and what rigrun.py calls before every launch):
  1. deletes BAMP_RigSaves entirely - the previous run's copies, -auto / -disconnect / -recover siblings
     and sign images - so the rig root cannot grow;
  2. copies, for EVERY _BAMP_MP* store under SaveGames, its store-level files (storeformat / _laststore /
     _carry / clientDisconnect ...) and the fixture session folder(s) named exactly <session> (under
     <playthrough>) into BAMP_RigSaves\\<role>\\<same relative path>, for each role h, c, d.
     Each instance then finds its own store (_BAMP_MP, _BAMP_MP_SIMCLIENT, _BAMP_MP_SIMCLIENT3) where it
     always did, relative to its redirected root.

reset (after prepare) - WHY (2026-09-11, T-P3-3 run 1): a scenario that ends (or aborts) with a company still
standing saves that merger into the fixture slot's manifest; the next scenario then loads an already-merged
world. Blanks Merger, MergerWalletBalance, MergerWalletContributed, Paperwork, Absence (and any key starting
with "Merger") in BAMP_RigSaves/*/_BAMP_MP*/**/<playthrough>/<session>*/manifest.bamp.json, and deletes the
per-machine cargo sidecars beside them. Never touches a .hsg. Only the rig's COPIES are written.

Usage:  python tools/fixture_reset.py [--session save1] [--playthrough *] [--check] [--keep-merger]
  --check        only report on the rig root (no copy); exit 1 when any manifest carries merger/paperwork/
                 absence state (a session with no copy at all reports 0 manifests and exits 0, as before -
                 a scenario that starts with hostnew has no fixture).
  --keep-merger  leave every "Merger*" key AND Paperwork alone (blank only Absence / Transfers / Cargo*).
                 For a fixture whose PREMISE is a standing merged pair (the 2026-09-21 reporter-save
                 fixtures fx-hq1 / fx-till1). CompanyBooks is not in KEYS_EXACT, so it is never blanked.
  --playthrough  defaults to "*" - match whatever playthrough folder the fixture was recorded under.
"""
import glob, json, os, shutil, stat, sys

LOCALLOW = os.path.join(os.environ.get("USERPROFILE", ""), r"AppData\LocalLow\Hovgaard Games\Big Ambitions")
SG = os.path.join(LOCALLOW, "SaveGames")         # Steam-Cloud synced: READ ONLY (the copy source)
RIG = os.path.join(LOCALLOW, "BAMP_RigSaves")    # the rig's save roots: RIG\h, RIG\c, RIG\d (src/RigSaveRoot.cs)
RIG_ROLES = ("h", "c", "d")
MANIFEST = "manifest.bamp.json"
KEYS_EXACT = ("Paperwork", "Absence", "Transfers", "CargoTransfers", "CargoClosed", "CargoApplied")   # Transfers = build A's employee moves; Cargo* = 4c part 2b
KEEP_WITH_MERGER = ("Paperwork",)   # --keep-merger: the standing company's paperwork is part of the merger
SIDECARS = ("cargo-marks.bamp.json", "cargo-transit.bamp.json")   # 4c part 2b r2/r3: the PER-MACHINE cargo sidecars beside each manifest (idempotence marks; the host's in-transit table) - deleted on reset


def _rig_is_safe():
    """The one folder this script may delete: exactly <LocalLow>\\...\\BAMP_RigSaves, never inside SaveGames."""
    r, sg = os.path.normcase(os.path.abspath(RIG)), os.path.normcase(os.path.abspath(SG))
    return (os.path.basename(r) == "bamp_rigsaves" and os.path.dirname(r) == os.path.normcase(os.path.abspath(LOCALLOW))
            and not r.startswith(sg + os.sep))


def _force_remove(func, path, _exc):
    os.chmod(path, stat.S_IWRITE)
    func(path)


def prepare(session="save1", playthrough="*", roles=RIG_ROLES, log=print):
    """Wipe the rig root, then copy the fixture FROM SaveGames (read only) into RIG\\<role> for every role.
    Returns the number of session folders copied per role (0 = the fixture does not exist under SaveGames)."""
    if not _rig_is_safe():
        raise RuntimeError("refusing: rig root '%s' is not the expected BAMP_RigSaves folder" % RIG)
    if os.path.exists(RIG):
        shutil.rmtree(RIG, onerror=_force_remove)
        log("WIPED  %s (previous run's copies)" % RIG)
    store_files, sessions = [], []
    for root in sorted(glob.glob(os.path.join(SG, "_BAMP_MP*"))):
        if not os.path.isdir(root):
            continue
        # Review LOW-4: the session AND its rotation/lineage siblings (-auto, -auto-N, -disconnect, -recover)
        for pat in (session, session + "-*"):
            for m in sorted(glob.glob(os.path.join(root, "**", playthrough, pat, MANIFEST), recursive=True)):
                if os.path.dirname(m) not in sessions:
                    sessions.append(os.path.dirname(m))
        # store-level files: every file NOT inside a session folder (a folder holding a manifest), 3 levels deep
        for dp, dn, fn in os.walk(root):
            depth = os.path.relpath(dp, root).count(os.sep) + (0 if dp == root else 1)
            if MANIFEST in fn and dp != root:
                dn[:] = []
                continue
            store_files += [os.path.join(dp, f) for f in fn]
            if depth >= 2:
                dn[:] = []
    for role in roles:
        dst_root = os.path.join(RIG, role)
        os.makedirs(dst_root, exist_ok=True)
        for f in store_files:
            d = os.path.join(dst_root, os.path.relpath(f, SG))
            os.makedirs(os.path.dirname(d), exist_ok=True)
            shutil.copy2(f, d)
        for s in sessions:
            shutil.copytree(s, os.path.join(dst_root, os.path.relpath(s, SG)))
    for s in sessions:
        log("COPIED %s -> %s\\{%s}" % (os.path.relpath(s, SG), RIG, ",".join(roles)))
    log("fixture %s: %d session folder(s) + %d store file(s) copied into %d rig root(s) (SaveGames only read)"
        % (session, len(sessions), len(store_files), len(roles)))
    return len(sessions)


def reset(session="save1", playthrough="*", check=False, keep_merger=False, log=print):
    """Blank the merger-related state of every rig COPY of the session. Returns (dirty, siblings, manifests)."""
    dirty, seen = 0, 0
    siblings = set()
    for root in sorted(glob.glob(os.path.join(RIG, "*", "_BAMP_MP*"))):
        pattern = os.path.join(root, "**", playthrough, session + "*", MANIFEST)
        for m in sorted(glob.glob(pattern, recursive=True)):
            seen += 1
            for side in SIDECARS:
                marks = os.path.join(os.path.dirname(m), side)
                if os.path.exists(marks):
                    dirty += 1
                    if check: log("DIRTY  %s (cargo sidecar present)" % os.path.relpath(marks, RIG))
                    else: os.remove(marks); log("REMOVED %s" % os.path.relpath(marks, RIG))
            raw = open(m, "rb").read()
            try:
                j = json.loads(raw.decode("utf-8-sig"))
            except Exception as ex:
                log("UNREADABLE %s %s" % (m, ex)); continue
            base = os.path.basename(os.path.dirname(m))
            if base != session and (base.startswith(session + "-disconnect") or base.startswith(session + "-auto")):
                siblings.add(os.path.relpath(os.path.dirname(m), RIG))
            keys = [k for k in j if k.startswith("Merger") or k in KEYS_EXACT]
            if keep_merger:
                keys = [k for k in keys if not k.startswith("Merger") and k not in KEEP_WITH_MERGER]
            state = {k: (len(j[k]) if isinstance(j[k], (list, dict)) else j[k]) for k in keys}
            rel = os.path.relpath(m, RIG)
            if not any(v for v in state.values()):
                log("clean  %s" % rel); continue
            dirty += 1
            if check:
                log("DIRTY  %s %s" % (rel, state)); continue
            for k in keys:
                v = j[k]
                j[k] = [] if isinstance(v, list) else {} if isinstance(v, dict) else 0 if isinstance(v, (int, float)) else v
            open(m, "w", encoding="utf-8", newline="\n").write(json.dumps(j, indent=2))
            log("RESET  %s %s" % (rel, state))
    return dirty, siblings, seen


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
    if not check:
        prepare(session, playthrough)
    dirty, siblings, seen = reset(session, playthrough, check, keep_merger)
    if check:
        for sib in sorted(siblings):
            print("WARN   %s (rig-written sibling session folder - wiped by the next prepare)" % sib)
        print("fixture %s: %d dirty manifest(s), %d sibling session folder(s), %d manifest(s) in the rig root"
              % (session, dirty, len(siblings), seen))
        return 1 if dirty else 0
    print("fixture %s: %d manifest(s) reset" % (session, dirty))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
