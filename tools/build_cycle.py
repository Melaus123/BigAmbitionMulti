"""build_cycle.py - the whole build gate in one call (Agent economy, 2026-09-06).

Runs Debug, Release, then Dev LAST (the csproj deploys the last build), reports errors/warnings per
configuration, the Dev vs deployed md5, the 'DEV build' marker count, and the line-ending state of every
changed source file (before = HEAD version, after = working tree). Refuses to build while the game or
another MSBuild is running. Never commits, never touches records.

Usage (from anywhere):  python C:\\code\\BigAmbitionsMP\\tools\\build_cycle.py [--skip-game-check] [--over-release]

RELEASE HOLD (user decision 2026-09-06, after the second dev-build Workshop leak): when the deployed DLL carries
NO "DEV build" marker, a RELEASE build is sitting in the game folder - which is the folder the user uploads to the
Steam Workshop from. This script then REFUSES to build (it would overwrite it with the Dev build) unless
--over-release is passed, which only the manager passes, and only after the user has confirmed the Workshop upload.
Exit code 0 = all three builds clean AND md5 match AND marker >= 1; 1 otherwise.
"""
import hashlib, os, re, shutil, subprocess, sys

ROOT = r"C:\code\BigAmbitionsMP"
CSPROJ = os.path.join(ROOT, "BigAmbitionsMP.csproj")
DEV_DLL = os.path.join(ROOT, r"bin\Dev\net48\BigAmbitionsMP.dll")
DEPLOYED = r"C:\Users\allsc\AppData\LocalLow\Hovgaard Games\Big Ambitions\ModsLocal\BigAmbitionsMP\BigAmbitionsMP.dll"
CONFIGS = ["Debug", "Release", "Dev"]


def run(cmd, cwd=None):
    r = subprocess.run(cmd, cwd=cwd, capture_output=True, shell=isinstance(cmd, str))
    return r.returncode, r.stdout.decode("utf-8", "replace"), r.stderr.decode("utf-8", "replace")


def process_running(name):
    rc, out, _ = run(["tasklist", "/FI", f"IMAGENAME eq {name}"])
    return name.lower() in out.lower()


def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def marker_count(path):
    data = open(path, "rb").read().replace(b"\x00", b"")
    return data.count(b"DEV build")


def endings(data: bytes):
    crlf = data.count(b"\r\n")
    lf = data.count(b"\n") - crlf
    cr = data.count(b"\r") - crlf
    return crlf, lf, cr


def changed_src_files():
    rc, out, _ = run(["git", "-C", ROOT, "status", "--short", "--", "src"])
    files = []
    for line in out.splitlines():
        if len(line) > 3:
            files.append((line[:2].strip(), line[3:].strip()))
    return files


