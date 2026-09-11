"""rigrun.py - scenario driver for the two-instance MP test rig (Agent economy, 2026-09-10).

Runs a JSON scenario end to end: pre-flight, arm the TestDrive file-drop channel, launch the
scenario's instances (top-level "instances": 2 or 3, default 2) with local\\launch-mp-test.bat, wait for each instance's ARMED line, send the scenario's
commands as <role>-<seq>.cmd files, match each .result, grep each role's log from that role's last
mark, then write a verdict report to local\\runs\\.

EVENTS OVER TIMERS: nothing ever "waits N seconds for state" - every wait is for a concrete event
(a .result file, a log line) with a deadline, and a deadline that expires is a loud logged FAILURE.
The only sleeps are steps that explicitly declare "sleep_s" (a scenario saying "now do nothing for
N s", e.g. the 120 s heartbeat window).

Usage:
  python tools/rigrun.py tools/scenarios/t-p0-merger.json --session "TESTBATCH-0813"
  python tools/rigrun.py <scenario.json> [--session NAME] [--var k=v ...] [--keep-open]
  python tools/rigrun.py <scenario.json> --dry-run     # parse + print the step table, exit 0
  python tools/rigrun.py --selftest                    # regex/capture machinery only, exit 0/1

--dry-run and --selftest touch no process, no game folder and no channel.

Rules this script encodes (from .modding/08-testdrive.md - the notes win over anything else):
  * launcher: local\\launch-mp-test.bat is THE launcher; never bare-start the Steam exe.
  * freshness: a failed launch does not recreate a log. Gate on mtime > launch time AND on log
    IDENTITY (exactly one "channel ARMED" line; this run's marker present) - the stale-log trap.
  * client logs Player-instance2/3.log truncate on relaunch with no -prev sibling: snapshot first.
  * grep mod lines on the emitter prefix "[BAMP] [Tag]", never a bare tag.
  * teardown via CloseMainWindow (WM_CLOSE), not taskkill /f - it exercises the real quit path;
    taskkill by PID is the fallback only. Never kill by image name (that kills both instances).
  * focus by WINDOW HANDLE, never PID/title, and only for the ONE sanctioned boot-focus flick.
"""

import argparse
import ctypes
import ctypes.wintypes as wt
import datetime
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time

ROOT = r"C:\code\BigAmbitionsMP"
LOCALLOW = r"C:\Users\allsc\AppData\LocalLow\Hovgaard Games\Big Ambitions"
DEPLOYED = os.path.join(LOCALLOW, r"ModsLocal\BigAmbitionsMP\BigAmbitionsMP.dll")
CHANNEL = os.path.join(LOCALLOW, r"BigAmbitionsMP\testdrive")
ROLES = ["h", "c", "d"]                       # h = Steam install (host), c/d = the client installs
INSTALLS = {"c": r"C:\BigAmbitions2", "d": r"C:\BigAmbitions3"}   # role by install path; else "h"
LOGS = {"h": os.path.join(LOCALLOW, "Player.log"),
        "c": os.path.join(LOCALLOW, "Player-instance2.log"),
        "d": os.path.join(LOCALLOW, "Player-instance3.log")}
LAUNCHER = os.path.join(ROOT, r"local\launch-mp-test.bat")
RUNS = os.path.join(ROOT, r"local\runs")
GAME_EXE = "Big Ambitions.exe"

ARMED_RE = {r: re.compile(r"\[BAMP\] \[TestDrive\] channel ARMED \(dev build, role '%s'\)" % r) for r in ROLES}
RESULT_TIMEOUT_S = 60.0
POLL_S = 0.5
DEFAULT_WITHIN_S = 10.0
BOOT_FREEZE_S = 60.0
BOOT_FREEZE_LINES = 40
ROLE_NAME = {"h": "host", "c": "client", "d": "client2"}


# ---------------------------------------------------------------- small helpers

def now():
    return time.time()


def stamp(t=None):
    return datetime.datetime.fromtimestamp(t or now()).strftime("%Y-%m-%d %H:%M:%S")


def say(msg):
    print("[%s] %s" % (stamp(), msg), flush=True)


def run(cmd):
    r = subprocess.run(cmd, capture_output=True, shell=isinstance(cmd, str))
    return r.returncode, r.stdout.decode("utf-8", "replace"), r.stderr.decode("utf-8", "replace")


def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def dev_marker_count(path):
    return open(path, "rb").read().replace(b"\x00", b"").count(b"DEV build")


def head_commit():
    rc, out, _ = run(["git", "-C", ROOT, "rev-parse", "--short", "HEAD"])
    return out.strip() or "(unknown)"


