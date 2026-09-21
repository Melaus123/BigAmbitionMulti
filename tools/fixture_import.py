"""fixture_import.py - install a bug-bundle MP session into the game's own save store as a
NORMAL, LOADABLE rig fixture, re-keyed from the reporter's two identities to this rig's two.

USER RULING it implements (2026-09-21): a kept save is a normal loadable session inside the
game's SaveGames folder, visible in the multiplayer load list. No private copies, nothing in
the repo, nothing hidden.

  python fixture_import.py "<bundle>\\saves\\<session folder>" --name fx-till1 \
         --host <original host stable id> --client <original client stable id> [--dry-run]

What it writes (and ONLY this):
  <USERPROFILE>\\AppData\\LocalLow\\Hovgaard Games\\Big Ambitions\\SaveGames\\_BAMP_MP\\
      <game version folder>\\<playthrough id>\\<--name>\\
          manifest.bamp.json          (re-keyed; source .hsg files untouched)
          <rig host stable id>\\save.hsg[.meta]
          <rig client stable id>\\save.hsg[.meta]

.hsg BYTES ARE NEVER TOUCHED. Everything the rig's two identities key off lives in the folder
names, the manifest and the .hsg.meta sidecar (see the report's D2) - the world file itself is
identity-free (its own `characterId` is an Odin object handle, not a player id).

REFUSES to overwrite: any existing session whose name is <--name> or starts with "<--name>"
(the lineage family: -auto, -auto-2, -disconnect, -recover) in the target playthrough.
"""
import argparse, glob, json, os, re, shutil, sys

SAVEGAMES = os.path.join(os.environ.get("USERPROFILE", ""),
                         r"AppData\LocalLow\Hovgaard Games\Big Ambitions\SaveGames")
MP_ROOT_NAME = "_BAMP_MP"                 # src\MPSaveManager.cs:330 (MpRootName)
SIM_ROOT_NAME = "_BAMP_MP_SIMCLIENT"      # src\MPSaveManager.cs:342 - the CLIENT instance's own root
CFG_DIR = os.path.join(os.environ.get("USERPROFILE", ""),
                       r"AppData\LocalLow\Hovgaard Games\Big Ambitions\BigAmbitionsMP")
CFG_HOST = "BigAmbitionsMP.cfg.Big_Ambitions.json"    # InstallKey of the Steam install (host)
CFG_CLIENT = "BigAmbitionsMP.cfg.BigAmbitions2.json"  # InstallKey of C:\BigAmbitions2 (client)
MANIFEST = "manifest.bamp.json"
SIDECARS = ("cargo-marks.bamp.json", "cargo-transit.bamp.json")   # per-machine, per-timeline: never imported


def die(msg):
    print("REFUSED: " + msg)
    sys.exit(2)


def read_json(path):
    with open(path, "rb") as f:
        return json.loads(f.read().decode("utf-8-sig"))


def rig_ids(args):
    """This rig's two stable ids. --to-host/--to-client win; else the two instances' own
    config files (src\\MPConfig.cs:263 writes BigAmbitionsMP.cfg.<InstallKey>.json, key
    'StableId'); else the existing save1 fixture manifest."""
    if args.to_host and args.to_client:
        return args.to_host, args.to_client, "--to-host/--to-client"
    h = c = None
    try:
        h = read_json(os.path.join(CFG_DIR, CFG_HOST)).get("StableId")
        c = read_json(os.path.join(CFG_DIR, CFG_CLIENT)).get("StableId")
    except Exception:
        pass
    if h and c:
        return h, c, "instance config files (%s / %s)" % (CFG_HOST, CFG_CLIENT)
    ref = os.path.join(SAVEGAMES, MP_ROOT_NAME, args.version or "", args.ref_playthrough, args.ref_session, MANIFEST)
    try:
        m = read_json(ref)
        hh = [s for s in m["Slots"] if s.get("IsHost")]
        cc = [s for s in m["Slots"] if s.get("DisplayName") == "Client1"]
        if hh and cc:
            return hh[0]["StableId"], cc[0]["StableId"], "reference fixture %s" % args.ref_session
    except Exception:
        pass
    die("could not resolve this rig's stable ids - pass --to-host and --to-client.")


