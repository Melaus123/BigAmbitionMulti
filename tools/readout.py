"""readout.py - turn a test run's logs into a short table (Agent economy, 2026-09-06).

One pass over the logs; for every pattern in the patterns file it prints the count per log and the first
matching line (number + text). Then generic scans: exceptions, patch summary, all [PROBE] lines, and the
mod's warning-shaped lines ("[Tag] something: message"). No interpretation - that is the manager's job.

Usage:  python C:\\code\\BigAmbitionsMP\\tools\\readout.py [--patterns FILE] [LOG ...]
Defaults: patterns = C:\\code\\BigAmbitionsMP\\.modding\\readout-patterns.txt;
          logs = the rig's Player.log and Player-instance2.log.
Patterns file: one per line  LABEL | regex   (lines starting with # are comments).
  A LABEL starting with '*' lists EVERY matching line (not just the count and the first one).
LOG may also be a bug-report bundle (.zip: its *.log files and peer-logs.txt are read), a folder (every
  .zip and .log under it, recursively) or a wildcard - so one call reads all bundles at once, e.g.
  python tools\\readout.py "C:\\path\\to\\bundles"
"""
import glob, os, re, sys, zipfile

DEFAULT_PATTERNS = r"C:\code\BigAmbitionsMP\.modding\readout-patterns.txt"
GAME_DIR = r"C:\Users\allsc\AppData\LocalLow\Hovgaard Games\Big Ambitions"
DEFAULT_LOGS = [os.path.join(GAME_DIR, "Player.log"), os.path.join(GAME_DIR, "Player-instance2.log")]
MAXLEN = 200


def load_patterns(path):
    pats = []
    for raw in open(path, encoding="utf-8", errors="replace"):
        line = raw.strip()
        if not line or line.startswith("#") or "|" not in line:
            continue
        label, rx = line.split("|", 1)
        pats.append((label.strip(), re.compile(rx.strip())))
    return pats


def expand_logs(args):
    """(display name, loader) per log: plain files, bundle zips (their logs), folders, wildcards."""
    out = []

    def add_zip(zp):
        try:
            with zipfile.ZipFile(zp) as z:
                for n in z.namelist():
                    b = n.rsplit("/", 1)[-1].lower()
                    if b.endswith(".log") or b == "peer-logs.txt":
                        out.append((f"{zp}!{n}", lambda zp=zp, n=n: zipfile.ZipFile(zp).read(n)))
        except Exception as ex:
            out.append((f"{zp}: unreadable zip ({ex})", None))

    def add(path):
        if os.path.isdir(path):
            for root, _dirs, files in os.walk(path):
                for f in sorted(files):
                    fp = os.path.join(root, f)
                    if f.lower().endswith(".zip"):
                        add_zip(fp)
                    elif f.lower().endswith(".log") or f.lower() == "peer-logs.txt":
                        out.append((fp, lambda fp=fp: open(fp, "rb").read()))
        elif path.lower().endswith(".zip") and os.path.exists(path):
            add_zip(path)
        else:
            out.append((path, (lambda fp=path: open(fp, "rb").read()) if os.path.exists(path) else None))

    for a in args:
        matches = sorted(glob.glob(a)) if any(c in a for c in "*?[") else [a]
        for m in matches or [a]:
            add(m)
    return out


def scan(data, pats):
    lines = data.decode("utf-8", "replace").splitlines()
    hits = {label: [0, None, []] for label, _ in pats}
    generic = {"Exception": [0, None], "Patch summary": [0, None], "mod load line": [0, None],
               "PROBE": [], "warning-shaped mod lines": {}}
    warn_rx = re.compile(r"\[BAMP\] \[[A-Za-z]+\] [^:]{3,60}: ")
    for i, line in enumerate(lines, 1):
        for label, rx in pats:
            if rx.search(line):
                h = hits[label]
                h[0] += 1
                if h[1] is None:
                    h[1] = (i, line.strip()[:MAXLEN])
                if label.startswith("*"):
                    h[2].append((i, line.strip()[:MAXLEN * 2]))
        if "Exception" in line:
            generic["Exception"][0] += 1
            if generic["Exception"][1] is None:
                generic["Exception"][1] = (i, line.strip()[:MAXLEN])
        if "Patch summary:" in line:
            generic["Patch summary"][0] += 1
            generic["Patch summary"][1] = (i, line.strip()[:MAXLEN])
        if "loaded. Canvas UI active" in line:
            generic["mod load line"][0] += 1
            generic["mod load line"][1] = (i, line.strip()[:MAXLEN])
        if "[PROBE]" in line:
            generic["PROBE"].append((i, line.strip()[:MAXLEN]))
        m = warn_rx.search(line)
        if m and "[PROBE]" not in line:
            key = m.group(0)[-60:]
            d = generic["warning-shaped mod lines"]
            d.setdefault(key, [0, None])
            d[key][0] += 1
            if d[key][1] is None:
                d[key][1] = (i, line.strip()[:MAXLEN])
    return len(lines), hits, generic


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")   # the console is cp1252; log lines carry arrows and dashes
    except Exception:
        pass
    args = sys.argv[1:]
    pat_path = DEFAULT_PATTERNS
    if "--patterns" in args:
        k = args.index("--patterns"); pat_path = args[k + 1]; del args[k:k + 2]
    logs = args or DEFAULT_LOGS
    pats = load_patterns(pat_path)
    for log, load in expand_logs(logs):
        if load is None:
            print(f"## {log}: MISSING"); continue
        n, hits, generic = scan(load(), pats)
        print(f"## {log}  ({n} lines)")
        for key in ("mod load line", "Patch summary", "Exception"):
            c, first = generic[key]
            print(f"  [{key}] count={c}" + (f"  first L{first[0]}: {first[1]}" if first else ""))
        print("  -- patterns:")
        for label, _ in pats:
            c, first, every = hits[label]
            if label.startswith("*"):
                print(f"  {label}: {c}")
                for i, text in every:
                    print(f"     L{i}: {text}")
                continue
            print(f"  {label}: {c}" + (f"  L{first[0]}: {first[1]}" if first else ""))
        print(f"  -- PROBE lines: {len(generic['PROBE'])}")
        seen = set()
        for i, text in generic["PROBE"]:
            shape = text.split("]")[1][:40] if "]" in text else text[:40]
            if shape in seen:
                continue
            seen.add(shape)
            print(f"     L{i}: {text}")
        print("  -- warning-shaped mod lines (by shape):")
        for key, (c, first) in sorted(generic["warning-shaped mod lines"].items(), key=lambda kv: -kv[1][0])[:25]:
            print(f"     {c}x  L{first[0]}: {first[1]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