def image_running(name):
    rc, out, _ = run(["tasklist", "/FI", "IMAGENAME eq %s" % name])
    return name.lower() in out.lower()


def build_running():
    """A LIVE build, not a leftover build server.

    MSBuild node reuse and the Roslyn compiler server leave `dotnet.exe` (VBCSCompiler.dll)
    alive for a long time after a build finishes - measured on this machine 2026-09-10 - so a
    bare 'is dotnet.exe running' test refuses forever. Gate on the command line instead.
    """
    if image_running("MSBuild.exe"):
        return "MSBuild.exe is running (a build is in progress)"
    rc, out, _ = run(["powershell", "-NoProfile", "-Command",
                      "(Get-CimInstance Win32_Process -Filter \"Name='dotnet.exe'\").CommandLine"])
    for line in out.splitlines():
        low = line.lower()
        if "vbcscompiler" in low or "nodemode" in low:
            continue          # idle compiler/build server, not a build
        if re.search(r"\bbuild\b|\bmsbuild\.dll\b", low):
            return "a dotnet build is running: %s" % line.strip()[:120]
    return None


# ---------------------------------------------------------------- log reading
# The game holds Player*.log open for writing. Plain Python open() on Windows uses the CRT's
# _SH_DENYNO share mode, so it does not deny the writer and is not denied by it; the ctypes
# CreateFileW path below is the explicit FILE_SHARE_READ|WRITE|DELETE fallback in case a build
# ever opens the log more restrictively.

_k32 = ctypes.WinDLL("kernel32", use_last_error=True)
_u32 = ctypes.WinDLL("user32", use_last_error=True)


def read_bytes_shared(path, offset=0):
    """Read a live log from `offset` to EOF; returns b'' when the file is absent."""
    if not os.path.exists(path):
        return b""
    try:
        with open(path, "rb") as f:
            f.seek(offset)
            return f.read()
    except PermissionError:
        GENERIC_READ, OPEN_EXISTING = 0x80000000, 3
        SHARE_ALL = 0x1 | 0x2 | 0x4
        _k32.CreateFileW.restype = wt.HANDLE
        h = _k32.CreateFileW(path, GENERIC_READ, SHARE_ALL, None, OPEN_EXISTING, 0x80, None)
        if h == wt.HANDLE(-1).value:
            return b""
        try:
            buf, out, got = ctypes.create_string_buffer(1 << 20), [], wt.DWORD(0)
            _k32.SetFilePointerEx(h, ctypes.c_longlong(offset), None, 0)
            while _k32.ReadFile(h, buf, len(buf), ctypes.byref(got), None) and got.value:
                out.append(buf.raw[: got.value])
            return b"".join(out)
        finally:
            _k32.CloseHandle(h)


def read_text_from(path, offset=0):
    return read_bytes_shared(path, offset).decode("utf-8", "replace")


def log_size(path):
    try:
        return os.path.getsize(path)
    except OSError:
        return 0


def log_mtime(path):
    try:
        return os.path.getmtime(path)
    except OSError:
        return 0.0


# ---------------------------------------------------------------- processes / windows

def game_processes():
    """[(pid, path, role)] for every running Big Ambitions.exe, role by install PATH."""
    psapi = ctypes.WinDLL("psapi", use_last_error=True)
    arr = (wt.DWORD * 4096)()
    got = wt.DWORD()
    if not psapi.EnumProcesses(ctypes.byref(arr), ctypes.sizeof(arr), ctypes.byref(got)):
        return []
    out = []
    for pid in arr[: got.value // ctypes.sizeof(wt.DWORD)]:
        if not pid:
            continue
        h = _k32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not h:
            continue
        try:
            buf, size = ctypes.create_unicode_buffer(32768), wt.DWORD(32768)
            if _k32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size)):
                path = buf.value
                if os.path.basename(path).lower() == GAME_EXE.lower():
                    role = "h"
                    for rl, inst in INSTALLS.items():
                        if path.lower().startswith(inst.lower()):
                            role = rl
                            break
                    out.append((pid, path, role))
        finally:
            _k32.CloseHandle(h)
    return out


def windows_of_pid(pid):
    hwnds = []
    CB = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)

    def cb(hwnd, _):
        p = wt.DWORD()
        _u32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value == pid and _u32.IsWindowVisible(hwnd):
            hwnds.append(hwnd)
        return True

    _u32.EnumWindows(CB(cb), 0)
    return hwnds


