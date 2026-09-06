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
import hashlib, os, re, subprocess, sys

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
        print("RESULT: FAIL - " + "; ".join(problems))
        return 1
    print("RESULT: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