def main():
    skip_game = "--skip-game-check" in sys.argv
    problems = []
    if not skip_game and process_running("Big Ambitions.exe"):
        print("REFUSED: the game is running - never build under a live game.")
        return 1
    if process_running("MSBuild.exe"):
        print("REFUSED: another MSBuild is running (one build at a time on the shared tree).")
        return 1
    if os.path.exists(DEPLOYED) and marker_count(DEPLOYED) == 0 and "--over-release" not in sys.argv:
        print(f"REFUSED: RELEASE HOLD - the deployed DLL ({md5(DEPLOYED)}) has no DEV marker, so a RELEASE build sits in the game"
              " folder (the Workshop upload source). Building would overwrite it with the Dev build. Pass --over-release ONLY"
              " after the user has confirmed the Workshop upload (02-project-rules.md: Post-release hold).")
        return 1

    # Backup/restore (user decision 2026-09-11, after F-2026-09-11-D): the csproj deploys EVERY configuration in
    # order Debug, Release, Dev, so a cycle that fails AFTER a successful Release step leaves a RELEASE DLL in the
    # game folder (0 DEV markers) and the next cycle refuses with a false RELEASE HOLD. Keep a copy of whatever was
    # deployed before this cycle and put it back whenever the cycle does not end in a verified Dev deploy.
    # The copy lives OUTSIDE the mod folder (user rule 2026-09-11: nothing test-only may travel with a Workshop
    # upload, and ModsLocal\BigAmbitionsMP IS the upload source) - under the repo's gitignored .modding\work\build\ folder (LOCAL-FOLDER-1: local\ holds only the user's launch .bat files).
    backup = None
    if os.path.exists(DEPLOYED):
        backup = os.path.join(ROOT, ".modding", "work", "build", "deployed-prebuild.dll")
        os.makedirs(os.path.dirname(backup), exist_ok=True)
        shutil.copy2(DEPLOYED, backup)
        print(f"== pre-build deployed DLL saved ({md5(DEPLOYED)}, DEV markers {marker_count(DEPLOYED)}) -> {os.path.basename(backup)}")

    files = changed_src_files()
    print("== changed src files:", ", ".join(f"{s} {p}" for s, p in files) or "(none)")
    for status, path in files:
        full = os.path.join(ROOT, path)
        after = endings(open(full, "rb").read()) if os.path.exists(full) else None
        before = None
        if status != "??":
            rc, head, _ = run(["git", "-C", ROOT, "show", f"HEAD:{path}"])
            # git show emits the blob verbatim (binary-safe enough for counting)
            rc2 = subprocess.run(["git", "-C", ROOT, "show", f"HEAD:{path}"], capture_output=True)
            before = endings(rc2.stdout)
        # core.autocrlf=true: git stores blobs LF-normalised and checks them out CRLF, so HEAD-vs-working
        # style comparison is meaningless (a build agent proved it on 2026-09-06). The gate is: the working
        # file must not be MIXED (both CRLF and bare LF present) - that is what a wrong-ending edit produces.
        print(f"   {path}: HEAD-blob CRLF/LF/CR={before} (LF-normalised by git)  working CRLF/LF/CR={after}")
        if after and after[0] > 0 and after[1] > 0:
            problems.append(f"MIXED line endings in {path} (CRLF {after[0]}, bare LF {after[1]})")

    summary = []
    for cfg in CONFIGS:
        rc, out, err = run(["dotnet", "build", CSPROJ, "-c", cfg, "-t:Rebuild", "-v", "q", "-nologo"], cwd=ROOT)
        text = out + err
        errors = len(re.findall(r": error ", text)) + text.count("Build FAILED")
        m = re.search(r"(\d+) Warning\(s\)", text)
        warnings = int(m.group(1)) if m else -1
        summary.append((cfg, rc, errors, warnings))
        print(f"== {cfg}: exit {rc}, errors {errors}, warnings {warnings}")
        # CROSS-HR-3c (2026-09-12): a count alone cannot NAME a new warning (three reviews guessed at one).
        # Every distinct warning line goes to local/build-warnings-<cfg>.txt (gitignored) and the per-code
        # tally is printed, so a build that adds one can be answered from the file, not from a rebuild.
        try:
            seen = []
            for line in text.splitlines():
                mm = re.search(r"(\S+\(\d+,\d+\)): warning (CS\d+): (.*?)(?: \[[^\]]*\])?\s*$", line)
                if mm:
                    entry = f"{mm.group(1)} {mm.group(2)} {mm.group(3)}"
                    if entry not in seen: seen.append(entry)
            os.makedirs(os.path.join(ROOT, ".modding", "work", "build"), exist_ok=True)
            with open(os.path.join(ROOT, ".modding", "work", "build", f"build-warnings-{cfg}.txt"), "w", encoding="utf-8") as wf:
                wf.write("\n".join(seen) + ("\n" if seen else ""))
            tally = {}
            for e in seen: tally[e.split(" ")[1]] = tally.get(e.split(" ")[1], 0) + 1
            print("   warning codes: " + ", ".join(f"{k} x{v}" for k, v in sorted(tally.items(), key=lambda kv: -kv[1])) + f"  (distinct {len(seen)}; full list in .modding/work/build/build-warnings-{cfg}.txt)")
        except Exception as wx:
            print(f"   (warning list not written: {wx})")
        if errors or rc != 0:
            for line in text.splitlines():
                if ": error " in line or "Build FAILED" in line:
                    print("   " + line.strip()[:220])
            problems.append(f"{cfg} build failed")
            break

    if os.path.exists(DEV_DLL) and os.path.exists(DEPLOYED):
        a, b = md5(DEV_DLL), md5(DEPLOYED)
        mk = marker_count(DEPLOYED)
        print(f"== md5 bin/Dev {a}\n== md5 deployed {b}  match={a == b}\n== DEV marker count {mk}")
        if a != b:
            problems.append("deployed md5 differs from bin/Dev")
        if mk < 1:
            problems.append("DEV marker missing in the deployed DLL")
    else:
        problems.append("Dev DLL or deployed DLL missing")

    if problems:
        if backup and os.path.exists(backup):
            shutil.copy2(backup, DEPLOYED)
            print(f"== RESTORED the pre-build DLL into the game folder ({md5(DEPLOYED)}, DEV markers {marker_count(DEPLOYED)})"
                  " - a failed cycle never leaves a half-deployed configuration behind")
        print("RESULT: FAIL - " + "; ".join(problems))
        return 1
    print("RESULT: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