def boot_focus_flick(role, report):
    """The ONE sanctioned focus steal (08-testdrive.md 'No-takeover exception'), by WINDOW HANDLE."""
    target = [p for p in game_processes() if p[2] == role]
    if not target:
        say("FLICK %s: no process found - cannot flick" % ROLE_NAME[role])
        return False
    hwnds = windows_of_pid(target[0][0])
    if not hwnds:
        say("FLICK %s: process %d has no visible window yet" % (ROLE_NAME[role], target[0][0]))
        return False
    hwnd, prev = hwnds[0], _u32.GetForegroundWindow()
    say("FLICK %s (announced per 08-testdrive.md): hwnd=%s pid=%d" % (ROLE_NAME[role], hwnd, target[0][0]))
    _u32.keybd_event(0x12, 0, 0, 0)   # ALT down - defeats the foreground lock
    _u32.keybd_event(0x12, 0, 2, 0)   # ALT up
    _u32.SetForegroundWindow(hwnd)
    time.sleep(0.4)
    ok = _u32.GetForegroundWindow() == hwnd
    if prev:
        _u32.SetForegroundWindow(prev)
    report.append("FLICK %s at %s (verified=%s, foreground restored)" % (ROLE_NAME[role], stamp(), ok))
    say("FLICK %s verified=%s; previous foreground restored" % (ROLE_NAME[role], ok))
    return ok


def close_instances(report):
    """Teardown the way the notes require: WM_CLOSE (the real quit path), taskkill by PID last."""
    procs = game_processes()
    if not procs:
        return
    for pid, path, role in procs:
        say("teardown: WM_CLOSE %s pid=%d (%s)" % (ROLE_NAME[role], pid, path))
        for hwnd in windows_of_pid(pid):
            _u32.PostMessageW(hwnd, 0x0010, 0, 0)  # WM_CLOSE == CloseMainWindow
    deadline = now() + 120
    while now() < deadline and game_processes():
        time.sleep(1.0)
    left = game_processes()
    for pid, path, role in left:
        say("teardown: %s pid=%d survived WM_CLOSE - taskkill by PID (never by image name)" % (ROLE_NAME[role], pid))
        run(["taskkill", "/PID", str(pid), "/F"])
        report.append("teardown: %s pid=%d needed taskkill /PID after WM_CLOSE" % (ROLE_NAME[role], pid))


# ---------------------------------------------------------------- scenario machinery

VAR_RE = re.compile(r"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")


class Unresolved(Exception):
    pass


def subst(text, vars_, escape=False):
    """${var} substitution. Pass escape for REGEX fields (expect/refute/log patterns): captured values are
    literal data (an employee id can start with '+', addresses hold '.'), so they are re.escape'd there -
    run T-P0-2 2026-09-10 died on a '+lRSN...' id used as a pattern (nothing to repeat)."""
    if text is None:
        return None

    def one(m):
        k = m.group(1)
        if k not in vars_ or vars_[k] is None:
            raise Unresolved(k)
        v = str(vars_[k])
        return re.escape(v) if escape else v

    return VAR_RE.sub(one, text)


def apply_captures(capture, result_text, vars_):
    """A step's capture map: regex -> first group of the match against the step's .result text."""
    got = {}
    for name, pattern in (capture or {}).items():
        pattern = subst(pattern, vars_, escape=True)   # T-P1-3 run 2 lesson: captures carried a raw ${dPid} into re.search
        m = re.search(pattern, result_text)
        if not m:
            return None, "capture '%s' did not match /%s/" % (name, pattern)
        got[name] = (m.group(1) if m.groups() else m.group(0)).strip()
    vars_.update(got)
    return got, None


def load_scenario(path):
    with open(path, "r", encoding="utf-8") as f:
        sc = json.load(f)
    steps = sc.get("steps") or []
    inst = sc.get("instances", 2)
    if inst not in (2, 3):
        raise ValueError('"instances" must be 2 or 3 (absent = 2, the old two-instance rig)')
    active = ROLES[:inst]
    for i, s in enumerate(steps, 1):
        s.setdefault("seq", i)
        if s.get("role") not in tuple(ROLES) + ("both", "all"):
            raise ValueError("step %d: role must be %s|both|all" % (i, "|".join(ROLES)))
        if s.get("role") in ROLES and s["role"] not in active:
            raise ValueError("step %d: role '%s' needs instances=%d"
                             % (i, s["role"], ROLES.index(s["role"]) + 1))
        if not s.get("cmd") and not s.get("sleep_s"):
            raise ValueError("step %d: needs cmd or sleep_s" % i)
    return sc


# ---------------------------------------------------------------- the runner