def version_folder(explicit):
    """The game version folder name (the MP store is a SIBLING of the SP version folder:
    src\\MPSaveManager.cs:574 MpVersionFolder)."""
    root = os.path.join(SAVEGAMES, MP_ROOT_NAME)
    if explicit:
        return explicit
    cands = [d for d in glob.glob(os.path.join(root, "*")) if os.path.isdir(d)]
    if not cands:
        die("no version folder under %s - is the mod installed?" % root)
    # the SP side is the truth; pick the version folder that exists on BOTH sides, newest first
    sp = {os.path.basename(d) for d in glob.glob(os.path.join(SAVEGAMES, "*"))
          if os.path.isdir(d) and not os.path.basename(d).startswith("_")}
    both = [d for d in cands if os.path.basename(d) in sp]
    pick = max(both or cands, key=os.path.getmtime)
    return os.path.basename(pick)


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("source", help=r"bundle session folder (holds manifest.bamp.json + per-player folders)")
    ap.add_argument("--name", required=True, help="fixture session name, e.g. fx-till1")
    ap.add_argument("--host", required=True, help="ORIGINAL host stable id in the bundle (steam-p...)")
    ap.add_argument("--client", required=True, help="ORIGINAL client stable id in the bundle (steam-p...)")
    ap.add_argument("--to-host", default="", help="this rig's HOST stable id (default: auto)")
    ap.add_argument("--to-client", default="", help="this rig's CLIENT stable id (default: auto)")
    ap.add_argument("--host-name", default="melaus", help="rig host display name")
    ap.add_argument("--client-name", default="Client1", help="rig client display name")
    ap.add_argument("--playthrough", default="", help="target playthrough id (default: the source manifest's own)")
    ap.add_argument("--version", default="", help="game version folder (default: auto-detected)")
    ap.add_argument("--ref-playthrough", default="738b64427f2f4ae99e0f79b66196173c")
    ap.add_argument("--ref-session", default="save1")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args(argv)

    src = os.path.abspath(args.source)
    if not os.path.isdir(src):
        die("source is not a folder: %s" % src)
    src_manifest = os.path.join(src, MANIFEST)
    if not os.path.exists(src_manifest):
        die("no %s in %s - pick the session folder that HAS a manifest (an 'anomaly-*' folder has none)." % (MANIFEST, src))
    if not re.match(r"^[A-Za-z0-9 _.-]{1,48}$", args.name):
        die("--name must be a plain session name (letters, digits, space, _ . -).")

    raw = open(src_manifest, "rb").read().decode("utf-8-sig")
    man = json.loads(raw)
    slots = {s.get("StableId"): s for s in man.get("Slots", [])}
    for who, sid in (("--host", args.host), ("--client", args.client)):
        if sid not in slots:
            die("%s '%s' is not a slot in the source manifest (slots: %s)" % (who, sid, ", ".join(slots)))
    if args.host == args.client:
        die("--host and --client are the same id.")
    to_host, to_client, src_of_ids = rig_ids(args)
    if to_host == to_client:
        die("this rig's host and client stable ids are identical - the two instances share a config.")
    for sid in (to_host, to_client):
        if sid in raw:
            die("target id '%s' already occurs in the source manifest - re-keying would collide." % sid)

    ver = version_folder(args.version)
    pid = args.playthrough or man.get("PlaythroughId") or ""
    if not re.match(r"^[0-9a-fA-F]{32}$", pid):
        die("no usable playthrough id (source manifest PlaythroughId=%r) - pass --playthrough." % man.get("PlaythroughId"))
    dest_pt = os.path.join(SAVEGAMES, MP_ROOT_NAME, ver, pid)
    dest = os.path.join(dest_pt, args.name)

    family = sorted(d for d in glob.glob(os.path.join(dest_pt, args.name + "*")) if os.path.isdir(d))
    if family:
        die("session family '%s*' already exists in this playthrough: %s. Pick another --name or "
            "delete those from the game's load screen first." % (args.name, ", ".join(os.path.basename(d) for d in family)))

    idmap = {args.host: to_host, args.client: to_client}
    plan = []          # (kind, path, note)
    plan.append(("MKDIR", dest, "session folder (this is what the load list shows as '%s')" % args.name))

    for old, new in idmap.items():
        s_dir = os.path.join(src, old)
        if not os.path.isdir(s_dir):
            die("source has no folder for '%s' (found: %s)" % (old, ", ".join(sorted(os.listdir(src)))))
        d_dir = os.path.join(dest, new)
        plan.append(("MKDIR", d_dir, "per-player folder RENAMED %s -> %s" % (old, new)))
        for f in sorted(os.listdir(s_dir)):
            if f.endswith(".hsg"):
                plan.append(("COPY-BYTES", os.path.join(d_dir, f), "from %s (%d bytes, UNCHANGED)"
                             % (os.path.join(old, f), os.path.getsize(os.path.join(s_dir, f)))))
            elif f.endswith(".hsg.meta"):
                plan.append(("REWRITE-META", os.path.join(d_dir, f), "characterId -> %s ; CharacterPath -> this folder "
                             "(the sidecar carries the reporter's REAL SteamID64 - the bundle redactor only "
                             "scrubs text entries, so this also removes it)" % new))
            else:
                plan.append(("SKIP", os.path.join(s_dir, f), "not part of a fixture"))
    for sc in SIDECARS:
        if os.path.exists(os.path.join(src, sc)):
            plan.append(("SKIP", os.path.join(src, sc), "per-machine cargo sidecar; its BaseSaveStamp belongs to the "
                                                        "reporter's timeline (tools\\fixture_reset.py deletes these anyway)"))

    # ---- manifest re-key -------------------------------------------------
    changes = []
    new_raw = raw
    for old, new in idmap.items():
        n = new_raw.count(old)
        new_raw = new_raw.replace(old, new)
        changes.append(("*every occurrence*", "%s -> %s (%d occurrences: Slots[].StableId/CharacterId, "
                        "BuildingOwners values, BuildingRealEstateOwners values, Merger[].StableId, "
                        "MergerWalletContributed[group][], ColourSlots keys, Grants Owner/Grantee, "
                        "Paperwork/CompanyBooks/Absence/Transfers/CargoTransfers entries AND the ids "
                        "embedded in their .Json payload strings, LastHostStableId)" % (old, new, n)))
    new_man = json.loads(new_raw)          # proves the re-key left valid JSON
    for s in new_man.get("Slots", []):
        if s.get("StableId") == to_host:
            changes.append(("Slots[host].DisplayName", "%r -> %r" % (s.get("DisplayName"), args.host_name)))
            changes.append(("Slots[host].IsHost", "%r -> True" % s.get("IsHost")))
            s["DisplayName"], s["IsHost"] = args.host_name, True
        elif s.get("StableId") == to_client:
            changes.append(("Slots[client].DisplayName", "%r -> %r" % (s.get("DisplayName"), args.client_name)))
            changes.append(("Slots[client].IsHost", "%r -> False" % s.get("IsHost")))
            s["DisplayName"], s["IsHost"] = args.client_name, False
    extra = [s.get("StableId") for s in new_man.get("Slots", []) if s.get("StableId") not in (to_host, to_client)]
    if extra:
        die("source manifest has slot(s) this import does not cover: %s (three-player save?)" % ", ".join(map(str, extra)))
    changes.append(("LastHostStableId", "%r -> %r" % (man.get("LastHostStableId"), to_host)))
    new_man["LastHostStableId"] = to_host
    changes.append(("SessionId", "%r -> a fresh 32-hex id (this is a NEW session in this store)" % man.get("SessionId")))
    new_man["SessionId"] = os.urandom(16).hex()
    changes.append(("PlaythroughId", "%r (KEPT - the imported world keeps its own identity)" % pid))
    changes.append(("GameVersion", "%r (KEPT - written only, never compared: src\\MPSaveCoordinator.cs:3789)"
                    % man.get("GameVersion")))
    plan.append(("WRITE-JSON", os.path.join(dest, MANIFEST), "%d key change group(s)" % len(changes)))

    print("fixture_import  source : %s" % src)
    print("                target : %s" % dest)
    print("                rig ids: host=%s client=%s   (from %s)" % (to_host, to_client, src_of_ids))
    print("                world  : day %s, playthrough %s, version folder %r"
          % (man.get("WorldDay"), pid, ver))
    print("")
    print("PATHS")
    for kind, p, note in plan:
        print("  %-12s %s\n               ^ %s" % (kind, p, note))
    print("")
    print("MANIFEST KEYS")
    for k, v in changes:
        print("  %-26s %s" % (k, v))
    print("")
    print("README: '%s' is an ordinary multiplayer save - it appears in the game's own MP load list "
          "next to save1 and can be loaded, renamed or deleted from there like any other. The rig loads "
          "it with `hostload %s` (host) plus a plain `join` (client); the host serves the client its own "
          "re-keyed .hsg. Imported from bug bundle %s. Nothing about it is hidden and nothing lives in "
          "the repo." % (args.name, args.name, os.path.basename(os.path.dirname(os.path.dirname(src))) or src))

    if args.dry_run:
        print("\n--dry-run: nothing was written.")
        return 0

    os.makedirs(dest, exist_ok=False)
    for old, new in idmap.items():
        s_dir, d_dir = os.path.join(src, old), os.path.join(dest, new)
        os.makedirs(d_dir, exist_ok=False)
        for f in sorted(os.listdir(s_dir)):
            if f.endswith(".hsg"):
                shutil.copyfile(os.path.join(s_dir, f), os.path.join(d_dir, f))
            elif f.endswith(".hsg.meta"):
                mj = read_json(os.path.join(s_dir, f))
                mj["characterId"] = new
                if "CharacterPath" in mj:
                    mj["CharacterPath"] = d_dir
                with open(os.path.join(d_dir, f), "w", encoding="utf-8", newline="\n") as fh:
                    json.dump(mj, fh, indent=1)
    with open(os.path.join(dest, MANIFEST), "w", encoding="utf-8", newline="\n") as fh:
        json.dump(new_man, fh, indent=2)
    print("\nWROTE %s" % dest)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