class Run:
    def __init__(self, sc, args):
        self.sc, self.args = sc, args
        self.vars = dict(sc.get("vars") or {})
        if args.session:
            self.vars["session"] = args.session
        for kv in args.var or []:
            k, _, v = kv.partition("=")
            self.vars[k.strip()] = v
        self.rows, self.notes, self.seq = [], [], 0
        self.instances = int(sc.get("instances", 2) or 2)
        self.active = ROLES[: self.instances]           # 2 -> h,c (unchanged); 3 -> h,c,d
        self.mark_off = {r: 0 for r in ROLES}
        self.run_start_off = {r: 0 for r in ROLES}
        self.flicked = {r: False for r in ROLES}
        self.launch_t = None
        self.id = sc.get("id", "run")
        self.rundir = os.path.join(RUNS, "%s-%s" % (self.id, datetime.datetime.now().strftime("%Y%m%d-%H%M%S")))

    # ---- channel
    def send(self, role, cmd):
        self.seq += 1
        base = "%s-%03d.cmd" % (role, self.seq)
        p = os.path.join(CHANNEL, base)
        with open(p, "w", encoding="utf-8", newline="\n") as f:
            f.write(cmd + "\n")
        say("-> %s: %s  (%s)" % (ROLE_NAME[role], cmd, base))
        deadline = now() + RESULT_TIMEOUT_S
        rp = p + ".result"
        while now() < deadline:
            if os.path.exists(rp):
                time.sleep(0.05)
                try:
                    txt = open(rp, "r", encoding="utf-8", errors="replace").read().strip()
                except OSError:
                    time.sleep(POLL_S)
                    continue
                say("<- %s: %s" % (ROLE_NAME[role], txt.splitlines()[0][:160] if txt else "(empty)"))
                return txt, None
            time.sleep(POLL_S)
        msg = "TIMEOUT: no %s.result after %.0fs (instance not polling the channel, or the verb hung)" % (
            base, RESULT_TIMEOUT_S)
        say("FAIL " + msg)
        return "", msg

    def mark(self, role, text):
        size = log_size(LOGS[role])
        res, err = self.send(role, "mark " + text)
        if err:
            return err
        deadline = now() + DEFAULT_WITHIN_S
        pat = re.compile(r"\[BAMP\] \[TestDrive\].*MARK .*" + re.escape(text))
        while now() < deadline:
            if pat.search(read_text_from(LOGS[role], size)):
                # bracket from the byte offset captured BEFORE the mark was sent: strictly at or
                # before the mark line, so a step's expect_log can never miss its own evidence.
                self.mark_off[role] = size
                return None
            time.sleep(POLL_S)
        self.mark_off[role] = size
        return "mark '%s' never appeared in the %s log within %.0fs" % (text, ROLE_NAME[role], DEFAULT_WITHIN_S)

    def wait_log(self, role, pattern, within_s):
        deadline = now() + within_s
        rx = re.compile(pattern)
        while True:
            text = read_text_from(LOGS[role], self.mark_off[role])
            for line in text.splitlines():
                if rx.search(line):
                    return line.strip()[:200], None
            if now() >= deadline:
                return "", "TIMEOUT: %s log never showed /%s/ within %.0fs (searched from this step's mark)" % (
                    ROLE_NAME[role], pattern, within_s)
            time.sleep(POLL_S)

    # ---- pre-flight
    def preflight(self):
        problems = []
        if image_running(GAME_EXE):
            problems.append("%s is already running - the rig must start from a cold pair" % GAME_EXE)
        b = build_running()
        if b:
            problems.append("never run the rig over a build - " + b)
        if not os.path.exists(DEPLOYED):
            problems.append("deployed DLL missing: %s" % DEPLOYED)
        else:
            mk = dev_marker_count(DEPLOYED)
            if mk == 0:
                problems.append("deployed DLL has 0 'DEV build' markers - the command channel is dev-only "
                                "(a Release build is in the mod slot)")
            self.deployed_md5, self.markers = md5(DEPLOYED), mk
        if not os.path.exists(LAUNCHER):
            problems.append("launcher missing: %s" % LAUNCHER)
        self.head = head_commit()
        return problems

    def arm_channel(self):
        os.makedirs(CHANNEL, exist_ok=True)
        stale = [f for f in os.listdir(CHANNEL) if f.endswith(".cmd") or f.endswith(".result")]
        for f in stale:
            try:
                os.remove(os.path.join(CHANNEL, f))
            except OSError:
                pass
        if stale:
            self.notes.append("cleared %d stale channel file(s) before arming" % len(stale))
        os.makedirs(self.rundir, exist_ok=True)
        # a client log truncates on relaunch with no -prev sibling: snapshot EVERY one that exists
        for role in ROLES[1:]:
            if os.path.exists(LOGS[role]):
                shutil.copy2(LOGS[role], os.path.join(
                    self.rundir, "pre-launch-%s-%s" % (ROLE_NAME[role], os.path.basename(LOGS[role]))))
        self.pre_mtime = {r: log_mtime(LOGS[r]) for r in ROLES}

    def launch(self):
        self.launch_t = now()
        say("launching %d instance(s) [%s] via %s" % (self.instances, ",".join(self.active), LAUNCHER))
        subprocess.Popen(["cmd", "/c", "start", "", "/D", os.path.dirname(LAUNCHER), LAUNCHER,
                          str(self.instances)], creationflags=0x00000008)
        self.notes.append("launch at %s via local\\launch-mp-test.bat %d" % (stamp(self.launch_t), self.instances))

    def wait_armed(self, role, timeout_s=300.0):
        """Freshness: mtime newer than launch. Identity: exactly one ARMED line for this role."""
        path = LOGS[role]
        deadline, last_size, last_growth = now() + timeout_s, -1, now()
        while now() < deadline:
            if log_mtime(path) > self.launch_t:
                text = read_text_from(path, 0)
                hits = ARMED_RE[role].findall(text)
                if len(hits) == 1:
                    self.run_start_off[role] = 0
                    self.mark_off[role] = 0
                    say("%s ARMED (fresh log, exactly one ARMED line)" % ROLE_NAME[role])
                    return None
                if len(hits) > 1:
                    return ("%s log carries %d 'channel ARMED' lines - that is a stale/shared log, not this "
                            "run's (08-testdrive.md stale-log trap)" % (ROLE_NAME[role], len(hits)))
                size, lines = log_size(path), text.count("\n")
                if size != last_size:
                    last_size, last_growth = size, now()
                elif (now() - last_growth) > BOOT_FREEZE_S and lines < BOOT_FREEZE_LINES and not self.flicked[role]:
                    self.flicked[role] = True
                    boot_focus_flick(role, self.notes)
                    last_growth = now()
            time.sleep(1.0)
        return "%s never logged its ARMED line within %.0fs of launch (log mtime fresh=%s)" % (
            ROLE_NAME[role], timeout_s, log_mtime(path) > self.launch_t)

    # ---- steps
    def roles_of(self, step):
        if step["role"] == "both":
            return ["h", "c"]              # unchanged: host + first client
        if step["role"] == "all":
            return list(self.active)       # every ACTIVE role of this scenario
        return [step["role"]]

    def do_step(self, step):
        seq, verdicts = step["seq"], []
        if not step.get("cmd"):
            say("step %d: declared wait of %ss (%s)" % (seq, step["sleep_s"], step.get("note", "")))
            time.sleep(float(step["sleep_s"]))
            self.rows.append((seq, step["role"], "(wait %ss)" % step["sleep_s"], "-", "declared wait", "PASS", ""))
            return True
        ok_all = True
        for role in self.roles_of(step):
            try:
                cmd = subst(step["cmd"], self.vars)
                exp = subst(step.get("expect_result"), self.vars, escape=True)
                refute = subst(step.get("refute_result"), self.vars, escape=True)
            except Unresolved as u:
                self.rows.append((seq, role, step["cmd"], "-", step.get("expect_result", ""), "FAIL",
                                  "unresolved variable ${%s} (pass --var %s=... or fix the capture)" % (u, u)))
                say("FAIL step %d: unresolved variable ${%s}" % (seq, u))
                return False
            err = self.mark(role, "TEST %s STEP %d" % (self.id, seq))
            if err:
                self.notes.append("step %d (%s): %s" % (seq, role, err))
            res, terr = self.send(role, cmd)
            verdict, evidence = "PASS", ""
            if terr:
                verdict, evidence = "FAIL", terr
            elif exp and not re.search(exp, res, re.S):
                verdict, evidence = "FAIL", "result did not match /%s/" % exp
            elif refute and re.search(refute, res, re.S):
                verdict, evidence = "FAIL", "result still matches the refuted /%s/" % refute
            if verdict == "PASS" and step.get("capture"):
                got, cerr = apply_captures(step["capture"], res, self.vars)
                if cerr:
                    verdict, evidence = "FAIL", cerr
                else:
                    evidence = "captured " + ", ".join("%s=%s" % kv for kv in got.items())
            if verdict == "PASS":
                for el in step.get("expect_log") or []:
                    lrole = el.get("role", role)
                    try:
                        pat = subst(el["regex"], self.vars, escape=True)
                    except Unresolved as u:
                        verdict, evidence = "FAIL", "expect_log has unresolved ${%s}" % u
                        break
                    line, lerr = self.wait_log(lrole, pat, float(el.get("within_s", DEFAULT_WITHIN_S)))
                    if lerr:
                        verdict, evidence = "FAIL", lerr
                        break
                    evidence = (evidence + " | " if evidence else "") + "%s: %s" % (ROLE_NAME[lrole], line)
            self.rows.append((seq, role, cmd, res.splitlines()[0][:160] if res else "-",
                              exp or refute or "(no result assertion)", verdict, evidence[:200]))
            if verdict == "FAIL":
                ok_all = False
                say("FAIL step %d (%s): %s" % (seq, ROLE_NAME[role], evidence))
        return ok_all

    def oracles(self):
        """Whole-log absence greps (FAIL if present) + the notes' passive greps (report only)."""
        out = []
        for pat in self.sc.get("oracles_absent") or []:
            for role in self.active:
                hits = [l.strip()[:200] for l in read_text_from(LOGS[role], self.run_start_off[role]).splitlines()
                        if re.search(pat, l)]
                if hits:
                    out.append(("FAIL", "%s: /%s/ x%d -> %s" % (ROLE_NAME[role], pat, len(hits), hits[0])))
        for pat in self.sc.get("oracles_report") or []:
            for role in self.active:
                hits = [l.strip()[:200] for l in read_text_from(LOGS[role], self.run_start_off[role]).splitlines()
                        if re.search(pat, l)]
                if hits:
                    out.append(("REPORT", "%s: /%s/ x%d -> %s" % (ROLE_NAME[role], pat, len(hits), hits[0])))
        return out

    # ---- report
    def write_report(self, passed, total, oracle_rows, blocked=None):
        os.makedirs(self.rundir, exist_ok=True)
        for role in self.active:
            name = "%s-%s" % (ROLE_NAME[role], os.path.basename(LOGS[role]))
            try:
                if os.path.exists(LOGS[role]):
                    shutil.copy2(LOGS[role], os.path.join(self.rundir, name))
            except OSError as e:
                self.notes.append("could not copy the %s log: %s" % (ROLE_NAME[role], e))
        path = os.path.join(self.rundir, "report.md")
        L = ["# rig run %s - %s" % (self.id, stamp()), "",
             "- scenario: `%s` (%s)" % (self.args.scenario, self.sc.get("title", "")),
             "- HEAD commit: `%s`" % getattr(self, "head", "?"),
             "- deployed DLL md5: `%s` (DEV markers %s)" % (getattr(self, "deployed_md5", "?"),
                                                            getattr(self, "markers", "?")),
             "- instances: %d (roles %s)" % (self.instances, ",".join(self.active)),
             "- launch: %s" % (stamp(self.launch_t) if self.launch_t else "(not launched)"),
             "- variables: %s" % json.dumps(self.vars, sort_keys=True), ""]
        if blocked:
            L += ["**BLOCKED before the scenario ran:**"] + ["- " + b for b in blocked] + [""]
        L += ["| step | role | cmd | result | expected | verdict | log evidence |",
              "|---|---|---|---|---|---|---|"]
        for r in self.rows:
            L.append("| %s | %s | `%s` | %s | `%s` | %s | %s |" % tuple(
                str(x).replace("|", "\\|").replace("\n", " ") for x in r))
        L += ["", "## oracles"] + (["- %s %s" % o for o in oracle_rows] or ["- (none fired)"])
        L += ["", "## run notes"] + (["- " + n for n in self.notes] or ["- (none)"])
        L += ["", "RESULT: %s (%d/%d steps)" % ("PASS" if passed == total and not blocked and
                                                not any(o[0] == "FAIL" for o in oracle_rows) else "FAIL",
                                                passed, total)]
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(L) + "\n")
        say("report: %s" % path)
        return path


# ---------------------------------------------------------------- dry run / selftest

def dry_run(sc, args):
    print("scenario %s - %s" % (sc.get("id"), sc.get("title", "")))
    print("instances: %s (roles %s)" % (sc.get("instances", 2), ",".join(ROLES[: int(sc.get("instances", 2) or 2)])))
    print("preconditions: %s" % sc.get("preconditions", "(none stated)"))
    print("%-4s %-5s %-44s %-34s %s" % ("step", "role", "cmd", "expect_result", "expect_log / note"))
    for s in sc["steps"]:
        extra = "; ".join("%s[%s] %s" % (e.get("role", s["role"]), e.get("within_s", DEFAULT_WITHIN_S), e["regex"])
                          for e in (s.get("expect_log") or []))
        if s.get("capture"):
            extra = (extra + " | " if extra else "") + "capture " + ",".join(s["capture"])
        if s.get("note"):
            extra = (extra + " | " if extra else "") + s["note"]
        cmd = s.get("cmd") or "(wait %ss)" % s.get("sleep_s")
        exp = s.get("expect_result") or (("NOT " + s["refute_result"]) if s.get("refute_result") else "-")
        print("%-4s %-5s %-44s %-34s %s" % (s["seq"], s["role"], cmd[:44], exp[:34], extra))
    names = sorted({m.group(1) for s in sc["steps"]
                    for txt in [s.get("cmd") or "", s.get("expect_result") or "", s.get("refute_result") or ""]
                    + [e["regex"] for e in (s.get("expect_log") or [])]
                    for m in VAR_RE.finditer(txt)})
    captured = sorted({k for s in sc["steps"] for k in (s.get("capture") or {})})
    supplied = sorted(set(sc.get("vars") or {}) | {kv.partition("=")[0].strip() for kv in (args.var or [])}
                      | ({"session"} if args.session else set()))
    print("\nvariables used: %s" % (", ".join(names) or "(none)"))
    print("  captured in-run: %s" % (", ".join(captured) or "(none)"))
    print("  must be supplied (--var / --session): %s" %
          (", ".join(n for n in names if n not in captured and n not in supplied) or "(none)"))
    print("oracles ABSENT (FAIL if present): %s" % ", ".join(sc.get("oracles_absent") or []) or "(none)")
    print("oracles REPORT-only: %s" % ", ".join(sc.get("oracles_report") or []) or "(none)")
    print("\n%d steps. DRY RUN - nothing launched, no channel touched." % len(sc["steps"]))
    return 0


SELFTESTS = [
    # (label, sample .result text, expect_result regex, refute regex or None, capture map, expected vars)
    # every sample below is the LITERAL shape src/TestDrive.cs builds (read 2026-09-10)
    ("merge propose", "OK merge propose sent -> 'Client1'", r"OK merge (propose|accept) sent", None, {}, {}),
    ("mergestatus member", "OK member=True group='g-1' members=[Host,Client1] flipped=3 "
     "keys=['12 ba:street_5thave','3 ba:street_wallst']", r"member=True.*flipped=[1-9]", None,
     {"hostShop": r"keys=\['([^']+)'"}, {"hostShop": "12 ba:street_5thave"}),
    ("mergestatus off", "OK member=False group='' members=[] flipped=0 keys=[]", r"member=False", None, {}, {}),
    ("regstate flipped", "OK rented=True stamp='' forRent=False type=ba:businesstype_shop name='Deli' days=5 "
     "shifts=2 flipped=True parked='Host'", r"rented=True stamp='' .*flipped=True", None,
     {"shifts": r"shifts=(\d+)"}, {"shifts": "2"}),
    ("employees list", "OK 2 employee(s) @ '12 ba:street_5thave': 7f3a-11|Ann Smith|assigned=12 "
     "ba:street_5thave|injected=False ; 9c2b-42|Bo Jones|assigned=1 ba:street_hq|injected=True",
     r"employee\(s\)", None, {"hostEmployee": r"employee\(s\)(?: @ '[^']*')?: ([^|]+)\|"}, {"hostEmployee": "7f3a-11"}),
    ("fired employee gone", "OK 1 employee(s) @ '12 ba:street_5thave': 9c2b-42|Bo Jones|assigned=12 "
     "ba:street_5thave|injected=False", None, r"7f3a-11", {}, {}),
    ("money merged", "OK money=125000.50 merged=True wallet=125000.50 (mirror)",
     r"money=125000\.50\b.*merged=True", None, {"hostMoney": r"money=([0-9.]+)"}, {"hostMoney": "125000.50"}),
    ("autofill", "OK AutoFillSchedule invoked for '12 ba:street_5thave'; the week now holds 14 shift(s)",
     r"^OK ", None, {}, {}),
    ("fire", "OK RemoveEmployee invoked for '7f3a-11' ('Ann Smith', injected=True)", r"^OK ", None, {}, {}),
    ("err is not ok", "ERR merge: no partner", r"^OK ", None, {}, None),  # None = the assertion must FAIL
]

LOGTESTS = [
    ("armed h", "[Warning: BigAmbitionsMP] [BAMP] [TestDrive] channel ARMED (dev build, role 'h') - watching ...",
     ARMED_RE["h"].pattern, True),
    ("armed h not c", "[Warning: BigAmbitionsMP] [BAMP] [TestDrive] channel ARMED (dev build, role 'c') - watching",
     ARMED_RE["h"].pattern, False),
    ("bracket tag is literal", "[Info   : BigAmbitionsMP] [BAMP] [Merger] flip OFF 'Wall St 3' (left merger)",
     r"\[BAMP\] \[Merger\] flip OFF", True),
    ("bare tag in a result is not a log line", "OK result text mentioning [Merger] flip OFF",
     r"\[BAMP\] \[Merger\] flip OFF", False),
    ("formed", "[Info   : BigAmbitionsMP] [BAMP] [Merger] FORMED group 'Merger-1' (2 members)",
     r"\[BAMP\] \[Merger\].*(FORMED|GROWN)", True),
    ("refused oracle", "[Warning: BigAmbitionsMP] [BAMP] [Merger] accept from 'c' REFUSED: already merged",
     r"\[Merger\] accept .* REFUSED", True),
]


def selftest():
    fails = []
    for label, sample, exp, refute, cap, want in SELFTESTS:
        v = {}
        ok = True
        if exp:
            ok = bool(re.search(exp, sample, re.S))
        if ok and refute:
            ok = not re.search(refute, sample, re.S)
        if want is None:
            status = "PASS" if not ok else "FAIL"
            if ok:
                fails.append(label)
            print("  %-28s expected-to-fail assertion -> %s" % (label, status))
            continue
        if not ok:
            fails.append(label)
            print("  %-28s ASSERTION FAILED against /%s/" % (label, exp))
            continue
        got, err = apply_captures(cap, sample, v)
        if err or got != want:
            fails.append(label)
            print("  %-28s CAPTURE FAILED: %s (got %s want %s)" % (label, err, got, want))
        else:
            print("  %-28s PASS%s" % (label, (" " + json.dumps(want)) if want else ""))
    print("  -- log matchers (emitter prefix '[BAMP] [Tag]') --")
    for label, line, pat, want in LOGTESTS:
        got = bool(re.search(pat, line))
        if got != want:
            fails.append(label)
        print("  %-28s %s (match=%s want=%s)" % (label, "PASS" if got == want else "FAIL", got, want))
    print("  -- variable substitution --")
    try:
        s = subst("shift ${hostShop} 1 ${hostEmployee} 9 17", {"hostShop": "5th Ave 12", "hostEmployee": "7f3a-11"})
        print("  %-28s %s" % ("subst", "PASS -> " + s))
    except Unresolved as u:
        fails.append("subst")
        print("  %-28s FAIL unresolved %s" % ("subst", u))
    try:
        subst("regstate ${nope}", {})
        fails.append("subst-missing")
        print("  %-28s FAIL (a missing variable must raise)" % "subst missing")
    except Unresolved:
        print("  %-28s PASS (missing variable raises, step fails loudly)" % "subst missing")
    print("SELFTEST: %s (%d check groups, %d failed)" % ("PASS" if not fails else "FAIL - " + ", ".join(fails),
                                                         len(SELFTESTS) + len(LOGTESTS) + 2, len(fails)))
    return 0 if not fails else 1


# ---------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description="two-instance rig scenario driver")
    ap.add_argument("scenario", nargs="?", help="path to a scenario .json")
    ap.add_argument("--session", help="value for ${session}")
    ap.add_argument("--var", action="append", help="k=v for a scenario variable (repeatable)")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--keep-open", action="store_true", help="leave both games running at the end")
    args = ap.parse_args()

    if args.selftest:
        return selftest()
    if not args.scenario:
        ap.error("a scenario .json is required (or --selftest)")
    sc = load_scenario(args.scenario)
    if args.dry_run:
        return dry_run(sc, args)

    r = Run(sc, args)
    problems = r.preflight()
    if problems:
        for p in problems:
            say("REFUSED: " + p)
        r.write_report(0, len(sc["steps"]), [], blocked=problems)
        return 1
    say("pre-flight OK - HEAD %s, deployed md5 %s, DEV markers %d" % (r.head, r.deployed_md5, r.markers))
    r.arm_channel()
    r.launch()
    for role in r.active:
        err = r.wait_armed(role)
        if err:
            say("FAIL " + err)
            r.write_report(0, len(sc["steps"]), [], blocked=[err])
            if not args.keep_open:
                close_instances(r.notes)
            return 1

    passed, total, aborted = 0, len(sc["steps"]), False
    for step in sc["steps"]:
        if r.do_step(step):
            passed += 1
        elif step.get("abort_on_fail", True):
            say("aborting the scenario at step %d (abort_on_fail) - no improvised recovery" % step["seq"])
            aborted = True
            break
    oracle_rows = r.oracles()
    for kind, line in oracle_rows:
        say("ORACLE %s %s" % (kind, line))
    r.write_report(passed, total, oracle_rows, blocked=["scenario aborted early"] if aborted else None)
    if args.keep_open:
        say("--keep-open: both instances left running")
    else:
        close_instances(r.notes)
    ok = passed == total and not aborted and not any(k == "FAIL" for k, _ in oracle_rows)
    print("RESULT: %s (%d/%d steps)" % ("PASS" if ok else "FAIL", passed, total))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
